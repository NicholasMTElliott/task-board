using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Round-9 Group B: pins the invariants that ensure dismissed candidate
/// work does NOT leak downstream — neither into the card body, nor into the
/// step-history context file, nor into the next step's / state's prompts.
/// The winning candidate's section_update is the sole channel by which
/// design output reaches downstream consumers.
///
/// Three tests:
/// <list type="bullet">
///   <item><b>B1</b> — three candidates with distinct section content;
///   only the winner's content reaches <see cref="ITaskBoardClient.UpdateCardBodyAsync"/>.</item>
///   <item><b>B3</b> — when the run store contains a mix of canonical,
///   per-candidate, and <c>:evaluator</c> rows, <c>.aiboard/context/step-history.md</c>
///   excludes the candidate and evaluator rows.</item>
///   <item><b>B5</b> (cross-state integration) — Design with a candidate group
///   followed by Implementation: the implementation step's task prompt sees
///   the winner's section content and not the loser's.</item>
/// </list>
/// </summary>
public class CandidateLoserNonLeakageTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _canonicalWorktree;
    private readonly GitWorkspaceManager _git;

    private const string TestCardId = "1";
    private const string TestCardTitle = "Test card";
    private const string BoardId = "board-1";

    public CandidateLoserNonLeakageTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "loser-leak-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoRoot);
        InitGitRepo(_repoRoot);

        _git = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _canonicalWorktree = _git.CreateWorktreeAsync(
            _repoRoot, "aiboard/canonical-1", CancellationToken.None).GetAwaiter().GetResult();

        var taskDir = Path.Combine(_canonicalWorktree, ".aiboard", "tasks");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(
            Path.Combine(taskDir, TaskFileManager.GetTaskFileName(TestCardId, TestCardTitle)),
            "# Card 1\n\noriginal task body\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
        try { Directory.Delete(_repoRoot + "-worktrees", recursive: true); } catch { }
    }

    // ── B1: winner section in result, losers not ────────────────────────────

    [Fact]
    public async Task ThreeCandidates_OnlyWinnerSection_AppearsInSlotResult()
    {
        // Three candidates each emit a distinct, easily-grep'd section content.
        // Evaluator picks index 1 (the middle candidate). The SlotResult that
        // CandidateExecutor returns to AgentRunner must carry only candidate 1's
        // content — neither candidate 0's nor candidate 2's may leak through.
        // Pre-fix regression for this would be e.g. promoting the wrong
        // candidate's section, or merging multiple candidates' sections.

        const string Cand0Section = "APPROACH-A-CONTENT-MARKER (loser, monolith)";
        const string Cand1Section = "APPROACH-B-CONTENT-MARKER (winner, microservices)";
        const string Cand2Section = "APPROACH-C-CONTENT-MARKER (loser, modular monolith)";

        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, "Approach A",
                section: new SectionUpdate(SectionUpdateStrategy.Replace, Cand0Section)),
            ["docker-opencode"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, "Approach B",
                section: new SectionUpdate(SectionUpdateStrategy.Replace, Cand1Section)),
            ["docker-codex"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, "Approach C",
                section: new SectionUpdate(SectionUpdateStrategy.Replace, Cand2Section)),
            // Evaluator picks index 1 explicitly.
            ["claude-cli"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE,
                """
                Microservices wins the trade-off matrix.
                ```json
                {"outcome":"COMPLETE","winner_index":1,"scores":[
                  {"index":0,"score":6,"reasoning":"works but couples concerns"},
                  {"index":1,"score":8.5,"reasoning":"clean separation"},
                  {"index":2,"score":7,"reasoning":"middle ground"}
                ]}
                ```
                """,
                section: new SectionUpdate(SectionUpdateStrategy.Leave)),
        };

        var resolver = new MapResolver(byProvider);
        var runStore = new ScriptedRunStore();
        var boardClient = new ScriptedBoardClient();
        var executor = new CandidateExecutor(
            _git, resolver, runStore, boardClient,
            NullLogger<CandidateExecutor>.Instance);

        var request = NewCandidateRequest(
            stepName: "create_design",
            providers: ["docker-claude-cli", "docker-opencode", "docker-codex"]);

        var result = await executor.ExecuteCandidateGroupAsync(request, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(result.Section);
        Assert.Equal(SectionUpdateStrategy.Replace, result.Section!.Strategy);

        // Winner content present.
        Assert.Equal(Cand1Section, result.Section.Content);

        // Loser content explicitly absent — guards against any future
        // refactor that accidentally concatenates candidates' sections, or
        // promotes the wrong index, or surfaces the evaluator's "leave" as
        // "use my (empty) section".
        Assert.DoesNotContain("APPROACH-A-CONTENT-MARKER", result.Section.Content);
        Assert.DoesNotContain("APPROACH-C-CONTENT-MARKER", result.Section.Content);
    }

    // ── B3: step-history file omits candidate / evaluator rows ──────────────

    [Fact]
    public async Task StepHistoryFile_OmitsCandidateAndEvaluatorRows_OnlyCanonicalSurvives()
    {
        // .aiboard/context/step-history.md is read by the next agent run for
        // cross-run context. AgentRunner.WritePriorStepContextAsync filters
        // via ShouldIncludeStepResultInAgentContext (AgentRunner.cs:3264) —
        // candidate/evaluator rows must never appear so the next agent
        // doesn't see (and potentially be influenced by) dismissed work.
        //
        // We seed the run store directly with a mix of canonical + candidate
        // (group_id != null) + evaluator-suffixed rows under a synthetic
        // prior run id, then run AgentRunner. The new run's worktree's
        // .aiboard/context/step-history.md must contain canonical content
        // and EXCLUDE the loser-only and evaluator-only markers.

        const string CanonicalSummary = "CANONICAL-PRIOR-OUTPUT-MARKER (winner promoted)";
        const string LoserCandidateSummary = "LOSER-CANDIDATE-OUTPUT-MARKER (must not appear in step-history.md)";
        const string EvaluatorReasoning = "EVALUATOR-REASONING-MARKER (must not appear in step-history.md)";

        // Set up a normal, single-step state. The agent will run once (cache
        // miss because store has nothing for the current state's step name)
        // and the orchestrator will write step-history.md before invoking it.
        // We capture the step-history.md content from inside the executor —
        // the worktree is cleaned up post-run for discard-mode states, so
        // reading the file after `runner.ExecuteAsync` returns wouldn't work.
        string? capturedStepHistory = null;
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<AgentExecutionContext>();
                var stepHistoryPath = Path.Combine(
                    ctx.WorkspacePath, ".aiboard", "context", "step-history.md");
                if (File.Exists(stepHistoryPath))
                    capturedStepHistory = File.ReadAllText(stepHistoryPath);
                return new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var store = new SeededReplayStore();
        SeedStepHistoryRows(store, CanonicalSummary, LoserCandidateSummary, EvaluatorReasoning);

        // Build a workflow whose state is "Design" (NOT "PriorState"), so the
        // seeded prior rows are loaded as cross-run history but don't satisfy
        // the cache lookup for the current step.
        var workflow = BuildSingleStepConfig();
        var (boardClient, _) = SetUpBoardClient();
        var taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);

        var runner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(executor),
            taskFileManager,
            _git,
            workflow.Normalised(),
            new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, workflow.Normalised(), new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            store,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);

        var runResult = await runner.ExecuteAsync(TestCardId, BoardId, _repoRoot, CancellationToken.None);
        Assert.Equal(AgentOutcome.COMPLETE, runResult.Outcome);

        // The executor captured step-history.md content during the agent
        // invocation (just after WritePriorStepContextAsync wrote it, but
        // before the discard-mode worktree cleanup deleted it).
        Assert.NotNull(capturedStepHistory);
        var stepHistoryContent = capturedStepHistory!;

        // Canonical row content must appear.
        Assert.Contains(CanonicalSummary, stepHistoryContent);

        // Per-candidate rows (group_id != null, suffixed step_name) must NOT
        // appear — the operator's prior reasoning was framed by the WINNER,
        // not the dismissed candidates. Surfacing loser context to a future
        // agent muddies its reasoning.
        Assert.DoesNotContain(LoserCandidateSummary, stepHistoryContent);

        // Evaluator-suffixed rows are pure meta — what the evaluator thought —
        // not durable design output. Same exclusion.
        Assert.DoesNotContain(EvaluatorReasoning, stepHistoryContent);

        // Defensive structural assertions: the step-history file shouldn't
        // mention the candidate-suffixed step names at all.
        Assert.DoesNotContain(":cand-", stepHistoryContent);
        Assert.DoesNotContain(":evaluator", stepHistoryContent);
    }

    // ── B5 (integration): cross-state design → implementation ───────────────

    [Fact]
    public async Task CrossState_DesignWithCandidates_ImplementationDoesNotSeeLoserDesigns()
    {
        // Two-state pipeline: Design (with a 2-candidate group) → Implementation.
        // Run state Design first; capture which candidate won. Move card to
        // Implementation column. Run state Implementation. Inspect the
        // implementation agent's visible workspace: it must contain the
        // winning candidate's section content and NOT the loser's.
        //
        // The plumbing under test:
        //   1. CandidateExecutor promotes the winner's section into the card body
        //   2. AgentRunner writes the post-section card body via UpdateCardBodyAsync
        //   3. The Implementation step reads the post-Design card body from
        //      the task file and agent-visible .aiboard context
        //   4. The Implementation step's step-history.md context surfaces only
        //      the canonical create_design row, not per-candidate rows.

        const string WinnerSection = "WINNER-DESIGN-CONTENT (microservices, candidate 1)";
        const string LoserSection = "LOSER-DESIGN-CONTENT (monolith, candidate 0)";
        const string WinnerDetail = "WINNER-DETAIL-MARKER design output";
        const string LoserDetail = "LOSER-DETAIL-MARKER design output";
        const string EvaluatorLoserReasoning = "EVALUATOR-LOSER-REASONING-MARKER";

        // Card body — mutates as steps run.
        var cardBody = "Operator requirements for X.";
        var currentColumn = "list-design";
        var commentsList = new List<CardComment>();

        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(_ => new List<BoardCard>
            {
                new(TestCardId, TestCardTitle, cardBody, currentColumn),
            });
        boardClient.GetCardCommentsAsync(TestCardId, Arg.Any<CancellationToken>())
            .Returns(_ => commentsList.ToList());
        boardClient.UpdateCardBodyAsync(TestCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => cardBody = ci.ArgAt<string>(1));
        boardClient.AppendAgentCommentAsync(TestCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => commentsList.Add(new CardComment(
                "bot", ci.ArgAt<string>(1), DateTimeOffset.UtcNow.AddSeconds(commentsList.Count))));
        // Simulate the operator manually moving the card to "list-impl" between
        // states by switching `currentColumn` after Design completes.
        boardClient.MoveCardToColumnAsync(TestCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Capture two things from inside the implementer's executor call:
        //   - The contents of the task file (.aiboard/tasks/{slug}.md) the
        //     agent reads to learn the card's design.
        //   - The contents of step-history.md (cross-run context) so we can
        //     assert losers don't appear there either.
        // We capture inside the executor because the discard-mode worktree
        // is cleaned up post-run.
        AgentExecutionContext? capturedImplContext = null;
        string? capturedImplTaskFile = null;
        string? capturedImplStepHistory = null;

        // Build the resolver: design candidates use distinct providers; the
        // evaluator and the implementation agent share `claude-cli`.
        // Each design candidate emits a distinct section. The evaluator
        // picks index 1. The implementation agent records the context it
        // received.
        var byProvider = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, LoserDetail,
                section: new SectionUpdate(SectionUpdateStrategy.Replace, LoserSection)),
            ["docker-opencode"] = new ScriptedExecutor(
                AgentOutcome.COMPLETE, WinnerDetail,
                section: new SectionUpdate(SectionUpdateStrategy.Replace, WinnerSection)),
            // claude-cli serves both the evaluator (during Design) and the
            // implementation agent (during Implementation). We disambiguate
            // by call sequence: first call is evaluator, subsequent calls
            // are the implementation agent.
            ["claude-cli"] = new SequencedExecutor(
                first: new AgentResult(AgentOutcome.COMPLETE,
                    """
                    Winner is candidate 1.
                    ```json
                    {"outcome":"COMPLETE","winner_index":1,"scores":[
                      {"index":0,"score":6,"reasoning":"EVALUATOR-LOSER-REASONING-MARKER"},
                      {"index":1,"score":9,"reasoning":"winner"}
                    ]}
                    ```
                    """,
                    Section: new SectionUpdate(SectionUpdateStrategy.Leave)),
                rest: new AgentResult(AgentOutcome.COMPLETE, "implementation done"),
                onCall: ctx =>
                {
                    // Capture the implementation agent's context (the second
                    // claude-cli call — the first was the design's evaluator).
                    // We disambiguate by the prompt prefix: only the impl prompt
                    // starts with "Implementation prompt for".
                    if (capturedImplContext is null && ctx.TaskPrompt.Contains("Implementation prompt for"))
                    {
                        capturedImplContext = ctx;
                        // Snapshot every file under .aiboard/ at this moment
                        // — the agent's view of its workspace, before the
                        // discard-mode worktree gets cleaned up post-run.
                        var aiboardDir = Path.Combine(ctx.WorkspacePath, ".aiboard");
                        if (Directory.Exists(aiboardDir))
                        {
                            // Concatenate every .md under .aiboard/ into one
                            // string so the assertions can grep across all
                            // agent-visible context (task file, step-history,
                            // comments, etc.) — the agent could read any of
                            // these and the loser content must not be in any.
                            var sb = new System.Text.StringBuilder();
                            foreach (var f in Directory.EnumerateFiles(aiboardDir, "*.md", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                            {
                                sb.AppendLine($"--- {Path.GetRelativePath(aiboardDir, f)} ---");
                                sb.AppendLine(File.ReadAllText(f));
                                sb.AppendLine();
                            }
                            capturedImplTaskFile = sb.ToString();
                        }
                        var stepHistoryPath = Path.Combine(
                            ctx.WorkspacePath, ".aiboard", "context", "step-history.md");
                        if (File.Exists(stepHistoryPath))
                            capturedImplStepHistory = File.ReadAllText(stepHistoryPath);
                    }
                }),
        };
        var resolver = new MapResolver(byProvider);

        var runStore = new SeededReplayStore();
        var taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        var workflow = BuildCrossStateConfig();

        // Real CandidateExecutor for the design state; AgentRunner uses it
        // for slot-driven steps.
        var candidateExecutor = new CandidateExecutor(
            _git, resolver, runStore,
            boardClient,
            NullLogger<CandidateExecutor>.Instance);

        var runner = new AgentRunner(
            boardClient,
            resolver,
            taskFileManager,
            _git,
            workflow.Normalised(),
            new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, workflow.Normalised(), new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            runStore,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance,
            candidateExecutor: candidateExecutor);

        // Phase 1: run Design (with candidates).
        await runner.ExecuteAsync(TestCardId, BoardId, _repoRoot, CancellationToken.None);

        // Sanity: the card body now contains the winner's section.
        Assert.Contains(WinnerSection, cardBody);
        Assert.DoesNotContain(LoserSection, cardBody);

        // Phase 2: simulate the operator approving the design and moving the
        // card to the Implementation column.
        currentColumn = "list-impl";

        // Run state Implementation. The implementation agent should see the
        // post-design card body through its task file and agent-visible
        // .aiboard context.
        await runner.ExecuteAsync(TestCardId, BoardId, _repoRoot, CancellationToken.None);

        Assert.True(capturedImplContext is not null,
            $"Implementation agent was never invoked. cardBody at end of phase 2: {cardBody}");

        // Headline: the implementation agent's worktree (.aiboard/...
        // surface area) contains the winner's design content and NOT the
        // loser's. We aggregated every .md file under .aiboard/ into one
        // string so the assertion is robust to where exactly the body
        // is surfaced (task file, comments file, step-history, etc.).
        Assert.True(capturedImplTaskFile is not null,
            "Implementation agent's .aiboard/ snapshot was not captured — " +
            "the executor's onCall didn't fire as expected.");
        Assert.Contains(WinnerSection, capturedImplTaskFile!);
        Assert.DoesNotContain(LoserSection, capturedImplTaskFile);
        Assert.DoesNotContain(LoserDetail, capturedImplTaskFile);
        Assert.DoesNotContain(EvaluatorLoserReasoning, capturedImplTaskFile);
        Assert.DoesNotContain("kind:candidate", capturedImplTaskFile);
        Assert.DoesNotContain("kind:evaluator", capturedImplTaskFile);

        // Implementation step's step-history.md (cross-run context) carries
        // ONLY the canonical create_design row — not the per-candidate
        // (kind:cand-) rows, not the :evaluator row. Per-candidate rows
        // exist in step_result for telemetry but are filtered out by
        // ShouldIncludeStepResultInAgentContext (AgentRunner.cs:3264).
        Assert.NotNull(capturedImplStepHistory);
        Assert.Contains("Design / create_design (COMPLETE)", capturedImplStepHistory!);
        Assert.DoesNotContain(LoserSection, capturedImplStepHistory);
        Assert.DoesNotContain(LoserDetail, capturedImplStepHistory);
        Assert.DoesNotContain(EvaluatorLoserReasoning, capturedImplStepHistory);
        Assert.DoesNotContain(":cand-", capturedImplStepHistory);
        Assert.DoesNotContain(":evaluator", capturedImplStepHistory);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (ITaskBoardClient, List<CardComment>) SetUpBoardClient()
    {
        var commentsList = new List<CardComment>();
        var board = Substitute.For<ITaskBoardClient>();
        board.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TestCardId, TestCardTitle, "Operator requirements", "list-design"),
            });
        board.GetCardCommentsAsync(TestCardId, Arg.Any<CancellationToken>())
            .Returns(commentsList);
        board.UpdateCardBodyAsync(TestCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        board.AppendAgentCommentAsync(TestCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        board.MoveCardToColumnAsync(TestCardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return (board, commentsList);
    }

    /// <summary>
    /// Seeds the run store with three rows simulating a prior run that
    /// produced canonical + per-candidate + evaluator records on a state
    /// named "PriorState" with step "prior_step". The current run's state
    /// is "Design" so these don't satisfy the cache lookup for any current
    /// step but they DO appear in <see cref="IRunStore.GetStepResultsForCardAsync"/>
    /// (which is what feeds <c>WritePriorStepContextAsync</c>).
    /// </summary>
    private static void SeedStepHistoryRows(
        SeededReplayStore store,
        string canonicalSummary,
        string loserCandidateSummary,
        string evaluatorReasoning)
    {
        var groupId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddMinutes(-30);
        store.SeedPriorRow(new StepResultRecord(
            RunId: "prior-run-1", CardId: TestCardId, StateName: "PriorState",
            StepName: "prior_step", StepIndex: 0,
            Role: "senior_engineer", Model: "opus-4.6",
            Outcome: AgentOutcome.COMPLETE,
            Summary: null, Detail: canonicalSummary,
            ReferenceContent: null, ConversationLog: null,
            Questions: null, RequestedSteps: null,
            StartedAtUtc: now, CompletedAtUtc: now.AddSeconds(5)));
        store.SeedPriorRow(new StepResultRecord(
            RunId: "prior-run-1", CardId: TestCardId, StateName: "PriorState",
            StepName: "prior_step:cand-0:claude", StepIndex: 0,
            Role: "senior_engineer", Model: "opus-4.6",
            Outcome: AgentOutcome.COMPLETE,
            Summary: null, Detail: loserCandidateSummary,
            ReferenceContent: null, ConversationLog: null,
            Questions: null, RequestedSteps: null,
            StartedAtUtc: now, CompletedAtUtc: now.AddSeconds(3),
            CandidateGroupId: groupId, CandidateIndex: 0, Selected: false));
        store.SeedPriorRow(new StepResultRecord(
            RunId: "prior-run-1", CardId: TestCardId, StateName: "PriorState",
            StepName: "prior_step:evaluator", StepIndex: 0,
            Role: "evaluator", Model: "opus-4.6",
            Outcome: AgentOutcome.COMPLETE,
            Summary: null, Detail: evaluatorReasoning,
            ReferenceContent: null, ConversationLog: null,
            Questions: null, RequestedSteps: null,
            StartedAtUtc: now, CompletedAtUtc: now.AddSeconds(7)));
    }

    private static WorkflowConfig BuildSingleStepConfig() =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-design"] = new(
                    "Design", null, "agent_run",
                    null,
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

    private static WorkflowConfig BuildCrossStateConfig() =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-design"] = new(
                    "Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-impl"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps: [
                        new("create_design", "senior_engineer",
                            TaskPrompt: "Design prompt for {TaskName}",
                            Slots: [
                                new SlotConfig(
                                    Candidates:
                                    [
                                        new("docker-claude-cli"),
                                        new("docker-opencode"),
                                    ],
                                    Evaluator: new EvaluatorConfig(
                                        Role: "evaluator",
                                        TaskPrompt: "Pick the better design.")),
                            ]),
                    ]),
                ["list-impl"] = new(
                    "Implementation", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-tested"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps: [
                        new("implement", "implementer",
                            TaskPrompt: "Implementation prompt for {TaskName}\n\n## Design\n{TaskBody}"),
                    ]),
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
                    new List<string> { "Technical Design" }, Provider: "docker-claude-cli"),
                ["implementer"] = new("sonnet-4.6", "You are an Implementer.",
                    new List<string>(), Provider: "claude-cli"),
                ["evaluator"] = new("opus-4.6", "You evaluate candidates.",
                    new List<string>(), Provider: "claude-cli"),
            });

    private CandidateGroupRequest NewCandidateRequest(
        string stepName,
        string[] providers,
        string gitBehavior = "commit_and_push")
    {
        var systemPromptFile = Path.Combine(_repoRoot, "system.md");
        File.WriteAllText(systemPromptFile, "# evaluator system prompt");

        return new CandidateGroupRequest(
            RunId: "run-test-1",
            CardId: TestCardId,
            CardTitle: TestCardTitle,
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
                ["evaluator"] = new("claude-opus-4-6", "you are an evaluator", []),
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

    // ── Test stubs ──────────────────────────────────────────────────────────

    /// <summary>
    /// Executor that returns a fixed outcome + detail and optional section.
    /// Touches a marker file in the worktree so commit_and_push has something
    /// to commit. Mirrors the ScriptedExecutor in CandidateExecutorFlowTests.
    /// </summary>
    private sealed class ScriptedExecutor(
        AgentOutcome outcome,
        string detail,
        SectionUpdate? section = null) : IAgentExecutor
    {
        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            var marker = Path.Combine(context.WorkspacePath, "candidate-output.txt");
            File.WriteAllText(marker, $"{detail}\n");
            return Task.FromResult(new AgentResult(outcome, detail, Section: section));
        }
    }

    /// <summary>
    /// Executor that returns <paramref name="first"/> on its first call and
    /// <paramref name="rest"/> on every subsequent call. Used to disambiguate
    /// the evaluator's invocation (first claude-cli call) from the
    /// implementation agent's (subsequent claude-cli calls). The
    /// <paramref name="onCall"/> callback fires for every call so tests can
    /// snapshot the context.
    /// </summary>
    private sealed class SequencedExecutor : IAgentExecutor
    {
        private readonly AgentResult _first;
        private readonly AgentResult _rest;
        private readonly Action<AgentExecutionContext>? _onCall;
        private int _calls;
        private readonly object _lock = new();

        public SequencedExecutor(AgentResult first, AgentResult rest, Action<AgentExecutionContext>? onCall = null)
        {
            _first = first;
            _rest = rest;
            _onCall = onCall;
        }

        public Task<AgentResult> ExecuteAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            int callIdx;
            lock (_lock) { callIdx = _calls++; }
            _onCall?.Invoke(context);
            // Touch the marker file so commit_and_push has something to commit.
            var marker = Path.Combine(context.WorkspacePath, $"sequenced-output-{callIdx}.txt");
            File.WriteAllText(marker, $"call {callIdx}\n");
            return Task.FromResult(callIdx == 0 ? _first : _rest);
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
    /// Minimal IRunStore that records writes and lets the test pre-seed
    /// rows for <see cref="GetStepResultsForCardAsync"/>. Designed for
    /// the step-history file test (B3) — the seeded rows simulate prior
    /// run output that the runner reads to write step-history.md.
    /// </summary>
    private sealed class SeededReplayStore : IRunStore
    {
        public List<StepResultRecord> SavedSteps { get; } = [];
        public List<StepResultRecord> PriorRows { get; } = [];

        public void SeedPriorRow(StepResultRecord row) => PriorRows.Add(row);

        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
        {
            SavedSteps.Add(result);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct)
        {
            var rows = PriorRows.Concat(SavedSteps)
                .Where(r => r.CardId == cardId && (stateName is null || r.StateName == stateName))
                .ToList();
            return Task.FromResult<IReadOnlyList<StepResultRecord>>(rows);
        }
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
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Trivial run store for the B1 direct-CandidateExecutor test where we
    /// don't need replay or seed logic — just need the saves to succeed
    /// and not throw.
    /// </summary>
    private sealed class ScriptedRunStore : IRunStore
    {
        public List<StepResultRecord> SavedSteps { get; } = [];
        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct) { SavedSteps.Add(result); return Task.CompletedTask; }
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct) => Task.CompletedTask;
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult<CacheCandidateRecord?>(null);
        public Task<string?> GetEarliestStateEntryShaAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetStateEntryShaAsync(string runId, string sha, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Trivial board client for the B1 direct test — just records comments
    /// and lets other writes pass through. CandidateExecutor uses
    /// AppendAgentCommentAsync for per-candidate comments.
    /// </summary>
    private sealed class ScriptedBoardClient : ITaskBoardClient
    {
        public List<string> Comments { get; } = [];
        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken cancellationToken) { Comments.Add(body); return Task.CompletedTask; }
        public Task AppendAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken) { Comments.Add(commentBody); return Task.CompletedTask; }
        public Task DeleteAgentCommentsByMarkerAsync(string cardId, string markerSubstring, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken, IReadOnlyList<string>? excludeStatuses = null) => throw new NotImplementedException();
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AssignAsync(string cardId, string username, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UnassignAsync(string cardId, string? username, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken cancellationToken) => Task.FromResult("new-card-1");
        public Task<string> GetCurrentUserAsync(CancellationToken cancellationToken) => Task.FromResult("test-user");
    }
}
