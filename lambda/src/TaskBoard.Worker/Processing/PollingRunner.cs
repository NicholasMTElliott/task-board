using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class PollingRunner(
    ITaskBoardClient boardClient,
    AgentRunner agentRunner,
    MergeRunner mergeRunner,
    WorkflowConfig workflowConfig,
    IAgentExecutorResolver executorResolver,
    ILogger<PollingRunner> logger)
{
    public async Task RunAsync(
        string boardId,
        string workspacePath,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Polling started, interval={IntervalSeconds}s", pollInterval.TotalSeconds);
        var totalCycles = 0;
        var consecutiveErrors = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            totalCycles++;
            try
            {
                var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken, workflowConfig.GetTerminalStateNames());
                var selectionResult = CardSelector.SelectNext(cards, workflowConfig, executorResolver.AvailableProviders);

                foreach (var skipped in selectionResult.SkippedDueToProviders)
                {
                    logger.LogWarning(
                        "Card {CardId} ({Title}) in '{State}' is not claimable — missing providers: {MissingProviders}",
                        skipped.Card.Id, skipped.Card.Title, skipped.StateName,
                        string.Join(", ", skipped.MissingProviders));
                }

                var selected = selectionResult.Selected;

                if (selected is null)
                {
                    logger.LogInformation("No eligible cards found (cycle {Cycle}), waiting", totalCycles);
                }
                else
                {
                    logger.LogInformation(
                        "Selected card {CardId} ({Title}) in {ColumnId} (cycle {Cycle})",
                        selected.Id, selected.Title, selected.ColumnId, totalCycles);

                    var selectedState = workflowConfig.States.GetValueOrDefault(selected.ColumnId);
                    var result = string.Equals(selectedState?.GateType, "system_merge", StringComparison.OrdinalIgnoreCase)
                        ? await mergeRunner.ExecuteAsync(selected.Id, boardId, workspacePath, cancellationToken)
                        : await agentRunner.ExecuteAsync(selected.Id, boardId, workspacePath, cancellationToken);

                    logger.LogInformation(
                        "Card {CardId} completed: {Outcome}",
                        selected.Id, result.Outcome);

                    if (result.Outcome == AgentOutcome.ERROR)
                        consecutiveErrors++;
                    else
                        consecutiveErrors = 0;
                }

                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                logger.LogError(ex,
                    "Poll cycle {Cycle} failed (consecutive errors: {ConsecutiveErrors})",
                    totalCycles, consecutiveErrors);

                try
                {
                    await Task.Delay(pollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Polling stopped after {TotalCycles} cycles", totalCycles);
    }
}
