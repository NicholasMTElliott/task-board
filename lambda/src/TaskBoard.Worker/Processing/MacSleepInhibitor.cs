using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TaskBoard.Worker.Processing;

[SupportedOSPlatform("macos")]
internal sealed class MacSleepInhibitor : ISystemSleepInhibitor
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private bool _disposed;

    private MacSleepInhibitor(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
    }

    public static async Task<ISystemSleepInhibitor> CreateAsync(ILogger logger)
    {
        const string executable = "caffeinate";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-i",
                UseShellExecute = false,
                RedirectStandardInput = true, // keep stdin open so caffeinate stays alive
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process is null || process.HasExited)
            {
                logger.LogWarning("Failed to start caffeinate — system may sleep during polling");
                return new NoOpSleepInhibitor();
            }

            logger.LogInformation("System sleep inhibited via caffeinate (pid={Pid})", process.Id);
            return new MacSleepInhibitor(process, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start caffeinate — system may sleep during polling");
            return new NoOpSleepInhibitor();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    await _process.WaitForExitAsync();
                }
                _logger.LogInformation("System sleep inhibition released (caffeinate killed)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping caffeinate process");
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
