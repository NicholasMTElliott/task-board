using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public interface ILlmClient
{
    Task<AgentResponse> GetCompletionAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken);
}
