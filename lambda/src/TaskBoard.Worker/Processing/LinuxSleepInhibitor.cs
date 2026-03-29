using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TaskBoard.Worker.Processing;

[SupportedOSPlatform("linux")]
internal sealed class LinuxSleepInhibitor : ISystemSleepInhibitor
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private bool _disposed;

    private LinuxSleepInhibitor(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
    }

    public static async Task<ISystemSleepInhibitor> CreateAsync(ILogger logger)
    {
        const string executable = "systemd-inhibit";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                // --what=idle:sleep prevents both idle and explicit sleep
                // --who/--why for identification in `systemd-inhibit --list`
                // --mode=block is default but explicit for clarity
                // cat with stdin redirected keeps the process alive until we kill it
                Arguments = "--what=idle:sleep --who=aiboard --why=\"Polling for board cards\" --mode=block cat",
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process is null || process.HasExited)
            {
                logger.LogWarning("Failed to start systemd-inhibit — system may sleep during polling");
                return new NoOpSleepInhibitor();
            }

            logger.LogInformation("System sleep inhibited via systemd-inhibit (pid={Pid})", process.Id);
            return new LinuxSleepInhibitor(process, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start systemd-inhibit — system may sleep during polling");
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
                _logger.LogInformation("System sleep inhibition released (systemd-inhibit killed)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping systemd-inhibit process");
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
