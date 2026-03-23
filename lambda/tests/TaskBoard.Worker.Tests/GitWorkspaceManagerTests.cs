using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class GitWorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly GitWorkspaceManager _manager;

    public GitWorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gitwm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        _manager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);

        // Initialize a git repo in the temp dir
        InitGitRepo(_tempDir);
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);

        // Prune worktrees before deleting the main repo (avoids locked refs)
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }

        CleanupDirectory(_tempDir);
    }

    // ── Worktree tests ────────────────────────────────────────────────

    [Fact]
    public async Task CreateWorktreeAsync_CreatesWorktreeAndBranch()
    {
        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/test-card", CancellationToken.None);

        Assert.True(Directory.Exists(path), $"Worktree directory should exist at {path}");

        // Branch inside the worktree should be correct
        var branch = await _manager.GetCurrentBranchAsync(path, CancellationToken.None);
        Assert.Equal("aiboard/test-card", branch);

        // Main repo should still be on its original branch
        var mainBranch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.True(mainBranch == "main" || mainBranch == "master",
            $"Main repo should still be on main/master, got '{mainBranch}'");
    }

    [Fact]
    public async Task CreateWorktreeAsync_BranchAlreadyExists_AttachesToExistingBranch()
    {
        // Create the branch manually first
        RunGitSync(_tempDir, "branch", "aiboard/existing-card");

        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/existing-card", CancellationToken.None);

        Assert.True(Directory.Exists(path));
        var branch = await _manager.GetCurrentBranchAsync(path, CancellationToken.None);
        Assert.Equal("aiboard/existing-card", branch);
    }

    [Fact]
    public async Task CreateWorktreeAsync_WorktreeAlreadyExists_ReusesIt()
    {
        var path1 = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/reuse-card", CancellationToken.None);
        var path2 = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/reuse-card", CancellationToken.None);

        Assert.Equal(path1, path2);
        Assert.True(Directory.Exists(path2));
    }

    [Fact]
    public async Task CreateWorktreeAsync_StaleDirectory_CleansUpAndCreates()
    {
        var worktreePath = Path.GetFullPath(GitWorkspaceManager.GetWorktreePath(_tempDir, "aiboard/stale-card"));

        // Create a stale directory (not a registered worktree)
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, "junk.txt"), "stale");

        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/stale-card", CancellationToken.None);

        Assert.True(Directory.Exists(path));
        var branch = await _manager.GetCurrentBranchAsync(path, CancellationToken.None);
        Assert.Equal("aiboard/stale-card", branch);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_RemovesWorktreeDirectory()
    {
        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/remove-card", CancellationToken.None);
        Assert.True(Directory.Exists(path));

        await _manager.RemoveWorktreeAsync(_tempDir, "aiboard/remove-card", deleteBranch: false, CancellationToken.None);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task RemoveWorktreeAsync_WithDeleteBranch_DeletesBranch()
    {
        await _manager.CreateWorktreeAsync(_tempDir, "aiboard/delete-branch-card", CancellationToken.None);

        await _manager.RemoveWorktreeAsync(_tempDir, "aiboard/delete-branch-card", deleteBranch: true, CancellationToken.None);

        var branchExists = await _manager.BranchExistsAsync(_tempDir, "aiboard/delete-branch-card", CancellationToken.None);
        Assert.False(branchExists);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_NonExistent_DoesNotThrow()
    {
        // Should not throw even if worktree doesn't exist
        await _manager.RemoveWorktreeAsync(_tempDir, "aiboard/never-created", deleteBranch: false, CancellationToken.None);
    }

    // ── Commit tests (inside worktree) ────────────────────────────────

    [Fact]
    public async Task CommitAsync_CommitsNonIgnoredFiles()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/commit-card", CancellationToken.None);

        // Create a non-ignored file
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "design.md"), "# Technical Design");

        await _manager.CommitAsync(worktreePath, "Agent: design for card1", CancellationToken.None);

        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline", "-1"], CancellationToken.None);
        Assert.Contains("Agent: design for card1", logOutput);
    }

    [Fact]
    public async Task CommitAsync_IgnoresAiboardFiles_NoOp()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/ignored-card", CancellationToken.None);

        // Create only .aiboard/ files (gitignored) — commit should be a no-op
        var tasksDir = Path.Combine(worktreePath, ".aiboard", "tasks");
        Directory.CreateDirectory(tasksDir);
        await File.WriteAllTextAsync(Path.Combine(tasksDir, "card1.md"), "test content");

        // Should not throw — just skips the commit
        await _manager.CommitAsync(worktreePath, "Should be skipped", CancellationToken.None);

        // Verify no commit was created (still on initial commit)
        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline"], CancellationToken.None);
        Assert.DoesNotContain("Should be skipped", logOutput);
    }

    [Fact]
    public async Task CommitAsync_NoChanges_IsNoOp()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/empty-commit", CancellationToken.None);

        // Should not throw — just skips the commit
        await _manager.CommitAsync(worktreePath, "Empty commit", CancellationToken.None);

        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline"], CancellationToken.None);
        Assert.DoesNotContain("Empty commit", logOutput);
    }

    // ── Helper tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsMainOnInit()
    {
        var branch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);

        Assert.True(branch == "main" || branch == "master",
            $"Expected 'main' or 'master', got '{branch}'");
    }

    [Fact]
    public async Task BranchExistsAsync_ReturnsFalseForNonExistent()
    {
        var exists = await _manager.BranchExistsAsync(_tempDir, "aiboard/no-such-branch", CancellationToken.None);
        Assert.False(exists);
    }

    [Fact]
    public async Task BranchExistsAsync_ReturnsTrueForExisting()
    {
        RunGitSync(_tempDir, "branch", "aiboard/exists");
        var exists = await _manager.BranchExistsAsync(_tempDir, "aiboard/exists", CancellationToken.None);
        Assert.True(exists);
    }

    [Fact]
    public async Task GetWorktreePath_ProducesExpectedPath()
    {
        var path = GitWorkspaceManager.GetWorktreePath("/repo/root", "aiboard/card-123");
        var expected = Path.Combine("/repo/root-worktrees", "aiboard", "card-123");
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task HasStagedChangesAsync_NoChanges_ReturnsFalse()
    {
        var result = await _manager.HasStagedChangesAsync(_tempDir, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task HasStagedChangesAsync_WithStagedFiles_ReturnsTrue()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "staged.txt"), "content");
        await GitWorkspaceManager.RunGitAsync(_tempDir, ["add", "staged.txt"], CancellationToken.None);

        var result = await _manager.HasStagedChangesAsync(_tempDir, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task RunGitAsync_InvalidCommand_ThrowsGitOperationException()
    {
        var ex = await Assert.ThrowsAsync<GitOperationException>(
            () => GitWorkspaceManager.RunGitAsync(
                _tempDir, ["not-a-real-command"], CancellationToken.None));

        Assert.NotEqual(0, ex.ExitCode);
    }

    // ── Test infrastructure ───────────────────────────────────────────

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        // Create initial commit with .gitignore matching the real repo
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    private static void RunGitSync(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Git pack files are read-only on Windows — clear attributes before deleting
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }
}
