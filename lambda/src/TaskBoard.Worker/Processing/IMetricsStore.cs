namespace TaskBoard.Worker.Processing;

/// <summary>
/// Read-only analytical queries over agent_run and step_result data.
/// Always registered in DI: PgMetricsStore when DB is configured, NullMetricsStore otherwise.
/// </summary>
public interface IMetricsStore
{
    /// <summary>Aggregate run counts and success rate within an optional time window.</summary>
    Task<RunSummary> GetRunSummaryAsync(DateTimeOffset? since, CancellationToken ct);

    /// <summary>Cycle time, working time, and waiting time per card.</summary>
    Task<IReadOnlyList<CardMetrics>> GetCardMetricsAsync(string? cardId, DateTimeOffset? since, CancellationToken ct);

    /// <summary>Top N slowest step executions by average duration.</summary>
    Task<IReadOnlyList<StepDuration>> GetTopStepDurationsAsync(int top, DateTimeOffset? since, CancellationToken ct);

    /// <summary>Cards and states that were re-entered (rework indicator).</summary>
    Task<IReadOnlyList<CardRework>> GetReworkCardsAsync(DateTimeOffset? since, CancellationToken ct);

    /// <summary>Cycle time per story point with rolling window averages and deviations.</summary>
    Task<CycleTimePerPointSummary> GetCycleTimePerPointAsync(DateTimeOffset? since, CancellationToken ct);
}
