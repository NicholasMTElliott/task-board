namespace TaskBoard.Worker.Processing;

internal sealed class NoOpSleepInhibitor : ISystemSleepInhibitor
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
