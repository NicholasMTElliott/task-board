namespace TaskBoard.Worker.Clients;

public sealed class AnthropicLlmOptions
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.anthropic.com";
}
