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

    [Fact]
    public async Task GateCheck_Fail_FlagsCandidateGroupWinnersAsRegressed()
    {
        // V22: when a same-run gate check returns ERROR, AgentRunner should
        // call IRunStore.FlagWinnersRegressedForRunAsync so any candidate-group
        // winners promoted earlier in this run are marked winner_regressed=true.
        // The test exercises the gate-fail path (not a candidate group itself —
        // that's a separate fixture). What matters here is that the side effect
        // fires regardless of whether this run actually had a candidate group;
        // the SQL UPDATE is a no-op when no winners exist.
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Impl done")
                    : new AgentResult(AgentOutcome.ERROR, "Gate fails");
            });

        var recording = new GateRecordingRunStore();
        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"), runStore: recording);
        SetupBoardCards(DesignListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.True(recording.WinnerRegressedFlags >= 1,
            $"Expected FlagWinnersRegressedForRunAsync to be called on gate fail; got {recording.WinnerRegressedFlags} call(s)");
    }

    [Fact]
    public async Task GateCheck_Pass_DoesNotFlagWinnersAsRegressed()
    {
        // Negative case: a passing gate must not trigger the regression flag.
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Impl done")
                    : new AgentResult(AgentOutcome.COMPLETE, "Gate passes");
            });

        var recording = new GateRecordingRunStore();
        var runner = CreateRunner(executor, BuildGateCheckConfig("discard"), runStore: recording);
        SetupBoardCards(DesignListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(0, recording.WinnerRegressedFlags);
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
        // For commit stages, the gate check diffs canonical's working tree
        // against the run-start canonical SHA. In single-agent flow with
        // no in-run promotions, that SHA equals the current HEAD, so a
        // commit_and_push step where the agent writes nothing produces an
        // empty diff and the gate check is skipped.
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                // Main agent: return COMPLETE without creating any files
                return new AgentResult(AgentOutcome.COMPLETE, "Nothing changed");
            });

        // Use commit_and_push stage where the gate-check diff is computed
        // against the run-start canonical SHA (== HEAD here, no commits)
        // and so will be empty.
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

    // ── Regression guard for v0.0.20 KvA card #3: candidate-flow gate check ──

    [Fact]
    public async Task GateCheck_CommitStage_SeesDiff_WhenHEADAdvancedDuringStep()
    {
        // Simulates the candidate-flow scenario where a candidate group's
        // `git reset --hard {winner-branch}` advances canonical's HEAD AND
        // resets the working tree to match HEAD before the gate check runs.
        // Pre-fix, the gate check called `git diff HEAD` on a clean working
        // tree and got nothing — the gate then skipped despite real
        // committed work being present. The fix captures runStartCanonicalSha
        // before the step loop and uses it as the diff base, so the gate
        // check still surfaces the work.
        //
        // We don't spin up a real candidate group here (that's covered at the
        // CandidateExecutor level in CandidateExecutorEvaluatorPromptTests).
        // Instead the substitute agent itself does what the orchestrator+
        // promotion would have done in the candidate flow: write a tracked
        // file AND commit it. Post-step the worktree is clean (HEAD ahead of
        // runStartCanonicalSha by one commit) — the same shape produced by
        // a candidate promotion.
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
                    var newFile = Path.Combine(ctx.WorkspacePath, "promoted-feature.cs");
                    File.WriteAllText(newFile, "public class PromotedFeature { }");
                    RunGitSync(ctx.WorkspacePath, "add", "promoted-feature.cs");
                    RunGitSync(ctx.WorkspacePath, "commit", "-m", "promote winner");
                    return new AgentResult(AgentOutcome.COMPLETE, "Promoted");
                }
                capturedGateContext = ctx;
                return new AgentResult(AgentOutcome.COMPLETE, "PASS");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig("commit_and_push"));
        SetupBoardCards(ImplListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedGateContext);
        // The committed file must appear in the gate prompt's {Diff} slot.
        // Pre-fix this assertion fails: the diff was empty because the
        // working tree matched HEAD post-commit.
        Assert.Contains("promoted-feature.cs", capturedGateContext!.TaskPrompt);
        Assert.Contains("PromotedFeature", capturedGateContext.TaskPrompt);
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

    private AgentRunner CreateRunner(IAgentExecutor executor, WorkflowConfig config, IRunStore? runStore = null)
    {
        var normalisedConfig = config.Normalised();
        return new AgentRunner(
            _boardClient, AgentExecutorResolver.ForSingleExecutor(executor), _taskFileManager, _gitWorkspaceManager,
            normalisedConfig, new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, normalisedConfig, new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            runStore ?? NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
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

    /// <summary>Counts V22 reliability-signal calls; everything else is no-op.</summary>
    private sealed class GateRecordingRunStore : IRunStore
    {
        public int WinnerRegressedFlags { get; private set; }
        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct) => Task.CompletedTask;
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct)
        {
            WinnerRegressedFlags++;
            return Task.CompletedTask;
        }
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult<CacheCandidateRecord?>(null);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }
}
