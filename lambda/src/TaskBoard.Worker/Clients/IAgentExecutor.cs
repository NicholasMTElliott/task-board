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
    string Model,
    IReadOnlyDictionary<string, string>? ProviderParams = null,
    string? CommentsFilePath = null);

public sealed record AgentResult(
    AgentOutcome Outcome,
    string? Detail = null,
    IReadOnlyList<AgentQuestion>? Questions = null,
    string? ConversationLog = null,
    IReadOnlyList<string>? RequestedSteps = null);

public sealed record AgentQuestion(
    string Question,
    IReadOnlyList<string>? Recommendations = null);

public enum AgentOutcome
{
    COMPLETE,
    NEEDS_INFO,
    ERROR
}
