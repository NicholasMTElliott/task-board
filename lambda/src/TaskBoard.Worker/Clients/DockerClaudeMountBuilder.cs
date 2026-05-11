namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mount specifications for running the Claude CLI inside a container.
/// Extends <see cref="DockerMountBuilderBase"/> with Claude-specific credential staging.
/// </summary>
/// <remarks>
/// Produces up to five bind mounts per run (see base class for the shared set):
/// <list type="number">
///   <item>Worktree (RW) — from the base class.</item>
///   <item>Base <c>.git</c> directory (RO) — from the base class.</item>
///   <item><c>.git</c> file override (RO) — from the base class.</item>
///   <item><c>~/.claude/</c> directory — a per-run staged RW copy of the host's
///         <c>~/.claude/</c> (the CLI creates <c>session-env/</c> at runtime, so a
///         read-only mount is insufficient). The staged directory excludes
///         <c>projects</c>, <c>shell-snapshots</c>, <c>todos</c>, and <c>history</c>.
///         The host's <c>~/.claude/</c> is never mutated; the staged copy is deleted
///         on <see cref="DockerMountContext.DisposeAsync"/>.</item>
///   <item><c>~/.claude.json</c> file — a per-run staged RW copy of the host's
///         <c>~/.claude.json</c> (the live config / auth file that lives at the home
///         root, NOT inside the <c>.claude/</c> directory). Mounted as a single-file
///         bind mount at <c>/home/agent/.claude.json</c>. Without this mount the
///         Claude CLI hard-fails with "Claude configuration file not found at:
///         /home/agent/.claude.json" — even though the directory mount above is in
///         place, because the CLI's auth/config file is the sibling-of-dir, not
///         inside it. Skipped when the file does not exist on the host.</item>
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
        AddClaudeJsonMount(options, mounts, tempFiles);

        var envVars = new Dictionary<string, string>
        {
            // Prevents git from acquiring index locks on read-only operations.
            // Required because the base .git directory is mounted read-only.
            ["GIT_OPTIONAL_LOCKS"] = "0",
            // Docker --user does not reliably update HOME. Keep Claude pointed
            // at the credential mount's home directory when operators use a
            // numeric/container user override.
            ["HOME"] = ParentOfCredentialMount(
                options.CredentialMountPoint ?? DefaultCredentialMountPoint),
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

    /// <summary>
    /// Stages and mounts the host's <c>~/.claude.json</c> (the live config / auth
    /// file that lives at the home root, NOT inside <c>.claude/</c>). Skipped if
    /// the file is absent on the host (Claude CLI auto-creates it on first
    /// successful auth, so a brand-new install on this host might not have one
    /// yet — that's a host setup problem, not a mount problem).
    /// </summary>
    /// <remarks>
    /// Container path is derived from the credential mount point: if the
    /// directory mounts at <c>/home/agent/.claude</c>, the file mounts at
    /// <c>/home/agent/.claude.json</c> — sibling-of-dir, mirroring the host
    /// layout. Mounted RW because Claude CLI's auth refresh writes to it; the
    /// staged copy is the write target, so the host file is never mutated.
    /// The temp file is deleted on <see cref="DockerMountContext.DisposeAsync"/>.
    /// </remarks>
    internal void AddClaudeJsonMount(
        DockerClaudeAgentOptions options,
        List<DockerMount> mounts,
        List<string> tempFiles)
    {
        var hostJsonPath = ResolveClaudeJsonHostPath(options);
        if (hostJsonPath is null)
        {
            logger.LogWarning(
                "No host ~/.claude.json found — Claude CLI will fail inside the container with " +
                "'Claude configuration file not found'. Run `claude` once on the host to authenticate, " +
                "or set DockerAgents:Claude:CredentialPath to a directory whose sibling .claude.json " +
                "exists.");
            return;
        }

        var credMountPoint = options.CredentialMountPoint ?? DefaultCredentialMountPoint;
        var containerJsonPath = DeriveClaudeJsonContainerPath(credMountPoint);

        var stagedJsonPath = Path.Combine(
            Path.GetTempPath(),
            $"aiboard-claude-json-{Guid.NewGuid():N}.json");
        try
        {
            File.Copy(hostJsonPath, stagedJsonPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to stage ~/.claude.json from {Host}; container CLI will fail to authenticate",
                hostJsonPath);
            return;
        }
        tempFiles.Add(stagedJsonPath);

        mounts.Add(new DockerMount
        {
            HostPath = NormalizeHostPath(stagedJsonPath),
            ContainerPath = containerJsonPath,
            ReadOnly = false,
        });

        logger.LogDebug(
            "Claude config file mount (staged RW copy): {HostJson} → {Staged} → {ContainerJson}",
            hostJsonPath, stagedJsonPath, containerJsonPath);
    }

    /// <summary>
    /// Resolves the host path of <c>.claude.json</c>.
    /// <list type="bullet">
    ///   <item>When <see cref="DockerClaudeAgentOptions.CredentialPath"/> is set,
    ///         only the sibling of that explicit directory is considered. This
    ///         keeps the credential identity coherent: an operator pointing at
    ///         a non-default <c>~/.claude/</c> shouldn't get the live <c>.claude.json</c>
    ///         from <c>$HOME</c> mixed in.</item>
    ///   <item>When <see cref="DockerClaudeAgentOptions.CredentialPath"/> is null
    ///         (auto-detect mode), <c>$HOME/.claude.json</c> is the candidate.</item>
    /// </list>
    /// Returns null when no candidate exists or the file is not present on disk.
    /// </summary>
    internal static string? ResolveClaudeJsonHostPath(DockerClaudeAgentOptions options)
    {
        if (!string.IsNullOrEmpty(options.CredentialPath))
        {
            var parent = Directory.GetParent(options.CredentialPath)?.FullName;
            if (string.IsNullOrEmpty(parent)) return null;
            var candidate = Path.Combine(parent, ".claude.json");
            return File.Exists(candidate) ? candidate : null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var defaultCandidate = Path.Combine(home, ".claude.json");
        return File.Exists(defaultCandidate) ? defaultCandidate : null;
    }

    /// <summary>
    /// Derives the container path for <c>.claude.json</c> from the credential
    /// directory mount point. Mirrors the host layout: a directory mount at
    /// <c>/home/agent/.claude</c> implies a sibling file at
    /// <c>/home/agent/.claude.json</c>. Always uses forward-slash paths
    /// (Linux container).
    /// </summary>
    internal static string DeriveClaudeJsonContainerPath(string credMountPoint)
    {
        // Treat as POSIX path. Strip trailing slashes so "/home/agent/.claude/"
        // and "/home/agent/.claude" both resolve to "/home/agent/.claude.json".
        var trimmed = credMountPoint.TrimEnd('/');
        return trimmed + ".json";
    }

    internal static string ParentOfCredentialMount(string credentialMountPoint)
    {
        var trimmed = credentialMountPoint.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
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
