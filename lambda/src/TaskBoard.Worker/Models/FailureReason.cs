namespace TaskBoard.Worker.Models;

/// <summary>
/// Classifies why an agent run failed when outcome is ERROR.
/// Only populated on error outcomes; null for COMPLETE and NEEDS_INFO.
/// Persisted as TEXT to the agent_run.failure_reason column.
/// </summary>
public enum FailureReason
{
    /// <summary>The agent CLI or board API hit a rate limit.</summary>
    RATE_LIMIT,

    /// <summary>The agent executor threw an unhandled exception (agent logic failure).</summary>
    AGENT_ERROR,

    /// <summary>
    /// CLI runtime or container infrastructure failure (e.g., binary missing,
    /// permission denied, Docker daemon error). Mapped from
    /// <see cref="Clients.CliInfrastructureException"/>.
    /// </summary>
    INFRASTRUCTURE,

    /// <summary>Wall-clock timeout. Mapped from <see cref="TimeoutException"/>.</summary>
    TIMEOUT,
}
