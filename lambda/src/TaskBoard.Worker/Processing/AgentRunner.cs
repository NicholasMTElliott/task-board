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
                AgentOutcome.COMPLETE => $"Agent: {state.Name} complete for {targetCard.Title}",
                AgentOutcome.NEEDS_INFO => $"Agent: questions for {targetCard.Title}",
                AgentOutcome.ERROR => $"Agent: error processing {targetCard.Title}",
                _ => $"Agent: {agentResult.Outcome} for {targetCard.Title}"
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

            // 8. Post-process: update card on board, add comment, move to next state
            await PostProcessAsync(targetCard, state, agentResult, worktreePath, cancellationToken);

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

    private async Task PostProcessAsync(
        BoardCard originalCard,
        WorkflowState state,
        AgentResult agentResult,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        // 8a. Read back the task file to detect agent changes to card content
        var taskFilePath = TaskFileManager.GetTaskFilePath(worktreePath, originalCard.Id);
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

        // 8b. Format and post comment
        var comment = FormatComment(agentResult);
        await boardClient.UpsertAgentCommentAsync(originalCard.Id, comment, cancellationToken);
        logger.LogInformation("Posted agent comment for {CardId}", originalCard.Id);

        // 8c. Move card to next state per transitions
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

    internal static string FormatComment(AgentResult result)
    {
        return result.Outcome switch
        {
            AgentOutcome.COMPLETE => $"## Agent Complete\n\n{result.Detail ?? "Task completed successfully."}",
            AgentOutcome.NEEDS_INFO => FormatQuestionsComment(result),
            AgentOutcome.ERROR => $"## Agent Error\n\n{result.Detail ?? "An error occurred."}",
            _ => $"## Agent: {result.Outcome}\n\n{result.Detail ?? ""}"
        };
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
