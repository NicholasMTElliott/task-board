namespace TaskBoard.Worker.Processing;

/// <summary>
/// No-op IMetricsStore used when no database connection is configured.
/// All methods return empty or zero results.
/// MetricsRunner detects this type and shows an informative message.
/// </summary>
public sealed class NullMetricsStore : IMetricsStore
{
    public static readonly NullMetricsStore Instance = new();

    private NullMetricsStore() { }

    public Task<RunSummary> GetRunSummaryAsync(DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult(new RunSummary(0, 0, 0, 0, 0, 0));

    public Task<IReadOnlyList<CardMetrics>> GetCardMetricsAsync(string? cardId, DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CardMetrics>>([]);

    public Task<IReadOnlyList<StepDuration>> GetTopStepDurationsAsync(int top, DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<StepDuration>>([]);

    public Task<IReadOnlyList<CardRework>> GetReworkCardsAsync(DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CardRework>>([]);

    public Task<CycleTimePerPointSummary> GetCycleTimePerPointAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var empty = new CycleTimePerPoint(null, null);
        return Task.FromResult(new CycleTimePerPointSummary(empty, empty, empty, empty));
    }

    public Task<IReadOnlyList<ProviderRoleMetric>> GetProviderRoleMetricsAsync(
        DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderRoleMetric>>([]);

    public Task<IReadOnlyList<HeadToHeadRecord>> GetCandidateHeadToHeadAsync(
        DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HeadToHeadRecord>>([]);

    public Task<IReadOnlyList<EvaluatorReliabilityRecord>> GetEvaluatorReliabilityAsync(
        DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<EvaluatorReliabilityRecord>>([]);

    public Task<IReadOnlyList<CacheHitRateRecord>> GetCacheHitRateAsync(
        DateTimeOffset? since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CacheHitRateRecord>>([]);
}
