using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Pins the inactivity-timer behaviour of <see cref="ProcessRunner"/>:
/// silent processes get killed past the threshold, fast-exiting processes
/// proceed normally, and the hard wall-clock cap still applies when no
/// inactivity threshold is configured.
/// </summary>
/// <remarks>
/// These tests launch real subprocesses (cmd.exe on Windows). They skip on
/// non-Windows hosts because the rest of the project is Windows-targeted and
/// the cmd.exe shape is the simplest mechanism for "sleep without producing
/// output". The skip is silent (returns early) so the suite stays green on
/// any host that runs the broader test pass.
/// </remarks>
public class ProcessRunnerInactivityTests
{
    private const int InactivityThresholdSeconds = 2;
    private const int SilentSleepSeconds = 6;

    [Fact]
    public async Task RunProcessAsync_SilentProcessExceedsInactivityThreshold_ThrowsInactivityTimeoutException()
    {
        if (!OperatingSystem.IsWindows()) return; // see remarks

        var startedAt = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<InactivityTimeoutException>(() =>
            ProcessRunner.RunProcessAsync(
                "powershell.exe",
                // Start-Sleep produces no output and doesn't depend on a TTY,
                // unlike cmd.exe's `timeout` which fails fast when stdin is
                // redirected. -NoProfile keeps cold start fast.
                ["-NoProfile", "-Command", $"Start-Sleep -Seconds {SilentSleepSeconds}"],
                Path.GetTempPath(),
                // Hard cap is well past the silent sleep — only the inactivity
                // timer should be the trigger.
                timeoutSeconds: 60,
                CancellationToken.None,
                agentName: "test",
                inactivityTimeoutSeconds: InactivityThresholdSeconds));

        var elapsed = DateTime.UtcNow - startedAt;

        // Inactivity should fire before the silent sleep finishes; allow an
        // absolute upper bound generous enough to absorb CI variance but tight
        // enough to prove the inactivity monitor — not the hard cap — fired.
        Assert.True(elapsed.TotalSeconds < SilentSleepSeconds,
            $"Expected inactivity to fire before {SilentSleepSeconds}s, but elapsed was {elapsed.TotalSeconds}s.");
        Assert.Equal(InactivityThresholdSeconds, ex.InactivitySeconds);
        Assert.Contains("inactivity timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunProcessAsync_FastProcess_CompletesWithoutFiringInactivity()
    {
        if (!OperatingSystem.IsWindows()) return; // see remarks

        var (exitCode, stdout, _) = await ProcessRunner.RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-Command", "Write-Output hello"],
            Path.GetTempPath(),
            timeoutSeconds: 30,
            CancellationToken.None,
            agentName: "test",
            inactivityTimeoutSeconds: InactivityThresholdSeconds);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [Fact]
    public async Task RunProcessAsync_StreamingOutput_DoesNotFireInactivity()
    {
        if (!OperatingSystem.IsWindows()) return; // see remarks

        // Regression guard for the OutputDataReceived → lastOutputTicks update
        // path: emit 4 lines of output spaced 750ms apart with a 2s inactivity
        // threshold. Each line must reset the gap; otherwise the monitor would
        // fire mid-loop. A silent test alone would pass even if the handler
        // were never wired.
        var (exitCode, stdout, _) = await ProcessRunner.RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-Command",
                "1..4 | ForEach-Object { Write-Output \"tick $_\"; Start-Sleep -Milliseconds 750 }"],
            Path.GetTempPath(),
            timeoutSeconds: 30,
            CancellationToken.None,
            agentName: "test",
            inactivityTimeoutSeconds: InactivityThresholdSeconds);

        Assert.Equal(0, exitCode);
        Assert.Contains("tick 1", stdout);
        Assert.Contains("tick 4", stdout);
    }

    [Fact]
    public async Task RunProcessAsync_HardCapShorterThanInactivityThreshold_ThrowsBaseTimeoutException()
    {
        if (!OperatingSystem.IsWindows()) return; // see remarks

        // Both timers configured, but hard cap (2s) fires before inactivity
        // threshold (10s). The catch block reads `inactivityFired`; if that
        // flag were spuriously set, we'd throw the InactivityTimeoutException
        // subclass and silently misclassify a hard-cap failure.
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunProcessAsync(
                "powershell.exe",
                ["-NoProfile", "-Command", $"Start-Sleep -Seconds {SilentSleepSeconds}"],
                Path.GetTempPath(),
                timeoutSeconds: InactivityThresholdSeconds,
                CancellationToken.None,
                agentName: "test",
                inactivityTimeoutSeconds: 10));

        Assert.IsNotType<InactivityTimeoutException>(ex);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunProcessAsync_NoInactivityConfigured_HardTimeoutThrowsRegularTimeoutException()
    {
        if (!OperatingSystem.IsWindows()) return; // see remarks

        // Hard timeout fires alone (no inactivity monitor) — must throw the
        // base TimeoutException, NOT the InactivityTimeoutException subclass,
        // so callers and metrics can still distinguish the two cases.
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunProcessAsync(
                "powershell.exe",
                ["-NoProfile", "-Command", $"Start-Sleep -Seconds {SilentSleepSeconds}"],
                Path.GetTempPath(),
                timeoutSeconds: InactivityThresholdSeconds,
                CancellationToken.None,
                agentName: "test",
                inactivityTimeoutSeconds: null));

        Assert.IsNotType<InactivityTimeoutException>(ex);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
