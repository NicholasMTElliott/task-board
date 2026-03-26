using System.Diagnostics;
using System.Text;

namespace TaskBoard.Worker.Processing;

public sealed class GitWorkspaceManager(ILogger<GitWorkspaceManager> logger, string? worktreeBasePath = null)
{
    private const int DefaultTimeoutSeconds = 30;

    // ── Worktree methods ──────────────────────────────────────────────

    internal static string GetWorktreePath(string repoPath, string branchName, string? worktreeBase = null)
    {
        var basePath = worktreeBase ?? (repoPath + "-worktrees");
        return Path.Combine(basePath, branchName.Replace('/', Path.DirectorySeparatorChar));
    }

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

    public async Task RemoveWorktreeAsync(
        string repoPath, string branchName, bool deleteBranch, CancellationToken cancellationToken)
    {
        var worktreePath = Path.GetFullPath(GetWorktreePath(repoPath, branchName, worktreeBasePath));

        logger.LogInformation("Removing worktree at {Path}", worktreePath);

        try
        {
            await RunGitAsync(repoPath, ["worktree", "remove", worktreePath, "--force"], cancellationToken);
        }
        catch (GitOperationException ex)
        {
            logger.LogWarning(ex, "Failed to remove worktree at {Path} — may not exist", worktreePath);
            // If directory still exists, force-remove it and prune
            if (Directory.Exists(worktreePath))
            {
                Directory.Delete(worktreePath, recursive: true);
                try { await RunGitAsync(repoPath, ["worktree", "prune"], cancellationToken); }
                catch (GitOperationException) { /* best effort */ }
            }
        }

        if (deleteBranch)
        {
            try
            {
                await RunGitAsync(repoPath, ["branch", "-D", branchName], cancellationToken);
            }
            catch (GitOperationException ex)
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
            await RunGitAsync(repoPath, ["fetch", "origin"], cancellationToken, timeoutSeconds: 60);
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
        await RunGitAsync(repoPath, ["fetch", "origin"], cancellationToken, timeoutSeconds: 60);
    }

    public async Task<string> GetDefaultBranchAsync(string repoPath, CancellationToken cancellationToken)
    {
        // Try symbolic-ref first
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

        // Fallback: probe known defaults
        foreach (var candidate in new[] { "main", "master", "mainline" })
        {
            if (await RemoteBranchExistsAsync(repoPath, candidate, cancellationToken))
                return candidate;
        }

        throw new GitOperationException("Could not determine default branch", -1);
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
    }

    public async Task DeleteRemoteBranchAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting remote branch {Branch}", branchName);
        await RunGitAsync(repoPath,
            ["push", "origin", "--delete", branchName], cancellationToken, timeoutSeconds: 60);
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
                ["push", "origin", $"{localRef}:{remoteRef}"], cancellationToken, timeoutSeconds: 60);
            return PushStatus.Success;
        }
        catch (GitOperationException ex) when (
            ex.Message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
        {
            return PushStatus.NonFastForward;
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
