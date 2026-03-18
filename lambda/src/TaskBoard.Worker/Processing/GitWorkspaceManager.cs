using System.Diagnostics;
using System.Text;

namespace TaskBoard.Worker.Processing;

public sealed class GitWorkspaceManager(ILogger<GitWorkspaceManager> logger)
{
    private const int DefaultTimeoutSeconds = 30;

    public async Task CreateBranchAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating branch {Branch} in {Repo}", branchName, repoPath);
        await RunGitAsync(repoPath, ["checkout", "-b", branchName], cancellationToken);
    }

    public async Task CommitAsync(
        string repoPath, string message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Committing changes in {Repo}", repoPath);
        await RunGitAsync(repoPath, ["add", ".aiboard/"], cancellationToken);
        await RunGitAsync(repoPath, ["commit", "-m", message], cancellationToken);
    }

    public async Task PushAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Pushing branch {Branch} in {Repo}", branchName, repoPath);
        await RunGitAsync(repoPath, ["push", "-u", "origin", branchName], cancellationToken);
    }

    public async Task CleanupAsync(
        string repoPath, string branchName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Cleaning up branch {Branch} in {Repo}", branchName, repoPath);

        // Switch back to previous branch before deleting
        await RunGitAsync(repoPath, ["checkout", "-"], cancellationToken);
        await RunGitAsync(repoPath, ["branch", "-D", branchName], cancellationToken);
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
