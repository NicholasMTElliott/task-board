using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TaskBoard.Worker.Processing;

/// <summary>Mode of a <see cref="DiffPacket"/> — Empty (no changes), Full (raw diff fits within threshold), or Summary (structured large-diff packet).</summary>
public enum DiffMode
{
    Empty,
    Full,
    Summary,
}

/// <summary>
/// Result of <see cref="GitWorkspaceManager.GetDiffPacketAsync"/>: either the
/// raw diff (Full mode) or a structured large-diff summary (Summary mode).
/// <see cref="Content"/> is ready to drop into a gate prompt's <c>{Diff}</c>
/// placeholder either way.
/// </summary>
public sealed record DiffPacket(
    DiffMode Mode,
    string Content,
    int RawByteSize,
    int FilesChanged,
    int FilesIncludedInline);

/// <summary>Per-file metadata used when building <see cref="DiffMode.Summary"/> packets.</summary>
internal sealed record DiffFileEntry(
    string Path,
    char Status,
    int Added,
    int Deleted,
    bool IsBinary,
    bool IsUntracked);

public sealed class GitWorkspaceManager(
    ILogger<GitWorkspaceManager> logger,
    string? worktreeBasePath = null,
    int gitTimeoutSeconds = 30)
{
    /// <summary>Timeout for local git operations (worktree, merge, commit).</summary>
    private readonly int _localTimeout = gitTimeoutSeconds;

    /// <summary>Timeout for network git operations (fetch, push, pull). 2x local timeout.</summary>
    private readonly int _networkTimeout = gitTimeoutSeconds * 2;

    // ── Worktree methods ──────────────────────────────────────────────

    internal static string GetWorktreePath(string repoPath, string branchName, string? worktreeBase = null)
    {
        var basePath = worktreeBase ?? (repoPath + "-worktrees");
        return Path.Combine(basePath, branchName.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Resolves the worktree path using the configured <c>worktreeBasePath</c>.
    /// Use this instead of the static <see cref="GetWorktreePath"/> when the configured base matters.
    /// </summary>
    public string ResolveWorktreePath(string repoPath, string branchName)
        => GetWorktreePath(repoPath, branchName, worktreeBasePath);

    public async Task<string> CreateWorktreeAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        var worktreePath = GetWorktreePath(repoPath, branchName, worktreeBasePath);
        var fullWorktreePath = Path.GetFullPath(worktreePath);

        // If worktree already registered, reuse it
        if (await WorktreeExistsAsync(repoPath, fullWorktreePath, cancellationToken))
        {
            logger.LogInformation("Reusing existing worktree at {Path} for {Branch}", fullWorktreePath, branchName);
            return fullWorktreePath;
        }

        // If directory exists but isn't a registered worktree, clean it up
        if (Directory.Exists(fullWorktreePath))
        {
            logger.LogWarning("Stale worktree directory at {Path} — removing and pruning", fullWorktreePath);
            Directory.Delete(fullWorktreePath, recursive: true);
            try { await RunGitAsync(repoPath, ["worktree", "prune"], cancellationToken); }
            catch (GitOperationException) { /* best effort */ }
        }

        // Ensure parent directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(fullWorktreePath)!);

        var branchExistsLocally = await BranchExistsAsync(repoPath, branchName, cancellationToken);
        var branchExistsOnRemote = !branchExistsLocally
            && await RemoteBranchExistsAsync(repoPath, branchName, cancellationToken);

        if (branchExistsLocally)
        {
            logger.LogInformation("Creating worktree at {Path} for existing local branch {Branch}", fullWorktreePath, branchName);
            await RunGitAsync(repoPath, ["worktree", "add", fullWorktreePath, branchName], cancellationToken);
        }
        else if (branchExistsOnRemote)
        {
            // Create local tracking branch from remote in one step
            logger.LogInformation("Creating worktree at {Path} tracking remote branch {Branch}", fullWorktreePath, branchName);
            await RunGitAsync(repoPath,
                ["worktree", "add", fullWorktreePath, "-b", branchName, $"origin/{branchName}"], cancellationToken);
        }
        else
        {
            logger.LogInformation("Creating worktree at {Path} with new branch {Branch}", fullWorktreePath, branchName);
            await RunGitAsync(repoPath, ["worktree", "add", fullWorktreePath, "-b", branchName], cancellationToken);
        }

        return fullWorktreePath;
    }

    /// <summary>
    /// Creates a new worktree on a brand-new branch that starts at <paramref name="startPoint"/>.
    /// <paramref name="startPoint"/> can be a branch name, a tag, or a commit SHA. Used by the
    /// candidate-evaluation flow to spawn N parallel worktrees off a canonical branch's HEAD.
    /// </summary>
    /// <remarks>
    /// Differs from <see cref="CreateWorktreeAsync"/> in two ways:
    /// <list type="bullet">
    ///   <item>Always creates a new branch — does not reuse an existing one.</item>
    ///   <item>Uses an explicit start point so the new branch starts at the right commit
    ///         instead of inheriting the base repo's current HEAD.</item>
    /// </list>
    /// Throws <see cref="GitOperationException"/> if a branch with <paramref name="newBranchName"/>
    /// already exists, since reusing a candidate branch across runs would silently merge stale
    /// state into the new candidate.
    /// </remarks>
    public async Task<string> CreateWorktreeFromStartPointAsync(
        string repoPath, string newBranchName, string startPoint, CancellationToken cancellationToken)
    {
        var worktreePath = GetWorktreePath(repoPath, newBranchName, worktreeBasePath);
        var fullWorktreePath = Path.GetFullPath(worktreePath);

        if (await BranchExistsAsync(repoPath, newBranchName, cancellationToken))
        {
            throw new GitOperationException(
                $"Cannot create candidate worktree: branch '{newBranchName}' already exists. " +
                "Candidate branch names must be unique per group; the runner generates them with " +
                "a per-run UUID, so a collision indicates leftover state from a prior crash.", -1);
        }

        if (Directory.Exists(fullWorktreePath))
        {
            logger.LogWarning(
                "Stale candidate worktree directory at {Path} — removing and pruning",
                fullWorktreePath);
            Directory.Delete(fullWorktreePath, recursive: true);
            try { await RunGitAsync(repoPath, ["worktree", "prune"], cancellationToken); }
            catch (GitOperationException) { /* best effort */ }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullWorktreePath)!);

        logger.LogInformation(
            "Creating candidate worktree at {Path} on new branch {Branch} from start point {Start}",
            fullWorktreePath, newBranchName, startPoint);
        await RunGitAsync(repoPath,
            ["worktree", "add", fullWorktreePath, "-b", newBranchName, startPoint],
            cancellationToken);

        return fullWorktreePath;
    }

    /// <summary>
    /// Resets the worktree at <paramref name="worktreePath"/> to the HEAD of
    /// <paramref name="sourceBranch"/> with <c>git reset --hard</c>. Used by the
    /// candidate-evaluation flow to promote the winner's branch into the canonical
    /// worktree before subsequent steps run.
    /// </summary>
    /// <remarks>
    /// Destructive: any uncommitted changes in <paramref name="worktreePath"/> are
    /// discarded. The caller must have already moved the candidate's commits onto
    /// <paramref name="sourceBranch"/> (i.e. the candidate ran with
    /// <c>commit_only</c> or <c>commit_and_push</c>).
    /// </remarks>
    public async Task ResetWorktreeToBranchAsync(
        string worktreePath, string sourceBranch, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Resetting worktree {Worktree} to branch {Branch}", worktreePath, sourceBranch);
        await RunGitAsync(worktreePath, ["reset", "--hard", sourceBranch], cancellationToken);
    }

    /// <summary>
    /// Best-effort cleanup of a worktree and (optionally) its branch.
    /// Never throws — every failure is logged at Warning and the next step is
    /// still attempted. Used by the candidate-evaluation cleanup paths where
    /// leaving a stale branch or worktree behind is preferable to aborting the
    /// run, but we want to do everything we can to remove leftover state.
    /// </summary>
    /// <remarks>
    /// Order of operations (each step independent, all best-effort):
    /// <list type="number">
    ///   <item><c>git worktree remove --force</c> the registered worktree.</item>
    ///   <item>If the directory still exists on disk (Windows file lock,
    ///         antivirus, container handle held briefly post-exit), try to
    ///         <c>Directory.Delete(recursive: true)</c> it. This may fail with
    ///         <c>IOException</c>/<c>UnauthorizedAccessException</c>; logged.</item>
    ///   <item><c>git worktree prune</c> to drop any stale registration left by
    ///         a partial removal — runs unconditionally so a registered-but-
    ///         orphaned worktree gets cleared even when (1) and (2) couldn't
    ///         delete the directory.</item>
    ///   <item>If <paramref name="deleteBranch"/> is true,
    ///         <c>git branch -D</c> the branch. Independent of worktree-removal
    ///         success; failure is logged but does not propagate.</item>
    /// </list>
    /// <see cref="OperationCanceledException"/> still propagates so Ctrl+C
    /// shutdown is honoured.
    /// </remarks>
    public async Task RemoveWorktreeAsync(
        string repoPath, string branchName, bool deleteBranch, CancellationToken cancellationToken)
    {
        var worktreePath = Path.GetFullPath(GetWorktreePath(repoPath, branchName, worktreeBasePath));

        logger.LogInformation("Removing worktree at {Path}", worktreePath);

        // (1) git worktree remove --force.
        // Logged at Debug rather than Warning: the candidate-cleanup loop
        // routinely calls this for predicted branches whose worktrees were
        // never created (slot threw mid-flight), so a "not a working tree"
        // failure here is expected noise. The directory-delete + prune
        // fallbacks below cover the genuinely-stale cases, and step (3)
        // unconditional prune surfaces the registration list correctly
        // either way.
        try
        {
            await RunGitAsync(repoPath, ["worktree", "remove", worktreePath, "--force"], cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git worktree remove failed for {Path} — may not exist; continuing with directory + prune fallbacks", worktreePath);
        }

        // (2) Directory.Delete fallback if the dir is still on disk
        if (Directory.Exists(worktreePath))
        {
            try
            {
                Directory.Delete(worktreePath, recursive: true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Directory.Delete failed for {Path} (file may be locked by container, antivirus, or another process) — leaving on disk; will retry prune",
                    worktreePath);
            }
        }

        // (3) git worktree prune — always runs, even if (1) and (2) failed.
        // Clears stale worktree registrations so subsequent runs don't trip
        // over "directory exists" / "branch in use" errors.
        try
        {
            await RunGitAsync(repoPath, ["worktree", "prune"], cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "git worktree prune failed for {Repo}", repoPath);
        }

        // (4) Branch deletion is independent of worktree removal. A worktree
        // that couldn't be removed still has its branch checked out, so
        // git branch -D will fail; that's logged and we move on.
        if (deleteBranch)
        {
            try
            {
                await RunGitAsync(repoPath, ["branch", "-D", branchName], cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete branch {Branch}", branchName);
            }
        }
    }

    /// <summary>
    /// Finds an existing branch matching the card ID prefix pattern.
    /// Checks local branches first, then fetches and checks remote branches.
    /// Returns the branch name if found, null otherwise.
    /// </summary>
    public async Task<string?> FindBranchByPrefixAsync(
        string repoPath, string cardId, CancellationToken cancellationToken)
    {
        // Check local slugged branch: aiboard/{cardId}-*
        // Trailing hyphen prevents aiboard/2-* matching aiboard/20-*
        var localBranch = await FindLocalBranchAsync(repoPath, cardId, cancellationToken);
        if (localBranch is not null)
            return localBranch;

        // Fetch remote refs and check for remote-only branches
        var remoteBranch = await FindRemoteBranchAsync(repoPath, cardId, cancellationToken);
        if (remoteBranch is not null)
        {
            logger.LogInformation("Found remote-only branch {Branch} for card {CardId}", remoteBranch, cardId);
            return remoteBranch;
        }

        return null;
    }

    private async Task<string?> FindLocalBranchAsync(
        string repoPath, string cardId, CancellationToken cancellationToken)
    {
        var (_, sluggedOutput, _) = await RunGitAsync(
            repoPath, ["branch", "--list", $"aiboard/{cardId}-*"], cancellationToken);

        var sluggedBranch = sluggedOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('*', '+', ' ', '\r', '\t'))
            .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));

        if (sluggedBranch is not null)
            return sluggedBranch;

        // Check for legacy exact branch: aiboard/{cardId}
        if (await BranchExistsAsync(repoPath, $"aiboard/{cardId}", cancellationToken))
            return $"aiboard/{cardId}";

        return null;
    }

    private async Task<string?> FindRemoteBranchAsync(
        string repoPath, string cardId, CancellationToken cancellationToken)
    {
        // Fetch latest remote refs
        try
        {
            await RunGitAsync(repoPath, ["fetch", "origin"], cancellationToken, timeoutSeconds: _networkTimeout);
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "Failed to fetch from origin — skipping remote branch check");
            return null;
        }

        // Check for remote slugged branch: origin/aiboard/{cardId}-*
        var (_, remoteOutput, _) = await RunGitAsync(
            repoPath, ["branch", "-r", "--list", $"origin/aiboard/{cardId}-*"], cancellationToken);

        var remoteBranch = remoteOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('*', '+', ' ', '\r', '\t'))
            .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));

        if (remoteBranch is not null)
        {
            // Strip "origin/" prefix to return the local branch name
            return remoteBranch.StartsWith("origin/", StringComparison.Ordinal)
                ? remoteBranch["origin/".Length..]
                : remoteBranch;
        }

        return null;
    }

    internal async Task<bool> WorktreeExistsAsync(
        string repoPath, string worktreePath, CancellationToken cancellationToken)
    {
        var (_, stdout, _) = await RunGitAsync(repoPath, ["worktree", "list", "--porcelain"], cancellationToken);
        var normalizedTarget = Path.GetFullPath(worktreePath);

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                var path = Path.GetFullPath(line["worktree ".Length..].Trim());
                if (string.Equals(path, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    internal async Task<bool> BranchExistsAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        var (_, stdout, _) = await RunGitAsync(repoPath, ["branch", "--list", branchName], cancellationToken);
        return !string.IsNullOrWhiteSpace(stdout);
    }

    internal async Task<bool> RemoteBranchExistsAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        var (_, stdout, _) = await RunGitAsync(
            repoPath, ["branch", "-r", "--list", $"origin/{branchName}"], cancellationToken);
        return !string.IsNullOrWhiteSpace(stdout);
    }

    // ── Merge-related operations ────────────────────────────────────

    public async Task FetchAsync(string repoPath, CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching from origin in {Repo}", repoPath);
        await RunGitAsync(repoPath, ["fetch", "origin"], cancellationToken, timeoutSeconds: _networkTimeout);
    }

    /// <summary>
    /// Pulls the work branch from remote (fast-forward only). Failures are logged and swallowed.
    /// </summary>
    public async Task PullWorkBranchAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        try
        {
            await RunGitAsync(repoPath,
                ["pull", "--ff-only", "origin", branchName], cancellationToken, timeoutSeconds: _networkTimeout);
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "Pull failed for {Branch} — continuing with local state", branchName);
        }
    }

    /// <summary>
    /// Merges origin/{defaultBranch} into the current branch using --no-ff --no-commit.
    /// Returns the merge result without committing, allowing the caller to inspect and decide.
    /// </summary>
    public async Task<MergeMainResult> MergeMainBranchAsync(
        string repoPath, string defaultBranch, CancellationToken cancellationToken)
    {
        // Count commits to merge (for summary)
        var commitCount = 0;
        try
        {
            var (_, logOutput, _) = await RunGitAsync(repoPath,
                ["log", "--oneline", $"HEAD..origin/{defaultBranch}"], cancellationToken);
            commitCount = logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch (GitOperationException) { /* best effort */ }

        // Attempt merge
        var hasConflicts = false;
        try
        {
            var (_, stdout, _) = await RunGitAsync(repoPath,
                ["merge", $"origin/{defaultBranch}", "--no-ff", "--no-commit"], cancellationToken);

            if (stdout.Contains("Already up to date"))
                return new MergeMainResult(MergeMainStatus.UpToDate, defaultBranch, [], [], 0, "Already up to date");
        }
        catch (GitOperationException ex) when (ex.ExitCode == 1)
        {
            hasConflicts = true;
        }

        // Gather file lists
        var (_, cachedOutput, _) = await RunGitAsync(repoPath,
            ["diff", "--cached", "--name-only"], cancellationToken);
        var changedFiles = cachedOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToList();

        var conflictFiles = new List<string>();
        if (hasConflicts)
        {
            var (_, conflictOutput, _) = await RunGitAsync(repoPath,
                ["diff", "--name-only", "--diff-filter=U"], cancellationToken);
            conflictFiles = conflictOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .ToList();
        }

        var status = hasConflicts ? MergeMainStatus.Conflicts : MergeMainStatus.Merged;
        var summary = hasConflicts
            ? $"{conflictFiles.Count} conflict(s) in {changedFiles.Count} changed files"
            : $"{changedFiles.Count} files changed, {commitCount} commit(s) merged";

        return new MergeMainResult(status, defaultBranch, changedFiles, conflictFiles, commitCount, summary);
    }

    /// <summary>
    /// Commits a staged merge with a descriptive message.
    /// </summary>
    public async Task CommitMergeAsync(
        string repoPath, string defaultBranch, string branchName, CancellationToken cancellationToken)
    {
        await RunGitAsync(repoPath, ["add", "."], cancellationToken);
        await RunGitAsync(repoPath,
            ["commit", "-m", $"Merge origin/{defaultBranch} into {branchName}"], cancellationToken);
    }

    public async Task<string> GetDefaultBranchAsync(
        string repoPath,
        CancellationToken cancellationToken,
        string? configuredOverride = null)
    {
        // Rerun redesign Finding 10: honour the workflow-config override
        // (rerun.defaultBranch) when set. Operators can pin the default branch
        // explicitly; we still validate it actually exists on origin so a
        // typo fails loudly rather than silently picking up a stale ref.
        if (!string.IsNullOrWhiteSpace(configuredOverride))
        {
            if (await RemoteBranchExistsAsync(repoPath, configuredOverride, cancellationToken))
                return configuredOverride;
            throw new GitOperationException(
                $"Configured rerun.defaultBranch '{configuredOverride}' was not found on origin. "
                + "Either run `git remote set-head origin --auto` to clear the override or "
                + "fix the value in workflow.json's `rerun.defaultBranch`.", -1);
        }

        // Try symbolic-ref first (canonical path: origin/HEAD is configured).
        try
        {
            var (_, stdout, _) = await RunGitAsync(repoPath,
                ["symbolic-ref", "refs/remotes/origin/HEAD"], cancellationToken);
            var refPath = stdout.Trim();
            var branchName = refPath.Replace("refs/remotes/origin/", "");
            if (!string.IsNullOrWhiteSpace(branchName))
                return branchName;
        }
        catch (GitOperationException) { /* fallback to probing */ }

        // Origin/HEAD missing: hard-fail with operator hint per rerun-redesign
        // Finding 10. We accept "must have origin/HEAD configured OR set
        // rerun.defaultBranch" as a setup requirement — the previous
        // best-effort probing of main/master/mainline silently masked
        // misconfigurations.
        throw new GitOperationException(
            "Could not determine default branch: origin/HEAD is not configured and "
            + "rerun.defaultBranch is not set. Run `git remote set-head origin --auto` "
            + "or set `rerun.defaultBranch` in workflow.json to fix.", -1);
    }

    public async Task<bool> IsAncestorAsync(
        string repoPath, string potentialAncestor, string descendant,
        CancellationToken cancellationToken)
    {
        try
        {
            // exit 0 = is ancestor, exit 1 = is not ancestor
            await RunGitAsync(repoPath,
                ["merge-base", "--is-ancestor", $"origin/{potentialAncestor}", $"origin/{descendant}"],
                cancellationToken);
            return true;
        }
        catch (GitOperationException ex) when (ex.ExitCode == 1)
        {
            return false;
        }
        catch (GitOperationException ex)
        {
            // Unexpected exit code (e.g., 128 for invalid ref, network error, corrupted repo).
            // Safe default: treat as "not ancestor" so the caller doesn't assume the merge happened.
            logger.LogWarning(ex,
                "IsAncestorAsync: unexpected git exit code {ExitCode} for {Ancestor} -> {Descendant}",
                ex.ExitCode, potentialAncestor, descendant);
            return false;
        }
    }

    public async Task DeleteRemoteBranchAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting remote branch {Branch}", branchName);
        await RunGitAsync(repoPath,
            ["push", "origin", "--delete", branchName], cancellationToken, timeoutSeconds: _networkTimeout);
    }

    public async Task<MergeStatus> MergeNoFfAsync(
        string repoPath, string sourceBranch, string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var (_, stdout, _) = await RunGitAsync(repoPath,
                ["merge", "--no-ff", sourceBranch, "-m", message], cancellationToken);

            if (stdout.Contains("Already up to date"))
                return MergeStatus.AlreadyUpToDate;

            return MergeStatus.Merged;
        }
        catch (GitOperationException ex) when (ex.ExitCode == 1)
        {
            return MergeStatus.Conflict;
        }
    }

    public async Task AbortMergeAsync(string repoPath, CancellationToken cancellationToken)
    {
        await RunGitAsync(repoPath, ["merge", "--abort"], cancellationToken);
    }

    public async Task<PushStatus> PushRefAsync(
        string repoPath, string localRef, string remoteRef,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunGitAsync(repoPath,
                ["push", "origin", $"{localRef}:{remoteRef}"], cancellationToken, timeoutSeconds: _networkTimeout);
            return PushStatus.Success;
        }
        catch (GitOperationException ex) when (
            ex.Message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
        {
            return PushStatus.NonFastForward;
        }
        catch (GitOperationException ex)
        {
            // Non-retryable push error (auth, repo not found, network, etc.)
            // Log and rethrow so the caller (MergeRunner) can handle it as a fatal error.
            logger.LogError(ex, "Push failed with unexpected error for {LocalRef} -> {RemoteRef}", localRef, remoteRef);
            throw;
        }
    }

    // ── Diff summary ─────────────────────────────────────────────────

    /// <summary>
    /// Captures a diff summary of all changes in the worktree relative to a
    /// base ref. <paramref name="baseRef"/> defaults to <c>HEAD</c>, which
    /// captures only uncommitted working-tree changes — appropriate for the
    /// single-agent flow where the orchestrator hasn't committed yet.
    ///
    /// For candidate-evaluation flows, pass the canonical SHA captured before
    /// the candidates ran: each candidate has already been committed onto its
    /// own branch by the time the evaluator's prompt is built, so
    /// <c>git diff HEAD</c> in the candidate worktree returns empty even
    /// though real work was committed. <c>git diff {canonicalSha}</c>
    /// captures the candidate's full divergence (committed + uncommitted).
    ///
    /// Same logic applies to gate checks running after a candidate-group
    /// step: post-promotion <c>git reset --hard</c> resets working tree to
    /// HEAD, so <c>git diff HEAD</c> on canonical returns empty. Pass the
    /// run-start canonical SHA to see the cumulative work of the run.
    /// </summary>
    public async Task<string> GetDiffSummaryAsync(
        string repoPath, int maxChars = 50_000,
        CancellationToken cancellationToken = default,
        string? baseRef = null)
    {
        var sb = new StringBuilder();
        var effectiveBase = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef;

        // 1. Tracked changes (modifications and deletions) relative to base ref
        try
        {
            var (_, diffOutput, _) = await RunGitAsync(repoPath, ["diff", effectiveBase], cancellationToken);
            if (!string.IsNullOrWhiteSpace(diffOutput))
                sb.Append(diffOutput);
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git diff {BaseRef} failed in {Repo}", effectiveBase, repoPath);
        }

        // 2. New untracked files (not captured by git diff HEAD)
        try
        {
            var (_, lsOutput, _) = await RunGitAsync(repoPath,
                ["ls-files", "--others", "--exclude-standard"], cancellationToken);

            var untrackedFiles = lsOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();

            foreach (var file in untrackedFiles)
            {
                if (sb.Length >= maxChars)
                    break;

                sb.AppendLine();
                sb.AppendLine($"--- /dev/null");
                sb.AppendLine($"+++ b/{file}");
                sb.AppendLine("@@ -0,0 +1 @@");

                var fullPath = Path.Combine(repoPath, file);
                if (File.Exists(fullPath))
                {
                    const int perFileLimit = 10_000;
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
                        if (content.Length > perFileLimit)
                            content = content[..perFileLimit] + "\n... (file truncated)";

                        foreach (var line in content.Split('\n'))
                            sb.AppendLine($"+{line}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"+[could not read file: {ex.Message}]");
                    }
                }
            }
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git ls-files failed in {Repo}", repoPath);
        }

        // 3. Truncation
        var result = sb.ToString();
        if (result.Length > maxChars)
        {
            result = result[..maxChars]
                + $"\n\n... (diff truncated at {maxChars} characters, {sb.Length} total)";
        }

        return result;
    }

    /// <summary>
    /// Returns either the full diff (when its byte size is at or below
    /// <paramref name="thresholdBytes"/>) or a structured large-diff summary
    /// packet (when above). The summary packet contains a name+status table,
    /// per-file line counts, the top-K files by churn included inline, and an
    /// omitted-file list with line counts. Build/test command output is not
    /// captured here — the gate prompt's <c>{AgentReport}</c> placeholder
    /// already carries the agent's narrative including any test results.
    ///
    /// Replaces the old "diff truncated, can't verify" rejection: the gate
    /// can still decide based on visible files + agent report, and only
    /// returns NEEDS_INFO/ERROR when the visible evidence is genuinely
    /// insufficient — not because the raw diff was too large.
    /// </summary>
    public async Task<DiffPacket> GetDiffPacketAsync(
        string repoPath,
        int thresholdBytes,
        CancellationToken cancellationToken = default,
        string? baseRef = null,
        int topInlineFiles = 10,
        int perFileInlineCharLimit = 5_000)
    {
        var effectiveBase = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef;

        // Build the would-be full diff first. Same shape as GetDiffSummaryAsync
        // but without the truncation step. We need the raw bytes both to decide
        // mode and to return as Content when below threshold.
        var fullDiff = new StringBuilder();
        await AppendTrackedDiffAsync(repoPath, effectiveBase, fullDiff, cancellationToken);
        var untrackedFiles = await ListUntrackedFilesAsync(repoPath, cancellationToken);
        foreach (var file in untrackedFiles)
        {
            await AppendUntrackedSyntheticDiffAsync(repoPath, file, fullDiff, perFileLimit: 10_000, cancellationToken);
        }

        var fullText = fullDiff.ToString();
        if (fullText.Length == 0)
        {
            return new DiffPacket(DiffMode.Empty, "", 0, 0, 0);
        }

        // Collect per-file metadata for both Full and Summary modes (so callers
        // can see the changed file count even on Full).
        var fileEntries = await CollectFileEntriesAsync(
            repoPath, effectiveBase, untrackedFiles, cancellationToken);

        if (fullText.Length <= thresholdBytes)
        {
            return new DiffPacket(
                DiffMode.Full,
                fullText,
                RawByteSize: fullText.Length,
                FilesChanged: fileEntries.Count,
                FilesIncludedInline: fileEntries.Count);
        }

        // Summary mode: structured packet with top-K inline files.
        return await BuildSummaryPacketAsync(
            repoPath, effectiveBase, fileEntries,
            rawByteSize: fullText.Length,
            thresholdBytes: thresholdBytes,
            topInlineFiles: topInlineFiles,
            perFileInlineCharLimit: perFileInlineCharLimit,
            cancellationToken);
    }

    // ── Diff helpers ─────────────────────────────────────────────────

    private async Task AppendTrackedDiffAsync(
        string repoPath, string baseRef, StringBuilder sb, CancellationToken ct)
    {
        try
        {
            var (_, diffOutput, _) = await RunGitAsync(repoPath, ["diff", baseRef], ct);
            if (!string.IsNullOrWhiteSpace(diffOutput))
                sb.Append(diffOutput);
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git diff {BaseRef} failed in {Repo}", baseRef, repoPath);
        }
    }

    private async Task<List<string>> ListUntrackedFilesAsync(
        string repoPath, CancellationToken ct)
    {
        try
        {
            var (_, lsOutput, _) = await RunGitAsync(repoPath,
                ["ls-files", "--others", "--exclude-standard"], ct);

            return lsOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git ls-files failed in {Repo}", repoPath);
            return [];
        }
    }

    private async Task AppendUntrackedSyntheticDiffAsync(
        string repoPath, string relativePath, StringBuilder sb,
        int perFileLimit, CancellationToken ct)
    {
        sb.AppendLine();
        sb.AppendLine($"--- /dev/null");
        sb.AppendLine($"+++ b/{relativePath}");
        sb.AppendLine("@@ -0,0 +1 @@");

        var fullPath = Path.Combine(repoPath, relativePath);
        if (!File.Exists(fullPath))
            return;

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct);
            if (content.Length > perFileLimit)
                content = content[..perFileLimit] + "\n... (file truncated)";

            foreach (var line in content.Split('\n'))
                sb.AppendLine($"+{line}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"+[could not read file: {ex.Message}]");
        }
    }

    private async Task<List<DiffFileEntry>> CollectFileEntriesAsync(
        string repoPath, string baseRef, IReadOnlyList<string> untrackedFiles,
        CancellationToken ct)
    {
        var entries = new List<DiffFileEntry>();

        // Tracked changes via --numstat (line counts) + --name-status (status letter)
        Dictionary<string, char> statusByPath = new(StringComparer.Ordinal);
        try
        {
            var (_, nameStatus, _) = await RunGitAsync(
                repoPath, ["diff", "--name-status", baseRef], ct);
            foreach (var line in nameStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                // Rename status is 'R<percent>'; we just use the first letter.
                statusByPath[parts[1].Trim()] = parts[0][0];
            }
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git diff --name-status failed in {Repo}", repoPath);
        }

        try
        {
            var (_, numstat, _) = await RunGitAsync(
                repoPath, ["diff", "--numstat", baseRef], ct);
            foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var addedRaw = parts[0].Trim();
                var deletedRaw = parts[1].Trim();
                var path = parts[2].Trim();
                // Binary files report "-\t-\t<path>"; treat as 0/0 with isBinary flag.
                var isBinary = addedRaw == "-" && deletedRaw == "-";
                int.TryParse(addedRaw, out var added);
                int.TryParse(deletedRaw, out var deleted);
                var status = statusByPath.TryGetValue(path, out var s) ? s : 'M';
                entries.Add(new DiffFileEntry(path, status, added, deleted, isBinary, IsUntracked: false));
            }
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git diff --numstat failed in {Repo}", repoPath);
        }

        // Untracked files appear as new files; line count = file's line count.
        foreach (var path in untrackedFiles)
        {
            var fullPath = Path.Combine(repoPath, path);
            int lineCount = 0;
            if (File.Exists(fullPath))
            {
                try
                {
                    lineCount = (await File.ReadAllLinesAsync(fullPath, ct)).Length;
                }
                catch
                {
                    // Best-effort; unreadable files just show 0 lines.
                }
            }
            entries.Add(new DiffFileEntry(path, 'A', lineCount, 0, IsBinary: false, IsUntracked: true));
        }

        return entries;
    }

    private async Task<DiffPacket> BuildSummaryPacketAsync(
        string repoPath, string baseRef, List<DiffFileEntry> fileEntries,
        int rawByteSize, int thresholdBytes,
        int topInlineFiles, int perFileInlineCharLimit,
        CancellationToken ct)
    {
        // Sort by churn (added + deleted; binary files sort to the bottom of
        // the inline tier since their diff would just be "binary files differ").
        var sorted = fileEntries
            .OrderByDescending(e => e.IsBinary ? -1 : (e.Added + e.Deleted))
            .ToList();

        var topK = Math.Min(topInlineFiles, sorted.Count);
        var inline = sorted.Take(topK).ToList();
        var omitted = sorted.Skip(topK).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## Summary mode (large diff)");
        sb.AppendLine();
        sb.Append("The full diff is ")
            .Append(rawByteSize.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" bytes, exceeding the configured threshold of ")
            .Append(thresholdBytes.ToString("N0", CultureInfo.InvariantCulture))
            .AppendLine(" bytes. Below is a structured summary: a name+status table for every changed file, the top files by churn shown inline, and the rest listed without inline diff content.");
        sb.AppendLine();
        sb.AppendLine("**Important:** Do not return a generic \"diff truncated, cannot verify\" rejection. Decide based on the visible files, line counts, and the agent's self-report. Only return `NEEDS_INFO` or `ERROR` when the visible evidence is genuinely insufficient — and when you do, name the specific files or evidence you would need.");
        sb.AppendLine();

        sb.Append("### Files changed (").Append(sorted.Count).AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("| Status | File | +Added | -Deleted |");
        sb.AppendLine("|--------|------|--------|----------|");
        foreach (var e in sorted)
        {
            var added = e.IsBinary ? "(bin)" : e.Added.ToString(CultureInfo.InvariantCulture);
            var deleted = e.IsBinary ? "(bin)" : e.Deleted.ToString(CultureInfo.InvariantCulture);
            sb.Append("| ").Append(e.Status).Append(" | `").Append(e.Path).Append("` | ")
                .Append(added).Append(" | ").Append(deleted).AppendLine(" |");
        }
        sb.AppendLine();

        sb.Append("### Top ").Append(topK).AppendLine(" files by churn (inline)");
        sb.AppendLine();

        foreach (var e in inline)
        {
            sb.Append("#### `").Append(e.Path).Append("` (")
                .Append(e.Status).Append(", +")
                .Append(e.IsBinary ? "bin" : e.Added.ToString(CultureInfo.InvariantCulture))
                .Append(" / -")
                .Append(e.IsBinary ? "bin" : e.Deleted.ToString(CultureInfo.InvariantCulture))
                .AppendLine(")");
            sb.AppendLine();

            string body;
            if (e.IsBinary)
            {
                body = "(binary file — diff omitted)";
            }
            else if (e.IsUntracked)
            {
                body = await ReadUntrackedAsDiffAsync(repoPath, e.Path, perFileInlineCharLimit, ct);
            }
            else
            {
                body = await GetSingleFileDiffAsync(repoPath, baseRef, e.Path, perFileInlineCharLimit, ct);
            }

            sb.AppendLine("```diff");
            sb.AppendLine(body);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (omitted.Count > 0)
        {
            sb.Append("### Omitted files (").Append(omitted.Count).AppendLine(") — listed without inline diff");
            sb.AppendLine();
            foreach (var e in omitted)
            {
                sb.Append("- `").Append(e.Path).Append("` (")
                    .Append(e.Status).Append(", +")
                    .Append(e.IsBinary ? "bin" : e.Added.ToString(CultureInfo.InvariantCulture))
                    .Append(" / -")
                    .Append(e.IsBinary ? "bin" : e.Deleted.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(")");
            }
            sb.AppendLine();
        }

        return new DiffPacket(
            DiffMode.Summary,
            sb.ToString(),
            RawByteSize: rawByteSize,
            FilesChanged: sorted.Count,
            FilesIncludedInline: topK);
    }

    private async Task<string> GetSingleFileDiffAsync(
        string repoPath, string baseRef, string path, int perFileLimit, CancellationToken ct)
    {
        try
        {
            var (_, output, _) = await RunGitAsync(
                repoPath, ["diff", baseRef, "--", path], ct);
            if (output.Length > perFileLimit)
            {
                output = output[..perFileLimit] + "\n... (file diff truncated)";
            }
            return output;
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git diff for file {Path} failed in {Repo}", path, repoPath);
            return $"(failed to read diff: {ex.Message})";
        }
    }

    private async Task<string> ReadUntrackedAsDiffAsync(
        string repoPath, string path, int perFileLimit, CancellationToken ct)
    {
        var fullPath = Path.Combine(repoPath, path);
        if (!File.Exists(fullPath))
            return "(untracked file not found)";

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct);
            if (content.Length > perFileLimit)
                content = content[..perFileLimit] + "\n... (file truncated)";

            var sb = new StringBuilder();
            sb.AppendLine("--- /dev/null");
            sb.AppendLine($"+++ b/{path}");
            sb.AppendLine("@@ -0,0 +1 @@");
            foreach (var line in content.Split('\n'))
                sb.Append('+').AppendLine(line);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(could not read untracked file: {ex.Message})";
        }
    }

    // ── Common git operations ────────────────────────────────────────

    public async Task CommitAsync(
        string repoPath, string message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Committing changes in {Repo}", repoPath);
        await RunGitAsync(repoPath, ["add", "."], cancellationToken);

        if (!await HasStagedChangesAsync(repoPath, cancellationToken))
        {
            logger.LogInformation("No staged changes to commit in {Repo}", repoPath);
            return;
        }

        await RunGitAsync(repoPath, ["commit", "-m", message], cancellationToken);
    }

    public async Task PushAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Pushing branch {Branch} in {Repo}", branchName, repoPath);
        await RunGitAsync(repoPath, ["push", "-u", "origin", branchName], cancellationToken);
    }

    public async Task<string> GetCurrentBranchAsync(
        string repoPath, CancellationToken cancellationToken)
    {
        var (_, stdout, _) = await RunGitAsync(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        return stdout.Trim();
    }

    /// <summary>
    /// Returns the full SHA of HEAD in the given repo. Used to capture a
    /// stable diff base before running candidate groups (so the evaluator
    /// can show each candidate's divergence) and at run start (so gate
    /// checks see the run's cumulative work even after candidate
    /// promotions reset the working tree).
    /// </summary>
    public async Task<string?> GetCurrentShaAsync(
        string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var (_, stdout, _) = await RunGitAsync(repoPath, ["rev-parse", "HEAD"], cancellationToken);
            var sha = stdout.Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "git rev-parse HEAD failed in {Repo}", repoPath);
            return null;
        }
    }

    public async Task<bool> HasStagedChangesAsync(
        string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            // git diff --cached --quiet returns exit code 1 if there are staged changes
            await RunGitAsync(repoPath, ["diff", "--cached", "--quiet"], cancellationToken);
            return false;
        }
        catch (GitOperationException ex) when (ex.ExitCode == 1)
        {
            return true;
        }
    }

    /// <summary>
    /// Checks if there are any uncommitted changes (tracked files only, .gitignore respected).
    /// </summary>
    public async Task<bool> HasUncommittedChangesAsync(
        string repoPath, CancellationToken cancellationToken)
    {
        var (_, stdout, _) = await RunGitAsync(repoPath, ["status", "--porcelain"], cancellationToken);
        return !string.IsNullOrWhiteSpace(stdout);
    }

    private const int DefaultTimeoutSeconds = 30;

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory, string[] args, CancellationToken cancellationToken,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        process.StartInfo = startInfo;

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new GitOperationException(
                $"git {string.Join(' ', args)} timed out after {timeoutSeconds}s", -1);
        }

        var stdout = stdoutBuf.ToString();
        var stderr = stderrBuf.ToString();

        if (process.ExitCode != 0)
        {
            throw new GitOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr.Trim()}",
                process.ExitCode);
        }

        return (process.ExitCode, stdout, stderr);
    }
}

public enum MergeStatus { Merged, Conflict, AlreadyUpToDate }

public enum PushStatus { Success, NonFastForward }

public sealed class GitOperationException(string message, int exitCode)
    : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
