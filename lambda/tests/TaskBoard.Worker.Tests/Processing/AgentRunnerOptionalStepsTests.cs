using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class AgentRunnerOptionalStepsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    private const string TargetCardId = "card-opt";
    private const string TargetCardTitle = "Optional Steps Test Feature";
    private const string BoardId = "board-1";
    private const string TriggerListId = "list-impl";

    public AgentRunnerOptionalStepsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "optionalsteps-tests-" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── Gate pass + optional step requested → executes optional step ─

    [Fact]
    public async Task GateCheck_Pass_WithRequestedSteps_ExecutesOptionalStep()
    {
        var callLog = new List<string>();
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<AgentExecutionContext>();
                // Gate checks use permissionMode: none; optional steps do not
                var isGate = ctx.ProviderParams?.TryGetValue("permissionMode", out var pm) == true
                    && pm == "none";
                if (ctx.TaskPrompt.Contains("implement the feature"))
                {
                    callLog.Add("mandatory");
                    return new AgentResult(AgentOutcome.COMPLETE, "Implementation done.");
                }
                if (isGate)
                {
                    callLog.Add("gate");
                    return new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit"]);
                }
                // Optional step
                callLog.Add("optional:security_audit");
                return new AgentResult(AgentOutcome.COMPLETE, "No security issues found.");
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(3, callLog.Count); // mandatory + gate + optional
        Assert.Contains("optional:security_audit", callLog);

        // Card moved to COMPLETE column
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-done", Arg.Any<CancellationToken>());

        // Optional step comment posted with optional: marker
        await _boardClient.Received().UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Optional Step: security_audit")),
            Arg.Is<string>(s => s.Contains("agent-step:optional:security_audit")),
            Arg.Any<CancellationToken>());
    }

    // ── Gate pass + empty requested steps → skips optional steps ─────

    [Fact]
    public async Task GateCheck_Pass_WithEmptyRequestedSteps_SkipsOptionalSteps()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Done")  // mandatory
                    : new AgentResult(AgentOutcome.COMPLETE, "PASS",  // gate
                        RequestedSteps: new List<string>());           // empty
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, callCount); // only mandatory + gate
    }

    // ── Gate pass + null requested steps → skips optional steps ──────

    [Fact]
    public async Task GateCheck_Pass_NullRequestedSteps_SkipsOptionalSteps()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Done")
                    : new AgentResult(AgentOutcome.COMPLETE, "PASS"); // no RequestedSteps
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, callCount);
    }

    // ── Gate requests invalid step names → skipped with warning ──────

    [Fact]
    public async Task GateCheck_Pass_InvalidStepNames_SkippedProceedsCOMPLETE()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Done")
                    : new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["hallucinated_step", "another_fake"]);
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Invalid names skipped — still COMPLETE
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, callCount); // no optional step executed
    }

    // ── Gate requests mix of valid and invalid → only valid executes ──

    [Fact]
    public async Task GateCheck_Pass_MixedValidAndInvalidStepNames_OnlyValidExecutes()
    {
        var callLog = new List<string>();
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callLog.Add(ci.Arg<AgentExecutionContext>().TaskPrompt.Contains("security_audit")
                    && callLog.Count >= 2
                    ? "optional"
                    : callLog.Count == 0 ? "mandatory" : "gate");

                return callLog.Count == 2
                    ? new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit", "hallucinated_step"])
                    : new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(3, callLog.Count); // mandatory + gate + 1 valid optional (not the hallucinated one)
    }

    // ── Optional step returns NEEDS_INFO → halts chain ───────────────

    [Fact]
    public async Task OptionalStep_NeedsInfo_HaltsChain_CardMovesToQuestionsColumn()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Done"),                // mandatory
                    2 => new AgentResult(AgentOutcome.COMPLETE, "PASS",                 // gate
                        RequestedSteps: ["security_audit"]),
                    _ => new AgentResult(AgentOutcome.NEEDS_INFO, "Auth concerns",      // optional
                        [new AgentQuestion("Is token storage secure?")])
                };
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Contains("Auth concerns", result.ErrorDetail);

        // Card moves to NEEDS_INFO column, not COMPLETE
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-questions", Arg.Any<CancellationToken>());
        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            TargetCardId, "list-done", Arg.Any<CancellationToken>());
    }

    // ── Optional step returns ERROR → halts chain ────────────────────

    [Fact]
    public async Task OptionalStep_Error_HaltsChain_CardMovesToErrorColumn()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Done"),
                    2 => new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit"]),
                    _ => new AgentResult(AgentOutcome.ERROR, "Critical SQL injection vulnerability found.")
                };
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);

        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-error", Arg.Any<CancellationToken>());
    }

    // ── Multiple optional steps, all COMPLETE ─────────────────────────

    [Fact]
    public async Task MultipleOptionalSteps_AllComplete_ProceedsToFinalTransition()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 2
                    ? new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit", "performance_review"])
                    : new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var config = BuildConfigWithOptionalSteps(includePerformanceReview: true);
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(4, callCount); // mandatory + gate + security_audit + performance_review

        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-done", Arg.Any<CancellationToken>());
    }

    // ── Multiple optional steps, second fails ─────────────────────────

    [Fact]
    public async Task MultipleOptionalSteps_SecondFails_HaltsAtSecond()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Done"),           // mandatory
                    2 => new AgentResult(AgentOutcome.COMPLETE, "PASS",            // gate
                        RequestedSteps: ["security_audit", "performance_review"]),
                    3 => new AgentResult(AgentOutcome.COMPLETE, "Secure."),        // security_audit
                    _ => new AgentResult(AgentOutcome.ERROR, "Performance issue.") // performance_review
                };
            });

        var config = BuildConfigWithOptionalSteps(includePerformanceReview: true);
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Equal(4, callCount); // halted at step 4
    }

    // ── Gate check without optional steps configured → no optional steps

    [Fact]
    public async Task GateCheck_RequestsSteps_ButNoOptionalStepsConfigured_ProceedsNormally()
    {
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Done")
                    : new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit"]); // requested but no catalog configured
            });

        var config = BuildConfigWithNoOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // No optional steps run — requested steps are silently ignored
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, callCount);
    }

    // ── providerParams merging ─────────────────────────────────────────

    [Fact]
    public async Task OptionalStep_ProviderParams_StepOverridesState()
    {
        AgentExecutionContext? capturedOptionalContext = null;
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount == 3)
                    capturedOptionalContext = ci.Arg<AgentExecutionContext>();
                return callCount == 2
                    ? new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit"])
                    : new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var config = BuildConfigWithOptionalSteps(stepProviderParams: new Dictionary<string, string>
        {
            ["effort"] = "max",  // overrides state's "medium"
            ["extraKey"] = "extraVal"
        });
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedOptionalContext);
        // Step effort "max" overrides state effort "medium"
        Assert.Equal("max", capturedOptionalContext!.ProviderParams!["effort"]);
        Assert.Equal("extraVal", capturedOptionalContext.ProviderParams["extraKey"]);
    }

    [Fact]
    public async Task OptionalStep_ProviderParams_NullStepParams_UsesStateParams()
    {
        AgentExecutionContext? capturedOptionalContext = null;
        var callCount = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount == 3)
                    capturedOptionalContext = ci.Arg<AgentExecutionContext>();
                return callCount == 2
                    ? new AgentResult(AgentOutcome.COMPLETE, "PASS",
                        RequestedSteps: ["security_audit"])
                    : new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        // No step-level providerParams
        var config = BuildConfigWithOptionalSteps(stepProviderParams: null);
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedOptionalContext);
        // Falls back to state-level params
        Assert.Equal("medium", capturedOptionalContext!.ProviderParams!["effort"]);
    }

    // ── Gate check + optional steps: full end-to-end pipeline ─────────

    [Fact]
    public async Task FullPipeline_MandatorySteps_GateWithOptionalSteps_Complete()
    {
        var executionOrder = new List<string>();
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<AgentExecutionContext>();
                var step = executionOrder.Count switch
                {
                    0 => "mandatory",
                    1 => "gate",
                    _ => "optional"
                };
                executionOrder.Add(step);

                return step == "gate"
                    ? new AgentResult(AgentOutcome.COMPLETE, "All requirements met.",
                        RequestedSteps: ["security_audit"])
                    : new AgentResult(AgentOutcome.COMPLETE, "Done.");
            });

        var config = BuildConfigWithOptionalSteps();
        var runner = CreateRunner(executor, config);
        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify execution order
        Assert.Equal(["mandatory", "gate", "optional"], executionOrder);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Card moved to COMPLETE
        await _boardClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-done", Arg.Any<CancellationToken>());

        // Optional step comment with correct marker
        await _boardClient.Received().UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Optional Step: security_audit")),
            Arg.Is<string>(s => s == "<!-- agent-step:optional:security_audit -->"),
            Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private AgentRunner CreateRunner(IAgentExecutor executor, WorkflowConfig config)
    {
        return new AgentRunner(
            _boardClient, executor, _taskFileManager, _gitWorkspaceManager,
            config.Normalised(), new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<AgentRunner>.Instance);
    }

    private void SetupBoardCards()
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle, "Build the optional steps feature.", TriggerListId),
            });

        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
    }

    private static WorkflowConfig BuildConfigWithOptionalSteps(
        bool includePerformanceReview = false,
        Dictionary<string, string>? stepProviderParams = null)
    {
        var optionalSteps = new List<OptionalStepDefinition>
        {
            new OptionalStepDefinition(
                "security_audit", "specialist_reviewer",
                TaskPrompt: "Perform a security_audit of this implementation.",
                ProviderParams: stepProviderParams)
        };

        if (includePerformanceReview)
        {
            optionalSteps.Add(new OptionalStepDefinition(
                "performance_review", "specialist_reviewer",
                TaskPrompt: "Perform a performance_review of this implementation."));
        }

        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TriggerListId] = new(
                    "Implementation", "senior_engineer", "agent_run",
                    "implement the feature for {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                        ["GATE_FAIL"] = TransitionTarget.ForColumn(TriggerListId),
                    },
                    GitBehavior: "discard",
                    ProviderParams: new Dictionary<string, string> { ["effort"] = "medium" },
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Check security_audit for '{TaskName}'.\n\n## Changes\n{Diff}\n\n## Report\n{AgentReport}"),
                    OptionalSteps: optionalSteps),
                ["list-done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
                ["list-questions"] = new("Questions", null, "holding", null, new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new("Error", null, "holding", null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("claude-opus-4-6", "You are a Senior Engineer.",
                    new List<string> { "Implementation" }),
                ["gate_checker"] = new("claude-haiku-4-5-20251001", "You are a gate checker.",
                    new List<string>()),
                ["specialist_reviewer"] = new("claude-sonnet-4-6", "You are a specialist reviewer.",
                    new List<string>()),
            });
    }

    private static WorkflowConfig BuildConfigWithNoOptionalSteps()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TriggerListId] = new(
                    "Implementation", "senior_engineer", "agent_run",
                    "implement the feature for {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                        ["GATE_FAIL"] = TransitionTarget.ForColumn(TriggerListId),
                    },
                    GitBehavior: "discard",
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Gate check.\n\n## Changes\n{Diff}\n\n## Report\n{AgentReport}"))
                // No OptionalSteps
                ,
                ["list-done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
                ["list-questions"] = new("Questions", null, "holding", null, new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new("Error", null, "holding", null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("claude-opus-4-6", "You are a Senior Engineer.",
                    new List<string> { "Implementation" }),
                ["gate_checker"] = new("claude-haiku-4-5-20251001", "You are a gate checker.",
                    new List<string>()),
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

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }
}
