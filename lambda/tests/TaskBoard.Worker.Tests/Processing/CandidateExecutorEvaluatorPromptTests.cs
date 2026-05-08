using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins the contract that for discard-mode candidate groups, the evaluator
/// receives each candidate's <c>.aiboard/tasks/{cardId}.md</c> body (and any
/// <c>.aiboard/updates/*.md</c> files) inline in its task prompt — NOT a
/// <c>git diff</c> block (which would be empty because <c>.aiboard/</c> is
/// gitignored). Without this, the evaluator has no real material to compare.
/// </summary>
public class CandidateExecutorEvaluatorPromptTests : IDisposable
{
    // Card identity reused across tests. The filename is what TaskFileManager
    // would write — slugged from the title — so test setup mirrors real
    // production behaviour. AppendDiscardCandidateOutputs reads this exact
    // filename from each candidate worktree to feed the evaluator's prompt.
    private const string TestCardId = "1";
    private const string TestCardTitle = "Test card";
    private static readonly string TestTaskFileName =
        TaskFileManager.GetTaskFileName(TestCardId, TestCardTitle);
    private static readonly string TestTaskWriteKey = $"tasks/{TestTaskFileName}";

    private readonly string _repoRoot;
    private readonly string _canonicalWorktree;
    private readonly GitWorkspaceManager _git;

    public CandidateExecutorEvaluatorPromptTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "candidate-evalprompt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoRoot);
        InitGitRepo(_repoRoot);

        _git = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _canonicalWorktree = _git.CreateWorktreeAsync(
            _repoRoot, "aiboard/canonical-1", CancellationToken.None).GetAwaiter().GetResult();

        var taskDir = Path.Combine(_canonicalWorktree, ".aiboard", "tasks");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, TestTaskFileName), "# Card 1\n\noriginal task body\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
        try { Directory.Delete(_repoRoot + "-worktrees", recursive: true); } catch { }
    }

    [Fact]
    public async Task DiscardMode_EvaluatorPromptContainsEachCandidateTaskFileBody()
    {
        // Two candidates write distinct content into .aiboard/tasks/1.md.
        // The evaluator's task prompt must include both bodies and must NOT
        // contain a "```diff" block (which would be empty for discard mode).
        var cand0Body = "# Card 1\n\n## Technical Design\n\nApproach A: monolith with shared DB.\n";
        var cand1Body = "# Card 1\n\n## Technical Design\n\nApproach B: microservices with event bus.\n";

        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE,
            """
            Microservices wins.
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[
              {"index":0,"score":6,"reasoning":"works"},
              {"index":1,"score":8,"reasoning":"better separation"}
            ]}
            ```
            """);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE,
                "Approach A short summary",
                new Dictionary<string, string> { [TestTaskWriteKey] = cand0Body }),
            ["docker-opencode"]   = new ScriptedWriter(AgentOutcome.COMPLETE,
                "Approach B short summary",
                new Dictionary<string, string> { [TestTaskWriteKey] = cand1Body }),
            ["claude-cli"]        = capturingEvaluator,  // evaluator role default provider
        };

        var executor = BuildExecutor(byProvider);

        var request = NewRequest(
            stepName: "create_design",
            providers: ["docker-claude-cli", "docker-opencode"],
            gitBehavior: "discard");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        var prompt = capturingEvaluator.LastContext?.TaskPrompt
            ?? throw new InvalidOperationException("Evaluator was not invoked.");

        // Both candidates' file bodies appear in the evaluator prompt.
        Assert.Contains("Approach A: monolith", prompt);
        Assert.Contains("Approach B: microservices", prompt);

        // No git-diff fence — discard mode must not ship the (empty) diff path.
        Assert.DoesNotContain("```diff", prompt);

        // Sanity: prompt should anchor on the per-candidate file label so the
        // evaluator agent knows what it's looking at.
        Assert.Contains("Card body", prompt);

        // Regression guard for the v0.0.19 bug: the prompt builder previously
        // hardcoded `{cardId}.md` and missed slug-suffixed filenames, so for
        // any card with a non-empty title the evaluator saw "candidate did
        // not write a card body" for every candidate even when the file was
        // there. Both candidates wrote real bodies above; the prompt must
        // not contain the false-negative marker for either.
        Assert.DoesNotContain("(candidate did not write a card body)", prompt);

        // The prompt label must reflect the actual filename the orchestrator
        // wrote/read — i.e. include the slug — so the evaluator agent can
        // tell humans where to look.
        Assert.Contains(TestTaskFileName, prompt);
    }

    [Fact]
    public async Task DiscardMode_EmptyCardTitle_FallsBackToCardIdOnlyFilename()
    {
        // When TaskFileManager has no title to slug, GetTaskFileName falls
        // back to `{cardId}.md`. AppendDiscardCandidateOutputs must follow
        // the same rule — read the same path the orchestrator wrote — or
        // empty-title cards regress into the fixed bug from the other side.
        var canonicalTaskDir = Path.Combine(_canonicalWorktree, ".aiboard", "tasks");
        // Drop the slugged seed file from the constructor and use the
        // empty-title shape instead so this test is self-contained.
        foreach (var f in Directory.EnumerateFiles(canonicalTaskDir))
            File.Delete(f);
        File.WriteAllText(Path.Combine(canonicalTaskDir, "1.md"),
            "# Card 1\n\noriginal task body\n");

        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE,
            """
            ```json
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":7,"reasoning":"ok"}
            ]}
            ```
            """);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE, "wrote it",
                new Dictionary<string, string> { ["tasks/1.md"] = "Empty-title card body" }),
            ["claude-cli"]        = capturingEvaluator,
        };

        var executor = BuildExecutor(byProvider);
        var request = NewRequestWithEmptyTitle("create_design",
            ["docker-claude-cli"], gitBehavior: "discard");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        var prompt = capturingEvaluator.LastContext?.TaskPrompt
            ?? throw new InvalidOperationException("Evaluator was not invoked.");

        Assert.Contains("Empty-title card body", prompt);
        Assert.DoesNotContain("(candidate did not write a card body)", prompt);
    }

    [Fact]
    public async Task CrossProviderCandidate_DoesNotInheritRoleModel()
    {
        // Role provider=claude-cli, model=claude-opus-4-6.
        // Candidate provider=codex, model=null. The runtime must NOT pass the
        // role's claude-opus-4-6 to Codex CLI (which would error with "model
        // not supported when using Codex with a ChatGPT account"). Codex
        // candidate's executor must receive Model = null.
        var capturingCodex = new CapturingExecutor(AgentOutcome.COMPLETE, "codex did its thing");

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE, "claude side",
                new Dictionary<string, string> { [TestTaskWriteKey] = "Claude version" }),
            ["codex"]             = capturingCodex,
            ["claude-cli"]        = new CapturingExecutor(AgentOutcome.COMPLETE,
                """
                ```json
                {"outcome":"COMPLETE","winner_index":0,"scores":[
                  {"index":0,"score":7,"reasoning":"ok"},
                  {"index":1,"score":7,"reasoning":"ok"}
                ]}
                ```
                """),
        };

        var executor = BuildExecutor(byProvider);

        // Use a request whose role.Provider = "claude-cli" (set up via
        // WorkflowRoles + Role params in NewRequest below) and a candidate
        // whose Provider = "codex" — note this exercises the cross-provider
        // Model-inheritance path in CandidateExecutor.
        var request = NewCrossProviderRequest();

        await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        var ctx = capturingCodex.LastContext
            ?? throw new InvalidOperationException("Codex candidate not invoked.");
        Assert.Null(ctx.Model);
    }

    private CandidateGroupRequest NewCrossProviderRequest()
    {
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# system");

        return new CandidateGroupRequest(
            RunId: "run-xprov-1",
            CardId: "1",
            CardTitle: "Test card",
            StateName: "Implementing",
            StepIndex: 0,
            Step: new WorkflowStep(
                Name: "implement",
                Role: "implementer",
                TaskPromptFile: null,
                TaskPrompt: "Implement.",
                Candidates:
                [
                    new CandidateOverride("docker-claude-cli"),  // matches role.Provider — inherits model
                    new CandidateOverride("codex"),              // differs — model must be null
                ],
                Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge")),
            Role: new WorkflowRole(
                Model: "claude-opus-4-6",
                SystemPrompt: "you are an implementer",
                Sections: [],
                Provider: "claude-cli"),  // intentional: role uses claude-cli
            WorkflowRoles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
                ["evaluator"]   = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
            },
            StateProviderParams: null,
            TaskPrompt: "Implement.",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: "discard",
            CommentsFilePath: null,
            PromptBaseDirectory: null);
    }

    [Fact]
    public async Task EvaluatorContext_CarriesEvaluatorSchemaOverride()
    {
        // The evaluator's AgentExecutionContext must set SchemaOverride to
        // EvaluatorOutcomeSchema (or the OpenAI variant for codex). Without
        // this, the LLM uses the generic outcome schema and can omit
        // winner_index — the v0.0.15 KvA bug.
        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE,
            """
            ```json
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":7,"reasoning":"ok"},
              {"index":1,"score":6,"reasoning":"meh"}
            ]}
            ```
            """);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE, "A",
                new Dictionary<string, string> { [TestTaskWriteKey] = "A body" }),
            ["docker-opencode"]   = new ScriptedWriter(AgentOutcome.COMPLETE, "B",
                new Dictionary<string, string> { [TestTaskWriteKey] = "B body" }),
            ["claude-cli"]        = capturingEvaluator,
        };

        var executor = BuildExecutor(byProvider);
        var request = NewRequest("create_design",
            ["docker-claude-cli", "docker-opencode"], gitBehavior: "discard");

        await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        var ctx = capturingEvaluator.LastContext
            ?? throw new InvalidOperationException("Evaluator was not invoked.");
        Assert.NotNull(ctx.SchemaOverride);
        Assert.Equal(AgentSchemas.EvaluatorOutcomeSchema, ctx.SchemaOverride);
    }

    [Fact]
    public async Task EvaluatorReturnsCompleteWithoutWinnerAnywhere_OverridesToError()
    {
        // Schema-violation defense: if the LLM bypasses the schema AND wrote no
        // winner-naming prose (no "Candidate N wins", no scoreboard table marked
        // Winner), the orchestrator must NOT silently fall through to "no winner;
        // clean up". Surface as ERROR so the run fails loudly. The v0.0.18 prose
        // fallback (TryExtractWinnerFromProse) catches the common case where
        // the verdict is in markdown; this test uses prose that genuinely names
        // no winner so the override still fires.
        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE,
            """
            Both candidates produced acceptable work. The differences are minor.
            I cannot pick decisively without more context.
            ```json
            {"outcome":"COMPLETE","detail":"Both look fine, no clear preference"}
            ```
            """);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE, "A",
                new Dictionary<string, string> { [TestTaskWriteKey] = "A body" }),
            ["docker-opencode"]   = new ScriptedWriter(AgentOutcome.COMPLETE, "B",
                new Dictionary<string, string> { [TestTaskWriteKey] = "B body" }),
            ["claude-cli"]        = capturingEvaluator,
        };

        var executor = BuildExecutor(byProvider);
        var request = NewRequest("create_design",
            ["docker-claude-cli", "docker-opencode"], gitBehavior: "discard");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("did not include a winner_index", result.Detail);

        // Canonical worktree's task body should NOT have been promoted from
        // either candidate (no winner picked).
        var canonicalTask = File.ReadAllText(
            Path.Combine(_canonicalWorktree, ".aiboard", "tasks", TestTaskFileName));
        Assert.Equal("# Card 1\n\noriginal task body\n", canonicalTask.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task EvaluatorReturnsCompleteWithProseWinner_RecoversFromMarkdown()
    {
        // v0.0.18 prose fallback. The model wrote a clear verdict in markdown
        // ("**Verdict: Candidate 1 wins**" + scoreboard with "**Winner.**") but
        // skipped the structured winner_index field — the exact shape KvA card
        // #3's v0.0.17 retry produced. Without the prose fallback this would
        // override to ERROR and lose the verdict; with it, candidate 1 is
        // recovered and promoted.
        var verdictMarkdown =
            "## Verdict: Candidate 1 wins\n\n" +
            "Candidate 1 had the better implementation map.\n\n" +
            "| # | Provider | Score | Notes |\n" +
            "|---|----------|-------|-------|\n" +
            "| 0 | claude-opus | 6 | Good but generic |\n" +
            "| 1 | claude-sonnet | **7** | **Winner.** Best implementation map |\n";

        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE, verdictMarkdown);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE, "A",
                new Dictionary<string, string> { [TestTaskWriteKey] = "A body" }),
            ["docker-opencode"]   = new ScriptedWriter(AgentOutcome.COMPLETE, "B",
                new Dictionary<string, string> { [TestTaskWriteKey] = "B body" }),
            ["claude-cli"]        = capturingEvaluator,
        };

        var executor = BuildExecutor(byProvider);
        var request = NewRequest("create_design",
            ["docker-claude-cli", "docker-opencode"], gitBehavior: "discard");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        // Prose recovery: outcome stays COMPLETE; candidate 1's body is promoted.
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        var canonicalTask = File.ReadAllText(
            Path.Combine(_canonicalWorktree, ".aiboard", "tasks", TestTaskFileName));
        Assert.Equal("B body", canonicalTask.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task DiscardMode_EvaluatorPromptIncludesUpdatesFiles()
    {
        // One candidate writes an updates file (child-card request); the
        // evaluator must see the filename + content.
        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE,
            """
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[
              {"index":0,"score":5,"reasoning":"no decomposition"},
              {"index":1,"score":8,"reasoning":"clear breakdown"}
            ]}
            ```
            """);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedWriter(AgentOutcome.COMPLETE,
                "no decomposition",
                new Dictionary<string, string> { [TestTaskWriteKey] = "# Card 1\nNo split.\n" }),
            ["docker-opencode"]   = new ScriptedWriter(AgentOutcome.COMPLETE,
                "split into subtasks",
                new Dictionary<string, string>
                {
                    [TestTaskWriteKey] = "# Card 1\nDecomposed.\n",
                    ["updates/new-task-alpha.md"] = "---\ntitle: Alpha subtask\n---\nBody for alpha.\n",
                }),
            ["claude-cli"]        = capturingEvaluator,
        };

        var executor = BuildExecutor(byProvider);

        var request = NewRequest(
            stepName: "generate_tasks",
            providers: ["docker-claude-cli", "docker-opencode"],
            gitBehavior: "discard");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        var prompt = capturingEvaluator.LastContext?.TaskPrompt
            ?? throw new InvalidOperationException("Evaluator was not invoked.");

        Assert.Contains("new-task-alpha.md", prompt);
        Assert.Contains("Body for alpha", prompt);
    }

    [Fact]
    public async Task CommitMode_EvaluatorPromptContainsEachCandidateDiff_NotEmpty()
    {
        // Regression guard for the v0.0.20 KvA card #3 bug. In commit-mode
        // candidate groups, the orchestrator commits each candidate's work
        // onto its own branch BEFORE the evaluator runs. So `git diff HEAD`
        // in the candidate worktree returns empty even though real work was
        // committed. The fix: diff against the canonical SHA captured before
        // candidates spawned. Without it the evaluator sees empty diff blocks
        // and (correctly) concludes "no candidate produced changes".
        //
        // Setup: two candidates write distinct tracked files (outside the
        // gitignored .aiboard/) so the orchestrator's `git add . && git
        // commit` captures them. Assert each candidate's diff section in the
        // evaluator's prompt names its unique file and shows non-empty
        // content.
        var capturingEvaluator = new CapturingExecutor(AgentOutcome.COMPLETE,
            """
            ```json
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":7,"reasoning":"works"},
              {"index":1,"score":7,"reasoning":"also works"}
            ]}
            ```
            """);

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new TrackedFileWriter(
                AgentOutcome.COMPLETE,
                "Claude impl",
                fileName: "claude_impl.txt",
                fileContent: "MARKER_CLAUDE_DIFF\n"),
            ["docker-opencode"]   = new TrackedFileWriter(
                AgentOutcome.COMPLETE,
                "OpenCode impl",
                fileName: "opencode_impl.txt",
                fileContent: "MARKER_OPENCODE_DIFF\n"),
            ["claude-cli"]        = capturingEvaluator,  // evaluator role default provider
        };

        var executor = BuildExecutor(byProvider);

        var request = NewRequest(
            stepName: "implement",
            providers: ["docker-claude-cli", "docker-opencode"],
            gitBehavior: "commit_and_push");

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        var prompt = capturingEvaluator.LastContext?.TaskPrompt
            ?? throw new InvalidOperationException("Evaluator was not invoked.");

        // The diff fence is present (commit-mode evaluator must include git diffs).
        Assert.Contains("```diff", prompt);

        // Each candidate's tracked file appears in its diff section. The diff
        // header line `+++ b/{file}` is what `git diff <baseRef>` produces for
        // a new file vs. that base; if the bug is back, those lines are absent.
        Assert.Contains("claude_impl.txt", prompt);
        Assert.Contains("opencode_impl.txt", prompt);

        // Each candidate's actual file content appears in its diff (the
        // strongest signal that the diff isn't empty). Without the fix, the
        // diff section between ```diff fences is just whitespace.
        Assert.Contains("MARKER_CLAUDE_DIFF", prompt);
        Assert.Contains("MARKER_OPENCODE_DIFF", prompt);

        // Per-candidate partitioning: candidate 0's section must contain
        // ONLY its own marker, candidate 1's section must contain ONLY its
        // own. Without partitioning, a regression that diffs every candidate
        // against the wrong base could leak the OTHER candidate's content
        // into both sections (e.g. if we accidentally diffed canonical's
        // worktree instead of the candidate's). We slice the prompt at the
        // `### Candidate N` headings the prompt builder emits.
        var cand0Section = ExtractCandidateSection(prompt, candidateIndex: 0);
        var cand1Section = ExtractCandidateSection(prompt, candidateIndex: 1);
        Assert.Contains("MARKER_CLAUDE_DIFF", cand0Section);
        Assert.DoesNotContain("MARKER_OPENCODE_DIFF", cand0Section);
        Assert.Contains("MARKER_OPENCODE_DIFF", cand1Section);
        Assert.DoesNotContain("MARKER_CLAUDE_DIFF", cand1Section);
    }

    private static string ExtractCandidateSection(string prompt, int candidateIndex)
    {
        var heading = $"### Candidate {candidateIndex} ";
        var start = prompt.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Heading not found: {heading}");

        var nextHeading = $"### Candidate {candidateIndex + 1} ";
        var end = prompt.IndexOf(nextHeading, start, StringComparison.Ordinal);
        // If there's no next candidate, slice to the start of the response
        // contract block (which always trails the candidates).
        if (end < 0)
            end = prompt.IndexOf("## Response Contract", start, StringComparison.Ordinal);
        if (end < 0)
            end = prompt.Length;

        return prompt[start..end];
    }

    // RerunPreambleBuilder + its evaluator-prompt integration deleted in
    // rerun redesign Round 4. Cache decisions now happen at AgentRunner before
    // any candidate spawn — the evaluator never needs the "candidates may have
    // confirmed prior" framing because cache hits skip the slot entirely.

    // ── Builders ─────────────────────────────────────────────────────────────

    private CandidateExecutor BuildExecutor(IReadOnlyDictionary<string, IAgentExecutor> byProvider)
        => new(_git,
               new MapResolver(byProvider),
               new NullRunStore(),
               new NullBoardClient(),
               NullLogger<CandidateExecutor>.Instance);

    private CandidateGroupRequest NewRequest(
        string stepName,
        string[] providers,
        string gitBehavior)
        => BuildRequest(stepName, providers, gitBehavior, cardTitle: TestCardTitle);

    private CandidateGroupRequest NewRequestWithEmptyTitle(
        string stepName,
        string[] providers,
        string gitBehavior)
        => BuildRequest(stepName, providers, gitBehavior, cardTitle: "");

    private CandidateGroupRequest BuildRequest(
        string stepName,
        string[] providers,
        string gitBehavior,
        string cardTitle)
    {
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# evaluator system prompt");

        return new CandidateGroupRequest(
            RunId: "run-evalprompt-1",
            CardId: TestCardId,
            CardTitle: cardTitle,
            StateName: "Designing",
            StepIndex: 0,
            Step: new WorkflowStep(
                Name: stepName,
                Role: "designer",
                TaskPromptFile: null,
                TaskPrompt: "Design the thing.",
                Candidates: providers.Select(p => new CandidateOverride(p)).ToList(),
                Evaluator: new EvaluatorConfig(
                    Role: "evaluator",
                    TaskPrompt: "Evaluate the candidates.")),
            Role: new WorkflowRole(
                Model: "claude-opus-4-6",
                SystemPrompt: "you are a designer",
                Sections: []),
            WorkflowRoles: new Dictionary<string, WorkflowRole>
            {
                ["designer"]  = new("claude-opus-4-6", "sys", []),
                ["evaluator"] = new("claude-opus-4-6", "you are an evaluator", []),
            },
            StateProviderParams: null,
            TaskPrompt: "Design the thing.",
            SystemPromptFilePath: systemPromptFile,
            WorktreePath: _canonicalWorktree,
            RepoPath: _repoRoot,
            GitBehavior: gitBehavior,
            CommentsFilePath: null,
            PromptBaseDirectory: null);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class ScriptedWriter(
        AgentOutcome outcome,
        string detail,
        IReadOnlyDictionary<string, string> aiboardWrites) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            foreach (var (relative, content) in aiboardWrites)
            {
                var target = Path.Combine(context.WorkspacePath, ".aiboard", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
            }
            return Task.FromResult(new AgentResult(outcome, detail));
        }
    }

    /// <summary>
    /// Writes a single tracked file (outside the gitignored <c>.aiboard/</c>)
    /// at the worktree root so commit-mode tests have something the
    /// orchestrator's <c>git add . &amp;&amp; git commit</c> will actually
    /// capture and the evaluator's diff query has real material to surface.
    /// </summary>
    private sealed class TrackedFileWriter(
        AgentOutcome outcome,
        string detail,
        string fileName,
        string fileContent) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var path = Path.Combine(context.WorkspacePath, fileName);
            File.WriteAllText(path, fileContent);
            return Task.FromResult(new AgentResult(outcome, detail));
        }
    }

    private sealed class CapturingExecutor(AgentOutcome outcome, string detail) : IAgentExecutor
    {
        public AgentExecutionContext? LastContext { get; private set; }

        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            LastContext = context;
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
            throw new InvalidOperationException($"Test resolver has no executor for '{providerKey}'");
        }
    }

    private sealed class NullRunStore : IRunStore
    {
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
        public Task UpdateCandidateEvaluationAsync(
            string runId, Guid candidateGroupId, int candidateIndex,
            bool selected, decimal? qualityScore, string? evaluatorReasoning,
            CancellationToken ct) => Task.CompletedTask;
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult<CacheCandidateRecord?>(null);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullBoardClient : ITaskBoardClient
    {
        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task AppendAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task DeleteAgentCommentsByMarkerAsync(string cardId, string markerSubstring, CancellationToken cancellationToken)
            => Task.CompletedTask;
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
}
