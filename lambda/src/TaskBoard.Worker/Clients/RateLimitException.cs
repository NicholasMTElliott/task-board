namespace TaskBoard.Worker.Clients;

/// <summary>
/// Identifies the subsystem that triggered the rate limit.
/// </summary>
public enum RateLimitSource
{
    /// <summary>Board provider API (GitHub, Trello).</summary>
    BoardApi,
    /// <summary>Agent CLI subprocess (Claude, Codex).</summary>
    AgentCli
}

/// <summary>
/// Thrown when an API call or CLI invocation is rejected due to rate limiting.
/// <see cref="Processing.PollingRunner"/> uses this to trigger extended backoff.
/// </summary>
public sealed class RateLimitException(
    string message,
    RateLimitSource source = RateLimitSource.BoardApi,
    DateTimeOffset? resetAt = null) : InvalidOperationException(message)
{
    /// <summary>Which subsystem hit the rate limit.</summary>
    public RateLimitSource Source { get; } = source;

    /// <summary>
    /// When the rate-limit window resets, if known. Null when the source
    /// does not report reset time (e.g., Claude CLI).
    /// </summary>
    public DateTimeOffset? ResetAt { get; } = resetAt;
}
