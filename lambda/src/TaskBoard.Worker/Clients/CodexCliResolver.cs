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
}
