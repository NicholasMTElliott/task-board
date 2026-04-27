namespace TaskBoard.Worker.Clients;

/// <summary>
/// Resolves the Claude CLI executable for the current platform.
/// On Windows, probes PATH for claude.cmd (Node.js/nvm4w) and claude.exe (native install).
/// </summary>
public static class ClaudeCliResolver
{
    private static readonly string[] WindowsCandidates = ["claude.cmd", "claude.exe"];

    /// <summary>
    /// Returns the correct Claude CLI executable path.
    /// If <paramref name="configuredPath"/> is not the default ("claude"),
    /// it is returned as-is (explicit user override).
    /// On Windows with the default, probes PATH then well-known install locations.
    /// </summary>
    public static string Resolve(string configuredPath)
    {
        // Explicit user override — respect it unconditionally
        if (!string.Equals(configuredPath, "claude", StringComparison.OrdinalIgnoreCase))
            return configuredPath;

        if (!OperatingSystem.IsWindows())
            return configuredPath;

        // Try PATH first
        foreach (var candidate in WindowsCandidates)
        {
            if (ExistsOnPath(candidate))
                return candidate;
        }

        // Fallback: well-known install locations
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] wellKnown =
        [
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
            Path.Combine(appData, "npm", "claude.exe"),
        ];

        foreach (var path in wellKnown)
        {
            if (File.Exists(path))
                return path;
        }

        // Nothing found — return the default and let downstream validation report the error
        return configuredPath;
    }

    private static bool ExistsOnPath(string fileName)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add(fileName);
            process.Start();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <c>claude --version</c> and returns the trimmed stdout on success,
    /// or an <c>(unknown: reason)</c> marker on failure. Mirrors
    /// <see cref="CodexCliResolver.TryGetVersionAsync"/>; consumed by
    /// <see cref="TaskBoard.Worker.Validation.CliVersionStartupCheck"/> at
    /// startup so version drift surfaces in logs (and triggers policy errors
    /// for unsupported versions). Never throws.
    /// </summary>
    public static async Task<string> TryGetVersionAsync(
        string executablePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token, cancellationToken);

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return "(unknown: --version timed out after 5s)";
            }

            var stdout = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            var stderr = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();

            if (process.ExitCode != 0)
                return $"(unknown: exit {process.ExitCode}; stderr={Truncate(stderr, 200)})";

            if (!string.IsNullOrWhiteSpace(stdout))
                return stdout;

            if (!string.IsNullOrWhiteSpace(stderr))
                return stderr;

            return "(unknown: no output)";
        }
        catch (Exception ex)
        {
            return $"(unknown: {ex.GetType().Name}: {Truncate(ex.Message, 200)})";
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
