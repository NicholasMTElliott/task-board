namespace TaskBoard.Worker.Clients;

public sealed class LlmApiException(string provider, string message, Exception? inner = null)
    : Exception($"LLM error ({provider}): {message}", inner)
{
    public string Provider { get; } = provider;
}
