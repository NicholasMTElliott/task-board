namespace TaskBoard.Worker.Clients;

public sealed class CodexCliLlmOptions
{
    public const string SectionName = "CodexCli";

    public string ExecutablePath { get; set; } = "codex";

    public int TimeoutSeconds { get; init; } = 900;

    /// <summary>
    /// When true, passes --full-auto to codex exec (workspace-write sandbox, on-request approvals).
    /// When false, codex runs in its default read-only mode.
    /// </summary>
    public bool FullAuto { get; init; } = true;

    /// <summary>
    /// Sandbox policy passed to codex --sandbox.
    /// Valid values: "read-only", "workspace-write", "danger-full-access".
    /// Null means use the codex default (or the --full-auto preset).
    /// </summary>
    public string? Sandbox { get; init; }
}
