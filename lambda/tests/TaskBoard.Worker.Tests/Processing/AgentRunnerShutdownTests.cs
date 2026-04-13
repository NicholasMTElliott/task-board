using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Tests for AgentRunner's inter-step graceful shutdown behavior.
///
/// The inter-step check fires when <see cref="ShutdownCoordinator.IsShutdownRequested"/> is true
/// at the start of any step with stepIndex &gt; 0. At that point the runner preserves partial work
/// and restores the card to its trigger column for re-processing.
/// </summary>
public class AgentRunnerShutdownTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;

    private const string TriggerColumn = "Ready for Implementation";
    private const string CardId = "card-54";
    private const string CardTitle = "Shutdown Feature";
    private const string BoardId = "board-1";

    public AgentRunnerShutdownTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sd-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(CardId, CardTitle, "Description", TriggerColumn),
            });
        _boardClient.GetCardCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CardComment>>(new List<CardComment>()));
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Build a 2-step workflow config with the specified gitBehavior.</summary>
    private static WorkflowConfig BuildTwoStepConfig(string gitBehavior = "discard")
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TriggerColumn] = new(
                    Name: "Ready for Implementation",
                    Role: null,
                    GateType: "agent_run",
                    TaskPrompt: null,
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Implementing"),
                        ["COMPLETE"] = TransitionTarget.ForColumn("Ready for Test"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("Implementation Questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("Error"),
                    },
                    GitBehavior: gitBehavior,
                    Steps:
                    [
                        new WorkflowStep("implement", "implementer",
                            TaskPrompt: "Implement the feature"),
                        new WorkflowStep("code_review", "implementer",
                            TaskPrompt: "Review the implementation"),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-sonnet-4-6", "You are an implementer.",
                    new List<string> { "Implementation" }),
            });
    }

    private AgentRunner CreateRunner(
        IAgentExecutor executor,
        string gitBehavior = "discard",
        ShutdownCoordinator? coordinator = null)
    {
        var config = BuildTwoStepConfig(gitBehavior);
        return new AgentRunner(
            _boardClient,
            AgentExecutorResolver.ForSingleExecutor(executor),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance,
            shutdownCoordinator: coordinator);
    }

    private IAgentExecutor MakeCompletingExecutor() =>
        Substitute.For<IAgentExecutor>()
            .WithResult(new AgentResult(AgentOutcome.COMPLETE, "Step complete"));

    // ── Test 1: stepIndex == 0 gate ──────────────────────────────────

    /// <summary>
    /// The inter-step shutdown check has a guard of <c>stepIndex &gt; 0</c>.
    /// Even with shutdown pre-requested, step 0 must execute normally.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownPreSet_StepZeroStillExecutes()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "step ok"));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown(); // set before run

        var runner = CreateRunner(executor, "discard", coordinator);

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Step 0 must have executed (exactly once — shutdown triggered before step 1)
        await executor.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    // ── Test 2: discard path ─────────────────────────────────────────

    /// <summary>
    /// For discard stages, the inter-step shutdown check cleans up the worktree
    /// and restores the card to its trigger column.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownAfterStep0_DiscardStage_RestoresCardToTriggerColumn()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "step ok"));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();

        var runner = CreateRunner(executor, "discard", coordinator);

        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        await _boardClient.Received(1).MoveCardToColumnAsync(
            CardId, TriggerColumn, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// For discard stages, after the inter-step shutdown check the leaf worktree
    /// directory is cleaned up.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownAfterStep0_DiscardStage_CleansUpWorktree()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "step ok"));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();

        var runner = CreateRunner(executor, "discard", coordinator);

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Leaf worktree directory should not exist after cleanup
        var aiboardDir = Path.Combine(_worktreeBase, "aiboard");
        var hasLeafWorktree = Directory.Exists(aiboardDir)
            && Directory.EnumerateDirectories(aiboardDir).Any();
        Assert.False(hasLeafWorktree,
            "Leaf worktree directory should have been cleaned up but child directories remain under aiboard/");
    }

    // ── Test 3: commit_and_push path ─────────────────────────────────

    /// <summary>
    /// For commit_and_push stages, the inter-step shutdown check commits partial work
    /// (or skips if nothing staged) and restores the card to its trigger column.
    /// Push fails silently since the test repo has no remote.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownAfterStep0_CommitAndPushStage_RestoresCardToTriggerColumn()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "step ok"));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();

        var runner = CreateRunner(executor, "commit_and_push", coordinator);

        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        await _boardClient.Received(1).MoveCardToColumnAsync(
            CardId, TriggerColumn, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// For commit_and_push stages, step 1 is skipped entirely — executor is called only once.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownAfterStep0_CommitAndPushStage_SecondStepNotExecuted()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "step ok"));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();

        var runner = CreateRunner(executor, "commit_and_push", coordinator);

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Only step 0 should have run; step 1 is skipped by the shutdown check
        await executor.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    // ── Test 4: commit failure resilience ────────────────────────────

    /// <summary>
    /// When CommitAsync fails (e.g., corrupt git state), the runner logs a warning
    /// but still restores the card to its trigger column and returns COMPLETE.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownAfterStep0_CommitFails_CardStillRestored()
    {
        // After step 0 completes, destroy the worktree's .git file so CommitAsync fails.
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ctx = callInfo.Arg<AgentExecutionContext>();
                // Break git in the worktree by removing its .git pointer
                var gitFile = Path.Combine(ctx.WorkspacePath, ".git");
                if (File.Exists(gitFile))
                    File.Delete(gitFile);
                return Task.FromResult(new AgentResult(AgentOutcome.COMPLETE, "step ok"));
            });

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();

        var runner = CreateRunner(executor, "commit_and_push", coordinator);

        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        // Card restored despite commit failure
        await _boardClient.Received(1).MoveCardToColumnAsync(
            CardId, TriggerColumn, Arg.Any<CancellationToken>());
    }

    // ── Test 5: card restore failure resilience ───────────────────────

    /// <summary>
    /// When card restore fails (board API down), the runner logs a warning
    /// and still returns COMPLETE — the outcome is not changed to ERROR.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShutdownAfterStep0_CardRestoreFails_ReturnsComplete()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "step ok"));

        // Make board client throw when restoring to trigger column
        _boardClient.MoveCardToColumnAsync(CardId, TriggerColumn, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("board API down")));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown();

        var runner = CreateRunner(executor, "discard", coordinator);

        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // COMPLETE is returned even when card restore fails (best-effort)
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }
}

/// <summary>
/// Extension to make NSubstitute executor setup more readable.
/// </summary>
internal static class SubstituteExecutorExtensions
{
    internal static IAgentExecutor WithResult(this IAgentExecutor executor, AgentResult result)
    {
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return executor;
    }
}
