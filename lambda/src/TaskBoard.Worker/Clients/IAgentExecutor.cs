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
    IReadOnlyList<EvaluatorScore>? Scores = null);

public sealed record AgentQuestion(
    string Question,
    IReadOnlyList<string>? Recommendations = null);

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
