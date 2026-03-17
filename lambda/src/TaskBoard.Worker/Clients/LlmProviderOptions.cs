namespace TaskBoard.Worker.Clients;

public sealed class LlmProviderOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; init; } = "stub";
}
