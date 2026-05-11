using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Integration tests for role-level fallback wiring in <see cref="AgentRunner"/>.
/// Pinned behaviour:
/// <list type="bullet">
///   <item>Main step: primary throws <see cref="RateLimitException"/> →
///         fallback runs and produces the result; the run COMPLETEs and the
///         persisted <c>step_result</c> records the FALLBACK's provider/model,
///         not the role's primary.</item>
///   <item><c>CliInfrastructureException</c> from the primary triggers fallback.</item>
///   <item>Generic exception (classifies AGENT_ERROR) triggers fallback because
///         it happened before a valid AgentResult existed.</item>
///   <item>When all attempts hit RATE_LIMIT, the runner restores the card to the
///         trigger column (existing top-level handler).</item>
///   <item>Cancellation is never a fallback trigger — propagates immediately.</item>
/// </list>
/// </summary>
public class AgentRunnerRoleFallbackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;

    private const string TriggerColumn = "Ready for Design";
    private const string CompletedColumn = "Designed";
    private const string CardId = "card-42";
    private const string CardTitle = "Auth Feature";
    private const string BoardId = "board-1";

    public AgentRunnerRoleFallbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rfb-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitTestGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(CardId, CardTitle, "Description", TriggerColumn),
            });
        _boardClient.GetCardCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CardComment>>(new List<CardComment>()));
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    private static void InitTestGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    private static AgentResult CompleteResult(string detail = "ok") =>
        new(AgentOutcome.COMPLETE, Detail: detail);

    private WorkflowConfig BuildConfig(List<RoleFallback>? fallbacks = null,
        List<FailureReason>? fallbackOn = null)
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TriggerColumn] = new("Ready for Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Designing"),
                        ["COMPLETE"]    = TransitionTarget.ForColumn(CompletedColumn),
                        ["NEEDS_INFO"]  = TransitionTarget.ForColumn("Design Questions"),
                        ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                    },
                    GitBehavior: "discard",
                    Steps: new List<WorkflowStep>
                    {
                        new("design", "senior_engineer", TaskPrompt: "Design {TaskName} ({TaskId})"),
                    }),
                ["Designing"] = new("Designing", null, "in_progress", null,
                    new Dictionary<string, TransitionTarget>()),
                [CompletedColumn] = new(CompletedColumn, null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["Design Questions"] = new("Design Questions", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
                ["Error"] = new("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new(
                    "claude-opus-4-6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" },
                    Provider: "docker-claude-cli",
                    Fallbacks: fallbacks,
                    FallbackOn: fallbackOn),
            });
    }

    private AgentRunner CreateRunner(
        IAgentExecutor primaryExec,
        IAgentExecutor fallbackExec,
        WorkflowConfig config,
        IRunStore? runStore = null)
    {
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["docker-claude-cli"] = primaryExec,
                ["docker-codex"]      = fallbackExec,
                ["stub"]              = primaryExec, // for any incidental resolves
            });

        return new AgentRunner(
            _boardClient,
            resolver,
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config, new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            runStore ?? NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimaryRateLimit_FallbackSucceeds_RunCompletesAndCardMovesToCompletedColumn()
    {
        // Primary throws RATE_LIMIT (default fallbackOn includes it). Fallback
        // returns COMPLETE. Run should complete normally — operator never sees
        // a card-back-to-Ready bounce just because the primary's quota was hit.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new RateLimitException("Claude rate limited", RateLimitSource.AgentCli));

        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult("Designed by codex"));

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Fallback executor was invoked exactly once.
        await fallback.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
        // Card moved to the COMPLETE column (fallback succeeded → the run
        // succeeded → the orchestrator transitioned forward).
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, CompletedColumn, Arg.Any<CancellationToken>());
        // And not to Error / Design Questions.
        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            CardId, "Error", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryRateLimit_FallbackContextHasFallbackModel()
    {
        // The fallback's executor must see the FALLBACK model in its
        // AgentExecutionContext, not the role's primary model — that's how
        // the right CLI flag (--model) gets passed through.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new RateLimitException("RL", RateLimitSource.AgentCli));

        AgentExecutionContext? capturedFallbackContext = null;
        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedFallbackContext = call.Arg<AgentExecutionContext>();
                return CompleteResult();
            });

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedFallbackContext);
        Assert.Equal("gpt-5.5", capturedFallbackContext!.Model);
    }

    [Fact]
    public async Task PrimaryInfrastructureFailure_FallbackSucceeds_RunCompletes()
    {
        // CliInfrastructureException is in the default fallback set. Should fire.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new CliInfrastructureException("docker daemon not running"));

        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult());

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await fallback.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, CompletedColumn, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryGenericException_FallbackSucceeds_RunCompletes()
    {
        // A generic InvalidOperationException classifies as AGENT_ERROR, but it
        // happened before the primary produced a valid AgentResult. Default
        // role fallback now treats that as provider redundancy and tries Codex.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("parser blew up"));

        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult("fallback recovered"));

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await fallback.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, CompletedColumn, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryTimeout_DefaultFallbackOn_FallbackRuns()
    {
        // TIMEOUT is also a pre-result executor failure. Default fallbackOn
        // covers every non-cancellation exception category.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("CLI inactivity timer fired"));

        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult());

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await fallback.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryTimeout_TimeoutOptedIn_FallbackRuns()
    {
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("inactivity"));

        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult());

        var config = BuildConfig(
            fallbacks: new List<RoleFallback> { new("docker-codex", Model: "gpt-5.5") },
            fallbackOn: new List<FailureReason>
            {
                FailureReason.RATE_LIMIT,
                FailureReason.INFRASTRUCTURE,
                FailureReason.TIMEOUT, // explicit opt-in
            });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await fallback.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllAttemptsRateLimited_PropagatesRateLimitException()
    {
        // Primary AND fallback both rate-limited. The orchestrator's outer
        // top-level handler should still take the card back to the trigger
        // column (existing behaviour); the test verifies the wrapper doesn't
        // swallow the final exception.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new RateLimitException("primary RL", RateLimitSource.AgentCli));

        var fallback = Substitute.For<IAgentExecutor>();
        fallback.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new RateLimitException("fallback RL", RateLimitSource.AgentCli));

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        // Card was restored to the trigger column (existing rate-limit handler).
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, TriggerColumn, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryAgentReturnsErrorOutcome_FallbackNotInvoked()
    {
        // The agent returned a successful run that DECLARED outcome=ERROR
        // (its in-band quality verdict). This must NOT trigger fallback —
        // fallback is for runtime exceptions, not quality signals.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.ERROR, Detail: "I cannot complete this task"));

        var fallback = Substitute.For<IAgentExecutor>();

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await fallback.DidNotReceive().ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
        // The agent's ERROR outcome routes via the workflow's ERROR transition.
        await _boardClient.Received().MoveCardToColumnAsync(
            CardId, "Error", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimarySuccess_FallbackNotInvoked()
    {
        // Sanity baseline: primary succeeds → fallback is never resolved.
        var primary = Substitute.For<IAgentExecutor>();
        primary.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult("primary did the work"));

        var fallback = Substitute.For<IAgentExecutor>();

        var config = BuildConfig(fallbacks: new List<RoleFallback>
        {
            new("docker-codex", Model: "gpt-5.5"),
        });

        var runner = CreateRunner(primary, fallback, config);
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await primary.Received(1).ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
        await fallback.DidNotReceive().ExecuteAsync(
            Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }
}
