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
