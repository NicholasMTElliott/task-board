namespace TaskBoard.Worker.Clients;

public interface IAgentExecutor
{
    Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken);
}

public sealed record AgentExecutionContext(
    string TargetCardId,
    string TargetCardTitle,
    string WorkspacePath,
    string TaskPrompt,
    string SystemPromptFilePath,
    string? Model,
    IReadOnlyDictionary<string, string>? ProviderParams = null,
    string? CommentsFilePath = null,
    string? SchemaOverride = null);

public sealed record AgentResult(
    AgentOutcome Outcome,
    string? Detail = null,
    IReadOnlyList<AgentQuestion>? Questions = null,
    string? ConversationLog = null,
    IReadOnlyList<string>? RequestedSteps = null,
    double? Estimate = null,
    // Evaluator-specific fields, populated by AgentOutputParser when the
    // structured_output JSON carries them (per AgentSchemas.EvaluatorOutcomeSchema).
    // Non-evaluator runs leave these null. Both layers exist because Claude CLI's
    // --json-schema treats required fields as a soft hint — when the model fills
    // them, capture and use them; when it doesn't, ParseEvaluatorVerdict falls
    // back to extracting from the detail markdown (fenced JSON or prose patterns).
    int? WinnerIndex = null,
    IReadOnlyList<EvaluatorScore>? Scores = null,
    // Usage and timing data captured by the executor, surfaced through to step_result.
    // All nullable because not every executor / model / wire format reports them
    // (Codex omits cost; OpenCode against local llama.cpp omits everything except
    // possibly token counts via the proxy; the stub executor reports nothing).
    UsageInfo? Usage = null,
    // True only when the DockerOpenCode no-think structurer fallback recovered the
    // result from prose narrative on a -think model run. Null otherwise.
    bool? StructurerFallbackUsed = null,
    // Rerun redesign: section_update is the agent's structured directive for
    // updating the card description's managed step section. The orchestrator
    // (DescriptionWriter) consumes it mechanically — strategy decides leave /
    // replace / append; content is the new section markdown; open_questions
    // and resolved_decisions render into HTML-marker-wrapped subsections.
    // Null when the agent didn't supply one (legacy roles, gates, evaluators,
    // or operator-managed flows).
    SectionUpdate? Section = null,
    // Raw JSON text of the agent's <c>section_update</c> object, captured
    // at parse time via JsonElement.GetRawText. Persisted to V24's
    // <c>step_result.section_update_json</c> for replay / debugging.
    // Null when no section_update was supplied or the field was malformed.
    string? SectionUpdateJson = null);

/// <summary>
/// Token / cost usage extracted from the CLI's final result event.
/// Cost is in USD; Codex (ChatGPT subscription) and local-LLM runs leave it null.
/// Cache fields are Claude-specific and null for other providers.
/// </summary>
public sealed record UsageInfo(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CacheReadTokens = null,
    long? CacheCreationTokens = null,
    decimal? CostUsd = null);

public sealed record AgentQuestion(
    string Question,
    IReadOnlyList<string>? Recommendations = null);

/// <summary>
/// Structured directive from the agent for updating its managed step section
/// in the card description. Mirrors the wire-format <c>section_update</c>
/// object on the agent contract schema.
/// </summary>
/// <param name="Strategy">
/// One of <c>leave</c> / <c>replace</c> / <c>append_with_revision_notes</c>.
/// On the very first run for a step (no prior section), <c>leave</c> is
/// coerced by the orchestrator to a placeholder section so future hashing
/// has a stable input.
/// </param>
/// <param name="Content">New section markdown. Required when Strategy != <c>leave</c>.</param>
/// <param name="OpenQuestions">Bullets rendered into the section's <c>### Open Questions</c> subsection (HTML-marker-wrapped).</param>
/// <param name="ResolvedDecisions">Bullets rendered into the section's <c>### Resolved Decisions</c> subsection (HTML-marker-wrapped).</param>
public sealed record SectionUpdate(
    SectionUpdateStrategy Strategy,
    string? Content = null,
    IReadOnlyList<string>? OpenQuestions = null,
    IReadOnlyList<string>? ResolvedDecisions = null);

public enum SectionUpdateStrategy
{
    /// <summary>Orchestrator does not modify the existing step section.</summary>
    Leave,
    /// <summary>Orchestrator replaces section content wholesale.</summary>
    Replace,
    /// <summary>Same as Replace, but Content includes lessons-learned / durable-decision notes.</summary>
    AppendWithRevisionNotes,
}

/// <summary>
/// One per-candidate score from an evaluator's structured response. Mirrors
/// the <c>scores[]</c> array in the evaluator JSON schema. Score is nullable
/// to accommodate parser-side sanitisation (out-of-[0,10] values dropped to
/// null; reasoning is preserved either way).
/// </summary>
public sealed record EvaluatorScore(
    int Index,
    decimal? Score,
    string? Reasoning);

public enum AgentOutcome
{
    COMPLETE,
    NEEDS_INFO,
    ERROR
}

/// <summary>
/// Resolves the appropriate <see cref="IAgentExecutor"/> for a given provider key.
/// </summary>
public interface IAgentExecutorResolver
{
    IAgentExecutor Resolve(string providerKey);

    /// <summary>
    /// The set of provider keys for which executors are registered.
    /// Used by CardSelector to determine which states can be executed.
    /// </summary>
    IReadOnlySet<string> AvailableProviders { get; }
}
