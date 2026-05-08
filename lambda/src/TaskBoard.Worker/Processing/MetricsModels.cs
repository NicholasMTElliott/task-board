namespace TaskBoard.Worker.Processing;

/// <summary>Summary of all agent runs within an optional time window.</summary>
public sealed record RunSummary(
    int TotalRuns,
    int CompleteRuns,
    int NeedsInfoRuns,
    int ErrorRuns,
    int RateLimitedRuns,
    double SuccessRatePercent);

/// <summary>Cycle, working, and waiting time for a single card.</summary>
public sealed record CardMetrics(
    string CardId,
    DateTimeOffset FirstRunStart,
    DateTimeOffset LastRunEnd,
    double CycleTimeSeconds,
    double WorkingTimeSeconds,
    double WaitingTimeSeconds,
    int TotalRuns,
    double? Estimate);

/// <summary>Duration of a single completed step execution.</summary>
public sealed record StepDuration(
    string CardId,
    string StateName,
    string StepName,
    string Role,
    string Model,
    double DurationSeconds);

/// <summary>A card+state combination that was re-entered (rework).</summary>
public sealed record CardRework(
    string CardId,
    string StateName,
    int EntryCount,
    int ReworkCount);

/// <summary>Average and standard deviation of cycle time per story point.</summary>
public sealed record CycleTimePerPoint(
    double? AvgCycleTimePerPointSeconds,
    double? StdDevCycleTimePerPointSeconds);

/// <summary>Cycle time per story point summary with rolling window breakdowns.</summary>
public sealed record CycleTimePerPointSummary(
    CycleTimePerPoint Overall,
    CycleTimePerPoint Last24h,
    CycleTimePerPoint Last7d,
    CycleTimePerPoint Last30d);

/// <summary>
/// Per-(role, provider) aggregate from <c>v_provider_role_metrics</c>. Drives
/// "is provider X worth keeping for role Y" decisions. <c>WinRatePercent</c>
/// and <c>AvgQualityScore</c> can be null when no candidate group has been
/// evaluated yet for this combination. Cost / token / structurer aggregates
/// are null when the underlying provider doesn't report them (Codex omits
/// cost; local-LLM omits cost and may omit tokens depending on the proxy).
/// </summary>
public sealed record ProviderRoleMetric(
    string Role,
    string Provider,
    int TotalRuns,
    int Wins,
    int RunsWithDecision,
    double? WinRatePercent,
    double? AvgQualityScore,
    double? AvgDurationSeconds,
    decimal? TotalCostUsd,
    decimal? AvgCostUsd,
    long? TotalInputTokens,
    long? TotalOutputTokens,
    double? AvgInputTokens,
    double? AvgOutputTokens,
    long? TotalCacheReadTokens,
    long? TotalCacheCreationTokens,
    int StructurerFallbackCount,
    double? StructurerFallbackRatePercent);

/// <summary>
/// Per-(evaluator-role, evaluator-provider) regression rate from
/// <c>v_evaluator_reliability</c>. <c>RegressionRatePercent</c> is the share
/// of evaluator verdicts whose chosen winner was later flagged
/// <c>winner_regressed</c> by the same run's gate check.
/// </summary>
public sealed record EvaluatorReliabilityRecord(
    string EvaluatorRole,
    string EvaluatorProvider,
    int TotalVerdicts,
    int RegressedCount,
    double? RegressionRatePercent);

/// <summary>
/// Per-(state, step, role, provider) cache-hit rate from the rerun-redesign
/// deterministic-skip cache (Problem 1). High hit rate means re-runs are
/// finding cached results to reuse; low rate on a step that re-runs often
/// signals operator-edit drift, marker churn, or input-bundle changes.
/// </summary>
public sealed record CacheHitRateRecord(
    string StateName,
    string StepName,
    string Role,
    string Provider,
    int TotalInvocations,
    int CacheHits,
    double? HitRatePercent);

/// <summary>
/// Pairwise head-to-head record: how many times <c>ProviderA</c> beat
/// <c>ProviderB</c> (and vice versa) within candidate groups where both
/// participated for the same role. <c>Ties</c> covers groups where neither
/// was selected (e.g., evaluator NEEDS_INFO outcome left selected = NULL).
/// </summary>
public sealed record HeadToHeadRecord(
    string Role,
    string ProviderA,
    string ProviderB,
    int AWins,
    int BWins,
    int Ties);
