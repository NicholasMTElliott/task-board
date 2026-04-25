using System.Globalization;
using System.Text;
using System.Text.Json;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Runs a single step as a parallel candidate group: N executors race on the
/// same task in separate worktrees, an evaluator picks a winner, and the
/// winning branch is promoted onto the canonical worktree before the next
/// step runs.
/// </summary>
/// <remarks>
/// Owned by <see cref="AgentRunner"/>. AgentRunner identifies a candidate-group
/// step (<c>step.Candidates is { Count: &gt; 0 }</c>) and delegates the entire
/// step body here — this class persists candidate + evaluator step_result rows,
/// posts per-candidate and consolidated comments, promotes the winner via
/// <see cref="GitWorkspaceManager.ResetWorktreeToBranchAsync"/>, and cleans up
/// loser worktrees / branches. AgentRunner only consumes the returned
/// <see cref="AgentResult"/> to drive outcome transitions.
///
/// <para>v1 supports candidates only on commit-based git behaviors
/// (<c>commit_only</c> / <c>commit_and_push</c>); the validator rejects
/// candidates on <c>discard</c> states.</para>
/// </remarks>
public sealed class CandidateExecutor(
    GitWorkspaceManager gitWorkspaceManager,
    IAgentExecutorResolver executorResolver,
    IRunStore runStore,
    ITaskBoardClient boardClient,
    ILogger<CandidateExecutor> logger)
{
    public async Task<AgentResult> ExecuteCandidateGroupAsync(
        CandidateGroupRequest request, CancellationToken cancellationToken)
    {
        var step = request.Step;
        var candidates = step.Candidates ?? throw new InvalidOperationException(
            $"ExecuteCandidateGroupAsync called for step '{step.Name}' with no candidates.");
        var evaluatorCfg = step.Evaluator ?? throw new InvalidOperationException(
            $"Step '{step.Name}' has candidates but no evaluator. Validator should have caught this.");

        var groupId = Guid.NewGuid();

        logger.LogInformation(
            "Starting candidate group for step '{StepName}' on card {CardId}: {Count} candidate(s), groupId={GroupId}",
            step.Name, request.CardId, candidates.Count, groupId);

        var canonicalBranch = await gitWorkspaceManager.GetCurrentBranchAsync(
            request.WorktreePath, cancellationToken);

        var executions = new List<CandidateExecution>(candidates.Count);

        // ── Phase 1: run each candidate in its own worktree ──────────────────
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var execution = await ExecuteSingleCandidateAsync(
                request, groupId, canonicalBranch, i, candidate, cancellationToken);
            executions.Add(execution);
        }

        // ── Short-circuit: if every candidate failed, skip the evaluator ─────
        if (executions.All(e => e.AgentResult.Outcome != AgentOutcome.COMPLETE))
        {
            logger.LogWarning(
                "All {Count} candidate(s) for step '{StepName}' returned non-COMPLETE outcomes; halting without running evaluator",
                executions.Count, step.Name);

            await CleanupCandidateWorktreesAsync(
                request.RepoPath, executions, deleteWinnerBranch: true, cancellationToken);

            // Pick the merged outcome by recoverability rather than candidate
            // declaration order: NEEDS_INFO is recoverable by a human (card moves
            // to Questions), ERROR is terminal. If even one candidate asked a
            // question, the operator deserves to see that path; otherwise fall
            // back to ERROR. Without this, a mix of {ERROR at index 0, NEEDS_INFO
            // at index 1} would route to Error and the questions would be lost.
            var mergedOutcome = executions.Any(e => e.AgentResult.Outcome == AgentOutcome.NEEDS_INFO)
                ? AgentOutcome.NEEDS_INFO
                : AgentOutcome.ERROR;
            var detail = BuildAllFailedDetail(executions);
            return new AgentResult(mergedOutcome, detail);
        }

        // ── Phase 2: run the evaluator ───────────────────────────────────────
        // RunEvaluatorAsync may write an inline-system-prompt to a temp file;
        // it returns both the result and the path so we can clean up afterwards.
        var (evaluatorResult, evaluatorTempPromptPath) = await RunEvaluatorAsync(
            request, evaluatorCfg, executions, groupId, cancellationToken);

        // Phases 3–5 are wrapped in try/finally so the evaluator's inline-prompt
        // temp file is always cleaned up even if parsing, persistence, promotion,
        // or comment posting throws. Without this guard, exceptions between
        // phase 2 and the cleanup at the bottom would orphan files in /tmp.
        try
        {
            // ── Phase 3: parse evaluator output, persist per-candidate verdicts ──
            var verdict = ParseEvaluatorVerdict(
                evaluatorResult, executions.Count, evaluatorCfg.Scoring);

            await PersistEvaluatorVerdictAsync(
                request.RunId, groupId, executions, verdict, cancellationToken);

            // ── Phase 4: promote the winner (if the evaluator picked one) ───────
            if (evaluatorResult.Outcome == AgentOutcome.COMPLETE && verdict.WinnerIndex is int winnerIdx)
            {
                var winner = executions[winnerIdx];
                logger.LogInformation(
                    "Promoting candidate {Index} (provider={Provider}) as winner for step '{StepName}'",
                    winnerIdx, winner.Provider, step.Name);

                try
                {
                    await gitWorkspaceManager.ResetWorktreeToBranchAsync(
                        request.WorktreePath, winner.BranchName, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Promotion failure is fatal — without the winner's commits the
                    // canonical worktree would proceed with stale state. Surface as ERROR.
                    logger.LogError(ex,
                        "Failed to promote candidate {Index} (branch={Branch}) for step '{StepName}'",
                        winnerIdx, winner.BranchName, step.Name);
                    await CleanupCandidateWorktreesAsync(
                        request.RepoPath, executions, deleteWinnerBranch: true, cancellationToken);
                    return new AgentResult(
                        AgentOutcome.ERROR,
                        $"Evaluator selected candidate {winnerIdx} but promotion failed: {ex.Message}");
                }

                // Cleanup losers; keep the winner's branch (it now backs the
                // canonical worktree's HEAD).
                await CleanupLoserWorktreesAsync(
                    request.RepoPath, executions, winnerIdx, cancellationToken);
            }
            else
            {
                // Evaluator returned NEEDS_INFO / ERROR or no winner. Tear down all
                // candidate worktrees + branches; canonical worktree is unchanged.
                logger.LogInformation(
                    "Evaluator did not select a winner (outcome={Outcome}); cleaning up all {Count} candidate worktrees",
                    evaluatorResult.Outcome, executions.Count);
                await CleanupCandidateWorktreesAsync(
                    request.RepoPath, executions, deleteWinnerBranch: true, cancellationToken);
            }

            // ── Phase 5: post per-candidate audit comments + the consolidated step comment
            await PostCandidateCommentsAsync(
                request, step.Name, executions, evaluatorResult, verdict, cancellationToken);

            return evaluatorResult;
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

    // ── Phase 1 helpers ──────────────────────────────────────────────────────

    private async Task<CandidateExecution> ExecuteSingleCandidateAsync(
        CandidateGroupRequest request,
        Guid groupId,
        string canonicalBranch,
        int index,
        CandidateOverride candidate,
        CancellationToken cancellationToken)
    {
        var step = request.Step;
        var providerSlug = SlugifyProvider(candidate.Provider);
        // Use a SEPARATE top-level prefix so the candidate worktree is not
        // nested inside the canonical worktree's directory (which would put one
        // git worktree inside another and fail).
        var groupShort = groupId.ToString("N")[..8];
        var candidateBranch = $"aiboard-cand/{request.CardId}-{groupShort}-{index}-{providerSlug}".ToLowerInvariant();

        var executor = executorResolver.Resolve(candidate.Provider);

        // Resolve the model and provider params: candidate overrides win, but
        // unset values fall back to the step's role defaults.
        var role = request.Role;
        var model = candidate.Model ?? role.Model;
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
                index, candidate.Provider, model, candidateBranch,
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

        // Build the per-candidate execution context.
        var context = new AgentExecutionContext(
            TargetCardId: request.CardId,
            TargetCardTitle: request.CardTitle,
            WorkspacePath: candidateWorktreePath,
            TaskPrompt: request.TaskPrompt,
            SystemPromptFilePath: request.SystemPromptFilePath,
            Model: model,
            ProviderParams: providerParams,
            CommentsFilePath: request.CommentsFilePath);

        AgentResult result;
        try
        {
            // Each candidate runs as its own short-lived process. Do NOT attempt
            // session reuse — sessions assume a single canonical worktree mount.
            result = await executor.ExecuteAsync(context, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException
            && ex is not RateLimitException)
        {
            logger.LogWarning(ex,
                "Candidate {Index} (provider={Provider}) threw {ExceptionType}; recording as ERROR outcome",
                index, candidate.Provider, ex.GetType().Name);
            result = new AgentResult(
                AgentOutcome.ERROR,
                $"Candidate execution threw {ex.GetType().Name}: {ex.Message}");
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
        // recorded even if the evaluator step blows up later.
        var stepRecord = new StepResultRecord(
            RunId: request.RunId,
            CardId: request.CardId,
            StateName: request.StateName,
            StepName: $"{step.Name}:cand-{index}:{providerSlug}",
            StepIndex: request.StepIndex,
            Role: step.Role,
            Model: model,
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
            EvaluatorReasoning: null);

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
            Model: model,
            BranchName: candidateBranch,
            WorktreePath: candidateWorktreePath,
            AgentResult: result,
            StartedAt: startedAt,
            CompletedAt: completedAt);
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
        IReadOnlyList<CandidateExecution> executions,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        var evaluatorRole = request.WorkflowRoles[evaluatorCfg.Role];
        var evaluatorExecutor = executorResolver.Resolve(evaluatorRole.Provider);

        var evaluatorTaskPrompt = await BuildEvaluatorTaskPromptAsync(
            request, evaluatorCfg, executions, cancellationToken);

        var (evaluatorSystemPromptPath, tempPromptPath) = await ResolveEvaluatorSystemPromptAsync(
            request, evaluatorRole, evaluatorCfg, cancellationToken);

        var startedAt = DateTimeOffset.UtcNow;

        // The evaluator runs against the canonical worktree (it doesn't write
        // code — it reads each candidate's diff via the embedded prompt and
        // returns a structured verdict).
        var context = new AgentExecutionContext(
            TargetCardId: request.CardId,
            TargetCardTitle: request.CardTitle,
            WorkspacePath: request.WorktreePath,
            TaskPrompt: evaluatorTaskPrompt,
            SystemPromptFilePath: evaluatorSystemPromptPath,
            Model: evaluatorRole.Model,
            ProviderParams: request.StateProviderParams,
            CommentsFilePath: request.CommentsFilePath);

        AgentResult evaluatorResult;
        try
        {
            evaluatorResult = await evaluatorExecutor.ExecuteAsync(context, cancellationToken);
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

        var completedAt = DateTimeOffset.UtcNow;

        // Persist evaluator step. Note: candidate_group_id stays NULL on the
        // evaluator row — it's a regular step that follows the group. The
        // step_name suffix `:evaluator` lets callers correlate by name.
        var evaluatorRecord = new StepResultRecord(
            RunId: request.RunId,
            CardId: request.CardId,
            StateName: request.StateName,
            StepName: $"{request.Step.Name}:evaluator",
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
            Provider: evaluatorRole.Provider);

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

            // Diff against the canonical branch — the actual code change.
            string diff;
            try
            {
                diff = await gitWorkspaceManager.GetDiffSummaryAsync(
                    e.WorktreePath, maxChars: 10_000, cancellationToken);
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
        sb.AppendLine("When `outcome = COMPLETE`, also include:");
        sb.AppendLine("- `winner_index`: integer (0-indexed) selecting the best candidate.");

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
    /// from the evaluator's structured response. Returns a verdict with sensible
    /// defaults when fields are missing so the caller doesn't have to special-case
    /// non-COMPLETE outcomes.
    /// </summary>
    internal static EvaluatorVerdict ParseEvaluatorVerdict(
        AgentResult evaluatorResult, int candidateCount, EvaluatorScoring scoring)
    {
        if (evaluatorResult.Outcome != AgentOutcome.COMPLETE)
            return new EvaluatorVerdict(WinnerIndex: null, Scores: Array.Empty<CandidateScore>());

        // Detail is the markdown summary. The structured fields we need
        // (winner_index, scores) ride alongside it inside structured_output but
        // AgentOutputParser only extracts the standard envelope. Re-parse Detail
        // for our extended fields. As a backstop, also try to extract from the
        // detail string itself if it contains a JSON block.
        var winner = TryExtractWinnerIndex(evaluatorResult.Detail);
        var scores = scoring == EvaluatorScoring.WinnerWithScores
            ? TryExtractScores(evaluatorResult.Detail, candidateCount)
            : Array.Empty<CandidateScore>();

        // If the evaluator said COMPLETE but no winner_index parsed, the
        // evaluator output is malformed. Don't pick a winner — the caller
        // treats this as "no winner promoted".
        if (winner is int idx && (idx < 0 || idx >= candidateCount))
            winner = null;

        return new EvaluatorVerdict(winner, scores);
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

    private async Task CleanupLoserWorktreesAsync(
        string repoPath,
        IReadOnlyList<CandidateExecution> executions,
        int winnerIndex,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < executions.Count; i++)
        {
            if (i == winnerIndex) continue;
            var loser = executions[i];
            try
            {
                await gitWorkspaceManager.RemoveWorktreeAsync(
                    repoPath, loser.BranchName, deleteBranch: true, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to remove loser candidate {Index} (branch={Branch})",
                    i, loser.BranchName);
            }
        }

        // Also remove the winner's worktree (we already promoted its commits to
        // canonical via ResetWorktreeToBranchAsync; the candidate worktree itself
        // is no longer needed). Keep the winner's BRANCH pointer intact so post-
        // flight git diagnostics can still reference it.
        var winner = executions[winnerIndex];
        try
        {
            await gitWorkspaceManager.RemoveWorktreeAsync(
                repoPath, winner.BranchName, deleteBranch: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to remove winner candidate worktree (branch={Branch}) after promotion",
                winner.BranchName);
        }
    }

    private async Task CleanupCandidateWorktreesAsync(
        string repoPath,
        IReadOnlyList<CandidateExecution> executions,
        bool deleteWinnerBranch,
        CancellationToken cancellationToken)
    {
        foreach (var exec in executions)
        {
            try
            {
                await gitWorkspaceManager.RemoveWorktreeAsync(
                    repoPath, exec.BranchName,
                    deleteBranch: deleteWinnerBranch,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to remove candidate worktree (branch={Branch}) during cleanup",
                    exec.BranchName);
            }
        }
    }

    private async Task PostCandidateCommentsAsync(
        CandidateGroupRequest request,
        string stepName,
        IReadOnlyList<CandidateExecution> executions,
        AgentResult evaluatorResult,
        EvaluatorVerdict verdict,
        CancellationToken cancellationToken)
    {
        // Per-candidate audit comments — make individual outputs visible without
        // bloating the consolidated step comment.
        for (var i = 0; i < executions.Count; i++)
        {
            var e = executions[i];
            var marker = $"<!-- agent-step:{stepName}:cand-{i}:{SlugifyProvider(e.Provider)} -->";
            var won = verdict.WinnerIndex is int w && w == i;
            var sb = new StringBuilder();
            sb.AppendLine($"**Candidate {i}** — provider `{e.Provider}`, model `{e.Model}` {(won ? "🏆" : "")}".TrimEnd());
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
                await boardClient.UpsertAgentCommentAsync(
                    request.CardId, sb.ToString(), marker, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to upsert candidate comment {Marker} on card {CardId}",
                    marker, request.CardId);
            }
        }

        // Consolidated step comment — the evaluator's detail plus a scores
        // table. AgentRunner posts its own step comment AFTER this method
        // returns (using the marker `agent-step:{stepName}`); we leave that
        // path alone but pre-augment evaluatorResult.Detail so the comment
        // shows the scoreboard.
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
    string? PromptBaseDirectory);

internal sealed record CandidateExecution(
    int Index,
    string Provider,
    string Model,
    string BranchName,
    string WorktreePath,
    AgentResult AgentResult,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
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
            CompletedAt: DateTimeOffset.UtcNow);
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
