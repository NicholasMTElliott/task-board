using System.Text;
using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed partial class AgentRunner(
    ITaskBoardClient boardClient,
    IAgentExecutor agentExecutor,
    TaskFileManager taskFileManager,
    GitWorkspaceManager gitWorkspaceManager,
    WorkflowConfig workflowConfig,
    ICrossReferenceResolver crossReferenceResolver,
    ILogger<AgentRunner> logger)
{
    private static readonly Regex PlaceholderRegex = PlaceholderPattern();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    private const string CommitFilePath = ".aiboard/commit.md";

    public async Task<AgentRunResult> ExecuteAsync(
        string cardId, string boardId, string workspacePath, CancellationToken cancellationToken)
    {
        var runId = $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
        var runMarker = $"<!-- agent-run:{runId} -->";
        logger.LogInformation("Starting agent run {RunId} for card {CardId} in workspace {Workspace}",
            runId, cardId, workspacePath);

        // 1. Fetch all board cards
        var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken);
        logger.LogInformation("Fetched {Count} cards from board {BoardId}", cards.Count, boardId);

        // 2. Find the target card and determine its workflow state
        var targetCard = cards.FirstOrDefault(c => c.Id == cardId);
        if (targetCard is null)
        {
            logger.LogError("Card {CardId} not found on board {BoardId}", cardId, boardId);
            return new AgentRunResult(AgentOutcome.ERROR, "Card not found on board");
        }

        if (!workflowConfig.States.TryGetValue(targetCard.ColumnId, out var state))
        {
            logger.LogError("Card {CardId} is in column {ColumnId} which is not in workflow config", cardId, targetCard.ColumnId);
            return new AgentRunResult(AgentOutcome.ERROR, "Card column not in workflow config");
        }

        if (state.Steps is not { Count: > 0 })
        {
            logger.LogError("State {StateName} has no steps configured", state.Name);
            return new AgentRunResult(AgentOutcome.ERROR, $"No steps configured for state {state.Name}");
        }

        // Validate all step roles exist up front
        foreach (var step in state.Steps)
        {
            if (!workflowConfig.Roles.ContainsKey(step.Role))
            {
                logger.LogError("State {StateName} step {StepName} references missing role {Role}",
                    state.Name, step.Name, step.Role);
                return new AgentRunResult(AgentOutcome.ERROR,
                    $"Step '{step.Name}' references missing role '{step.Role}' in state {state.Name}");
            }
        }

        // 2b. Move card to in-progress state if defined
        if (state.Transitions.TryGetValue("IN_PROGRESS", out var inProgressColumnId))
        {
            await boardClient.MoveCardToColumnAsync(cardId, inProgressColumnId, cancellationToken);
            logger.LogInformation("Moved card {CardId} to in-progress column {Column}", cardId, inProgressColumnId);
        }

        // 3. Resolve branch name (slug-based with prefix reuse)
        var (branchName, isExistingBranch) = await ResolveBranchNameAsync(workspacePath, cardId, targetCard.Title, cancellationToken);
        var gitBehavior = state.GitBehavior ?? "discard";

        try
        {
            // 4. Create git worktree (isolated working directory for the agent)
            var worktreePath = await gitWorkspaceManager.CreateWorktreeAsync(
                workspacePath, branchName, cancellationToken);

            // 4a. Merge main branch for existing branches
            string? mergePromptAugmentation = null;
            if (isExistingBranch)
            {
                var mergeOutcome = await HandleMergeStepAsync(
                    worktreePath, branchName, cardId, targetCard, state, cancellationToken);

                switch (mergeOutcome.Action)
                {
                    case MergeStepAction.KickBack:
                        await boardClient.UpsertAgentCommentAsync(
                            cardId, mergeOutcome.KickBackComment!, runMarker, cancellationToken);
                        await boardClient.MoveCardToColumnAsync(
                            cardId, mergeOutcome.KickBackColumnId!, cancellationToken);
                        logger.LogInformation("Card {CardId} kicked back due to merge conflict", cardId);
                        return new AgentRunResult(AgentOutcome.ERROR,
                            "Merge conflict with upstream — card returned to implementation");

                    case MergeStepAction.ProceedWithConflictContext:
                        mergePromptAugmentation = mergeOutcome.PromptAugmentation;
                        break;

                    case MergeStepAction.Proceed:
                        break;
                }
            }

            // 5. Fetch comments (used for both cross-references and comments file)
            var comments = await boardClient.GetCardCommentsAsync(cardId, cancellationToken);

            // 5.1 Resolve cross-references for target card
            var referenceContext = await ResolveCrossReferencesAsync(
                targetCard, cards, comments, cancellationToken);

            // 5.2 Write filtered task files into the worktree (with reference annotations)
            var referencedCardIds = referenceContext?.References
                .Select(r => r.ReferencedCardId)
                .ToHashSet();
            var contextCards = GetContextCards(cards, cardId, workflowConfig, referencedCardIds);

            // Fetch any referenced cards not already in allCards
            if (referenceContext is not null)
            {
                var existingIds = contextCards.Select(c => c.Id).ToHashSet();
                var additionalCards = new List<BoardCard>();
                foreach (var refCard in referenceContext.References)
                {
                    if (existingIds.Contains(refCard.ReferencedCardId))
                        continue;
                    try
                    {
                        var fetched = await boardClient.GetCardAsync(refCard.ReferencedCardId, cancellationToken);
                        additionalCards.Add(fetched);
                        existingIds.Add(fetched.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to fetch referenced card {CardId}", refCard.ReferencedCardId);
                    }
                }

                if (additionalCards.Count > 0)
                {
                    contextCards = [.. contextCards, .. additionalCards];
                }

                // Rebuild cardIdToFilePath with all context cards
                var cardIdToFilePath = new Dictionary<string, string>();
                foreach (var card in contextCards)
                {
                    var fileName = TaskFileManager.GetTaskFileName(card.Id, card.Title);
                    cardIdToFilePath[card.Id] = $".aiboard/tasks/{fileName}";
                }
                referenceContext = new ReferenceAnnotationContext(
                    referenceContext.TargetCardId, referenceContext.References, cardIdToFilePath);
            }

            await taskFileManager.WriteAllTaskFilesAsync(
                worktreePath, contextCards, workflowConfig, referenceContext, cancellationToken);

            // 5.3 Write comments file for the active card
            string? commentsFilePath = null;
            if (comments.Count > 0)
            {
                await taskFileManager.WriteCommentsFileAsync(
                    worktreePath, targetCard.Id, targetCard.Title, comments, cancellationToken);
                commentsFilePath = TaskFileManager.GetCommentsFilePath(
                    worktreePath, targetCard.Id, targetCard.Title);
            }

            // 6. Execute steps sequentially
            var commentPrefix = BuildCommentPrefix(state, workflowConfig);
            AgentResult? lastResult = null;

            for (var stepIndex = 0; stepIndex < state.Steps.Count; stepIndex++)
            {
                var step = state.Steps[stepIndex];
                var stepRole = workflowConfig.Roles[step.Role];

                logger.LogInformation("Executing step {StepIndex}/{StepCount} '{StepName}' (role={Role}) for card {CardId}",
                    stepIndex + 1, state.Steps.Count, step.Name, step.Role, cardId);

                // 6a. Resolve system prompt file path for this step's role
                var systemPromptFilePath = await ResolveSystemPromptFileAsync(
                    stepRole, step.Role, worktreePath, workflowConfig.ConfigDirectory, cancellationToken);

                // 6b. Resolve task prompt for this step
                var resolvedPrompt = await ResolveStepTaskPromptAsync(
                    step, worktreePath, targetCard, workflowConfig.ConfigDirectory, cancellationToken);

                if (isExistingBranch && stepIndex == 0)
                {
                    resolvedPrompt += "\n\nNote: This task has been worked on previously. A branch with prior changes already exists. " +
                        "Review the existing state of the codebase and any changes already made before beginning new work. " +
                        "Avoid duplicating or overwriting prior progress.";

                    if (mergePromptAugmentation is not null)
                    {
                        resolvedPrompt += "\n\n" + mergePromptAugmentation;
                    }
                }

                // 6c. Execute agent for this step
                var context = new AgentExecutionContext(
                    TargetCardId: cardId,
                    TargetCardTitle: targetCard.Title,
                    WorkspacePath: worktreePath,
                    TaskPrompt: resolvedPrompt,
                    SystemPromptFilePath: systemPromptFilePath,
                    Model: stepRole.Model,
                    ProviderParams: state.ProviderParams,
                    CommentsFilePath: commentsFilePath);

                lastResult = await agentExecutor.ExecuteAsync(context, cancellationToken);

                // 6d. Update card body from task file after each step (write-after-each-step strategy)
                await UpdateCardBodyFromTaskFileAsync(targetCard, worktreePath, cancellationToken);

                // 6e. Upsert step-specific comment
                var stepMarker = $"<!-- agent-step:{step.Name} -->";
                var stepComment = $"{commentPrefix}\n\n**Step: {step.Name}**\n\n{FormatComment(lastResult)}";
                await boardClient.UpsertAgentCommentAsync(cardId, stepComment, stepMarker, cancellationToken);

                // 6e-ii. Refresh comments file so the next step sees this step's output
                comments = await boardClient.GetCardCommentsAsync(cardId, cancellationToken);
                if (comments.Count > 0)
                {
                    await taskFileManager.WriteCommentsFileAsync(
                        worktreePath, targetCard.Id, targetCard.Title, comments, cancellationToken);
                    commentsFilePath ??= TaskFileManager.GetCommentsFilePath(
                        worktreePath, targetCard.Id, targetCard.Title);
                }

                logger.LogInformation("Step '{StepName}' for card {CardId}: outcome={Outcome}",
                    step.Name, cardId, lastResult.Outcome);

                // 6f. If step did not complete, halt the chain and transition
                if (lastResult.Outcome != AgentOutcome.COMPLETE)
                {
                    var outcomeKey = lastResult.Outcome.ToString();
                    if (state.Transitions.TryGetValue(outcomeKey, out var targetColumnId))
                    {
                        await boardClient.MoveCardToColumnAsync(cardId, targetColumnId, cancellationToken);
                        logger.LogInformation("Step '{StepName}' returned {Outcome}, moved card {CardId} to {Column}",
                            step.Name, outcomeKey, cardId, targetColumnId);
                    }

                    // Handle git for non-complete (still need to commit if applicable)
                    await HandleGitBehaviorAsync(
                        gitBehavior, worktreePath, branchName, targetCard, state, lastResult, cancellationToken);

                    if (gitBehavior == "discard")
                        await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);

                    return new AgentRunResult(lastResult.Outcome, lastResult.Detail, lastResult.Questions);
                }
            }

            // All steps complete
            // 7. Run gate check if configured
            var gateResult = await RunGateCheckAsync(
                state, lastResult!, worktreePath, targetCard, cardId, cancellationToken);

            if (gateResult is not null)
            {
                // Gate blocked progression — handle git and return
                await HandleGitBehaviorAsync(
                    gitBehavior, worktreePath, branchName, targetCard, state, lastResult!, cancellationToken);

                if (gitBehavior == "discard")
                    await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);

                return gateResult;
            }

            // 8. Handle git operations based on stage-specific behavior
            var gitNote = await HandleGitBehaviorAsync(
                gitBehavior, worktreePath, branchName, targetCard, state, lastResult!, cancellationToken);

            // 9. Post-process: upsert run-level comment, move card to next state
            var runComment = $"{commentPrefix}\n\n{FormatComment(lastResult!, gitNote)}";
            await boardClient.UpsertAgentCommentAsync(cardId, runComment, runMarker, cancellationToken);

            var completeKey = lastResult!.Outcome.ToString();
            if (state.Transitions.TryGetValue(completeKey, out var completeColumnId))
            {
                await boardClient.MoveCardToColumnAsync(cardId, completeColumnId, cancellationToken);
                logger.LogInformation("Moved card {CardId} to column {ColumnId}", cardId, completeColumnId);
            }

            // 10. Cleanup worktree for discard stages
            if (gitBehavior == "discard")
            {
                await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);
            }

            logger.LogInformation("Agent run complete for card {CardId}: outcome={Outcome}, detail={Detail}",
                cardId, lastResult.Outcome, lastResult.Detail ?? "(none)");
            return new AgentRunResult(lastResult.Outcome, lastResult.Detail, lastResult.Questions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent run failed for card {CardId}", cardId);

            // For commit-capable stages, try to record error in worktree
            if (gitBehavior is "commit_and_push" or "commit_only")
            {
                try
                {
                    var worktreePath = GitWorkspaceManager.GetWorktreePath(workspacePath, branchName);
                    var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, cardId, targetCard.Title);
                    if (File.Exists(taskFilePath))
                    {
                        var existing = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
                        await File.WriteAllTextAsync(taskFilePath,
                            existing + $"\n\n## Error\n\n{ex.Message}", cancellationToken);
                    }

                    await gitWorkspaceManager.CommitAsync(worktreePath,
                        $"Agent: error processing {cardId}", cancellationToken);
                }
                catch
                {
                    // Best effort error recording
                }
            }
            else
            {
                // Discard stage: just clean up the worktree
                await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);
            }

            // Best effort: post error comment and move card to error state
            try
            {
                var errorResult = new AgentResult(AgentOutcome.ERROR, ex.Message);
                var errorPrefix = BuildCommentPrefix(state, workflowConfig);
                var comment = $"{errorPrefix}\n\n{FormatComment(errorResult)}";
                await boardClient.UpsertAgentCommentAsync(cardId, comment, runMarker, cancellationToken);

                if (state.Transitions.TryGetValue("ERROR", out var errorColumnId))
                {
                    await boardClient.MoveCardToColumnAsync(cardId, errorColumnId, cancellationToken);
                }
            }
            catch (Exception postEx)
            {
                logger.LogWarning(postEx, "Failed to post error feedback to board for card {CardId}", cardId);
            }

            return new AgentRunResult(AgentOutcome.ERROR, ex.Message);
        }
    }

    private async Task<(string BranchName, bool IsExisting)> ResolveBranchNameAsync(
        string repoPath, string cardId, string title, CancellationToken cancellationToken)
    {
        // Check for existing branch with this card's prefix
        var existingBranch = await gitWorkspaceManager.FindBranchByPrefixAsync(
            repoPath, cardId, cancellationToken);

        if (existingBranch is not null)
        {
            logger.LogInformation("Reusing existing branch {Branch} for card {CardId}", existingBranch, cardId);
            return (existingBranch, true);
        }

        // Create new branch name with slug
        var slug = SlugHelper.Sanitize(title);
        var branchName = string.IsNullOrEmpty(slug)
            ? $"aiboard/{cardId}"
            : $"aiboard/{cardId}-{slug}";

        logger.LogInformation("Using new branch name {Branch} for card {CardId}", branchName, cardId);
        return (branchName, false);
    }

    /// <summary>
    /// Handles git operations based on the stage's gitBehavior config.
    /// Returns an optional note to include in the agent comment.
    /// </summary>
    private async Task<string?> HandleGitBehaviorAsync(
        string gitBehavior,
        string worktreePath,
        string branchName,
        BoardCard card,
        WorkflowState state,
        AgentResult agentResult,
        CancellationToken cancellationToken)
    {
        switch (gitBehavior)
        {
            case "commit_and_push":
            {
                var commitMsg = await ReadCommitMessageAsync(worktreePath, card, state, cancellationToken);
                await gitWorkspaceManager.CommitAsync(worktreePath, commitMsg, cancellationToken);

                if (agentResult.Outcome == AgentOutcome.COMPLETE)
                {
                    try
                    {
                        await gitWorkspaceManager.PushAsync(worktreePath, branchName, cancellationToken);
                        return $"Branch `{branchName}` pushed to origin.";
                    }
                    catch (GitOperationException ex)
                    {
                        logger.LogWarning(ex, "Failed to push branch {Branch} — no remote configured?", branchName);
                        return $"Branch `{branchName}` committed locally (push failed).";
                    }
                }

                return $"Changes committed to branch `{branchName}`.";
            }

            case "commit_only":
            {
                var commitMsg = await ReadCommitMessageAsync(worktreePath, card, state, cancellationToken);
                await gitWorkspaceManager.CommitAsync(worktreePath, commitMsg, cancellationToken);
                return $"Changes committed to branch `{branchName}`.";
            }

            case "discard":
            default:
            {
                if (await gitWorkspaceManager.HasUncommittedChangesAsync(worktreePath, cancellationToken))
                {
                    logger.LogWarning(
                        "Discarding unexpected code changes in {State} stage for card {CardId}",
                        state.Name, card.Id);
                    return "Note: agent made unexpected code changes which were discarded.";
                }

                return null;
            }
        }
    }

    private static async Task<string> ReadCommitMessageAsync(
        string worktreePath, BoardCard card, WorkflowState state, CancellationToken cancellationToken)
    {
        var commitFilePath = Path.Combine(worktreePath, CommitFilePath);
        if (File.Exists(commitFilePath))
        {
            var msg = (await File.ReadAllTextAsync(commitFilePath, cancellationToken)).Trim();
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }

        // Fallback: auto-generated message
        return $"Agent: {state.Name} complete for {card.Title}";
    }

    private async Task CleanupWorktreeAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        try
        {
            await gitWorkspaceManager.RemoveWorktreeAsync(
                repoPath, branchName, deleteBranch: true, cancellationToken);
            logger.LogInformation("Cleaned up worktree for branch {Branch}", branchName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up worktree for branch {Branch}", branchName);
        }
    }

    // ── Gate check logic ─────────────────────────────────────────────

    /// <summary>
    /// Runs an optional gate check after all agent steps complete successfully.
    /// Returns null if no gate check is configured or if the gate passes.
    /// Returns an AgentRunResult if the gate blocks progression.
    /// </summary>
    private async Task<AgentRunResult?> RunGateCheckAsync(
        WorkflowState state,
        AgentResult lastStepResult,
        string worktreePath,
        BoardCard targetCard,
        string cardId,
        CancellationToken cancellationToken)
    {
        if (state.GateCheck is null)
            return null;

        var gateCheck = state.GateCheck;

        // Resolve gate role
        if (!workflowConfig.Roles.TryGetValue(gateCheck.Role, out var gateRole))
        {
            logger.LogError("Gate check role '{Role}' not found in workflow config — skipping gate", gateCheck.Role);
            return null;
        }

        // Capture diff based on git behavior
        var gitBehavior = state.GitBehavior ?? "discard";
        string changes;

        if (gitBehavior is "commit_and_push" or "commit_only")
        {
            changes = await gitWorkspaceManager.GetDiffSummaryAsync(
                worktreePath, gateCheck.MaxDiffChars, cancellationToken);
        }
        else
        {
            // For discard stages, the agent output is the task file content
            var taskFilePath = TaskFileManager.GetTaskFilePath(
                worktreePath, targetCard.Id, targetCard.Title);
            changes = File.Exists(taskFilePath)
                ? await File.ReadAllTextAsync(taskFilePath, cancellationToken)
                : "";
        }

        // Skip gate check if no changes
        if (string.IsNullOrWhiteSpace(changes))
        {
            logger.LogWarning("Gate check skipped: no changes detected for card {CardId}", cardId);
            return null;
        }

        // Build gate prompt
        string gatePromptTemplate;
        if (gateCheck.TaskPromptFile is not null)
        {
            var basePath = workflowConfig.ConfigDirectory ?? worktreePath;
            var path = Path.GetFullPath(Path.Combine(basePath, gateCheck.TaskPromptFile));
            if (!File.Exists(path))
            {
                logger.LogError("Gate check prompt file not found: {Path} — skipping gate", path);
                return null;
            }
            gatePromptTemplate = await File.ReadAllTextAsync(path, cancellationToken);
        }
        else
        {
            gatePromptTemplate = gateCheck.TaskPrompt ?? "";
        }

        var agentReport = lastStepResult.Detail ?? "(no self-report provided)";
        var gatePrompt = ResolvePromptPlaceholders(gatePromptTemplate, targetCard);
        gatePrompt = gatePrompt
            .Replace("{TaskBody}", targetCard.Body ?? "")
            .Replace("{Diff}", changes)
            .Replace("{AgentReport}", agentReport);

        // Resolve system prompt
        string gateSystemPromptPath;
        try
        {
            gateSystemPromptPath = await ResolveSystemPromptFileAsync(
                gateRole, gateCheck.Role, worktreePath, workflowConfig.ConfigDirectory, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve gate check system prompt — skipping gate");
            return null;
        }

        // Execute gate check
        AgentResult gateResult;
        try
        {
            var gateContext = new AgentExecutionContext(
                TargetCardId: targetCard.Id,
                TargetCardTitle: targetCard.Title,
                WorkspacePath: worktreePath,
                TaskPrompt: gatePrompt,
                SystemPromptFilePath: gateSystemPromptPath,
                Model: gateRole.Model,
                ProviderParams: new Dictionary<string, string>
                {
                    ["permissionMode"] = "none",
                    ["effort"] = "min",
                    ["maxBudget"] = "0.10"
                });

            logger.LogInformation("Running gate check for card {CardId} in state {State}", cardId, state.Name);
            gateResult = await agentExecutor.ExecuteAsync(gateContext, cancellationToken);
            logger.LogInformation("Gate check result for card {CardId}: {Outcome}", cardId, gateResult.Outcome);
        }
        catch (Exception ex)
        {
            // Gate check infrastructure failure is non-blocking
            logger.LogError(ex, "Gate check failed to execute for card {CardId} — proceeding without verification", cardId);
            var warningComment = $"<!-- gate-check:{state.Name} -->\n\n" +
                $"## Gate Check Warning\n\nGate check failed to execute: {ex.Message}\nProceeding without verification.";
            await boardClient.UpsertAgentCommentAsync(cardId, warningComment,
                $"<!-- gate-check:{state.Name} -->", cancellationToken);
            return null;
        }

        // Interpret result
        switch (gateResult.Outcome)
        {
            case AgentOutcome.COMPLETE:
                // PASS — proceed with normal flow
                logger.LogInformation("Gate check PASSED for card {CardId}", cardId);
                return null;

            case AgentOutcome.NEEDS_INFO:
            {
                // CONCERNS — route to questions column
                var comment = $"<!-- gate-check:{state.Name} -->\n\n" +
                    $"## Gate Check: Concerns\n\n{gateResult.Detail ?? "The gate check raised concerns."}";
                await boardClient.UpsertAgentCommentAsync(cardId, comment,
                    $"<!-- gate-check:{state.Name} -->", cancellationToken);

                if (state.Transitions.TryGetValue("NEEDS_INFO", out var questionsColumn))
                    await boardClient.MoveCardToColumnAsync(cardId, questionsColumn, cancellationToken);

                return new AgentRunResult(AgentOutcome.NEEDS_INFO, gateResult.Detail, gateResult.Questions);
            }

            case AgentOutcome.ERROR:
            default:
            {
                // FAIL — check retry count for infinite loop guard
                var previousFailures = await CountGateCheckFailuresAsync(
                    cardId, state.Name, cancellationToken);

                if (previousFailures >= gateCheck.MaxRetries)
                {
                    // Escalate to NEEDS_INFO for human intervention
                    logger.LogWarning("Gate check for card {CardId} has failed {Count} times — escalating to NEEDS_INFO",
                        cardId, previousFailures + 1);
                    var escalateComment = $"<!-- gate-check:{state.Name} result:ERROR attempt:{previousFailures + 1} -->\n\n" +
                        $"## Gate Check: Escalated to Human Review\n\n" +
                        $"The gate check has failed {previousFailures + 1} consecutive times. Escalating for human review.\n\n" +
                        $"**Latest failure reason:**\n{gateResult.Detail ?? "No detail provided."}";
                    await boardClient.UpsertAgentCommentAsync(cardId, escalateComment,
                        $"<!-- gate-check:{state.Name} -->", cancellationToken);

                    if (state.Transitions.TryGetValue("NEEDS_INFO", out var questionsCol))
                        await boardClient.MoveCardToColumnAsync(cardId, questionsCol, cancellationToken);

                    return new AgentRunResult(AgentOutcome.NEEDS_INFO, gateResult.Detail, gateResult.Questions);
                }

                // Route via GATE_FAIL (re-trigger) or fall back to ERROR
                var failComment = $"<!-- gate-check:{state.Name} result:ERROR attempt:{previousFailures + 1} -->\n\n" +
                    $"## Gate Check: Failed\n\n{gateResult.Detail ?? "The gate check detected issues with the agent's output."}";
                await boardClient.UpsertAgentCommentAsync(cardId, failComment,
                    $"<!-- gate-check:{state.Name} -->", cancellationToken);

                var transitionKey = state.Transitions.ContainsKey("GATE_FAIL") ? "GATE_FAIL" : "ERROR";
                if (state.Transitions.TryGetValue(transitionKey, out var targetColumn))
                    await boardClient.MoveCardToColumnAsync(cardId, targetColumn, cancellationToken);

                return new AgentRunResult(AgentOutcome.ERROR, gateResult.Detail);
            }
        }
    }

    /// <summary>
    /// Counts previous consecutive gate check failures for a card by inspecting comments.
    /// </summary>
    private async Task<int> CountGateCheckFailuresAsync(
        string cardId, string stateName, CancellationToken cancellationToken)
    {
        try
        {
            var comments = await boardClient.GetCardCommentsAsync(cardId, cancellationToken);
            var marker = $"gate-check:{stateName} result:ERROR";
            return comments.Count(c => c.Body.Contains(marker, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to count gate check failures for card {CardId}", cardId);
            return 0;
        }
    }

    // ── Merge step logic ──────────────────────────────────────────────

    private enum MergeStepAction { Proceed, KickBack, ProceedWithConflictContext }

    private sealed record MergeStepOutcome(
        MergeStepAction Action,
        string? PromptAugmentation = null,
        string? KickBackComment = null,
        string? KickBackColumnId = null);

    private async Task<MergeStepOutcome> HandleMergeStepAsync(
        string worktreePath,
        string branchName,
        string cardId,
        BoardCard targetCard,
        WorkflowState state,
        CancellationToken cancellationToken)
    {
        // 1. Fetch origin
        try
        {
            await gitWorkspaceManager.FetchAsync(worktreePath, cancellationToken);
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "Failed to fetch origin — skipping merge step");
            return new MergeStepOutcome(MergeStepAction.Proceed);
        }

        // 2. Pull work branch
        await gitWorkspaceManager.PullWorkBranchAsync(worktreePath, branchName, cancellationToken);

        // 3. Detect default branch
        string defaultBranch;
        try
        {
            defaultBranch = await gitWorkspaceManager.GetDefaultBranchAsync(worktreePath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot detect default branch — skipping merge step");
            return new MergeStepOutcome(MergeStepAction.Proceed);
        }

        // 4. Attempt merge
        var mergeResult = await gitWorkspaceManager.MergeMainBranchAsync(worktreePath, defaultBranch, cancellationToken);

        if (mergeResult.Status == MergeMainStatus.UpToDate)
        {
            logger.LogInformation("Branch {Branch} is up to date with origin/{Default}", branchName, defaultBranch);
            return new MergeStepOutcome(MergeStepAction.Proceed);
        }

        logger.LogInformation("Merge from origin/{Default}: status={Status}, {ChangedCount} changed, {ConflictCount} conflicts",
            defaultBranch, mergeResult.Status, mergeResult.ChangedFiles.Count, mergeResult.ConflictFiles.Count);

        var hasMergeConflictTransition = state.Transitions.ContainsKey("MERGE_CONFLICT");

        // 5. Handle based on stage type
        if (!hasMergeConflictTransition)
        {
            // Implementation-style: handle inline
            if (mergeResult.Status == MergeMainStatus.Merged)
            {
                // Clean merge — commit directly
                await gitWorkspaceManager.CommitMergeAsync(worktreePath, defaultBranch, branchName, cancellationToken);
                logger.LogInformation("Committed clean merge of origin/{Default} into {Branch}", defaultBranch, branchName);
                return new MergeStepOutcome(MergeStepAction.Proceed);
            }
            else
            {
                // Conflicts — leave them for the implementing agent
                var conflictContext = BuildConflictPromptAugmentation(mergeResult);
                return new MergeStepOutcome(MergeStepAction.ProceedWithConflictContext, PromptAugmentation: conflictContext);
            }
        }
        else
        {
            // Non-implementation: run merge validation/resolution agent
            var mergeAgentResult = await RunMergeResolutionAgentAsync(
                mergeResult, worktreePath, cardId, targetCard, cancellationToken);

            if (mergeAgentResult.Outcome == AgentOutcome.COMPLETE)
            {
                // Agent validated/resolved — commit the merge
                await gitWorkspaceManager.CommitMergeAsync(worktreePath, defaultBranch, branchName, cancellationToken);

                // Push if the state's gitBehavior supports it
                var mergeGitBehavior = state.GitBehavior ?? "discard";
                if (mergeGitBehavior is "commit_and_push")
                {
                    await gitWorkspaceManager.PushAsync(worktreePath, branchName, cancellationToken);
                }

                logger.LogInformation("Merge validated and committed for {Branch}", branchName);
                return new MergeStepOutcome(MergeStepAction.Proceed);
            }
            else
            {
                // Agent couldn't resolve — abort and kick back
                try
                {
                    await gitWorkspaceManager.AbortMergeAsync(worktreePath, cancellationToken);
                }
                catch (GitOperationException abortEx)
                {
                    logger.LogWarning(abortEx, "merge --abort failed (no merge in progress?)");
                }

                var kickBackColumn = state.Transitions["MERGE_CONFLICT"];
                var comment = BuildMergeKickBackComment(mergeResult, mergeAgentResult, defaultBranch, branchName);
                return new MergeStepOutcome(MergeStepAction.KickBack, KickBackComment: comment, KickBackColumnId: kickBackColumn);
            }
        }
    }

    private async Task<AgentResult> RunMergeResolutionAgentAsync(
        MergeMainResult mergeResult,
        string worktreePath,
        string cardId,
        BoardCard targetCard,
        CancellationToken cancellationToken)
    {
        var mergeResolution = workflowConfig.MergeResolution;
        if (mergeResolution is null)
        {
            logger.LogWarning("No mergeResolution config — cannot run merge agent");
            return new AgentResult(AgentOutcome.ERROR, "No merge resolution agent configured");
        }

        if (!workflowConfig.Roles.TryGetValue(mergeResolution.Role, out var mergeRole))
        {
            logger.LogError("Merge resolution role {Role} not found in workflow config", mergeResolution.Role);
            return new AgentResult(AgentOutcome.ERROR, $"Merge resolution role '{mergeResolution.Role}' not found");
        }

        var systemPromptPath = await ResolveSystemPromptFileAsync(
            mergeRole, mergeResolution.Role, worktreePath, workflowConfig.ConfigDirectory, cancellationToken);

        var taskPrompt = BuildMergeAgentTaskPrompt(mergeResult);

        var context = new AgentExecutionContext(
            TargetCardId: cardId,
            TargetCardTitle: targetCard.Title,
            WorkspacePath: worktreePath,
            TaskPrompt: taskPrompt,
            SystemPromptFilePath: systemPromptPath,
            Model: mergeRole.Model,
            ProviderParams: mergeResolution.ProviderParams,
            CommentsFilePath: null);

        logger.LogInformation("Running merge resolution agent for card {CardId}", cardId);
        return await agentExecutor.ExecuteAsync(context, cancellationToken);
    }

    private static string BuildMergeAgentTaskPrompt(MergeMainResult mergeResult)
    {
        var sb = new StringBuilder();

        if (mergeResult.Status == MergeMainStatus.Merged)
        {
            sb.AppendLine($"The default branch (origin/{mergeResult.DefaultBranch}) has been merged into the work branch.");
            sb.AppendLine($"{mergeResult.CommitCount} new commit(s) were merged.");
            sb.AppendLine();
            sb.AppendLine("Changed files:");
            foreach (var f in mergeResult.ChangedFiles)
                sb.AppendLine($"- {f}");
            sb.AppendLine();
            sb.AppendLine("Verify the build passes and the merged changes do not introduce regressions or conflicts with the existing work on this branch.");
        }
        else
        {
            sb.AppendLine($"A merge of origin/{mergeResult.DefaultBranch} into the work branch resulted in conflicts.");
            sb.AppendLine();
            sb.AppendLine("Conflicted files:");
            foreach (var f in mergeResult.ConflictFiles)
                sb.AppendLine($"- {f}");
            if (mergeResult.ChangedFiles.Count > mergeResult.ConflictFiles.Count)
            {
                sb.AppendLine();
                sb.AppendLine("Auto-merged files (no conflicts):");
                foreach (var f in mergeResult.ChangedFiles.Except(mergeResult.ConflictFiles))
                    sb.AppendLine($"- {f}");
            }
            sb.AppendLine();
            sb.AppendLine("Resolve all conflict markers (<<<<<<< / ======= / >>>>>>>), verify the build passes, and confirm the merged result is correct.");
        }

        return sb.ToString();
    }

    private static string BuildConflictPromptAugmentation(MergeMainResult mergeResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IMPORTANT: A merge of the main branch into your work branch has resulted in conflicts.");
        sb.AppendLine("The following files have merge conflict markers that you MUST resolve before proceeding:");
        foreach (var f in mergeResult.ConflictFiles)
            sb.AppendLine($"- {f}");
        sb.AppendLine();
        sb.AppendLine("Resolve all conflict markers (<<<<<<< / ======= / >>>>>>>), verify the build passes, then proceed with your assigned task.");
        return sb.ToString();
    }

    private static string BuildMergeKickBackComment(
        MergeMainResult mergeResult, AgentResult agentResult,
        string defaultBranch, string branchName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Merge Conflict: upstream changes could not be resolved");
        sb.AppendLine();
        sb.AppendLine($"Merging `origin/{defaultBranch}` into `{branchName}` resulted in issues that the merge resolution agent could not automatically resolve.");
        sb.AppendLine();

        if (mergeResult.ConflictFiles.Count > 0)
        {
            sb.AppendLine("**Conflicted files:**");
            foreach (var f in mergeResult.ConflictFiles)
                sb.AppendLine($"- `{f}`");
            sb.AppendLine();
        }

        if (agentResult.Detail is not null)
        {
            sb.AppendLine("**Agent assessment:**");
            sb.AppendLine(agentResult.Detail);
            sb.AppendLine();
        }

        sb.AppendLine("The implementation needs to be reworked to incorporate the upstream changes. Re-trigger this card once the conflicts are addressed.");
        return sb.ToString();
    }

    private async Task PostProcessAsync(
        BoardCard originalCard,
        WorkflowState state,
        AgentResult agentResult,
        string worktreePath,
        string branchName,
        string? gitNote,
        string commentPrefix,
        string runMarker,
        CancellationToken cancellationToken)
    {
        // 9a. Read back the task file to detect agent changes to card content
        var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, originalCard.Id, originalCard.Title);
        if (File.Exists(taskFilePath))
        {
            var taskFileContent = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
            var bodyFromFile = TaskFileManager.ExtractBodyFromTaskFile(taskFileContent);
            var cleanBody = TaskFileManager.StripAnnotations(bodyFromFile);

            if (!string.Equals(cleanBody, originalCard.Body, StringComparison.Ordinal))
            {
                await boardClient.UpdateCardBodyAsync(originalCard.Id, cleanBody, cancellationToken);
                logger.LogInformation("Updated card body for {CardId}", originalCard.Id);
            }
        }

        // 9b. Format and post comment (with optional git note and role/state prefix)
        var comment = $"{commentPrefix}\n\n{FormatComment(agentResult, gitNote)}";
        await boardClient.UpsertAgentCommentAsync(originalCard.Id, comment, runMarker, cancellationToken);
        logger.LogInformation("Posted agent comment for {CardId}", originalCard.Id);

        // 9c. Move card to next state per transitions
        var outcomeKey = agentResult.Outcome.ToString();
        if (state.Transitions.TryGetValue(outcomeKey, out var targetColumnId))
        {
            await boardClient.MoveCardToColumnAsync(originalCard.Id, targetColumnId, cancellationToken);
            logger.LogInformation("Moved card {CardId} to column {ColumnId}", originalCard.Id, targetColumnId);
        }
        else
        {
            logger.LogWarning("No transition for {Outcome} in state {State}", outcomeKey, state.Name);
        }
    }

    private static IReadOnlyList<BoardCard> GetContextCards(
        IReadOnlyList<BoardCard> allCards,
        string activeCardId,
        WorkflowConfig config,
        IReadOnlySet<string>? additionalCardIds = null)
    {
        return allCards.Where(card =>
            card.Id == activeCardId
            || (config.States.TryGetValue(card.ColumnId, out var state)
                && state.IncludeInAgentContext)
            || (additionalCardIds?.Contains(card.Id) == true))
        .ToList();
    }

    private async Task<ReferenceAnnotationContext?> ResolveCrossReferencesAsync(
        BoardCard targetCard,
        IReadOnlyList<BoardCard> allCards,
        IReadOnlyList<CardComment> comments,
        CancellationToken cancellationToken)
    {
        // Parse body for text references
        var bodyRefs = await crossReferenceResolver.ParseTextReferencesAsync(
            targetCard.Body, cancellationToken);

        // Parse comments for text references, then tag them for the cross-references section
        var commentText = string.Join("\n", comments.Select(c => c.Body));
        var rawCommentRefs = await crossReferenceResolver.ParseTextReferencesAsync(
            commentText, cancellationToken);
        var commentRefs = rawCommentRefs
            .Select(r => new CardReference(r.ReferencedCardId, "mentioned_in_comments", null, r.Title))
            .ToList();

        // Fetch structured references
        var structuredRefs = await crossReferenceResolver.GetStructuredReferencesAsync(
            targetCard.Id, cancellationToken);

        // Merge + deduplicate by ReferencedCardId (structured wins over text)
        var merged = new Dictionary<string, CardReference>();
        foreach (var r in bodyRefs)
            merged.TryAdd(r.ReferencedCardId, r);
        foreach (var r in commentRefs)
            merged.TryAdd(r.ReferencedCardId, r);
        foreach (var r in structuredRefs)
            merged[r.ReferencedCardId] = r; // Structured overwrites

        // Exclude self-references
        merged.Remove(targetCard.Id);

        if (merged.Count == 0)
            return null;

        // Populate Title from allCards where missing
        var cardLookup = allCards.ToDictionary(c => c.Id);
        var allReferences = new List<CardReference>();
        foreach (var r in merged.Values)
        {
            var refWithTitle = r;
            if (r.Title is null && cardLookup.TryGetValue(r.ReferencedCardId, out var card))
            {
                refWithTitle = r with { Title = card.Title };
            }
            allReferences.Add(refWithTitle);
        }

        // Build initial cardIdToFilePath (will be rebuilt after fetching additional cards)
        var cardIdToFilePath = new Dictionary<string, string>();
        foreach (var card in allCards)
        {
            var fileName = TaskFileManager.GetTaskFileName(card.Id, card.Title);
            cardIdToFilePath[card.Id] = $".aiboard/tasks/{fileName}";
        }

        return new ReferenceAnnotationContext(targetCard.Id, allReferences, cardIdToFilePath);
    }

    private async Task UpdateCardBodyFromTaskFileAsync(
        BoardCard originalCard, string worktreePath, CancellationToken cancellationToken)
    {
        var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, originalCard.Id, originalCard.Title);
        if (!File.Exists(taskFilePath))
            return;

        var taskFileContent = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
        var bodyFromFile = TaskFileManager.ExtractBodyFromTaskFile(taskFileContent);
        var cleanBody = TaskFileManager.StripAnnotations(bodyFromFile);

        if (!string.Equals(cleanBody, originalCard.Body, StringComparison.Ordinal))
        {
            await boardClient.UpdateCardBodyAsync(originalCard.Id, cleanBody, cancellationToken);
            logger.LogInformation("Updated card body for {CardId}", originalCard.Id);
        }
    }

    internal static async Task<string> ResolveStepTaskPromptAsync(
        WorkflowStep step, string worktreePath, BoardCard card, string? configDirectory, CancellationToken cancellationToken)
    {
        string template;

        if (step.TaskPromptFile is not null)
        {
            var basePath = configDirectory ?? worktreePath;
            var path = Path.GetFullPath(Path.Combine(basePath, step.TaskPromptFile));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Task prompt file not found: {path} (configured as '{step.TaskPromptFile}' for step '{step.Name}')",
                    path);
            template = await File.ReadAllTextAsync(path, cancellationToken);
        }
        else
        {
            template = step.TaskPrompt ?? "";
        }

        return ResolvePromptPlaceholders(template, card);
    }

    private static string BuildCommentPrefix(WorkflowState state, WorkflowConfig config)
    {
        var activeStateName = state.Transitions.TryGetValue("IN_PROGRESS", out var inProgressCol)
            && config.States.TryGetValue(inProgressCol, out var inProgressState)
            ? inProgressState.Name
            : state.Name;

        // For multi-step states, use the first step's role or fall back to state-level role
        var roleName = state.Steps is { Count: > 0 }
            ? state.Steps[0].Role
            : state.Role ?? "agent";
        return $"**{roleName} in {activeStateName}:**";
    }

    internal static string FormatComment(AgentResult result, string? gitNote = null)
    {
        var comment = result.Outcome switch
        {
            AgentOutcome.COMPLETE => $"## Agent Complete\n\n{result.Detail ?? "Task completed successfully."}",
            AgentOutcome.NEEDS_INFO => FormatQuestionsComment(result),
            AgentOutcome.ERROR => $"## Agent Error\n\n{result.Detail ?? "The agent returned ERROR with no detail. This may indicate a timeout, budget exhaustion, or a failure to produce structured output."}",
            _ => $"## Agent: {result.Outcome}\n\n{result.Detail ?? ""}"
        };

        if (!string.IsNullOrEmpty(gitNote))
            comment += $"\n\n---\n{gitNote}";

        if (!string.IsNullOrEmpty(result.ConversationLog))
        {
            var log = result.ConversationLog.Length > 50_000
                ? result.ConversationLog[..50_000] + "\n...[truncated]"
                : result.ConversationLog;
            comment += $"\n\n<details>\n<summary>Agent conversation log</summary>\n\n{log}\n\n</details>";
        }

        return comment;
    }

    private static string FormatQuestionsComment(AgentResult result)
    {
        var sb = new StringBuilder("## Questions\n\nThe agent needs more information:\n\n");

        if (result.Questions is { Count: > 0 })
        {
            foreach (var q in result.Questions)
            {
                sb.AppendLine($"- **{q.Question}**");
                if (q.Recommendations is { Count: > 0 })
                {
                    foreach (var rec in q.Recommendations)
                        sb.AppendLine($"  - Suggestion: {rec}");
                }
            }
        }

        if (result.Detail is not null)
            sb.AppendLine($"\n{result.Detail}");

        return sb.ToString();
    }

    internal static async Task<string> ResolveSystemPromptFileAsync(
        WorkflowRole role, string roleName, string worktreePath, string? configDirectory, CancellationToken cancellationToken)
    {
        if (role.SystemPromptFile is not null)
        {
            var basePath = configDirectory ?? worktreePath;
            var path = Path.GetFullPath(Path.Combine(basePath, role.SystemPromptFile));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"System prompt file not found: {path} (configured as '{role.SystemPromptFile}' for role '{roleName}')",
                    path);
            return path;
        }

        if (string.IsNullOrWhiteSpace(role.SystemPrompt))
            throw new InvalidOperationException(
                $"Role '{roleName}' has no SystemPromptFile and no inline SystemPrompt.");

        var tempPath = Path.Combine(worktreePath, ".aiboard", $"system-prompt-{roleName}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await File.WriteAllTextAsync(tempPath, role.SystemPrompt, cancellationToken);
        return tempPath;
    }

    internal static async Task<string> ResolveTaskPromptAsync(
        WorkflowState state, string worktreePath, BoardCard card, string? configDirectory, CancellationToken cancellationToken)
    {
        string template;

        if (state.TaskPromptFile is not null)
        {
            var basePath = configDirectory ?? worktreePath;
            var path = Path.GetFullPath(Path.Combine(basePath, state.TaskPromptFile));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Task prompt file not found: {path} (configured as '{state.TaskPromptFile}' for state '{state.Name}')",
                    path);
            template = await File.ReadAllTextAsync(path, cancellationToken);
        }
        else
        {
            template = state.TaskPrompt ?? "";
        }

        return ResolvePromptPlaceholders(template, card);
    }

    internal static string ResolvePromptPlaceholders(string template, BoardCard card)
    {
        return PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return key switch
            {
                "TaskName" => card.Title,
                "TaskId" => card.Id,
                _ => match.Value
            };
        });
    }
}

public sealed record AgentRunResult(
    AgentOutcome Outcome,
    string? ErrorDetail,
    IReadOnlyList<AgentQuestion>? Questions = null);
