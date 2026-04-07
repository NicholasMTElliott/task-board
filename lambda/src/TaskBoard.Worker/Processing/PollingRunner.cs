using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class PollingRunner(
    ITaskBoardClient boardClient,
    AgentRunner agentRunner,
    MergeRunner mergeRunner,
    CompletionRunner completionRunner,
    WorkflowConfig workflowConfig,
    IAgentExecutorResolver executorResolver,
    ILogger<PollingRunner> logger)
{
    // Adaptive polling constants
    private const int MaxConsecutiveIdleCycles = 10;   // idle stretch before hitting max delay
    private const double IdleBackoffMultiplier = 1.5;  // each idle cycle multiplies delay
    private const int MaxIdleDelaySeconds = 600;       // cap: 10 minutes
    private const int MaxErrorDelaySeconds = 300;      // cap: 5 minutes
    private const int RateLimitDelaySeconds = 120;     // base rate-limit backoff: 2 minutes

    public async Task RunAsync(
        string boardId,
        string workspacePath,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Polling started, base interval={IntervalSeconds}s (adaptive backoff enabled)",
            pollInterval.TotalSeconds);

        var totalCycles = 0;
        var consecutiveErrors = 0;
        var consecutiveIdleCycles = 0;

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
                    consecutiveIdleCycles++;
                    var idleDelay = ComputeIdleDelay(pollInterval, consecutiveIdleCycles);
                    logger.LogDebug(
                        "No eligible cards found (cycle {Cycle}, idle streak {IdleStreak}), waiting {DelaySec}s",
                        totalCycles, consecutiveIdleCycles, idleDelay.TotalSeconds);
                    await Task.Delay(idleDelay, cancellationToken);
                }
                else
                {
                    consecutiveIdleCycles = 0;

                    logger.LogInformation(
                        "Selected card {CardId} ({Title}) in {ColumnId} (cycle {Cycle})",
                        selected.Id, selected.Title, selected.ColumnId, totalCycles);

                    var selectedState = workflowConfig.States.GetValueOrDefault(selected.ColumnId);
                    var result = selectedState?.GateType switch
                    {
                        GateTypes.SystemMerge => await mergeRunner.ExecuteAsync(
                            selected.Id, boardId, workspacePath, cancellationToken),
                        GateTypes.ChildrenComplete => await completionRunner.ExecuteAsync(
                            selected.Id, boardId, workspacePath, cancellationToken),
                        _ => await agentRunner.ExecuteAsync(
                            selected.Id, boardId, workspacePath, cancellationToken),
                    };

                    logger.LogInformation(
                        "Card {CardId} completed: {Outcome}",
                        selected.Id, result.Outcome);

                    if (result.Outcome == AgentOutcome.ERROR)
                        consecutiveErrors++;
                    else
                        consecutiveErrors = 0;

                    // After completing work, poll again quickly to pick up any queued cards
                    await Task.Delay(pollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitException ex)
            {
                consecutiveErrors++;
                var rateLimitDelay = ComputeRateLimitDelay(consecutiveErrors);
                logger.LogWarning(ex,
                    "Rate limit hit (cycle {Cycle}, consecutive errors: {ConsecutiveErrors}). " +
                    "Backing off for {DelaySec}s",
                    totalCycles, consecutiveErrors, rateLimitDelay.TotalSeconds);

                try { await Task.Delay(rateLimitDelay, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                var errorDelay = ComputeErrorDelay(pollInterval, consecutiveErrors);
                logger.LogError(ex,
                    "Poll cycle {Cycle} failed (consecutive errors: {ConsecutiveErrors}). " +
                    "Waiting {DelaySec}s before retry",
                    totalCycles, consecutiveErrors, errorDelay.TotalSeconds);

                try { await Task.Delay(errorDelay, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        logger.LogInformation("Polling stopped after {TotalCycles} cycles", totalCycles);
    }

    /// <summary>
    /// Idle delay grows with consecutive idle cycles: base * 1.5^idleStreak, capped at 10 minutes.
    /// Resets to base interval as soon as a card is found.
    /// </summary>
    private static TimeSpan ComputeIdleDelay(TimeSpan baseInterval, int consecutiveIdleCycles)
    {
        var capped = Math.Min(consecutiveIdleCycles, MaxConsecutiveIdleCycles);
        var seconds = baseInterval.TotalSeconds * Math.Pow(IdleBackoffMultiplier, capped);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxIdleDelaySeconds));
    }

    /// <summary>
    /// Error delay uses exponential backoff: base * 2^(errors-1), capped at 5 minutes.
    /// </summary>
    private static TimeSpan ComputeErrorDelay(TimeSpan baseInterval, int consecutiveErrors)
    {
        var seconds = baseInterval.TotalSeconds * Math.Pow(2, Math.Min(consecutiveErrors - 1, 5));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxErrorDelaySeconds));
    }

    /// <summary>
    /// Rate limit backoff: 2 minutes * 2^(errors-1), capped at 5 minutes.
    /// More aggressive than generic errors since rate limits need time to reset.
    /// </summary>
    private static TimeSpan ComputeRateLimitDelay(int consecutiveErrors)
    {
        var seconds = RateLimitDelaySeconds * Math.Pow(2, Math.Min(consecutiveErrors - 1, 3));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxErrorDelaySeconds));
    }
}
