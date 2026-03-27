namespace TaskBoard.Worker.Clients;

/// <summary>
/// Resolves the Claude CLI executable for the current platform.
/// On Windows, probes PATH for claude.cmd (Node.js/nvm4w) and claude.exe (native install).
/// </summary>
public static class ClaudeCliResolver
{
    private static readonly string[] WindowsCandidates = ["claude.cmd", "claude.exe"];

    /// <summary>
    /// Returns the correct Claude CLI executable name.
    /// If <paramref name="configuredPath"/> is not the default ("claude"),
    /// it is returned as-is (explicit user override).
    /// On Windows with the default, probes for claude.cmd then claude.exe in PATH.
    /// </summary>
    public static string Resolve(string configuredPath)
    {
        // Explicit user override — respect it unconditionally
        if (!string.Equals(configuredPath, "claude", StringComparison.OrdinalIgnoreCase))
            return configuredPath;

        if (!OperatingSystem.IsWindows())
            return configuredPath;

        foreach (var candidate in WindowsCandidates)
        {
            if (ExistsOnPath(candidate))
                return candidate;
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
}
