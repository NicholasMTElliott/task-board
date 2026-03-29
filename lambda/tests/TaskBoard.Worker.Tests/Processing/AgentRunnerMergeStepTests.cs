using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Tests for the pre-agent merge step in AgentRunner.HandleMergeStepAsync.
/// Uses real git repos (bare + clone) since GitWorkspaceManager is sealed.
/// The merge step only runs when isExistingBranch=true (a work branch already exists).
/// </summary>
public class AgentRunnerMergeStepTests : IDisposable
{
    private const string CardId = "77";
    private const string CardTitle = "Add merge feature";
    private const string BoardId = "board-1";

    // Column IDs for workflow states
    private const string ImplColumnId = "impl-col";
    private const string TestColumnId = "test-col";
    private const string DesignColumnId = "design-col";
    private const string CompleteCol = "complete-col";
    private const string QuestionsCol = "questions-col";
    private const string ErrorCol = "error-col";
    private const string ReadyForImplCol = "ready-impl-col";
    private const string InProgressCol = "in-progress-col";

    private readonly string _bareRepoDir;
    private readonly string _workspaceDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly GitWorkspaceManager _gitManager;
    private readonly TaskFileManager _taskFileManager;
    private readonly string _workBranch;

    public AgentRunnerMergeStepTests()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _bareRepoDir = Path.Combine(Path.GetTempPath(), $"mergestep-bare-{suffix}");
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"mergestep-ws-{suffix}");
        _worktreeBase = _workspaceDir + "-worktrees";

        _boardClient = Substitute.For<ITaskBoardClient>();
        _boardClient.GetCardCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CardComment>>([]));

        _gitManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _workBranch = $"aiboard/{CardId}-add-merge-feature";
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

    // ── Test 1: UpToDate → Proceed (no-op) ──────────────────────────────

    [Fact]
    public async Task MergeStep_UpToDate_ProceedsWithNoMerge()
    {
        // Work branch exists but main has no new commits → merge is a no-op
        SetupBareRepoWithWorkBranch();
        SetupBoardCards(ImplColumnId);

        var executor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        executor.NextOutcome = AgentOutcome.COMPLETE;
        var runner = CreateRunner(BuildImplConfig(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Should move to COMPLETE column (normal agent flow, no kick-back)
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, CompleteCol, Arg.Any<CancellationToken>());
    }

    // ── Test 2: Clean merge, no MERGE_CONFLICT transition → auto-commit ─

    [Fact]
    public async Task MergeStep_CleanMerge_NoMergeConflictTransition_AutoCommitsAndProceeds()
    {
        // Implementation stage (no MERGE_CONFLICT transition) with clean merge
        SetupBareRepoWithWorkBranch();
        AddNonConflictingChangeToMain();
        SetupBoardCards(ImplColumnId);

        var executor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        executor.NextOutcome = AgentOutcome.COMPLETE;
        var runner = CreateRunner(BuildImplConfig(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Verify the merge commit exists on the work branch in the worktree
        // The agent should have run after the merge was committed
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, CompleteCol, Arg.Any<CancellationToken>());

        // No kick-back should have happened
        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            CardId, ReadyForImplCol, Arg.Any<CancellationToken>());
    }

    // ── Test 3: Conflicts, no MERGE_CONFLICT transition → ProceedWithConflictContext ─

    [Fact]
    public async Task MergeStep_Conflicts_NoMergeConflictTransition_ProceedsWithConflictAugmentation()
    {
        // Implementation stage with conflicts → agent should still run (with conflict context)
        SetupBareRepoWithWorkBranch();
        AddConflictingChangeToMain();
        SetupBoardCards(ImplColumnId);

        var executor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        executor.NextOutcome = AgentOutcome.COMPLETE;
        var runner = CreateRunner(BuildImplConfig(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        // Agent should have run and completed (it handles conflicts inline)
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // No kick-back — implementation handles conflicts inline
        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            CardId, ReadyForImplCol, Arg.Any<CancellationToken>());
    }

    // ── Test 4: Clean merge, with MERGE_CONFLICT transition + agent COMPLETE → commit ─

    [Fact]
    public async Task MergeStep_CleanMerge_WithMergeConflictTransition_AgentComplete_CommitsAndProceeds()
    {
        // Test/Design stage (has MERGE_CONFLICT transition) with clean merge
        // Merge resolution agent returns COMPLETE → merge is committed, main agent runs
        SetupBareRepoWithWorkBranch();
        AddNonConflictingChangeToMain();
        SetupBoardCards(TestColumnId);

        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // Both merge resolution agent and main agent return COMPLETE
                return new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var runner = CreateRunner(BuildTestConfigWithMergeResolution(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Merge resolution agent + main agent = 2 calls
        Assert.Equal(2, callCount);

        // No kick-back
        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            CardId, ReadyForImplCol, Arg.Any<CancellationToken>());
    }

    // ── Test 5: Conflicts, with MERGE_CONFLICT transition + agent ERROR → abort + KickBack ─

    [Fact]
    public async Task MergeStep_Conflicts_WithMergeConflictTransition_AgentError_AbortsAndKicksBack()
    {
        // Test/Design stage with conflicts, merge resolution agent returns ERROR
        // → abort merge, kick back to Ready for Implementation
        SetupBareRepoWithWorkBranch();
        AddConflictingChangeToMain();
        SetupBoardCards(TestColumnId);

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.ERROR, "Cannot resolve conflicts"));

        var runner = CreateRunner(BuildTestConfigWithMergeResolution(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        // Should report ERROR and kick back
        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("Merge conflict", result.ErrorDetail);

        // Card should be moved to the MERGE_CONFLICT target column
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, ReadyForImplCol, Arg.Any<CancellationToken>());

        // A kick-back comment should be posted
        await _boardClient.Received().UpsertAgentCommentAsync(
            CardId,
            Arg.Is<string>(s => s.Contains("Merge Conflict") && s.Contains("could not be resolved")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test 6: Fetch failure → graceful skip (Proceed) ─────────────────

    [Fact]
    public async Task MergeStep_FetchFailure_SkipsMergeAndProceeds()
    {
        // Set up a repo where origin is unreachable (bad remote URL)
        SetupBareRepoWithWorkBranch();

        // Break the remote URL so fetch fails
        RunGitSync(_workspaceDir, "remote", "set-url", "origin", "file:///nonexistent/path");

        SetupBoardCards(ImplColumnId);

        var executor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        executor.NextOutcome = AgentOutcome.COMPLETE;
        var runner = CreateRunner(BuildImplConfig(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        // Fetch failure should be swallowed; agent should still run and complete
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    // ── Test 7: Default branch detection failure → graceful skip ────────

    [Fact]
    public async Task MergeStep_DefaultBranchDetectionFailure_SkipsMergeAndProceeds()
    {
        // Set up a repo where origin has no main/master/mainline branch
        // (rename main to something unrecognizable)
        SetupBareRepoWithWorkBranch();

        // Rename main to "trunk" on bare repo and update workspace
        RunGitSync(_bareRepoDir, "branch", "-m", "main", "trunk");
        // Update the workspace's remote tracking to match
        RunGitSync(_workspaceDir, "fetch", "origin", "--prune");
        // Remove local main so GetDefaultBranchAsync can't find it
        try { RunGitSync(_workspaceDir, "branch", "-D", "main"); } catch { }
        // Unset HEAD on the remote
        RunGitSync(_bareRepoDir, "symbolic-ref", "HEAD", "refs/heads/trunk");

        SetupBoardCards(ImplColumnId);

        var executor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        executor.NextOutcome = AgentOutcome.COMPLETE;
        var runner = CreateRunner(BuildImplConfig(), executor);

        var result = await runner.ExecuteAsync(CardId, BoardId, _workspaceDir, CancellationToken.None);

        // Default branch detection failure should be swallowed; agent proceeds
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupBoardCards(string columnId)
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new(CardId, CardTitle, "Implement the merge feature", columnId),
            }));
    }

    private AgentRunner CreateRunner(WorkflowConfig config, IAgentExecutor executor)
    {
        return new AgentRunner(
            _boardClient,
            executor,
            _taskFileManager,
            _gitManager,
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<AgentRunner>.Instance);
    }

    /// <summary>
    /// Creates a bare repo with a main branch containing an initial commit.
    /// </summary>
    private void SetupBareRepo()
    {
        Directory.CreateDirectory(_bareRepoDir);
        RunGitSync(_bareRepoDir, "init", "--bare", "--initial-branch=main");

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
    /// Sets up bare repo + workspace clone + work branch with a commit pushed to origin.
    /// The work branch modifies feature.txt, which is used to test conflict scenarios.
    /// </summary>
    private void SetupBareRepoWithWorkBranch()
    {
        SetupBareRepo();

        RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, _workspaceDir);
        RunGitSync(_workspaceDir, "config", "user.email", "test@test.com");
        RunGitSync(_workspaceDir, "config", "user.name", "Test");

        RunGitSync(_workspaceDir, "checkout", "-b", _workBranch);
        File.WriteAllText(Path.Combine(_workspaceDir, "feature.txt"), "work branch content");
        RunGitSync(_workspaceDir, "add", ".");
        RunGitSync(_workspaceDir, "commit", "-m", "Add feature on work branch");
        RunGitSync(_workspaceDir, "push", "origin", _workBranch);

        RunGitSync(_workspaceDir, "checkout", "main");
    }

    /// <summary>
    /// Adds a non-conflicting change to main (different file from feature.txt).
    /// </summary>
    private void AddNonConflictingChangeToMain()
    {
        var tempClone = Path.Combine(Path.GetTempPath(), $"mergestep-nc-{Guid.NewGuid():N}"[..28]);
        try
        {
            RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, tempClone);
            RunGitSync(tempClone, "config", "user.email", "test@test.com");
            RunGitSync(tempClone, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(tempClone, "other-file.txt"), "new content on main");
            RunGitSync(tempClone, "add", ".");
            RunGitSync(tempClone, "commit", "-m", "non-conflicting change on main");
            RunGitSync(tempClone, "push", "origin", "main");
        }
        finally
        {
            CleanupDirectory(tempClone);
        }
    }

    /// <summary>
    /// Adds a conflicting change to main (same file: feature.txt).
    /// </summary>
    private void AddConflictingChangeToMain()
    {
        var tempClone = Path.Combine(Path.GetTempPath(), $"mergestep-cf-{Guid.NewGuid():N}"[..28]);
        try
        {
            RunGitSync(Path.GetTempPath(), "clone", _bareRepoDir, tempClone);
            RunGitSync(tempClone, "config", "user.email", "test@test.com");
            RunGitSync(tempClone, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(tempClone, "feature.txt"), "conflicting content from main");
            RunGitSync(tempClone, "add", ".");
            RunGitSync(tempClone, "commit", "-m", "conflicting change on main");
            RunGitSync(tempClone, "push", "origin", "main");
        }
        finally
        {
            CleanupDirectory(tempClone);
        }
    }

    // ── Workflow configs ─────────────────────────────────────────────────

    /// <summary>
    /// Implementation stage: no MERGE_CONFLICT transition (conflicts handled inline).
    /// </summary>
    private static WorkflowConfig BuildImplConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [ImplColumnId] = new("Ready for Implementation", "senior_engineer", "agent_run",
                    "Implement {TaskName}",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn(CompleteCol),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn(QuestionsCol),
                        ["ERROR"] = TransitionTarget.ForColumn(ErrorCol),
                    },
                    GitBehavior: "commit_and_push"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" }),
            }).Normalised();
    }

    /// <summary>
    /// Test stage: has MERGE_CONFLICT transition and merge resolution config.
    /// </summary>
    private static WorkflowConfig BuildTestConfigWithMergeResolution()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TestColumnId] = new("Ready for Test", "qa", "agent_run",
                    "Test {TaskName}",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn(CompleteCol),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn(QuestionsCol),
                        ["ERROR"] = TransitionTarget.ForColumn(ErrorCol),
                        ["MERGE_CONFLICT"] = TransitionTarget.ForColumn(ReadyForImplCol),
                    },
                    GitBehavior: "discard"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["qa"] = new("sonnet-4.6", "You are a QA engineer.",
                    new List<string> { "Test Plan" }),
                ["merge_resolver"] = new("sonnet-4.6", "You are a merge resolver.",
                    new List<string>()),
            },
            MergeResolution: new MergeResolutionConfig("merge_resolver")).Normalised();
    }

    // ── Git helpers ──────────────────────────────────────────────────────

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
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}");
        }
    }
}
