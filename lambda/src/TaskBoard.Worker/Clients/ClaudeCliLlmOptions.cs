namespace TaskBoard.Worker.Clients;

public sealed class ClaudeCliLlmOptions
{
    public const string SectionName = "ClaudeCli";

    public string ExecutablePath { get; init; } = "claude";

    public int MaxTurns { get; init; } = 5;

    public decimal MaxBudgetUsd { get; init; } = 2.00m;

    public int TimeoutSeconds { get; init; } = 300;
}
