using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class ShutdownCoordinatorTests
{
    [Fact]
    public void IsShutdownRequested_DefaultsToFalse()
    {
        using var coordinator = new ShutdownCoordinator();
        Assert.False(coordinator.IsShutdownRequested);
    }

    [Fact]
    public void RequestShutdown_FirstCall_SetsFlag()
    {
        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();
        Assert.True(coordinator.IsShutdownRequested);
    }

    [Fact]
    public void RequestShutdown_FirstCall_ReturnsTrue()
    {
        using var coordinator = new ShutdownCoordinator();
        var result = coordinator.RequestShutdown();
        Assert.True(result);
    }

    [Fact]
    public void RequestShutdown_SecondCall_ReturnsFalse()
    {
        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();
        var result = coordinator.RequestShutdown();
        Assert.False(result);
    }

    [Fact]
    public void RequestShutdown_FlagRemainsSet_OnSubsequentCalls()
    {
        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();
        coordinator.RequestShutdown(); // second call
        Assert.True(coordinator.IsShutdownRequested);
    }

    [Fact]
    public void IdleToken_NotCancelled_BeforeShutdown()
    {
        using var coordinator = new ShutdownCoordinator();
        Assert.False(coordinator.IdleToken.IsCancellationRequested);
    }

    [Fact]
    public void IdleToken_CancelledAfterRequestShutdown()
    {
        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();
        Assert.True(coordinator.IdleToken.IsCancellationRequested);
    }

    [Fact]
    public void RequestShutdown_IsThreadSafe_OnlyFirstReturnsTrue()
    {
        using var coordinator = new ShutdownCoordinator();

        var results = new bool[10];
        var threads = Enumerable.Range(0, 10)
            .Select(i => new Thread(() => results[i] = coordinator.RequestShutdown()))
            .ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        // Exactly one thread should have received true (first press)
        Assert.Equal(1, results.Count(r => r));
        // All others received false
        Assert.Equal(9, results.Count(r => !r));
        // Flag is set
        Assert.True(coordinator.IsShutdownRequested);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var coordinator = new ShutdownCoordinator();
        var exception = Record.Exception(() => coordinator.Dispose());
        Assert.Null(exception);
    }
}
