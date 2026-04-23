namespace TaskBoard.Worker.Clients;

/// <summary>
/// Represents a long-lived container session that can execute multiple agent invocations
/// without the overhead of creating a new container for each step.
/// Implementations hold a running container open across steps within a single agent run.
/// </summary>
public interface IAgentExecutorSession : IAsyncDisposable
{
    /// <summary>Unique identifier for this session (typically the container name).</summary>
    string SessionId { get; }

    /// <summary>The provider key this session belongs to (e.g., "docker").</summary>
    string ProviderKey { get; }

    /// <summary>True if the underlying container is still running and can accept execution requests.</summary>
    bool IsAlive { get; }

    /// <summary>
    /// Executes an agent invocation inside the existing container (e.g., via docker exec).
    /// Callers should check <see cref="IsAlive"/> before calling and fall back to
    /// direct execution if the session is no longer alive.
    /// </summary>
    Task<AgentResult> ExecuteInSessionAsync(AgentExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Marker interface for agent executors that support long-lived container sessions.
/// Executors implementing this interface can create sessions that span multiple steps
/// within a single agent run, avoiding per-step container startup overhead.
/// Non-session executors (ClaudeAgentExecutor, CodexAgentExecutor, StubAgentExecutor) do not
/// implement this interface and are unaffected by session logic.
/// </summary>
public interface ISessionableAgentExecutor : IAgentExecutor
{
    /// <summary>The provider key for this executor (e.g., "docker"). Used for provider-mismatch detection.</summary>
    string ProviderKey { get; }

    /// <summary>
    /// Attempts to create a long-lived container session for the given run.
    /// Returns null if session creation is not possible (e.g., image not found, Docker not available).
    /// Throws on unrecoverable errors that should abort the run.
    /// </summary>
    Task<IAgentExecutorSession?> TryCreateSessionAsync(SessionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Request record containing all metadata needed to create an agent execution session.
/// Passed to <see cref="ISessionableAgentExecutor.TryCreateSessionAsync"/>.
/// </summary>
public sealed record SessionRequest(
    /// <summary>Board card ID (used for container naming: aiboard-{CardId}).</summary>
    string CardId,
    /// <summary>Agent run UUID for log correlation.</summary>
    string RunId,
    /// <summary>Container name to use (aiboard-{CardId} for mutual exclusion per card).</summary>
    string ContainerName,
    /// <summary>Docker image name to start the container from.</summary>
    string ImageName,
    /// <summary>
    /// Volume mounts to attach to the container. Populated by a subclass of
    /// <see cref="DockerMountBuilderBase"/> (e.g. <see cref="DockerClaudeMountBuilder"/>)
    /// with workspace, base .git, .git override, and credential mounts.
    /// </summary>
    IReadOnlyList<DockerMount>? Mounts = null,
    /// <summary>Environment variables to inject into the container (e.g., GIT_OPTIONAL_LOCKS=0).</summary>
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
