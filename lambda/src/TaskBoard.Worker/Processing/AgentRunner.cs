using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed partial class AgentRunner(
    ITaskBoardClient boardClient,
    IAgentExecutorResolver executorResolver,
    TaskFileManager taskFileManager,
    GitWorkspaceManager gitWorkspaceManager,
    WorkflowConfig workflowConfig,
    ICrossReferenceResolver crossReferenceResolver,
    AgentIdentity agentIdentity,
    UpdateFileProcessor updateFileProcessor,
    IRunStore runStore,
    ImageDownloader imageDownloader,
    ITenantIdentifier tenant,
    ILogger<AgentRunner> logger,
    DockerAgentOptions? dockerOptions = null,
    DockerMountBuilder? mountBuilder = null,
    ShutdownCoordinator? shutdownCoordinator = null)
{
    private static readonly Regex PlaceholderRegex = PlaceholderPattern();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    private const string CommitFilePath = ".aiboard/commit.md";

    /// <summary>
    /// Maximum length for reference content before truncation.
    /// Matches the pattern used for conversation log truncation (50k) but with a higher limit
    /// for richer audit content.
    /// </summary>
    private const int MaxReferenceContentLength = 100_000;

    public async Task<AgentRunResult> ExecuteAsync(
        string cardId, string boardId, string workspacePath, CancellationToken cancellationToken)
    {
        var runId = $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
        var runMarker = $"<!-- agent-run:{runId} -->";

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["AgentName"] = agentIdentity.DisplayName,
            ["RunId"] = runId
        }))
        {

        logger.LogInformation("Starting agent run {RunId} for card {CardId} in workspace {Workspace}",
            runId, cardId, workspacePath);

        // 1. Fetch all board cards (exclude terminal states to keep the working set small)
        var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken, workflowConfig.GetTerminalColumnNames());
        logger.LogInformation("Fetched {Count} cards from board {BoardId}", cards.Count, boardId);

        // 2. Find the target card and determine its workflow state
        var targetCard = cards.FirstOrDefault(c => c.Id == cardId);
        if (targetCard is null)
        {
            logger.LogError("Card {CardId} not found on board {BoardId}", cardId, boardId);
            return new AgentRunResult(AgentOutcome.ERROR, "Card not found on board");
        }

        var state = workflowConfig.ResolveState(targetCard);
        if (state is null)
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

        // Build template context once per run (resolves {{agent}}, etc.)
        var templateContext = await BuildTemplateContextAsync(cancellationToken);

        // 2b. Move card to in-progress state if defined
        if (state.Transitions.TryGetValue(TransitionKeys.InProgress, out var inProgressTarget))
        {
            await TransitionExecutor.ExecuteAsync(
                cardId, inProgressTarget, boardClient, logger, cancellationToken, templateContext, crossReferenceResolver, workflowConfig);
            logger.LogInformation("Moved card {CardId} to in-progress via {Count} action(s)",
                cardId, inProgressTarget.Actions.Count);
        }

        // 3. Resolve branch name (slug-based with prefix reuse)
        var (branchName, isExistingBranch) = await ResolveBranchNameAsync(workspacePath, cardId, targetCard.Title, cancellationToken);
        var gitBehavior = state.GitBehavior ?? "discard";

        try
        {
            // 4. Create git worktree (isolated working directory for the agent)
            var worktreePath = await gitWorkspaceManager.CreateWorktreeAsync(
                workspacePath, branchName, cancellationToken);

            // 4a. Merge main branch for existing branches (skip for discard states — changes are thrown away)
            string? mergePromptAugmentation = null;
            if (isExistingBranch && gitBehavior != "discard")
            {
                var mergeOutcome = await HandleMergeStepAsync(
                    worktreePath, branchName, cardId, targetCard, state, cancellationToken);

                switch (mergeOutcome.Action)
                {
                    case MergeStepAction.KickBack:
                        await boardClient.UpsertAgentCommentAsync(
                            cardId, mergeOutcome.KickBackComment!, runMarker, cancellationToken);
                        if (mergeOutcome.KickBackTarget is not null)
                        {
                            await TransitionExecutor.ExecuteAsync(
                                cardId, mergeOutcome.KickBackTarget, boardClient, logger,
                                cancellationToken, templateContext, crossReferenceResolver, workflowConfig);
                        }
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

            // 5.4 Include calibration ticket in context cards if estimation is configured
            if (workflowConfig.Estimation is { } estimationConfig)
            {
                var existingIds = contextCards.Select(c => c.Id).ToHashSet();
                if (!existingIds.Contains(estimationConfig.CalibrationTicketId))
                {
                    // First check if the calibration card was already fetched in allCards
                    var calibrationFromBoard = cards.FirstOrDefault(
                        c => c.Id == estimationConfig.CalibrationTicketId);
                    if (calibrationFromBoard is not null)
                    {
                        contextCards = [.. contextCards, calibrationFromBoard];
                    }
                    else
                    {
                        try
                        {
                            var calibrationCard = await boardClient.GetCardAsync(
                                estimationConfig.CalibrationTicketId, cancellationToken);
                            contextCards = [.. contextCards, calibrationCard];
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "Failed to fetch calibration ticket {TicketId} — estimation step will run without calibration context",
                                estimationConfig.CalibrationTicketId);
                        }
                    }
                }
            }

            // 5.2a Download images referenced in the target card body
            var imageMapping = await imageDownloader.DownloadImagesAsync(
                cardId, targetCard.Body, worktreePath, cancellationToken);

            await taskFileManager.WriteAllTaskFilesAsync(
                worktreePath, contextCards, workflowConfig, referenceContext,
                imageMapping.Count > 0 ? imageMapping : null,
                imageMapping.Count > 0 ? cardId : null,
                cancellationToken);

            // 5.3 Write comments file for the active card
            string? commentsFilePath = null;
            if (comments.Count > 0)
            {
                await taskFileManager.WriteCommentsFileAsync(
                    worktreePath, targetCard.Id, targetCard.Title, comments, cancellationToken);
                commentsFilePath = TaskFileManager.GetCommentsFilePath(
                    worktreePath, targetCard.Id, targetCard.Title);
            }

            // 5.4 Create run record in DB and write prior step context
            var runRecord = new RunRecord(
                RunId: runId,
                CardId: cardId,
                StateName: state.Name,
                AgentIdentity: agentIdentity.DisplayName,
                GitBranch: branchName,
                TotalSteps: state.Steps.Count,
                StartedAtUtc: DateTimeOffset.UtcNow);
            await SafeDbCallAsync(() => runStore.CreateRunAsync(runRecord, cancellationToken));
            await WritePriorStepContextAsync(worktreePath, cardId, state.Name, cancellationToken);

            // 5.5 Build mount context for Docker workspace/credential mounts (if builder is available)
            DockerMountContext? mountContext = null;
            if (mountBuilder is not null && dockerOptions is not null)
            {
                try
                {
                    mountContext = await mountBuilder.BuildAsync(
                        worktreePath, dockerOptions, cancellationToken);
                    logger.LogDebug(
                        "Mount context built for run {RunId} card {CardId}: {MountCount} mount(s)",
                        runId, cardId, mountContext.Mounts.Count);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to build mount context for run {RunId} card {CardId} — " +
                        "session will proceed without workspace mounts",
                        runId, cardId);
                }
            }

            // 5.6 Attempt to create a reusable container session (if a sessionable executor is available)
            IAgentExecutorSession? session = null;
            if (dockerOptions?.ReuseContainer == true)
            {
                var sessionCreatedAt = DateTimeOffset.UtcNow;
                try
                {
                    session = await TryCreateSessionAsync(
                        state, cardId, runId, mountContext, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Session creation failed for run {RunId} card {CardId}, falling back to per-step execution",
                        runId, cardId);
                }

                if (session is not null)
                {
                    var startupMs = (int)(DateTimeOffset.UtcNow - sessionCreatedAt).TotalMilliseconds;
                    logger.LogInformation(
                        "Session {SessionId} created for run {RunId} card {CardId} in {StartupMs}ms",
                        session.SessionId, runId, cardId, startupMs);
                    await SafeDbCallAsync(() =>
                        runStore.UpdateRunSessionStartupMsAsync(runId, startupMs, cancellationToken));
                }
            }

            // 6. Variables needed both inside the session lifecycle and after it
            var commentPrefix = BuildCommentPrefix(state, workflowConfig, agentIdentity);
            AgentResult? lastResult = null;

            try // session lifecycle: dispose after all LLM invocations complete
            {

            // Build prompt context for estimation placeholders (null if estimation not configured)
            Dictionary<string, string>? promptContext = null;
            if (workflowConfig.Estimation is { } estConfig)
            {
                promptContext = new Dictionary<string, string>
                {
                    ["CalibrationTicketId"] = estConfig.CalibrationTicketId,
                    ["CalibrationSize"] = estConfig.CalibrationSize.ToString(),
                    ["EstimationScale"] = string.Join(", ", estConfig.Scale ?? [1, 2, 4, 8]),
                    ["EstimationFieldName"] = estConfig.FieldName,
                };
            }

            double? capturedEstimate = null;

            for (var stepIndex = 0; stepIndex < state.Steps.Count; stepIndex++)
            {
                // Inter-step shutdown check: if graceful shutdown was requested after at least one step
                // completed, preserve partial work and restore the card to its trigger column so it can
                // be re-processed. Only applies from step 1 onwards — if shutdown is requested before
                // any work starts, the runner loop condition handles it without entering this path.
                if (stepIndex > 0 && shutdownCoordinator?.IsShutdownRequested == true)
                {
                    logger.LogWarning(
                        "Shutdown requested between steps at index {StepIndex}/{TotalSteps} for card {CardId} — " +
                        "preserving partial work and restoring to trigger column",
                        stepIndex, state.Steps.Count, cardId);

                    // For commit_and_push stages: commit partial work and push so future runs can
                    // resume from the existing branch (branch is found via FindBranchByPrefixAsync).
                    if (gitBehavior == "commit_and_push")
                    {
                        try
                        {
                            var partialCommitMsg = await ReadCommitMessageAsync(worktreePath, targetCard, state, cancellationToken);
                            await gitWorkspaceManager.CommitAsync(worktreePath, partialCommitMsg, cancellationToken);
                            try
                            {
                                await gitWorkspaceManager.PushAsync(worktreePath, branchName, cancellationToken);
                                logger.LogInformation(
                                    "Partial work pushed to branch {Branch} for card {CardId}",
                                    branchName, cardId);
                            }
                            catch (Exception pushEx)
                            {
                                logger.LogWarning(pushEx,
                                    "Failed to push partial work for card {CardId} during shutdown — branch exists locally for manual recovery",
                                    cardId);
                            }
                        }
                        catch (Exception commitEx)
                        {
                            logger.LogWarning(commitEx,
                                "Failed to commit partial work for card {CardId} during shutdown",
                                cardId);
                        }
                    }
                    else if (gitBehavior == "discard")
                    {
                        await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);
                    }

                    // Restore card to trigger column (best effort — same pattern as rate-limit restore)
                    try
                    {
                        await boardClient.MoveCardToColumnAsync(cardId, targetCard.ColumnId, cancellationToken);
                        var shutdownComment = gitBehavior == "commit_and_push"
                            ? $"{commentPrefix}\n\n**Shutdown requested** — {stepIndex}/{state.Steps.Count} steps completed. " +
                              $"Partial work committed to branch `{branchName}`. " +
                              $"Card returned to **{targetCard.ColumnId}** for re-processing."
                            : $"{commentPrefix}\n\n**Shutdown requested** — {stepIndex}/{state.Steps.Count} steps completed. " +
                              $"Card returned to **{targetCard.ColumnId}** for re-processing.";
                        await boardClient.UpsertAgentCommentAsync(
                            cardId, shutdownComment, $"<!-- agent-shutdown:{cardId} -->", cancellationToken);
                    }
                    catch (Exception restoreEx)
                    {
                        logger.LogWarning(restoreEx,
                            "Failed to restore card {CardId} to trigger column during shutdown — card may be stuck in IN_PROGRESS",
                            cardId);
                    }

                    await SafeDbCallAsync(() => runStore.CompleteRunAsync(
                        runId, AgentOutcome.COMPLETE, "Shutdown requested — partial work preserved", null, cancellationToken));
                    return new AgentRunResult(AgentOutcome.COMPLETE,
                        "Shutdown requested — partial work preserved and card returned to trigger column for re-processing");
                }

                var step = state.Steps[stepIndex];
                var stepRole = workflowConfig.Roles[step.Role];

                logger.LogInformation("Executing step {StepIndex}/{StepCount} '{StepName}' (role={Role}, provider={Provider}) for card {CardId}",
                    stepIndex + 1, state.Steps.Count, step.Name, step.Role, stepRole.Provider, cardId);

                var stepStartedAt = DateTimeOffset.UtcNow;

                // 6a. Resolve system prompt file path for this step's role
                var systemPromptFilePath = await ResolveSystemPromptFileAsync(
                    stepRole, step.Role, worktreePath, workflowConfig.ConfigDirectory, cancellationToken);

                // 6b. Resolve task prompt for this step
                var resolvedPrompt = await ResolveStepTaskPromptAsync(
                    step, worktreePath, targetCard, workflowConfig.ConfigDirectory, cancellationToken, promptContext);

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

                // 6c. Execute agent for this step (via session if available, direct otherwise)
                var context = new AgentExecutionContext(
                    TargetCardId: cardId,
                    TargetCardTitle: targetCard.Title,
                    WorkspacePath: worktreePath,
                    TaskPrompt: resolvedPrompt,
                    SystemPromptFilePath: systemPromptFilePath,
                    Model: stepRole.Model,
                    ProviderParams: state.ProviderParams,
                    CommentsFilePath: commentsFilePath);

                var (stepResult, stepSessionExecMs) = await ExecuteWithSessionAsync(
                    session, stepRole.Provider, context, step.Name, runId, cancellationToken);
                lastResult = stepResult;

                // Capture estimate if this step returned one and persist it to DB
                if (lastResult.Estimate.HasValue)
                {
                    capturedEstimate = lastResult.Estimate;
                    logger.LogInformation("Step '{StepName}' produced estimate: {Estimate} story point(s)",
                        step.Name, capturedEstimate);
                    await SafeDbCallAsync(() =>
                        runStore.UpdateRunEstimateAsync(runId, capturedEstimate.Value, cancellationToken));
                }

                // 6d. Update card body from task file after each step (write-after-each-step strategy)
                await UpdateCardBodyFromTaskFileAsync(targetCard, worktreePath, cancellationToken,
                    trimForBoard: runStore is not NullRunStore);

                // 6d-ii. Process update files (.aiboard/updates/) — handles both generation steps
                //        (step.GenerationConfig set) and ad-hoc ticket creation (no config).
                UpdateProcessingResult updateResult;
                try
                {
                    updateResult = await updateFileProcessor.ProcessUpdatesAsync(
                        worktreePath, cardId, step.Name, comments, cancellationToken,
                        step.GenerationConfig);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Update file processing failed for step '{StepName}' on card {CardId}", step.Name, cardId);
                    updateResult = UpdateProcessingResult.Empty;
                }

                // 6d-ii-b. If update processing produced estimates (e.g., generate_tasks step),
                //          capture the total for use in the COMPLETE transition's setField action.
                if (updateResult.TotalEstimate.HasValue)
                {
                    capturedEstimate = updateResult.TotalEstimate;
                    logger.LogInformation("Update processing produced total estimate: {Estimate} (from {Count} tickets)",
                        capturedEstimate, updateResult.CreatedTickets.Count);
                }

                // 6d-iii. Save step result to DB
                {
                    var stepCompletedAt = DateTimeOffset.UtcNow;

                    string? taskFileSnapshot = null;
                    var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, cardId, targetCard.Title);
                    if (File.Exists(taskFilePath))
                    {
                        var rawContent = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
                        taskFileSnapshot = TaskFileManager.ExtractBodyFromTaskFile(rawContent);
                    }

                    var referenceContent = updateResult.ReferenceContent;
                    if (referenceContent is not null && referenceContent.Length > MaxReferenceContentLength)
                    {
                        logger.LogWarning("Truncating reference content from {Original} to {Max} chars for card {CardId}",
                            referenceContent.Length, MaxReferenceContentLength, cardId);
                        referenceContent = referenceContent[..MaxReferenceContentLength] + "\n...[truncated]";
                    }

                    var stepRecord = new StepResultRecord(
                        RunId: runId,
                        CardId: cardId,
                        StateName: state.Name,
                        StepName: step.Name,
                        StepIndex: stepIndex,
                        Role: step.Role,
                        Model: stepRole.Model,
                        Outcome: lastResult.Outcome,
                        Summary: lastResult.Detail,
                        Detail: taskFileSnapshot,
                        ReferenceContent: referenceContent,
                        ConversationLog: lastResult.ConversationLog,
                        Questions: lastResult.Questions,
                        RequestedSteps: lastResult.RequestedSteps,
                        StartedAtUtc: stepStartedAt,
                        CompletedAtUtc: stepCompletedAt,
                        SessionExecMs: stepSessionExecMs);
                    await SafeDbCallAsync(() => runStore.SaveStepResultAsync(stepRecord, cancellationToken));
                    await SafeDbCallAsync(() => runStore.UpdateRunProgressAsync(runId, stepIndex + 1, cancellationToken));
                }

                // 6e. Upsert step-specific comment (augmented with update file summary if applicable)
                var stepMarker = $"<!-- agent-step:{step.Name} -->";
                var stepComment = $"{commentPrefix}\n\n**Step: {step.Name}**\n\n{FormatComment(lastResult, includeConversationLog: runStore is NullRunStore)}";
                if (updateResult.HasUpdates)
                    stepComment += FormatUpdateSummary(updateResult);
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
                    if (state.Transitions.TryGetValue(outcomeKey, out var stepOutcomeTarget))
                    {
                        await TransitionExecutor.ExecuteAsync(
                            cardId, stepOutcomeTarget, boardClient, logger, cancellationToken, templateContext, crossReferenceResolver, workflowConfig);
                        logger.LogInformation("Step '{StepName}' returned {Outcome}, executed transition for card {CardId}",
                            step.Name, outcomeKey, cardId);
                    }

                    // Handle git for non-complete (still need to commit if applicable)
                    await HandleGitBehaviorAsync(
                        gitBehavior, worktreePath, branchName, targetCard, state, lastResult, cancellationToken);

                    if (gitBehavior == "discard")
                        await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);

                    await SafeDbCallAsync(() => runStore.CompleteRunAsync(runId, lastResult.Outcome, lastResult.Detail, null, cancellationToken));
                    return new AgentRunResult(lastResult.Outcome, lastResult.Detail, lastResult.Questions);
                }
            }

            // All steps complete
            // Add captured estimate to template context for transition actions (e.g., setField)
            if (capturedEstimate.HasValue)
            {
                templateContext["estimation"] = capturedEstimate.Value.ToString("G", CultureInfo.InvariantCulture);
            }
            else if (workflowConfig.Estimation is not null)
            {
                logger.LogWarning(
                    "Estimation is configured but no step returned a structured estimate for card {CardId}. " +
                    "The estimator agent may have described the estimate in the detail text without setting " +
                    "the 'estimate' field in its JSON output.",
                    cardId);
            }

            // 7. Run gate check if configured
            var gateCheckResult = await RunGateCheckAsync(
                session, state, lastResult!, worktreePath, targetCard, cardId, runId, cancellationToken);

            if (gateCheckResult.BlockingResult is not null)
            {
                // Gate blocked progression — handle git and return
                await HandleGitBehaviorAsync(
                    gitBehavior, worktreePath, branchName, targetCard, state, lastResult!, cancellationToken);

                if (gitBehavior == "discard")
                    await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);

                return gateCheckResult.BlockingResult;
            }

            // 7a. Execute optional steps if gate requested them
            if (gateCheckResult.RequestedSteps is { Count: > 0 } && state.OptionalSteps is { Count: > 0 })
            {
                var optionalResult = await ExecuteOptionalStepsAsync(
                    session, gateCheckResult.RequestedSteps, state, worktreePath, targetCard, cardId,
                    runId, commentPrefix, commentsFilePath, cancellationToken);

                if (optionalResult is not null)
                {
                    // An optional step halted progression
                    await HandleGitBehaviorAsync(
                        gitBehavior, worktreePath, branchName, targetCard, state,
                        new AgentResult(optionalResult.Outcome, optionalResult.ErrorDetail),
                        cancellationToken);

                    if (gitBehavior == "discard")
                        await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);

                    return optionalResult;
                }
            }

            } // end session try block
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                    logger.LogInformation(
                        "Session {SessionId} disposed for run {RunId} card {CardId}",
                        session.SessionId, runId, cardId);
                }

                // Dispose mount context after session (temp .git files must outlive the container)
                if (mountContext is not null)
                    await mountContext.DisposeAsync();
            }

            // 8. Handle git operations based on stage-specific behavior
            var gitNote = await HandleGitBehaviorAsync(
                gitBehavior, worktreePath, branchName, targetCard, state, lastResult!, cancellationToken);

            // 9. Post-process: upsert run-level comment (only when git note is present), move card to next state
            if (!string.IsNullOrEmpty(gitNote))
            {
                var runComment = $"{commentPrefix}\n\n---\n{gitNote}";
                await boardClient.UpsertAgentCommentAsync(cardId, runComment, runMarker, cancellationToken);
            }

            await SafeDbCallAsync(() => runStore.CompleteRunAsync(runId, lastResult!.Outcome, null, null, cancellationToken));

            var completeKey = lastResult!.Outcome.ToString();
            if (state.Transitions.TryGetValue(completeKey, out var completeTarget))
            {
                await TransitionExecutor.ExecuteAsync(
                    cardId, completeTarget, boardClient, logger, cancellationToken, templateContext, crossReferenceResolver, workflowConfig);
                logger.LogInformation("Executed {Outcome} transition for card {CardId}", completeKey, cardId);
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
        catch (RateLimitException rateLimitEx)
        {
            logger.LogWarning(rateLimitEx,
                "Rate limit hit during agent run for card {CardId} — restoring to trigger column {TriggerColumn}",
                cardId, targetCard.ColumnId);

            // Cleanup worktree for discard stages only (best effort)
            if (gitBehavior == "discard")
            {
                try
                {
                    await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogWarning(cleanupEx,
                        "Failed to cleanup worktree after rate limit for card {CardId}", cardId);
                }
            }

            // Restore card to original trigger column and post informational comment (best effort)
            try
            {
                await boardClient.MoveCardToColumnAsync(cardId, targetCard.ColumnId, cancellationToken);

                var rateLimitPrefix = BuildCommentPrefix(state, workflowConfig, agentIdentity);
                var rateLimitComment = $"{rateLimitPrefix}\n\n" +
                    $"**Rate limited** — card returned to **{targetCard.ColumnId}** for re-processing.\n\n" +
                    $"This is not a problem with the ticket — the agent's usage limit was reached. " +
                    $"The card can be picked up again when the limit resets, or by another agent.";
                await boardClient.UpsertAgentCommentAsync(
                    cardId, rateLimitComment, $"<!-- agent-rate-limit:{cardId} -->", cancellationToken);
            }
            catch (Exception restoreEx)
            {
                logger.LogWarning(restoreEx,
                    "Failed to restore card {CardId} to trigger column after rate limit — card may be stuck in IN_PROGRESS",
                    cardId);
            }

            await SafeDbCallAsync(() => runStore.CompleteRunAsync(runId, AgentOutcome.ERROR, rateLimitEx.Message, FailureReason.RATE_LIMIT, cancellationToken));

            // Rethrow so PollingRunner can back off, or Program.cs can handle cleanly
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent run failed for card {CardId}", cardId);

            // For commit-capable stages, try to record error in worktree
            if (gitBehavior is "commit_and_push" or "commit_only")
            {
                try
                {
                    var worktreePath = gitWorkspaceManager.ResolveWorktreePath(workspacePath, branchName);
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
                var preserveWorktree = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("AIBOARD_PRESERVE_WORKTREE"));

                if (preserveWorktree)
                {
                    var worktreePath = gitWorkspaceManager.ResolveWorktreePath(workspacePath, branchName);
                    logger.LogWarning(
                        "AIBOARD_PRESERVE_WORKTREE is set — keeping worktree for inspection at: {WorktreePath}",
                        Path.GetFullPath(worktreePath));
                }
                else
                {
                    // Discard stage: clean up the worktree
                    await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);
                }
            }

            // Best effort: post error comment and move card to error state
            try
            {
                var errorResult = new AgentResult(AgentOutcome.ERROR, ex.Message);
                var errorPrefix = BuildCommentPrefix(state, workflowConfig, agentIdentity);
                var comment = $"{errorPrefix}\n\n{FormatComment(errorResult)}";
                await boardClient.UpsertAgentCommentAsync(cardId, comment, runMarker, cancellationToken);

                if (state.Transitions.TryGetValue(TransitionKeys.Error, out var errorTarget))
                {
                    await TransitionExecutor.ExecuteAsync(
                        cardId, errorTarget, boardClient, logger, cancellationToken, crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);
                }
            }
            catch (Exception postEx)
            {
                logger.LogWarning(postEx, "Failed to post error feedback to board for card {CardId}", cardId);
            }

            await SafeDbCallAsync(() => runStore.CompleteRunAsync(runId, AgentOutcome.ERROR, ex.Message, FailureReason.AGENT_ERROR, cancellationToken));
            return new AgentRunResult(AgentOutcome.ERROR, ex.Message);
        }
        } // using logger scope
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
    /// <remarks>
    /// This method runs on the orchestrator host AFTER the agent (and any Docker container)
    /// has exited. All git write operations (commit, push) are intentionally orchestrator-side —
    /// agents must not run git write commands during execution. For Docker execution, the base
    /// .git directory is mounted read-only, physically enforcing this constraint.
    /// </remarks>
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

    // ── Template context ──────────────────────────────────────────────

    /// <summary>
    /// Builds the template variable context for transition action resolution.
    /// Currently resolves: {{agent}} → authenticated board user (with optional config override).
    /// </summary>
    private async Task<Dictionary<string, string>> BuildTemplateContextAsync(CancellationToken ct)
    {
        var context = new Dictionary<string, string>();
        try
        {
            var agentUsername = await boardClient.GetCurrentUserAsync(ct);
            if (!string.IsNullOrEmpty(agentUsername))
                context["agent"] = agentUsername;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve {{agent}} template variable — assign actions using {{agent}} will not be resolved");
        }
        return context;
    }

    // ── Gate check logic ─────────────────────────────────────────────

    /// <summary>
    /// Returned by RunGateCheckAsync. BlockingResult is non-null when the gate blocked
    /// progression; RequestedSteps carries optional steps requested when the gate passed.
    /// </summary>
    private sealed record GateCheckResult(
        AgentRunResult? BlockingResult,
        IReadOnlyList<string>? RequestedSteps);

    /// <summary>
    /// Runs an optional gate check after all agent steps complete successfully.
    /// Returns a GateCheckResult with null BlockingResult if the gate passes (or no gate configured).
    /// Returns a GateCheckResult with non-null BlockingResult if the gate blocks progression.
    /// </summary>
    private async Task<GateCheckResult> RunGateCheckAsync(
        IAgentExecutorSession? session,
        WorkflowState state,
        AgentResult lastStepResult,
        string worktreePath,
        BoardCard targetCard,
        string cardId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (state.GateCheck is null)
            return new GateCheckResult(null, null);

        var gateCheck = state.GateCheck;

        // Resolve gate role
        if (!workflowConfig.Roles.TryGetValue(gateCheck.Role, out var gateRole))
        {
            logger.LogError("Gate check role '{Role}' not found in workflow config — skipping gate", gateCheck.Role);
            return new GateCheckResult(null, null);
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
            return new GateCheckResult(null, null);
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
                return new GateCheckResult(null, null);
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

        // Append optional step catalog if configured
        if (state.OptionalSteps is { Count: > 0 })
        {
            var sb = new StringBuilder(gatePrompt);
            sb.AppendLine("\n\n## Available Optional Review Steps\n");
            sb.AppendLine("The following specialist review steps are available. Request any that are clearly "
                + "warranted by the changes above. Only request steps whose trigger criteria match.\n");
            sb.AppendLine("| Step Name | Description | When to Request |");
            sb.AppendLine("|-----------|-------------|----------------|");

            foreach (var opt in state.OptionalSteps)
            {
                sb.AppendLine($"| `{opt.Name}` | {opt.Description} | {opt.Triggers} |");
            }

            sb.AppendLine("\nTo request optional steps, include a `requestedSteps` array in your output "
                + "with the step names. You may request steps alongside a COMPLETE verdict.");
            gatePrompt = sb.ToString();
        }

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
            return new GateCheckResult(null, null);
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
                    ["effort"] = "low",
                });

            logger.LogInformation("Running gate check for card {CardId} in state {State}", cardId, state.Name);
            var gateStartedAt = DateTimeOffset.UtcNow;
            var (gateResult2, gateSessionExecMs) = await ExecuteWithSessionAsync(
                session, gateRole.Provider, gateContext, "gate_check", runId, cancellationToken);
            gateResult = gateResult2;
            logger.LogInformation("Gate check result for card {CardId}: {Outcome}", cardId, gateResult.Outcome);

            // Save gate check result to DB
            var gateRecord = new StepResultRecord(
                RunId: runId,
                CardId: cardId,
                StateName: state.Name,
                StepName: "gate_check",
                StepIndex: state.Steps.Count,
                Role: gateCheck.Role,
                Model: gateRole.Model,
                Outcome: gateResult.Outcome,
                Summary: gateResult.Detail,
                Detail: null,
                ReferenceContent: null,
                ConversationLog: gateResult.ConversationLog,
                Questions: gateResult.Questions,
                RequestedSteps: gateResult.RequestedSteps,
                StartedAtUtc: gateStartedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                SessionExecMs: gateSessionExecMs);
            await SafeDbCallAsync(() => runStore.SaveStepResultAsync(gateRecord, cancellationToken));
        }
        catch (Exception ex)
        {
            // Gate check infrastructure failure is non-blocking
            logger.LogError(ex, "Gate check failed to execute for card {CardId} — proceeding without verification", cardId);
            var gateIdentity = $"(via {agentIdentity.DisplayName})";
            var warningComment = $"<!-- gate-check:{state.Name} -->\n\n" +
                $"## Gate Check Warning {gateIdentity}\n\nGate check failed to execute: {ex.Message}\nProceeding without verification.";
            await boardClient.UpsertAgentCommentAsync(cardId, warningComment,
                $"<!-- gate-check:{state.Name} -->", cancellationToken);
            return new GateCheckResult(null, null);
        }

        // Interpret result
        switch (gateResult.Outcome)
        {
            case AgentOutcome.COMPLETE:
                // PASS — proceed with normal flow (possibly with optional step requests)
                logger.LogInformation("Gate check PASSED for card {CardId}", cardId);
                return new GateCheckResult(null, gateResult.RequestedSteps);

            case AgentOutcome.NEEDS_INFO:
            {
                // CONCERNS — route to questions column
                var gateIdentityConcerns = $"(via {agentIdentity.DisplayName})";
                var comment = $"<!-- gate-check:{state.Name} -->\n\n" +
                    $"## Gate Check: Concerns {gateIdentityConcerns}\n\n{gateResult.Detail ?? "The gate check raised concerns."}";
                await boardClient.UpsertAgentCommentAsync(cardId, comment,
                    $"<!-- gate-check:{state.Name} -->", cancellationToken);

                if (state.Transitions.TryGetValue(TransitionKeys.NeedsInfo, out var questionsTarget))
                    await TransitionExecutor.ExecuteAsync(
                        cardId, questionsTarget, boardClient, logger, cancellationToken, crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);

                return new GateCheckResult(
                    new AgentRunResult(AgentOutcome.NEEDS_INFO, gateResult.Detail, gateResult.Questions),
                    null);
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
                    var gateIdentityEscalate = $"(via {agentIdentity.DisplayName})";
                    var escalateComment = $"<!-- gate-check:{state.Name} result:ERROR attempt:{previousFailures + 1} -->\n\n" +
                        $"## Gate Check: Escalated to Human Review {gateIdentityEscalate}\n\n" +
                        $"The gate check has failed {previousFailures + 1} consecutive times. Escalating for human review.\n\n" +
                        $"**Latest failure reason:**\n{gateResult.Detail ?? "No detail provided."}";
                    await boardClient.UpsertAgentCommentAsync(cardId, escalateComment,
                        $"<!-- gate-check:{state.Name} -->", cancellationToken);

                    if (state.Transitions.TryGetValue(TransitionKeys.NeedsInfo, out var questionsCol))
                        await TransitionExecutor.ExecuteAsync(
                            cardId, questionsCol, boardClient, logger, cancellationToken, crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);

                    return new GateCheckResult(
                        new AgentRunResult(AgentOutcome.NEEDS_INFO, gateResult.Detail, gateResult.Questions),
                        null);
                }

                // Route via GATE_FAIL (re-trigger) or fall back to ERROR
                var gateIdentityFail = $"(via {agentIdentity.DisplayName})";
                var failComment = $"<!-- gate-check:{state.Name} result:ERROR attempt:{previousFailures + 1} -->\n\n" +
                    $"## Gate Check: Failed {gateIdentityFail}\n\n{gateResult.Detail ?? "The gate check detected issues with the agent's output."}";
                await boardClient.UpsertAgentCommentAsync(cardId, failComment,
                    $"<!-- gate-check:{state.Name} -->", cancellationToken);

                var transitionKey = state.Transitions.ContainsKey(TransitionKeys.GateFail) ? TransitionKeys.GateFail : TransitionKeys.Error;
                if (state.Transitions.TryGetValue(transitionKey, out var gateFailTarget))
                    await TransitionExecutor.ExecuteAsync(
                        cardId, gateFailTarget, boardClient, logger, cancellationToken, crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);

                return new GateCheckResult(new AgentRunResult(AgentOutcome.ERROR, gateResult.Detail), null);
            }
        }
    }

    /// <summary>
    /// Validates and executes optional steps requested by the gate check.
    /// Returns null if all optional steps completed successfully.
    /// Returns an AgentRunResult if any step halted progression.
    /// </summary>
    private async Task<AgentRunResult?> ExecuteOptionalStepsAsync(
        IAgentExecutorSession? session,
        IReadOnlyList<string> requestedStepNames,
        WorkflowState state,
        string worktreePath,
        BoardCard targetCard,
        string cardId,
        string runId,
        string commentPrefix,
        string? commentsFilePath,
        CancellationToken cancellationToken)
    {
        // Build lookup of available optional steps
        var catalog = state.OptionalSteps!
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // Validate requested step names against catalog; skip unknowns
        var stepsToExecute = new List<OptionalStepDefinition>();
        foreach (var name in requestedStepNames)
        {
            if (catalog.TryGetValue(name, out var stepDef))
            {
                stepsToExecute.Add(stepDef);
            }
            else
            {
                logger.LogWarning(
                    "Gate check requested optional step '{StepName}' for card {CardId} " +
                    "but it is not in the state's optional step catalog. Skipping.",
                    name, cardId);
            }
        }

        if (stepsToExecute.Count == 0)
        {
            logger.LogInformation("No valid optional steps to execute for card {CardId}", cardId);
            return null;
        }

        logger.LogInformation(
            "Executing {Count} optional step(s) for card {CardId}: {StepNames}",
            stepsToExecute.Count, cardId, string.Join(", ", stepsToExecute.Select(s => s.Name)));

        for (var i = 0; i < stepsToExecute.Count; i++)
        {
            var step = stepsToExecute[i];

            if (!workflowConfig.Roles.TryGetValue(step.Role, out var stepRole))
            {
                logger.LogWarning(
                    "Optional step '{StepName}' references missing role '{Role}' for card {CardId}. Skipping.",
                    step.Name, step.Role, cardId);
                continue;
            }

            logger.LogInformation(
                "Executing optional step {Index}/{Count} '{StepName}' (role={Role}) for card {CardId}",
                i + 1, stepsToExecute.Count, step.Name, step.Role, cardId);

            var systemPromptFilePath = await ResolveSystemPromptFileAsync(
                stepRole, step.Role, worktreePath, workflowConfig.ConfigDirectory, cancellationToken);

            var resolvedPrompt = await ResolveTaskPromptFromFileOrInlineAsync(
                step.TaskPromptFile, step.TaskPrompt, step.Name, worktreePath, targetCard, cancellationToken);

            var effectiveParams = MergeProviderParams(state.ProviderParams, step.ProviderParams);

            var context = new AgentExecutionContext(
                TargetCardId: cardId,
                TargetCardTitle: targetCard.Title,
                WorkspacePath: worktreePath,
                TaskPrompt: resolvedPrompt,
                SystemPromptFilePath: systemPromptFilePath,
                Model: stepRole.Model,
                ProviderParams: effectiveParams,
                CommentsFilePath: commentsFilePath);

            var optionalStepStartedAt = DateTimeOffset.UtcNow;
            var (result, optionalSessionExecMs) = await ExecuteWithSessionAsync(
                session, stepRole.Provider, context, $"optional:{step.Name}", runId, cancellationToken);

            // Save optional step result to DB
            var optionalStepRecord = new StepResultRecord(
                RunId: runId,
                CardId: cardId,
                StateName: state.Name,
                StepName: $"optional:{step.Name}",
                StepIndex: state.Steps.Count + 1 + i,
                Role: step.Role,
                Model: stepRole.Model,
                Outcome: result.Outcome,
                Summary: result.Detail,
                Detail: null,
                ReferenceContent: null,
                ConversationLog: result.ConversationLog,
                Questions: result.Questions,
                RequestedSteps: result.RequestedSteps,
                StartedAtUtc: optionalStepStartedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                SessionExecMs: optionalSessionExecMs);
            await SafeDbCallAsync(() => runStore.SaveStepResultAsync(optionalStepRecord, cancellationToken));

            // Update card body
            await UpdateCardBodyFromTaskFileAsync(targetCard, worktreePath, cancellationToken,
                trimForBoard: runStore is not NullRunStore);

            // Post step-specific comment with optional: prefix to avoid marker collision
            var stepMarker = $"<!-- agent-step:optional:{step.Name} -->";
            var stepComment = $"{commentPrefix}\n\n**Optional Step: {step.Name}**\n\n{FormatComment(result, includeConversationLog: runStore is NullRunStore)}";
            await boardClient.UpsertAgentCommentAsync(cardId, stepComment, stepMarker, cancellationToken);

            // Refresh comments file for the next step
            var comments = await boardClient.GetCardCommentsAsync(cardId, cancellationToken);
            if (comments.Count > 0)
            {
                await taskFileManager.WriteCommentsFileAsync(
                    worktreePath, targetCard.Id, targetCard.Title, comments, cancellationToken);
                commentsFilePath ??= TaskFileManager.GetCommentsFilePath(
                    worktreePath, targetCard.Id, targetCard.Title);
            }

            if (result.Outcome != AgentOutcome.COMPLETE)
            {
                logger.LogWarning(
                    "Optional step '{StepName}' returned {Outcome} for card {CardId}. Halting.",
                    step.Name, result.Outcome, cardId);

                var outcomeKey = result.Outcome.ToString();
                if (state.Transitions.TryGetValue(outcomeKey, out var optOutcomeTarget))
                    await TransitionExecutor.ExecuteAsync(
                        cardId, optOutcomeTarget, boardClient, logger, cancellationToken, crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);

                return new AgentRunResult(result.Outcome, result.Detail, result.Questions);
            }
        }

        logger.LogInformation("All optional steps completed for card {CardId}", cardId);
        return null;
    }

    // ── Session-aware execution ───────────────────────────────────────

    /// <summary>
    /// Attempts to create a reusable container session for the primary executor of this run.
    /// Returns null if: no sessionable executor is registered, ReuseContainer is false,
    /// or session creation is not possible (executor returns null from TryCreateSessionAsync).
    /// </summary>
    private async Task<IAgentExecutorSession?> TryCreateSessionAsync(
        WorkflowState state,
        string cardId,
        string runId,
        DockerMountContext? mountContext,
        CancellationToken cancellationToken)
    {
        if (state.Steps is not { Count: > 0 })
            return null;

        var firstStep = state.Steps[0];
        if (!workflowConfig.Roles.TryGetValue(firstStep.Role, out var firstRole))
            return null;

        var executor = executorResolver.Resolve(firstRole.Provider);
        if (executor is not ISessionableAgentExecutor sessionableExecutor)
            return null;

        var containerName = $"aiboard-{tenant.ShortHash}-{cardId}";
        var imageName = dockerOptions?.ImageName ?? "aiboard-agent:latest";
        var request = new SessionRequest(
            CardId: cardId,
            RunId: runId,
            ContainerName: containerName,
            ImageName: imageName,
            Mounts: mountContext?.Mounts,
            EnvironmentVariables: mountContext?.EnvironmentVariables);

        logger.LogInformation(
            "Creating session for run {RunId} card {CardId} (container={ContainerName}, image={ImageName}, mounts={MountCount})",
            runId, cardId, containerName, imageName, request.Mounts?.Count ?? 0);

        return await sessionableExecutor.TryCreateSessionAsync(request, cancellationToken);
    }

    /// <summary>
    /// Executes an agent invocation using the session if alive and provider matches;
    /// falls back to direct per-step execution transparently.
    /// Returns the result and the session execution time in ms (null if not executed via session).
    /// </summary>
    private async Task<(AgentResult Result, int? SessionExecMs)> ExecuteWithSessionAsync(
        IAgentExecutorSession? session,
        string providerKey,
        AgentExecutionContext context,
        string stepName,
        string runId,
        CancellationToken cancellationToken)
    {
        // Provider mismatch: step uses a different provider than the session
        if (session is not null
            && !string.Equals(session.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Step '{StepName}' provider '{Provider}' does not match session provider '{SessionProvider}', " +
                "using direct execution",
                stepName, providerKey, session.ProviderKey);
        }
        else if (session is not null && session.IsAlive)
        {
            // Session is alive and provider matches — execute via container
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var sessionResult = await session.ExecuteInSessionAsync(context, cancellationToken);
                sw.Stop();
                logger.LogDebug(
                    "Step '{StepName}' executed via session {SessionId} run {RunId} in {ExecMs}ms",
                    stepName, session.SessionId, runId, sw.ElapsedMilliseconds);
                return (sessionResult, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                logger.LogWarning(ex,
                    "Session {SessionId} execution failed for step '{StepName}' run {RunId}, " +
                    "falling back to direct execution",
                    session.SessionId, stepName, runId);
                // Fall through to direct execution
            }
        }
        else if (session is not null && !session.IsAlive)
        {
            logger.LogWarning(
                "Session {SessionId} is no longer alive for step '{StepName}' run {RunId}, " +
                "falling back to direct execution",
                session.SessionId, stepName, runId);
        }

        // Direct execution (no session, provider mismatch, or session dead/failed)
        var directExecutor = executorResolver.Resolve(providerKey);
        var directResult = await directExecutor.ExecuteAsync(context, cancellationToken);
        return (directResult, null);
    }

    /// <summary>
    /// Merges step-level providerParams over state-level defaults.
    /// Step values override state values for matching keys; state values fill gaps.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? MergeProviderParams(
        Dictionary<string, string>? stateParams,
        Dictionary<string, string>? stepParams)
    {
        if (stepParams is null or { Count: 0 }) return stateParams;
        if (stateParams is null or { Count: 0 }) return stepParams;

        var merged = new Dictionary<string, string>(stateParams);
        foreach (var (key, value) in stepParams)
            merged[key] = value; // step overrides state

        return merged;
    }

    /// <summary>
    /// Resolves a task prompt from a file path or inline text, then applies card placeholders.
    /// Shared by mandatory step resolution and optional step resolution.
    /// </summary>
    private async Task<string> ResolveTaskPromptFromFileOrInlineAsync(
        string? taskPromptFile, string? taskPrompt, string stepName,
        string worktreePath, BoardCard card, CancellationToken cancellationToken)
    {
        string template;
        if (taskPromptFile is not null)
        {
            var basePath = workflowConfig.ConfigDirectory ?? worktreePath;
            var path = Path.GetFullPath(Path.Combine(basePath, taskPromptFile));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Optional step '{stepName}' task prompt file not found: {path}", path);
            template = await File.ReadAllTextAsync(path, cancellationToken);
        }
        else if (taskPrompt is not null)
        {
            template = taskPrompt;
        }
        else
        {
            throw new InvalidOperationException(
                $"Optional step '{stepName}' has neither taskPrompt nor taskPromptFile.");
        }

        return ResolvePromptPlaceholders(template, card);
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
        TransitionTarget? KickBackTarget = null);

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

        var hasMergeConflictTransition = state.Transitions.ContainsKey(TransitionKeys.MergeConflict);

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

                state.Transitions.TryGetValue(TransitionKeys.MergeConflict, out var kickBackTarget);
                var comment = BuildMergeKickBackComment(mergeResult, mergeAgentResult, defaultBranch, branchName);
                return new MergeStepOutcome(MergeStepAction.KickBack, KickBackComment: comment, KickBackTarget: kickBackTarget);
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
        var mergeExecutor = executorResolver.Resolve(mergeRole.Provider);
        return await mergeExecutor.ExecuteAsync(context, cancellationToken);
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
        var comment = $"{commentPrefix}\n\n{FormatComment(agentResult, gitNote, includeConversationLog: runStore is NullRunStore)}";
        await boardClient.UpsertAgentCommentAsync(originalCard.Id, comment, runMarker, cancellationToken);
        logger.LogInformation("Posted agent comment for {CardId}", originalCard.Id);

        // 9c. Execute transition actions for outcome
        var outcomeKey = agentResult.Outcome.ToString();
        if (state.Transitions.TryGetValue(outcomeKey, out var outcomeTarget))
        {
            await TransitionExecutor.ExecuteAsync(
                originalCard.Id, outcomeTarget, boardClient, logger, cancellationToken, crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);
            logger.LogInformation("Executed {Outcome} transition for card {CardId}", outcomeKey, originalCard.Id);
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
            || (config.ResolveState(card) is { IncludeInAgentContext: true })
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
        BoardCard originalCard, string worktreePath, CancellationToken cancellationToken,
        bool trimForBoard = false)
    {
        var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, originalCard.Id, originalCard.Title);
        if (!File.Exists(taskFilePath))
            return;

        var taskFileContent = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
        var bodyFromFile = TaskFileManager.ExtractBodyFromTaskFile(taskFileContent);
        var cleanBody = TaskFileManager.StripAnnotations(bodyFromFile);

        if (trimForBoard)
            cleanBody = TaskFileManager.TrimToSummary(cleanBody);

        if (!string.Equals(cleanBody, originalCard.Body, StringComparison.Ordinal))
        {
            await boardClient.UpdateCardBodyAsync(originalCard.Id, cleanBody, cancellationToken);
            logger.LogInformation("Updated card body for {CardId}{Trimmed}",
                originalCard.Id, trimForBoard ? " (trimmed for board)" : "");
        }
    }

    /// <summary>
    /// Wraps a DB call so that any exception is logged as a warning and the run continues.
    /// A DB failure must never interrupt an agent run.
    /// </summary>
    private async Task SafeDbCallAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database operation failed - continuing without DB persistence");
        }
    }

    /// <summary>
    /// Queries the DB for prior step results for this card and writes them to
    /// .aiboard/context/step-history.md in the worktree so agents have cross-run context.
    /// Skips silently when using NullRunStore or when no prior results exist.
    /// </summary>
    private async Task WritePriorStepContextAsync(
        string worktreePath, string cardId, string currentStateName, CancellationToken cancellationToken)
    {
        if (runStore is NullRunStore)
            return;

        IReadOnlyList<StepResultRecord> priorResults;
        try
        {
            priorResults = await runStore.GetStepResultsForCardAsync(cardId, null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read prior step results from DB for card {CardId}", cardId);
            return;
        }

        if (priorResults.Count == 0)
            return;

        var contextDir = Path.Combine(worktreePath, ".aiboard", "context");
        Directory.CreateDirectory(contextDir);

        var sb = new StringBuilder();
        sb.AppendLine("# Prior Step Results");
        sb.AppendLine();
        sb.AppendLine("These are results from prior agent runs on this card. Use them for context.");
        sb.AppendLine();

        foreach (var result in priorResults)
        {
            sb.AppendLine($"## {result.StateName} / {result.StepName} ({result.Outcome})");
            sb.AppendLine($"*Run: {result.RunId} | Role: {result.Role} | {result.CompletedAtUtc:u}*");
            sb.AppendLine();

            if (result.Detail is not null)
            {
                sb.AppendLine(result.Detail);
                sb.AppendLine();
            }
            else if (result.Summary is not null)
            {
                sb.AppendLine(result.Summary);
                sb.AppendLine();
            }

            if (result.ReferenceContent is not null)
            {
                sb.AppendLine("### Reference Content");
                sb.AppendLine(result.ReferenceContent);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(
            Path.Combine(contextDir, "step-history.md"), sb.ToString(), cancellationToken);

        logger.LogInformation("Wrote prior step context for card {CardId} ({Count} results)", cardId, priorResults.Count);
    }

    internal static async Task<string> ResolveStepTaskPromptAsync(
        WorkflowStep step, string worktreePath, BoardCard card, string? configDirectory, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraContext = null)
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

        return ResolvePromptPlaceholders(template, card, extraContext);
    }

    private static string BuildCommentPrefix(WorkflowState state, WorkflowConfig config, AgentIdentity identity)
    {
        var activeStateName = state.Transitions.TryGetValue(TransitionKeys.InProgress, out var inProgressTarget)
            && inProgressTarget.Column is string inProgressCol
            && config.FindStatesByColumn(inProgressCol).FirstOrDefault() is { } inProgressState
            ? inProgressState.Name
            : state.Name;

        // For multi-step states, use the first step's role or fall back to state-level role
        var roleName = state.Steps is { Count: > 0 }
            ? state.Steps[0].Role
            : state.Role ?? "agent";
        return $"**{roleName} in {activeStateName} ({identity.DisplayName}):**";
    }

    internal static string FormatUpdateSummary(UpdateProcessingResult result)
    {
        var sb = new StringBuilder("\n\n---\n**Update file actions:**\n");

        foreach (var ticket in result.CreatedTickets)
            sb.AppendLine($"- Created #{ticket.NewCardId} — {ticket.Title}");

        foreach (var comment in result.PostedComments)
            sb.AppendLine($"- Posted cross-card comment on #{comment.TargetCardId}");

        return sb.ToString();
    }

    internal static string FormatComment(AgentResult result, string? gitNote = null, bool includeConversationLog = true)
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

        // Conversation log: include in comment only when no DB is available (includeConversationLog = true)
        if (includeConversationLog && !string.IsNullOrEmpty(result.ConversationLog))
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

    internal static string ResolvePromptPlaceholders(
        string template, BoardCard card,
        IReadOnlyDictionary<string, string>? extraContext = null)
    {
        return PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return key switch
            {
                "TaskName" => card.Title,
                "TaskId" => card.Id,
                _ => extraContext is not null && extraContext.TryGetValue(key, out var value)
                    ? value
                    : match.Value
            };
        });
    }
}

public sealed record AgentRunResult(
    AgentOutcome Outcome,
    string? ErrorDetail,
    IReadOnlyList<AgentQuestion>? Questions = null);
