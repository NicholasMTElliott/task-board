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

    // ── Round-8 fix 1: WritesDescriptionSection suppression ──────────

    [Fact]
    public async Task SectionUpdate_Suppressed_WhenWritesDescriptionSectionIsFalse()
    {
        // The agent returns a structured section_update directive on a step
        // explicitly configured with writesDescriptionSection=false. The
        // orchestrator must NOT call UpdateCardBodyAsync (the section is
        // suppressed) AND must not fall back to the legacy task-file body
        // sync. The directive is still captured raw on step_result.section_update_json.
        const string Body = "Operator-only body for suppression test";
        const string ExpectedRawJson =
            """{"strategy":"replace","content":"Should be suppressed"}""";

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(
                AgentOutcome.COMPLETE,
                "Done",
                Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Should be suppressed"),
                SectionUpdateJson: ExpectedRawJson));

        var capturingStore = new CapturingRunStore();
        // Build a config whose single step opts out of section writes.
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new(
                    "Design", "senior_engineer", "agent_run",
                    "Work on {TaskName}",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps: [
                        new("review_only", "senior_engineer",
                            TaskPrompt: "Review {TaskName}",
                            WritesDescriptionSection: false),
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
                    new List<string>()),
            });

        var runner = CreateRunner(executor, config, runStore: capturingStore);
        SetupBoardCards(DesignListId, initialBody: Body);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Body must NOT have been updated. UpdateCardBodyAsync is the canonical
        // signal — pre-fix the section_update path would have rewritten the body
        // even with WritesDescriptionSection=false.
        await _boardClient.DidNotReceive().UpdateCardBodyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Directive itself is still captured for replay/debugging.
        var stepRecord = capturingStore.SavedStepResults
            .FirstOrDefault(r => r.StepName == "review_only");
        Assert.NotNull(stepRecord);
        Assert.Equal(ExpectedRawJson, stepRecord!.SectionUpdateJson);

        // section_output_hash stays null when the step is suppressed — there's
        // no managed section to hash.
        Assert.Null(stepRecord.SectionOutputHash);
    }

    // ── Round-8 fix 3: cached estimator step preserves estimate ──────

    [Fact]
    public async Task CacheHit_ForwardsEstimateFromSourceRun()
    {
        // Two-run replay: run 1 produces an estimate; run 2 cache-hits and
        // must forward the estimate from the source row so the
        // {{estimation}} template variable resolves on the cached run too.
        const double SourceEstimate = 8.0;

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "Done", Estimate: SourceEstimate));

        var replayStore = new ReplayingRunStore();
        var runner = CreateRunner(executor, BuildBasicConfig(),
            runStore: replayStore,
            cacheGate: new RerunCacheGate(replayStore, NullLogger<RerunCacheGate>.Instance));
        SetupBoardCards(DesignListId, initialBody: "Operator content for cached estimator test");

        // Run 1: cache miss, executor runs, estimate persisted.
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(SourceEstimate, replayStore.LastPersistedEstimate);
        Assert.Single(replayStore.RunIds);
        var run1Id = replayStore.RunIds[0];

        // Run 2: same inputs → cache hit. Executor returns a different value
        // if it runs (proves the cache hit short-circuits it).
        executor.ClearReceivedCalls();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "FRESH-RUN-NOT-CACHED", Estimate: 99.0));
        replayStore.PersistedEstimateBeforeRun2 = replayStore.LastPersistedEstimate;
        replayStore.LastPersistedEstimate = null;

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify cache hit: the second run's saved step record has
        // ExecutionKind = "cache_hit" and source_run_id pointing at run 1.
        var run2GateOrStep = replayStore.SavedStepResults
            .Where(r => r.StepName == "create_design")
            .ToList();
        Assert.Equal(2, run2GateOrStep.Count);
        var run2Step = run2GateOrStep[1];
        Assert.Equal("cache_hit", run2Step.ExecutionKind);
        Assert.Equal(run1Id, run2Step.SourceRunId);

        // The cached estimate must have been forwarded to UpdateRunEstimateAsync
        // on run 2 — the headline behaviour fix. Pre-fix this stays null.
        Assert.Equal(SourceEstimate, replayStore.LastPersistedEstimate);
    }

    // ── Round-8 fix 2: gate-check caching ────────────────────────────

    [Fact]
    public async Task GateCheck_CacheHit_SkipsExecutorAndPostsCacheHitComment()
    {
        // Two-run replay: run 1 paints a full-run gate row carrying an
        // input_hash; run 2 cache-hits the gate, skips the executor, persists
        // a cache_hit gate row, and posts a kind:cache_hit comment.

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE, "Step/Gate done"));

        var replayStore = new ReplayingRunStore();
        var runner = CreateRunner(executor, BuildGateCheckConfig(),
            runStore: replayStore,
            cacheGate: new RerunCacheGate(replayStore, NullLogger<RerunCacheGate>.Instance));
        SetupBoardCards(DesignListId);

        // Run 1: cache miss (no priors). Both step and gate run, both record
        // input_hash on their full_run rows.
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        var run1GateRow = replayStore.SavedStepResults.FirstOrDefault(r => r.StepName == "gate_check");
        Assert.NotNull(run1GateRow);
        Assert.Equal("full_run", run1GateRow!.ExecutionKind);
        Assert.NotNull(run1GateRow.InputHash);

        // Run 2: identical inputs → both step and gate cache-hit.
        // We track gate executor calls by counting calls whose Model matches
        // the gate role (haiku) — the senior_engineer step uses opus.
        executor.ClearReceivedCalls();
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        var gateExecutorCallsOnRun2 = executor.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentExecutor.ExecuteAsync))
            .Select(c => (AgentExecutionContext)c.GetArguments()[0]!)
            .Count(ctx => ctx.Model == "claude-haiku-4-5-20251001");
        Assert.Equal(0, gateExecutorCallsOnRun2);

        // A gate cache_hit step_result must have been persisted on run 2.
        var gateRowsPerRun = replayStore.SavedStepResults
            .Where(r => r.StepName == "gate_check")
            .ToList();
        Assert.Equal(2, gateRowsPerRun.Count);
        var run2GateRow = gateRowsPerRun[1];
        Assert.Equal("cache_hit", run2GateRow.ExecutionKind);
        Assert.Equal(AgentOutcome.COMPLETE, run2GateRow.Outcome);
        Assert.Equal(run1GateRow.RunId, run2GateRow.SourceRunId);

        // A kind:cache_hit comment must have been posted with step:gate_check.
        await _boardClient.Received().AppendAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(body =>
                body.Contains("kind:cache_hit", StringComparison.Ordinal)
                && body.Contains("step:gate_check", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLifecycleErrorComment_UsesAiboardLogRunKind_AndIsAgentGeneratedForHashing()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentResult>(new InvalidOperationException("boom")));

        var runner = CreateRunner(executor, BuildBasicConfig());
        SetupBoardCards(DesignListId);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.True(TaskFileManager.ContainsAgentMarker(
            "<!-- aiboard-log kind:run state:Design run:run-1 outcome:error -->\nbody"));

        await _boardClient.Received().AppendAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(body =>
                body.Contains("kind:run", StringComparison.Ordinal)
                && body.Contains("outcome:error", StringComparison.Ordinal)
                && !body.Contains("agent-run", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LongWorkflow_GateFailThenRerun_CacheHitsSteps_ThenGatePasses()
    {
        var body = "Operator requirements.";
        var comments = SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "No blockers found.")),
                    2 => new AgentResult(AgentOutcome.COMPLETE, "Design created",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Use append-only audit comments.")),
                    3 => new AgentResult(AgentOutcome.ERROR, "Gate found a missing success audit comment."),
                    4 => new AgentResult(AgentOutcome.COMPLETE, "Gate passes after rerun."),
                    _ => new AgentResult(AgentOutcome.ERROR, "unexpected extra executor call"),
                };
            });

        var store = new ReplayingRunStore();
        var runner = CreateRunner(executor, BuildTwoStepGateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance));

        var first = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        var second = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, first.Outcome);
        Assert.Equal(AgentOutcome.COMPLETE, second.Outcome);
        await executor.Received(4).ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());

        Assert.Contains(store.SavedStepResults, r =>
            r.StepName == "review_related_tickets" && r.ExecutionKind == "cache_hit");
        Assert.Contains(store.SavedStepResults, r =>
            r.StepName == "create_design" && r.ExecutionKind == "cache_hit");
        Assert.Equal(2, store.SavedStepResults.Count(r => r.StepName == "gate_check"));
        Assert.Contains(comments, c => c.Body.Contains("kind:gate", StringComparison.Ordinal)
            && c.Body.Contains("outcome:ERROR", StringComparison.Ordinal));
        Assert.Contains(comments, c => c.Body.Contains("kind:gate", StringComparison.Ordinal)
            && c.Body.Contains("outcome:COMPLETE", StringComparison.Ordinal));
        Assert.DoesNotContain(comments, c => c.Body.Contains("agent-run", StringComparison.Ordinal)
            || c.Body.Contains("agent-step", StringComparison.Ordinal));
        Assert.Contains("## Review Related Tickets", body);
        Assert.Contains("## Create Design", body);
    }

    [Fact]
    public async Task LongWorkflow_QuestionsStopDownstream_ThenRerunCompletesGate()
    {
        var body = "Operator requirements.";
        var comments = SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "No blockers found.")),
                    2 => new AgentResult(AgentOutcome.NEEDS_INFO, "Need API choice",
                        [new AgentQuestion("Which API version should the design target?")],
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Waiting on API version.")),
                    3 => new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed again",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "No blockers found.")),
                    4 => new AgentResult(AgentOutcome.COMPLETE, "Design completed",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Target the stable API.")),
                    5 => new AgentResult(AgentOutcome.COMPLETE, "Gate passes."),
                    _ => new AgentResult(AgentOutcome.ERROR, "unexpected extra executor call"),
                };
            });

        var runner = CreateRunner(executor, BuildTwoStepGateConfig(), runStore: new ReplayingRunStore());

        var first = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(AgentOutcome.NEEDS_INFO, first.Outcome);
        Assert.Equal(2, callIndex);
        Assert.DoesNotContain(comments, c => c.Body.Contains("kind:gate", StringComparison.Ordinal));

        comments.Add(new CardComment("human", "Use API v2.", DateTimeOffset.UtcNow));

        var second = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, second.Outcome);
        Assert.Equal(5, callIndex);
        Assert.Contains(comments, c => c.Body.Contains("kind:gate", StringComparison.Ordinal)
            && c.Body.Contains("outcome:COMPLETE", StringComparison.Ordinal));
        Assert.Contains("## Review Related Tickets", body);
        Assert.Contains("## Create Design", body);
    }

    [Fact]
    public async Task LongWorkflow_AiboardCommentChurnCacheHits_ButOperatorCommentForcesRework()
    {
        var body = "Operator requirements.";
        var comments = SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "No blockers found.")),
                    2 => new AgentResult(AgentOutcome.COMPLETE, "Design created",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Use append-only audit comments.")),
                    3 => new AgentResult(AgentOutcome.COMPLETE, "Gate passes."),
                    4 => new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed after operator edit",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Operator added acceptance criteria.")),
                    5 => new AgentResult(AgentOutcome.COMPLETE, "Design updated after operator edit",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Include the acceptance criteria.")),
                    6 => new AgentResult(AgentOutcome.COMPLETE, "Gate passes after operator edit."),
                    _ => new AgentResult(AgentOutcome.ERROR, "unexpected extra executor call"),
                };
            });

        var store = new ReplayingRunStore();
        var runner = CreateRunner(executor, BuildTwoStepGateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance));

        var first = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, first.Outcome);
        Assert.Equal(3, callIndex);

        var second = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, second.Outcome);
        Assert.Equal(3, callIndex);
        Assert.Equal(3, store.SavedStepResults.Count(r =>
            r.RunId == store.RunIds[1] && r.ExecutionKind == "cache_hit"));

        comments.Add(new CardComment("human", "Operator adds acceptance criteria.", DateTimeOffset.UtcNow));

        var third = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, third.Outcome);
        Assert.Equal(6, callIndex);
        Assert.Equal(3, store.SavedStepResults.Count(r =>
            r.RunId == store.RunIds[2] && r.ExecutionKind == "full_run"));

        Assert.Equal(3, store.RunRecords.Count(r => r.CardId == TargetCardId && r.StateName == "Design"));
        Assert.Contains(comments, c => c.Body.Contains("kind:cache_hit", StringComparison.Ordinal));
        Assert.DoesNotContain(comments, c => c.Body.Contains("agent-run", StringComparison.Ordinal)
            || c.Body.Contains("agent-step", StringComparison.Ordinal));
        Assert.Contains("Operator added acceptance criteria.", body);
        Assert.Contains("Include the acceptance criteria.", body);
    }

    [Fact]
    public async Task LongWorkflow_ShutdownThenRerun_CacheHitsCompletedStepAndFinishesGate()
    {
        var body = "Operator requirements.";
        var comments = SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed before shutdown",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Partial review survived shutdown.")),
                    2 => new AgentResult(AgentOutcome.COMPLETE, "Design completed after restart",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Design completed after restart.")),
                    3 => new AgentResult(AgentOutcome.COMPLETE, "Gate passes after restart."),
                    _ => new AgentResult(AgentOutcome.ERROR, "unexpected extra executor call"),
                };
            });

        var store = new ReplayingRunStore();
        using var shutdown = new ShutdownCoordinator();
        shutdown.RequestShutdown();

        var firstRunner = CreateRunner(executor, BuildTwoStepGateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance),
            shutdownCoordinator: shutdown);
        var first = await firstRunner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, first.Outcome);
        Assert.Equal(1, callIndex);
        Assert.DoesNotContain(store.SavedStepResults, r => r.StepName == "gate_check");
        Assert.Contains(comments, c => c.Body.Contains("kind:shutdown_notice", StringComparison.Ordinal));

        var secondRunner = CreateRunner(executor, BuildTwoStepGateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance));
        var second = await secondRunner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, second.Outcome);
        Assert.Equal(3, callIndex);
        Assert.Contains(store.SavedStepResults, r =>
            r.RunId == store.RunIds[1]
            && r.StepName == "review_related_tickets"
            && r.ExecutionKind == "cache_hit");
        Assert.Contains(store.SavedStepResults, r =>
            r.RunId == store.RunIds[1]
            && r.StepName == "create_design"
            && r.ExecutionKind == "full_run");
        Assert.Contains(store.SavedStepResults, r =>
            r.RunId == store.RunIds[1]
            && r.StepName == "gate_check"
            && r.ExecutionKind == "full_run"
            && r.Outcome == AgentOutcome.COMPLETE);
        Assert.Contains("Partial review survived shutdown.", body);
        Assert.Contains("Design completed after restart.", body);
    }

    [Fact]
    public async Task LongWorkflow_GateStepHistory_UsesCanonicalRowsOnly_NotCandidateRows()
    {
        var body = "Operator requirements.";
        SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        AgentExecutionContext? capturedGateContext = null;
        var callIndex = 0;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callIndex++;
                if (callIndex == 1)
                {
                    return new AgentResult(AgentOutcome.COMPLETE, "Related tickets reviewed",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "No blockers found."));
                }

                if (callIndex == 2)
                {
                    return new AgentResult(AgentOutcome.COMPLETE, "Design created",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Winning design only."));
                }

                capturedGateContext = ci.Arg<AgentExecutionContext>();
                return new AgentResult(AgentOutcome.COMPLETE, "Gate passes.");
            });

        var store = new ReplayingRunStore
        {
            InjectSyntheticCandidateRowsForLatestRun = true,
        };
        var runner = CreateRunner(executor, BuildTwoStepGateConfig(), runStore: store);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(capturedGateContext);
        Assert.Contains("Related tickets reviewed", capturedGateContext!.TaskPrompt);
        Assert.Contains("Design created", capturedGateContext.TaskPrompt);
        Assert.DoesNotContain("DISMISSED CANDIDATE DESIGN", capturedGateContext.TaskPrompt);
        Assert.DoesNotContain("EVALUATOR MENTIONED DISMISSED", capturedGateContext.TaskPrompt);
        Assert.DoesNotContain(":cand-", capturedGateContext.TaskPrompt);
        Assert.DoesNotContain(":evaluator", capturedGateContext.TaskPrompt);
    }

    // ── Round-9 Group A: mid-candidate-group interrupt + rerun ───────

    [Fact]
    public async Task InterruptedCandidateGroup_NoChange_RerunsAllCandidatesFresh_ProducesCanonicalRow()
    {
        // Scenario: a polling-mode runner was killed mid-candidate-group on a
        // prior run — 3 of 5 candidates persisted their step_result rows
        // (with suffixed step_name and candidate_group_id != null), but no
        // evaluator ran and no canonical row was written. Operator manually
        // moves the card back to Ready. We re-run.
        //
        // Pinned behaviour (per user's selected option): re-run all 5
        // candidates fresh — stale per-candidate rows from the killed run
        // remain in the DB as telemetry but DO NOT satisfy the cache lookup.
        //
        // The cache miss is enforced by PgRunStore's SQL predicate:
        // `step_name = $4` matches the canonical step name only — partial
        // candidate rows have suffixed step_names like `create_design:cand-0:claude`
        // and never match. This test pins that invariant through the
        // AgentRunner pipeline.

        var store = new ReplayingRunStore();
        SeedPartialCandidateRows(store, candidateCount: 3);
        var harness = CreateFiveCandidateHarness(store);

        var runner = CreateRunner(harness.Resolver, BuildFiveCandidateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance),
            candidateExecutor: harness.CandidateExecutor);
        SetupBoardCards(DesignListId, initialBody: "Operator content (unchanged from killed run)");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        harness.AssertFreshCandidateFanout();

        // A new canonical row was written with execution_kind=full_run and a
        // computed input_hash (so a future run can cache against it).
        var canonicalRow = store.SavedStepResults
            .Where(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[0])
            .Single();
        Assert.Equal("full_run", canonicalRow.ExecutionKind);
        Assert.NotNull(canonicalRow.InputHash);
        Assert.DoesNotContain(store.SavedStepResults, r =>
            r.RunId == store.RunIds[0] && r.ExecutionKind == "cache_hit");

        // The 3 partial rows from the killed run remain — they're telemetry,
        // not destructive cleanup. Verify by stepname suffix / candidate-group
        // presence: SeedPartialCandidateRows used candidate_group_id != null
        // and stepname suffixes.
        var partialRows = store.SavedStepResults
            .Where(r => r.CandidateGroupId is not null && r.RunId == "killed-run-1")
            .ToList();
        Assert.Equal(3, partialRows.Count);
    }

    [Fact]
    public async Task CandidateGroup_NeedsInfoWinner_RoutesToQuestions_WithoutFallbackSlot()
    {
        // Regression guard for KvA #6: NEEDS_INFO candidates are successful
        // candidates for evaluator ranking. If the evaluator picks one, the
        // AgentRunner slot loop must surface NEEDS_INFO and stop; it must NOT
        // treat the slot as failed and fall through to a fallback slot.
        var needInfo0 = new TrackingExecutor(new AgentResult(
            AgentOutcome.NEEDS_INFO,
            "Question A is plausible but incomplete."));
        var needInfo1 = new TrackingExecutor(new AgentResult(
            AgentOutcome.NEEDS_INFO,
            "Question B is the real blocker."));
        var fallback = new TrackingExecutor(new AgentResult(
            AgentOutcome.COMPLETE,
            "fallback should not run"));
        var evaluator = new TrackingExecutor(new AgentResult(
            AgentOutcome.COMPLETE,
            """
            Candidate 1 wins because its question is the real blocker.
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[
              {"index":0,"score":6,"reasoning":"plausible but weaker"},
              {"index":1,"score":9,"reasoning":"correctly identifies the blocker"}
            ]}
            ```
            """));

        var executors = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["need-info-0"] = needInfo0,
            ["need-info-1"] = needInfo1,
            ["fallback"] = fallback,
            ["eval"] = evaluator,
        };
        var resolver = new AgentExecutorResolver(executors);
        var store = new ReplayingRunStore();
        var candidateExecutor = new CandidateExecutor(
            _gitWorkspaceManager,
            resolver,
            store,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);
        var runner = CreateRunner(
            resolver,
            BuildNeedsInfoWinnerWithFallbackConfig(),
            runStore: store,
            candidateExecutor: candidateExecutor);
        SetupBoardCards(DesignListId, initialBody: "Design the thing.");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Question B is the real blocker.", result.ErrorDetail);
        Assert.Equal(1, needInfo0.Calls);
        Assert.Equal(1, needInfo1.Calls);
        Assert.Equal(1, evaluator.Calls);
        Assert.Equal(0, fallback.Calls);

        await _boardClient.Received(1).MoveCardToColumnAsync(
            TargetCardId, "list-questions", Arg.Any<CancellationToken>());

        var canonicalRow = store.SavedStepResults.Single(r =>
            r.RunId == store.RunIds[0]
            && r.StepName == "create_design"
            && r.CandidateGroupId is null);
        Assert.Equal(AgentOutcome.NEEDS_INFO, canonicalRow.Outcome);

        var selected = store.RecordedVerdicts.Single(v => v.Selected);
        Assert.Equal(1, selected.CandidateIndex);
    }

    [Fact]
    public async Task InterruptedCandidateGroup_DescriptionEdited_NewCanonicalHashDiffersFromUnchangedBaseline()
    {
        // Two-run: baseline run with body "ORIG" and partial rows; reset; new
        // run with body "EDITED" and partial rows. The canonical input_hash
        // from the EDITED run must differ from the ORIG run — proving an
        // operator description change is reflected in the cache key even
        // when partial-run telemetry is present (which would otherwise be a
        // distractor signal).

        var body = "ORIG operator content";
        SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var store = new ReplayingRunStore();
        SeedPartialCandidateRows(store, candidateCount: 3);
        var harness = CreateFiveCandidateHarness(store);
        var runner = CreateRunner(harness.Resolver, BuildFiveCandidateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance),
            candidateExecutor: harness.CandidateExecutor);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        harness.AssertCandidateFanoutCount(1);
        var baselineHash = store.SavedStepResults
            .Single(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[0])
            .InputHash;
        Assert.NotNull(baselineHash);

        body = "EDITED operator content (different from ORIG)";

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        harness.AssertCandidateFanoutCount(2);
        var editedHash = store.SavedStepResults
            .Single(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[1])
            .InputHash;

        Assert.NotEqual(baselineHash, editedHash);
    }

    [Fact]
    public async Task InterruptedCandidateGroup_OperatorCommentAdded_NewCanonicalHashDiffersFromBaseline()
    {
        // Baseline: no comments. Modified: one operator comment added before
        // run 2. The canonical input_hash on the modified run must differ —
        // operator comments are part of the input bundle.

        var body = "Operator content";
        var comments = SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var store = new ReplayingRunStore();
        SeedPartialCandidateRows(store, candidateCount: 3);
        var harness = CreateFiveCandidateHarness(store);
        var runner = CreateRunner(harness.Resolver, BuildFiveCandidateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance),
            candidateExecutor: harness.CandidateExecutor);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        harness.AssertCandidateFanoutCount(1);
        var baselineHash = store.SavedStepResults
            .Single(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[0])
            .InputHash;

        comments.Add(new CardComment(
            "op",
            "Operator question added between killed run and rerun",
            DateTimeOffset.UtcNow.AddMinutes(-1)));

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        harness.AssertCandidateFanoutCount(2);
        var modifiedHash = store.SavedStepResults
            .Single(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[1])
            .InputHash;

        Assert.NotEqual(baselineHash, modifiedHash);
    }

    [Fact]
    public async Task InterruptedCandidateGroup_AiboardLogCommentAdded_HashUnaffected()
    {
        // Most adversarial of the four: an aiboard-log marker comment is
        // present on the board on run 2 (left over from one of the partial
        // run-1 candidates that posted its kind:candidate comment before
        // being killed). The hash must IGNORE this comment — it's
        // agent-generated, classified as such by the permissive
        // AiboardLogMarker.IsAgentGenerated prefix match.
        //
        // Pre-fix would-be regression: if the filter ever incorrectly
        // includes aiboard-log comments, every cache check after a
        // partial-run interruption would see different hashes purely
        // because of orphan candidate comments — defeats the whole
        // "irrelevant change doesn't bust the cache" guarantee.

        var body = "Operator content";
        var comments = SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var store = new ReplayingRunStore();
        SeedPartialCandidateRows(store, candidateCount: 3);
        var harness = CreateFiveCandidateHarness(store);
        var runner = CreateRunner(harness.Resolver, BuildFiveCandidateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance),
            candidateExecutor: harness.CandidateExecutor);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        harness.AssertCandidateFanoutCount(1);
        var baselineHash = store.SavedStepResults
            .Single(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[0])
            .InputHash;

        comments.Add(new CardComment(
            "bot",
            "<!-- aiboard-log kind:candidate state:Design step:create_design candidate:0 -->\n" +
            "Orphan candidate comment from a killed run.",
            DateTimeOffset.UtcNow.AddMinutes(-1)));

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        harness.AssertCandidateFanoutCount(1);
        var modifiedHash = store.SavedStepResults
            .Single(r => r.StepName == "create_design"
                && r.CandidateGroupId is null
                && r.RunId == store.RunIds[1])
            .InputHash;

        // Identical hashes — the aiboard-log comment didn't influence the
        // input bundle.
        Assert.Equal(baselineHash, modifiedHash);
    }

    /// <summary>
    /// Seeds <paramref name="candidateCount"/> per-candidate step_result rows
    /// into <paramref name="store"/> under a synthetic "killed-run-1" run id.
    /// Mirrors what the database would look like after a polling-mode runner
    /// was killed mid-candidate-group: per-candidate rows persisted with
    /// suffixed step_names and candidate_group_id != null, NO canonical row,
    /// NO evaluator row.
    /// </summary>
    private static void SeedPartialCandidateRows(ReplayingRunStore store, int candidateCount)
    {
        var groupId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddMinutes(-30);
        for (var i = 0; i < candidateCount; i++)
        {
            store.SavedStepResults.Add(new StepResultRecord(
                RunId: "killed-run-1",
                CardId: TargetCardId,
                StateName: "Design",
                StepName: $"create_design:cand-{i}:claude",
                StepIndex: 0,
                Role: "senior_engineer",
                Model: "opus-4.6",
                Outcome: AgentOutcome.COMPLETE,
                Summary: $"Partial candidate {i} output",
                Detail: null,
                ReferenceContent: null,
                ConversationLog: null,
                Questions: null,
                RequestedSteps: null,
                StartedAtUtc: now.AddSeconds(i * 2),
                CompletedAtUtc: now.AddSeconds(i * 2 + 1),
                CandidateGroupId: groupId,
                CandidateIndex: i,
                Selected: null));
        }
    }

    // ── Round-9 Group C: cross-step transitive cache propagation ─────

    [Fact]
    public async Task MultiStepState_BothStepsCacheHit_OnSecondRunWithUnchangedInputs()
    {
        // Run 1: both steps execute fresh. Run 2 with no input changes:
        // step A cache hits; step B's input bundle includes A's
        // section_output_hash unchanged → step B also cache hits.
        // Asserts that transitive cache propagation works end-to-end:
        // upstream cache hit produces a stable section that downstream
        // sees as identical.

        var body = "Operator requirements.";
        SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var executor = Substitute.For<IAgentExecutor>();
        var callIndex = 0;
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Step A first run",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Step A output content.")),
                    2 => new AgentResult(AgentOutcome.COMPLETE, "Step B first run",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Step B output content.")),
                    _ => new AgentResult(AgentOutcome.ERROR, "should not reach — both steps should cache-hit"),
                };
            });

        var store = new ReplayingRunStore();
        var runner = CreateRunner(executor, BuildTwoStepNoGateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance));

        // Run 1: 2 executor calls (steps A and B both run fresh).
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(2, callIndex);

        // Run 2: no input changes → both steps cache-hit, no executor calls.
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(2, callIndex);  // unchanged — neither step re-ran.

        // Both step's run-2 rows are cache_hit.
        var run2Rows = store.SavedStepResults
            .Where(r => r.RunId == store.RunIds[1] && r.CandidateGroupId is null)
            .ToList();
        Assert.All(run2Rows, r => Assert.Equal("cache_hit", r.ExecutionKind));
        Assert.Equal(2, run2Rows.Count);
        Assert.Contains(run2Rows, r => r.StepName == "step_a");
        Assert.Contains(run2Rows, r => r.StepName == "step_b");
    }

    [Fact]
    public async Task MultiStepState_StepASectionDrift_StepARerunsIdenticalOutput_StepBStillCacheHits()
    {
        // Run 1: both steps execute fresh, step A produces section content X.
        // Between runs, the operator edits step A's managed section (drift) —
        // BUT then re-edits it back to byte-identical content (or, more
        // realistically, the agent re-runs and produces the same content).
        //
        // To simulate this with a substitute executor, run 2 makes step A's
        // executor invocation produce IDENTICAL section content to run 1 —
        // proving section_output_hash is stable across A's re-run, and
        // therefore step B's input bundle is unchanged → step B cache hits.
        //
        // Pins the documented transitive-invalidation semantic
        // (RerunRedesign.md §5.7 "downstream re-runs only when upstream
        // section_output_hash actually changes").

        const string SharedStepASectionContent = "Stable step A output (byte-identical across runs).";
        var body = "Operator requirements.";
        SetupMutableBoard(DesignListId, () => body, nextBody => body = nextBody);

        var executor = Substitute.For<IAgentExecutor>();
        var callIndex = 0;
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callIndex++;
                return callIndex switch
                {
                    1 => new AgentResult(AgentOutcome.COMPLETE, "Step A first run",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, SharedStepASectionContent)),
                    2 => new AgentResult(AgentOutcome.COMPLETE, "Step B first run",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, "Step B output content.")),
                    3 => new AgentResult(AgentOutcome.COMPLETE, "Step A re-run after drift",
                        Section: new SectionUpdate(SectionUpdateStrategy.Replace, SharedStepASectionContent)),
                    _ => new AgentResult(AgentOutcome.ERROR, "should not reach — step B should cache-hit"),
                };
            });

        var store = new ReplayingRunStore();
        var runner = CreateRunner(executor, BuildTwoStepNoGateConfig(),
            runStore: store,
            cacheGate: new RerunCacheGate(store, NullLogger<RerunCacheGate>.Instance));

        // Run 1.
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(2, callIndex);

        // Capture step A's section_output_hash from run 1 — that's the
        // upstream signal step B's input bundle depends on.
        var run1StepA = store.SavedStepResults
            .Single(r => r.StepName == "step_a" && r.RunId == store.RunIds[0]);
        var stableSectionHash = run1StepA.SectionOutputHash;
        Assert.NotNull(stableSectionHash);

        // Force section drift on step A: edit the body's step-section content
        // so the section_output_hash differs from what's recorded.
        body = body.Replace(SharedStepASectionContent, "DRIFTED — operator edited step A's section");

        // Run 2: step A misses (drift), re-runs, produces byte-identical
        // content. Step B's prior_section_hash is unchanged → cache hit.
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);
        Assert.Equal(3, callIndex);  // only step A re-ran; step B did not.

        // Step A's run-2 row is full_run (drift forced it); Step B's run-2
        // row is cache_hit.
        var run2StepA = store.SavedStepResults
            .Single(r => r.StepName == "step_a" && r.CandidateGroupId is null && r.RunId == store.RunIds[1]);
        Assert.Equal("full_run", run2StepA.ExecutionKind);
        Assert.Equal(stableSectionHash, run2StepA.SectionOutputHash);

        var run2StepB = store.SavedStepResults
            .Single(r => r.StepName == "step_b" && r.CandidateGroupId is null && r.RunId == store.RunIds[1]);
        Assert.Equal("cache_hit", run2StepB.ExecutionKind);
    }

    /// <summary>
    /// Two sequential steps in one state with NO gate check. Used by the
    /// transitive cache propagation tests — without a gate, the test can
    /// observe pure step-to-step propagation without gate behaviour
    /// confounding the executor call count.
    /// </summary>
    private static WorkflowConfig BuildTwoStepNoGateConfig() =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new(
                    "Design", "senior_engineer", "agent_run",
                    "Work on {TaskName}",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps:
                    [
                        new("step_a", "senior_engineer", TaskPrompt: "Step A {TaskName}"),
                        new("step_b", "senior_engineer", TaskPrompt: "Step B {TaskName}"),
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
                    new List<string> { "Step A", "Step B" }),
            });

    // ── Helpers ──────────────────────────────────────────────────────

    private AgentRunner CreateRunner(
        IAgentExecutor executor,
        WorkflowConfig config,
        IRunStore? runStore = null,
        RerunCacheGate? cacheGate = null,
        ShutdownCoordinator? shutdownCoordinator = null)
        => CreateRunner(
            AgentExecutorResolver.ForSingleExecutor(executor),
            config,
            runStore,
            cacheGate,
            shutdownCoordinator);

    private AgentRunner CreateRunner(
        IAgentExecutorResolver resolver,
        WorkflowConfig config,
        IRunStore? runStore = null,
        RerunCacheGate? cacheGate = null,
        ShutdownCoordinator? shutdownCoordinator = null,
        CandidateExecutor? candidateExecutor = null)
    {
        var normalisedConfig = config.Normalised();
        return new AgentRunner(
            _boardClient,
            resolver,
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
            shutdownCoordinator: shutdownCoordinator,
            cacheGate: cacheGate,
            candidateExecutor: candidateExecutor);
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

    private List<CardComment> SetupMutableBoard(
        string listId,
        Func<string> getBody,
        Action<string> setBody)
    {
        var comments = new List<CardComment>();
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(_ => new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle, getBody(), listId),
            });

        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(_ => comments.ToList());

        _boardClient.UpdateCardBodyAsync(TargetCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => setBody(ci.ArgAt<string>(1)));

        _boardClient.AppendAgentCommentAsync(TargetCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => comments.Add(new CardComment(
                "bot", ci.ArgAt<string>(1), DateTimeOffset.UtcNow.AddSeconds(comments.Count))));

        return comments;
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

    private static WorkflowConfig BuildFiveCandidateConfig() =>
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
                    Steps:
                    [
                        new("create_design", "senior_engineer",
                            TaskPrompt: "Design {TaskName}",
                            Slots:
                            [
                                new SlotConfig(
                                    Candidates:
                                    [
                                        new("cand-0"),
                                        new("cand-1"),
                                        new("cand-2"),
                                        new("cand-3"),
                                        new("cand-4"),
                                    ],
                                    Evaluator: new EvaluatorConfig(
                                        Role: "evaluator",
                                        TaskPrompt: "Pick the best design.")),
                            ]),
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
                ["evaluator"] = new("eval-model", "You are an evaluator.",
                    new List<string>(), Provider: "eval"),
            });

    private static WorkflowConfig BuildNeedsInfoWinnerWithFallbackConfig() =>
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
                    Steps:
                    [
                        new("create_design", "senior_engineer",
                            TaskPrompt: "Design {TaskName}",
                            Slots:
                            [
                                new SlotConfig(
                                    Candidates:
                                    [
                                        new("need-info-0"),
                                        new("need-info-1"),
                                    ],
                                    Evaluator: new EvaluatorConfig(
                                        Role: "evaluator",
                                        TaskPrompt: "Pick the candidate with the most important unresolved question.")),
                                new SlotConfig(
                                    Candidates:
                                    [
                                        new("fallback"),
                                    ]),
                            ]),
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
                ["evaluator"] = new("eval-model", "You are an evaluator.",
                    new List<string>(), Provider: "eval"),
            });

    private FiveCandidateHarness CreateFiveCandidateHarness(IRunStore runStore)
    {
        var executors = Enumerable.Range(0, 5)
            .ToDictionary(
                i => $"cand-{i}",
                i => (TrackingExecutor)new TrackingExecutor(
                    new AgentResult(
                        AgentOutcome.COMPLETE,
                        $"Candidate {i} detail",
                        Section: new SectionUpdate(
                            SectionUpdateStrategy.Replace,
                            $"Candidate {i} section"))),
                StringComparer.OrdinalIgnoreCase);

        var evaluator = new TrackingExecutor(new AgentResult(
            AgentOutcome.COMPLETE,
            """
            Candidate 0 wins.
            ```json
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":9,"reasoning":"best"},
              {"index":1,"score":5,"reasoning":"ok"},
              {"index":2,"score":5,"reasoning":"ok"},
              {"index":3,"score":5,"reasoning":"ok"},
              {"index":4,"score":5,"reasoning":"ok"}
            ]}
            ```
            """,
            Section: new SectionUpdate(SectionUpdateStrategy.Leave)));

        var all = executors.ToDictionary(
            kvp => kvp.Key,
            kvp => (IAgentExecutor)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        all["eval"] = evaluator;

        var resolver = new AgentExecutorResolver(all);
        var candidateExecutor = new CandidateExecutor(
            _gitWorkspaceManager,
            resolver,
            runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);

        return new FiveCandidateHarness(resolver, candidateExecutor, executors, evaluator);
    }

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

    private static WorkflowConfig BuildTwoStepGateConfig() =>
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
                    Steps:
                    [
                        new("review_related_tickets", "senior_engineer",
                            TaskPrompt: "Review related tickets for {TaskName}"),
                        new("create_design", "senior_engineer",
                            TaskPrompt: "Create the design for {TaskName}"),
                    ],
                    GateCheck: new GateCheckConfig(
                        Role: "gate_checker",
                        TaskPrompt: "Gate.\n\n## Body\n{TaskBody}\n\n## Report\n{AgentReport}\n\n## History\n{StepHistory}")),
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

    private sealed class FiveCandidateHarness(
        IAgentExecutorResolver resolver,
        CandidateExecutor candidateExecutor,
        IReadOnlyDictionary<string, TrackingExecutor> candidates,
        TrackingExecutor evaluator)
    {
        public IAgentExecutorResolver Resolver { get; } = resolver;
        public CandidateExecutor CandidateExecutor { get; } = candidateExecutor;

        public void AssertFreshCandidateFanout()
            => AssertCandidateFanoutCount(1);

        public void AssertCandidateFanoutCount(int expectedCalls)
        {
            Assert.Equal(5, candidates.Count);
            foreach (var candidate in candidates.Values)
                Assert.Equal(expectedCalls, candidate.Calls);

            Assert.Equal(expectedCalls, evaluator.Calls);
        }
    }

    private sealed class TrackingExecutor(AgentResult result) : IAgentExecutor
    {
        private int _calls;
        public int Calls => _calls;

        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(result);
        }
    }

    private sealed record RecordedVerdict(
        string RunId,
        Guid CandidateGroupId,
        int CandidateIndex,
        bool Selected,
        decimal? QualityScore,
        string? EvaluatorReasoning);

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
    /// configurable priors back to <see cref="RerunCacheGate"/>.
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

    /// <summary>
    /// Replays cache-relevant fields across runs: when a step or gate row was
    /// persisted in run N with a non-null InputHash and outcome COMPLETE, the
    /// next run's <see cref="GetMostRecentCompleteForStepAsync"/> lookup returns
    /// it as the prior. This mirrors what a real Postgres-backed store would do
    /// without exercising the SQL layer in a unit test. Tracks the estimate
    /// passed to <see cref="UpdateRunEstimateAsync"/> so tests can assert that
    /// a cache hit propagated the source run's estimate forward.
    /// </summary>
    private sealed class ReplayingRunStore : IRunStore
    {
        public List<StepResultRecord> SavedStepResults { get; } = [];
        public List<RunRecord> RunRecords { get; } = [];
        public List<string> RunIds { get; } = [];
        public List<RecordedVerdict> RecordedVerdicts { get; } = [];
        public bool InjectSyntheticCandidateRowsForLatestRun { get; init; }
        private readonly Dictionary<string, double> _runEstimates = [];
        public double? LastPersistedEstimate { get; set; }
        public double? PersistedEstimateBeforeRun2 { get; set; }

        public Task CreateRunAsync(RunRecord run, CancellationToken ct)
        {
            RunRecords.Add(run);
            RunIds.Add(run.RunId);
            return Task.CompletedTask;
        }

        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
        {
            SavedStepResults.Add(result);
            return Task.CompletedTask;
        }

        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(
            string cardId, string stateName, string stepName, CancellationToken ct)
        {
            // Walk SavedStepResults in reverse so the most recent COMPLETE wins.
            for (var i = SavedStepResults.Count - 1; i >= 0; i--)
            {
                var r = SavedStepResults[i];
                if (r.StepName != stepName) continue;
                if (r.StateName != stateName) continue;
                if (r.CardId != cardId) continue;
                if (r.Outcome != AgentOutcome.COMPLETE) continue;
                if (r.CandidateIndex is not null && r.CandidateIndex != 0) continue;
                if (r.InputHash is null) continue;

                _runEstimates.TryGetValue(r.RunId, out var est);
                return Task.FromResult<CacheCandidateRecord?>(new CacheCandidateRecord(
                    Id: Guid.NewGuid(),
                    RunId: r.RunId,
                    CompletedAtUtc: r.CompletedAtUtc,
                    InputHash: r.InputHash,
                    SectionOutputHash: r.SectionOutputHash,
                    OutputSummary: r.OutputSummary,
                    Detail: r.Detail,
                    Estimate: est == 0 ? null : est));
            }
            return Task.FromResult<CacheCandidateRecord?>(null);
        }

        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct)
        {
            _runEstimates[runId] = estimate;
            LastPersistedEstimate = estimate;
            return Task.CompletedTask;
        }

        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct)
        {
            var rows = SavedStepResults
                .Where(r => r.CardId == cardId
                    && (stateName is null || r.StateName == stateName))
                .ToList();

            if (InjectSyntheticCandidateRowsForLatestRun && RunIds.Count > 0)
            {
                var runId = RunIds[^1];
                var groupId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                rows.Add(new StepResultRecord(
                    runId, cardId, "Design", "create_design:cand-0:claude", 1,
                    "senior_engineer", "opus-4.6", AgentOutcome.COMPLETE,
                    "DISMISSED CANDIDATE DESIGN", null, null, null, null, null,
                    now.AddSeconds(-3), now.AddSeconds(-2),
                    CandidateGroupId: groupId, CandidateIndex: 0, Selected: false));
                rows.Add(new StepResultRecord(
                    runId, cardId, "Design", "create_design:evaluator", 1,
                    "gate_checker", "claude-haiku-4-5-20251001", AgentOutcome.COMPLETE,
                    "EVALUATOR MENTIONED DISMISSED", null, null, null, null, null,
                    now.AddSeconds(-2), now.AddSeconds(-1)));
            }

            return Task.FromResult<IReadOnlyList<StepResultRecord>>(rows);
        }
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct)
        {
            RecordedVerdicts.Add(new RecordedVerdict(
                runId,
                candidateGroupId,
                candidateIndex,
                selected,
                qualityScore,
                evaluatorReasoning));
            return Task.CompletedTask;
        }
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }
}
