namespace TaskBoard.Worker.Clients;

public sealed class ClaudeCliLlmOptions
{
    public const string SectionName = "ClaudeCli";

    public string ExecutablePath { get; set; } = "claude";

    public int MaxTurns { get; set; } = 5;

    public decimal MaxBudgetUsd { get; set; } = 10.00m;

    /// <summary>
    /// Hard wall-clock cap, in seconds, before the Claude CLI subprocess is killed.
    /// Default 7200 (2h) — outer bound; <see cref="InactivityTimeoutSeconds"/> is the
    /// "stuck" detector for normal runs.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 7200;

    /// <summary>
    /// Inactivity threshold, in seconds — kill the subprocess if no stdout/stderr
    /// has been observed for this long. Default 1200 (20m). Set to <c>null</c> to disable.
    /// </summary>
    public int? InactivityTimeoutSeconds { get; set; } = 1200;
}
