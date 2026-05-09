using System.Globalization;
using System.Text;
using System.Text.Json;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Runs a single SLOT of a step's candidate-fallback chain: N executors race
/// on the same task in separate worktrees (in parallel), an evaluator picks a
/// winner among the survivors, and the winning branch / artifact set is
/// promoted onto the canonical worktree.
/// </summary>
/// <remarks>
/// Owned by <see cref="AgentRunner"/>. AgentRunner walks the step's effective
/// slots (<see cref="WorkflowStep.GetEffectiveSlots"/>) and calls
/// <see cref="ExecuteSlotAsync"/> per slot in order. Each slot returns a
/// <see cref="SlotResult"/> indicating Won / NeedsInfo / Failed; AgentRunner
/// short-circuits on Won/NeedsInfo and falls back to the next slot on Failed.
/// <para>
/// Single-candidate slots whose <see cref="SlotConfig.Evaluator"/> is null
/// skip the evaluator phase entirely — the candidate's outcome surfaces
/// directly. Multi-candidate slots require an evaluator (the validator
/// enforces this).
/// </para>
/// <para>
/// Per-candidate retries on transient failures (RATE_LIMIT, TIMEOUT) happen
/// in-place inside the slot before its result is decided; they do not cross
/// slot boundaries — that's what fallback slots are for.
/// </para>
/// <para>
/// Both commit-based git behaviors (<c>commit_only</c> / <c>commit_and_push</c>)
/// and <c>discard</c> mode are supported. In commit modes the winner is
/// promoted via <see cref="GitWorkspaceManager.ResetWorktreeToBranchAsync"/>;
/// in discard mode via <see cref="PromoteDiscardWinnerArtifacts"/>
/// (file-copy, no commits).
/// </para>
/// </remarks>
public sealed class CandidateExecutor(
    GitWorkspaceManager gitWorkspaceManager,
    IAgentExecutorResolver executorResolver,
    IRunStore runStore,
    ITaskBoardClient boardClient,
    ILogger<CandidateExecutor> logger,
    IResourcePool? resourcePool = null,
    AgentIdentity? agentIdentity = null,
    // Rerun redesign Problem 2: route per-candidate audit comments through the
    // comment router (kind:candidate → append) when wired. When null, direct
    // fallback calls still append the same aiboard-log marker shape.
    ICommentRouter? commentRouter = null,
    Func<int, CancellationToken, Task>? retryDelay = null)
{
    /// <summary>
    /// Backward-compatible single-slot entry. Treats the request's step as a
    /// single-slot config (legacy <c>Candidates</c>+<c>Evaluator</c>) and
    /// returns just the <see cref="AgentResult"/>. New code should call
    /// <see cref="ExecuteSlotAsync"/> directly with explicit slots from
    /// <see cref="WorkflowStep.GetEffectiveSlots"/>.
    /// </summary>
    public async Task<AgentResult> ExecuteCandidateGroupAsync(
        CandidateGroupRequest request, CancellationToken cancellationToken)
    {
        var slots = request.Step.GetEffectiveSlots();
        if (slots.Count == 0)
        {
            throw new InvalidOperationException(
                $"ExecuteCandidateGroupAsync called for step '{request.Step.Name}' with no candidates.");
        }
        if (slots.Count > 1)
        {
            throw new InvalidOperationException(
                $"ExecuteCandidateGroupAsync called for step '{request.Step.Name}' with {slots.Count} slots. " +
                "Multi-slot steps must be driven by AgentRunner's slot loop calling ExecuteSlotAsync per slot.");
        }
        var result = await ExecuteSlotAsync(slots[0], slotIndex: 0, totalSlots: 1, request, cancellationToken);
        return result.AgentResult;
    }

    /// <summary>
    /// Run a single slot end-to-end: phase 1 candidate fan-out (with per-candidate
    /// retries), phase 2 evaluator, phase 3 verdict parse, phase 4 winner promotion,
    /// phase 5 comment posting + cleanup. The returned <see cref="SlotResult"/>
    /// tells AgentRunner whether to short-circuit (Won / NeedsInfo) or fall back
    /// to the next slot (Failed).
    /// </summary>
    /// <param name="slot">This slot's candidates + evaluator.</param>
    /// <param name="slotIndex">0-based position in the step's slot list (used in
    ///   step_result names and comment markers when <paramref name="totalSlots"/> &gt; 1).</param>
    /// <param name="totalSlots">Total slot count for this step. When 1, names and
    ///   markers omit the slot infix for backward compatibility with single-slot configs.</param>
    public async Task<SlotResult> ExecuteSlotAsync(
        SlotConfig slot,
        int slotIndex,
        int totalSlots,
        CandidateGroupRequest request,
        CancellationToken cancellationToken)
    {
        var step = request.Step;
        var candidates = slot.Candidates ?? throw new InvalidOperationException(
            $"ExecuteSlotAsync called for step '{step.Name}' slot {slotIndex} with no candidates.");
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"ExecuteSlotAsync called for step '{step.Name}' slot {slotIndex} with empty candidates list.");

        // Single-candidate slots may omit the evaluator: the runtime surfaces
        // the candidate's outcome directly. Multi-candidate slots require one;
        // the validator enforces this.
        var skipEvaluator = candidates.Count == 1 && slot.Evaluator is null;
        var evaluatorCfg = slot.Evaluator;
        if (!skipEvaluator && evaluatorCfg is null)
        {
            throw new InvalidOperationException(
                $"Step '{step.Name}' slot {slotIndex} has {candidates.Count} candidates but no evaluator. " +
                "Validator should have caught this.");
        }

        var groupId = Guid.NewGuid();

        logger.LogInformation(
            "Starting slot {SlotIndex}/{TotalSlots} for step '{StepName}' on card {CardId}: {Count} candidate(s), groupId={GroupId}{Eval}",
            slotIndex, totalSlots, step.Name, request.CardId, candidates.Count, groupId,
            skipEvaluator ? ", evaluator skipped (single-candidate)" : "");

        var canonicalBranch = await gitWorkspaceManager.GetCurrentBranchAsync(
            request.WorktreePath, cancellationToken);

        // Capture the canonical SHA at slot start. Used as the diff base when
        // building the evaluator's prompt: each candidate's `git diff HEAD`
        // is empty post-orchestrator-commit, so we diff against this stable
        // pre-divergence SHA to surface the candidate's actual work.
        // Discard-mode slots ignore this (they show .aiboard/ file content
        // instead of git diffs).
        var slotStartCanonicalSha = await gitWorkspaceManager.GetCurrentShaAsync(
            request.WorktreePath, cancellationToken);

        // ── Phase 1: run all candidates in parallel ─────────────────────────
        // Every candidate runs concurrently — including same-provider, different-
        // model candidates (e.g. Opus + Sonnet + Haiku on docker-claude-cli).
        // Wall-clock time is bounded by the SLOWEST single candidate, not by
        // the sum or any provider-group total.
        //
        // Concurrency caps that genuinely matter (e.g. a single shared local
        // llama.cpp server handling docker-opencode + docker-claude-qwen) are
        // expressed via the ResourcePool — declare a named pool with a slot
        // count and tag the providers that need it. Account-level rate limits
        // (Anthropic, OpenAI) are best handled via the per-candidate retry
        // loop in ExecuteCandidateWithRetriesAsync, which converts a 429 to an
        // ERROR-with-rate-limit-flag for slot/chain-level fallback rather than
        // by pre-emptively serialising siblings.
        //
        // The original candidate-index is preserved in the returned list so
        // downstream consumers (evaluator's "Candidate N" enumeration,
        // step_result.candidate_index, branch naming) see the declaration
        // order regardless of completion order.
        var indexedCandidates = candidates
            .Select((candidate, index) => (Index: index, Candidate: candidate))
            .ToList();

        logger.LogInformation(
            "Slot {SlotIndex}/{TotalSlots} step '{StepName}' card {CardId}: fanning out {CandidateCount} candidate(s) in parallel ({Layout})",
            slotIndex, totalSlots, step.Name, request.CardId,
            indexedCandidates.Count,
            string.Join(", ", indexedCandidates.Select(c =>
                $"#{c.Index}:{c.Candidate.Provider}{(c.Candidate.Model is null ? "" : "/" + c.Candidate.Model)}")));

        // Pre-compute the deterministic branch name for every candidate. Used
        // by the slot-throw catch block to sweep candidates whose tasks were
        // still in flight (or faulted) when Task.WhenAll threw — those don't
        // produce a CandidateExecution, so the post-success cleanup path can't
        // see them. Without this sweep their worktrees + branches leak.
        var predictedBranches = candidates
            .Select((candidate, index) => (
                Index: index,
                BranchName: BuildCandidateBranchName(
                    request.CardId, groupId, slotIndex, totalSlots, index,
                    SlugifyProvider(candidate.Provider))))
            .ToList();

        var candidateTasks = indexedCandidates.Select(async entry =>
        {
            var execution = await ExecuteSingleCandidateAsync(
                request, slotIndex, totalSlots, groupId, canonicalBranch,
                entry.Index, entry.Candidate, cancellationToken);
            return (Index: entry.Index, Execution: execution);
        }).ToList();

        // Task.WhenAll waits for every candidate to complete (or throw). On a
        // thrown RateLimitException from any candidate, sibling candidates
        // still finish their current await before propagation. Because branch
        // names embed a per-run UUID, leaked candidate worktrees from siblings
        // are NOT self-cleaning on a re-run (the next attempt uses different
        // paths) — so we sweep them here on the way out before rethrowing.
        // AgentRunner still restores the card to the trigger column on
        // RateLimitException; this just stops disk accumulation.
        (int Index, CandidateExecution Execution)[] allCandidateResults;
        try
        {
            allCandidateResults = await Task.WhenAll(candidateTasks);
        }
        catch
        {
            // Sweep the FULL predicted branch list, not just RanToCompletion.
            // RemoveWorktreeAsync is non-throwing for branches/worktrees that
            // never got created (the underlying git commands fail; warnings
            // logged), so passing in-flight predictions is safe — and catches
            // candidates whose worktree got created but whose CandidateExecution
            // was never returned.
            try
            {
                // Use CancellationToken.None — the original token may be
                // cancelled (shutdown path), but disk state still needs
                // cleaning. Cleanup is itself best-effort; failures log.
                await CleanupAllCandidateWorktreesAsync(
                    request.RepoPath, predictedBranches, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx,
                    "Failed to clean up {Count} predicted candidate worktree(s) after slot {SlotIndex} threw",
                    predictedBranches.Count, slotIndex);
            }
            throw;
        }

        var executions = allCandidateResults
            .OrderBy(r => r.Index)
            .Select(r => r.Execution)
            .ToList();

        // ── Phase 1b: single-candidate slot with no evaluator ────────────────
        // The slot's result is the (only) candidate's result, regardless of
        // outcome. Skip the evaluator phase entirely. Checked BEFORE the
        // all-failed short-circuit so a NEEDS_INFO from the single candidate
        // surfaces as NeedsInfo (propagate questions to operator) rather than
        // Failed (force fallback to next slot, losing the questions).
        if (skipEvaluator)
        {
            var soleCandidate = executions[0];
            logger.LogInformation(
                "Single-candidate slot {SlotIndex} for step '{StepName}': using candidate 0 ({Provider}) directly without evaluator (outcome={Outcome})",
                slotIndex, step.Name, soleCandidate.Provider, soleCandidate.AgentResult.Outcome);

            // ERROR / NEEDS_INFO from the sole candidate: no winner to promote,
            // so we just clean up and return the candidate's outcome directly.
            // COMPLETE: promote artifacts and return Won.
            if (soleCandidate.AgentResult.Outcome != AgentOutcome.COMPLETE)
            {
                await CleanupCandidateWorktreesAsync(
                    request.RepoPath, executions, cancellationToken);

                try
                {
                    await PostCandidateCommentsAsync(
                        request, step.Name, slotIndex, totalSlots, executions,
                        soleCandidate.AgentResult,
                        new EvaluatorVerdict(WinnerIndex: null, Scores: []),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to post per-candidate comments for single-candidate slot {SlotIndex} of step '{StepName}'",
                        slotIndex, step.Name);
                }

                // NEEDS_INFO short-circuits the slot chain (questions reach the
                // operator). ERROR falls through to the next slot. The
                // rate-limit flag is meaningful only on Failed: if the sole
                // candidate's ERROR was due to rate-limit, AgentRunner can use
                // it to keep the chain "rate-limit only" and eventually bubble.
                return new SlotResult(
                    soleCandidate.AgentResult.Outcome == AgentOutcome.NEEDS_INFO
                        ? SlotOutcome.NeedsInfo  // propagate questions; no fallback
                        : SlotOutcome.Failed,    // ERROR → fallback to next slot
                    soleCandidate.AgentResult,
                    WasRateLimited: soleCandidate.RateLimited);
            }

            // COMPLETE: promote and finalize as the winner.
            return await PromoteAndFinalizeWinnerAsync(
                request, slotIndex, totalSlots, step, executions,
                winnerIdx: 0,
                evaluatorResultForReturn: soleCandidate.AgentResult,
                verdict: new EvaluatorVerdict(
                    WinnerIndex: 0,
                    Scores: [new CandidateScore(Index: 0, Score: null, Reasoning: null)]),
                evaluatorTempPromptPath: null,
                cancellationToken);
        }

        // ── Short-circuit: if every candidate failed, skip the evaluator ─────
        // (Multi-candidate slot path. The single-candidate-no-evaluator case
        // was handled above so it can surface NEEDS_INFO as the slot's outcome.)
        if (executions.All(e => e.AgentResult.Outcome != AgentOutcome.COMPLETE))
        {
            logger.LogWarning(
                "All {Count} candidate(s) for step '{StepName}' slot {SlotIndex} returned non-COMPLETE outcomes; halting without running evaluator",
                executions.Count, step.Name, slotIndex);

            await CleanupCandidateWorktreesAsync(
                request.RepoPath, executions, cancellationToken);

            // Pick the merged outcome by recoverability rather than candidate
            // declaration order: NEEDS_INFO is recoverable by a human (card moves
            // to Questions), ERROR is terminal. The SLOT outcome is Failed
            // either way — the slot didn't produce a single coherent result,
            // so AgentRunner should try the next slot. The merged
            // NEEDS_INFO/ERROR matters only as the AgentResult to surface if
            // every slot ends up failing.
            var mergedOutcome = executions.Any(e => e.AgentResult.Outcome == AgentOutcome.NEEDS_INFO)
                ? AgentOutcome.NEEDS_INFO
                : AgentOutcome.ERROR;
            var detail = BuildAllFailedDetail(executions);

            // Best-effort: post per-candidate audit comments even on all-failed
            // so the operator can see what each provider returned. No verdict
            // yet (no evaluator ran); pass an empty verdict.
            try
            {
                await PostCandidateCommentsAsync(
                    request, step.Name, slotIndex, totalSlots, executions,
                    new AgentResult(mergedOutcome, detail),
                    new EvaluatorVerdict(WinnerIndex: null, Scores: []),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to post per-candidate comments for all-failed slot {SlotIndex} of step '{StepName}'",
                    slotIndex, step.Name);
            }

            // WasRateLimited: only true when EVERY candidate in the slot ended
            // in a transient rate-limit failure. A single non-rate-limit
            // candidate (e.g., a hard ERROR from a code-broken provider, or a
            // legitimate NEEDS_INFO) means the slot's failure isn't purely
            // capacity-driven — AgentRunner shouldn't treat the chain as
            // rate-limited just because one of N providers ran into a 429.
            var slotWasRateLimited = executions.All(e => e.RateLimited);
            return new SlotResult(
                SlotOutcome.Failed,
                new AgentResult(mergedOutcome, detail),
                WasRateLimited: slotWasRateLimited);
        }

        // ── Phase 2: run the evaluator ───────────────────────────────────────
        // RunEvaluatorAsync may write an inline-system-prompt to a temp file;
        // it returns both the result and the path so we can clean up afterwards.
        var (evaluatorResult, evaluatorTempPromptPath) = await RunEvaluatorAsync(
            request, evaluatorCfg!, slotIndex, totalSlots, executions, groupId,
            slotStartCanonicalSha, cancellationToken);

        // Phases 3–5 are wrapped in try/finally so the evaluator's inline-prompt
        // temp file is always cleaned up even if parsing, persistence, promotion,
        // or comment posting throws. Without this guard, exceptions between
        // phase 2 and the cleanup at the bottom would orphan files in /tmp.
        try
        {
            // ── Phase 3: parse evaluator output, persist per-candidate verdicts ──
            var verdict = ParseEvaluatorVerdict(
                evaluatorResult, executions.Count, evaluatorCfg!.Scoring);

            // Schema-violation defense: outcome=COMPLETE without a winner_index
            // is the failure mode KvA hit on v0.0.15. The evaluator schema
            // (EvaluatorOutcomeSchema) makes winner_index required when
            // outcome=COMPLETE, but we still defend in code for two reasons:
            //   (1) The OpenAI variant types winner_index as ["integer", "null"]
            //       because OpenAI structured outputs don't support if/then —
            //       so a null can still slip through on the codex path.
            //   (2) Some CLI versions / models may drift on schema enforcement.
            // Surface this as ERROR with a clear message instead of silently
            // discarding all candidates. Slot-level: this is Failed → fallback.
            if (evaluatorResult.Outcome == AgentOutcome.COMPLETE && verdict.WinnerIndex is null)
            {
                logger.LogWarning(
                    "Evaluator returned outcome=COMPLETE but winner_index was missing or null for step '{StepName}' slot {SlotIndex} — overriding to ERROR (schema violation).",
                    request.Step.Name, slotIndex);
                evaluatorResult = new AgentResult(
                    AgentOutcome.ERROR,
                    "Evaluator returned outcome=COMPLETE but did not include a winner_index. " +
                    "The evaluator schema requires winner_index when outcome=COMPLETE; the response violated this contract. " +
                    "Original detail:\n\n" + (evaluatorResult.Detail ?? "(empty)"));
            }

            await PersistEvaluatorVerdictAsync(
                request.RunId, groupId, executions, verdict, cancellationToken);

            // ── Phase 4 & 5: promote winner + post comments ───────────────────
            if (evaluatorResult.Outcome == AgentOutcome.COMPLETE && verdict.WinnerIndex is int winnerIdx)
            {
                return await PromoteAndFinalizeWinnerAsync(
                    request, slotIndex, totalSlots, step, executions,
                    winnerIdx, evaluatorResult, verdict,
                    evaluatorTempPromptPath: null,  // we own the cleanup in finally
                    cancellationToken);
            }

            // No winner: the evaluator declined to pick (NEEDS_INFO or ERROR or
            // post-override). Tear everything down; the canonical worktree is
            // untouched. NEEDS_INFO from the evaluator surfaces up as a slot
            // success-with-questions (no fallback). ERROR maps to Failed.
            logger.LogInformation(
                "Evaluator did not select a winner for slot {SlotIndex} of step '{StepName}' (outcome={Outcome}); cleaning up all {Count} candidate worktrees",
                slotIndex, step.Name, evaluatorResult.Outcome, executions.Count);
            await CleanupCandidateWorktreesAsync(
                request.RepoPath, executions, cancellationToken);

            // Phase 5: post per-candidate audit comments
            await PostCandidateCommentsAsync(
                request, step.Name, slotIndex, totalSlots, executions, evaluatorResult, verdict, cancellationToken);

            var slotOutcome = evaluatorResult.Outcome == AgentOutcome.NEEDS_INFO
                ? SlotOutcome.NeedsInfo   // evaluator's own questions propagate
                : SlotOutcome.Failed;     // ERROR / no-winner_index → fallback
            return new SlotResult(slotOutcome, evaluatorResult);
        }
        finally
        {
            // Clean up the evaluator's inline-prompt temp file (if one was created).
            // Best-effort; orphaned files are harmless but accumulate in /tmp.
            if (evaluatorTempPromptPath is not null)
            {
                try { File.Delete(evaluatorTempPromptPath); }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "Failed to delete evaluator inline-prompt temp file {Path}",
                        evaluatorTempPromptPath);
                }
            }
        }
    }

    /// <summary>
    /// Phase 4 + 5 helper: promote the chosen winner's artifacts (file-based or
    /// git-reset depending on git behaviour), tear down loser worktrees, post
    /// per-candidate comments, and translate the result into a
    /// <see cref="SlotResult"/>. Used by both the evaluator-picks-winner path
    /// and the single-candidate-no-evaluator path.
    /// </summary>
    private async Task<SlotResult> PromoteAndFinalizeWinnerAsync(
        CandidateGroupRequest request,
        int slotIndex, int totalSlots,
        WorkflowStep step,
        IReadOnlyList<CandidateExecution> executions,
        int winnerIdx,
        AgentResult evaluatorResultForReturn,
        EvaluatorVerdict verdict,
        string? evaluatorTempPromptPath,
        CancellationToken cancellationToken)
    {
        var winner = executions[winnerIdx];
        var promotionMode = IsDiscardMode(request.GitBehavior) ? "files" : "git";
        logger.LogInformation(
            "Promoting candidate {Index} (provider={Provider}) as winner for step '{StepName}' slot {SlotIndex} via {Mode} promotion",
            winnerIdx, winner.Provider, step.Name, slotIndex, promotionMode);

        try
        {
            if (IsDiscardMode(request.GitBehavior))
            {
                PromoteDiscardWinnerArtifacts(winner.WorktreePath, request.WorktreePath);
            }
            else
            {
                await gitWorkspaceManager.ResetWorktreeToBranchAsync(
                    request.WorktreePath, winner.BranchName, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Promotion failure is fatal for THIS slot — surface as Failed so
            // AgentRunner falls back to the next slot rather than continuing
            // with stale canonical state.
            logger.LogError(ex,
                "Failed to promote candidate {Index} ({Mode} promotion, branch={Branch}) for step '{StepName}' slot {SlotIndex}",
                winnerIdx, promotionMode, winner.BranchName, step.Name, slotIndex);
            await CleanupCandidateWorktreesAsync(
                request.RepoPath, executions, cancellationToken);
            return new SlotResult(
                SlotOutcome.Failed,
                new AgentResult(
                    AgentOutcome.ERROR,
                    $"Evaluator selected candidate {winnerIdx} but {promotionMode} promotion failed: {ex.Message}"));
        }

        // Tear down ALL candidate worktrees AND branches (winner included).
        // Commit-mode winners had their commits promoted to canonical via
        // git reset --hard; discard-mode winners had their .aiboard/ artifacts
        // copied via PromoteDiscardWinnerArtifacts. Either way, the candidate's
        // branch ref no longer carries unique state, and preserving it would
        // accumulate aiboard-cand/... branches across runs.
        await CleanupCandidateWorktreesAsync(
            request.RepoPath, executions, cancellationToken);

        // Phase 5: post per-candidate audit comments
        await PostCandidateCommentsAsync(
            request, step.Name, slotIndex, totalSlots, executions, evaluatorResultForReturn, verdict, cancellationToken);

        // Evaluator-commits-winner-section: the doc says "the evaluator commits
        // the winner's section_update on the group's behalf" — meaning the
        // winning candidate's Section should be the one applied to the card,
        // not the evaluator's own (we instruct the evaluator to set
        // strategy=leave). Substitute the winner's Section into the result that
        // propagates up to AgentRunner.DescriptionWriter so the winner's
        // section_update lands in the managed description on Won.
        //
        // For the single-candidate-no-evaluator path, evaluatorResultForReturn
        // already IS the candidate's own AgentResult, so its Section is already
        // the winner's — the rewrite is a no-op-by-equality. For the
        // multi-candidate path, evaluatorResultForReturn is the evaluator's
        // result; this is where the substitution actually matters.
        var resultWithWinnerSection = evaluatorResultForReturn with
        {
            Section = winner.AgentResult.Section,
        };

        // Slot outcome derives from the WINNER's outcome, not the evaluator's.
        // Evaluator says COMPLETE = "I picked a winner"; the winner itself can
        // still be NEEDS_INFO from the candidate-agent's perspective. The user
        // chose: NEEDS_INFO from a winning slot propagates up — no fallback.
        return winner.AgentResult.Outcome switch
        {
            AgentOutcome.COMPLETE => new SlotResult(SlotOutcome.Won, resultWithWinnerSection),
            AgentOutcome.NEEDS_INFO => new SlotResult(SlotOutcome.NeedsInfo, winner.AgentResult),
            _ => new SlotResult(SlotOutcome.Failed, winner.AgentResult),
        };
    }

    /// <summary>
    /// Builds the slot infix used in step_result names and comment markers.
    /// Single-slot steps (legacy, or new but with one slot) get an empty
    /// infix for backward compatibility — existing JSON workflow configs and
    /// existing comment markers continue to deserialize/match. Multi-slot
    /// steps prefix every per-candidate row/marker with <c>:slot-N</c> so the
    /// step_result rows and audit comments stay disambiguated across the
    /// fallback chain.
    /// </summary>
    private static string SlotInfix(int slotIndex, int totalSlots)
        => totalSlots > 1 ? $":slot-{slotIndex}" : string.Empty;

    /// <summary>
    /// Default failure categories that are retried in-place when the candidate
    /// doesn't supply its own <see cref="CandidateOverride.RetryOn"/> list.
    /// Both are typically transient; the others (AGENT_ERROR, INFRASTRUCTURE)
    /// are usually permanent for the same provider+model and won't recover from
    /// a 30-second wait — fall back to the next slot instead.
    /// </summary>
    private static readonly FailureReason[] DefaultRetryOn =
        [FailureReason.RATE_LIMIT, FailureReason.TIMEOUT];

    /// <summary>
    /// Runs the candidate's executor with bounded retries on transient failures
    /// (RATE_LIMIT, TIMEOUT — configurable via <see cref="CandidateOverride.RetryOn"/>).
    /// Other exceptions short-circuit and are recorded as an ERROR result. The
    /// final result wins; intermediate failed attempts are not persisted as
    /// separate <c>step_result</c> rows (they're an internal attempt count).
    /// </summary>
    /// <remarks>
    /// Backoff: 30s base, 2× exponential, 5min cap, ±20% jitter — bounded to
    /// keep retries snappy enough that a card doesn't sit pending for hours.
    /// Cancellation is honored on the delay so Ctrl+C still interrupts cleanly.
    /// <para>
    /// Returns a tuple of <c>(AgentResult, bool RateLimited)</c>. When retries
    /// are exhausted on <see cref="RateLimitException"/> or
    /// <see cref="TimeoutException"/>, the exception is converted to an
    /// <see cref="AgentOutcome.ERROR"/> result with <c>RateLimited=true</c>
    /// rather than propagating. This lets sibling candidates in the same slot
    /// continue to run (a single bad provider no longer aborts the whole slot)
    /// and lets the slot's all-failed short-circuit produce a slot-level
    /// rate-limit signal that AgentRunner can use to fall through to the next
    /// slot in the fallback chain.
    /// </para>
    /// </remarks>
    private async Task<(AgentResult Result, bool RateLimited)> ExecuteCandidateWithRetriesAsync(
        IAgentExecutor executor,
        AgentExecutionContext context,
        CandidateOverride candidate,
        int slotIndex,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        var retryOn = candidate.RetryOn is { Count: > 0 } ? candidate.RetryOn : (IReadOnlyList<FailureReason>)DefaultRetryOn;
        var maxRetries = Math.Max(0, candidate.Retries);
        var attempt = 0;

        while (true)
        {
            try
            {
                // Each candidate runs as its own short-lived process. Do NOT attempt
                // session reuse — sessions assume a single canonical worktree mount.
                // Acquire any named resources this provider needs (e.g. local-llm
                // for docker-opencode + docker-claude-qwen sharing a single
                // llama.cpp server) so concurrent same-resource candidates from
                // different providers don't pile up on the same backend.
                AgentResult result;
                if (resourcePool is not null)
                {
                    await using var lease = await resourcePool.AcquireAsync(
                        candidate.Provider, cancellationToken);
                    result = await executor.ExecuteAsync(context, cancellationToken);
                }
                else
                {
                    result = await executor.ExecuteAsync(context, cancellationToken);
                }
                return (result, RateLimited: false);
            }
            catch (RateLimitException ex) when (
                attempt < maxRetries && retryOn.Contains(FailureReason.RATE_LIMIT))
            {
                attempt++;
                logger.LogWarning(ex,
                    "Slot {Slot} candidate {Index} ({Provider}) hit RATE_LIMIT; retry {Attempt}/{Max}",
                    slotIndex, candidateIndex, candidate.Provider, attempt, maxRetries);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (TimeoutException ex) when (
                attempt < maxRetries && retryOn.Contains(FailureReason.TIMEOUT))
            {
                attempt++;
                logger.LogWarning(ex,
                    "Slot {Slot} candidate {Index} ({Provider}) hit TIMEOUT; retry {Attempt}/{Max}",
                    slotIndex, candidateIndex, candidate.Provider, attempt, maxRetries);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (RateLimitException ex)
            {
                // Retries exhausted (or rate-limit not in RetryOn): convert to
                // an ERROR outcome with the rate-limit flag set instead of
                // propagating. Sibling candidates and sibling slots get a
                // chance; if every candidate at every level rate-limits,
                // AgentRunner re-raises a RateLimitException at the top.
                logger.LogWarning(ex,
                    "Slot {Slot} candidate {Index} ({Provider}) rate-limited after {Attempt} attempt(s); recording as ERROR outcome (will surface as slot-level rate-limit if all candidates fail)",
                    slotIndex, candidateIndex, candidate.Provider, attempt + 1);
                return (
                    new AgentResult(
                        AgentOutcome.ERROR,
                        $"[RATE_LIMIT after {attempt + 1} attempt(s)] {ex.Message}"),
                    RateLimited: true);
            }
            catch (TimeoutException ex)
            {
                // Convert to ERROR but do NOT set the rate-limit flag. Timeout
                // is transient enough to retry within the slot, but a chain of
                // timeouts is more likely a real problem (model too slow,
                // prompt too complex) than a capacity issue — surfacing it as
                // a plain ERROR via AgentResult is more honest than bubbling
                // RateLimitException at the top and triggering 30-minute
                // poller backoff.
                logger.LogWarning(ex,
                    "Slot {Slot} candidate {Index} ({Provider}) timed out after {Attempt} attempt(s); recording as ERROR outcome",
                    slotIndex, candidateIndex, candidate.Provider, attempt + 1);
                return (
                    new AgentResult(
                        AgentOutcome.ERROR,
                        $"[TIMEOUT after {attempt + 1} attempt(s)] {ex.Message}"),
                    RateLimited: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Slot {Slot} candidate {Index} ({Provider}) threw {ExceptionType}; recording as ERROR outcome",
                    slotIndex, candidateIndex, candidate.Provider, ex.GetType().Name);
                return (
                    new AgentResult(
                        AgentOutcome.ERROR,
                        $"Candidate execution threw {ex.GetType().Name}: {ex.Message}"),
                    RateLimited: false);
            }
            // OperationCanceledException is intentionally NOT caught here —
            // shutdown/Ctrl+C should propagate immediately, not be retried or
            // swallowed.
        }
    }

    /// <summary>
    /// Bounded exponential backoff between retries: 30s × 2^(attempt-1),
    /// capped at 5 minutes, with ±20% random jitter so multiple candidates
    /// retrying the same upstream don't synchronise their wait windows.
    /// </summary>
    private static async Task DelayWithBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        const double BaseSeconds = 30.0;
        const double CapSeconds = 300.0;
        var raw = Math.Min(CapSeconds, BaseSeconds * Math.Pow(2, attempt - 1));
        var jitterFactor = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2);  // [0.8, 1.2)
        var delay = TimeSpan.FromSeconds(raw * jitterFactor);
        await Task.Delay(delay, cancellationToken);
    }

    private Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
        => (retryDelay ?? DelayWithBackoffAsync)(attempt, cancellationToken);

    // ── Phase 1 helpers ──────────────────────────────────────────────────────

    private async Task<CandidateExecution> ExecuteSingleCandidateAsync(
        CandidateGroupRequest request,
        int slotIndex,
        int totalSlots,
        Guid groupId,
        string canonicalBranch,
        int index,
        CandidateOverride candidate,
        CancellationToken cancellationToken)
    {
        var step = request.Step;
        var providerSlug = SlugifyProvider(candidate.Provider);
        var candidateBranch = BuildCandidateBranchName(
            request.CardId, groupId, slotIndex, totalSlots, index, providerSlug);

        var executor = executorResolver.Resolve(candidate.Provider);

        // Resolve the model and provider params. Precedence:
        //   1. candidate.Model — explicit override always wins
        //   2. role.Model — applies ONLY when the candidate's provider matches
        //      the role's. Otherwise the role's default name (e.g. an Anthropic
        //      model on a senior_engineer role) would leak into a different
        //      provider's executor (e.g. Codex), which then errors with
        //      "the 'claude-opus-4-6' model is not supported when using Codex
        //      with a ChatGPT account." When the providers diverge, leave
        //      Model null so the executor falls back to its own default.
        var role = request.Role;
        var model = candidate.Model
            ?? (string.Equals(candidate.Provider, role.Provider, StringComparison.OrdinalIgnoreCase)
                ? role.Model
                : null);
        var providerParams = MergeProviderParams(request.StateProviderParams, candidate.ProviderParams);

        var startedAt = DateTimeOffset.UtcNow;

        // Create candidate worktree off canonical HEAD. New branch, fresh worktree.
        string candidateWorktreePath;
        try
        {
            candidateWorktreePath = await gitWorkspaceManager.CreateWorktreeFromStartPointAsync(
                request.RepoPath, candidateBranch, canonicalBranch, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to create candidate worktree for index {Index} (branch={Branch})",
                index, candidateBranch);
            return CandidateExecution.FailedSetup(
                index, candidate.Provider, model ?? "(provider default)", candidateBranch,
                startedAt, ex.Message);
        }

        // Copy ephemeral .aiboard files from canonical to candidate so the
        // executor sees the same task prompt + comments.
        try
        {
            CopyAiboardArtifactsToCandidate(request.WorktreePath, candidateWorktreePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to copy .aiboard artifacts to candidate {Index} — proceeding without them",
                index);
        }

        // Build the per-candidate execution context. The comments file path
        // must point to the CANDIDATE'S copy of the file (CopyAiboardArtifacts
        // copied .aiboard/tasks/ into the candidate worktree), not the
        // canonical worktree's copy. Without this rewrite, the prompt embeds
        // the canonical host path (e.g. C:\…\worktrees\aiboard\3-… on Windows)
        // which doesn't exist inside the candidate's container at all and
        // sends the agent into a doomed read-fail-glob-fail loop until the
        // step times out.
        var candidateCommentsFilePath = request.CommentsFilePath is null
            ? null
            : TaskFileManager.GetCommentsFilePath(
                candidateWorktreePath, request.CardId, request.CardTitle);

        var context = new AgentExecutionContext(
            TargetCardId: request.CardId,
            TargetCardTitle: request.CardTitle,
            WorkspacePath: candidateWorktreePath,
            TaskPrompt: request.TaskPrompt,
            SystemPromptFilePath: request.SystemPromptFilePath,
            Model: model,
            ProviderParams: providerParams,
            CommentsFilePath: candidateCommentsFilePath);

        // Mirror CLAUDE.md ↔ AGENTS.md so this candidate's provider has the
        // project init file regardless of which name the repo committed.
        // Cleanup must run BEFORE the orchestrator's git commit below — if the
        // mirror is left around, `git add .` captures it and the file shows up
        // in the evaluator's diff prompt as a spurious change, which biases
        // the verdict against this candidate.
        var initMirror = AgentInitFileResolver.EnsureInitFile(
            candidateWorktreePath, candidate.Provider, logger);

        AgentResult result;
        bool rateLimited;
        try
        {
            (result, rateLimited) = await ExecuteCandidateWithRetriesAsync(
                executor, context, candidate, slotIndex, index, cancellationToken);
        }
        finally
        {
            AgentInitFileResolver.CleanupInitFile(initMirror, logger);
        }

        var completedAt = DateTimeOffset.UtcNow;

        // Commit any uncommitted changes the candidate made. The promotion path
        // assumes commits — if the candidate ran with discard semantics, the
        // validator should have rejected this config.
        if (request.GitBehavior is "commit_only" or "commit_and_push")
        {
            try
            {
                if (await gitWorkspaceManager.HasUncommittedChangesAsync(
                        candidateWorktreePath, cancellationToken))
                {
                    await gitWorkspaceManager.CommitAsync(
                        candidateWorktreePath,
                        $"[candidate {index}/{candidate.Provider}] {step.Name} for card {request.CardId}",
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to commit candidate {Index} changes — promotion of this candidate will fail if it wins",
                    index);
            }
        }

        // Persist the candidate row immediately so the in-flight work is
        // recorded even if the evaluator step blows up later. step_result.model
        // is NOT NULL in the schema, so we use a sentinel when the candidate
        // ran with the executor's default (cross-provider candidate, no model
        // override).
        var modelForRecord = model ?? "(provider default)";
        var stepRecord = new StepResultRecord(
            RunId: request.RunId,
            CardId: request.CardId,
            StateName: request.StateName,
            StepName: $"{step.Name}{SlotInfix(slotIndex, totalSlots)}:cand-{index}:{providerSlug}",
            StepIndex: request.StepIndex,
            Role: step.Role,
            Model: modelForRecord,
            Outcome: result.Outcome,
            Summary: result.Detail,
            Detail: null,
            ReferenceContent: null,
            ConversationLog: result.ConversationLog,
            Questions: result.Questions,
            RequestedSteps: result.RequestedSteps,
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            SessionExecMs: null,
            Provider: candidate.Provider,
            CandidateGroupId: groupId,
            CandidateIndex: index,
            Selected: null,
            QualityScore: null,
            EvaluatorReasoning: null,
            SlotIndex: totalSlots > 1 ? slotIndex : null,
            // Usage / fast-path / structurer flow through to the per-candidate row
            // so v_provider_role_metrics can aggregate cost / tokens / structurer
            // rate by (role, provider). Each candidate hits its own provider, so
            // these are the most meaningful place to attribute consumption.
            CostUsd: result.Usage?.CostUsd,
            InputTokens: result.Usage?.InputTokens,
            OutputTokens: result.Usage?.OutputTokens,
            CacheReadTokens: result.Usage?.CacheReadTokens,
            CacheCreationTokens: result.Usage?.CacheCreationTokens,
            StructurerFallbackUsed: result.StructurerFallbackUsed,
            SectionUpdateJson: result.SectionUpdateJson);

        try
        {
            await runStore.SaveStepResultAsync(stepRecord, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to persist candidate {Index} step_result — continuing", index);
        }

        return new CandidateExecution(
            Index: index,
            Provider: candidate.Provider,
            Model: modelForRecord,
            BranchName: candidateBranch,
            WorktreePath: candidateWorktreePath,
            AgentResult: result,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            RateLimited: rateLimited);
    }

    private static IReadOnlyDictionary<string, string>? MergeProviderParams(
        IReadOnlyDictionary<string, string>? stateParams,
        IReadOnlyDictionary<string, string>? candidateParams)
    {
        if (candidateParams is null or { Count: 0 }) return stateParams;
        if (stateParams is null or { Count: 0 }) return candidateParams;

        var merged = new Dictionary<string, string>(stateParams, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in candidateParams)
            merged[k] = v;
        return merged;
    }

    private static string SlugifyProvider(string provider) =>
        provider.Replace(':', '-').Replace('/', '-').Replace('_', '-');

    /// <summary>
    /// Deterministic candidate branch-name builder. Used both at candidate-
    /// spawn time and pre-computed up-front so the slot-throw cleanup path
    /// can sweep candidates whose tasks were still in flight (or had faulted)
    /// when the slot's <c>Task.WhenAll</c> threw — those branches/worktrees
    /// aren't reachable through the returned <see cref="CandidateExecution"/>
    /// list because the task never produced one.
    /// </summary>
    /// <remarks>
    /// Format: <c>aiboard-cand/{cardId}-{groupShort}[-s{slotIndex}]-{index}-{providerSlug}</c>,
    /// lowercased. The slot infix is omitted in single-slot steps for
    /// backwards compatibility with v0.0.19-and-earlier branch names. The
    /// SEPARATE top-level <c>aiboard-cand/</c> prefix keeps candidate
    /// worktrees as siblings (not nested under) the canonical worktree —
    /// nested git worktrees fail.
    /// </remarks>
    internal static string BuildCandidateBranchName(
        string cardId, Guid groupId, int slotIndex, int totalSlots, int index, string providerSlug)
    {
        var groupShort = groupId.ToString("N")[..8];
        var slotBranchInfix = totalSlots > 1 ? $"-s{slotIndex}" : string.Empty;
        return $"aiboard-cand/{cardId}-{groupShort}{slotBranchInfix}-{index}-{providerSlug}".ToLowerInvariant();
    }

    private static void CopyAiboardArtifactsToCandidate(
        string canonicalWorktree, string candidateWorktree)
    {
        // Only copy what the candidate's executor will read: tasks/, comments/, images/.
        // updates/ is OUTPUT-only — the candidate writes its own.
        foreach (var subdir in new[] { "tasks", "comments", "images" })
        {
            var src = Path.Combine(canonicalWorktree, ".aiboard", subdir);
            var dst = Path.Combine(candidateWorktree, ".aiboard", subdir);
            if (Directory.Exists(src))
                CopyDirectoryRecursive(src, dst);
        }
    }

    /// <summary>True for the <c>discard</c> git behaviour (case-insensitive).</summary>
    internal static bool IsDiscardMode(string? gitBehavior) =>
        string.Equals(gitBehavior, "discard", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// File-based winner promotion for <c>discard</c>-mode states: copies the
    /// winner's <c>.aiboard/tasks/</c> and <c>.aiboard/updates/</c> contents
    /// onto the canonical worktree so AgentRunner's post-step processors read
    /// the winner's outputs the same way they would for a single-agent step.
    /// </summary>
    /// <remarks>
    /// Why these two subdirs only:
    /// <list type="bullet">
    ///   <item><c>tasks/</c> holds the card body the agent may have edited
    ///         (e.g. design steps write the design into the task body via
    ///         markdown sections).</item>
    ///   <item><c>updates/</c> holds <c>new-*.md</c> child-card requests the
    ///         agent wrote (consumed by <c>UpdateFileProcessor</c>).</item>
    /// </list>
    /// <c>comments/</c> and <c>images/</c> are inputs (refreshed from the board
    /// before each run) — overwriting them would just clobber the canonical
    /// copies with stale candidate copies. <c>commit.md</c> is irrelevant in
    /// discard mode (no commit happens).
    /// </remarks>
    internal static void PromoteDiscardWinnerArtifacts(
        string winnerWorktree, string canonicalWorktree)
    {
        foreach (var subdir in new[] { "tasks", "updates" })
        {
            var src = Path.Combine(winnerWorktree, ".aiboard", subdir);
            if (!Directory.Exists(src)) continue;

            var dst = Path.Combine(canonicalWorktree, ".aiboard", subdir);
            // Mirror semantics: clear the canonical copy first so a winner that
            // *removed* a file (e.g. dropped a child card) is reflected. Without
            // this, a stale file from a previous run could survive a winner that
            // didn't write it.
            if (Directory.Exists(dst))
            {
                foreach (var existing in Directory.EnumerateFiles(dst))
                {
                    try { File.Delete(existing); }
                    catch { /* best-effort */ }
                }
            }
            CopyDirectoryRecursive(src, dst);
        }
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true); }
            catch { /* best-effort */ }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Appends a discard-mode candidate's actual outputs (task body + any
    /// updates files) to the evaluator prompt. Used in place of the git-diff
    /// block when the state is discard-mode and there are no commits to diff.
    /// </summary>
    private static void AppendDiscardCandidateOutputs(
        StringBuilder sb, string candidateWorktree, string cardId, string? cardTitle)
    {
        const int TaskBodyCap = 8_000;       // headroom for design docs without blowing the prompt
        const int UpdateFileCap = 2_000;     // each new-*.md is small, but cap to be safe
        const int MaxUpdateFiles = 20;

        // The orchestrator writes the task file via TaskFileManager, which slugs
        // the title into the filename ({cardId}-{slug}.md). Building a plain
        // {cardId}.md path here would silently miss the file for any card with
        // a non-empty title and force the evaluator to score on candidates'
        // self-reported claims rather than actual outputs.
        var fileName = TaskFileManager.GetTaskFileName(cardId, cardTitle);
        var taskFile = Path.Combine(candidateWorktree, ".aiboard", "tasks", fileName);
        if (File.Exists(taskFile))
        {
            sb.AppendLine($"**Card body (`.aiboard/tasks/{fileName}`):**");
            sb.AppendLine();
            sb.AppendLine("```markdown");
            try { sb.AppendLine(Truncate(File.ReadAllText(taskFile), TaskBodyCap)); }
            catch (Exception ex) { sb.AppendLine($"(unreadable: {ex.Message})"); }
            sb.AppendLine("```");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("_(candidate did not write a card body)_");
            sb.AppendLine();
        }

        var updatesDir = Path.Combine(candidateWorktree, ".aiboard", "updates");
        if (Directory.Exists(updatesDir))
        {
            var files = Directory.EnumerateFiles(updatesDir, "*.md")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Take(MaxUpdateFiles)
                .ToList();

            if (files.Count > 0)
            {
                sb.AppendLine($"**Updates files ({files.Count}):**");
                sb.AppendLine();
                foreach (var f in files)
                {
                    sb.AppendLine($"- `{Path.GetFileName(f)}`:");
                    sb.AppendLine("  ```markdown");
                    try
                    {
                        var content = File.ReadAllText(f);
                        foreach (var line in Truncate(content, UpdateFileCap).Split('\n'))
                            sb.AppendLine("  " + line.TrimEnd('\r'));
                    }
                    catch (Exception ex) { sb.AppendLine($"  (unreadable: {ex.Message})"); }
                    sb.AppendLine("  ```");
                }
                sb.AppendLine();
            }
        }
    }

    private static string BuildAllFailedDetail(IReadOnlyList<CandidateExecution> executions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("All candidates failed; halted before running evaluator.");
        sb.AppendLine();
        for (var i = 0; i < executions.Count; i++)
        {
            var e = executions[i];
            sb.AppendLine($"- **Candidate {i}** (`{e.Provider}` / `{e.Model}`): {e.AgentResult.Outcome}");
            if (!string.IsNullOrWhiteSpace(e.AgentResult.Detail))
                sb.AppendLine($"  > {Truncate(e.AgentResult.Detail, 300)}");
        }
        return sb.ToString();
    }

    // ── Phase 2: evaluator invocation ────────────────────────────────────────

    private async Task<(AgentResult Result, string? TempPromptPathToDelete)> RunEvaluatorAsync(
        CandidateGroupRequest request,
        EvaluatorConfig evaluatorCfg,
        int slotIndex,
        int totalSlots,
        IReadOnlyList<CandidateExecution> executions,
        Guid groupId,
        string? slotStartCanonicalSha,
        CancellationToken cancellationToken)
    {
        var evaluatorRole = request.WorkflowRoles[evaluatorCfg.Role];
        var evaluatorExecutor = executorResolver.Resolve(evaluatorRole.Provider);

        var evaluatorTaskPrompt = await BuildEvaluatorTaskPromptAsync(
            request, evaluatorCfg, executions, slotStartCanonicalSha, cancellationToken);

        var (evaluatorSystemPromptPath, tempPromptPath) = await ResolveEvaluatorSystemPromptAsync(
            request, evaluatorRole, evaluatorCfg, cancellationToken);

        var startedAt = DateTimeOffset.UtcNow;

        // The evaluator runs against the canonical worktree (it doesn't write
        // code — it reads each candidate's diff via the embedded prompt and
        // returns a structured verdict).
        //
        // SchemaOverride: pin the response shape to the evaluator-specific
        // schema so winner_index is required when outcome=COMPLETE. Without
        // this, the LLM can pick a winner in prose but omit the structured
        // field, and the orchestrator silently cleans up all candidates with
        // no winner promoted (KvA card #3 v0.0.15 reproduction). The variant
        // matches the evaluator's provider — codex needs the OpenAI shape;
        // everything else uses the JSON-Schema-2020-12 if/then form.
        var schemaOverride = string.Equals(evaluatorRole.Provider, "codex", StringComparison.OrdinalIgnoreCase)
            ? AgentSchemas.EvaluatorOutcomeSchemaOpenAI
            : AgentSchemas.EvaluatorOutcomeSchema;

        var context = new AgentExecutionContext(
            TargetCardId: request.CardId,
            TargetCardTitle: request.CardTitle,
            WorkspacePath: request.WorktreePath,
            TaskPrompt: evaluatorTaskPrompt,
            SystemPromptFilePath: evaluatorSystemPromptPath,
            Model: evaluatorRole.Model,
            ProviderParams: request.StateProviderParams,
            CommentsFilePath: request.CommentsFilePath,
            SchemaOverride: schemaOverride);

        // Mirror CLAUDE.md ↔ AGENTS.md so the evaluator's provider has the
        // project init file regardless of which name the repo committed.
        // Cleanup runs in the finally so the mirror doesn't survive into
        // post-evaluator-step git operations or get re-discovered by a
        // subsequent step in the same run.
        var initMirror = AgentInitFileResolver.EnsureInitFile(
            request.WorktreePath, evaluatorRole.Provider, logger);

        AgentResult evaluatorResult;
        try
        {
            // Acquire named resources for the evaluator's provider, same
            // pattern as candidate execution.
            if (resourcePool is not null)
            {
                await using var lease = await resourcePool.AcquireAsync(
                    evaluatorRole.Provider, cancellationToken);
                evaluatorResult = await evaluatorExecutor.ExecuteAsync(context, cancellationToken);
            }
            else
            {
                evaluatorResult = await evaluatorExecutor.ExecuteAsync(context, cancellationToken);
            }
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException
            && ex is not RateLimitException)
        {
            logger.LogError(ex, "Evaluator threw for step '{StepName}'", request.Step.Name);
            evaluatorResult = new AgentResult(
                AgentOutcome.ERROR,
                $"Evaluator threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            AgentInitFileResolver.CleanupInitFile(initMirror, logger);
        }

        var completedAt = DateTimeOffset.UtcNow;

        // Persist evaluator step. Note: candidate_group_id stays NULL on the
        // evaluator row — it's a regular step that follows the group. The
        // step_name suffix `:evaluator` (with `:slot-N` infix when multi-slot)
        // lets callers correlate by name.
        var evaluatorRecord = new StepResultRecord(
            RunId: request.RunId,
            CardId: request.CardId,
            StateName: request.StateName,
            StepName: $"{request.Step.Name}{SlotInfix(slotIndex, totalSlots)}:evaluator",
            StepIndex: request.StepIndex,
            Role: evaluatorCfg.Role,
            Model: evaluatorRole.Model,
            Outcome: evaluatorResult.Outcome,
            Summary: evaluatorResult.Detail,
            Detail: null,
            ReferenceContent: null,
            ConversationLog: evaluatorResult.ConversationLog,
            Questions: evaluatorResult.Questions,
            RequestedSteps: evaluatorResult.RequestedSteps,
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            SessionExecMs: null,
            Provider: evaluatorRole.Provider,
            SlotIndex: totalSlots > 1 ? slotIndex : null,
            CostUsd: evaluatorResult.Usage?.CostUsd,
            InputTokens: evaluatorResult.Usage?.InputTokens,
            OutputTokens: evaluatorResult.Usage?.OutputTokens,
            CacheReadTokens: evaluatorResult.Usage?.CacheReadTokens,
            CacheCreationTokens: evaluatorResult.Usage?.CacheCreationTokens,
            // Evaluator prompt size: useful to spot when growing diffs / candidate
            // counts push the evaluator toward context-truncation territory before
            // the verdict silently degrades. evaluatorTaskPrompt is the entire
            // prompt body (rubric + per-candidate diffs / artifacts).
            EvaluatorPromptChars: evaluatorTaskPrompt.Length,
            // Persist the evaluator's own section_update directive (typically
            // strategy=leave) for replay/debugging. The orchestrator substitutes
            // the winner's section into the propagated AgentResult; this column
            // captures what the evaluator actually emitted.
            SectionUpdateJson: evaluatorResult.SectionUpdateJson);

        try
        {
            await runStore.SaveStepResultAsync(evaluatorRecord, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist evaluator step_result for step '{StepName}'",
                request.Step.Name);
        }

        return (evaluatorResult, tempPromptPath);
    }

    private async Task<string> BuildEvaluatorTaskPromptAsync(
        CandidateGroupRequest request,
        EvaluatorConfig evaluatorCfg,
        IReadOnlyList<CandidateExecution> executions,
        string? slotStartCanonicalSha,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        // Task prompt template: configured in EvaluatorConfig, with a sane
        // built-in fallback so users don't have to supply one for the v1 trial.
        var promptTemplate = await ResolveEvaluatorPromptTemplateAsync(
            request, evaluatorCfg, cancellationToken);
        sb.AppendLine(promptTemplate);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Original Step Task");
        sb.AppendLine();
        sb.AppendLine(request.TaskPrompt);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Candidate Outputs");
        sb.AppendLine();

        for (var i = 0; i < executions.Count; i++)
        {
            var e = executions[i];
            sb.AppendLine($"### Candidate {i} — provider `{e.Provider}`, model `{e.Model}`");
            sb.AppendLine();
            sb.AppendLine($"**Outcome:** `{e.AgentResult.Outcome}`");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(e.AgentResult.Detail))
            {
                sb.AppendLine("**Detail:**");
                sb.AppendLine();
                sb.AppendLine(Truncate(e.AgentResult.Detail, 4000));
                sb.AppendLine();
            }

            // For commit-mode states the candidate's actual output is its git diff
            // against the canonical branch. For discard-mode states (design,
            // tasking, etc.) nothing is committed — the output is whatever the
            // agent wrote into .aiboard/tasks/{cardId}.md (the card body) and
            // .aiboard/updates/*.md (child-card requests). Show whichever is
            // appropriate so the evaluator has real material to compare on.
            if (IsDiscardMode(request.GitBehavior))
            {
                AppendDiscardCandidateOutputs(sb, e.WorktreePath, request.CardId, request.CardTitle);
            }
            else
            {
                string diff;
                try
                {
                    // Diff against the canonical SHA captured before candidates
                    // were spawned. Necessary because the orchestrator already
                    // committed each candidate's work onto its own branch by
                    // the time we get here, so `git diff HEAD` in the candidate
                    // worktree would be empty. Falls back to HEAD if the SHA
                    // capture failed (logged at slot start).
                    diff = await gitWorkspaceManager.GetDiffSummaryAsync(
                        e.WorktreePath, maxChars: 10_000, cancellationToken,
                        baseRef: slotStartCanonicalSha);
                }
                catch (Exception ex)
                {
                    diff = $"(diff unavailable: {ex.Message})";
                }

                sb.AppendLine("**Diff:**");
                sb.AppendLine();
                sb.AppendLine("```diff");
                sb.AppendLine(diff);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Response Contract");
        sb.AppendLine();
        sb.AppendLine(
            "Return JSON conforming to the standard Agent Contract `outcome` schema, " +
            "extended with the candidate-evaluation fields described below.");
        sb.AppendLine();
        sb.AppendLine("Required fields:");
        sb.AppendLine("- `outcome`: `COMPLETE` if you can pick a winner, `NEEDS_INFO` if you need more, `ERROR` if all candidates are unacceptable.");
        sb.AppendLine("- `detail`: GitHub-flavored markdown summary that gets posted as the step comment.");
        sb.AppendLine();
        sb.AppendLine("When `outcome = COMPLETE`, you **MUST** include:");
        sb.AppendLine("- `winner_index`: integer (0-indexed) selecting the best candidate.");
        sb.AppendLine("  The schema enforces this — a COMPLETE response without `winner_index` is rejected as a schema violation, the run is marked ERROR, and no winner is promoted. Picking the winner in prose only is not enough; the structured field is the only signal the orchestrator reads.");

        if (evaluatorCfg.Scoring == EvaluatorScoring.WinnerWithScores)
        {
            sb.AppendLine("- `scores`: array with one object per candidate:");
            sb.AppendLine("    - `index`: integer (must match the candidate's position)");
            sb.AppendLine("    - `score`: number 0–10 (calibrated; 10 = production-ready, 5 = mostly works with caveats, 0 = unusable)");
            sb.AppendLine("    - `reasoning`: short string explaining the score");
        }

        sb.AppendLine();
        sb.AppendLine("Be calibrated. If the candidates are very close, reflect that in their scores; if one clearly dominates, reflect that too.");

        return sb.ToString();
    }

    private async Task<string> ResolveEvaluatorPromptTemplateAsync(
        CandidateGroupRequest request,
        EvaluatorConfig evaluatorCfg,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(evaluatorCfg.TaskPromptFile))
        {
            var path = ResolveRelativePath(evaluatorCfg.TaskPromptFile, request.PromptBaseDirectory);
            if (File.Exists(path))
                return await File.ReadAllTextAsync(path, cancellationToken);
            logger.LogWarning(
                "Evaluator taskPromptFile '{Path}' not found; falling back to inline prompt or default",
                path);
        }

        if (!string.IsNullOrWhiteSpace(evaluatorCfg.TaskPrompt))
            return evaluatorCfg.TaskPrompt;

        // Built-in default for the v1 trial. Operators can override with a file.
        return
            "# Candidate Evaluation\n\n" +
            "You are comparing N candidate implementations of the same task. Read the original task, " +
            "each candidate's output, and each candidate's diff against the canonical branch. Pick the " +
            "winner based on correctness, code quality, and adherence to the task. Score each 0–10. " +
            "Failed candidates (outcome != COMPLETE) cannot win — assign them a low score with reasoning " +
            "and pick from the rest.";
    }

    /// <summary>
    /// Resolves the evaluator's system prompt to a file path for the executor.
    /// Returns (path, tempPathToDelete) — tempPathToDelete is non-null only when
    /// we materialised an inline prompt to disk; the caller deletes it after
    /// the evaluator run completes so /tmp doesn't accumulate stale files.
    /// </summary>
    private async Task<(string Path, string? TempPathToDelete)> ResolveEvaluatorSystemPromptAsync(
        CandidateGroupRequest request,
        WorkflowRole evaluatorRole,
        EvaluatorConfig evaluatorCfg,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(evaluatorRole.SystemPromptFile))
        {
            var path = ResolveRelativePath(evaluatorRole.SystemPromptFile, request.PromptBaseDirectory);
            if (File.Exists(path))
                return (path, null);
        }

        // Inline system prompt: write to a temp file the executor can mount,
        // and surface the path so the caller can clean it up later.
        var inline = !string.IsNullOrWhiteSpace(evaluatorRole.SystemPrompt)
            ? evaluatorRole.SystemPrompt
            : DefaultEvaluatorSystemPrompt;

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"aiboard-evaluator-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(tmp, inline, cancellationToken);
        return (tmp, tmp);
    }

    private const string DefaultEvaluatorSystemPrompt =
        "You are an evaluator agent. You compare multiple candidate implementations of the same task " +
        "and pick the best one, with calibrated 0–10 scores. Be terse, specific, and honest about " +
        "tradeoffs. Always return valid JSON matching the contract in the task prompt.";

    private static string ResolveRelativePath(string relativeOrAbsolute, string? baseDir)
    {
        if (Path.IsPathRooted(relativeOrAbsolute)) return relativeOrAbsolute;
        if (string.IsNullOrEmpty(baseDir)) return relativeOrAbsolute;
        return Path.GetFullPath(Path.Combine(baseDir, relativeOrAbsolute));
    }

    // ── Phase 3: parse evaluator verdict ─────────────────────────────────────

    /// <summary>
    /// Extracts the candidate-specific fields (<c>winner_index</c>, <c>scores</c>)
    /// from the evaluator's structured response. Three layers of extraction in
    /// priority order:
    ///
    /// <list type="number">
    ///   <item><b>Structured-output fields</b> (<see cref="AgentResult.WinnerIndex"/>,
    ///         <see cref="AgentResult.Scores"/>) — populated by
    ///         <see cref="AgentOutputParser"/> when the model filled the
    ///         schema-required fields. This is the primary path; fastest,
    ///         most accurate, no markdown parsing needed.</item>
    ///   <item><b>Detail-embedded JSON</b> — search the detail markdown for a
    ///         fenced or top-level <c>{...}</c> block carrying
    ///         <c>winner_index</c> / <c>scores</c>. Backstop for when the model
    ///         skipped the schema field but included a redundant JSON in prose.</item>
    ///   <item><b>Prose patterns in detail</b> — match "Candidate N wins",
    ///         "Winner: Candidate N", or scoreboard table rows marked
    ///         "**Winner.**" / "Winner.". Last-resort fallback for the v0.0.16/17
    ///         field-report shape: clear verdict in markdown, no structured
    ///         field, no JSON block. The cost is one regex pass; the benefit
    ///         is recovering verdicts we'd otherwise lose to ERROR routing.</item>
    /// </list>
    ///
    /// Returns a verdict with sensible defaults when no layer found a winner so
    /// the caller doesn't have to special-case non-COMPLETE outcomes.
    /// </summary>
    internal static EvaluatorVerdict ParseEvaluatorVerdict(
        AgentResult evaluatorResult, int candidateCount, EvaluatorScoring scoring)
    {
        if (evaluatorResult.Outcome != AgentOutcome.COMPLETE)
            return new EvaluatorVerdict(WinnerIndex: null, Scores: Array.Empty<CandidateScore>());

        // Layer 1: structured fields. When the model fills them, use them
        // directly — the schema-validated source is authoritative.
        var winner = evaluatorResult.WinnerIndex;

        // Layer 2: detail-embedded JSON. Backstop when the model skipped the
        // schema field but included a redundant JSON copy in prose.
        if (winner is null)
            winner = TryExtractWinnerIndex(evaluatorResult.Detail);

        // Layer 3: prose patterns. Last-resort recovery for verdicts written
        // entirely in markdown ("Candidate 1 wins", scoreboard with
        // "**Winner.**"). Without this, every Claude-evaluator run that
        // skipped the structured field AND wrote no JSON block routes to ERROR
        // even though the verdict is unambiguous in plain text.
        if (winner is null)
            winner = TryExtractWinnerFromProse(evaluatorResult.Detail, candidateCount);

        // Bounds-check whichever layer produced the winner. Out-of-range = no
        // winner (treated as "malformed; do not promote").
        if (winner is int idx && (idx < 0 || idx >= candidateCount))
            winner = null;

        // Scores: prefer structured, fall back to detail-embedded JSON. No
        // prose fallback for scores — they're calibration data, and a
        // markdown-table parser would be brittle.
        IReadOnlyList<CandidateScore> scores;
        if (scoring != EvaluatorScoring.WinnerWithScores)
        {
            scores = Array.Empty<CandidateScore>();
        }
        else if (evaluatorResult.Scores is { Count: > 0 } structuredScores)
        {
            // Translate the parser's EvaluatorScore (a flat record) into the
            // CandidateScore type the rest of CandidateExecutor uses.
            scores = structuredScores
                .Where(s => s.Index >= 0 && s.Index < candidateCount)
                .Select(s => new CandidateScore(s.Index, s.Score, s.Reasoning))
                .OrderBy(s => s.Index)
                .ToList();
        }
        else
        {
            scores = TryExtractScores(evaluatorResult.Detail, candidateCount);
        }

        return new EvaluatorVerdict(winner, scores);
    }

    /// <summary>
    /// Last-resort verdict extraction from plain markdown prose. Matches the
    /// patterns evaluators commonly emit when they wrote a clear verdict but
    /// skipped the structured <c>winner_index</c> field:
    ///
    /// <list type="bullet">
    ///   <item><c>Verdict: Candidate N wins</c> / <c>**Verdict: Candidate N wins**</c></item>
    ///   <item><c>Winner: Candidate N</c></item>
    ///   <item><c>Candidate N wins</c></item>
    ///   <item>Scoreboard table row containing <c>**Winner.**</c> or <c>Winner.</c>
    ///         in the notes column, with the candidate index in the first column.</item>
    /// </list>
    ///
    /// Returns null when no pattern matches. Bounds-checked by the caller.
    /// </summary>
    internal static int? TryExtractWinnerFromProse(string? detail, int candidateCount)
    {
        if (string.IsNullOrEmpty(detail)) return null;

        // Pattern 1: "Candidate N wins" / "Verdict: Candidate N wins" /
        // "Winner: Candidate N". Case-insensitive; allows the index to be
        // followed by space, punctuation, or end-of-line.
        var directMatch = System.Text.RegularExpressions.Regex.Match(
            detail,
            @"(?:Verdict\s*:?\s*)?(?:Winner\s*:?\s*)?Candidate\s+(\d+)\s*(?:wins\b|is\s+the\s+winner\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (directMatch.Success
            && int.TryParse(directMatch.Groups[1].Value, out var directIdx)
            && directIdx >= 0 && directIdx < candidateCount)
        {
            return directIdx;
        }

        // Pattern 2: "Winner: Candidate N" without "wins" / "is the winner".
        var winnerColonMatch = System.Text.RegularExpressions.Regex.Match(
            detail,
            @"\bWinner\s*:\s*Candidate\s+(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (winnerColonMatch.Success
            && int.TryParse(winnerColonMatch.Groups[1].Value, out var winnerIdx)
            && winnerIdx >= 0 && winnerIdx < candidateCount)
        {
            return winnerIdx;
        }

        // Pattern 3: scoreboard table row marked "**Winner.**" or "Winner.".
        // Matches lines like "| 1 | claude-sonnet-4-6 | **7** | **Winner.** ..."
        // The first cell after "|" is the candidate index. Match by line so we
        // only get rows containing the Winner marker.
        foreach (var line in detail.Split('\n'))
        {
            if (!line.Contains("Winner", StringComparison.OrdinalIgnoreCase)) continue;

            // First non-empty cell of a markdown table row should be the index.
            // Match: optional pipe, optional whitespace, capture digits.
            var rowMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"^\s*\|?\s*(\d+)\s*\|");
            if (!rowMatch.Success) continue;
            if (!int.TryParse(rowMatch.Groups[1].Value, out var rowIdx)) continue;
            if (rowIdx < 0 || rowIdx >= candidateCount) continue;

            // Confirm "Winner" appears in a context that means "this row is the
            // winner" — surface forms like "**Winner.**", "Winner.",
            // "**Winner:**", "Winner:". Reject "Winner candidate is X" framings
            // by requiring punctuation after Winner.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                line,
                @"\bWinner\s*[.:]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return rowIdx;
            }
        }

        return null;
    }

    private static int? TryExtractWinnerIndex(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        foreach (var json in EnumerateJsonObjects(detail))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("winner_index", out var wi)
                    && wi.ValueKind == JsonValueKind.Number
                    && wi.TryGetInt32(out var idx))
                    return idx;
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>
    /// Maximum allowed value for an evaluator quality score. Scores outside
    /// the [0, MaxScore] range are clamped to null at parse time so they
    /// don't pollute <c>v_provider_role_metrics.avg_quality_score</c>.
    /// </summary>
    private const decimal MaxScore = 10m;

    private static IReadOnlyList<CandidateScore> TryExtractScores(string? detail, int candidateCount)
    {
        if (string.IsNullOrEmpty(detail))
            return Array.Empty<CandidateScore>();

        foreach (var json in EnumerateJsonObjects(detail))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("scores", out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                    continue;

                // Dedupe by index: if the evaluator emits two entries with the
                // same index (revision after second thought, or simple error),
                // keep the LAST occurrence so "the evaluator's final answer"
                // wins. Map preserves insertion order for first-occurrence
                // index semantics in the returned list.
                var byIndex = new Dictionary<int, CandidateScore>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var idx = item.TryGetProperty("index", out var iEl) && iEl.TryGetInt32(out var i)
                        ? i : -1;
                    if (idx < 0 || idx >= candidateCount) continue;

                    decimal? score = null;
                    if (item.TryGetProperty("score", out var sEl)
                        && sEl.ValueKind == JsonValueKind.Number
                        && sEl.TryGetDecimal(out var rawScore))
                    {
                        // Drop scores outside [0, 10] rather than persisting
                        // garbage that would skew avg_quality_score in metrics.
                        // The reasoning field is kept either way — the absence
                        // of a score is itself useful data.
                        if (rawScore >= 0m && rawScore <= MaxScore)
                            score = rawScore;
                    }

                    var reasoning = item.TryGetProperty("reasoning", out var rEl)
                        ? rEl.GetString() : null;

                    byIndex[idx] = new CandidateScore(idx, score, reasoning);
                }
                if (byIndex.Count > 0) return byIndex.Values.OrderBy(s => s.Index).ToList();
            }
            catch (JsonException) { }
        }
        return Array.Empty<CandidateScore>();
    }

    private static IEnumerable<string> EnumerateJsonObjects(string text)
    {
        // Look for fenced JSON blocks first, then fall back to any balanced
        // top-level {...} region. Mirrors OpenCodeOutputParser's approach.
        var fenced = ExtractFencedJson(text);
        if (fenced is not null) yield return fenced;

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{') { i++; continue; }
            var balanced = TryReadBalancedObject(text, i);
            if (balanced is null) { i++; continue; }
            yield return balanced;
            i += balanced.Length;
        }
    }

    private static string? ExtractFencedJson(string text)
    {
        var idx = text.IndexOf("```", StringComparison.Ordinal);
        while (idx >= 0)
        {
            var afterFence = idx + 3;
            var eol = text.IndexOf('\n', afterFence);
            if (eol < 0) return null;
            var bodyStart = eol + 1;
            var closeIdx = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (closeIdx < 0) return null;
            var body = text[bodyStart..closeIdx].Trim();
            if (body.StartsWith('{')) return body;
            idx = text.IndexOf("```", closeIdx + 3, StringComparison.Ordinal);
        }
        return null;
    }

    private static string? TryReadBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    // ── Phase 3 persistence + Phase 4/5 cleanup + comments ──────────────────

    private async Task PersistEvaluatorVerdictAsync(
        string runId,
        Guid groupId,
        IReadOnlyList<CandidateExecution> executions,
        EvaluatorVerdict verdict,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < executions.Count; i++)
        {
            var selected = verdict.WinnerIndex is int w && w == i;
            var score = verdict.Scores.FirstOrDefault(s => s.Index == i);

            try
            {
                await runStore.UpdateCandidateEvaluationAsync(
                    runId, groupId, i,
                    selected: selected,
                    qualityScore: score?.Score,
                    evaluatorReasoning: score?.Reasoning,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to persist evaluator verdict for candidate {Index} (group {Group})",
                    i, groupId);
            }
        }
    }

    /// <summary>
    /// Tears down every candidate's worktree AND branch — including the winner
    /// after promotion. Best-effort: <see cref="GitWorkspaceManager.RemoveWorktreeAsync"/>
    /// is itself non-throwing (logs Warning on each failed sub-step), and we
    /// additionally wrap each candidate so a single bad teardown cannot stop
    /// the loop from completing the remaining ones.
    /// </summary>
    /// <remarks>
    /// In commit modes the winner's commits have already been promoted to the
    /// canonical worktree via <c>git reset --hard</c>; the candidate branch
    /// pointer is therefore redundant (it points at the same commits canonical
    /// now references). Preserving it would just accumulate
    /// <c>aiboard-cand/...</c> branches in the operator's repo across runs —
    /// hundreds over time on busy boards. In discard modes the winner's branch
    /// is doubly redundant (file-based promotion didn't even use it).
    /// </remarks>
    private async Task CleanupAllCandidateWorktreesAsync(
        string repoPath,
        IEnumerable<(int Index, string BranchName)> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var (index, branchName) in candidates)
        {
            try
            {
                await gitWorkspaceManager.RemoveWorktreeAsync(
                    repoPath, branchName, deleteBranch: true, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // RemoveWorktreeAsync is itself non-throwing for non-cancellation
                // failures (it logs internally), so reaching this branch indicates
                // an unexpected exception class. Still non-fatal.
                logger.LogWarning(ex,
                    "Unexpected exception removing candidate {Index} (branch={Branch}) during cleanup; continuing with the rest",
                    index, branchName);
            }
        }
    }

    private Task CleanupCandidateWorktreesAsync(
        string repoPath,
        IReadOnlyList<CandidateExecution> executions,
        CancellationToken cancellationToken) =>
        CleanupAllCandidateWorktreesAsync(
            repoPath,
            executions.Select(e => (e.Index, e.BranchName)),
            cancellationToken);

    private async Task PostCandidateCommentsAsync(
        CandidateGroupRequest request,
        string stepName,
        int slotIndex,
        int totalSlots,
        IReadOnlyList<CandidateExecution> executions,
        AgentResult evaluatorResult,
        EvaluatorVerdict verdict,
        CancellationToken cancellationToken)
    {
        // Per-candidate audit comments — make individual outputs visible without
        // bloating the consolidated step comment. Multi-slot steps surface the
        // slot via the `slot:` field in the aiboard-log marker.

        // Resolve the (card, state, step) attempt count once per slot so all
        // candidate comments in this slot agree on the attempt field. This is
        // resolved AFTER the per-candidate step_result rows are persisted, so
        // the count includes them — but we want the canonical attempt
        // (representing this run's slot try). The candidate rows have non-null
        // candidate_index ≠ 0, so they're EXCLUDED from the COUNT predicate
        // (`candidate_index IS NULL OR candidate_index = 0`), and the count
        // reflects only prior canonical runs. +1 for the current run.
        int slotAttempt = 1;
        try
        {
            var prior = await runStore.GetStepAttemptCountAsync(
                request.CardId, request.StateName, request.Step.Name, cancellationToken);
            slotAttempt = prior + 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve attempt count for candidate comments on card {Card}; defaulting to 1",
                request.CardId);
        }

        for (var i = 0; i < executions.Count; i++)
        {
            var e = executions[i];
            var won = verdict.WinnerIndex is int w && w == i;
            var sb = new StringBuilder();
            var candidateAgentName = agentIdentity is not null
                ? $" ({agentIdentity.FormatAgentName(e.Provider, e.Model)})"
                : "";
            sb.AppendLine($"**Candidate {i}** — provider `{e.Provider}`, model `{e.Model}`{candidateAgentName} {(won ? "🏆" : "")}".TrimEnd());
            sb.AppendLine();
            sb.AppendLine($"Outcome: `{e.AgentResult.Outcome}`");

            var score = verdict.Scores.FirstOrDefault(s => s.Index == i);
            if (score is not null && score.Score.HasValue)
            {
                sb.AppendLine($"Score: **{score.Score.Value.ToString("F1", CultureInfo.InvariantCulture)} / 10**");
                if (!string.IsNullOrWhiteSpace(score.Reasoning))
                {
                    sb.AppendLine();
                    sb.AppendLine($"> {score.Reasoning}");
                }
            }
            if (!string.IsNullOrWhiteSpace(e.AgentResult.Detail))
            {
                sb.AppendLine();
                sb.AppendLine("<details><summary>Candidate detail</summary>");
                sb.AppendLine();
                sb.AppendLine(Truncate(e.AgentResult.Detail, 3000));
                sb.AppendLine();
                sb.AppendLine("</details>");
            }

            try
            {
                // kind:candidate → append (per-candidate chronological log).
                // Attempt field included per "every aiboard-log emit site"
                // contract. No legacy upsert fallback — when no router is
                // wired, we still append the aiboard-log marker directly via
                // boardClient (test-only path; production registers the
                // router).
                var fields = new List<KeyValuePair<string, string>>
                {
                    KeyValuePair.Create("state", request.StateName),
                    KeyValuePair.Create("step", stepName),
                };
                if (totalSlots > 1)
                    fields.Add(KeyValuePair.Create("slot",
                        slotIndex.ToString(CultureInfo.InvariantCulture)));
                fields.Add(KeyValuePair.Create("candidate",
                    i.ToString(CultureInfo.InvariantCulture)));
                fields.Add(KeyValuePair.Create("provider", e.Provider));
                fields.Add(KeyValuePair.Create("run", request.RunId));
                fields.Add(KeyValuePair.Create("outcome", e.AgentResult.Outcome.ToString()));
                fields.Add(KeyValuePair.Create("attempt",
                    slotAttempt.ToString(CultureInfo.InvariantCulture)));

                var newMarker = AiboardLogMarker.Build(
                    AiboardLogMarker.KindCandidate, fields);
                if (commentRouter is not null)
                {
                    await commentRouter.PostAsync(
                        request.CardId, AiboardLogMarker.KindCandidate,
                        sb.ToString(), newMarker, cancellationToken);
                }
                else
                {
                    await boardClient.AppendAgentCommentAsync(
                        request.CardId, $"{newMarker}\n{sb}", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to post candidate comment for card {CardId} candidate {Index}",
                    request.CardId, i);
            }
        }

        // Consolidated step comment — the evaluator's detail plus a scores
        // table. AgentRunner posts its own kind:step comment AFTER this method
        // returns; pre-augment evaluatorResult.Detail so that comment shows
        // the scoreboard.
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...[truncated]");
}

// ── Public DTOs the runtime hands to CandidateExecutor ───────────────────────

/// <summary>Inputs needed to run a single candidate-group step.</summary>
public sealed record CandidateGroupRequest(
    string RunId,
    string CardId,
    string CardTitle,
    string StateName,
    int StepIndex,
    WorkflowStep Step,
    WorkflowRole Role,
    IReadOnlyDictionary<string, WorkflowRole> WorkflowRoles,
    IReadOnlyDictionary<string, string>? StateProviderParams,
    string TaskPrompt,
    string SystemPromptFilePath,
    string WorktreePath,
    string RepoPath,
    string GitBehavior,
    string? CommentsFilePath,
    string? PromptBaseDirectory,
    /// <summary>
    /// Card comments fetched just before this candidate group runs. Threaded
    /// through so the evaluator's re-run fast-path can find prior step markers
    /// without re-fetching. Empty list when no comments exist on the card.
    /// </summary>
    IReadOnlyList<CardComment>? ExistingComments = null);

internal sealed record CandidateExecution(
    int Index,
    string Provider,
    string Model,
    string BranchName,
    string WorktreePath,
    AgentResult AgentResult,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    /// <summary>
    /// True if this candidate exhausted retries on a transient failure (RATE_LIMIT
    /// or TIMEOUT) and the executor would have thrown <see cref="RateLimitException"/>
    /// or <see cref="TimeoutException"/> on the final attempt — but the runtime
    /// converted the failure to <see cref="AgentOutcome.ERROR"/> so sibling
    /// candidates and sibling slots still get a chance. Surfaces upward through
    /// <see cref="SlotResult.WasRateLimited"/> so AgentRunner can decide whether
    /// to bubble a real <see cref="RateLimitException"/> after the whole chain
    /// exhausts.
    /// </summary>
    bool RateLimited = false)
{
    /// <summary>Constructs a record for a candidate that failed during worktree setup.</summary>
    public static CandidateExecution FailedSetup(
        int index, string provider, string model, string branchName,
        DateTimeOffset startedAt, string errorMessage) =>
        new(
            Index: index,
            Provider: provider,
            Model: model,
            BranchName: branchName,
            WorktreePath: "",
            AgentResult: new AgentResult(AgentOutcome.ERROR,
                $"Candidate setup failed: {errorMessage}"),
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow,
            RateLimited: false);
}

/// <summary>Evaluator's verdict — winner index and per-candidate scores.</summary>
internal sealed record EvaluatorVerdict(
    int? WinnerIndex,
    IReadOnlyList<CandidateScore> Scores);

/// <summary>Per-candidate score from the evaluator.</summary>
internal sealed record CandidateScore(
    int Index,
    decimal? Score,
    string? Reasoning);

/// <summary>
/// Outcome of a single slot's execution, used by <see cref="AgentRunner"/> to
/// decide whether to short-circuit (Won/NeedsInfo) or fall back to the next slot
/// (Failed) in the step's slot list.
/// </summary>
public enum SlotOutcome
{
    /// <summary>Slot produced a winner whose outcome is COMPLETE — step succeeds with this winner.</summary>
    Won,
    /// <summary>Slot produced a result with NEEDS_INFO — step propagates the questions; no fallback.</summary>
    NeedsInfo,
    /// <summary>Slot did not produce a usable result — step should try the next slot.</summary>
    Failed,
}

/// <summary>Slot-level result returned to AgentRunner.</summary>
/// <param name="WasRateLimited">
/// True only when <see cref="Outcome"/> is <see cref="SlotOutcome.Failed"/> AND every
/// candidate in the slot exhausted retries on a transient failure (RATE_LIMIT / TIMEOUT).
/// AgentRunner's slot loop uses this to distinguish "fall through and try the next slot"
/// from "the whole slot chain was rate-limited; bubble a real RateLimitException so the
/// poller backs off." Always false for <see cref="SlotOutcome.Won"/> /
/// <see cref="SlotOutcome.NeedsInfo"/>.
/// </param>
public sealed record SlotResult(
    SlotOutcome Outcome,
    AgentResult AgentResult,
    bool WasRateLimited = false);
