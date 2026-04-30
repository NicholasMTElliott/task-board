namespace TaskBoard.Worker.Processing;

/// <summary>
/// Ensures each agent provider can read a project-context init file
/// (CLAUDE.md / AGENTS.md) from the workspace, regardless of which name the
/// project repo committed. Idempotent: a no-op if the expected file is
/// already present.
/// </summary>
/// <remarks>
/// When the expected file is missing but a sibling provider's file is
/// present, mirrors it via relative symlink at the expected name. Falls
/// back to <see cref="File.Copy(string, string)"/> when symlink creation
/// is not permitted (Windows non-Developer-Mode).
///
/// Operates on the host worktree; the existing bind mount makes the result
/// visible inside Docker containers without any image or entrypoint change.
///
/// Callers are expected to <see cref="CleanupInitFile"/> the returned
/// <see cref="InitFileMirror"/> after the agent invocation so the mirror
/// doesn't get caught in the orchestrator's <c>git add . &amp;&amp; git commit</c>
/// (which would commit it to the work branch and surface it in the
/// evaluator's diff prompt as a spurious change).
/// </remarks>
public static class AgentInitFileResolver
{
    private static readonly IReadOnlyDictionary<string, string> ProviderInitFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-cli"]         = "CLAUDE.md",
            ["docker-claude-cli"]  = "CLAUDE.md",
            ["docker-claude-qwen"] = "CLAUDE.md",
            ["codex"]              = "AGENTS.md",
            ["docker-opencode"]    = "AGENTS.md",
            // "stub" intentionally absent — early return below.
        };

    private static readonly IReadOnlyList<string> AllKnownInitFiles =
        ProviderInitFiles.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// If the provider's expected init file is missing in
    /// <paramref name="workspacePath"/> but a sibling provider's init file is
    /// present, create a relative symlink (or copy on Windows non-Developer-Mode)
    /// at the expected name pointing at the sibling. Returns a token describing
    /// the mirror so the caller can clean it up after the agent run; returns
    /// <c>null</c> when no mirror was created (unknown provider, expected file
    /// already present, no sibling found, or creation failed).
    /// </summary>
    public static InitFileMirror? EnsureInitFile(
        string workspacePath,
        string providerKey,
        ILogger logger)
    {
        if (!ProviderInitFiles.TryGetValue(providerKey, out var expectedName))
            return null;

        var expectedPath = Path.Combine(workspacePath, expectedName);

        // Detect symlink-ness BEFORE the simple existence check. On Linux .NET 10,
        // `File.Exists` may return true for a broken symlink (the link entry
        // exists even though the target doesn't), which would short-circuit
        // past the cleanup path and leave a stale broken link in place — then
        // CreateSymbolicLink below would throw EEXIST. Resolve the link target
        // manually to distinguish working from broken.
        var info = new FileInfo(expectedPath);
        if (info.LinkTarget is not null)
        {
            // Resolve relative link targets against the link's parent directory.
            var resolvedTarget = Path.IsPathRooted(info.LinkTarget)
                ? info.LinkTarget
                : Path.Combine(workspacePath, info.LinkTarget);
            if (File.Exists(resolvedTarget))
                return null; // working symlink (operator-managed or our own from a prior run)

            // Broken symlink — clean it up so CreateSymbolicLink doesn't throw.
            try
            {
                File.Delete(expectedPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not clean up broken symlink at {Path}; skipping init-file resolution",
                    expectedPath);
                return null;
            }
        }
        else if (File.Exists(expectedPath))
        {
            // Real file at the expected name — operator's own copy or
            // committed file. Leave it alone.
            return null;
        }

        foreach (var siblingName in AllKnownInitFiles)
        {
            if (string.Equals(siblingName, expectedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var siblingPath = Path.Combine(workspacePath, siblingName);
            if (!File.Exists(siblingPath))
                continue;

            try
            {
                // Relative target so the link resolves correctly across the
                // host-worktree → /workspace bind mount inside Docker.
                File.CreateSymbolicLink(expectedPath, siblingName);
                logger.LogInformation(
                    "Symlinked {Expected} → {Sibling} in {Workspace} for provider {Provider}",
                    expectedName, siblingName, workspacePath, providerKey);
                return new InitFileMirror(expectedPath, siblingPath, InitFileMirrorKind.Symlink);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                try
                {
                    File.Copy(siblingPath, expectedPath, overwrite: false);
                    logger.LogDebug(ex,
                        "Symlink not permitted; copied {Sibling} → {Expected} in {Workspace} for provider {Provider}",
                        siblingName, expectedName, workspacePath, providerKey);
                    return new InitFileMirror(expectedPath, siblingPath, InitFileMirrorKind.Copy);
                }
                catch (Exception copyEx)
                {
                    logger.LogWarning(copyEx,
                        "Failed to mirror {Sibling} → {Expected} in {Workspace}; provider {Provider} will run without project init context",
                        siblingName, expectedName, workspacePath, providerKey);
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Removes the mirror file created by <see cref="EnsureInitFile"/> so it
    /// doesn't get picked up by the orchestrator's <c>git add . &amp;&amp; git commit</c>
    /// or surface as a spurious entry in the evaluator's diff. Symlinks are
    /// always safe to delete (the link, not its target). Copies are deleted
    /// only when their content still matches the sibling — if the agent
    /// modified the file during its run, the modified copy is preserved and
    /// the operator is warned.
    /// </summary>
    public static void CleanupInitFile(InitFileMirror? mirror, ILogger logger)
    {
        if (mirror is null) return;

        try
        {
            var info = new FileInfo(mirror.MirrorPath);
            if (!info.Exists && info.LinkTarget is null)
            {
                // Already gone (agent or another caller cleaned up first).
                return;
            }

            if (mirror.Kind == InitFileMirrorKind.Symlink)
            {
                if (info.LinkTarget is null)
                {
                    // Was a symlink at create time, now isn't — the agent
                    // replaced it with a real file. Don't delete arbitrary
                    // content; leave it for the operator to inspect.
                    logger.LogDebug(
                        "Init file mirror at {Path} is no longer a symlink; leaving as-is",
                        mirror.MirrorPath);
                    return;
                }
                File.Delete(mirror.MirrorPath);
                logger.LogDebug("Cleaned up init file symlink at {Path}", mirror.MirrorPath);
                return;
            }

            // Copy fallback: only delete when the mirror is still byte-equal
            // to the sibling. Anything else means the agent edited the
            // mirror in place, and dropping its work would be surprising.
            if (!File.Exists(mirror.SiblingPath)
                || !FilesAreIdentical(mirror.MirrorPath, mirror.SiblingPath))
            {
                logger.LogWarning(
                    "Init file mirror at {Path} differs from sibling {Sibling}; " +
                    "leaving as-is so any agent-authored changes aren't lost",
                    mirror.MirrorPath, mirror.SiblingPath);
                return;
            }
            File.Delete(mirror.MirrorPath);
            logger.LogDebug("Cleaned up init file copy at {Path}", mirror.MirrorPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to clean up init file mirror at {Path}",
                mirror.MirrorPath);
        }
    }

    private static bool FilesAreIdentical(string a, string b)
    {
        var ai = new FileInfo(a);
        var bi = new FileInfo(b);
        if (ai.Length != bi.Length) return false;

        using var sa = ai.OpenRead();
        using var sb = bi.OpenRead();
        Span<byte> ba = stackalloc byte[4096];
        Span<byte> bb = stackalloc byte[4096];
        while (true)
        {
            var ra = sa.Read(ba);
            var rb = sb.Read(bb);
            if (ra != rb) return false;
            if (ra == 0) return true;
            if (!ba[..ra].SequenceEqual(bb[..rb])) return false;
        }
    }
}

/// <summary>
/// Token returned by <see cref="AgentInitFileResolver.EnsureInitFile"/>
/// describing a mirror file the resolver created. Pass back to
/// <see cref="AgentInitFileResolver.CleanupInitFile"/> after the agent run
/// to remove the mirror.
/// </summary>
public sealed record InitFileMirror(
    string MirrorPath,
    string SiblingPath,
    InitFileMirrorKind Kind);

public enum InitFileMirrorKind
{
    Symlink,
    Copy,
}
