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

    /// <summary>The agent executor threw an unhandled exception.</summary>
    AGENT_ERROR,

    /// <summary>Reserved for infrastructure failures (e.g., network, compute). Not yet populated.</summary>
    INFRASTRUCTURE,

    /// <summary>Reserved for timeout failures. Not yet populated.</summary>
    TIMEOUT,
}
