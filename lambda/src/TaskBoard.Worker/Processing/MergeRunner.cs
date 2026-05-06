using System.Text;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class MergeRunner(
    ITaskBoardClient boardClient,
    GitWorkspaceManager gitWorkspaceManager,
    WorkflowConfig workflowConfig,
    AgentIdentity agentIdentity,
    ICrossReferenceResolver crossReferenceResolver,
    IAgentExecutorResolver executorResolver,
    ILogger<MergeRunner> logger,
    DependencyGuard? dependencyGuard = null)
{
    private const int DefaultMaxRetries = 3;

    public async Task<AgentRunResult> ExecuteAsync(
        string cardId, string boardId, string workspacePath, CancellationToken cancellationToken)
    {
        var runId = $"merge-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
        var runMarker = $"<!-- merge-run:{runId} -->";

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["AgentName"] = agentIdentity.DisplayName,
            ["RunId"] = runId
        }))
        {

        logger.LogInformation("Starting merge run {RunId} for card {CardId}", runId, cardId);

        // 1. Fetch card and validate state
        var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken, workflowConfig.GetTerminalColumnNames());
        var card = cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null)
        {
            logger.LogError("Card {CardId} not found on board {BoardId}", cardId, boardId);
            return new AgentRunResult(AgentOutcome.ERROR, "Card not found on board");
        }

        var state = workflowConfig.ResolveState(card);
        if (state is null)
        {
            logger.LogError("Card {CardId} is in column {ColumnId} which is not in workflow config", cardId, card.ColumnId);
            return new AgentRunResult(AgentOutcome.ERROR, "Card column not in workflow config");
        }

        if (!string.Equals(state.GateType, GateTypes.SystemMerge, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Card {CardId} is in state {State} with gateType {GateType}, expected system_merge",
                cardId, state.Name, state.GateType);
            return new AgentRunResult(AgentOutcome.ERROR, $"State {state.Name} is not a system_merge state");
        }

        if (dependencyGuard is not null)
        {
            var dependencyResult = await dependencyGuard.CheckAsync(
                card, state, "merge", cancellationToken);
            if (dependencyResult.IsBlocked)
            {
                var blockers = string.Join(", ", dependencyResult.UnresolvedBlockers.Select(b => $"#{b.CardId}"));
                return new AgentRunResult(
                    AgentOutcome.NEEDS_INFO,
                    $"Card is blocked by unresolved dependencies: {blockers}");
            }
        }

        var maxRetries = ParseMaxRetries(state.ProviderParams);

        // 2. Move to IN_PROGRESS
        if (state.Transitions.TryGetValue(TransitionKeys.InProgress, out var inProgressTarget))
        {
            await TransitionExecutor.ExecuteAsync(
                cardId, inProgressTarget, boardClient, logger, cancellationToken,
                crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);
            logger.LogInformation("Moved card {CardId} to in-progress via {Count} action(s)",
                cardId, inProgressTarget.Actions.Count);
        }

        // 3. Find the work branch
        var workBranch = await gitWorkspaceManager.FindBranchByPrefixAsync(workspacePath, cardId, cancellationToken);
        if (workBranch is null)
        {
            var detail = $"No branch found for card {cardId}. The work branch may have been deleted or the card may not have gone through implementation.";
            logger.LogError("No branch found for card {CardId}", cardId);
            await PostCommentBestEffort(cardId, detail, runMarker, cancellationToken);
            await TransitionBestEffort(cardId, state, TransitionKeys.Error, cancellationToken);
            return new AgentRunResult(AgentOutcome.ERROR, detail);
        }

        try
        {
            // 4. Fetch latest remote state
            await gitWorkspaceManager.FetchAsync(workspacePath, cancellationToken);

            // 5. Detect default branch
            var defaultBranch = await gitWorkspaceManager.GetDefaultBranchAsync(workspacePath, cancellationToken);
            logger.LogInformation("Default branch: {DefaultBranch}, work branch: {WorkBranch}", defaultBranch, workBranch);

            // 6. Check if already merged
            if (await gitWorkspaceManager.IsAncestorAsync(workspacePath, workBranch, defaultBranch, cancellationToken))
            {
                logger.LogInformation("Branch {WorkBranch} already merged into {DefaultBranch}", workBranch, defaultBranch);
                await CleanupBranchesAsync(workspacePath, workBranch, cancellationToken);
                var detail = $"Branch `{workBranch}` is already merged into `{defaultBranch}`. Cleaned up branches.";
                await PostComment(cardId, detail, card, runMarker, cancellationToken);
                await TransitionBestEffort(cardId, state, TransitionKeys.Complete, cancellationToken);
                return new AgentRunResult(AgentOutcome.COMPLETE, detail);
            }

            // 7. Retry loop
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                logger.LogInformation("Merge attempt {Attempt}/{MaxRetries} for card {CardId}",
                    attempt, maxRetries, cardId);

                var mergeBranch = $"merge/{cardId}";
                var mergePath = gitWorkspaceManager.ResolveWorktreePath(workspacePath, mergeBranch);

                try
                {
                    // Clean up stale merge branch if it exists
                    await DeleteLocalBranchBestEffort(workspacePath, mergeBranch, cancellationToken);

                    // Create temp worktree from origin/{defaultBranch}
                    await CreateMergeWorktreeAsync(workspacePath, mergePath, mergeBranch, defaultBranch, cancellationToken);

                    // Merge work branch
                    var mergeMessage = $"Merge #{cardId}: {card.Title}";
                    var mergeResult = await gitWorkspaceManager.MergeNoFfAsync(
                        mergePath, $"origin/{workBranch}", mergeMessage, cancellationToken);

                    if (mergeResult == MergeStatus.Conflict)
                    {
                        logger.LogInformation("Merge conflicts between {WorkBranch} and {DefaultBranch} — attempting resolution",
                            workBranch, defaultBranch);

                        // Attempt merge resolution via agent before giving up
                        var resolved = await TryResolveMergeConflictsAsync(
                            mergePath, cardId, card, workBranch, defaultBranch, cancellationToken);

                        if (!resolved)
                        {
                            try { await gitWorkspaceManager.AbortMergeAsync(mergePath, cancellationToken); }
                            catch (GitOperationException) { /* best effort */ }
                            await CleanupMergeWorktreeAsync(workspacePath, mergePath, mergeBranch, cancellationToken);

                            var transitionKey = state.Transitions.ContainsKey(TransitionKeys.MergeConflict)
                                ? TransitionKeys.MergeConflict
                                : TransitionKeys.Error;

                            var detail = $"Merge conflicts between `{workBranch}` and `{defaultBranch}` could not be resolved automatically.";
                            await PostComment(cardId, detail, card, runMarker, cancellationToken);
                            await TransitionBestEffort(cardId, state, transitionKey, cancellationToken);
                            return new AgentRunResult(AgentOutcome.ERROR, detail);
                        }

                        logger.LogInformation("Merge conflicts resolved by agent for {WorkBranch}", workBranch);
                    }

                    // Push merge result to main
                    var pushResult = await gitWorkspaceManager.PushRefAsync(
                        workspacePath, mergeBranch, defaultBranch, cancellationToken);

                    if (pushResult == PushStatus.Success)
                    {
                        logger.LogInformation("Successfully merged and pushed {WorkBranch} into {DefaultBranch}",
                            workBranch, defaultBranch);
                        await CleanupMergeWorktreeAsync(workspacePath, mergePath, mergeBranch, cancellationToken);
                        await CleanupBranchesAsync(workspacePath, workBranch, cancellationToken);

                        var detail = $"Merged `{workBranch}` into `{defaultBranch}` and pushed to origin.\nRemote branch `{workBranch}` deleted.";
                        await PostComment(cardId, detail, card, runMarker, cancellationToken);
                        await TransitionBestEffort(cardId, state, TransitionKeys.Complete, cancellationToken);
                        return new AgentRunResult(AgentOutcome.COMPLETE, detail);
                    }

                    // Push failed (non-fast-forward) — retry
                    logger.LogWarning("Push failed for card {CardId} (main advanced), attempt {Attempt}/{MaxRetries}",
                        cardId, attempt, maxRetries);
                    await CleanupMergeWorktreeAsync(workspacePath, mergePath, mergeBranch, cancellationToken);

                    // Re-fetch and check if someone else merged it
                    await gitWorkspaceManager.FetchAsync(workspacePath, cancellationToken);
                    if (await gitWorkspaceManager.IsAncestorAsync(workspacePath, workBranch, defaultBranch, cancellationToken))
                    {
                        logger.LogInformation("Branch {WorkBranch} was merged by another process", workBranch);
                        await CleanupBranchesAsync(workspacePath, workBranch, cancellationToken);
                        var detail = $"Branch `{workBranch}` was merged into `{defaultBranch}` by another process.";
                        await PostComment(cardId, detail, card, runMarker, cancellationToken);
                        await TransitionBestEffort(cardId, state, TransitionKeys.Complete, cancellationToken);
                        return new AgentRunResult(AgentOutcome.COMPLETE, detail);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    // Unexpected error during this attempt — clean up and retry
                    logger.LogWarning(ex, "Merge attempt {Attempt} failed unexpectedly for card {CardId}, will retry",
                        attempt, cardId);
                    await CleanupMergeWorktreeAsync(workspacePath, mergePath, mergeBranch, cancellationToken);
                }
            }

            // Exhausted retries
            var exhaustedDetail = $"Failed to merge after {maxRetries} attempts (main keeps advancing).";
            logger.LogError("Exhausted {MaxRetries} merge retries for card {CardId}", maxRetries, cardId);
            await PostCommentBestEffort(cardId, exhaustedDetail, runMarker, cancellationToken);
            await TransitionBestEffort(cardId, state, TransitionKeys.Error, cancellationToken);
            return new AgentRunResult(AgentOutcome.ERROR, exhaustedDetail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Merge failed for card {CardId}: {Message}", cardId, ex.Message);
            await PostCommentBestEffort(cardId, $"Merge error: {ex.Message}", runMarker, cancellationToken);
            await TransitionBestEffort(cardId, state, TransitionKeys.Error, cancellationToken);
            return new AgentRunResult(AgentOutcome.ERROR, ex.Message);
        }
        } // using logger scope
    }

    private async Task CreateMergeWorktreeAsync(
        string repoPath, string mergePath, string mergeBranch, string defaultBranch,
        CancellationToken cancellationToken)
    {
        var fullMergePath = Path.GetFullPath(mergePath);

        // Clean up stale directory if it exists
        if (Directory.Exists(fullMergePath))
        {
            logger.LogWarning("Stale merge worktree at {Path} — removing", fullMergePath);
            Directory.Delete(fullMergePath, recursive: true);
            try { await GitWorkspaceManager.RunGitAsync(repoPath, ["worktree", "prune"], cancellationToken); }
            catch (GitOperationException) { /* best effort */ }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullMergePath)!);

        await GitWorkspaceManager.RunGitAsync(repoPath,
            ["worktree", "add", fullMergePath, "-b", mergeBranch, $"origin/{defaultBranch}"],
            cancellationToken);
    }

    private async Task<bool> TryResolveMergeConflictsAsync(
        string mergePath, string cardId, BoardCard card,
        string workBranch, string defaultBranch, CancellationToken cancellationToken)
    {
        var mergeResolution = workflowConfig.MergeResolution;
        if (mergeResolution is null)
        {
            logger.LogWarning("No mergeResolution config — cannot attempt merge conflict resolution");
            return false;
        }

        if (!workflowConfig.Roles.TryGetValue(mergeResolution.Role, out var mergeRole))
        {
            logger.LogError("Merge resolution role {Role} not found in workflow config", mergeResolution.Role);
            return false;
        }

        // Gather conflict file list from the merge worktree
        IReadOnlyList<string> conflictFiles;
        try
        {
            var (_, conflictOutput, _) = await GitWorkspaceManager.RunGitAsync(mergePath,
                ["diff", "--name-only", "--diff-filter=U"], cancellationToken);
            conflictFiles = conflictOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim()).ToArray();
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "Failed to enumerate conflict files — skipping resolution");
            return false;
        }

        try
        {
            var systemPromptPath = await AgentRunner.ResolveSystemPromptFileAsync(
                mergeRole, mergeResolution.Role, mergePath, workflowConfig.ConfigDirectory, cancellationToken);

            var taskPrompt = BuildMergeResolutionTaskPrompt(workBranch, defaultBranch, conflictFiles);

            var context = new AgentExecutionContext(
                TargetCardId: cardId,
                TargetCardTitle: card.Title,
                WorkspacePath: mergePath,
                TaskPrompt: taskPrompt,
                SystemPromptFilePath: systemPromptPath,
                Model: mergeRole.Model,
                ProviderParams: mergeResolution.ProviderParams,
                CommentsFilePath: null);

            logger.LogInformation("Running merge resolution agent for card {CardId} ({ConflictCount} conflicts)",
                cardId, conflictFiles.Count);

            var mergeExecutor = executorResolver.Resolve(mergeRole.Provider);
            var result = await mergeExecutor.ExecuteAsync(context, cancellationToken);

            if (result.Outcome == AgentOutcome.COMPLETE)
            {
                // Agent resolved conflicts — stage and commit the resolution
                await GitWorkspaceManager.RunGitAsync(mergePath, ["add", "."], cancellationToken);
                await GitWorkspaceManager.RunGitAsync(mergePath,
                    ["commit", "--no-edit"], cancellationToken);
                return true;
            }

            logger.LogWarning("Merge resolution agent returned {Outcome} for card {CardId}: {Detail}",
                result.Outcome, cardId, result.Detail);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Merge resolution failed for card {CardId} — falling back to conflict transition", cardId);
            return false;
        }
    }

    private static string BuildMergeResolutionTaskPrompt(
        string workBranch, string defaultBranch, IReadOnlyList<string> conflictFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"A merge of `{workBranch}` into `{defaultBranch}` resulted in conflicts.");
        sb.AppendLine();
        sb.AppendLine("Conflicted files:");
        foreach (var f in conflictFiles)
            sb.AppendLine($"- {f}");
        sb.AppendLine();
        sb.AppendLine("Resolve all conflict markers (<<<<<<< / ======= / >>>>>>>), verify the build passes, and confirm the merged result is correct.");
        return sb.ToString();
    }

    private async Task CleanupMergeWorktreeAsync(
        string repoPath, string mergePath, string mergeBranch,
        CancellationToken cancellationToken)
    {
        try
        {
            await gitWorkspaceManager.RemoveWorktreeAsync(repoPath, mergeBranch, deleteBranch: true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up merge worktree {Path}", mergePath);
        }
    }

    private async Task CleanupBranchesAsync(
        string repoPath, string workBranch, CancellationToken cancellationToken)
    {
        // Delete remote work branch (best-effort)
        try
        {
            await gitWorkspaceManager.DeleteRemoteBranchAsync(repoPath, workBranch, cancellationToken);
            logger.LogInformation("Deleted remote branch {Branch}", workBranch);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete remote branch {Branch} — may already be deleted", workBranch);
        }

        // Remove work branch worktree + local branch (best-effort)
        try
        {
            await gitWorkspaceManager.RemoveWorktreeAsync(repoPath, workBranch, deleteBranch: true, cancellationToken);
            logger.LogInformation("Removed worktree and local branch for {Branch}", workBranch);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up work branch worktree for {Branch}", workBranch);
        }
    }

    private async Task DeleteLocalBranchBestEffort(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        try
        {
            await GitWorkspaceManager.RunGitAsync(repoPath, ["branch", "-D", branchName], cancellationToken);
        }
        catch (GitOperationException) { /* branch doesn't exist — fine */ }
    }

    private async Task PostComment(
        string cardId, string detail, BoardCard card, string runMarker,
        CancellationToken cancellationToken)
    {
        var comment = $"{runMarker}\n\n**system in Merging ({agentIdentity.DisplayName}):**\n\n## Merge Complete\n\n{detail}";
        await boardClient.UpsertAgentCommentAsync(cardId, comment, runMarker, cancellationToken);
    }

    private async Task PostCommentBestEffort(
        string cardId, string detail, string runMarker,
        CancellationToken cancellationToken)
    {
        try
        {
            var comment = $"{runMarker}\n\n**system in Merging ({agentIdentity.DisplayName}):**\n\n## Merge Result\n\n{detail}";
            await boardClient.UpsertAgentCommentAsync(cardId, comment, runMarker, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post merge comment for card {CardId}", cardId);
        }
    }

    private async Task TransitionBestEffort(
        string cardId, WorkflowState state, string outcome,
        CancellationToken cancellationToken)
    {
        if (!state.Transitions.TryGetValue(outcome, out var target))
        {
            logger.LogWarning("No {Outcome} transition defined for state {State}", outcome, state.Name);
            return;
        }

        try
        {
            await TransitionExecutor.ExecuteAsync(cardId, target, boardClient, logger, cancellationToken,
                crossRefResolver: crossReferenceResolver, workflowConfig: workflowConfig);
            logger.LogInformation("Executed {Outcome} transition actions for card {CardId}", outcome, cardId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to execute transition actions for card {CardId} ({Outcome})", cardId, outcome);
        }
    }

    private static int ParseMaxRetries(Dictionary<string, string>? providerParams)
    {
        if (providerParams is not null
            && providerParams.TryGetValue("maxRetries", out var value)
            && int.TryParse(value, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return DefaultMaxRetries;
    }
}
