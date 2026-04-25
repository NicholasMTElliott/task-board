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
/// evaluated yet for this combination.
/// </summary>
public sealed record ProviderRoleMetric(
    string Role,
    string Provider,
    int TotalRuns,
    int Wins,
    int RunsWithDecision,
    double? WinRatePercent,
    double? AvgQualityScore,
    double? AvgDurationSeconds);

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
