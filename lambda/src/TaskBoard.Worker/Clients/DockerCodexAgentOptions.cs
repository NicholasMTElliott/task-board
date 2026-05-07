namespace TaskBoard.Worker.Clients;

/// <summary>
/// Configuration for running the OpenAI Codex CLI inside a Docker container
/// (see <see cref="DockerCodexAgentExecutor"/>). Bound from the
/// <c>DockerAgents:Codex</c> section.
/// </summary>
/// <remarks>
/// The container is the security boundary. Codex defaults to <c>--yolo</c>
/// inside the sandbox because filesystem isolation is provided by Docker, not
/// by the Codex CLI's own sandbox layer. Operators who want belt-and-braces
/// can override <see cref="Yolo"/>/<see cref="Sandbox"/>/<see cref="FullAuto"/>
/// per workflow step via <c>providerParams</c> exactly like the host
/// <see cref="CodexCliLlmOptions"/>.
/// </remarks>
public sealed class DockerCodexAgentOptions : DockerAgentOptionsBase
{
    /// <summary>Configuration section: <c>DockerAgents:Codex</c>.</summary>
    public const string SectionName = "DockerAgents:Codex";

    public DockerCodexAgentOptions()
    {
        // Default image is the Codex sandbox built by scripts/build-codex-sandbox.ps1.
        ImageName = "aiboard-codex-sandbox:latest";
        // Short prefix keeps container names readable while preserving the
        // `aiboard-` marker that orphaned-container detection filters on.
        ContainerNamePrefix = "aiboard-cdx";
        // host network: Codex needs outbound HTTPS to api.openai.com /
        // chat.openai.com. Different from docker-opencode and
        // docker-claude-qwen which use the local-llm `llm-net` bridge.
    }

    /// <summary>
    /// Host path to the Codex CLI credential directory (e.g., <c>~/.codex/</c>,
    /// populated by <c>codex login</c>). When null,
    /// <see cref="DockerCodexMountBuilder"/> auto-detects by probing
    /// <c>~/.codex/</c>. Set explicitly if credentials are stored in a
    /// non-standard location.
    /// </summary>
    public string? CredentialPath { get; set; }

    /// <summary>
    /// Container path where Codex credentials are mounted. Defaults to
    /// <see cref="DockerCodexMountBuilder.DefaultCredentialMountPoint"/>
    /// (<c>/home/agent/.codex</c>), which matches the <c>agent</c> user in
    /// the aiboard-codex-sandbox image.
    /// </summary>
    public string? CredentialMountPoint { get; set; }

    /// <summary>
    /// Optional explicit override for the Codex CLI's <c>installation_id</c>
    /// (the UUID Codex sends as the <c>x-codex-installation-id</c> header on
    /// API calls). When null (default), <see cref="DockerCodexMountBuilder"/>
    /// reads or generates a stable UUID at
    /// <c>&lt;UserProfile&gt;/.aiboard/codex_installation_id</c>. Set to a
    /// fixed UUID to share one identity across a fleet of identical
    /// aiboard hosts. The value is passed to the sandbox via the
    /// <c>CODEX_INSTALLATION_ID</c> env var; the entrypoint script writes it
    /// into <c>~/.codex/installation_id</c> agent-owned, sidestepping the
    /// EROFS/EPERM problems of bind-mounting the host file.
    /// </summary>
    public string? InstallationId { get; set; }

    /// <summary>
    /// When true (default), passes <c>--yolo</c> to <c>codex exec</c>. The
    /// container itself provides filesystem isolation (per the
    /// <c>docker-codex</c> design rationale), so disabling Codex's own
    /// sandbox layer is the expected configuration. Override per-step via
    /// <c>providerParams.yolo = "false"</c> if you want belt-and-braces.
    /// </summary>
    public bool Yolo { get; set; } = true;

    /// <summary>
    /// When true and <see cref="Yolo"/> is false, passes <c>--full-auto</c>.
    /// Workspace-write sandbox + on-request approvals. Has no effect when
    /// <see cref="Yolo"/> is true (yolo supersedes).
    /// </summary>
    public bool FullAuto { get; set; }

    /// <summary>
    /// Sandbox policy passed to <c>--sandbox</c> (e.g. "read-only",
    /// "workspace-write", "danger-full-access") when <see cref="Yolo"/> is
    /// false. Null = use whatever <see cref="FullAuto"/> implies.
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// Additional stderr patterns that indicate rate limiting, merged with
    /// <see cref="CliRateLimitDetector.CodexDefaultPatterns"/>. Operators
    /// extend this when Codex surfaces provider-specific wording the
    /// built-in list doesn't catch.
    /// </summary>
    public List<string> RateLimitPatterns { get; set; } = new();

    /// <summary>
    /// Environment variables to remove from the Codex subprocess environment
    /// (mirrors host <see cref="CodexCliLlmOptions.EnvVarsToRemove"/>).
    /// Default: empty.
    /// </summary>
    public List<string> EnvVarsToRemove { get; set; } = new();

    // ── No MaxBudgetUsd field — by design ──────────────────────────────────────
    //
    // Unlike DockerClaudeAgentOptions.MaxBudgetUsd, this class deliberately
    // does NOT expose a budget cap. The Codex CLI has no `--max-budget-usd`
    // flag (or equivalent) — setting a value here would be silently ignored
    // by the binary. StartupConfigValidator already errors on
    // CodexCli:MaxBudgetUsd for the same reason; mirror the policy here so a
    // future author doesn't "fix" the perceived omission and re-introduce
    // the silent-ignore footgun. Cost-control for Codex is operator-level
    // (rate limits / billing alerts), not per-invocation.
}
