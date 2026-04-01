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
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string executable, string[] argumentList, string workingDirectory,
        int timeoutSeconds, CancellationToken cancellationToken,
        string? stdinData = null,
        IReadOnlyCollection<string>? envVarsToRemove = null,
        string agentName = "agent")
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

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

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

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException(
                $"{agentName} timed out after {timeoutSeconds}s. " +
                $"Partial stderr: {stderrBuf.ToString()[..Math.Min(500, stderrBuf.Length)]}");
        }

        return (process.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString());
    }
}
