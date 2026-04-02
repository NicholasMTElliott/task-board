namespace TaskBoard.Worker.Clients;

/// <summary>
/// Simple retry utility with exponential backoff and jitter for transient errors.
/// </summary>
internal static class RetryHelper
{
    /// <summary>
    /// Retries <paramref name="action"/> up to <paramref name="maxRetries"/> times
    /// when <paramref name="isTransient"/> returns true for the caught exception.
    /// Uses exponential backoff with jitter (base 1s, cap 8s).
    /// </summary>
    internal static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action,
        Func<Exception, bool> isTransient,
        int maxRetries,
        ILogger? logger,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxRetries && isTransient(ex))
            {
                var baseDelay = Math.Min(Math.Pow(2, attempt), 8); // 1, 2, 4, 8 seconds
                var jitter = Random.Shared.NextDouble() * 0.5;     // 0–0.5s jitter
                var delay = TimeSpan.FromSeconds(baseDelay + jitter);

                logger?.LogWarning(ex,
                    "Transient error on {Operation} (attempt {Attempt}/{MaxRetries}), retrying in {Delay:F1}s",
                    operationName, attempt + 1, maxRetries, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>Non-generic overload for void-returning operations.</summary>
    internal static async Task ExecuteWithRetryAsync(
        Func<Task> action,
        Func<Exception, bool> isTransient,
        int maxRetries,
        ILogger? logger,
        string operationName,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            async () => { await action(); return true; },
            isTransient,
            maxRetries,
            logger,
            operationName,
            cancellationToken);
    }
}
