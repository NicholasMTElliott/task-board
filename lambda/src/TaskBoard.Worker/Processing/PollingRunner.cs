using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class PollingRunner(
    ITaskBoardClient boardClient,
    AgentRunner agentRunner,
    MergeRunner mergeRunner,
    WorkflowConfig workflowConfig,
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
                var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken);
                var selected = CardSelector.SelectNext(cards, workflowConfig);

                if (selected is null)
                {
                    logger.LogDebug("No eligible cards found (cycle {Cycle}), waiting", totalCycles);
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
