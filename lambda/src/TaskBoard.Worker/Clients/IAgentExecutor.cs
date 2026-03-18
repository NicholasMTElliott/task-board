namespace TaskBoard.Worker.Clients;

public interface IAgentExecutor
{
    Task<AgentOutcome> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken);
}

public sealed record AgentExecutionContext(
    string TargetCardId,
    string WorkspacePath,
    string TaskPrompt,
    string SystemPrompt,
    string Model);

public enum AgentOutcome
{
    SUCCESS,
    QUESTIONS,
    ERROR
}
