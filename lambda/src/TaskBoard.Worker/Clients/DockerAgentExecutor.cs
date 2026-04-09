using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Executes agents inside a Docker container.
/// Full implementation tracked in #63 (DockerAgentExecutor core implementation).
/// </summary>
public sealed class DockerAgentExecutor(
    IOptions<DockerAgentOptions> options,
    ILogger<DockerAgentExecutor> logger) : IAgentExecutor
{
    public Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(
            "DockerAgentExecutor is not yet implemented. Full implementation tracked in #63.");
    }
}
