using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Models;

/// <summary>
/// Represents the result of a single agent step execution.
/// Covers mandatory steps, gate checks (step_name = "gate_check"),
/// optional steps (step_name = "optional:{name}"), and candidate-group
/// executions (CandidateGroupId / CandidateIndex set; one of N parallel
/// runs of the same step against different providers).
/// </summary>
public sealed record StepResultRecord(
    string RunId,
    string CardId,
    string StateName,
    string StepName,
    int StepIndex,
    string Role,
    string Model,
    AgentOutcome Outcome,
    string? Summary,
    string? Detail,
    string? ReferenceContent,
    string? ConversationLog,
    IReadOnlyList<AgentQuestion>? Questions,
    IReadOnlyList<string>? RequestedSteps,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    /// <summary>
    /// Time in milliseconds taken to execute this step via docker exec inside a reused container.
    /// Null when the step ran via direct docker run (no session) or via a non-Docker executor.
    /// </summary>
    int? SessionExecMs = null,
    /// <summary>
    /// Provider key the executor ran under (e.g. <c>claude-cli</c>,
    /// <c>docker-claude-cli</c>, <c>docker-opencode</c>, <c>codex</c>, <c>stub</c>).
    /// Always populated for new rows so per-(role, provider) metrics can attribute
    /// runs without joining back to the workflow config.
    /// </summary>
    string Provider = "claude-cli",
    /// <summary>
    /// Identifier shared by all candidates in a parallel evaluation group, plus
    /// the evaluator step itself. Null for traditional single-agent steps.
    /// </summary>
    Guid? CandidateGroupId = null,
    /// <summary>
    /// Position of this candidate within its group (0..N-1). Null for non-candidate
    /// steps and for the evaluator step that follows them.
    /// </summary>
    int? CandidateIndex = null,
    /// <summary>
    /// True when the evaluator picked this candidate as the winner; false when it
    /// ran but lost; null when no evaluator has run yet or this is not a candidate row.
    /// </summary>
    bool? Selected = null,
    /// <summary>
    /// Evaluator-assigned quality score on a 0–10 scale. Null until the evaluator
    /// has run and scored this candidate.
    /// </summary>
    decimal? QualityScore = null,
    /// <summary>
    /// Free-text reasoning from the evaluator about why this candidate won or lost.
    /// </summary>
    string? EvaluatorReasoning = null);
