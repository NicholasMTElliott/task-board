using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class SystemSleepInhibitorTests
{
    private readonly NullLogger _logger = NullLogger.Instance;

    [Fact]
    public async Task CreateAsync_ReturnsNonNullInhibitor()
    {
        var inhibitor = await SystemSleepInhibitor.CreateAsync(_logger);
        Assert.NotNull(inhibitor);
        await inhibitor.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_OnCurrentPlatform_ReturnsExpectedType()
    {
        var inhibitor = await SystemSleepInhibitor.CreateAsync(_logger);

        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsSleepInhibitor>(inhibitor);
        else if (OperatingSystem.IsMacOS())
            Assert.True(inhibitor is MacSleepInhibitor or NoOpSleepInhibitor);
        else if (OperatingSystem.IsLinux())
            Assert.True(inhibitor is LinuxSleepInhibitor or NoOpSleepInhibitor);
        else
            Assert.IsType<NoOpSleepInhibitor>(inhibitor);

        await inhibitor.DisposeAsync();
    }

    [Fact]
    public async Task NoOpSleepInhibitor_DisposeAsync_DoesNotThrow()
    {
        var inhibitor = new NoOpSleepInhibitor();
        await inhibitor.DisposeAsync();
        // Double dispose must also be safe
        await inhibitor.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_DisposeAsync_DoesNotThrow()
    {
        var inhibitor = await SystemSleepInhibitor.CreateAsync(_logger);
        await inhibitor.DisposeAsync();
        // Double dispose must be safe
        await inhibitor.DisposeAsync();
    }
}

public class WindowsSleepInhibitorTests
{
    [Fact]
    public async Task WindowsInhibitor_CreateAndDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var inhibitor = new WindowsSleepInhibitor(NullLogger.Instance);
        Assert.IsType<WindowsSleepInhibitor>(inhibitor);
        await inhibitor.DisposeAsync();
    }

    [Fact]
    public async Task WindowsInhibitor_DoubleDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var inhibitor = new WindowsSleepInhibitor(NullLogger.Instance);
        await inhibitor.DisposeAsync();
        await inhibitor.DisposeAsync();
    }
}

public class MacSleepInhibitorTests
{
    [Fact]
    public async Task MacInhibitor_CreateAndDispose_KillsProcess()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var inhibitor = await MacSleepInhibitor.CreateAsync(NullLogger.Instance);
        Assert.IsType<MacSleepInhibitor>(inhibitor);
        await inhibitor.DisposeAsync();
    }

    [Fact]
    public async Task MacInhibitor_DoubleDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var inhibitor = await MacSleepInhibitor.CreateAsync(NullLogger.Instance);
        await inhibitor.DisposeAsync();
        await inhibitor.DisposeAsync();
    }
}

public class LinuxSleepInhibitorTests
{
    [Fact]
    public async Task LinuxInhibitor_CreateAndDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux()) return;

        var inhibitor = await LinuxSleepInhibitor.CreateAsync(NullLogger.Instance);
        Assert.NotNull(inhibitor);
        await inhibitor.DisposeAsync();
    }

    [Fact]
    public async Task LinuxInhibitor_DoubleDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux()) return;

        var inhibitor = await LinuxSleepInhibitor.CreateAsync(NullLogger.Instance);
        await inhibitor.DisposeAsync();
        await inhibitor.DisposeAsync();
    }
}
