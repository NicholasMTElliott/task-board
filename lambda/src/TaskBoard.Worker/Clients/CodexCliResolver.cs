namespace TaskBoard.Worker.Clients;

/// <summary>
/// Resolves the Codex CLI executable for the current platform.
/// On Windows, probes PATH for codex.cmd (Node.js/npm install) and codex.exe (native install).
/// </summary>
public static class CodexCliResolver
{
    private static readonly string[] WindowsCandidates = ["codex.cmd", "codex.exe"];

    /// <summary>
    /// Returns the correct Codex CLI executable name.
    /// If <paramref name="configuredPath"/> is not the default ("codex"),
    /// it is returned as-is (explicit user override).
    /// On Windows with the default, probes for codex.cmd then codex.exe in PATH.
    /// </summary>
    public static string Resolve(string configuredPath)
    {
        // Explicit user override — respect it unconditionally
        if (!string.Equals(configuredPath, "codex", StringComparison.OrdinalIgnoreCase))
            return configuredPath;

        if (!OperatingSystem.IsWindows())
            return configuredPath;

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
            Path.Combine(home, ".local", "bin", "codex.exe"),
            Path.Combine(appData, "npm", "codex.cmd"),
            Path.Combine(appData, "npm", "codex.exe"),
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
    /// Runs <c>codex --version</c> and returns the trimmed stdout on success,
    /// or an <c>(unknown: reason)</c> marker on failure. Intended for one-shot
    /// startup logging — we want version drift to be visible in logs so that
    /// post-mortems can correlate behaviour changes with CLI upgrades. Never throws.
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

            // Some CLIs print version to stderr
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
