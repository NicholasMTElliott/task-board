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
    ILogger<AgentRunner> logger)
{
    private static readonly Regex PlaceholderRegex = PlaceholderPattern();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    private const string CommitFilePath = ".aiboard/commit.md";

    public async Task<AgentRunResult> ExecuteAsync(
        string cardId, string boardId, string workspacePath, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting agent run for card {CardId} in workspace {Workspace}",
            cardId, workspacePath);

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

        if (state.Role is null || !workflowConfig.Roles.TryGetValue(state.Role, out var role))
        {
            logger.LogError("State {StateName} has no valid role", state.Name);
            return new AgentRunResult(AgentOutcome.ERROR, $"No valid role for state {state.Name}");
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

            // 5. Write all task files into the worktree
            await taskFileManager.WriteAllTaskFilesAsync(worktreePath, cards, workflowConfig, cancellationToken);

            // 5a. Resolve system prompt file path (writes temp file for inline prompts)
            var systemPromptFilePath = await ResolveSystemPromptFileAsync(
                role, state.Role!, worktreePath, cancellationToken);

            // 5b. Resolve task prompt (from file or inline), with placeholder substitution
            var resolvedPrompt = await ResolveTaskPromptAsync(state, worktreePath, targetCard, cancellationToken);

            if (isExistingBranch)
            {
                resolvedPrompt += "\n\nNote: This task has been worked on previously. A branch with prior changes already exists. " +
                    "Review the existing state of the codebase and any changes already made before beginning new work. " +
                    "Avoid duplicating or overwriting prior progress.";
            }

            // 7. Execute agent — WorkspacePath is the worktree, so Claude CLI runs there
            var context = new AgentExecutionContext(
                TargetCardId: cardId,
                TargetCardTitle: targetCard.Title,
                WorkspacePath: worktreePath,
                TaskPrompt: resolvedPrompt,
                SystemPromptFilePath: systemPromptFilePath,
                Model: role.Model,
                ProviderParams: state.ProviderParams);

            var agentResult = await agentExecutor.ExecuteAsync(context, cancellationToken);

            // 8. Handle git operations based on stage-specific behavior
            var gitNote = await HandleGitBehaviorAsync(
                gitBehavior, worktreePath, branchName, targetCard, state, agentResult, cancellationToken);

            // 9. Post-process: update card on board, add comment, move to next state
            await PostProcessAsync(targetCard, state, agentResult, worktreePath, branchName, gitNote, cancellationToken);

            // 10. Cleanup worktree for discard stages
            if (gitBehavior == "discard")
            {
                await CleanupWorktreeAsync(workspacePath, branchName, cancellationToken);
            }

            logger.LogInformation("Agent run complete for card {CardId}: outcome={Outcome}, detail={Detail}",
                cardId, agentResult.Outcome, agentResult.Detail ?? "(none)");
            return new AgentRunResult(agentResult.Outcome, agentResult.Detail, agentResult.Questions);
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
                var comment = FormatComment(errorResult);
                await boardClient.UpsertAgentCommentAsync(cardId, comment, cancellationToken);

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

    private async Task PostProcessAsync(
        BoardCard originalCard,
        WorkflowState state,
        AgentResult agentResult,
        string worktreePath,
        string branchName,
        string? gitNote,
        CancellationToken cancellationToken)
    {
        // 9a. Read back the task file to detect agent changes to card content
        var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, originalCard.Id, originalCard.Title);
        if (File.Exists(taskFilePath))
        {
            var taskFileContent = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
            var bodyFromFile = TaskFileManager.ExtractBodyFromTaskFile(taskFileContent);

            if (!string.Equals(bodyFromFile, originalCard.Body, StringComparison.Ordinal))
            {
                await boardClient.UpdateCardBodyAsync(originalCard.Id, bodyFromFile, cancellationToken);
                logger.LogInformation("Updated card body for {CardId}", originalCard.Id);
            }
        }

        // 9b. Format and post comment (with optional git note)
        var comment = FormatComment(agentResult, gitNote);
        await boardClient.UpsertAgentCommentAsync(originalCard.Id, comment, cancellationToken);
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
        WorkflowRole role, string roleName, string worktreePath, CancellationToken cancellationToken)
    {
        if (role.SystemPromptFile is not null)
        {
            var path = Path.GetFullPath(Path.Combine(worktreePath, role.SystemPromptFile));
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
        WorkflowState state, string worktreePath, BoardCard card, CancellationToken cancellationToken)
    {
        string template;

        if (state.TaskPromptFile is not null)
        {
            var path = Path.GetFullPath(Path.Combine(worktreePath, state.TaskPromptFile));
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
