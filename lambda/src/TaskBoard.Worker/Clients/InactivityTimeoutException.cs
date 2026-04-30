namespace TaskBoard.Worker.Clients;

/// <summary>
/// Thrown when a subprocess produced no stdout/stderr output for longer than the
/// configured inactivity threshold. Subclass of <see cref="TimeoutException"/> so
/// existing classification (<c>AgentRunner.ClassifyFailure</c> → TIMEOUT) and
/// retry logic (<c>CandidateExecutor</c>'s timeout-retry path) handle it
/// transparently — the distinct type only exists so logs and metrics can
/// distinguish "stuck mid-run" from "hit the wall-clock cap".
/// </summary>
public sealed class InactivityTimeoutException : TimeoutException
{
    /// <summary>The configured inactivity threshold in seconds.</summary>
    public int InactivitySeconds { get; }

    public InactivityTimeoutException(string message, int inactivitySeconds)
        : base(message)
    {
        InactivitySeconds = inactivitySeconds;
    }
}
