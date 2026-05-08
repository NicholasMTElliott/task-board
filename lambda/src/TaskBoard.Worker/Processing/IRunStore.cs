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

    /// <summary>
    /// Returns the most recent <c>outcome = 'COMPLETE'</c> step_result for
    /// the (tenant, card, state, step) cache key, plus its <c>input_hash</c>,
    /// <c>section_output_hash</c>, and <c>id</c> for cache-decision matching.
    /// Returns null when no matching COMPLETE row exists.
    /// <para>
    /// Predicate excludes per-candidate rows
    /// (<c>candidate_index IS NULL OR candidate_index = 0</c>) so the cache
    /// decision uses the slot-level identity, not individual losing
    /// candidates whose work was discarded.
    /// </para>
    /// </summary>
    Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(
        string cardId, string stateName, string stepName, CancellationToken ct);

    /// <summary>
    /// Returns the earliest non-null <c>state_entry_canonical_sha</c> across
    /// every <c>agent_run</c> row for <c>(tenant, card, state)</c> — the SHA
    /// captured the FIRST time this card entered this state. Subsequent runs
    /// in the same state copy it forward so the gate-check diff base
    /// (Problem 3) shows cumulative committed work across re-runs, not just
    /// this run's possibly-empty diff. Returns null when no prior run has
    /// captured a SHA yet (the caller should capture one for the current
    /// run via <see cref="SetStateEntryShaAsync"/>).
    /// </summary>
    Task<string?> GetEarliestStateEntryShaAsync(
        string cardId, string stateName, CancellationToken ct);

    /// <summary>
    /// Sets <c>agent_run.state_entry_canonical_sha</c> for the current run.
    /// Idempotent: if the column is already set on this row, the update is
    /// a no-op (so re-entry into the same state mid-run doesn't clobber the
    /// stored SHA).
    /// </summary>
    Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct);
}

/// <summary>
/// Compact projection of a step_result row used by Problem 1's cache decision.
/// Carries only the fields the cache check needs (id for back-reference,
/// hashes for matching, run_id + completed_at for the cache_hit comment's
/// "since run X" callback). Avoids materialising the full StepResultRecord
/// when only these fields are read.
/// </summary>
public sealed record CacheCandidateRecord(
    Guid Id,
    string RunId,
    DateTimeOffset CompletedAtUtc,
    string? InputHash,
    string? SectionOutputHash,
    string? OutputSummary,
    string? Detail);
