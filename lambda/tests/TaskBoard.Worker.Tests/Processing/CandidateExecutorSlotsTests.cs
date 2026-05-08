using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins the slot-based fallback model: each slot is its own parallel candidate
/// group with an evaluator (or a single candidate without one); slots are
/// tried sequentially until one Wins or returns NEEDS_INFO; per-candidate
/// retries fire in-place on RATE_LIMIT / TIMEOUT.
/// </summary>
public class CandidateExecutorSlotsTests : IDisposable
{
    private const string TestCardId = "1";
    private const string TestCardTitle = "Test card";

    private readonly string _repoRoot;
    private readonly string _canonicalWorktree;
    private readonly GitWorkspaceManager _git;
    private readonly RecordingRunStore _runStore;
    private readonly RecordingBoardClient _boardClient;

    public CandidateExecutorSlotsTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "candidate-slots-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoRoot);
        InitGitRepo(_repoRoot);

        _git = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _canonicalWorktree = _git.CreateWorktreeAsync(
            _repoRoot, "aiboard/canonical-1", CancellationToken.None).GetAwaiter().GetResult();

        _runStore = new RecordingRunStore();
        _boardClient = new RecordingBoardClient();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
        try { Directory.Delete(_repoRoot + "-worktrees", recursive: true); } catch { }
    }

    // ── Multi-slot fallback ─────────────────────────────────────────────────

    [Fact]
    public async Task TwoSlots_FirstSlotWins_SecondSlotNotInvoked()
    {
        // Slot 0 has one candidate that completes; should short-circuit.
        // Slot 1 has a candidate registered but its executor MUST NOT be
        // invoked. We register a sentinel that throws if called.
        var slot0Provider = new TrackingExecutor(AgentOutcome.COMPLETE, "slot 0 wins");
        var slot1Provider = new SentinelExecutor("slot 1 must not run");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["slot0-provider"] = slot0Provider,
            ["slot1-provider"] = slot1Provider,
        });

        var slot0 = new SlotConfig(Candidates: [new CandidateOverride("slot0-provider")]);
        var slot1 = new SlotConfig(Candidates: [new CandidateOverride("slot1-provider")]);

        var request = NewRequestWithSlots([slot0, slot1]);

        var result0 = await executor.ExecuteSlotAsync(slot0, slotIndex: 0, totalSlots: 2, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Won, result0.Outcome);
        Assert.Equal(1, slot0Provider.InvocationCount);
        // slot1Provider is the sentinel — would throw if invoked, so it
        // implicitly never ran. AgentRunner would short-circuit before slot 1.
    }

    [Fact]
    public async Task FirstSlotAllCandidatesFailed_ProducesSlotFailedOutcome()
    {
        // Both candidates in slot 0 return ERROR. The slot returns Failed,
        // signalling AgentRunner to try slot 1. (This test exercises the slot
        // boundary; AgentRunner's loop is tested separately via integration.)
        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["fail-a"]   = new ScriptedExecutor(AgentOutcome.ERROR, "boom A"),
            ["fail-b"]   = new ScriptedExecutor(AgentOutcome.ERROR, "boom B"),
            // evaluator never runs because all candidates failed
            ["claude-cli"] = new SentinelExecutor("evaluator must not run when all candidates fail"),
        });

        var slot = new SlotConfig(
            Candidates: [new CandidateOverride("fail-a"), new CandidateOverride("fail-b")],
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge"));

        var request = NewRequestWithSlots([slot]);

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 2, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task SlotEvaluatorPicksNeedsInfoWinner_PropagatesNeedsInfoNoFallback()
    {
        // The evaluator picks candidate 0, whose own outcome is NEEDS_INFO
        // (the agent has questions for the operator). Slot reports NeedsInfo
        // — AgentRunner short-circuits and surfaces the questions to the
        // operator without trying the next slot.
        var capturingEvaluator = new ScriptedExecutor(AgentOutcome.COMPLETE,
            """
            ```json
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":7,"reasoning":"thoughtful question"},
              {"index":1,"score":5,"reasoning":"misunderstood"}
            ]}
            ```
            """);

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["asks-question"] = new ScriptedExecutor(AgentOutcome.NEEDS_INFO, "What about edge case X?"),
            ["complete"]      = new ScriptedExecutor(AgentOutcome.COMPLETE, "implementation"),
            ["claude-cli"]    = capturingEvaluator,
        });

        var slot = new SlotConfig(
            Candidates: [new CandidateOverride("asks-question"), new CandidateOverride("complete")],
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge"));

        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.NeedsInfo, result.Outcome);
        Assert.Equal(AgentOutcome.NEEDS_INFO, result.AgentResult.Outcome);
    }

    [Fact]
    public async Task EvaluatorReturnsCompleteWithoutWinnerIndex_SlotFails()
    {
        // Schema-violation defense: outcome=COMPLETE without winner_index → ERROR
        // override → SlotResult.Failed (so AgentRunner tries the next slot).
        var brokenEvaluator = new ScriptedExecutor(AgentOutcome.COMPLETE,
            """
            Both look ok.
            ```json
            {"outcome":"COMPLETE","detail":"forgot to pick"}
            ```
            """);

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"]          = new ScriptedExecutor(AgentOutcome.COMPLETE, "A"),
            ["b"]          = new ScriptedExecutor(AgentOutcome.COMPLETE, "B"),
            ["claude-cli"] = brokenEvaluator,
        });

        var slot = new SlotConfig(
            Candidates: [new CandidateOverride("a"), new CandidateOverride("b")],
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge"));

        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 2, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Failed, result.Outcome);
        Assert.Contains("did not include a winner_index", result.AgentResult.Detail);
    }

    // ── Single-candidate slot, no evaluator ─────────────────────────────────

    [Fact]
    public async Task SingleCandidateSlot_NoEvaluator_SurfacesCandidateOutcomeDirectly()
    {
        var soleCandidate = new TrackingExecutor(AgentOutcome.COMPLETE, "did the thing");
        var sentinelEvaluator = new SentinelExecutor("evaluator must not run for single-candidate slots");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["sole"]       = soleCandidate,
            ["claude-cli"] = sentinelEvaluator,
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("sole")]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Won, result.Outcome);
        Assert.Equal(1, soleCandidate.InvocationCount);

        // Persisted as a single candidate row marked selected = true (winner)
        var stepRow = _runStore.SavedSteps.Single(r => r.CandidateGroupId is not null);
        Assert.Equal("sole", stepRow.Provider);
    }

    [Fact]
    public async Task SingleCandidateSlot_NoEvaluator_NeedsInfo_PropagatesNeedsInfo()
    {
        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["sole"] = new ScriptedExecutor(AgentOutcome.NEEDS_INFO, "I need clarification"),
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("sole")]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.NeedsInfo, result.Outcome);
        Assert.Equal(AgentOutcome.NEEDS_INFO, result.AgentResult.Outcome);
    }

    [Fact]
    public async Task SingleCandidateSlot_NoEvaluator_Error_FailsSlotForFallback()
    {
        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["sole"] = new ScriptedExecutor(AgentOutcome.ERROR, "broke"),
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("sole")]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 2, request, CancellationToken.None);

        // ERROR from a single-candidate-no-evaluator slot is Failed: the next
        // slot (if any) should pick up.
        Assert.Equal(SlotOutcome.Failed, result.Outcome);
    }

    // ── Per-candidate retries on RATE_LIMIT / TIMEOUT ───────────────────────

    [Fact]
    public async Task RateLimitException_IsRetried_WhenRetriesConfigured()
    {
        // Candidate has Retries = 2, RetryOn defaults to RATE_LIMIT+TIMEOUT.
        // Executor throws RateLimitException twice then COMPLETE. The
        // candidate's final result wins; intermediate failures are not
        // persisted as separate step_result rows.
        var rateLimitedThenOk = new RateLimitedThenSuccess(
            failuresBeforeSuccess: 2,
            successOutcome: AgentOutcome.COMPLETE,
            successDetail: "OK after retries");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["flaky"] = rateLimitedThenOk,
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("flaky", Retries: 2)]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Won, result.Outcome);
        Assert.Equal(3, rateLimitedThenOk.InvocationCount); // 2 fails + 1 success
        Assert.Single(_runStore.SavedSteps.Where(r => r.CandidateGroupId is not null)); // single final row
    }

    [Fact]
    public async Task RateLimitException_ExhaustsRetries_SlotReturnsFailedWithRateLimitFlag()
    {
        // Retries = 1 but executor always rate-limits → the post-retry
        // RateLimitException is converted to an ERROR outcome at the candidate
        // level instead of propagating, so sibling candidates and sibling
        // slots get a chance to run. The slot's WasRateLimited flag carries
        // the signal upward; AgentRunner re-raises a real RateLimitException
        // only when the ENTIRE chain failed with rate-limit (covered separately
        // in AgentRunner-level tests).
        var alwaysRateLimited = new AlwaysFailing(new RateLimitException(
            "subscription out", RateLimitSource.AgentCli));

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["broken"] = alwaysRateLimited,
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("broken", Retries: 1)]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(
            slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Failed, result.Outcome);
        Assert.True(result.WasRateLimited, "WasRateLimited should be set when every candidate exhausts rate-limit retries");
        Assert.Equal(AgentOutcome.ERROR, result.AgentResult.Outcome);
        Assert.Contains("RATE_LIMIT", result.AgentResult.Detail ?? string.Empty);
        // Should have retried Retries+1 = 2 times before giving up.
        Assert.Equal(2, alwaysRateLimited.InvocationCount);
    }

    [Fact]
    public async Task TimeoutException_IsRetried_OnDefaultRetryOn()
    {
        var timeoutThenOk = new TimeoutThenSuccess(
            failuresBeforeSuccess: 1,
            successOutcome: AgentOutcome.COMPLETE,
            successDetail: "OK after one timeout");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["timeouty"] = timeoutThenOk,
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("timeouty", Retries: 1)]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Won, result.Outcome);
        Assert.Equal(2, timeoutThenOk.InvocationCount);
    }

    [Fact]
    public async Task InactivityTimeoutException_IsRetried_SameAsTimeoutException()
    {
        // InactivityTimeoutException : TimeoutException, so the retry-on-TIMEOUT
        // catch clause must cover it transparently — operators don't need to
        // add a new FailureReason value or update RetryOn lists when the
        // inactivity-timer feature ships.
        var inactivityThenOk = new InactivityThenSuccess(
            failuresBeforeSuccess: 1,
            successOutcome: AgentOutcome.COMPLETE,
            successDetail: "OK after one inactivity timeout");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["sticky"] = inactivityThenOk,
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("sticky", Retries: 1)]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Won, result.Outcome);
        Assert.Equal(2, inactivityThenOk.InvocationCount);
    }

    [Fact]
    public async Task RateLimitException_NotInRetryOn_SlotReturnsFailedAfterFirstAttempt()
    {
        // Retries = 5 but RetryOn = [TIMEOUT] only — rate-limit is NOT retried.
        // Same conversion-to-ERROR-with-flag treatment as the retries-exhausted
        // case; just no retries happen first.
        var alwaysRateLimited = new AlwaysFailing(new RateLimitException(
            "out", RateLimitSource.AgentCli));

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["expensive"] = alwaysRateLimited,
        });

        var slot = new SlotConfig(Candidates:
        [
            new CandidateOverride("expensive", Retries: 5, RetryOn: [FailureReason.TIMEOUT])
        ]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(
            slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Failed, result.Outcome);
        Assert.True(result.WasRateLimited);
        // Only 1 attempt: rate-limit is not in RetryOn so no retry.
        Assert.Equal(1, alwaysRateLimited.InvocationCount);
    }

    [Fact]
    public async Task GenericException_IsNotRetried_RecordedAsErrorOutcome()
    {
        // A non-rate-limit / non-timeout exception (e.g. InvalidOperationException
        // from a parser) is recorded as ERROR — no retry, slot fails for fallback.
        var generic = new AlwaysFailing(new InvalidOperationException("parser blew up"));

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["broken"] = generic,
        });

        var slot = new SlotConfig(Candidates: [new CandidateOverride("broken", Retries: 5)]);
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(slot, slotIndex: 0, totalSlots: 2, request, CancellationToken.None);

        // 1 attempt; no retries. Slot fails so AgentRunner tries next slot.
        Assert.Equal(1, generic.InvocationCount);
        Assert.Equal(SlotOutcome.Failed, result.Outcome);
    }

    // ── Rate-limit propagation across slot boundary (mirrors AgentRunner) ──

    [Fact]
    public async Task TwoSlot_Slot0AllRateLimited_Slot1Wins_NoExceptionPropagated()
    {
        // Slot 0's only candidate exhausts rate-limit retries. Per the new
        // semantics, that no longer throws — it returns SlotResult.Failed
        // with WasRateLimited=true. The slot-loop falls through to slot 1,
        // which succeeds. Net effect: card progresses normally, no card-back-
        // to-Ready bounce. This is the "primary subscription out, fallback
        // takes over" headline use case.
        var alwaysRateLimited = new AlwaysFailing(new RateLimitException(
            "primary out", RateLimitSource.AgentCli));
        var fallbackOk = new TrackingExecutor(AgentOutcome.COMPLETE, "fallback delivered");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["primary"]  = alwaysRateLimited,
            ["fallback"] = fallbackOk,
        });

        var slots = new List<SlotConfig>
        {
            new(Candidates: [new CandidateOverride("primary",  Retries: 0)]),
            new(Candidates: [new CandidateOverride("fallback", Retries: 0)]),
        };
        var request = NewRequestWithSlots(slots, gitBehavior: "discard");

        // Walk slots like AgentRunner does. On Failed, fall through.
        SlotResult? slot0 = null, slot1 = null;
        slot0 = await executor.ExecuteSlotAsync(slots[0], 0, slots.Count, request, CancellationToken.None);
        Assert.Equal(SlotOutcome.Failed, slot0.Outcome);
        Assert.True(slot0.WasRateLimited, "slot 0's only candidate hit RateLimit; flag should be set");

        slot1 = await executor.ExecuteSlotAsync(slots[1], 1, slots.Count, request, CancellationToken.None);
        Assert.Equal(SlotOutcome.Won, slot1.Outcome);
        Assert.False(slot1.WasRateLimited);
        Assert.Equal(1, fallbackOk.InvocationCount);
    }

    [Fact]
    public async Task SingleSlot_OneCandidateRateLimited_OneSucceeds_SlotWonNotRateLimited()
    {
        // Mixed within a single slot: claude rate-limits, opencode succeeds.
        // Pre-fix, claude's RateLimitException would propagate out of
        // Task.WhenAll and abort the whole slot. Post-fix, it's converted to
        // ERROR with the rate-limit flag, the evaluator sees one COMPLETE
        // candidate, picks it, and the slot wins. WasRateLimited must NOT
        // be true — at least one candidate produced a real result, so the
        // chain isn't capacity-blocked.
        var rateLimited = new AlwaysFailing(new RateLimitException(
            "claude out", RateLimitSource.AgentCli));
        var succeeds = new ScriptedExecutor(AgentOutcome.COMPLETE, "qwen result");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = rateLimited,
            ["docker-opencode"]   = succeeds,
            ["claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE,
                "qwen wins.\n```json\n{\"outcome\":\"COMPLETE\",\"winner_index\":1," +
                "\"scores\":[{\"index\":0,\"score\":2,\"reasoning\":\"rate-limited\"}," +
                "{\"index\":1,\"score\":8,\"reasoning\":\"\"}]}\n```"),
        });

        var slot = new SlotConfig(
            Candidates: [
                new CandidateOverride("docker-claude-cli", Retries: 0),
                new CandidateOverride("docker-opencode",   Retries: 0),
            ],
            Evaluator: new EvaluatorConfig(
                Role: "evaluator",
                TaskPrompt: "pick"));
        var request = NewRequestWithSlots([slot], gitBehavior: "discard");

        var result = await executor.ExecuteSlotAsync(
            slot, slotIndex: 0, totalSlots: 1, request, CancellationToken.None);

        Assert.Equal(SlotOutcome.Won, result.Outcome);
        Assert.False(result.WasRateLimited);
    }

    // ── Multi-slot integration via direct loop (mirrors AgentRunner) ────────

    [Fact]
    public async Task TwoSlot_Slot0Fails_Slot1Wins_ChainsViaAgentRunnerEquivalentLoop()
    {
        // Mimics AgentRunner's loop: try slot 0; if Failed, try slot 1.
        var slot0Failed = new ScriptedExecutor(AgentOutcome.ERROR, "primary blew up");
        var slot1Won = new TrackingExecutor(AgentOutcome.COMPLETE, "fallback ok");

        var executor = BuildExecutor(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["primary"]   = slot0Failed,
            ["fallback"]  = slot1Won,
        });

        var slots = new List<SlotConfig>
        {
            new(Candidates: [new CandidateOverride("primary")]),
            new(Candidates: [new CandidateOverride("fallback")]),
        };
        var request = NewRequestWithSlots(slots, gitBehavior: "discard");

        SlotResult? finalResult = null;
        for (var i = 0; i < slots.Count; i++)
        {
            finalResult = await executor.ExecuteSlotAsync(slots[i], i, slots.Count, request, CancellationToken.None);
            if (finalResult.Outcome != SlotOutcome.Failed) break;
        }

        Assert.NotNull(finalResult);
        Assert.Equal(SlotOutcome.Won, finalResult!.Outcome);
        Assert.Equal(1, slot1Won.InvocationCount);

        // Both slots' candidate rows persisted, with slot_index populated.
        var slotRows = _runStore.SavedSteps.Where(r => r.SlotIndex.HasValue).ToList();
        Assert.Contains(slotRows, r => r.SlotIndex == 0 && r.Provider == "primary");
        Assert.Contains(slotRows, r => r.SlotIndex == 1 && r.Provider == "fallback");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private CandidateExecutor BuildExecutor(IReadOnlyDictionary<string, IAgentExecutor> byProvider)
        => new(_git,
               new MapResolver(byProvider),
               _runStore,
               _boardClient,
               NullLogger<CandidateExecutor>.Instance);

    private CandidateGroupRequest NewRequestWithSlots(
        IReadOnlyList<SlotConfig> slots,
        string gitBehavior = "discard")
    {
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# system prompt");

        return new CandidateGroupRequest(
            RunId: "run-slots-1",
            CardId: TestCardId,
            CardTitle: TestCardTitle,
            StateName: "Implementing",
            StepIndex: 0,
            Step: new WorkflowStep(
                Name: "implement",
                Role: "implementer",
                TaskPrompt: "do work",
                Slots: slots.ToList()),
            Role: new WorkflowRole(
                Model: "claude-sonnet-4-6",
                SystemPrompt: "you are an implementer",
                Sections: []),
            WorkflowRoles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-sonnet-4-6", "sys", []),
                ["evaluator"]   = new("claude-opus-4-6", "you are an evaluator", []),
            },
            StateProviderParams: null,
            TaskPrompt: "do work",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: gitBehavior,
            CommentsFilePath: null,
            PromptBaseDirectory: null);
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private class ScriptedExecutor(AgentOutcome outcome, string detail) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new AgentResult(outcome, detail));
    }

    private sealed class TrackingExecutor(AgentOutcome outcome, string detail) : IAgentExecutor
    {
        public int InvocationCount { get; private set; }
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(new AgentResult(outcome, detail));
        }
    }

    private sealed class SentinelExecutor(string message) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }

    private sealed class AlwaysFailing(Exception ex) : IAgentExecutor
    {
        public int InvocationCount { get; private set; }
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw ex;
        }
    }

    private sealed class RateLimitedThenSuccess(
        int failuresBeforeSuccess,
        AgentOutcome successOutcome,
        string successDetail) : IAgentExecutor
    {
        public int InvocationCount { get; private set; }
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (InvocationCount <= failuresBeforeSuccess)
                throw new RateLimitException("transient", RateLimitSource.AgentCli);
            return Task.FromResult(new AgentResult(successOutcome, successDetail));
        }
    }

    private sealed class TimeoutThenSuccess(
        int failuresBeforeSuccess,
        AgentOutcome successOutcome,
        string successDetail) : IAgentExecutor
    {
        public int InvocationCount { get; private set; }
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (InvocationCount <= failuresBeforeSuccess)
                throw new TimeoutException("test timeout");
            return Task.FromResult(new AgentResult(successOutcome, successDetail));
        }
    }

    private sealed class InactivityThenSuccess(
        int failuresBeforeSuccess,
        AgentOutcome successOutcome,
        string successDetail) : IAgentExecutor
    {
        public int InvocationCount { get; private set; }
        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (InvocationCount <= failuresBeforeSuccess)
                throw new InactivityTimeoutException(
                    "no output for 1200s (inactivity timeout)", inactivitySeconds: 1200);
            return Task.FromResult(new AgentResult(successOutcome, successDetail));
        }
    }

    private sealed class MapResolver(IReadOnlyDictionary<string, IAgentExecutor> map) : IAgentExecutorResolver
    {
        public IReadOnlySet<string> AvailableProviders { get; } =
            new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase);
        public IAgentExecutor Resolve(string providerKey)
            => map.TryGetValue(providerKey, out var ex) ? ex
                : throw new InvalidOperationException($"No executor for '{providerKey}'");
    }

    private sealed class RecordingRunStore : IRunStore
    {
        // Lock-guarded: parallel candidate execution can race List<T>.Add otherwise.
        private readonly object _lock = new();
        private readonly List<StepResultRecord> _savedSteps = new();
        private readonly List<RecordedVerdict> _recordedVerdicts = new();
        public IReadOnlyList<StepResultRecord> SavedSteps { get { lock (_lock) { return _savedSteps.ToList(); } } }
        public IReadOnlyList<RecordedVerdict> RecordedVerdicts { get { lock (_lock) { return _recordedVerdicts.ToList(); } } }
        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct) { lock (_lock) { _savedSteps.Add(result); } return Task.CompletedTask; }
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct)
        {
            lock (_lock) { _recordedVerdicts.Add(new RecordedVerdict(runId, candidateGroupId, candidateIndex, selected, qualityScore, evaluatorReasoning)); }
            return Task.CompletedTask;
        }
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed record RecordedVerdict(string RunId, Guid GroupId, int CandidateIndex, bool Selected, decimal? QualityScore, string? Reasoning);

    private sealed class RecordingBoardClient : ITaskBoardClient
    {
        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAgentCommentAsync(string cardId, string commentBody, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAgentCommentsByMarkerAsync(string cardId, string markerSubstring, CancellationToken ct) => Task.CompletedTask;
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken ct, IReadOnlyList<string>? excludeStatuses = null) => throw new NotImplementedException();
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken ct) => throw new NotImplementedException();
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken ct) => throw new NotImplementedException();
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken ct) => throw new NotImplementedException();
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken ct) => throw new NotImplementedException();
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken ct) => throw new NotImplementedException();
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken ct) => throw new NotImplementedException();
        public Task AssignAsync(string cardId, string username, CancellationToken ct) => throw new NotImplementedException();
        public Task UnassignAsync(string cardId, string? username, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> GetCurrentUserAsync(CancellationToken ct) => throw new NotImplementedException();
    }
}
