using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed partial class AgentRunner(
    ITrelloClient trelloClient,
    IAgentExecutor agentExecutor,
    TaskFileManager taskFileManager,
    GitWorkspaceManager gitWorkspaceManager,
    WorkflowConfig workflowConfig,
    ILogger<AgentRunner> logger)
{
    private static readonly Regex PlaceholderRegex = PlaceholderPattern();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    public async Task<AgentRunResult> ExecuteAsync(
        string cardId, string boardId, string workspacePath, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting agent run for card {CardId} in workspace {Workspace}",
            cardId, workspacePath);

        // 1. Fetch all board cards
        var cards = await trelloClient.GetBoardCardsAsync(boardId, cancellationToken);
        logger.LogInformation("Fetched {Count} cards from board {BoardId}", cards.Count, boardId);

        // 2. Find the target card and determine its workflow state
        var targetCard = cards.FirstOrDefault(c => c.Id == cardId);
        if (targetCard is null)
        {
            logger.LogError("Card {CardId} not found on board {BoardId}", cardId, boardId);
            return new AgentRunResult(AgentOutcome.ERROR, "Card not found on board");
        }

        if (!workflowConfig.States.TryGetValue(targetCard.IdList, out var state))
        {
            logger.LogError("Card {CardId} is in list {ListId} which is not in workflow config", cardId, targetCard.IdList);
            return new AgentRunResult(AgentOutcome.ERROR, "Card list not in workflow config");
        }

        if (state.Role is null || !workflowConfig.Roles.TryGetValue(state.Role, out var role))
        {
            logger.LogError("State {StateName} has no valid role", state.Name);
            return new AgentRunResult(AgentOutcome.ERROR, $"No valid role for state {state.Name}");
        }

        var branchName = $"aiboard/{cardId}";

        try
        {
            // 3. Create git worktree (isolated working directory for the agent)
            var worktreePath = await gitWorkspaceManager.CreateWorktreeAsync(
                workspacePath, branchName, cancellationToken);

            // 4. Write all task files into the worktree
            await taskFileManager.WriteAllTaskFilesAsync(worktreePath, cards, workflowConfig, cancellationToken);

            // 5. Resolve prompt placeholders
            var resolvedPrompt = ResolvePromptPlaceholders(state.TaskPrompt ?? "", targetCard);

            // 6. Execute agent — WorkspacePath is the worktree, so Claude CLI runs there
            var context = new AgentExecutionContext(
                TargetCardId: cardId,
                WorkspacePath: worktreePath,
                TaskPrompt: resolvedPrompt,
                SystemPrompt: role.SystemPrompt,
                Model: role.Model);

            var agentResult = await agentExecutor.ExecuteAsync(context, cancellationToken);

            // 7. Commit based on outcome (inside the worktree)
            var commitMessage = agentResult.Outcome switch
            {
                AgentOutcome.COMPLETE => $"Agent: {state.Name} complete for {targetCard.Name}",
                AgentOutcome.NEEDS_INFO => $"Agent: questions for {targetCard.Name}",
                AgentOutcome.ERROR => $"Agent: error processing {targetCard.Name}",
                _ => $"Agent: {agentResult.Outcome} for {targetCard.Name}"
            };

            // CommitAsync is a no-op if nothing is staged (e.g., only .aiboard/ files changed)
            await gitWorkspaceManager.CommitAsync(worktreePath, commitMessage, cancellationToken);

            if (agentResult.Outcome == AgentOutcome.COMPLETE)
            {
                try
                {
                    await gitWorkspaceManager.PushAsync(worktreePath, branchName, cancellationToken);
                }
                catch (GitOperationException ex)
                {
                    logger.LogWarning(ex, "Failed to push branch {Branch} — no remote configured?", branchName);
                }
            }

            logger.LogInformation("Agent run complete for card {CardId}: outcome={Outcome}", cardId, agentResult.Outcome);
            return new AgentRunResult(agentResult.Outcome, null, agentResult.Questions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent run failed for card {CardId}", cardId);

            // Try to write error to task file in the worktree
            try
            {
                var worktreePath = GitWorkspaceManager.GetWorktreePath(workspacePath, branchName);
                var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, cardId);
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

            return new AgentRunResult(AgentOutcome.ERROR, ex.Message);
        }
    }

    internal static string ResolvePromptPlaceholders(string template, TrelloCard card)
    {
        return PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return key switch
            {
                "TaskName" => card.Name,
                "TaskId" => card.Id,
                // UserStoryName and UserStoryId are TBD for MVP — leave as-is
                _ => match.Value
            };
        });
    }
}

public sealed record AgentRunResult(
    AgentOutcome Outcome,
    string? ErrorDetail,
    IReadOnlyList<AgentQuestion>? Questions = null);
