namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared Docker-runtime configuration for any Docker-wrapped CLI agent executor.
/// CLI-specific subclasses (e.g., <see cref="DockerClaudeAgentOptions"/>) add provider
/// settings such as credential paths and prompt mount points.
/// </summary>
public abstract class DockerAgentOptionsBase
{
    /// <summary>
    /// When true (default), a Docker container is created before the first step and reused
    /// for all subsequent steps within the same agent run (via docker exec).
    /// Set to false to revert to per-step docker run behaviour.
    /// </summary>
    public bool ReuseContainer { get; set; } = true;

    /// <summary>
    /// Docker image to run. Defaults are set by the provider-specific subclass.
    /// </summary>
    public string ImageName { get; set; } = "";

    /// <summary>Prefix for auto-generated container names: {prefix}-{tenantHash}-{cardId}-{suffix}.</summary>
    public string ContainerNamePrefix { get; set; } = "aiboard-run";

    /// <summary>
    /// Hard wall-clock cap, in seconds, before the container is killed regardless of progress.
    /// Default 7200 (2h) — designed as the outer bound; <see cref="InactivityTimeoutSeconds"/>
    /// is the actual "stuck" detector for normal runs. Operators who need a tighter cap
    /// (e.g. polling environments where a stuck card should free up the queue sooner) can
    /// override this at the section level.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 7200;

    /// <summary>
    /// Inactivity threshold, in seconds, after which the process is killed if no
    /// stdout/stderr output has been observed. Default 1200 (20m) — generous enough
    /// to cover cold prefix-cache loads and long single-turn inferences while still
    /// catching genuine hangs (e.g. an agent stuck in an explore-subagent loop with
    /// no output, or a CLI that lost its event stream). Set to <c>null</c> to disable
    /// inactivity checking entirely (the hard <see cref="TimeoutSeconds"/> cap still
    /// applies).
    /// </summary>
    public int? InactivityTimeoutSeconds { get; set; } = 1200;

    /// <summary>User to run as inside the container. Empty = use image default.</summary>
    public string ContainerUser { get; set; } = "";

    /// <summary>Optional container memory limit (e.g., "4g"). Null = no limit.</summary>
    public string? MemoryLimit { get; set; }

    /// <summary>Optional CPU limit (e.g., "2.0"). Null = no limit.</summary>
    public string? CpuLimit { get; set; }

    /// <summary>
    /// Container network mode. Default "host" allows web access.
    /// Use "none" for full network isolation.
    /// </summary>
    public string NetworkMode { get; set; } = "host";

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
