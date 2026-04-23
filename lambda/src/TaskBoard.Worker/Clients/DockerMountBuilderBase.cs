namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared, CLI-agnostic infrastructure for building Docker volume mount specifications.
/// Handles the worktree + <c>.git</c> mounts that every containerized agent executor needs.
/// CLI-specific subclasses (e.g. <see cref="DockerClaudeMountBuilder"/>) add provider-specific
/// mounts such as credential directories and assemble the final <see cref="DockerMountContext"/>.
/// </summary>
/// <remarks>
/// <b>Shared mounts produced by <see cref="AddWorkspaceAndGitMountsAsync"/>:</b>
/// <list type="number">
///   <item>Worktree (RW) — the agent's working directory at <c>/workspace</c>.</item>
///   <item>Base <c>.git</c> directory (RO) — provides shared object store and refs for the worktree.</item>
///   <item><c>.git</c> file override (RO) — a temp file that shadows the worktree's <c>.git</c> file
///         with a container-internal <c>gitdir:</c> path so that git resolves the base repo correctly.</item>
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
/// this env var is set. Subclasses should include it in the env vars passed to
/// <see cref="DockerMountContext"/>.
/// </para>
/// </remarks>
public abstract class DockerMountBuilderBase
{
    /// <summary>Container path where the git worktree is mounted (read-write).</summary>
    public const string WorkspaceMountPoint = "/workspace";

    /// <summary>Container path where the base <c>.git</c> directory is mounted (read-only).</summary>
    public const string BaseGitMountPoint = "/repo/.git";

    private const string WorktreesSubdir = "worktrees";

    /// <summary>
    /// Adds the worktree and <c>.git</c> mounts that every Docker-wrapped agent executor needs,
    /// appending entries to the supplied collections.
    /// </summary>
    /// <param name="worktreePath">Absolute host path to the git worktree.</param>
    /// <param name="mounts">Accumulating list of mounts; worktree and git entries are appended.</param>
    /// <param name="tempFiles">Accumulating list of temp files; the <c>.git</c> override file is appended.</param>
    /// <param name="pathMap">
    /// Accumulating list of host → container path prefix mappings used for
    /// <see cref="DockerMountContext.TranslatePath"/> resolution.
    /// </param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected static async Task AddWorkspaceAndGitMountsAsync(
        string worktreePath,
        List<DockerMount> mounts,
        List<string> tempFiles,
        List<(string HostPrefix, string ContainerPrefix)> pathMap,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var normalizedWorktree = NormalizeHostPath(worktreePath);
        mounts.Add(new DockerMount
        {
            HostPath = normalizedWorktree,
            ContainerPath = WorkspaceMountPoint,
            ReadOnly = false,
        });
        pathMap.Add((normalizedWorktree.TrimEnd('/'), WorkspaceMountPoint));

        var gitdirPath = ReadWorktreeGitdirPath(worktreePath);
        if (gitdirPath is null)
        {
            logger.LogWarning(
                "Worktree at '{WorktreePath}' has no .git file (or it is not a gitfile) — " +
                "git operations may fail inside the container",
                worktreePath);
            return;
        }

        var worktreeName = GetWorktreeName(gitdirPath);
        var baseGitPath = GetBaseGitPath(gitdirPath);

        if (baseGitPath is null || worktreeName is null)
        {
            logger.LogWarning(
                "Could not parse base .git path from gitdir '{GitdirPath}' — " +
                "git operations may fail inside the container",
                gitdirPath);
            return;
        }

        mounts.Add(new DockerMount
        {
            HostPath = NormalizeHostPath(baseGitPath),
            ContainerPath = BaseGitMountPoint,
            ReadOnly = true,
        });

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

    /// <summary>
    /// Reads the <c>gitdir:</c> path from the worktree's <c>.git</c> file.
    /// Returns null if the path does not exist, is a directory (base repo), or is not a valid gitfile.
    /// </summary>
    internal static string? ReadWorktreeGitdirPath(string worktreePath)
    {
        var gitFilePath = Path.Combine(worktreePath, ".git");

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

    protected static async Task<string> WriteTempFileAsync(string content, CancellationToken ct)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"aiboard-git-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, content, ct);
        return tempPath;
    }
}
