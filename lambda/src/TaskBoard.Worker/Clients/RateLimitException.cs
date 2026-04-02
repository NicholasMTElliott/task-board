namespace TaskBoard.Worker.Clients;

/// <summary>
/// Thrown when a board provider API call is rejected due to rate limiting.
/// <see cref="Processing.PollingRunner"/> uses this to trigger extended backoff.
/// </summary>
public sealed class RateLimitException(string message) : InvalidOperationException(message);
