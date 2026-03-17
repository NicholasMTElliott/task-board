namespace TaskBoard.Worker.Clients;

public sealed class OpenAiLlmOptions
{
    public const string SectionName = "OpenAi";

    public string ApiKey { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.openai.com";
}
