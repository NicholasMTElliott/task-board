using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class GitWorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitWorkspaceManager _manager;

    public GitWorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gitwm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _manager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);

        // Initialize a git repo in the temp dir
        InitGitRepo(_tempDir);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir))
            return;

        // Git pack files are read-only on Windows — clear attributes before deleting
        foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task CreateBranchAsync_CreatesBranch()
    {
        await _manager.CreateBranchAsync(_tempDir, "aiboard/test-card", CancellationToken.None);

        var branch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal("aiboard/test-card", branch);
    }

    [Fact]
    public async Task CommitAsync_CommitsAiboardFiles()
    {
        await _manager.CreateBranchAsync(_tempDir, "aiboard/test-card", CancellationToken.None);

        // Create .aiboard/tasks directory and a file
        var tasksDir = Path.Combine(_tempDir, ".aiboard", "tasks");
        Directory.CreateDirectory(tasksDir);
        await File.WriteAllTextAsync(Path.Combine(tasksDir, "card1.md"), "test content");

        await _manager.CommitAsync(_tempDir, "Agent: design for card1", CancellationToken.None);

        // Verify commit exists by checking git log
        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            _tempDir, ["log", "--oneline", "-1"], CancellationToken.None);
        Assert.Contains("Agent: design for card1", logOutput);
    }

    [Fact]
    public async Task CommitAsync_NoChanges_Throws()
    {
        await _manager.CreateBranchAsync(_tempDir, "aiboard/test-card", CancellationToken.None);

        // git commit with nothing staged should fail
        var ex = await Assert.ThrowsAsync<GitOperationException>(
            () => _manager.CommitAsync(_tempDir, "Empty commit", CancellationToken.None));
        Assert.NotEqual(0, ex.ExitCode);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsMainOnInit()
    {
        var branch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);

        // git init creates 'main' or 'master' depending on config
        Assert.True(branch == "main" || branch == "master",
            $"Expected 'main' or 'master', got '{branch}'");
    }

    [Fact]
    public async Task CleanupAsync_DeletesBranchAndReturns()
    {
        var originalBranch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        await _manager.CreateBranchAsync(_tempDir, "aiboard/temp-branch", CancellationToken.None);

        await _manager.CleanupAsync(_tempDir, "aiboard/temp-branch", CancellationToken.None);

        var currentBranch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal(originalBranch, currentBranch);
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
        // Create and stage a file
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

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        // Create initial commit so branches work
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".gitkeep");
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
}
