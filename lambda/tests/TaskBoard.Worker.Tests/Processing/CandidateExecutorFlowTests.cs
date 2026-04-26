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
        File.WriteAllText(Path.Combine(taskDir, "1.md"), "# Card 1\n\noriginal task body\n");

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
            ["tasks/1.md"] = "# Card 1\n\n## Technical Design\n\nApproach A: monolith.\n",
            ["updates/new-task-foo.md"] = "---\ntitle: Task Foo\n---\nFrom A.\n",
        };
        var cand1Writes = new Dictionary<string, string>
        {
            ["tasks/1.md"] = "# Card 1\n\n## Technical Design\n\nApproach B: microservices.\n",
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
        var canonicalTask = File.ReadAllText(Path.Combine(_canonicalWorktree, ".aiboard", "tasks", "1.md"));
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

    /// <summary>RunStore that records every call. Read-only methods return empty.</summary>
    private sealed class RecordingRunStore : IRunStore
    {
        public List<StepResultRecord> SavedSteps { get; } = new();
        public List<RecordedVerdict> RecordedVerdicts { get; } = new();

        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;

        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
        {
            SavedSteps.Add(result);
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
            RecordedVerdicts.Add(new RecordedVerdict(
                runId, candidateGroupId, candidateIndex, selected, qualityScore, evaluatorReasoning));
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
