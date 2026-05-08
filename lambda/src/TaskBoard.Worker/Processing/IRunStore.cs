using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Persistent storage for agent run and step result data.
/// Always registered in DI: PgRunStore when DB is configured, NullRunStore otherwise.
/// </summary>
public interface IRunStore
{
    Task CreateRunAsync(RunRecord run, CancellationToken ct);
    Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct);
    Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct);
    Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct);
    Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
        string cardId, string? stateName, CancellationToken ct);
    Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(
        string cardId, string stateName, CancellationToken ct);

    /// <summary>Persists the story-point estimate for an agent run.</summary>
    Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct);

    /// <summary>Persists the container session startup time in milliseconds for an agent run.</summary>
    Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct);

    /// <summary>
    /// Records the evaluator's verdict for a single candidate row identified by
    /// (run_id, candidate_group_id, candidate_index). Updates <c>selected</c>,
    /// <c>quality_score</c>, and <c>evaluator_reasoning</c>. Other columns are
    /// untouched.
    /// </summary>
    Task UpdateCandidateEvaluationAsync(
        string runId,
        Guid candidateGroupId,
        int candidateIndex,
        bool selected,
        decimal? qualityScore,
        string? evaluatorReasoning,
        CancellationToken ct);

    /// <summary>
    /// Increments <c>agent_run.rate_limit_events</c> by 1. Called whenever
    /// AgentRunner catches a <c>RateLimitException</c> from any source
    /// (board API or agent CLI). Idempotent failure: a DB-side hiccup logs
    /// at warning level but never throws to the caller.
    /// </summary>
    Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct);

    /// <summary>
    /// Flags every <c>selected = true</c> candidate row in the given run as
    /// <c>winner_regressed = true</c>. Called from AgentRunner when the same
    /// run's gate check returns GATE_FAIL — the evaluator's verdict didn't
    /// survive downstream scrutiny.
    /// </summary>
    Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct);

    /// <summary>
    /// Returns the number of attempts on record for a step on a card+state.
    /// Predicate: <c>candidate_index IS NULL OR candidate_index = 0</c>, so a
    /// multi-candidate slot counts as a single attempt regardless of fan-out.
    /// Multi-slot fallback chains within one run count each slot try as its
    /// own attempt (slot 0 row + slot 1 row = 2 attempts). Used by the rerun
    /// redesign's comment poster to render <c>attempt:N</c> in the
    /// <c>aiboard-log</c> marker preamble.
    /// </summary>
    Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct);
}
