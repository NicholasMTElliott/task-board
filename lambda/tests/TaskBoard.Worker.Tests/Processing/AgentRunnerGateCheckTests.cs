using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

public class AgentRunnerGateCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    private const string TargetCardId = "card-gate";
    private const string TargetCardTitle = "Gate Check Feature";
    private const string BoardId = "board-1";
    private const string DesignListId = "list-design";
    private const string ImplListId = "list-impl";

    public AgentRunnerGateCheckTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gatecheck-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    // ── Gate PASS: normal COMPLETE transition ────────────────────────

    [Fact]
    public async Task GateCheck_Pass_CardMovesToCompleteColumn()
    {
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                // First call = main agent, second call = gate check
                return new AgentResult(AgentOutcome.COMPLETE, callIndex == 1 ? "Impl done" : "PASS: all good");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Card should move to COMPLETE column (list-designed)
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-designed", Arg.Any<CancellationToken>());
    }

    // ── Gate CONCERNS: card moves to NEEDS_INFO column ──────────────

    [Fact]
    public async Task GateCheck_Concerns_CardMovesToNeedsInfoColumn()
    {
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Impl done")
                    : new AgentResult(AgentOutcome.NEEDS_INFO, "Missing error handling for edge case",
                        [new AgentQuestion("Should null inputs be handled?")]);
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Contains("Missing error handling", result.ErrorDetail);

        // Card moves to NEEDS_INFO column
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-questions", Arg.Any<CancellationToken>());

        // Gate comment posted
        await _boardClient.Received().UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Gate Check: Concerns")),
            Arg.Is<string>(s => s.Contains("gate-check:")),
            Arg.Any<CancellationToken>());
    }

    // ── Gate FAIL: card moves to GATE_FAIL column ───────────────────

    [Fact]
    public async Task GateCheck_Fail_CardMovesToGateFailColumn()
    {
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Impl done")
                    : new AgentResult(AgentOutcome.ERROR, "Requirements 2 and 3 not addressed");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("Requirements 2 and 3", result.ErrorDetail);

        // Card moves to GATE_FAIL column (re-trigger to same state)
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, DesignListId, Arg.Any<CancellationToken>());
    }

    // ── Gate FAIL without GATE_FAIL transition: falls back to ERROR ─

    [Fact]
    public async Task GateCheck_Fail_NoGateFailTransition_FallsBackToError()
    {
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Done")
                    : new AgentResult(AgentOutcome.ERROR, "Missing requirement");
            });

        // Config without GATE_FAIL transition
        var config = BuildGateCheckConfigNoGateFail();
        var runner = CreateRunner(executor, config);
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);

        // Falls back to ERROR column
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-error", Arg.Any<CancellationToken>());
    }

    // ── No gate configured: existing behavior unchanged ─────────────

    [Fact]
    public async Task NoGateCheck_NormalCompleteTransition()
    {
        var executor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.COMPLETE
        };

        var config = BuildNoGateCheckConfig();
        var runner = CreateRunner(executor, config);
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-designed", Arg.Any<CancellationToken>());
    }

    // ── Gate executor throws: pipeline proceeds with warning ────────

    [Fact]
    public async Task GateCheck_ExecutorThrows_ProceedsWithWarning()
    {
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                if (callIndex == 1)
                    return new AgentResult(AgentOutcome.COMPLETE, "Impl done");
                throw new TimeoutException("Gate check timed out");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Pipeline proceeds despite gate failure
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Warning comment posted
        await _boardClient.Received().UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Gate Check Warning") && s.Contains("timed out")),
            Arg.Is<string>(s => s.Contains("gate-check:")),
            Arg.Any<CancellationToken>());
    }

    // ── Empty diff: gate check skipped ──────────────────────────────

    [Fact]
    public async Task GateCheck_EmptyDiff_SkippedAndProceedsNormally()
    {
        // For commit stages, empty diff means git diff HEAD returns nothing.
        // The agent "completes" but makes no file changes in the worktree.
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                // Main agent: return COMPLETE without creating any files
                return new AgentResult(AgentOutcome.COMPLETE, "Nothing changed");
            });

        // Use commit_and_push stage where diff is from git diff HEAD (will be empty)
        var runner = CreateRunner(executor, BuildGateCheckConfig("commit_and_push"));
        SetupBoardCards(ImplListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        // Gate executor should only be called once (for the main agent step)
        // because gate check is skipped on empty diff
        await executor.Received(1).ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    // ── Retry limit: escalates to NEEDS_INFO after max failures ─────

    [Fact]
    public async Task GateCheck_RetryLimitExceeded_EscalatesToNeedsInfo()
    {
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Impl done")
                    : new AgentResult(AgentOutcome.ERROR, "Still failing");
            });

        // MaxRetries = 2 (default), so with 2 prior failures, this should escalate
        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        // Simulate 2 prior gate failures via existing comments
        // (must be after SetupBoardCards to override the empty default)
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>
            {
                new("bot", "<!-- gate-check:Design result:ERROR attempt:1 -->\n\nFailed first time", DateTimeOffset.UtcNow.AddHours(-2)),
                new("bot", "<!-- gate-check:Design result:ERROR attempt:2 -->\n\nFailed second time", DateTimeOffset.UtcNow.AddHours(-1)),
            });

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Escalated to NEEDS_INFO instead of ERROR
        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);

        // Card moves to questions column, not GATE_FAIL
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-questions", Arg.Any<CancellationToken>());

        // Escalation comment posted
        await _boardClient.Received().UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Escalated to Human Review")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ── Gate check on commit_and_push stage uses git diff ────────────

    [Fact]
    public async Task GateCheck_CommitStage_UsesDiffNotTaskFile()
    {
        AgentExecutionContext? capturedGateContext = null;
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                var ctx = ci.Arg<AgentExecutionContext>();
                if (callIndex == 1)
                {
                    // Main agent: create a file in the worktree to produce a diff
                    var newFile = Path.Combine(ctx.WorkspacePath, "new-feature.cs");
                    File.WriteAllText(newFile, "public class NewFeature { }");
                    return new AgentResult(AgentOutcome.COMPLETE, "Implemented feature");
                }

                // Gate check: capture context
                capturedGateContext = ctx;
                return new AgentResult(AgentOutcome.COMPLETE, "PASS");
            });

        var config = BuildGateCheckConfig("commit_and_push");
        var runner = CreateRunner(executor, config);
        SetupBoardCards(ImplListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Gate check should have been called
        Assert.NotNull(capturedGateContext);
        // The gate prompt should contain the diff (new-feature.cs content)
        Assert.Contains("new-feature.cs", capturedGateContext!.TaskPrompt);
        Assert.Contains("NewFeature", capturedGateContext.TaskPrompt);
    }

    // ── Gate check invoked with correct provider params ─────────────

    [Fact]
    public async Task GateCheck_UsesCorrectProviderParams()
    {
        AgentExecutionContext? capturedGateContext = null;
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                if (callIndex == 2)
                    capturedGateContext = ci.Arg<AgentExecutionContext>();
                return new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedGateContext);
        Assert.Equal("none", capturedGateContext!.ProviderParams!["permissionMode"]);
        Assert.Equal("low", capturedGateContext.ProviderParams["effort"]);
        Assert.False(capturedGateContext.ProviderParams.ContainsKey("maxBudget"),
            "Gate check should not set a budget cap — budget was removed per design decision.");
        Assert.Equal("claude-haiku-4-5-20251001", capturedGateContext.Model);
    }

    // ── Agent returns non-COMPLETE: gate check does NOT run ─────────

    [Fact]
    public async Task AgentNeedsInfo_GateCheckNotRun()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.NEEDS_INFO, "Need more info",
                [new AgentQuestion("What scale?")]));

        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"));
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        // Executor called only once (for the main step, not for gate)
        await executor.Received(1).ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private AgentRunner CreateRunner(IAgentExecutor executor, WorkflowConfig config)
    {
        var normalisedConfig = config.Normalised();
        return new AgentRunner(
            _boardClient, AgentExecutorResolver.ForSingleExecutor(executor), _taskFileManager, _gitWorkspaceManager,
            normalisedConfig, new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, normalisedConfig, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            NullLogger<AgentRunner>.Instance);
    }

    private void SetupBoardCards(string listId)
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle, "Implement the gate check feature", listId),
            });

        // Default: no existing comments (no prior gate failures)
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
    }

    private static WorkflowConfig BuildGateCheckConfig(string gitBehavior)
    {
        var listId = gitBehavior == "discard" ? DesignListId : ImplListId;
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [listId] = new(
                    gitBehavior == "discard" ? "Design" : "Implementation",
                    "senior_engineer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                        ["GATE_FAIL"] = TransitionTarget.ForColumn(listId),
                    },
                    GitBehavior: gitBehavior,
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Gate check for '{TaskName}' ({TaskId}).\n\n## Task\n{TaskBody}\n\n## Changes\n{Diff}\n\n## Report\n{AgentReport}")),
                ["list-designed"] = new("Designed", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-questions"] = new("Questions", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" }),
                ["gate_checker"] = new("claude-haiku-4-5-20251001", "You are a gate checker.",
                    new List<string>()),
            });
    }

    private static WorkflowConfig BuildGateCheckConfigNoGateFail()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                        // No GATE_FAIL transition
                    },
                    GitBehavior: "discard",
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Gate check.\n\n## Task\n{TaskBody}\n\n## Changes\n{Diff}\n\n## Report\n{AgentReport}")),
                ["list-designed"] = new("Designed", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-questions"] = new("Questions", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" }),
                ["gate_checker"] = new("claude-haiku-4-5-20251001", "You are a gate checker.",
                    new List<string>()),
            });
    }

    private static WorkflowConfig BuildNoGateCheckConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard"),
                ["list-designed"] = new("Designed", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-questions"] = new("Questions", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" }),
            });
    }

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
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
