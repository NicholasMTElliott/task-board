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
    string? EvaluatorReasoning = null,
    /// <summary>
    /// Position of this row's slot within the step's slot list (0..N-1) for
    /// multi-slot fallback chains. Null for single-slot steps (the common case)
    /// and for non-slot rows; legacy rows persisted before the slot model was
    /// added are also null.
    /// </summary>
    int? SlotIndex = null,
    /// <summary>
    /// Cost in USD of this step's LLM invocation. Populated for Claude (host
    /// and Docker variants) from <c>total_cost_usd</c> on the result event.
    /// Null for Codex (ChatGPT subscription has no per-call cost), local-LLM
    /// providers, and the stub executor.
    /// </summary>
    decimal? CostUsd = null,
    /// <summary>
    /// Input tokens consumed by this step. Populated by all real providers when
    /// the CLI emits <c>usage.input_tokens</c>; null when not reported.
    /// Local-LLM rows include this so operators can reason about
    /// llama.cpp prefix-cache pressure on Qwen-target steps.
    /// </summary>
    long? InputTokens = null,
    /// <summary>Output tokens emitted by this step. Same semantics as <see cref="InputTokens"/>.</summary>
    long? OutputTokens = null,
    /// <summary>Cache-read tokens (Claude only; null for other providers).</summary>
    long? CacheReadTokens = null,
    /// <summary>Cache-creation tokens (Claude only; null for other providers).</summary>
    long? CacheCreationTokens = null,
    /// <summary>
    /// True when the re-run preamble was injected for this step AND the agent
    /// returned COMPLETE — the fast path actually short-circuited a full re-run.
    /// Null when the fast path didn't apply or wasn't checked.
    /// </summary>
    bool? FastPathHit = null,
    /// <summary>
    /// True only on DockerOpenCode rows where the no-think structurer fallback
    /// recovered the Agent Contract JSON from the agent's narrative. Null otherwise.
    /// </summary>
    bool? StructurerFallbackUsed = null,
    /// <summary>
    /// Character length of the prompt sent to the evaluator. Set on evaluator
    /// step rows (<c>step_name LIKE '%:evaluator'</c>) so trends toward
    /// truncation territory are visible before the evaluator silently degrades.
    /// </summary>
    int? EvaluatorPromptChars = null,
    /// <summary>
    /// True on candidate winners (Selected = true) when the same run's gate
    /// check returned GATE_FAIL — the evaluator's verdict didn't survive
    /// downstream scrutiny. Null when GATE_FAIL hasn't fired yet or this row
    /// is not a candidate winner.
    /// </summary>
    bool? WinnerRegressed = null);
