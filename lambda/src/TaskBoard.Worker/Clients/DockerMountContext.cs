namespace TaskBoard.Worker.Clients;

/// <summary>
/// Holds Docker volume mount specifications and environment variables for a containerized agent run.
/// Owns any temporary files created for the run (e.g., the .git override file) and deletes them on dispose.
/// </summary>
/// <remarks>
/// Created by <see cref="DockerMountBuilder.BuildAsync"/>. Lifetime matches the container:
/// dispose after the container exits so that temp files outlive the container run.
/// </remarks>
public sealed class DockerMountContext : IAsyncDisposable
{
    private readonly IReadOnlyList<(string HostPrefix, string ContainerPrefix)> _pathMap;
    private readonly List<string> _tempFiles;
    private readonly List<string> _tempDirs;

    internal DockerMountContext(
        IReadOnlyList<DockerMount> mounts,
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyList<(string HostPrefix, string ContainerPrefix)> pathMap,
        List<string> tempFiles,
        List<string>? tempDirs = null)
    {
        Mounts = mounts;
        EnvironmentVariables = environmentVariables;
        _pathMap = pathMap;
        _tempFiles = tempFiles;
        _tempDirs = tempDirs ?? new List<string>();
    }

    /// <summary>Volume mounts to pass to <c>docker run -v</c>.</summary>
    public IReadOnlyList<DockerMount> Mounts { get; }

    /// <summary>
    /// Environment variables to inject into the container via <c>docker run -e</c>
    /// (e.g., <c>GIT_OPTIONAL_LOCKS=0</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>
    /// Translates a host-side absolute path to its container-side equivalent using the mount map.
    /// Returns null if no mapping matches.
    /// </summary>
    /// <remarks>
    /// Normalizes backslashes to forward slashes before matching, so Windows paths work correctly.
    /// </remarks>
    public string? TranslatePath(string hostPath)
    {
        if (string.IsNullOrEmpty(hostPath)) return null;

        var normalized = hostPath.Replace('\\', '/');

        foreach (var (hostPrefix, containerPrefix) in _pathMap)
        {
            // Exact match (e.g., translating the worktree root itself)
            if (string.Equals(normalized, hostPrefix, StringComparison.OrdinalIgnoreCase))
                return containerPrefix;

            // Prefix match with separator (prevents /workspace-extra matching /workspace)
            if (normalized.StartsWith(hostPrefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized[(hostPrefix.Length + 1)..];
                return $"{containerPrefix}/{relative}";
            }
        }

        return null;
    }

    /// <summary>Deletes any temporary files created for this mount context (best-effort).</summary>
    public ValueTask DisposeAsync()
    {
        foreach (var tempFile in _tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
                // Best-effort cleanup: ignore errors (e.g., file already deleted)
            }
        }
        _tempFiles.Clear();

        foreach (var tempDir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
        _tempDirs.Clear();
        return ValueTask.CompletedTask;
    }
}
