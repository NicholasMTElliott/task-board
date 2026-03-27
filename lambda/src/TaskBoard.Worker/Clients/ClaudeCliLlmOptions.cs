namespace TaskBoard.Worker.Clients;

public sealed class ClaudeCliLlmOptions
{
    public const string SectionName = "ClaudeCli";

    public string ExecutablePath { get; set; } = "claude";

    public int MaxTurns { get; init; } = 5;

    public decimal MaxBudgetUsd { get; init; } = 10.00m;

    public int TimeoutSeconds { get; init; } = 900;
}
