namespace TaskBoard.Worker.Clients;

public sealed class CodexCliLlmOptions
{
    public const string SectionName = "CodexCli";

    public string ExecutablePath { get; set; } = "codex";

    /// <summary>
    /// Hard wall-clock cap, in seconds, before the Codex CLI subprocess is killed.
    /// Default 7200 (2h) — outer bound; <see cref="InactivityTimeoutSeconds"/> is the
    /// "stuck" detector for normal runs.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 7200;

    /// <summary>
    /// Inactivity threshold, in seconds — kill the subprocess if no stdout/stderr
    /// has been observed for this long. Default 1200 (20m). Set to <c>null</c> to disable.
    /// </summary>
    public int? InactivityTimeoutSeconds { get; set; } = 1200;

    /// <summary>
    /// When true, passes --full-auto to codex exec (workspace-write sandbox, on-request approvals).
    /// When false, codex runs in its default read-only mode.
    /// </summary>
    public bool FullAuto { get; set; } = true;

    /// <summary>
    /// Sandbox policy passed to codex --sandbox.
    /// Valid values: "read-only", "workspace-write", "danger-full-access".
    /// Null means use the codex default (or the --full-auto preset).
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// When true, passes --yolo (--dangerously-bypass-approvals-and-sandbox) to codex exec.
    /// Bypasses all approval prompts and sandboxing. Only use in isolated/trusted environments.
    /// </summary>
    public bool Yolo { get; set; } = false;

    /// <summary>
    /// Additional stderr patterns that indicate rate limiting, merged with the defaults
    /// in <see cref="CliRateLimitDetector.CodexDefaultPatterns"/>. Operators can set this
    /// when the Codex CLI surfaces provider-specific rate-limit wording not covered by
    /// the built-in list. Matches are case-insensitive substring matches.
    /// </summary>
    public List<string> RateLimitPatterns { get; set; } = new();

    /// <summary>
    /// Environment variables to remove from the Codex subprocess environment.
    /// Mirrors Claude's <c>CLAUDECODE</c> strip for defence against self-invocation guards
    /// or leaked state from a parent agent process. Default: empty.
    /// </summary>
    public List<string> EnvVarsToRemove { get; set; } = new();
}
