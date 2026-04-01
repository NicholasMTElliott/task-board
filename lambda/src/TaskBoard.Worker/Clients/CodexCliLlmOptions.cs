namespace TaskBoard.Worker.Clients;

public sealed class CodexCliLlmOptions
{
    public const string SectionName = "CodexCli";

    public string ExecutablePath { get; set; } = "codex";

    public int TimeoutSeconds { get; init; } = 900;

    /// <summary>
    /// Approval policy passed to codex --approval-policy.
    /// "auto-edit" approves all file edits without prompting.
    /// "full-auto" also approves command execution.
    /// </summary>
    public string ApprovalPolicy { get; init; } = "auto-edit";
}
