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
            // 3. Create git branch
            await gitWorkspaceManager.CreateBranchAsync(workspacePath, branchName, cancellationToken);

            // 4. Write all task files
            await taskFileManager.WriteAllTaskFilesAsync(workspacePath, cards, workflowConfig, cancellationToken);

            // 5. Resolve prompt placeholders
            var resolvedPrompt = ResolvePromptPlaceholders(state.TaskPrompt ?? "", targetCard);

            // 6. Execute agent
            var context = new AgentExecutionContext(
                TargetCardId: cardId,
                WorkspacePath: workspacePath,
                TaskPrompt: resolvedPrompt,
                SystemPrompt: role.SystemPrompt,
                Model: role.Model);

            var outcome = await agentExecutor.ExecuteAsync(context, cancellationToken);

            // 7. Commit based on outcome
            var commitMessage = outcome switch
            {
                AgentOutcome.SUCCESS => $"Agent: {state.Name} complete for {targetCard.Name}",
                AgentOutcome.QUESTIONS => $"Agent: questions for {targetCard.Name}",
                AgentOutcome.ERROR => $"Agent: error processing {targetCard.Name}",
                _ => $"Agent: {outcome} for {targetCard.Name}"
            };

            try
            {
                await gitWorkspaceManager.CommitAsync(workspacePath, commitMessage, cancellationToken);

                if (outcome == AgentOutcome.SUCCESS)
                {
                    try
                    {
                        await gitWorkspaceManager.PushAsync(workspacePath, branchName, cancellationToken);
                    }
                    catch (GitOperationException ex)
                    {
                        logger.LogWarning(ex, "Failed to push branch {Branch} — no remote configured?", branchName);
                    }
                }
            }
            catch (GitOperationException ex) when (ex.Message.Contains("nothing to commit"))
            {
                logger.LogWarning("No changes to commit for card {CardId}", cardId);
            }

            logger.LogInformation("Agent run complete for card {CardId}: outcome={Outcome}", cardId, outcome);
            return new AgentRunResult(outcome, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent run failed for card {CardId}", cardId);

            // Try to write error to task file
            try
            {
                var taskFilePath = TaskFileManager.GetTaskFilePath(workspacePath, cardId);
                if (File.Exists(taskFilePath))
                {
                    var existing = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
                    await File.WriteAllTextAsync(taskFilePath,
                        existing + $"\n\n## Error\n\n{ex.Message}", cancellationToken);
                }

                await gitWorkspaceManager.CommitAsync(workspacePath,
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

public sealed record AgentRunResult(AgentOutcome Outcome, string? ErrorDetail);
