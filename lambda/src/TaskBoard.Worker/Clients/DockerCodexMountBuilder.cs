namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mount specifications for running the Codex CLI
/// inside a container. Extends <see cref="DockerMountBuilderBase"/> with
/// Codex-specific credential staging.
/// </summary>
/// <remarks>
/// Produces three shared bind mounts (worktree RW, base <c>.git</c> RO, <c>.git</c>
/// file override RO) plus one read-only file mount per allowlisted credential
/// file in the staged copy of <c>~/.codex/</c>.
/// <para>
/// Per-file mounts (rather than a single dir-level mount at <c>/home/agent/.codex</c>)
/// are required for compatibility with Docker Desktop on Windows + WSL2. A bind-mounted
/// Windows-temp directory's effective permissions inside the container don't allow
/// the non-root <c>agent</c> user (UID 1000) to <c>mkdir sessions/</c> inside it
/// (EPERM via gRPC FUSE / virtiofs). Mounting individual files into the agent-owned
/// image-baked <c>/home/agent/.codex/</c> directory (created in the Dockerfile)
/// keeps the directory itself writable by the agent user, so Codex CLI's runtime
/// creation of <c>sessions/</c> and <c>log/</c> succeeds.
/// </para>
/// <para>
/// Mounted files are constrained by an explicit allowlist (<see cref="CredentialFileNames"/>)
/// rather than "every top-level file." Codex CLI 0.125.0+ writes runtime state to
/// several files in <c>~/.codex/</c> at startup (<c>models_cache.json</c>, the
/// <c>state_*.sqlite*</c> session DB, the <c>logs_*.sqlite*</c> logging DB,
/// <c>sandbox.log</c>, <c>history.jsonl</c>). Mounting those RO blocked startup
/// with "Read-only file system (os error 30)" before any agent work began;
/// mounting them RW would re-trigger the v0.0.22 dir-perms class of bug.
/// The fix is to skip them entirely — Codex regenerates fresh copies in the
/// agent-writable container directory on every run. Trade-off: ~1s extra on
/// first API call (no models_cache priming) and no command-history carryover
/// across runs (which is the right behaviour for an isolated agent anyway).
/// Privacy bonus: the operator's host-side <c>history.jsonl</c> is no longer
/// exposed to every agent.
/// </para>
/// <para>
/// Trade-off on credential mounts: Codex's runtime token refresh writes to
/// <c>auth.json</c> are silently no-op'd by the read-only mount. Functionally
/// equivalent to the prior dir-level behaviour, since the staged copy was
/// destroyed on <see cref="DockerMountContext.DisposeAsync"/> regardless —
/// refreshed tokens never reached the host's <c>~/.codex/auth.json</c>.
/// Operators re-authenticate via <c>codex login</c> on the host as before.
/// </para>
/// <para>Heavy subdirectories (<c>sessions</c>, <c>log</c>, <c>screenshots</c>)
/// are excluded from the copy. Subdirs that survive the copy are NOT mounted —
/// only top-level allowlisted files are mounted, by design, so we never
/// re-introduce the dir-level permissions issue.</para>
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
        CopyDirectoryRecursive(credPath, stagedCredDir, CredentialCopyExcludes, CredentialFileNames);
        tempDirs.Add(stagedCredDir);

        // Mount each top-level file from the staged copy as a read-only file
        // mount into the agent-owned credential directory. The directory
        // itself is image-baked (Dockerfile mkdir + chown agent:agent), so
        // file-level mounts overlay on it without changing its permissions.
        // Subdirs are intentionally NOT mounted — re-introducing a dir-level
        // mount would re-trigger the bind-mount perms issue this design avoids.
        var fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(stagedCredDir))
        {
            var name = Path.GetFileName(file);
            // Use forward slash for the container path even on Windows hosts;
            // ContainerPath is a Linux path and Path.Combine would emit backslashes.
            mounts.Add(new DockerMount
            {
                HostPath = NormalizeHostPath(file),
                ContainerPath = $"{credMountPoint}/{name}",
                ReadOnly = true,
            });
            fileCount++;
        }

        if (fileCount == 0)
        {
            logger.LogWarning(
                "Codex credential staging produced no files (staged from '{CredPath}'). " +
                "Codex CLI may not authenticate inside the container.",
                credPath);
            return;
        }

        logger.LogDebug(
            "Codex credential mounts (per-file RO): {CredPath} → {Staged} → {MountPoint} ({FileCount} file(s))",
            credPath, stagedCredDir, credMountPoint, fileCount);
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

    /// <summary>
    /// Allowlist of top-level filenames inside <c>~/.codex/</c> that are
    /// staged and mounted into the container. Anything else (including the
    /// runtime-state files Codex CLI 0.125.0+ writes at startup —
    /// <c>models_cache.json</c>, <c>state_*.sqlite*</c>, <c>logs_*.sqlite*</c>,
    /// <c>sandbox.log</c>, <c>history.jsonl</c>) is skipped, so Codex
    /// regenerates fresh copies in the agent-writable container directory
    /// rather than failing on a read-only mount.
    /// </summary>
    internal static readonly HashSet<string> CredentialFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth.json",            // OAuth tokens — required for API calls
        "config.toml",          // CLI config (model preferences, etc.)
        "cap_sid",              // capability/session ID — auth-related
        "installation_id",      // stable per-installation UUID — keep for telemetry continuity
        "version.json",         // last-checked CLI version metadata
        ".personality_migration", // one-time migration flag
    };

    private static void CopyDirectoryRecursive(
        string source,
        string dest,
        HashSet<string> excludeTopLevelDirs,
        HashSet<string> includeTopLevelFiles)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!includeTopLevelFiles.Contains(name)) continue;
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
