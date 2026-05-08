using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Regression tests for code-review findings against the rerun-redesign work
/// (Phase 1 / Phase 2 fixes). Each test pins one specific bug behaviour so a
/// future refactor can't quietly re-introduce it:
///
/// <list type="bullet">
///   <item><b>Finding 1</b> — <c>SetStateEntryShaAsync</c> must run AFTER
///   <c>CreateRunAsync</c>. Pre-fix the UPDATE matched zero rows and the
///   state-entry SHA was silently lost on every run.</item>
///
///   <item><b>Finding 2</b> — the cache gate must hash the plain task prompt,
///   NOT the prompt with the re-run preamble injected. Pre-fix a re-run with
///   identical operator inputs always missed the cache because its hashed
///   prompt included the "bail if unchanged" preamble that the original run
///   never saw.</item>
///
///   <item><b>Finding 3</b> — the gate prompt's <c>{TaskBody}</c> placeholder
///   must use <c>currentBody</c> (kept fresh by the section_update path),
///   NOT the stale <c>targetCard.Body</c> snapshot from the initial fetch.
///   Pre-fix the gate saw the pre-step description and couldn't reason about
///   the managed sections the just-completed steps wrote.</item>
///
///   <item><b>Finding 7</b> — the agent's <c>section_update</c> directive
///   (raw JSON) must be persisted to <c>step_result.section_update_json</c>.
///   Pre-fix the V24 column was always null because the INSERT didn't bind
///   a value for it and the record had no field for it.</item>
/// </list>
/// </summary>
public class AgentRunnerRerunRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    private const string TargetCardId = "card-rerun";
    private const string TargetCardTitle = "Rerun Redesign Feature";
    private const string BoardId = "board-1";
    private const string DesignListId = "list-design";

    public AgentRunnerRerunRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rerun-tests-" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── Finding 1: state-entry SHA persists AFTER CreateRunAsync ─────

    [Fact]
    public async Task StateEntrySha_PersistedAfterCreateRunAsync_NotBefore()
    {
        // Pre-fix: SetStateEntryShaAsync(runId, ...) ran before
        // CreateRunAsync, so the UPDATE targeted a row that didn't exist yet
        // and the SHA was silently lost. The fix splits resolution
        // (GetEarliestStateEntryShaAsync, before CreateRunAsync) from
        // persistence (SetStateEntryShaAsync, after CreateRunAsync). This
        // test pins the post-fix ordering by recording the call sequence on
        // a fake IRunStore.

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "Done"));

        var recording = new OrderingRunStore();
        var runner = CreateRunner(executor, BuildBasicConfig(), runStore: recording);
        SetupBoardCards(DesignListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // CreateRunAsync must appear strictly before SetStateEntryShaAsync.
        // If SetStateEntryShaAsync wasn't called at all, treating that as a
        // pass would silently let the bug regress, so we also assert the
        // SHA was actually persisted (the canonical worktree HEAD exists).
        Assert.Contains("CreateRunAsync", recording.CallOrder);
        Assert.Contains("SetStateEntryShaAsync", recording.CallOrder);

        var createIdx = recording.CallOrder.IndexOf("CreateRunAsync");
        var setShaIdx = recording.CallOrder.IndexOf("SetStateEntryShaAsync");
        Assert.True(createIdx < setShaIdx,
            $"SetStateEntryShaAsync must run AFTER CreateRunAsync. Observed order: {string.Join(" -> ", recording.CallOrder)}");

        // The persisted SHA is non-empty — the fix's failure mode (UPDATE
        // matches zero rows because the runId doesn't exist) would still pass
        // the ordering check with a no-op call, but the recorded SHA would be
        // missing. Asserting both pins the full contract.
        Assert.NotNull(recording.PersistedStateEntrySha);
        Assert.NotEmpty(recording.PersistedStateEntrySha!);
    }

    // ── Finding 2: cache gate hashes the plain prompt, not preamble+plain ──

    [Fact]
    public async Task CacheGate_HashesPlainPrompt_NotPreambleAugmentedPrompt()
    {
        // Two runs with identical operator inputs:
        //   Run 1 — clean state. No preamble. step_result row stored.
        //   Run 2 — prior COMPLETE row + step marker present on the card.
        //           RerunPreambleBuilder injects preamble into the prompt
        //           the AGENT sees. But the cache gate must hash the PLAIN
        //           prompt (the cacheKeyPrompt captured before injection),
        //           so the persisted input_hash matches run 1's.
        //
        // Pre-fix: resolvedPrompt was mutated before the cache evaluation
        // → run 2 hashed "preamble + plain" → input_hash differed from run 1
        // → cache always missed on re-runs even when nothing changed.

        var executor = Substitute.For<IAgentExecutor>();
        var capturedAgentPrompts = new List<string>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedAgentPrompts.Add(ci.Arg<AgentExecutionContext>().TaskPrompt);
                return new AgentResult(AgentOutcome.COMPLETE, "Step done");
            });

        var capturingStore = new CapturingRunStore();
        var preambleBuilder = new RerunPreambleBuilder(
            capturingStore, NullLogger<RerunPreambleBuilder>.Instance);
        var cacheGate = new RerunCacheGate(
            capturingStore, NullLogger<RerunCacheGate>.Instance);

        var runner = CreateRunner(
            executor, BuildBasicConfig(), runStore: capturingStore,
            preambleBuilder: preambleBuilder, cacheGate: cacheGate);
        SetupBoardCards(DesignListId);

        // Run 1: no marker, no prior. Cache miss. input_hash computed and stored.
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        var run1Records = capturingStore.SavedStepResults
            .Where(r => r.StepName == "create_design")
            .ToList();
        Assert.Single(run1Records);
        var hashRun1 = run1Records[0].InputHash;
        Assert.NotNull(hashRun1);

        // Sanity: agent's run-1 prompt did NOT contain the preamble marker.
        Assert.DoesNotContain("RE-RUN OF PREVIOUSLY COMPLETED STEP", capturedAgentPrompts[0]);

        // Set up run 2 priors: marker comment on the card + a prior COMPLETE
        // step_result returned by GetStepResultsForCardAsync (preamble eligibility),
        // and a NULL prior on GetMostRecentCompleteForStepAsync so the cache
        // doesn't hit (we want to compare hashes on a miss, not a hit).
        capturingStore.PriorStepResultsForCard = [run1Records[0] with { RunId = "prior-run-1" }];
        // Cache gate prior intentionally null — we want a cache MISS on run 2 so
        // the input_hash gets computed and persisted afresh, lettingrelease us compare.
        capturingStore.PriorCacheCandidate = null;
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>
            {
                new("bot",
                    "<!-- aiboard-log kind:step state:Designing step:create_design -->\nPrior step output.",
                    DateTimeOffset.UtcNow.AddHours(-1)),
            });

        capturingStore.SavedStepResults.Clear();
        capturedAgentPrompts.Clear();

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Sanity: the agent's run-2 prompt DID contain the preamble (the
        // injection happened — without this, the test wouldn't be exercising
        // the bug path at all).
        Assert.NotEmpty(capturedAgentPrompts);
        Assert.Contains("RE-RUN OF PREVIOUSLY COMPLETED STEP", capturedAgentPrompts[0]);

        var run2Records = capturingStore.SavedStepResults
            .Where(r => r.StepName == "create_design")
            .ToList();
        Assert.Single(run2Records);
        var hashRun2 = run2Records[0].InputHash;

        // The actual assertion: input_hash is identical across the two runs
        // even though run 2's agent saw preamble + plain. Pre-fix this fails
        // because resolvedPrompt was mutated before being passed to the cache
        // gate, so run 2's hash included the preamble bytes.
        Assert.Equal(hashRun1, hashRun2);
    }

    // ── Finding 3: gate prompt sees post-section-update body ─────────

    [Fact]
    public async Task GateCheck_TaskBody_UsesCurrentBody_NotStaleTargetCardBody()
    {
        // Agent returns a SectionUpdate with replace strategy. After the step
        // the orchestrator updates currentBody via DescriptionWriter and writes
        // it to the board. The gate check that follows must substitute the
        // post-update body into {TaskBody}, NOT the original snapshot.
        //
        // Pre-fix: ResolvePromptPlaceholders -> .Replace("{TaskBody}", targetCard.Body)
        // — and targetCard was the initial fetch, so the gate saw the original
        // operator-only body without any of the agent's managed-section content.

        const string AgentSectionContent = "POSTUPDATE-MARKER-XYZ-design content";
        AgentExecutionContext? capturedGateContext = null;
        var callIndex = 0;

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                if (callIndex == 1)
                {
                    // Main agent: emit a SectionUpdate that replaces the section.
                    return new AgentResult(
                        AgentOutcome.COMPLETE,
                        "Designed",
                        Section: new SectionUpdate(
                            SectionUpdateStrategy.Replace,
                            Content: AgentSectionContent));
                }
                // Gate: capture the prompt for inspection.
                capturedGateContext = ci.Arg<AgentExecutionContext>();
                return new AgentResult(AgentOutcome.COMPLETE, "PASS");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig());
        SetupBoardCards(DesignListId, initialBody: "ORIGINAL-OPERATOR-BODY");

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedGateContext);
        // The agent's section content must appear in the gate's TaskBody slot —
        // proving currentBody (post-section-update) is what the gate sees.
        Assert.Contains(AgentSectionContent, capturedGateContext!.TaskPrompt);
    }

    [Fact]
    public async Task GateCheck_TaskBody_PreservesOperatorAuthoredPortion()
    {
        // Negative half of Finding 3: the operator-authored portion of the body
        // must STILL be visible in the gate prompt. ApplySectionUpdate appends
        // the managed section to the existing body; substituting currentBody
        // means the gate sees both "ORIGINAL-OPERATOR-BODY" and the new section.
        // This guards against an over-correction where the fix accidentally
        // discards the operator content.

        AgentExecutionContext? capturedGateContext = null;
        var callIndex = 0;

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                if (callIndex == 1)
                {
                    return new AgentResult(
                        AgentOutcome.COMPLETE,
                        "Designed",
                        Section: new SectionUpdate(
                            SectionUpdateStrategy.Replace,
                            Content: "Some new design"));
                }
                capturedGateContext = ci.Arg<AgentExecutionContext>();
                return new AgentResult(AgentOutcome.COMPLETE, "PASS");
            });

        var runner = CreateRunner(executor, BuildGateCheckConfig());
        SetupBoardCards(DesignListId, initialBody: "ORIGINAL-OPERATOR-BODY");

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedGateContext);
        Assert.Contains("ORIGINAL-OPERATOR-BODY", capturedGateContext!.TaskPrompt);
    }

    // ── Finding 6: rerun.diff.summaryThresholdBytes precedence ───────

    [Fact]
    public async Task GateCheck_DiffThreshold_PrefersRerunConfigOverGateCheckMaxDiffChars()
    {
        // Pre-fix: the gate diff threshold was always gateCheck.MaxDiffChars.
        // The fix prefers workflowConfig.Rerun.Diff.SummaryThresholdBytes
        // when set. With a tiny rerun threshold (1 byte) and a much larger
        // gateCheck.MaxDiffChars (the default 50_000), even a small diff
        // should trigger summary mode — proof that the rerun field wins.

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
                    // Main agent: write a small file so the gate has a non-empty diff
                    var newFile = Path.Combine(ctx.WorkspacePath, "feature.cs");
                    File.WriteAllText(newFile, "public class Feature { }");
                    return new AgentResult(AgentOutcome.COMPLETE, "Done");
                }
                capturedGateContext = ctx;
                return new AgentResult(AgentOutcome.COMPLETE, "PASS");
            });

        var config = BuildGateCheckConfigCommitMode(
            rerunDiffThresholdBytes: 1);  // forces summary mode for any non-empty diff
        var runner = CreateRunner(executor, config);
        SetupBoardCards("list-impl");

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedGateContext);
        // Summary-mode marker emitted by GitWorkspaceManager.BuildSummaryPacketAsync.
        // Pre-fix this assertion fails because gateCheck.MaxDiffChars (50_000)
        // is never reached, so the diff renders in raw mode — no summary header.
        Assert.Contains("## Summary mode (large diff)", capturedGateContext!.TaskPrompt);
    }

    [Fact]
    public async Task GateCheck_DiffThreshold_NoRerunConfig_FallsBackToGateCheckMaxDiffChars()
    {
        // Negative half: when the rerun config is absent, gateCheck.MaxDiffChars
        // continues to drive the threshold (preserves backward-compat for
        // workflows that haven't adopted the rerun.diff block). A small diff
        // well under the default 50_000-byte cap should NOT trigger summary
        // mode.

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
                    var newFile = Path.Combine(ctx.WorkspacePath, "feature.cs");
                    File.WriteAllText(newFile, "public class Feature { }");
                    return new AgentResult(AgentOutcome.COMPLETE, "Done");
                }
                capturedGateContext = ctx;
                return new AgentResult(AgentOutcome.COMPLETE, "PASS");
            });

        var config = BuildGateCheckConfigCommitMode(rerunDiffThresholdBytes: null);
        var runner = CreateRunner(executor, config);
        SetupBoardCards("list-impl");

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedGateContext);
        // Without the rerun threshold, the small diff stays in raw mode.
        Assert.DoesNotContain("## Summary mode (large diff)", capturedGateContext!.TaskPrompt);
    }

    // ── Finding 7: section_update_json reaches the StepResultRecord ──

    [Fact]
    public async Task StepResult_SectionUpdateJson_PopulatedFromAgentResult()
    {
        // Agent's structured output carries section_update; AgentOutputParser
        // captures both the typed SectionUpdate and the raw JSON via
        // GetRawText. AgentRunner forwards the raw JSON onto the StepResultRecord
        // so PgRunStore can persist it to step_result.section_update_json.

        const string ExpectedRawJson =
            """{"strategy":"replace","content":"Section content","open_questions":[]}""";

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(
                AgentOutcome.COMPLETE,
                "Done",
                Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Section content"),
                SectionUpdateJson: ExpectedRawJson));

        var capturingStore = new CapturingRunStore();
        var runner = CreateRunner(executor, BuildBasicConfig(), runStore: capturingStore);
        SetupBoardCards(DesignListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        var stepRecord = capturingStore.SavedStepResults
            .FirstOrDefault(r => r.StepName == "create_design");
        Assert.NotNull(stepRecord);
        // Pre-fix: this is null because AgentRunner didn't populate the field.
        Assert.Equal(ExpectedRawJson, stepRecord!.SectionUpdateJson);
    }

    [Fact]
    public async Task StepResult_SectionUpdateJson_NullWhenAgentDidNotEmitOne()
    {
        // Negative case: when the agent omits section_update entirely (legacy
        // role, gate, or explicit opt-out), the JSON column should be null —
        // not a serialized null or an empty-object placeholder.

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "Done"));

        var capturingStore = new CapturingRunStore();
        var runner = CreateRunner(executor, BuildBasicConfig(), runStore: capturingStore);
        SetupBoardCards(DesignListId);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        var stepRecord = capturingStore.SavedStepResults
            .FirstOrDefault(r => r.StepName == "create_design");
        Assert.NotNull(stepRecord);
        Assert.Null(stepRecord!.SectionUpdateJson);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private AgentRunner CreateRunner(
        IAgentExecutor executor,
        WorkflowConfig config,
        IRunStore? runStore = null,
        RerunPreambleBuilder? preambleBuilder = null,
        RerunCacheGate? cacheGate = null)
    {
        var normalisedConfig = config.Normalised();
        return new AgentRunner(
            _boardClient,
            AgentExecutorResolver.ForSingleExecutor(executor),
            _taskFileManager,
            _gitWorkspaceManager,
            normalisedConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, normalisedConfig, new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            runStore ?? NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance,
            rerunPreambleBuilder: preambleBuilder,
            cacheGate: cacheGate);
    }

    private void SetupBoardCards(string listId, string? initialBody = null)
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle,
                    initialBody ?? "Implement the rerun feature",
                    listId),
            });

        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
    }

    private static WorkflowConfig BuildBasicConfig() =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new(
                    "Design", "senior_engineer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps: [
                        new("create_design", "senior_engineer",
                            TaskPrompt: "Design {TaskName}"),
                    ]),
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

    private static WorkflowConfig BuildGateCheckConfigCommitMode(int? rerunDiffThresholdBytes) =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-impl"] = new(
                    "Implementation", "senior_engineer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-tested"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                        ["GATE_FAIL"] = TransitionTarget.ForColumn("list-impl"),
                    },
                    GitBehavior: "commit_and_push",
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Gate.\n\n## Body\n{TaskBody}\n\n## Diff\n{Diff}\n\n## Report\n{AgentReport}",
                        // Default ≈50_000; only the rerun threshold (when set) should win.
                        MaxDiffChars: 50_000)),
                ["list-tested"] = new("Tested", null, "manual_gate", null,
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
            },
            Rerun: rerunDiffThresholdBytes is { } t
                ? new RerunConfig(Diff: new RerunDiffConfig(SummaryThresholdBytes: t))
                : null);

    private static WorkflowConfig BuildGateCheckConfig() =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new(
                    "Design", "senior_engineer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                        ["GATE_FAIL"] = TransitionTarget.ForColumn(DesignListId),
                    },
                    GitBehavior: "discard",
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Gate.\n\n## Body\n{TaskBody}\n\n## Diff\n{Diff}\n\n## Report\n{AgentReport}")),
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

    /// <summary>
    /// Records the order of CreateRunAsync vs SetStateEntryShaAsync calls plus
    /// the SHA value passed to SetStateEntryShaAsync. Other IRunStore methods
    /// are no-ops; only the ordering signal matters for Finding 1's regression
    /// guard.
    /// </summary>
    private sealed class OrderingRunStore : IRunStore
    {
        public List<string> CallOrder { get; } = [];
        public string? PersistedStateEntrySha { get; private set; }

        public Task CreateRunAsync(RunRecord run, CancellationToken ct)
        {
            CallOrder.Add(nameof(CreateRunAsync));
            return Task.CompletedTask;
        }

        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct)
        {
            CallOrder.Add(nameof(SetStateEntryShaAsync));
            PersistedStateEntrySha = sha;
            return Task.CompletedTask;
        }

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
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult<CacheCandidateRecord?>(null);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Records every <see cref="SaveStepResultAsync"/> payload and serves
    /// configurable priors back to <see cref="RerunPreambleBuilder"/> and
    /// <see cref="RerunCacheGate"/>. The two services share an IRunStore
    /// instance in production wiring, so the test mirrors that.
    /// </summary>
    private sealed class CapturingRunStore : IRunStore
    {
        public List<StepResultRecord> SavedStepResults { get; } = [];
        public IReadOnlyList<StepResultRecord> PriorStepResultsForCard { get; set; } = [];
        public CacheCandidateRecord? PriorCacheCandidate { get; set; }

        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
        {
            SavedStepResults.Add(result);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
            string cardId, string? stateName, CancellationToken ct)
            => Task.FromResult(PriorStepResultsForCard);

        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(
            string cardId, string stateName, string stepName, CancellationToken ct)
            => Task.FromResult(PriorCacheCandidate);

        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct) => Task.CompletedTask;
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }
}
