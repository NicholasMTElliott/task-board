namespace TaskBoard.Worker.Processing;

/// <summary>
/// Prevents the operating system from entering sleep mode.
/// Acquire via <see cref="SystemSleepInhibitor.CreateAsync"/>.
/// Dispose to release the inhibition.
/// </summary>
public interface ISystemSleepInhibitor : IAsyncDisposable
{
}
