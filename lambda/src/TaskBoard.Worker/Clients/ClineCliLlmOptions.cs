namespace TaskBoard.Worker.Clients;

public sealed class ClineCliLlmOptions
{
    public const string SectionName = "ClineCli";

    public string ExecutablePath { get; init; } = "cline";

    public int TimeoutSeconds { get; init; } = 300;
}
