namespace TaskBoard.Worker.Clients;

public sealed class ClaudeCliLlmOptions
{
    public const string SectionName = "ClaudeCli";

    public string ExecutablePath { get; set; } = "claude";

    public int MaxTurns { get; set; } = 5;

    public decimal MaxBudgetUsd { get; set; } = 10.00m;

    public int TimeoutSeconds { get; set; } = 900;
}
