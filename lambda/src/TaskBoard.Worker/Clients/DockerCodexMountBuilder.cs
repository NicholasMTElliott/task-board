namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mount specifications for running the Codex CLI
/// inside a container. Extends <see cref="DockerMountBuilderBase"/> with
/// Codex-specific credential staging.
/// </summary>
/// <remarks>
/// Produces up to four bind mounts per run (see base class for the shared set):
/// <list type="number">
///   <item>Worktree (RW) — from the base class.</item>
///   <item>Base <c>.git</c> directory (RO) — from the base class.</item>
///   <item><c>.git</c> file override (RO) — from the base class.</item>
///   <item>Codex credentials — a per-run staged RW copy of <c>~/.codex/</c>.
///         Codex CLI may refresh tokens during exec and writes session-state
///         files, so a read-only mount is insufficient. The host's
///         <c>~/.codex/</c> is never mutated; the staged copy is deleted on
///         <see cref="DockerMountContext.DisposeAsync"/>. Heavy subdirectories
///         (sessions, log, screenshots) are excluded from the copy.</item>
/// </list>
/// </remarks>
public sealed class DockerCodexMountBuilder(ILogger<DockerCodexMountBuilder> logger)
    : DockerMountBuilderBase
{
    /// <summary>
    /// Default container path for Codex credentials.
    /// Matches the <c>agent</c> user's home directory in the
    /// aiboard-codex-sandbox image.
    /// </summary>
    public const string DefaultCredentialMountPoint = "/home/agent/.codex";

    /// <summary>
    /// Builds a <see cref="DockerMountContext"/> containing all volume
    /// mounts and environment variables needed to run the Codex CLI agent
    /// inside a Docker container.
    /// </summary>
    public async Task<DockerMountContext> BuildAsync(
        string worktreePath,
        DockerCodexAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var mounts = new List<DockerMount>();
        var tempFiles = new List<string>();
        var tempDirs = new List<string>();
        var pathMap = new List<(string HostPrefix, string ContainerPrefix)>();

        await AddWorkspaceAndGitMountsAsync(
            worktreePath, mounts, tempFiles, pathMap, logger, cancellationToken);

        AddCredentialMount(options, mounts, tempDirs);

        var envVars = new Dictionary<string, string>
        {
            // Required because the base .git directory is mounted read-only;
            // git read commands skip index lock acquisition.
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

        return new DockerMountContext(mounts, envVars, pathMap, tempFiles, tempDirs);
    }

    private void AddCredentialMount(
        DockerCodexAgentOptions options,
        List<DockerMount> mounts,
        List<string> tempDirs)
    {
        var credPath = options.CredentialPath ?? DetectCredentialPath();
        var credMountPoint = options.CredentialMountPoint ?? DefaultCredentialMountPoint;

        if (string.IsNullOrEmpty(credPath))
        {
            logger.LogWarning(
                "No Codex credential path configured or detected — " +
                "Codex CLI may not authenticate. Run `codex login` on the host, " +
                "or set DockerAgents:Codex:CredentialPath explicitly.");
            return;
        }

        if (!Directory.Exists(credPath))
        {
            logger.LogWarning(
                "Codex credential path '{CredPath}' does not exist — " +
                "Codex CLI may not authenticate inside the container",
                credPath);
            return;
        }

        var stagedCredDir = Path.Combine(
            Path.GetTempPath(),
            $"aiboard-codex-{Guid.NewGuid():N}");
        CopyDirectoryRecursive(credPath, stagedCredDir, CredentialCopyExcludes);
        tempDirs.Add(stagedCredDir);

        mounts.Add(new DockerMount
        {
            HostPath = NormalizeHostPath(stagedCredDir),
            ContainerPath = credMountPoint,
            ReadOnly = false,
        });

        logger.LogDebug(
            "Codex credential mount (staged RW copy): {CredPath} → {Staged} → {MountPoint}",
            credPath, stagedCredDir, credMountPoint);
    }

    private static readonly HashSet<string> CredentialCopyExcludes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Heavy subdirectories that the agent doesn't need: prior session
        // logs, conversation rollouts, debug captures. Excluded to keep
        // per-run staging fast and small. auth.json + config.toml live at
        // the top level of ~/.codex and are always copied.
        "sessions",
        "log",
        "logs",
        "screenshots",
    };

    private static void CopyDirectoryRecursive(string source, string dest, HashSet<string> excludeTopLevelDirs)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            try
            {
                File.Copy(file, Path.Combine(dest, name), overwrite: true);
            }
            catch
            {
                // Best-effort: skip files that can't be copied (locked, permissions)
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (excludeTopLevelDirs.Contains(name)) continue;
            CopyDirectoryRecursiveAll(dir, Path.Combine(dest, name));
        }
    }

    private static void CopyDirectoryRecursiveAll(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            }
            catch
            {
                // Best-effort
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryRecursiveAll(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// Auto-detects the Codex CLI credential directory on the host by probing
    /// <c>~/.codex/</c>. Returns null if the directory does not exist.
    /// </summary>
    internal static string? DetectCredentialPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        var codexDir = Path.Combine(home, ".codex");
        return Directory.Exists(codexDir) ? codexDir : null;
    }
}
