using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// End-to-end-ish tests for <see cref="CandidateExecutor"/> using a real git
/// repo + worktree (the tests' baseline already runs git for the canonical
/// workspace setup, so an additional candidate worktree per case is cheap).
/// Stubs at the executor + run-store + board-client boundary; real
/// <see cref="GitWorkspaceManager"/>, real filesystem.
/// </summary>
public class CandidateExecutorFlowTests : IDisposable
{
    // Card identity reused across tests. The filename is what TaskFileManager
    // would write — slugged from the title — so test setup mirrors real
    // production behaviour.
    private const string TestCardId = "1";
    private const string TestCardTitle = "Test card";
    private static readonly string TestTaskFileName =
        TaskFileManager.GetTaskFileName(TestCardId, TestCardTitle);
    private static readonly string TestTaskWriteKey = $"tasks/{TestTaskFileName}";

    private readonly string _repoRoot;
    private readonly string _canonicalWorktree;
    private readonly GitWorkspaceManager _git;
    private readonly RecordingRunStore _runStore;
    private readonly RecordingBoardClient _boardClient;

    public CandidateExecutorFlowTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "candidate-flow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoRoot);
        InitGitRepo(_repoRoot);

        _git = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _canonicalWorktree = _git.CreateWorktreeAsync(
            _repoRoot, "aiboard/canonical-1", CancellationToken.None).GetAwaiter().GetResult();

        // Seed canonical worktree with the .aiboard task file copy logic exercises.
        var taskDir = Path.Combine(_canonicalWorktree, ".aiboard", "tasks");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, TestTaskFileName), "# Card 1\n\noriginal task body\n");

        _runStore = new RecordingRunStore();
        _boardClient = new RecordingBoardClient();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
        try { Directory.Delete(_repoRoot + "-worktrees", recursive: true); } catch { }
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_TwoCandidates_EvaluatorPicksWinner_PromotesAndPersists()
    {
        // Two candidates that both COMPLETE with different details. Evaluator
        // picks index 1. Asserts: 2 candidate rows + 1 evaluator row saved,
        // selected=true on row index 1, canonical worktree's HEAD now points
        // at candidate 1's commit.
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.COMPLETE, "Claude implementation: tweaked X"),
            ("docker-opencode",   AgentOutcome.COMPLETE, "Qwen implementation: tweaked Y"),
            evaluatorOutcome: AgentOutcome.COMPLETE,
            evaluatorDetail: """
                Both work; Qwen is cleaner.
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":7,"reasoning":"works but verbose"},
                  {"index":1,"score":8.5,"reasoning":"clean and idiomatic"}
                ]}
                ```
                """);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // 2 candidate rows + 1 evaluator row
        var saved = _runStore.SavedSteps;
        Assert.Equal(3, saved.Count);
        Assert.Equal(2, saved.Count(r => r.CandidateGroupId is not null));
        Assert.Single(saved, r => r.StepName.EndsWith(":evaluator"));

        // Evaluator updated 2 candidate rows with verdicts.
        var verdicts = _runStore.RecordedVerdicts;
        Assert.Equal(2, verdicts.Count);
        Assert.Single(verdicts, v => v.CandidateIndex == 0 && v.Selected == false);
        Assert.Single(verdicts, v => v.CandidateIndex == 1 && v.Selected == true);

        // Quality scores recorded for both.
        Assert.Equal(7m, verdicts.Single(v => v.CandidateIndex == 0).QualityScore);
        Assert.Equal(8.5m, verdicts.Single(v => v.CandidateIndex == 1).QualityScore);

        // Per-candidate audit comments + evaluator-driven step comment posted.
        // (CandidateExecutor posts the per-candidate ones; AgentRunner posts the
        // step-level comment, which this test does NOT exercise.)
        Assert.Equal(2, _boardClient.Comments.Count(c => c.Marker.Contains("kind:candidate")));
    }

    // ── V22: evaluator_prompt_chars persisted on evaluator step row ──────────

    [Fact]
    public async Task EvaluatorRow_PersistsEvaluatorPromptChars()
    {
        // V22: the evaluator step row should carry the prompt body's char count
        // so operators can spot context-truncation trends before evaluator
        // verdicts silently degrade. Char count is non-zero (the prompt always
        // contains rubric + per-candidate sections). Non-evaluator candidate
        // rows must NOT carry this field — it's evaluator-specific.
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.COMPLETE, "Claude impl"),
            ("docker-opencode",   AgentOutcome.COMPLETE, "Qwen impl"),
            evaluatorOutcome: AgentOutcome.COMPLETE,
            evaluatorDetail: """
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":7,"reasoning":"works"},
                  {"index":1,"score":8.5,"reasoning":"cleaner"}
                ]}
                ```
                """);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        await candidateExecutor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        var evaluatorRow = _runStore.SavedSteps.Single(r => r.StepName.EndsWith(":evaluator"));
        Assert.NotNull(evaluatorRow.EvaluatorPromptChars);
        Assert.True(evaluatorRow.EvaluatorPromptChars > 0,
            $"Expected evaluator prompt chars > 0; got {evaluatorRow.EvaluatorPromptChars}");

        // Candidate rows themselves should NOT carry the evaluator-prompt char count.
        var candidateRows = _runStore.SavedSteps.Where(r => r.CandidateGroupId is not null);
        Assert.All(candidateRows, r => Assert.Null(r.EvaluatorPromptChars));
    }

    // ── Evaluator-commits-winner section_update ──────────────────────────────

    [Fact]
    public async Task EvaluatorCommitsWinner_PropagatesWinnerSection_NotEvaluatorSection()
    {
        // Rerun-redesign Problem 2: "the evaluator commits the winner's
        // section_update on the group's behalf." Mechanically, the SlotResult
        // returned by CandidateExecutor must carry the WINNER candidate's
        // Section field — not the evaluator's. AgentRunner's DescriptionWriter
        // wiring then applies the winner's section update to the card.
        //
        // We tell the evaluator to set strategy=leave (per the prompt
        // contract); without this fix, the evaluator's leave Section would
        // propagate and the winner's replace would be silently discarded.
        var winnerSection = new SectionUpdate(
            Strategy: SectionUpdateStrategy.Replace,
            Content: "winner-content (qwen)");
        var loserSection = new SectionUpdate(
            Strategy: SectionUpdateStrategy.Replace,
            Content: "loser-content (claude)");
        var evaluatorSection = new SectionUpdate(
            Strategy: SectionUpdateStrategy.Leave);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, "Claude impl", section: loserSection),
            ["docker-opencode"]   = new ScriptedExecutor(
                AgentOutcome.COMPLETE, "Qwen impl", section: winnerSection),
            ["claude-cli"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE,
                """
                Qwen wins on cleanliness.
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":6,"reasoning":"verbose"},
                  {"index":1,"score":8,"reasoning":"clean"}
                ]}
                ```
                """,
                section: evaluatorSection),
        };
        var resolver = new MapResolver(byProvider);
        var candidateExecutor = new CandidateExecutor(
            _git, resolver, _runStore, _boardClient,
            NullLogger<CandidateExecutor>.Instance);

        var request = NewRequest(
            stepName: "create_design",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(result.Section);
        // The decisive assertion: the propagated Section is the WINNER's, not
        // the evaluator's leave nor the loser's replace.
        Assert.Equal(SectionUpdateStrategy.Replace, result.Section!.Strategy);
        Assert.Equal("winner-content (qwen)", result.Section.Content);
    }

    [Fact]
    public async Task SingleCandidateNoEvaluator_PropagatesCandidateSection()
    {
        // The single-candidate-no-evaluator slot path also goes through
        // PromoteAndFinalizeWinnerAsync. Ensure the candidate's Section
        // propagates as-is — this is the no-op-by-equality case (the fix's
        // substitution is `with { Section = winner.AgentResult.Section }`,
        // and here the winner's result is already the surfaced result).
        var winnerSection = new SectionUpdate(
            Strategy: SectionUpdateStrategy.Replace,
            Content: "single-candidate-content");

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, "single impl", section: winnerSection),
        };
        var resolver = new MapResolver(byProvider);
        var candidateExecutor = new CandidateExecutor(
            _git, resolver, _runStore, _boardClient,
            NullLogger<CandidateExecutor>.Instance);

        // Build a single-candidate slot WITH no evaluator.
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# system");
        var step = new WorkflowStep(
            Name: "create_design",
            Role: "implementer",
            TaskPromptFile: null,
            TaskPrompt: "Implement the thing.",
            Slots: new List<SlotConfig>
            {
                new SlotConfig(
                    Candidates: new List<CandidateOverride> { new("docker-claude-cli") },
                    Evaluator: null),
            });

        var request = new CandidateGroupRequest(
            RunId: "run-test-1",
            CardId: "1",
            CardTitle: "Test card",
            StateName: "Implementing",
            StepIndex: 0,
            Step: step,
            Role: new WorkflowRole(
                Model: "claude-sonnet-4-6",
                SystemPrompt: "you are an implementer",
                Sections: []),
            WorkflowRoles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-sonnet-4-6", "sys", []),
            },
            StateProviderParams: null,
            TaskPrompt: "Implement the thing.",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: "commit_and_push",
            CommentsFilePath: null,
            PromptBaseDirectory: null);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(result.Section);
        Assert.Equal(SectionUpdateStrategy.Replace, result.Section!.Strategy);
        Assert.Equal("single-candidate-content", result.Section.Content);
    }

    // ── Cleanup pinning ──────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_AfterPromotion_NoLeftoverBranchesOrWorktrees()
    {
        // Same shape as HappyPath_TwoCandidates, focused only on cleanup
        // post-conditions. Pre-v0.0.23 the winner's branch was preserved
        // under a "post-flight git diagnostics" rationale that nothing
        // actually used; over many runs this accumulated hundreds of
        // aiboard-cand/... branches in the operator's repo (the v0.0.22
        // KvA field-report symptom). Splitting this from HappyPath keeps
        // the failure-to-pinpoint clean: a break here means cleanup
        // regressed; a break in HappyPath means promotion regressed.
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.COMPLETE, "Claude impl"),
            ("docker-opencode",   AgentOutcome.COMPLETE, "Qwen impl"),
            evaluatorOutcome: AgentOutcome.COMPLETE,
            evaluatorDetail: """
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":7,"reasoning":"works"},
                  {"index":1,"score":8.5,"reasoning":"cleaner"}
                ]}
                ```
                """);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);  // pre-condition

        // (a) No candidate branches survive — winner included.
        var leftoverBranches = RunGitSyncWithOutput(_repoRoot, "branch", "--list", "aiboard-cand/*");
        Assert.True(string.IsNullOrWhiteSpace(leftoverBranches),
            $"Expected no surviving candidate branches; got: '{leftoverBranches}'");

        // (b) No non-empty candidate worktree directories on disk. Empty
        //     parent dirs (the `aiboard-cand/` root itself) are tolerated —
        //     git typically removes leaf dirs during `worktree remove --force`
        //     but may leave the parent.
        var candidateWorktreeRoot = Path.Combine(_repoRoot + "-worktrees", "aiboard-cand");
        if (Directory.Exists(candidateWorktreeRoot))
        {
            var leftoverDirs = Directory.EnumerateDirectories(candidateWorktreeRoot, "*", SearchOption.AllDirectories)
                .Where(d => Directory.EnumerateFileSystemEntries(d).Any())
                .ToArray();
            Assert.True(leftoverDirs.Length == 0,
                $"Expected no surviving candidate worktree directories; got: {string.Join(", ", leftoverDirs)}");
        }

        // (c) No stale `git worktree list` registrations.
        var registeredWorktrees = RunGitSyncWithOutput(_repoRoot, "worktree", "list", "--porcelain");
        Assert.DoesNotContain("aiboard-cand", registeredWorktrees);
    }

    // ── All candidates error ─────────────────────────────────────────────────

    [Fact]
    public async Task AllCandidatesFail_HaltsBeforeRunningEvaluator()
    {
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.ERROR, "broke during build"),
            ("docker-opencode",   AgentOutcome.ERROR, "model returned garbage"),
            evaluatorOutcome: AgentOutcome.COMPLETE,  // never invoked
            evaluatorDetail: "(should not appear)");

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("All candidates errored", result.Detail);

        // Two candidate rows persisted, NO evaluator row.
        Assert.Equal(2, _runStore.SavedSteps.Count);
        Assert.DoesNotContain(_runStore.SavedSteps, r => r.StepName.EndsWith(":evaluator"));

        // No verdict updates (evaluator never ran).
        Assert.Empty(_runStore.RecordedVerdicts);
    }

    [Fact]
    public async Task MixedCompleteAndNeedsInfo_RunsEvaluatorAndCanPickNeedsInfoWinner()
    {
        // NEEDS_INFO is a successful candidate outcome for ranking purposes:
        // the candidate may have spotted a real blocker that COMPLETE
        // candidates missed. The evaluator must decide, and if it picks the
        // NEEDS_INFO candidate the slot propagates that question to the user.
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.COMPLETE, "Implemented the obvious path."),
            ("docker-opencode",   AgentOutcome.NEEDS_INFO, "Critical API ambiguity needs resolution."),
            evaluatorOutcome: AgentOutcome.COMPLETE,
            evaluatorDetail: """
                Candidate 1 wins because the ambiguity is real.
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":6,"reasoning":"works but misses the blocker"},
                  {"index":1,"score":8,"reasoning":"correctly blocks on a critical ambiguity"}
                ]}
                ```
                """);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);

        // Both candidates persisted, evaluator ran, and the NEEDS_INFO
        // candidate was selected.
        Assert.Equal(3, _runStore.SavedSteps.Count);
        Assert.Single(_runStore.SavedSteps, r => r.StepName.EndsWith(":evaluator"));
        Assert.Single(_runStore.RecordedVerdicts, v => v.CandidateIndex == 1 && v.Selected);
    }

    [Fact]
    public async Task AllCandidatesNeedInfo_RunsEvaluatorInsteadOfAllErrorShortcut()
    {
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.NEEDS_INFO, "Question A, with partial design."),
            ("docker-opencode",   AgentOutcome.NEEDS_INFO, "Question B, with better risk analysis."),
            evaluatorOutcome: AgentOutcome.COMPLETE,
            evaluatorDetail: """
                Candidate 1 wins; its question is the actionable blocker.
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":6,"reasoning":"reasonable but less actionable"},
                  {"index":1,"score":8,"reasoning":"best blocker analysis"}
                ]}
                ```
                """);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Question B, with better risk analysis.", result.Detail);
        Assert.Equal(3, _runStore.SavedSteps.Count);
        Assert.Single(_runStore.SavedSteps, r => r.StepName.EndsWith(":evaluator"));
        Assert.Single(_runStore.RecordedVerdicts, v => v.CandidateIndex == 1 && v.Selected);
    }

    [Fact]
    public async Task EvaluatorExecutorThrows_FallsBackToConfiguredEvaluator()
    {
        // Evaluator fallback is different from candidate fallback: once the
        // candidate work exists, a primary evaluator CLI/auth/parser crash
        // should not strand the whole slot if a configured evaluator fallback
        // can still judge the candidates.
        var fallbackEvaluator = new TrackingScriptedExecutor(
            AgentOutcome.COMPLETE,
            """
            Candidate 1 wins.
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[
              {"index":0,"score":6,"reasoning":"ok"},
              {"index":1,"score":8,"reasoning":"better"}
            ]}
            ```
            """);
        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Claude implementation"),
            ["docker-opencode"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Qwen implementation"),
            ["claude-cli"] = new ThrowingExecutor(new InvalidOperationException(
                "Claude CLI exited with code 1. Stdout: init only")),
            ["docker-codex"] = fallbackEvaluator,
        };
        var candidateExecutor = new CandidateExecutor(
            _git,
            new MapResolver(byProvider),
            _runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);
        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"],
            evaluatorRole: EvaluatorRoleWithCodexFallback());

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(1, fallbackEvaluator.Calls);
        Assert.Single(_runStore.RecordedVerdicts, v => v.CandidateIndex == 1 && v.Selected);

        var evaluatorRow = _runStore.SavedSteps.Single(r => r.StepName.EndsWith(":evaluator"));
        Assert.Equal("docker-codex", evaluatorRow.Provider);
        Assert.Equal("gpt-5.5", evaluatorRow.Model);
    }

    [Fact]
    public async Task EvaluatorCompleteWithoutWinner_FallsBackToConfiguredEvaluator()
    {
        // outcome=COMPLETE without a usable winner_index is not a valid
        // evaluator verdict. If a fallback evaluator is configured, try it
        // before failing the slot.
        var primaryEvaluator = new TrackingScriptedExecutor(
            AgentOutcome.COMPLETE,
            "Candidate 1 seems best, but I forgot the structured field.");
        var fallbackEvaluator = new TrackingScriptedExecutor(
            AgentOutcome.COMPLETE,
            """
            Candidate 1 wins.
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[
              {"index":0,"score":6,"reasoning":"ok"},
              {"index":1,"score":8,"reasoning":"better"}
            ]}
            ```
            """);
        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Claude implementation"),
            ["docker-opencode"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Qwen implementation"),
            ["claude-cli"] = primaryEvaluator,
            ["docker-codex"] = fallbackEvaluator,
        };
        var candidateExecutor = new CandidateExecutor(
            _git,
            new MapResolver(byProvider),
            _runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);
        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"],
            evaluatorRole: EvaluatorRoleWithCodexFallback());

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(1, primaryEvaluator.Calls);
        Assert.Equal(1, fallbackEvaluator.Calls);
        Assert.Single(_runStore.RecordedVerdicts, v => v.CandidateIndex == 1 && v.Selected);

        var evaluatorRow = _runStore.SavedSteps.Single(r => r.StepName.EndsWith(":evaluator"));
        Assert.Equal("docker-codex", evaluatorRow.Provider);
    }

    [Fact]
    public async Task EvaluatorInBandError_DoesNotFallback()
    {
        // A structured evaluator ERROR is a valid verdict ("no candidate is
        // acceptable"), not an executor failure. Do not evaluator-shop by
        // asking the fallback for a different answer.
        var primaryEvaluator = new TrackingScriptedExecutor(
            AgentOutcome.ERROR,
            "All candidates are unacceptable.");
        var fallbackEvaluator = new TrackingScriptedExecutor(
            AgentOutcome.COMPLETE,
            """
            Candidate 1 wins.
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[]}
            ```
            """);
        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Claude implementation"),
            ["docker-opencode"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Qwen implementation"),
            ["claude-cli"] = primaryEvaluator,
            ["docker-codex"] = fallbackEvaluator,
        };
        var candidateExecutor = new CandidateExecutor(
            _git,
            new MapResolver(byProvider),
            _runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);
        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"],
            evaluatorRole: EvaluatorRoleWithCodexFallback());

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Equal(1, primaryEvaluator.Calls);
        Assert.Equal(0, fallbackEvaluator.Calls);
        Assert.All(_runStore.RecordedVerdicts, v => Assert.False(v.Selected));
    }

    // ── Evaluator returns NEEDS_INFO ────────────────────────────────────────

    [Fact]
    public async Task EvaluatorReturnsNeedsInfo_CleansUpAllCandidates_NoPromotion()
    {
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.COMPLETE, "works"),
            ("docker-opencode",   AgentOutcome.COMPLETE, "also works"),
            evaluatorOutcome: AgentOutcome.NEEDS_INFO,
            evaluatorDetail: "Need clarification on the spec.");

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);

        // Verdicts recorded — both selected=false because no winner_index parsed.
        var verdicts = _runStore.RecordedVerdicts;
        Assert.All(verdicts, v => Assert.False(v.Selected));

        // Candidate worktrees + branches gone (cleanup phase).
        Assert.False(BranchExists("aiboard/canonical-1/cand-0-docker-claude-cli-" + verdicts[0].GroupShortHash + "..."));
        // We can't easily guess the full branch name with the random groupId; instead
        // assert that no aiboard/canonical-1/cand-* branches survive.
        var branches = RunGitSyncWithOutput(_repoRoot, "branch", "--list", "aiboard/canonical-1/cand-*");
        Assert.True(string.IsNullOrWhiteSpace(branches),
            $"Expected no surviving candidate branches; got: '{branches}'");
    }

    // ── Discard-mode: file-based winner promotion ────────────────────────────

    [Fact]
    public async Task DiscardMode_WinnerArtifactsCopied_NoGitCommitsOnCanonical()
    {
        // Two design candidates each write a different design into
        // .aiboard/tasks/1.md plus a child-card request to .aiboard/updates/.
        // Evaluator picks candidate 1. Assertions:
        //   1. Canonical worktree's .aiboard/tasks/1.md matches candidate 1's content
        //   2. Canonical worktree's .aiboard/updates/ has candidate 1's update files
        //   3. No new commits on the canonical branch (discard preserves git state)
        //   4. All candidate branches torn down

        var headBefore = RunGitSyncWithOutput(_canonicalWorktree, "rev-parse", "HEAD").Trim();

        var cand0Writes = new Dictionary<string, string>
        {
            [TestTaskWriteKey] = "# Card 1\n\n## Technical Design\n\nApproach A: monolith.\n",
            ["updates/new-task-foo.md"] = "---\ntitle: Task Foo\n---\nFrom A.\n",
        };
        var cand1Writes = new Dictionary<string, string>
        {
            [TestTaskWriteKey] = "# Card 1\n\n## Technical Design\n\nApproach B: microservices.\n",
            ["updates/new-task-bar.md"] = "---\ntitle: Task Bar\n---\nFrom B.\n",
            ["updates/new-task-baz.md"] = "---\ntitle: Task Baz\n---\nAlso from B.\n",
        };

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE, "Approach A — monolith.", cand0Writes),
            ["docker-opencode"]   = new ScriptedExecutor(AgentOutcome.COMPLETE, "Approach B — microservices.", cand1Writes),
            ["claude-cli"]        = new ScriptedExecutor(AgentOutcome.COMPLETE,
                """
                Microservices wins on long-term flexibility.
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":6,"reasoning":"works but couples concerns"},
                  {"index":1,"score":8.5,"reasoning":"better separation"}
                ]}
                ```
                """),
        };
        var resolver = new MapResolver(byProvider);

        var executor = new CandidateExecutor(_git, resolver, _runStore, _boardClient,
            NullLogger<CandidateExecutor>.Instance);

        var request = NewRequest(
            stepName: "create_design",
            providers: ["docker-claude-cli", "docker-opencode"],
            gitBehavior: "discard");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // (1) Canonical task body is candidate 1's
        var canonicalTask = File.ReadAllText(
            Path.Combine(_canonicalWorktree, ".aiboard", "tasks", TestTaskFileName));
        Assert.Contains("Approach B: microservices", canonicalTask);
        Assert.DoesNotContain("Approach A: monolith", canonicalTask);

        // (2) Canonical updates directory has candidate 1's two files, NOT candidate 0's
        var updatesDir = Path.Combine(_canonicalWorktree, ".aiboard", "updates");
        var updateFiles = Directory.EnumerateFiles(updatesDir, "*.md")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "new-task-bar.md", "new-task-baz.md" }, updateFiles);

        // (3) No new commits on canonical (HEAD unchanged)
        var headAfter = RunGitSyncWithOutput(_canonicalWorktree, "rev-parse", "HEAD").Trim();
        Assert.Equal(headBefore, headAfter);

        // (4) All candidate branches gone (discard cleanup deletes winner branch too)
        var branches = RunGitSyncWithOutput(_repoRoot, "branch", "--list", "aiboard-cand/*");
        Assert.True(string.IsNullOrWhiteSpace(branches),
            $"Expected no surviving candidate branches; got: '{branches}'");

        // Verdict persisted with selected=true on index 1
        var winnerVerdict = _runStore.RecordedVerdicts.Single(v => v.CandidateIndex == 1);
        Assert.True(winnerVerdict.Selected);
    }

    // ── Fully-parallel candidate execution ───────────────────────────────────

    [Fact]
    public async Task AllCandidates_RunInParallel_RegardlessOfProvider()
    {
        // Three candidates: docker-claude-cli×2, docker-opencode×1.
        // Each candidate sleeps 250ms before returning COMPLETE. Expected timing:
        // every candidate starts before any of them finishes (full parallelism).
        // The pre-fix shape grouped by provider key and serialised same-provider
        // candidates within each group, costing wall-clock time when one provider
        // contributed several model variants.
        //
        // Concurrency caps that genuinely matter (a single shared local llama.cpp
        // server) are now expressed via ResourcePool, not by reflexive grouping.
        var sleepDuration = TimeSpan.FromMilliseconds(250);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new SleepingExecutor(sleepDuration, AgentOutcome.COMPLETE, "claude-ok"),
            ["docker-opencode"]   = new SleepingExecutor(sleepDuration, AgentOutcome.COMPLETE, "opencode-ok"),
            ["claude-cli"]        = new ScriptedExecutor(AgentOutcome.COMPLETE,
                """
                {"outcome":"COMPLETE","winner_index":0,"scores":[
                  {"index":0,"score":7,"reasoning":"a"},
                  {"index":1,"score":7,"reasoning":"b"},
                  {"index":2,"score":7,"reasoning":"c"}
                ]}
                """),
        };

        var candidateExecutor = new CandidateExecutor(
            _git,
            new MapResolver(byProvider),
            _runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        var candidateRecords = _runStore.SavedSteps
            .Where(r => r.CandidateGroupId is not null)
            .ToDictionary(r => r.CandidateIndex!.Value);

        Assert.Equal(3, candidateRecords.Count);
        var claude0 = candidateRecords[0];
        var claude1 = candidateRecords[1];
        var opencode = candidateRecords[2];

        // Full parallelism: every candidate starts before the first one finishes.
        // Reference point is min(completed) — the fastest candidate to finish.
        // Every other candidate must have started BEFORE that point, otherwise
        // someone was waiting in line.
        var earliestCompletion = new[] { claude0.CompletedAtUtc, claude1.CompletedAtUtc, opencode.CompletedAtUtc }.Min();

        Assert.True(
            claude0.StartedAtUtc < earliestCompletion,
            $"Candidate 0 (claude) should start before any candidate finishes. earliestCompletion={earliestCompletion:O}, claude0.StartedAtUtc={claude0.StartedAtUtc:O}");
        Assert.True(
            claude1.StartedAtUtc < earliestCompletion,
            $"Candidate 1 (claude) should start before any candidate finishes — same-provider parallelism is now expected. earliestCompletion={earliestCompletion:O}, claude1.StartedAtUtc={claude1.StartedAtUtc:O}");
        Assert.True(
            opencode.StartedAtUtc < earliestCompletion,
            $"Candidate 2 (opencode) should start before any candidate finishes. earliestCompletion={earliestCompletion:O}, opencode.StartedAtUtc={opencode.StartedAtUtc:O}");
    }

    // ── V22: ResourcePool serialization across providers ─────────────────────

    [Fact]
    public async Task ResourcePool_SharedResource_SerializesAcrossProviders()
    {
        // V22: docker-opencode and docker-claude-qwen both target a single
        // local llama.cpp server. Without the resource pool, the parallel-by-
        // provider path lets them hit the proxy concurrently — the second waits
        // minutes for a slot, the inactivity timer fires, and the candidate
        // looks like a timeout. With the pool tagging both providers to a
        // local-llm resource of capacity 1, the second waits cleanly on the
        // semaphore until the first releases.
        var sleepDuration = TimeSpan.FromMilliseconds(250);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-opencode"]    = new SleepingExecutor(sleepDuration, AgentOutcome.COMPLETE, "qwen-1"),
            ["docker-claude-qwen"] = new SleepingExecutor(sleepDuration, AgentOutcome.COMPLETE, "qwen-2"),
            ["claude-cli"]         = new ScriptedExecutor(AgentOutcome.COMPLETE,
                """
                {"outcome":"COMPLETE","winner_index":0,"scores":[
                  {"index":0,"score":7,"reasoning":"a"},
                  {"index":1,"score":7,"reasoning":"b"}
                ]}
                """),
        };

        var poolOpts = new ResourcePoolOptions
        {
            Pools = { ["local-llm"] = new ResourcePoolDefinition { MaxConcurrent = 1 } },
            ProviderResources =
            {
                ["docker-opencode"] = ["local-llm"],
                ["docker-claude-qwen"] = ["local-llm"],
            },
        };
        using var pool = new ResourcePool(
            Microsoft.Extensions.Options.Options.Create(poolOpts),
            NullLogger<ResourcePool>.Instance);

        var candidateExecutor = new CandidateExecutor(
            _git,
            new MapResolver(byProvider),
            _runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance,
            resourcePool: pool);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-opencode", "docker-claude-qwen"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        var records = _runStore.SavedSteps
            .Where(r => r.CandidateGroupId is not null)
            .ToDictionary(r => r.CandidateIndex!.Value);

        Assert.Equal(2, records.Count);
        var first = records[0];
        var second = records[1];

        // The two candidates run on DIFFERENT providers, so without the pool
        // they'd overlap (~250ms total wall time). With the pool serialising
        // them on local-llm, total wall time should be ≥ 2*sleepDuration.
        //
        // We check end-to-end span: from earliest startedAt to latest completedAt.
        // Note: candidate `startedAt` is captured BEFORE the pool acquire (worktree
        // setup runs concurrently across providers), so we can't assert on
        // startedAt ordering alone — the pool only serialises the LLM call itself.
        var earliestStart = new[] { first.StartedAtUtc, second.StartedAtUtc }.Min();
        var latestEnd = new[] { first.CompletedAtUtc, second.CompletedAtUtc }.Max();
        var totalSpan = latestEnd - earliestStart;

        Assert.True(
            totalSpan >= sleepDuration * 1.8,  // 1.8x to allow some scheduling slop while still rejecting 1x overlap
            $"Total wall time should reflect serialised execution (≥ ~{(sleepDuration * 1.8).TotalMilliseconds:F0}ms). " +
            $"Got {totalSpan.TotalMilliseconds:F0}ms — pool isn't serialising? " +
            $"first: {first.StartedAtUtc:O}–{first.CompletedAtUtc:O} ({(first.CompletedAtUtc - first.StartedAtUtc).TotalMilliseconds:F0}ms), " +
            $"second: {second.StartedAtUtc:O}–{second.CompletedAtUtc:O} ({(second.CompletedAtUtc - second.StartedAtUtc).TotalMilliseconds:F0}ms).");

        // The waiting candidate's elapsed time is at least its sleep duration
        // PLUS the wait — so one of the two should show elapsed ≥ ~2x sleep.
        var firstElapsed = first.CompletedAtUtc - first.StartedAtUtc;
        var secondElapsed = second.CompletedAtUtc - second.StartedAtUtc;
        var maxElapsed = firstElapsed > secondElapsed ? firstElapsed : secondElapsed;
        Assert.True(
            maxElapsed >= sleepDuration * 1.8,
            $"At least one candidate should have waited on the semaphore. " +
            $"Max elapsed = {maxElapsed.TotalMilliseconds:F0}ms; expected ≥ ~{(sleepDuration * 1.8).TotalMilliseconds:F0}ms.");
    }

    // ── CommentsFilePath candidate-relative rewrite ──────────────────────────

    [Fact]
    public async Task CandidateExecution_CommentsFilePath_PointsAtCandidateWorktree_NotCanonical()
    {
        // Regression: prior to the fix, CandidateGroupRequest.CommentsFilePath
        // (canonical-worktree path) was passed verbatim into each candidate's
        // AgentExecutionContext, so PromptBuilder embedded the canonical path
        // in the prompt — the candidate's container had no such file at that
        // path and the agent burned its timeout in a doomed read-fail-glob-fail
        // loop. Fix: rebuild CommentsFilePath from the candidate's WorkspacePath
        // after CopyAiboardArtifactsToCandidate runs.

        // Seed the canonical worktree's comments file (production AgentRunner
        // would have written it before invoking CandidateExecutor).
        var canonicalCommentsPath = TaskFileManager.GetCommentsFilePath(
            _canonicalWorktree, TestCardId, TestCardTitle);
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalCommentsPath)!);
        File.WriteAllText(canonicalCommentsPath, "# prior conversation\n");

        // Capture (context, file-existed-at-call-time). We must check
        // file-exists DURING the executor call — by the time the assertion
        // runs, the loser candidate's worktree has been cleaned up by the
        // evaluator's promotion phase, and a post-call File.Exists would
        // false-negative on the cleaned-up path.
        var capturedSnapshots = new List<(AgentExecutionContext Context, bool FileExistedAtCallTime)>();
        var capturingExecutor = new CapturingExecutor(
            AgentOutcome.COMPLETE, "ok", capturedSnapshots);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = capturingExecutor,
            ["docker-opencode"]   = capturingExecutor,
            ["claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE,
                "Both fine.\n```json\n{\"outcome\":\"COMPLETE\",\"winner_index\":0," +
                "\"scores\":[{\"index\":0,\"score\":8,\"reasoning\":\"\"}," +
                "{\"index\":1,\"score\":6,\"reasoning\":\"\"}]}\n```"),
        };

        var executor = new CandidateExecutor(
            _git, new MapResolver(byProvider),
            _runStore, _boardClient, NullLogger<CandidateExecutor>.Instance);

        // Re-use NewRequest's shape but inject CommentsFilePath. NewRequest's
        // default leaves it null; we want it set to the canonical path so the
        // bug-vs-fix is observable.
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# evaluator system prompt");
        var request = new CandidateGroupRequest(
            RunId: "run-comments-rewrite-1",
            CardId: TestCardId,
            CardTitle: TestCardTitle,
            StateName: "Implementing",
            StepIndex: 0,
            Step: new WorkflowStep(
                Name: "implement",
                Role: "implementer",
                TaskPromptFile: null,
                TaskPrompt: "Implement the thing.",
                Candidates: [
                    new CandidateOverride("docker-claude-cli"),
                    new CandidateOverride("docker-opencode"),
                ],
                Evaluator: new EvaluatorConfig(
                    Role: "evaluator",
                    TaskPrompt: "Evaluate.")),
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
            TaskPrompt: "Implement.",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: "commit_and_push",
            CommentsFilePath: canonicalCommentsPath,
            PromptBaseDirectory: null);

        await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        // Both candidates' contexts captured. Each must have a CommentsFilePath
        // that lives UNDER the candidate's own worktree, NOT the canonical one.
        Assert.Equal(2, capturedSnapshots.Count);
        foreach (var (ctx, fileExistedAtCallTime) in capturedSnapshots)
        {
            Assert.NotNull(ctx.CommentsFilePath);
            Assert.NotEqual(canonicalCommentsPath, ctx.CommentsFilePath);
            // Candidate worktree path == ctx.WorkspacePath; the comments file
            // must live underneath it (so production-side TaskFileManager.Read
            // finds the file the candidate sees).
            Assert.StartsWith(ctx.WorkspacePath, ctx.CommentsFilePath!,
                StringComparison.OrdinalIgnoreCase);
            // The actual file must exist at the rewritten path AT THE MOMENT
            // the executor saw the context (proves CopyAiboardArtifactsToCandidate
            // seeded it before the agent ran). After the call returns, the
            // evaluator's cleanup may have removed the loser worktree.
            Assert.True(fileExistedAtCallTime,
                $"Comments file should exist on disk at the rewritten path when the agent runs: {ctx.CommentsFilePath}");
        }
    }

    [Fact]
    public async Task CandidateExecution_CommentsFilePathNullOnRequest_RemainsNullPerCandidate()
    {
        // When the canonical request didn't have a comments file (no comments
        // on the card yet, or the test path), candidates must also see null —
        // not a fabricated path that doesn't exist anywhere.
        var capturedSnapshots = new List<(AgentExecutionContext Context, bool FileExistedAtCallTime)>();
        var capturingExecutor = new CapturingExecutor(
            AgentOutcome.COMPLETE, "ok", capturedSnapshots);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = capturingExecutor,
            ["claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE,
                "OK.\n```json\n{\"outcome\":\"COMPLETE\",\"winner_index\":0," +
                "\"scores\":[{\"index\":0,\"score\":8,\"reasoning\":\"\"}]}\n```"),
        };
        var executor = new CandidateExecutor(
            _git, new MapResolver(byProvider),
            _runStore, _boardClient, NullLogger<CandidateExecutor>.Instance);

        // NewRequest defaults CommentsFilePath: null.
        var request = NewRequest("implement", ["docker-claude-cli"]);
        await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        Assert.Single(capturedSnapshots);
        Assert.Null(capturedSnapshots[0].Context.CommentsFilePath);
    }

    // ── Cross-provider init-file mirroring (AgentInitFileResolver) ─────────

    [Fact]
    public async Task Candidate_OpenCodeProvider_AgentsMirroredFromClaude_ExistsAtCallTime()
    {
        // Project repo has CLAUDE.md only. An OpenCode candidate runs against
        // a worktree branched off canonical HEAD; AgentInitFileResolver should
        // surface AGENTS.md (link or copy) in the candidate worktree before
        // the executor runs.
        File.WriteAllText(Path.Combine(_canonicalWorktree, "CLAUDE.md"), "# Project context\nClaude rules.\n");
        RunGitSync(_canonicalWorktree, "add", "CLAUDE.md");
        RunGitSync(_canonicalWorktree, "commit", "-m", "add CLAUDE.md");

        var snapshots = new List<(AgentExecutionContext Context, bool AgentsExistedAtCallTime)>();
        var capturingExecutor = new InitFileCapturingExecutor(snapshots);

        // Evaluator default provider is claude-cli (WorkflowRole record default);
        // wire a scripted COMPLETE so the candidate-group flow can complete.
        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-opencode"] = capturingExecutor,
            ["claude-cli"] = new ScriptedExecutor(AgentOutcome.COMPLETE,
                "OK.\n```json\n{\"outcome\":\"COMPLETE\",\"winner_index\":0," +
                "\"scores\":[{\"index\":0,\"score\":8,\"reasoning\":\"\"}]}\n```"),
        };
        var executor = new CandidateExecutor(
            _git, new MapResolver(byProvider),
            _runStore, _boardClient, NullLogger<CandidateExecutor>.Instance);

        var request = NewRequest("implement", ["docker-opencode"]);
        await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        var snap = Assert.Single(snapshots);
        Assert.True(snap.AgentsExistedAtCallTime,
            $"AGENTS.md should exist at {snap.Context.WorkspacePath} when the OpenCode candidate runs");
    }

    [Fact]
    public async Task Evaluator_OpenCodeProvider_AgentsMirroredFromClaude_ExistsAtCallTime()
    {
        // Canonical worktree has CLAUDE.md only. An evaluator wired to
        // docker-opencode should see AGENTS.md mirrored at the canonical
        // worktree by the time RunEvaluatorAsync calls the executor.
        File.WriteAllText(Path.Combine(_canonicalWorktree, "CLAUDE.md"), "# Project context\nClaude rules.\n");
        RunGitSync(_canonicalWorktree, "add", "CLAUDE.md");
        RunGitSync(_canonicalWorktree, "commit", "-m", "add CLAUDE.md");

        var canonicalAgentsPath = Path.Combine(_canonicalWorktree, "AGENTS.md");
        var evaluatorSnapshots = new List<(AgentExecutionContext Context, bool AgentsExistedAtCallTime)>();

        // Two providers in the candidate group so we get to the evaluator
        // (single candidate skips evaluator entirely). Candidate executor is
        // a benign scripted complete; the evaluator's executor is our capture.
        var candidateExecutor = new ScriptedExecutor(AgentOutcome.COMPLETE, "ok");
        var evaluatorExecutor = new EvaluatorInitFileCapturingExecutor(
            canonicalAgentsPath, evaluatorSnapshots,
            verdictDetail: "OK\n```json\n{\"outcome\":\"COMPLETE\",\"winner_index\":0,\"scores\":[" +
                "{\"index\":0,\"score\":8,\"reasoning\":\"\"}," +
                "{\"index\":1,\"score\":7,\"reasoning\":\"\"}]}\n```");

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = candidateExecutor,
            ["docker-opencode"]   = evaluatorExecutor,
        };
        var executor = new CandidateExecutor(
            _git, new MapResolver(byProvider),
            _runStore, _boardClient, NullLogger<CandidateExecutor>.Instance);

        // Two-candidate group with an evaluator wired to docker-opencode.
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# eval prompt");
        var request = new CandidateGroupRequest(
            RunId: "run-eval-init-1",
            CardId: TestCardId,
            CardTitle: TestCardTitle,
            StateName: "Implementing",
            StepIndex: 0,
            Step: new WorkflowStep(
                Name: "implement",
                Role: "implementer",
                TaskPromptFile: null,
                TaskPrompt: "Do it.",
                Candidates: [
                    new CandidateOverride("docker-claude-cli"),
                    new CandidateOverride("docker-claude-cli"),
                ],
                Evaluator: new EvaluatorConfig(
                    Role: "evaluator",
                    TaskPrompt: "Evaluate.")),
            Role: new WorkflowRole(
                Model: "claude-sonnet-4-6",
                SystemPrompt: "you are an implementer",
                Sections: []),
            WorkflowRoles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-sonnet-4-6", "sys", []),
                ["evaluator"]   = new(
                    Model: "qwen3.6-35b-a3b-think",
                    SystemPrompt: "you are an evaluator",
                    Sections: [],
                    SystemPromptFile: null,
                    Provider: "docker-opencode"),
            },
            StateProviderParams: null,
            TaskPrompt: "Do it.",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: "discard",
            CommentsFilePath: null,
            PromptBaseDirectory: null);

        await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        var snap = Assert.Single(evaluatorSnapshots);
        Assert.True(snap.AgentsExistedAtCallTime,
            $"AGENTS.md should exist at {canonicalAgentsPath} when the OpenCode evaluator runs");

        // Regression: the mirror must NOT survive past the evaluator's
        // execution. If it does, the orchestrator's `git add . && git commit`
        // (in commit modes) catches it as a tracked change and the next
        // step's evaluator sees a spurious file in its diff prompt — the
        // exact bug the user reported in v0.0.21+ field runs.
        Assert.False(File.Exists(canonicalAgentsPath),
            "AGENTS.md should be cleaned up after the evaluator returns; " +
            "if it survives, downstream git operations capture it as a tracked change");
    }

    /// <summary>
    /// Executor that records the candidate's AgentExecutionContext along with
    /// whether AGENTS.md (the OpenCode-expected init file) existed in the
    /// candidate's workspace at the moment of the call.
    /// </summary>
    private sealed class InitFileCapturingExecutor(
        List<(AgentExecutionContext Context, bool AgentsExistedAtCallTime)> snapshots) : IAgentExecutor
    {
        private readonly object _lock = new();
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var agentsPath = Path.Combine(context.WorkspacePath, "AGENTS.md");
            var existed = File.Exists(agentsPath);
            lock (_lock) { snapshots.Add((context, existed)); }
            // Touch a marker so commit-mode promotion would have something to commit.
            File.WriteAllText(Path.Combine(context.WorkspacePath, "candidate-output.txt"), "ok\n");
            return Task.FromResult(new AgentResult(AgentOutcome.COMPLETE, "ok"));
        }
    }

    /// <summary>
    /// Same as <see cref="InitFileCapturingExecutor"/> but checks a fixed path
    /// (the canonical worktree) where the evaluator runs, and returns a
    /// scripted verdict so the candidate group flow completes.
    /// </summary>
    private sealed class EvaluatorInitFileCapturingExecutor(
        string agentsPath,
        List<(AgentExecutionContext Context, bool AgentsExistedAtCallTime)> snapshots,
        string verdictDetail) : IAgentExecutor
    {
        private readonly object _lock = new();
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var existed = File.Exists(agentsPath);
            lock (_lock) { snapshots.Add((context, existed)); }
            return Task.FromResult(new AgentResult(AgentOutcome.COMPLETE, verdictDetail));
        }
    }

    /// <summary>
    /// Executor that records every <see cref="AgentExecutionContext"/> it sees so
    /// tests can assert on what the context looked like at the executor boundary.
    /// Also captures whether the resolved CommentsFilePath existed on disk at
    /// the moment of the call (post-call cleanup of loser candidate worktrees
    /// can remove the file before assertions run).
    /// </summary>
    private sealed class CapturingExecutor(
        AgentOutcome outcome,
        string detail,
        List<(AgentExecutionContext Context, bool FileExistedAtCallTime)> capturedSnapshots) : IAgentExecutor
    {
        private readonly object _lock = new();

        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var fileExisted = context.CommentsFilePath is not null
                && File.Exists(context.CommentsFilePath);
            lock (_lock) { capturedSnapshots.Add((context, fileExisted)); }
            // Touch a file so commit_and_push has something to commit.
            var marker = Path.Combine(context.WorkspacePath, "candidate-output.txt");
            File.WriteAllText(marker, $"{detail}\n");
            return Task.FromResult(new AgentResult(outcome, detail));
        }
    }

    /// <summary>
    /// Executor that delays for a fixed duration before returning, to widen the
    /// timing window the parallelism test inspects. Touches a marker
    /// file so commit_and_push promotion has something to commit.
    /// </summary>
    private sealed class SleepingExecutor(
        TimeSpan delay, AgentOutcome outcome, string detail) : IAgentExecutor
    {
        public async Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var marker = Path.Combine(context.WorkspacePath, "candidate-output.txt");
            await File.WriteAllTextAsync(marker, $"{detail}\n", cancellationToken);
            await Task.Delay(delay, cancellationToken);
            return new AgentResult(outcome, detail);
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private CandidateExecutor BuildExecutor(
        (string Provider, AgentOutcome Outcome, string Detail) cand0,
        (string Provider, AgentOutcome Outcome, string Detail) cand1,
        AgentOutcome evaluatorOutcome,
        string evaluatorDetail)
    {
        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            [cand0.Provider] = new ScriptedExecutor(cand0.Outcome, cand0.Detail),
            [cand1.Provider] = new ScriptedExecutor(cand1.Outcome, cand1.Detail),
            // The evaluator role's provider defaults to "claude-cli" in our test config.
            ["claude-cli"]   = new ScriptedExecutor(evaluatorOutcome, evaluatorDetail),
        };
        var resolver = new MapResolver(byProvider);

        return new CandidateExecutor(
            _git,
            resolver,
            _runStore,
            _boardClient,
            NullLogger<CandidateExecutor>.Instance);
    }

    private CandidateGroupRequest NewRequest(
        string stepName,
        string[] providers,
        string gitBehavior = "commit_and_push",
        WorkflowRole? evaluatorRole = null)
    {
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# evaluator system prompt");

        return new CandidateGroupRequest(
            RunId: "run-test-1",
            CardId: "1",
            CardTitle: "Test card",
            StateName: "Implementing",
            StepIndex: 0,
            Step: new WorkflowStep(
                Name: stepName,
                Role: "implementer",
                TaskPromptFile: null,
                TaskPrompt: "Implement the thing.",
                Candidates: providers.Select(p => new CandidateOverride(p)).ToList(),
                Evaluator: new EvaluatorConfig(
                    Role: "evaluator",
                    TaskPrompt: "Evaluate the candidates.")),
            Role: new WorkflowRole(
                Model: "claude-sonnet-4-6",
                SystemPrompt: "you are an implementer",
                Sections: []),
            WorkflowRoles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-sonnet-4-6", "sys", []),
                ["evaluator"]   = evaluatorRole ?? new("claude-opus-4-6", "you are an evaluator", []),
            },
            StateProviderParams: null,
            TaskPrompt: "Implement the thing.",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: gitBehavior,
            CommentsFilePath: null,
            PromptBaseDirectory: null);
    }

    private static WorkflowRole EvaluatorRoleWithCodexFallback() =>
        new(
            Model: "claude-opus-4-6",
            SystemPrompt: "you are an evaluator",
            Sections: [],
            Provider: "claude-cli",
            Fallbacks:
            [
                new RoleFallback(
                    Provider: "docker-codex",
                    Model: "gpt-5.5",
                    ProviderParams: new Dictionary<string, string> { ["fullAuto"] = "true" }),
            ]);

    private bool BranchExists(string branchName)
    {
        var output = RunGitSyncWithOutput(_repoRoot, "branch", "--list", branchName);
        return !string.IsNullOrWhiteSpace(output);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    /// <summary>Executor that returns a fixed outcome + detail. Writes a per-call
    /// marker file into the worktree so the test can confirm it ran in the right place.
    /// Optional <paramref name="aiboardWrites"/> lets a discard-mode test populate
    /// <c>.aiboard/tasks/</c> + <c>.aiboard/updates/</c> so file-based winner
    /// promotion has something to copy. Optional <paramref name="section"/>
    /// surfaces a SectionUpdate on the AgentResult — used to lock in
    /// evaluator-commits-winner-section semantics.</summary>
    private sealed class ScriptedExecutor(
        AgentOutcome outcome,
        string detail,
        IReadOnlyDictionary<string, string>? aiboardWrites = null,
        SectionUpdate? section = null) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            // Touch a file so commit_and_push has something to commit.
            var marker = Path.Combine(context.WorkspacePath, "candidate-output.txt");
            File.WriteAllText(marker, $"{detail}\n");

            // Discard-mode candidates write their "design" into .aiboard/{tasks,updates}.
            // The relative paths in aiboardWrites are interpreted under .aiboard/.
            if (aiboardWrites is not null)
            {
                foreach (var (relative, content) in aiboardWrites)
                {
                    var target = Path.Combine(context.WorkspacePath, ".aiboard", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, content);
                }
            }

            return Task.FromResult(new AgentResult(outcome, detail, Section: section));
        }
    }

    private sealed class TrackingScriptedExecutor(
        AgentOutcome outcome,
        string detail,
        IReadOnlyDictionary<string, string>? aiboardWrites = null,
        SectionUpdate? section = null) : IAgentExecutor
    {
        private readonly ScriptedExecutor _inner = new(outcome, detail, aiboardWrites, section);
        private int _calls;
        public int Calls => _calls;

        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _inner.ExecuteAsync(context, cancellationToken);
        }
    }

    private sealed class ThrowingExecutor(Exception exception) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
            => Task.FromException<AgentResult>(exception);
    }

    private sealed class MapResolver(IReadOnlyDictionary<string, IAgentExecutor> map)
        : IAgentExecutorResolver
    {
        public IReadOnlySet<string> AvailableProviders { get; } =
            new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase);

        public IAgentExecutor Resolve(string providerKey)
        {
            if (map.TryGetValue(providerKey, out var ex)) return ex;
            throw new InvalidOperationException(
                $"Test resolver has no executor for '{providerKey}'");
        }
    }

    /// <summary>
    /// RunStore that records every call. Read-only methods return empty.
    /// Mutable lists guarded by a lock so different-provider candidate groups
    /// running in parallel can't race on List&lt;T&gt;.Add (which corrupts internal state).
    /// </summary>
    private sealed class RecordingRunStore : IRunStore
    {
        private readonly object _lock = new();
        private readonly List<StepResultRecord> _savedSteps = new();
        private readonly List<RecordedVerdict> _recordedVerdicts = new();

        public IReadOnlyList<StepResultRecord> SavedSteps
        {
            get { lock (_lock) { return _savedSteps.ToList(); } }
        }

        public IReadOnlyList<RecordedVerdict> RecordedVerdicts
        {
            get { lock (_lock) { return _recordedVerdicts.ToList(); } }
        }

        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;

        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
        {
            lock (_lock) { _savedSteps.Add(result); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);

        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);

        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateCandidateEvaluationAsync(
            string runId, Guid candidateGroupId, int candidateIndex,
            bool selected, decimal? qualityScore, string? evaluatorReasoning,
            CancellationToken ct)
        {
            lock (_lock)
            {
                _recordedVerdicts.Add(new RecordedVerdict(
                    runId, candidateGroupId, candidateIndex, selected, qualityScore, evaluatorReasoning));
            }
            return Task.CompletedTask;
        }

        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult<CacheCandidateRecord?>(null);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed record RecordedVerdict(
        string RunId,
        Guid GroupId,
        int CandidateIndex,
        bool Selected,
        decimal? QualityScore,
        string? Reasoning)
    {
        public string GroupShortHash => GroupId.ToString("N")[..8];
    }

    /// <summary>Board client that records every comment upsert. Other methods throw.</summary>
    private sealed class RecordingBoardClient : ITaskBoardClient
    {
        public List<RecordedComment> Comments { get; } = new();

        public Task UpsertAgentCommentAsync(
            string cardId, string body, string marker, CancellationToken cancellationToken)
        {
            Comments.Add(new RecordedComment(cardId, body, marker));
            return Task.CompletedTask;
        }

        public Task AppendAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken)
        {
            // Extract the leading aiboard-log / legacy marker from the body so
            // existing tests that introspect by marker still work. The
            // CandidateExecutor / AgentRunner call sites prepend the marker
            // followed by a newline.
            var extracted = ExtractLeadingMarker(commentBody);
            Comments.Add(new RecordedComment(cardId, commentBody, extracted));
            return Task.CompletedTask;
        }

        private static string ExtractLeadingMarker(string body)
        {
            const string Open = "<!--";
            const string Close = "-->";
            var trimmed = body.TrimStart();
            if (!trimmed.StartsWith(Open, StringComparison.Ordinal)) return "";
            var endIdx = trimmed.IndexOf(Close, StringComparison.Ordinal);
            if (endIdx < 0) return "";
            return trimmed[..(endIdx + Close.Length)];
        }

        public Task DeleteAgentCommentsByMarkerAsync(string cardId, string markerSubstring, CancellationToken cancellationToken)
        {
            Comments.RemoveAll(c => c.CardId == cardId && c.Body.Contains(markerSubstring, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        // Methods CandidateExecutor doesn't call — surface as test failures if hit.
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken, IReadOnlyList<string>? excludeStatuses = null)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task AssignAsync(string cardId, string username, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task UnassignAsync(string cardId, string? username, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<string> GetCurrentUserAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed record RecordedComment(string CardId, string Body, string Marker);
}
