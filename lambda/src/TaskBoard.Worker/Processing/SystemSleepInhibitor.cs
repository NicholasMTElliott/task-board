using Microsoft.Extensions.Logging;

namespace TaskBoard.Worker.Processing;

public static class SystemSleepInhibitor
{
    public static async Task<ISystemSleepInhibitor> CreateAsync(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsSleepInhibitor(logger);

        if (OperatingSystem.IsMacOS())
            return await MacSleepInhibitor.CreateAsync(logger);

        if (OperatingSystem.IsLinux())
            return await LinuxSleepInhibitor.CreateAsync(logger);

        logger.LogWarning("Sleep inhibition not supported on this platform");
        return new NoOpSleepInhibitor();
    }
}
