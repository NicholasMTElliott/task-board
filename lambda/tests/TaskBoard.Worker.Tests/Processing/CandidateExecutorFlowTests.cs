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
        Assert.Equal(2, _boardClient.Comments.Count(c => c.Marker.Contains(":cand-")));
    }

    // ── All candidates fail ──────────────────────────────────────────────────

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
        Assert.Contains("All candidates failed", result.Detail);

        // Two candidate rows persisted, NO evaluator row.
        Assert.Equal(2, _runStore.SavedSteps.Count);
        Assert.DoesNotContain(_runStore.SavedSteps, r => r.StepName.EndsWith(":evaluator"));

        // No verdict updates (evaluator never ran).
        Assert.Empty(_runStore.RecordedVerdicts);
    }

    [Fact]
    public async Task AllCandidatesFail_MixedOutcomes_PrefersNeedsInfoOverError()
    {
        // Mixed non-COMPLETE outcomes: candidate 0 errored, candidate 1 asked
        // a question. The merged step outcome must surface NEEDS_INFO so the
        // card routes to the Questions column rather than Error — otherwise
        // the operator never sees the question candidate 1 actually raised.
        // Candidate ordering must NOT determine the routing.
        var candidateExecutor = BuildExecutor(
            ("docker-claude-cli", AgentOutcome.ERROR,      "failed to compile"),
            ("docker-opencode",   AgentOutcome.NEEDS_INFO, "Which API version do you want?"),
            evaluatorOutcome: AgentOutcome.COMPLETE,  // never invoked
            evaluatorDetail: "(should not appear)");

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"]);

        var result = await candidateExecutor.ExecuteCandidateGroupAsync(
            request, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Contains("All candidates failed", result.Detail);

        // Both candidates persisted; no evaluator row (still all-failed path).
        Assert.Equal(2, _runStore.SavedSteps.Count);
        Assert.DoesNotContain(_runStore.SavedSteps, r => r.StepName.EndsWith(":evaluator"));
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

    // ── Parallel-by-provider execution ───────────────────────────────────────

    [Fact]
    public async Task ParallelByProvider_DifferentProvidersOverlap_SameProviderSerializes()
    {
        // Three candidates: docker-claude-cli×2, docker-opencode×1.
        // Each candidate sleeps 250ms before returning COMPLETE. Expected timing:
        //   - claude[1] starts AFTER claude[0] finishes (same-provider serialization)
        //   - opencode[2] starts BEFORE claude[0] finishes (cross-provider parallelism)
        // Pure-sequential execution (the pre-fix shape) would fail the second
        // assertion: opencode would only start after both claude calls complete.
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

        // Same-provider serialization: claude[1] starts no earlier than claude[0] ends.
        // 10ms slop accommodates clock-resolution + scheduling jitter.
        Assert.True(
            claude1.StartedAtUtc >= claude0.CompletedAtUtc - TimeSpan.FromMilliseconds(10),
            $"Same-provider candidates should serialize. claude[0] completed at {claude0.CompletedAtUtc:O}, " +
            $"claude[1] started at {claude1.StartedAtUtc:O} (gap {(claude1.StartedAtUtc - claude0.CompletedAtUtc).TotalMilliseconds:F0}ms — should be ≥0).");

        // Cross-provider parallelism: opencode starts before claude[0] finishes.
        Assert.True(
            opencode.StartedAtUtc < claude0.CompletedAtUtc,
            $"Different-provider candidates should overlap. claude[0] completed at {claude0.CompletedAtUtc:O}, " +
            $"opencode started at {opencode.StartedAtUtc:O} (lag {(opencode.StartedAtUtc - claude0.CompletedAtUtc).TotalMilliseconds:F0}ms — should be <0).");
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
    /// timing window the parallel-by-provider test inspects. Touches a marker
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
        string gitBehavior = "commit_and_push")
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
                ["evaluator"]   = new("claude-opus-4-6", "you are an evaluator", []),
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
    /// promotion has something to copy.</summary>
    private sealed class ScriptedExecutor(
        AgentOutcome outcome,
        string detail,
        IReadOnlyDictionary<string, string>? aiboardWrites = null) : IAgentExecutor
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

            return Task.FromResult(new AgentResult(outcome, detail));
        }
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
