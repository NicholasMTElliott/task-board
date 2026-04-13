namespace TaskBoard.Worker.Processing;

/// <summary>
/// Coordinates graceful two-phase shutdown for long-running runner modes (polling, queue).
///
/// First Ctrl+C  → <see cref="RequestShutdown"/> sets <see cref="IsShutdownRequested"/> and cancels
///                 <see cref="IdleToken"/> so idle delays are interrupted immediately.
///                 In-progress work is allowed to finish before the runner exits.
///
/// Second Ctrl+C → the caller cancels the main <see cref="CancellationToken"/> for hard abort
///                 (existing behavior, unchanged).
/// </summary>
public sealed class ShutdownCoordinator : IDisposable
{
    private int _shutdownRequested; // 0 = no, 1 = yes (Interlocked)
    private readonly CancellationTokenSource _idleCts = new();

    /// <summary>True after the first shutdown request (first Ctrl+C).</summary>
    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>
    /// Token cancelled when shutdown is first requested. Pass this (via a linked token)
    /// to idle <see cref="Task.Delay"/> calls so they are interrupted promptly on graceful shutdown.
    /// Do NOT pass to agent execution calls — those should only be cancelled by the hard-cancel token.
    /// </summary>
    public CancellationToken IdleToken => _idleCts.Token;

    /// <summary>
    /// Signals shutdown. Returns <c>true</c> if this was the first call (first Ctrl+C),
    /// <c>false</c> if shutdown was already requested (second+ press).
    /// Thread-safe via <see cref="Interlocked.CompareExchange"/>.
    /// </summary>
    public bool RequestShutdown()
    {
        var wasFirst = Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) == 0;
        if (wasFirst && !_idleCts.IsCancellationRequested)
            _idleCts.Cancel();
        return wasFirst;
    }

    public void Dispose() => _idleCts.Dispose();
}
