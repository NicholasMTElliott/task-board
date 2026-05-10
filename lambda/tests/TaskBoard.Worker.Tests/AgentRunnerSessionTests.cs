using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Tests for container session lifecycle in AgentRunner:
/// - session creation via ISessionableAgentExecutor
/// - session reuse across steps (via ExecuteInSessionAsync)
/// - transparent fallback to direct execution when session dies
/// - provider-mismatch routing to direct execution
/// - ReuseContainer=false disables session creation
/// - session timing persisted to run store
/// - session disposal on run completion, early exit, and cancellation
/// </summary>
public class AgentRunnerSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly IRunStore _runStore;

    private const string CardId = "card-session-test";
    private const string CardTitle = "Session Feature";
    private const string BoardId = "board-1";
    private const string ListId = "list-design";

    public AgentRunnerSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agentrunner-session-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _runStore = Substitute.For<IRunStore>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);

        // Default board responses
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(CardId, CardTitle, "Implement session reuse", ListId),
            });
        _boardClient.GetCardCommentsAsync(CardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
    }

    public void Dispose()
    {
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
        var worktreeBase = _tempDir + "-worktrees";
        CleanupDirectory(worktreeBase);
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

    // ── Stub implementations ──────────────────────────────────────────────────

    /// <summary>
    /// A test session that records calls and can simulate IsAlive = false.
    /// </summary>
    private sealed class StubSession : IAgentExecutorSession
    {
        private readonly StubAgentExecutor _executor;
        public string SessionId { get; } = "aiboard-session-test";
        public string ProviderKey { get; }
        public bool IsAlive { get; set; } = true;
        public bool WasDisposed { get; private set; }
        public int ExecuteCallCount { get; private set; }

        public StubSession(string providerKey, StubAgentExecutor executor)
        {
            ProviderKey = providerKey;
            _executor = executor;
        }

        public async Task<AgentResult> ExecuteInSessionAsync(
            AgentExecutionContext context, CancellationToken cancellationToken)
        {
            ExecuteCallCount++;
            return await _executor.ExecuteAsync(context, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A sessionable executor that wraps StubAgentExecutor and creates StubSessions.
    /// </summary>
    private sealed class StubSessionableExecutor(
        string providerKey,
        StubAgentExecutor inner,
        StubSession? sessionToReturn = null) : ISessionableAgentExecutor
    {
        public string ProviderKey { get; } = providerKey;
        public bool TryCreateSessionCalled { get; private set; }
        public bool ThrowOnCreateSession { get; set; }

        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken ct)
            => inner.ExecuteAsync(context, ct);

        public Task<IAgentExecutorSession?> TryCreateSessionAsync(
            SessionRequest request, CancellationToken ct)
        {
            TryCreateSessionCalled = true;
            if (ThrowOnCreateSession)
                throw new InvalidOperationException("Simulated session creation failure");
            return Task.FromResult<IAgentExecutorSession?>(sessionToReturn);
        }
    }

    // ── Helper builders ───────────────────────────────────────────────────────

    private AgentRunner BuildRunner(
        IAgentExecutorResolver resolver,
        WorkflowConfig config,
        DockerClaudeAgentOptions? dockerOptions = null)
    {
        return new AgentRunner(
            _boardClient,
            resolver,
            _taskFileManager,
            _gitWorkspaceManager,
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Session", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config,
                new AgentIdentity("Session", "TestMachine"),
                NullLogger<UpdateFileProcessor>.Instance),
            _runStore,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance,
            dockerOptions);
    }

    private static WorkflowConfig BuildSingleStepConfig(string provider = "test-provider")
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [ListId] = new("Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-q"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps:
                    [
                        new WorkflowStep("engineer", "engineer", TaskPrompt: "Do the work {TaskName} ({TaskId})"),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["engineer"] = new("stub-model", "You are an engineer.", new List<string>(), Provider: provider),
            }).Normalised();
    }

    private static WorkflowConfig BuildMultiStepConfig(
        string provider1 = "test-provider",
        string provider2 = "test-provider")
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [ListId] = new(
                    Name: "Design",
                    Role: null,
                    GateType: "agent_run",
                    TaskPrompt: null,
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-q"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps: new List<WorkflowStep>
                    {
                        new("step1", "engineer1", TaskPrompt: "Step 1 for {TaskName}"),
                        new("step2", "engineer2", TaskPrompt: "Step 2 for {TaskName}"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["engineer1"] = new("stub-model", "You are an engineer.", new List<string>(), Provider: provider1),
                ["engineer2"] = new("stub-model", "You are an engineer.", new List<string>(), Provider: provider2),
            });
    }

    private AgentExecutorResolver BuildResolverWithSessionable(
        StubSessionableExecutor sessionable, string provider = "test-provider")
    {
        return new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                [provider] = sessionable,
            });
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SessionableExecutor_CallsTryCreateSession()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.True(sessionable.TryCreateSessionCalled, "TryCreateSessionAsync should have been called");
    }

    [Fact]
    public async Task ExecuteAsync_WithSession_UsesExecuteInSessionAsync()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: executed via session, not direct
        Assert.Equal(1, session.ExecuteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MultiStep_ReusesSameSessionForBothSteps()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildMultiStepConfig("test-provider", "test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: both steps executed via the same session
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, session.ExecuteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_SessionDisposedAfterRun_OnSuccessPath()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: session was disposed after the run
        Assert.True(session.WasDisposed, "Session must be disposed after run completion");
    }

    [Fact]
    public async Task ExecuteAsync_SessionDisposedAfterRun_OnEarlyExitPath()
    {
        // Arrange: step returns NEEDS_INFO, causing early exit
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.NEEDS_INFO;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: run returned NEEDS_INFO and session still disposed
        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.True(session.WasDisposed, "Session must be disposed even on early exit");
    }

    [Fact]
    public async Task ExecuteAsync_SessionDeadMidRun_FallsBackToDirectExecution()
    {
        // Arrange: session starts alive, but IsAlive becomes false before step
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner) { IsAlive = false }; // starts dead

        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act - should complete successfully via direct execution fallback
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: run completed (executor was called directly, not via session)
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(0, session.ExecuteCallCount); // session not used
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreationFails_FallsBackToDirectExecution()
    {
        // Arrange: TryCreateSessionAsync throws
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var sessionable = new StubSessionableExecutor("test-provider", inner, null)
        {
            ThrowOnCreateSession = true
        };

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act - should still succeed via direct execution
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: run completed despite session creation failure
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_SessionCreationReturnsNull_FallsBackToDirectExecution()
    {
        // Arrange: TryCreateSessionAsync returns null (Docker not available)
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        // sessionToReturn = null → no session
        var sessionable = new StubSessionableExecutor("test-provider", inner, null);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: completed via direct execution
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_ReuseContainerFalse_DoesNotCallTryCreateSession()
    {
        // Arrange: ReuseContainer = false disables session creation
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = false });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: session creation skipped
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.False(sessionable.TryCreateSessionCalled, "TryCreateSessionAsync must NOT be called when ReuseContainer=false");
        Assert.Equal(0, session.ExecuteCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_NullDockerOptions_DoesNotCallTryCreateSession()
    {
        // Arrange: dockerOptions = null (default for tests that don't pass it)
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, dockerOptions: null);

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: null options skips session creation
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.False(sessionable.TryCreateSessionCalled);
    }

    [Fact]
    public async Task ExecuteAsync_NonSessionableExecutor_NoSessionCreated()
    {
        // Arrange: executor does NOT implement ISessionableAgentExecutor
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;

        var config = BuildSingleStepConfig("test-provider");
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test-provider"] = inner, // plain IAgentExecutor, no session support
            });
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: run completes normally without session
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderMismatch_UsesDirectExecutionForMismatchedStep()
    {
        // Arrange: step2 uses a different provider than the session
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;

        var otherInner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        otherInner.NextOutcome = AgentOutcome.COMPLETE;

        var session = new StubSession("provider-a", inner);
        var sessionable = new StubSessionableExecutor("provider-a", inner, session);

        // Multi-step: step1 uses provider-a (session), step2 uses provider-b (direct)
        var config = BuildMultiStepConfig("provider-a", "provider-b");
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider-a"] = sessionable,
                ["provider-b"] = otherInner,
            });
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: run completes; step1 used session (count=1), step2 used direct (count=0 additional)
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(1, session.ExecuteCallCount); // only step1 used session
    }

    [Fact]
    public async Task ExecuteAsync_WithSession_PersistsSessionStartupMs()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: session startup ms was persisted to run store
        await _runStore.Received(1).UpdateRunSessionStartupMsAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NoSession_DoesNotPersistSessionStartupMs()
    {
        // Arrange: no sessionable executor → no session
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;

        var config = BuildSingleStepConfig("test-provider");
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test-provider"] = inner,
            });
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: startup ms NOT persisted (no session was created)
        await _runStore.DidNotReceive().UpdateRunSessionStartupMsAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithSession_StepResultIncludesSessionExecMs()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var sessionable = new StubSessionableExecutor("test-provider", inner, session);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = BuildResolverWithSessionable(sessionable);
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: saved step result has SessionExecMs set (non-null)
        await _runStore.Received().SaveStepResultAsync(
            Arg.Is<StepResultRecord>(r => r.SessionExecMs.HasValue),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutSession_StepResultHasNullSessionExecMs()
    {
        // Arrange: no sessionable executor
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;

        var config = BuildSingleStepConfig("test-provider");
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test-provider"] = inner,
            });
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: saved step result has SessionExecMs = null
        await _runStore.Received().SaveStepResultAsync(
            Arg.Is<StepResultRecord>(r => !r.SessionExecMs.HasValue),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SessionRequest_ContainsCorrectCardIdAndContainerName()
    {
        // Arrange: capture the session request
        SessionRequest? capturedRequest = null;
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);

        var interceptingExecutor = new CapturingSessionableExecutor("test-provider", inner, session,
            req => capturedRequest = req);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test-provider"] = interceptingExecutor,
            });
        var runner = BuildRunner(resolver, config, new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: request has correct card ID, run ID, and container name convention
        Assert.NotNull(capturedRequest);
        Assert.Equal(CardId, capturedRequest!.CardId);
        // Container name format: aiboard-{tenantShortHash}-{cardId}
        Assert.StartsWith("aiboard-", capturedRequest.ContainerName);
        Assert.EndsWith($"-{CardId}", capturedRequest.ContainerName);
        Assert.False(string.IsNullOrEmpty(capturedRequest.RunId));
        Assert.False(string.IsNullOrEmpty(capturedRequest.ImageName));
    }

    /// <summary>
    /// Sessionable executor that captures the SessionRequest passed to TryCreateSessionAsync.
    /// </summary>
    private sealed class CapturingSessionableExecutor(
        string providerKey,
        StubAgentExecutor inner,
        StubSession? sessionToReturn,
        Action<SessionRequest> onCreateSession) : ISessionableAgentExecutor
    {
        public string ProviderKey { get; } = providerKey;

        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken ct)
            => inner.ExecuteAsync(context, ct);

        public Task<IAgentExecutorSession?> TryCreateSessionAsync(
            SessionRequest request, CancellationToken ct)
        {
            onCreateSession(request);
            return Task.FromResult<IAgentExecutorSession?>(sessionToReturn);
        }
    }
}

/// <summary>
/// Integration test: 2-step run using a stub session verifies the session spans both steps.
/// </summary>
public class AgentRunnerSessionIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    private const string CardId = "card-session-integ";
    private const string CardTitle = "Two Step Session";
    private const string BoardId = "board-integ";
    private const string ListId = "list-multi-session";

    public AgentRunnerSessionIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "session-integ-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);

        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(CardId, CardTitle, "Two-step session test", ListId),
            });
        _boardClient.GetCardCommentsAsync(CardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
    }

    public void Dispose()
    {
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        var worktreeBase = _tempDir + "-worktrees";
        if (Directory.Exists(worktreeBase))
        {
            foreach (var f in Directory.EnumerateFiles(worktreeBase, "*", SearchOption.AllDirectories))
            {
                var a = File.GetAttributes(f);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(worktreeBase, recursive: true);
        }
        if (Directory.Exists(_tempDir))
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                var a = File.GetAttributes(f);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class TrackingSession : IAgentExecutorSession
    {
        private readonly StubAgentExecutor _executor;
        public string SessionId => "aiboard-integ-session";
        public string ProviderKey { get; }
        public bool IsAlive => true;
        public bool WasDisposed { get; private set; }
        public List<string> StepNames { get; } = new();

        public TrackingSession(string providerKey, StubAgentExecutor executor)
        {
            ProviderKey = providerKey;
            _executor = executor;
        }

        public async Task<AgentResult> ExecuteInSessionAsync(
            AgentExecutionContext context, CancellationToken ct)
        {
            StepNames.Add(context.TaskPrompt[..Math.Min(50, context.TaskPrompt.Length)]);
            return await _executor.ExecuteAsync(context, ct);
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingSessionableExecutor(
        string providerKey,
        StubAgentExecutor inner,
        TrackingSession session) : ISessionableAgentExecutor
    {
        public string ProviderKey { get; } = providerKey;

        public Task<AgentResult> ExecuteAsync(AgentExecutionContext ctx, CancellationToken ct)
            => inner.ExecuteAsync(ctx, ct);

        public Task<IAgentExecutorSession?> TryCreateSessionAsync(
            SessionRequest req, CancellationToken ct)
            => Task.FromResult<IAgentExecutorSession?>(session);
    }

    [Fact]
    public async Task ExecuteAsync_TwoStepRun_BothStepsExecutedViaSession_SessionDisposed()
    {
        // Arrange
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new TrackingSession("integ-provider", inner);
        var sessionable = new TrackingSessionableExecutor("integ-provider", inner, session);

        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [ListId] = new(
                    Name: "Design",
                    Role: null,
                    GateType: "agent_run",
                    TaskPrompt: null,
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps: new List<WorkflowStep>
                    {
                        new("review", "eng", TaskPrompt: "Review {TaskName}"),
                        new("implement", "eng", TaskPrompt: "Implement {TaskName}"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["eng"] = new("model", "System prompt.", new List<string>(), Provider: "integ-provider"),
            });

        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["integ-provider"] = sessionable,
            });

        var runner = new AgentRunner(
            _boardClient,
            resolver,
            _taskFileManager,
            _gitWorkspaceManager,
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Integ", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config,
                new AgentIdentity("Integ", "TestMachine"),
                NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance,
            new DockerClaudeAgentOptions { ReuseContainer = true });

        // Act
        var result = await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: both steps ran via the session
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, session.StepNames.Count);
        Assert.True(session.WasDisposed, "Session must be disposed after 2-step run");
    }
}
