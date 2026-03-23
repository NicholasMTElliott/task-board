using System.Diagnostics;
using System.Text;

namespace TaskBoard.Worker.Processing;

public sealed class GitWorkspaceManager(ILogger<GitWorkspaceManager> logger)
{
    private const int DefaultTimeoutSeconds = 30;

    // ── Worktree methods ──────────────────────────────────────────────

    internal static string GetWorktreePath(string repoPath, string branchName)
        => Path.Combine(repoPath + "-worktrees", branchName.Replace('/', Path.DirectorySeparatorChar));

    public async Task<string> CreateWorktreeAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        var worktreePath = GetWorktreePath(repoPath, branchName);
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

        var branchExists = await BranchExistsAsync(repoPath, branchName, cancellationToken);

        if (branchExists)
        {
            logger.LogInformation("Creating worktree at {Path} for existing branch {Branch}", fullWorktreePath, branchName);
            await RunGitAsync(repoPath, ["worktree", "add", fullWorktreePath, branchName], cancellationToken);
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
        var worktreePath = Path.GetFullPath(GetWorktreePath(repoPath, branchName));

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

public sealed class GitOperationException(string message, int exitCode)
    : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
