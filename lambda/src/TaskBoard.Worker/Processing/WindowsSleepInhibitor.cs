using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TaskBoard.Worker.Processing;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSleepInhibitor : ISystemSleepInhibitor
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS      = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private readonly ILogger _logger;
    private bool _disposed;

    public WindowsSleepInhibitor(ILogger logger)
    {
        _logger = logger;
        var result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        if (result == 0)
            _logger.LogWarning("SetThreadExecutionState failed — system may sleep during polling");
        else
            _logger.LogInformation("System sleep inhibited (Windows)");
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            SetThreadExecutionState(ES_CONTINUOUS); // Clear SYSTEM_REQUIRED, restore default
            _logger.LogInformation("System sleep inhibition released (Windows)");
        }
        return ValueTask.CompletedTask;
    }
}
