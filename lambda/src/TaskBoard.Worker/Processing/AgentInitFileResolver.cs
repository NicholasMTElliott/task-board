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
    /// at the expected name pointing at the sibling.
    /// </summary>
    public static void EnsureInitFile(
        string workspacePath,
        string providerKey,
        ILogger logger)
    {
        if (!ProviderInitFiles.TryGetValue(providerKey, out var expectedName))
            return;

        var expectedPath = Path.Combine(workspacePath, expectedName);

        // File.Exists follows symlinks and returns true only when the link
        // target actually resolves. A broken symlink left behind by a prior
        // run looks "missing" by File.Exists but would still cause
        // CreateSymbolicLink to throw, so reach for FileInfo.LinkTarget to
        // detect and clean up reparse-point stubs.
        if (File.Exists(expectedPath))
            return;

        var info = new FileInfo(expectedPath);
        if (info.LinkTarget is not null)
        {
            try
            {
                File.Delete(expectedPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not clean up broken symlink at {Path}; skipping init-file resolution",
                    expectedPath);
                return;
            }
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
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                try
                {
                    File.Copy(siblingPath, expectedPath, overwrite: false);
                    logger.LogDebug(ex,
                        "Symlink not permitted; copied {Sibling} → {Expected} in {Workspace} for provider {Provider}",
                        siblingName, expectedName, workspacePath, providerKey);
                }
                catch (Exception copyEx)
                {
                    logger.LogWarning(copyEx,
                        "Failed to mirror {Sibling} → {Expected} in {Workspace}; provider {Provider} will run without project init context",
                        siblingName, expectedName, workspacePath, providerKey);
                }
                return;
            }
        }
    }
}
