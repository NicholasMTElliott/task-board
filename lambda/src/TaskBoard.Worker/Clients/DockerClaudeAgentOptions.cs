namespace TaskBoard.Worker.Clients;

/// <summary>
/// Configuration for running the Claude CLI inside a Docker container
/// (see <see cref="DockerClaudeAgentExecutor"/>).
/// </summary>
/// <remarks>
/// Bound from the <c>DockerAgents:Claude</c> configuration section. The legacy
/// <c>Docker</c> section is still honoured as a fallback; a deprecation warning
/// is emitted when that section is the only one populated.
/// </remarks>
public sealed class DockerClaudeAgentOptions : DockerAgentOptionsBase
{
    /// <summary>New-style configuration section.</summary>
    public const string SectionName = "DockerAgents:Claude";

    /// <summary>Legacy configuration section. Deprecated; retained for backward compatibility.</summary>
    public const string LegacySectionName = "Docker";

    public DockerClaudeAgentOptions()
    {
        // Default image is the aiboard-agent-sandbox (node:22-slim + Claude CLI).
        ImageName = "aiboard-agent-sandbox:latest";
    }

    /// <summary>
    /// Mount point inside the container for host-side system prompt files (read-only).
    /// The parent directory of <c>SystemPromptFilePath</c> is mounted here.
    /// </summary>
    public string PromptMountPoint { get; set; } = "/mnt/aiboard/prompts";

    /// <summary>Maximum Claude CLI budget per container invocation.</summary>
    public decimal MaxBudgetUsd { get; set; } = 10.00m;

    /// <summary>
    /// Host path to the Claude CLI credential directory (e.g., <c>~/.claude/</c>).
    /// When null, <see cref="DockerClaudeMountBuilder"/> auto-detects by probing <c>~/.claude/</c>.
    /// Set explicitly if credentials are stored in a non-standard location.
    /// </summary>
    public string? CredentialPath { get; set; }

    /// <summary>
    /// Container path where Claude credentials are mounted.
    /// Defaults to <see cref="DockerClaudeMountBuilder.DefaultCredentialMountPoint"/> (<c>/home/agent/.claude</c>),
    /// which matches the <c>agent</c> user in the aiboard-agent-sandbox image.
    /// </summary>
    public string? CredentialMountPoint { get; set; }
}
