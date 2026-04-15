namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mount specifications for containerized agent execution.
/// </summary>
/// <remarks>
/// Produces up to four bind mounts per run:
/// <list type="number">
///   <item>Worktree (RW) — the agent's working directory at <c>/workspace</c>.</item>
///   <item>Base <c>.git</c> directory (RO) — provides shared object store and refs for the worktree.</item>
///   <item><c>.git</c> file override (RO) — a temp file that shadows the worktree's <c>.git</c> file
///         with a container-internal <c>gitdir:</c> path so that git resolves the base repo correctly.</item>
///   <item>Claude credentials (RO) — mounts <c>~/.claude/</c> so the CLI authenticates non-interactively.</item>
/// </list>
///
/// <para>
/// <b>Why the .git file override is needed:</b>
/// Git worktrees reference their base repository via a <c>.git</c> file containing
/// <c>gitdir: /absolute/host/path/.git/worktrees/{name}</c>. Inside the container, this host path
/// does not exist. The override replaces it with the container-internal path so git can resolve
/// the base <c>.git</c> directory (mounted at <see cref="BaseGitMountPoint"/>).
/// </para>
///
/// <para>
/// <b>GIT_OPTIONAL_LOCKS=0:</b> The base <c>.git</c> directory is mounted read-only. Git read
/// commands that would normally acquire an index lock (e.g., <c>git status</c>) will fail unless
/// this env var is set.
/// </para>
/// </remarks>
public sealed class DockerMountBuilder(ILogger<DockerMountBuilder> logger)
{
    /// <summary>Container path where the git worktree is mounted (read-write).</summary>
    public const string WorkspaceMountPoint = "/workspace";

    /// <summary>Container path where the base <c>.git</c> directory is mounted (read-only).</summary>
    public const string BaseGitMountPoint = "/repo/.git";

    /// <summary>
    /// Default container path for Claude credentials.
    /// Matches the <c>agent</c> user's home directory in the aiboard-agent-sandbox image (#61).
    /// </summary>
    public const string DefaultCredentialMountPoint = "/home/agent/.claude";

    private const string WorktreesSubdir = "worktrees";

    /// <summary>
    /// Builds a <see cref="DockerMountContext"/> containing all volume mounts and environment
    /// variables needed to run the Claude CLI agent inside a Docker container.
    /// </summary>
    /// <param name="worktreePath">Absolute host path to the git worktree (agent's working directory).</param>
    /// <param name="options">Docker agent configuration (credential path override, mount points).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DockerMountContext> BuildAsync(
        string worktreePath,
        DockerAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        var mounts = new List<DockerMount>();
        var tempFiles = new List<string>();
        var tempDirs = new List<string>();
        var pathMap = new List<(string HostPrefix, string ContainerPrefix)>();

        // 1. Worktree mount (RW) — the agent's working directory
        var normalizedWorktree = NormalizeHostPath(worktreePath);
        mounts.Add(new DockerMount
        {
            HostPath = normalizedWorktree,
            ContainerPath = WorkspaceMountPoint,
            ReadOnly = false,
        });
        pathMap.Add((normalizedWorktree.TrimEnd('/'), WorkspaceMountPoint));

        // 2. Parse .git file → base .git dir + .git override
        var gitdirPath = ReadWorktreeGitdirPath(worktreePath);
        if (gitdirPath is not null)
        {
            var worktreeName = GetWorktreeName(gitdirPath);
            var baseGitPath = GetBaseGitPath(gitdirPath);

            if (baseGitPath is not null && worktreeName is not null)
            {
                // 2a. Base .git directory (RO) — provides object store, refs, and commondir
                mounts.Add(new DockerMount
                {
                    HostPath = NormalizeHostPath(baseGitPath),
                    ContainerPath = BaseGitMountPoint,
                    ReadOnly = true,
                });

                // 2b. .git file override — temp file with the container-internal gitdir path,
                //     bind-mounted over /workspace/.git to shadow the original host-path reference
                var containerGitdirPath = $"{BaseGitMountPoint}/{WorktreesSubdir}/{worktreeName}";
                var tempGitContent = $"gitdir: {containerGitdirPath}";
                var tempGitFile = await WriteTempFileAsync(tempGitContent, cancellationToken);
                tempFiles.Add(tempGitFile);

                mounts.Add(new DockerMount
                {
                    HostPath = NormalizeHostPath(tempGitFile),
                    ContainerPath = $"{WorkspaceMountPoint}/.git",
                    ReadOnly = true,
                });

                logger.LogDebug(
                    "Git override mounted: temp={TempFile} → /workspace/.git (gitdir: {ContainerGitdir})",
                    tempGitFile, containerGitdirPath);
            }
            else
            {
                logger.LogWarning(
                    "Could not parse base .git path from gitdir '{GitdirPath}' — " +
                    "git operations may fail inside the container",
                    gitdirPath);
            }
        }
        else
        {
            logger.LogWarning(
                "Worktree at '{WorktreePath}' has no .git file (or it is not a gitfile) — " +
                "git operations may fail inside the container",
                worktreePath);
        }

        // 3. Claude credential mount (RO)
        var credPath = options.CredentialPath ?? DetectCredentialPath();
        var credMountPoint = options.CredentialMountPoint ?? DefaultCredentialMountPoint;

        if (!string.IsNullOrEmpty(credPath))
        {
            if (Directory.Exists(credPath))
            {
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
            else
            {
                logger.LogWarning(
                    "Claude credential path '{CredPath}' does not exist — " +
                    "Claude CLI may not authenticate inside the container",
                    credPath);
            }
        }
        else
        {
            logger.LogWarning(
                "No Claude credential path configured or detected — " +
                "Claude CLI may not authenticate. Set DockerAgent:CredentialPath to override.");
        }

        // Environment variables injected into the container via docker run -e
        var envVars = new Dictionary<string, string>
        {
            // Prevents git from acquiring index locks on read-only operations.
            // Required because the base .git directory is mounted read-only.
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

        return new DockerMountContext(mounts, envVars, pathMap, tempFiles, tempDirs);
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
    /// Reads the <c>gitdir:</c> path from the worktree's <c>.git</c> file.
    /// Returns null if the path does not exist, is a directory (base repo), or is not a valid gitfile.
    /// </summary>
    internal static string? ReadWorktreeGitdirPath(string worktreePath)
    {
        var gitFilePath = Path.Combine(worktreePath, ".git");

        // Base repos have a .git directory; worktrees have a .git file
        if (!File.Exists(gitFilePath))
            return null;

        string content;
        try
        {
            content = File.ReadAllText(gitFilePath).Trim();
        }
        catch
        {
            return null;
        }

        // Format: "gitdir: /absolute/path/to/.git/worktrees/{name}"
        const string prefix = "gitdir:";
        if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var path = content[prefix.Length..].Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>
    /// Extracts the worktree name from a gitdir path.
    /// E.g., <c>/path/.git/worktrees/my-branch</c> → <c>my-branch</c>
    /// </summary>
    internal static string? GetWorktreeName(string gitdirPath)
    {
        var normalized = gitdirPath.Replace('\\', '/');
        var marker = $"/{WorktreesSubdir}/";
        var idx = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var name = normalized[(idx + marker.Length)..].Trim('/');
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Extracts the base <c>.git</c> directory path from a gitdir path.
    /// E.g., <c>/path/.git/worktrees/my-branch</c> → <c>/path/.git</c>
    /// </summary>
    internal static string? GetBaseGitPath(string gitdirPath)
    {
        var normalized = gitdirPath.Replace('\\', '/');
        var marker = $"/{WorktreesSubdir}/";
        var idx = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var baseGit = normalized[..idx];
        return string.IsNullOrEmpty(baseGit) ? null : baseGit;
    }

    /// <summary>
    /// Normalizes a host path for Docker volume mount syntax by converting
    /// backslashes to forward slashes (required by Docker Desktop on Windows).
    /// </summary>
    internal static string NormalizeHostPath(string hostPath) =>
        hostPath.Replace('\\', '/');

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

    private static async Task<string> WriteTempFileAsync(string content, CancellationToken ct)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"aiboard-git-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, content, ct);
        return tempPath;
    }
}
