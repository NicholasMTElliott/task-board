namespace TaskBoard.Worker.Clients;

public sealed class DockerAgentOptions
{
    public const string SectionName = "Docker";

    /// <summary>Docker image to use for the agent container.</summary>
    public string ImageName { get; set; } = "aiboard-agent-sandbox:latest";

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
    /// Host path to Claude CLI credentials directory.
    /// Auto-detected from ~/.claude if not set.
    /// </summary>
    public string CredentialPath { get; set; } = "";

    /// <summary>Extra volume mounts in "host:container" format.</summary>
    public List<string> AdditionalMounts { get; set; } = [];
}
