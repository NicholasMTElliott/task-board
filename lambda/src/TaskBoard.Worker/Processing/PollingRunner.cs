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
    ILogger<PollingRunner> logger,
    ShutdownCoordinator? shutdownCoordinator = null,
    DependencyGuard? dependencyGuard = null)
{
    // Adaptive polling constants
    private const int MaxConsecutiveIdleCycles = 10;   // idle stretch before hitting max delay
    private const double IdleBackoffMultiplier = 1.5;  // each idle cycle multiplies delay
    private const int MaxIdleDelaySeconds = 600;       // cap: 10 minutes
    private const int MaxErrorDelaySeconds = 300;      // cap: 5 minutes
    private const int RateLimitDelaySeconds = 120;     // base board-API rate-limit backoff: 2 minutes
    private const int AgentRateLimitDelaySeconds = 1800;    // base agent rate-limit backoff: 30 minutes
    private const int MaxAgentRateLimitDelaySeconds = 7200; // cap: 2 hours

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

        // Create a linked token that is cancelled by either the hard-cancel token or the shutdown idle
        // token. Used for idle/error delays so they are interrupted promptly when graceful shutdown
        // is requested. Agent execution still receives only cancellationToken (hard-cancel only).
        using var idleCts = shutdownCoordinator is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownCoordinator.IdleToken)
            : null;
        var idleToken = idleCts?.Token ?? cancellationToken;

        while (!cancellationToken.IsCancellationRequested && shutdownCoordinator?.IsShutdownRequested != true)
        {
            totalCycles++;
            try
            {
                var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken, workflowConfig.GetPollingExcludedColumnNames());
                var selectionResult = CardSelector.SelectAll(cards, workflowConfig, executorResolver.AvailableProviders);

                foreach (var skipped in selectionResult.SkippedDueToProviders)
                {
                    logger.LogWarning(
                        "Card {CardId} ({Title}) in '{State}' is not claimable — missing providers: {MissingProviders}",
                        skipped.Card.Id, skipped.Card.Title, skipped.StateName,
                        string.Join(", ", skipped.MissingProviders));
                }

                BoardCard? selected = null;
                if (dependencyGuard is null)
                {
                    selected = selectionResult.Eligible.FirstOrDefault();
                }
                else
                {
                    // Per-cycle blocker hydration cache: two enforced cards
                    // sharing a blocker only fetch the blocker's card body once.
                    var blockerCardCache = new Dictionary<string, BoardCard>(StringComparer.Ordinal);
                    foreach (var candidate in selectionResult.Eligible)
                    {
                        var candidateState = workflowConfig.ResolveState(candidate);
                        if (candidateState is null)
                            continue;

                        var dependencyResult = await dependencyGuard.CheckAsync(
                            candidate, candidateState, "polling", cancellationToken, blockerCardCache);
                        if (!dependencyResult.IsBlocked)
                        {
                            selected = candidate;
                            break;
                        }

                        // Debug, not Info: same card stays blocked across many
                        // cycles until the blocker resolves. The persistent
                        // record lives in card_dependency_wait + the upserted
                        // blocked-comment on the card.
                        logger.LogDebug(
                            "Skipping blocked card {CardId} ({Title}); unresolved blockers: {Blockers}",
                            candidate.Id, candidate.Title,
                            string.Join(", ", dependencyResult.UnresolvedBlockers.Select(b => $"#{b.CardId}")));
                    }
                }

                if (selected is null)
                {
                    consecutiveIdleCycles++;
                    var idleDelay = ComputeIdleDelay(pollInterval, consecutiveIdleCycles);
                    logger.LogInformation(
                        "No eligible cards found (cycle {Cycle}, idle streak {IdleStreak}), waiting {DelaySec}s",
                        totalCycles, consecutiveIdleCycles, idleDelay.TotalSeconds);
                    await Task.Delay(idleDelay, idleToken);
                }
                else
                {
                    consecutiveIdleCycles = 0;

                    logger.LogInformation(
                        "Selected card {CardId} ({Title}) in {ColumnId} (cycle {Cycle})",
                        selected.Id, selected.Title, selected.ColumnId, totalCycles);

                    var selectedState = workflowConfig.ResolveState(selected);
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
                    await Task.Delay(pollInterval, idleToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitException ex)
            {
                consecutiveErrors++;
                var rateLimitDelay = ex.Source == RateLimitSource.AgentCli
                    ? ComputeAgentRateLimitDelay(consecutiveErrors)
                    : ComputeRateLimitDelay(consecutiveErrors);
                logger.LogWarning(ex,
                    "Rate limit hit — source={Source} (cycle {Cycle}, consecutive errors: {ConsecutiveErrors}). " +
                    "Backing off for {DelaySec}s",
                    ex.Source, totalCycles, consecutiveErrors, rateLimitDelay.TotalSeconds);

                try { await Task.Delay(rateLimitDelay, idleToken); }
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

                try { await Task.Delay(errorDelay, idleToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        var stopReason = shutdownCoordinator?.IsShutdownRequested == true ? "graceful shutdown" : "cancellation";
        logger.LogInformation("Polling stopped after {TotalCycles} cycles ({StopReason})", totalCycles, stopReason);
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
    /// Board-API rate limit backoff: 2 minutes * 2^(errors-1), capped at 5 minutes.
    /// More aggressive than generic errors since rate limits need time to reset.
    /// </summary>
    private static TimeSpan ComputeRateLimitDelay(int consecutiveErrors)
    {
        var seconds = RateLimitDelaySeconds * Math.Pow(2, Math.Min(consecutiveErrors - 1, 3));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxErrorDelaySeconds));
    }

    /// <summary>
    /// Agent (Claude) rate limit backoff: 30 minutes * 2^(errors-1), capped at 2 hours.
    /// Subscription rate-limit windows are 5-hour/7-day/monthly, so short retries are wasteful.
    /// </summary>
    internal static TimeSpan ComputeAgentRateLimitDelay(int consecutiveErrors)
    {
        var seconds = AgentRateLimitDelaySeconds * Math.Pow(2, Math.Min(consecutiveErrors - 1, 3));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxAgentRateLimitDelaySeconds));
    }
}
