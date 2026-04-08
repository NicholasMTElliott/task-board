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
}
