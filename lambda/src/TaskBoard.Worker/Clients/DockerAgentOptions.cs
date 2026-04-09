namespace TaskBoard.Worker.Clients;

/// <summary>
/// Configuration options for Docker-based agent execution.
/// </summary>
public sealed class DockerAgentOptions
{
    public const string SectionName = "DockerAgent";

    /// <summary>
    /// When true (default), a Docker container is created before the first step and reused
    /// for all subsequent steps within the same agent run (via docker exec).
    /// Set to false to revert to per-step docker run behaviour.
    /// </summary>
    public bool ReuseContainer { get; set; } = true;

    /// <summary>
    /// Docker image name to use when creating agent execution containers.
    /// Must include Claude CLI and required tooling.
    /// </summary>
    public string ImageName { get; set; } = "aiboard-agent:latest";

    /// <summary>Prefix for auto-generated container names: {prefix}-{cardId}-{suffix}.</summary>
    public string ContainerNamePrefix { get; set; } = "aiboard-run";

    /// <summary>
    /// Mount point inside the container for host-side system prompt files (read-only).
    /// The parent directory of <c>SystemPromptFilePath</c> is mounted here.
    /// </summary>
    public string PromptMountPoint { get; set; } = "/mnt/aiboard/prompts";

    /// <summary>Maximum Claude CLI budget per container invocation.</summary>
    public decimal MaxBudgetUsd { get; set; } = 10.00m;

    /// <summary>Timeout in seconds before the container is killed.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Additional volume mounts passed to <c>docker run -v</c>.
    /// Key: a human-readable label for logging; value: mount details.
    /// </summary>
    public Dictionary<string, DockerMount> AdditionalMounts { get; set; } = [];
}

/// <summary>A volume mount entry for <c>docker run -v {host}:{container}[:ro]</c>.</summary>
public sealed class DockerMount
{
    /// <summary>Absolute path on the host.</summary>
    public string HostPath { get; set; } = "";

    /// <summary>Absolute path inside the container.</summary>
    public string ContainerPath { get; set; } = "";

    /// <summary>Whether to mount read-only.</summary>
    public bool ReadOnly { get; set; }
}
