namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mount specifications for running the Claude CLI inside a container.
/// Extends <see cref="DockerMountBuilderBase"/> with Claude-specific credential staging.
/// </summary>
/// <remarks>
/// Produces up to four bind mounts per run (see base class for the shared set):
/// <list type="number">
///   <item>Worktree (RW) — from the base class.</item>
///   <item>Base <c>.git</c> directory (RO) — from the base class.</item>
///   <item><c>.git</c> file override (RO) — from the base class.</item>
///   <item>Claude credentials — a per-run staged RW copy of <c>~/.claude/</c>
///         (the CLI creates <c>session-env/</c> at runtime, so a read-only mount is
///         insufficient). The staged directory excludes <c>projects</c>, <c>shell-snapshots</c>,
///         <c>todos</c>, and <c>history</c>. The host's <c>~/.claude/</c> is never mutated;
///         the staged copy is deleted on <see cref="DockerMountContext.DisposeAsync"/>.</item>
/// </list>
/// </remarks>
public sealed class DockerClaudeMountBuilder(ILogger<DockerClaudeMountBuilder> logger)
    : DockerMountBuilderBase
{
    /// <summary>
    /// Default container path for Claude credentials.
    /// Matches the <c>agent</c> user's home directory in the aiboard-agent-sandbox image.
    /// </summary>
    public const string DefaultCredentialMountPoint = "/home/agent/.claude";

    /// <summary>
    /// Builds a <see cref="DockerMountContext"/> containing all volume mounts and environment
    /// variables needed to run the Claude CLI agent inside a Docker container.
    /// </summary>
    public async Task<DockerMountContext> BuildAsync(
        string worktreePath,
        DockerClaudeAgentOptions options,
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
            // Prevents git from acquiring index locks on read-only operations.
            // Required because the base .git directory is mounted read-only.
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

        return new DockerMountContext(mounts, envVars, pathMap, tempFiles, tempDirs);
    }

    private void AddCredentialMount(
        DockerClaudeAgentOptions options,
        List<DockerMount> mounts,
        List<string> tempDirs)
    {
        var credPath = options.CredentialPath ?? DetectCredentialPath();
        var credMountPoint = options.CredentialMountPoint ?? DefaultCredentialMountPoint;

        if (string.IsNullOrEmpty(credPath))
        {
            logger.LogWarning(
                "No Claude credential path configured or detected — " +
                "Claude CLI may not authenticate. Set DockerAgents:Claude:CredentialPath to override.");
            return;
        }

        if (!Directory.Exists(credPath))
        {
            logger.LogWarning(
                "Claude credential path '{CredPath}' does not exist — " +
                "Claude CLI may not authenticate inside the container",
                credPath);
            return;
        }

        var stagedCredDir = Path.Combine(
            Path.GetTempPath(),
            $"aiboard-claude-{Guid.NewGuid():N}");
        CopyDirectoryRecursive(credPath, stagedCredDir, CredentialCopyExcludes);
        tempDirs.Add(stagedCredDir);

        mounts.Add(new DockerMount
        {
            HostPath = NormalizeHostPath(stagedCredDir),
            ContainerPath = credMountPoint,
            ReadOnly = false,
        });

        logger.LogDebug(
            "Claude credential mount (staged RW copy): {CredPath} → {Staged} → {MountPoint}",
            credPath, stagedCredDir, credMountPoint);
    }

    private static readonly HashSet<string> CredentialCopyExcludes = new(StringComparer.OrdinalIgnoreCase)
    {
        "projects",
        "shell-snapshots",
        "todos",
        "history",
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
    /// Auto-detects the Claude CLI credential directory on the host by probing <c>~/.claude/</c>.
    /// Returns null if the directory does not exist.
    /// </summary>
    internal static string? DetectCredentialPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        var claudeDir = Path.Combine(home, ".claude");
        return Directory.Exists(claudeDir) ? claudeDir : null;
    }
}
