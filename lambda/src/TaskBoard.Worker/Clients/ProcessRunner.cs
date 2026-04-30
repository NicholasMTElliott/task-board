using System.Diagnostics;
using System.Text;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared process execution utilities for agent executors.
/// Used by both <see cref="ClaudeAgentExecutor"/> and <see cref="CodexAgentExecutor"/>.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Formats an argument list for diagnostic logging, truncating long args and quoting args with spaces.
    /// </summary>
    internal static string FormatArgsForLogging(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var arg = args[i];
            if (arg.Length > 2000)
                arg = arg[..2000] + "...[truncated]";
            sb.Append(arg.Contains(' ') ? $"\"{arg}\"" : arg);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Launches a subprocess, optionally writing <paramref name="stdinData"/> to its stdin,
    /// and collects stdout/stderr with a timeout.
    /// </summary>
    /// <param name="executable">Path to the executable.</param>
    /// <param name="argumentList">Arguments passed via <see cref="ProcessStartInfo.ArgumentList"/> (bypasses cmd.exe quoting).</param>
    /// <param name="workingDirectory">Working directory for the subprocess.</param>
    /// <param name="timeoutSeconds">Maximum wall-clock seconds before the process is killed.</param>
    /// <param name="cancellationToken">Cancellation token; cancellation also kills the process.</param>
    /// <param name="stdinData">Optional data to pipe to the process's stdin. If non-null, stdin is redirected.</param>
    /// <param name="envVarsToRemove">Optional env var names to remove from the subprocess environment.</param>
    /// <param name="agentName">Display name used in timeout error messages (e.g. "Claude agent").</param>
    /// <param name="inactivityTimeoutSeconds">
    /// Optional — kill the process if no stdout/stderr output is observed for this many seconds.
    /// Throws <see cref="InactivityTimeoutException"/> when triggered. Null = inactivity check disabled.
    /// </param>
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string executable, string[] argumentList, string workingDirectory,
        int timeoutSeconds, CancellationToken cancellationToken,
        string? stdinData = null,
        IReadOnlyCollection<string>? envVarsToRemove = null,
        string agentName = "agent",
        int? inactivityTimeoutSeconds = null)
    {
        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.FileName = executable;
        foreach (var arg in argumentList)
            startInfo.ArgumentList.Add(arg);

        if (envVarsToRemove is not null)
        {
            foreach (var envVar in envVarsToRemove)
                startInfo.Environment.Remove(envVar);
        }

        process.StartInfo = startInfo;

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        long lastOutputTicks = DateTime.UtcNow.Ticks;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
            stdoutBuf.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
            stderrBuf.AppendLine(e.Data);
        };

        process.Start();

        // MUST begin async output reads BEFORE writing to stdin.
        // Otherwise, if stdinData is large enough to fill the OS pipe buffer (~4 KB),
        // WriteAsync blocks waiting for the child to consume stdin — but the child
        // may be blocked writing to stdout (which nobody is reading yet) → deadlock.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Pipe prompt via stdin to avoid Windows command-line length limits
        if (stdinData is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdinData);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }
            catch (IOException ex)
            {
                // The child process exited or closed its stdin before we finished writing.
                // Wait briefly for the process to exit so we can capture its exit code and stderr.
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(exitCts.Token); } catch { /* best effort */ }

                var exitInfo = process.HasExited ? $"exit code {process.ExitCode}" : "still running";
                var stderrSnapshot = stderrBuf.ToString();
                throw new InvalidOperationException(
                    $"Failed to write prompt to subprocess stdin ({exitInfo}). " +
                    $"Stderr: {stderrSnapshot[..Math.Min(1000, stderrSnapshot.Length)]}".TrimEnd(),
                    ex);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // Inactivity monitor: only spun up when configured. Tracks the gap between
        // the last observed stdout/stderr line and "now"; if that gap exceeds the
        // threshold while the process is still running, fires its own CTS to kill
        // the wait. The inactivity flag distinguishes the eventual exception type.
        using var inactivityCts = inactivityTimeoutSeconds.HasValue
            ? new CancellationTokenSource()
            : null;
        var inactivityFired = 0;
        Task? inactivityMonitor = null;

        if (inactivityTimeoutSeconds.HasValue && inactivityCts is not null)
        {
            var thresholdSeconds = inactivityTimeoutSeconds.Value;
            // Cap the poll cadence at 30s so a generous inactivity window doesn't
            // waste minutes after a process exits. Floor at 1s for tight tests.
            var checkSeconds = Math.Max(1, Math.Min(30, thresholdSeconds / 4));
            inactivityMonitor = Task.Run(async () =>
            {
                var threshold = TimeSpan.FromSeconds(thresholdSeconds);
                var checkInterval = TimeSpan.FromSeconds(checkSeconds);
                while (!process.HasExited)
                {
                    try
                    {
                        await Task.Delay(checkInterval, inactivityCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (process.HasExited) return;
                    var sinceLast = TimeSpan.FromTicks(
                        DateTime.UtcNow.Ticks - Interlocked.Read(ref lastOutputTicks));
                    if (sinceLast > threshold)
                    {
                        // Re-check exit status to avoid a false-positive race:
                        // the process may have exited between the HasExited
                        // check at the top of the loop and this comparison,
                        // and HasExited is only updated when the runtime polls
                        // it. If the process is already done, let the natural
                        // success path handle it.
                        if (process.HasExited) return;
                        Interlocked.Exchange(ref inactivityFired, 1);
                        try { inactivityCts.Cancel(); } catch { /* best effort */ }
                        return;
                    }
                }
            });
        }

        using var combinedCts = inactivityCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, inactivityCts.Token)
            : null;
        var waitToken = combinedCts?.Token ?? timeoutCts.Token;

        try
        {
            await process.WaitForExitAsync(waitToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

            if (Volatile.Read(ref inactivityFired) == 1)
            {
                throw new InactivityTimeoutException(
                    $"{agentName} produced no output for {inactivityTimeoutSeconds}s " +
                    $"(inactivity timeout; hard cap was {timeoutSeconds}s). " +
                    $"Partial stderr: {stderrBuf.ToString()[..Math.Min(500, stderrBuf.Length)]}",
                    inactivityTimeoutSeconds!.Value);
            }

            throw new TimeoutException(
                $"{agentName} timed out after {timeoutSeconds}s. " +
                $"Partial stderr: {stderrBuf.ToString()[..Math.Min(500, stderrBuf.Length)]}");
        }
        finally
        {
            // Stop the monitor regardless of how the wait ended (success, timeout,
            // inactivity, caller cancellation). Awaiting it here keeps the Task
            // tracked — it never throws because all exceptions inside are caught.
            if (inactivityCts is not null && !inactivityCts.IsCancellationRequested)
            {
                try { inactivityCts.Cancel(); } catch { /* best effort */ }
            }
            if (inactivityMonitor is not null)
            {
                try { await inactivityMonitor.ConfigureAwait(false); } catch { /* best effort */ }
            }
        }

        return (process.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString());
    }
}
