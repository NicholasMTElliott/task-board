using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

public class MergeRunnerTests : IDisposable
{
    private const string CardId = "42";
    private const string CardTitle = "Add login feature";
    private const string BoardId = "board-1";
    private const string AcceptedCol = "Accepted";
    private const string MergingCol = "Merging";
    private const string DoneCol = "Done";
    private const string ErrorCol = "Error";

    private readonly string _bareRepoDir;
    private readonly string _workspaceDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly GitWorkspaceManager _gitManager;
    private readonly WorkflowConfig _config;

    public MergeRunnerTests()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _bareRepoDir = Path.Combine(Path.GetTempPath(), $"mergerunner-bare-{suffix}");
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"mergerunner-ws-{suffix}");
        _worktreeBase = _workspaceDir + "-worktrees";

        _boardClient = Substitute.For<ITaskBoardClient>();
        _gitManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _config = BuildMergeWorkflowConfig();
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        if (Directory.Exists(_workspaceDir))
        {
            try { RunGitSync(_workspaceDir, "worktree", "prune"); } catch { }
        }
        CleanupDirectory(_workspaceDir);
        CleanupDirectory(_bareRepoDir);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CleanMerge_MergesAndMovesToDone()
    {
        SetupBareRepoWithWorkBranch();
        SetupBoardCards(AcceptedCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Contains("Merged", result.ErrorDetail);

        // Card should move to Merging (IN_PROGRESS) then Done (COMPLETE)
        var moveCalls = _boardClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "MoveCardToColumnAsync")
            .Select(c => (string)c.GetArguments()[1]!)
            .ToList();
        Assert.Contains(MergingCol, moveCalls);
        Assert.Contains(DoneCol, moveCalls);

        // Comment should be posted
        await _boardClient.Received().UpsertAgentCommentAsync(
            CardId, Arg.Is<string>(s => s.Contains("Merge Complete")), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Merge commit should exist on origin/main
        var logOutput = RunGitSyncWithOutput(_bareRepoDir, "log", "--oneline", "main");
        Assert.Contains($"Merge #{CardId}", logOutput);

        // Remote work branch should be deleted (best-effort, verify it's gone)
        var remoteBranches = RunGitSyncWithOutput(_bareRepoDir, "branch", "--list");
        Assert.DoesNotContain($"aiboard/{CardId}", remoteBranches);
    }

    // ── Already merged ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AlreadyMerged_SkipsMergeAndMovesToDone()
    {
        SetupBareRepoWithWorkBranch();

        // Merge the work branch into main on the bare repo directly
        // (simulating someone already merged it)
        var tempClone = Path.Combine(Path.GetTempPath(), $"mergerunner-tmp-{Guid.NewGuid():N}"[..30]);
        try
        {
            RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, tempClone);
            RunGitSync(tempClone, "config", "user.email", "test@test.com");
            RunGitSync(tempClone, "config", "user.name", "Test");
            RunGitSync(tempClone, "merge", "--no-ff", $"origin/aiboard/{CardId}-add-login-feature", "-m", "Pre-merge");
            RunGitSync(tempClone, "push", "origin", "main");
        }
        finally
        {
            CleanupDirectory(tempClone);
        }

        SetupBoardCards(AcceptedCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Contains("already merged", result.ErrorDetail);

        await _boardClient.Received().MoveCardToColumnAsync(CardId, DoneCol, Arg.Any<CancellationToken>());
    }

    // ── No branch found ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoBranchFound_ReturnsError()
    {
        // Set up a bare repo and clone WITHOUT any work branch
        SetupBareRepo();
        RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, _workspaceDir);
        RunGitSync(_workspaceDir, "config", "user.email", "test@test.com");
        RunGitSync(_workspaceDir, "config", "user.name", "Test");

        SetupBoardCards(AcceptedCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("No branch found", result.ErrorDetail);

        await _boardClient.Received().MoveCardToColumnAsync(CardId, ErrorCol, Arg.Any<CancellationToken>());
    }

    // ── Merge conflict ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MergeConflict_ReturnsErrorAndAbortsCleanly()
    {
        SetupBareRepoWithWorkBranch();

        // Create a conflicting change on main via a temp clone
        var tempClone = Path.Combine(Path.GetTempPath(), $"mergerunner-conflict-{Guid.NewGuid():N}"[..30]);
        try
        {
            RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, tempClone);
            RunGitSync(tempClone, "config", "user.email", "test@test.com");
            RunGitSync(tempClone, "config", "user.name", "Test");
            // Write a conflicting change to the same file modified on the work branch
            File.WriteAllText(Path.Combine(tempClone, "feature.txt"), "conflicting content from main");
            RunGitSync(tempClone, "add", ".");
            RunGitSync(tempClone, "commit", "-m", "conflicting change on main");
            RunGitSync(tempClone, "push", "origin", "main");
        }
        finally
        {
            CleanupDirectory(tempClone);
        }

        SetupBoardCards(AcceptedCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("conflict", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);

        await _boardClient.Received().MoveCardToColumnAsync(CardId, ErrorCol, Arg.Any<CancellationToken>());
    }

    // ── Card not found ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CardNotFound_ReturnsError()
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>()));

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(CardId, BoardId, "C:/fake/path", CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not found", result.ErrorDetail);
    }

    // ── Wrong gate type ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WrongGateType_ReturnsError()
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new(CardId, CardTitle, "body", "Ready for Design"),
            }));

        // Config has "Ready for Design" as agent_run, not system_merge
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", "se", "agent_run",
                    "Design it.", new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["se"] = new("model", "prompt", new List<string>()),
            });

        var runner = new MergeRunner(_boardClient, _gitManager, config, new AgentIdentity("Test", "Agent", "TestMachine"), Substitute.For<ICrossReferenceResolver>(), NullLogger<MergeRunner>.Instance);
        var result = await runner.ExecuteAsync(CardId, BoardId, "C:/fake/path", CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not a system_merge", result.ErrorDetail);
    }

    // ── Cleanup failure is non-blocking ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RemoteBranchAlreadyDeleted_MergeStillSucceeds()
    {
        SetupBareRepoWithWorkBranch();

        // Delete the remote work branch ahead of time so cleanup will fail
        RunGitSync(_bareRepoDir, "branch", "-D", $"aiboard/{CardId}-add-login-feature");

        SetupBoardCards(AcceptedCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        // Merge should still succeed even though remote branch cleanup will fail
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        await _boardClient.Received().MoveCardToColumnAsync(CardId, DoneCol, Arg.Any<CancellationToken>());
    }

    // ── Retry exhaustion (maxRetries=1, push always fails) ──────────────
    // Note: Push retry scenarios (NonFastForward recovery, externally merged during retry)
    // require intercepting between merge and push, which is not feasible without
    // extracting GitWorkspaceManager behind an interface. These are better validated
    // via manual E2E testing or future interface extraction.

    // ── Helpers ──────────────────────────────────────────────────────────

    private MergeRunner CreateRunner() =>
        new(_boardClient, _gitManager, _config, new AgentIdentity("Test", "Agent", "TestMachine"), Substitute.For<ICrossReferenceResolver>(), NullLogger<MergeRunner>.Instance);

    private void SetupBoardCards(string column)
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new(CardId, CardTitle, "body", column),
            }));
    }

    /// <summary>
    /// Creates a bare repo (acts as "origin") with a main branch.
    /// </summary>
    private void SetupBareRepo()
    {
        Directory.CreateDirectory(_bareRepoDir);
        RunGitSync(_bareRepoDir, "init", "--bare", "--initial-branch=main");

        // Create a temp clone to make the initial commit
        var initClone = _bareRepoDir + "-init";
        try
        {
            RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, initClone);
            RunGitSync(initClone, "config", "user.email", "test@test.com");
            RunGitSync(initClone, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(initClone, "README.md"), "initial");
            File.WriteAllText(Path.Combine(initClone, ".gitignore"), ".aiboard/\n");
            RunGitSync(initClone, "add", ".");
            RunGitSync(initClone, "commit", "-m", "initial commit");
            RunGitSync(initClone, "push", "origin", "main");
        }
        finally
        {
            CleanupDirectory(initClone);
        }
    }

    /// <summary>
    /// Sets up bare repo + clone workspace + work branch with a commit pushed to origin.
    /// </summary>
    private void SetupBareRepoWithWorkBranch()
    {
        SetupBareRepo();

        // Clone bare repo to workspace
        RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, _workspaceDir);
        RunGitSync(_workspaceDir, "config", "user.email", "test@test.com");
        RunGitSync(_workspaceDir, "config", "user.name", "Test");

        // Create work branch matching the convention aiboard/{cardId}-{slug}
        var workBranch = $"aiboard/{CardId}-add-login-feature";
        RunGitSync(_workspaceDir, "checkout", "-b", workBranch);
        File.WriteAllText(Path.Combine(_workspaceDir, "feature.txt"), "new feature code");
        RunGitSync(_workspaceDir, "add", ".");
        RunGitSync(_workspaceDir, "commit", "-m", "Add login feature");
        RunGitSync(_workspaceDir, "push", "origin", workBranch);

        // Return to main
        RunGitSync(_workspaceDir, "checkout", "main");
    }

    private static WorkflowConfig BuildMergeWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [AcceptedCol] = new("Accepted", null, "system_merge",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn(MergingCol),
                        ["COMPLETE"]    = TransitionTarget.ForColumn(DoneCol),
                        ["ERROR"]       = TransitionTarget.ForColumn(ErrorCol),
                    },
                    ProviderParams: new Dictionary<string, string> { ["maxRetries"] = "3" },
                    PipelineOrder: 4),
                [MergingCol] = new("Merging", null, "in_progress",
                    null, new Dictionary<string, TransitionTarget>()),
                [DoneCol] = new("Done", null, "terminal",
                    null, new Dictionary<string, TransitionTarget>()),
                [ErrorCol] = new("Error", null, "holding",
                    null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>());
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }

}
