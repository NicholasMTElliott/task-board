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

    /// <summary>
    /// Aggregated metrics by (role, provider) from <c>v_provider_role_metrics</c>.
    /// Empty when no candidate-group data exists yet.
    /// </summary>
    Task<IReadOnlyList<ProviderRoleMetric>> GetProviderRoleMetricsAsync(
        DateTimeOffset? since, CancellationToken ct);

    /// <summary>
    /// Pairwise head-to-head records between providers within the same role.
    /// Returns one row per (role, providerA, providerB) where providerA &lt; providerB
    /// alphabetically, so callers don't need to dedupe symmetric pairs.
    /// </summary>
    Task<IReadOnlyList<HeadToHeadRecord>> GetCandidateHeadToHeadAsync(
        DateTimeOffset? since, CancellationToken ct);
}
