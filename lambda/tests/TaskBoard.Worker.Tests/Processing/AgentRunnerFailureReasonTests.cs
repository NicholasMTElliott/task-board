using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// End-to-end verification that <see cref="AgentRunner"/> persists the correct
/// <see cref="FailureReason"/> for each exception type the executor can throw.
/// Complements <see cref="AgentRunnerClassifyFailureTests"/> (unit-level) by
/// exercising the full run path through the store.
/// </summary>
public class AgentRunnerFailureReasonTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly IRunStore _runStore;

    private const string TriggerColumn = "Ready for Design";
    private const string CardId = "card-42";
    private const string CardTitle = "Test";
    private const string BoardId = "board-1";

    public AgentRunnerFailureReasonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fr-tests-" + Guid.NewGuid().ToString("N")[..8]);
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

        _runStore = Substitute.For<IRunStore>();
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

    private AgentRunner CreateRunnerWithThrowingExecutor(Exception toThrow)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(toThrow);

        var config = BuildWorkflowConfig().Normalised();
        return new AgentRunner(
            _boardClient,
            AgentExecutorResolver.ForSingleExecutor(executor),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config,
                new AgentIdentity("Test", "Agent", "TestMachine"),
                NullLogger<UpdateFileProcessor>.Instance),
            _runStore,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(),
                NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);
    }

    private static WorkflowConfig BuildWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TriggerColumn] = new("Ready for Design", "senior_engineer", "agent_run",
                    "Design task {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Designing"),
                        ["COMPLETE"] = TransitionTarget.ForColumn("Designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("Design Questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("Error"),
                    },
                    GitBehavior: "discard"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("claude-opus-4-6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" }),
            });
    }

    [Fact]
    public async Task TimeoutException_PersistsFailureReasonTimeout()
    {
        var runner = CreateRunnerWithThrowingExecutor(new TimeoutException("late"));

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await _runStore.Received(1).CompleteRunAsync(
            Arg.Any<string>(),
            AgentOutcome.ERROR,
            Arg.Any<string?>(),
            FailureReason.TIMEOUT,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CliInfrastructureException_PersistsFailureReasonInfrastructure()
    {
        var runner = CreateRunnerWithThrowingExecutor(
            new CliInfrastructureException("codex not found (exit 127)"));

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await _runStore.Received(1).CompleteRunAsync(
            Arg.Any<string>(),
            AgentOutcome.ERROR,
            Arg.Any<string?>(),
            FailureReason.INFRASTRUCTURE,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenericException_PersistsFailureReasonAgentError()
    {
        var runner = CreateRunnerWithThrowingExecutor(
            new InvalidOperationException("agent blew up"));

        await runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None);

        await _runStore.Received(1).CompleteRunAsync(
            Arg.Any<string>(),
            AgentOutcome.ERROR,
            Arg.Any<string?>(),
            FailureReason.AGENT_ERROR,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RateLimitException_PersistsFailureReasonRateLimit_AndRethrows()
    {
        // RateLimitException is handled in its own catch block before ClassifyFailure
        // is reached; verify it still produces FailureReason.RATE_LIMIT in the store.
        var runner = CreateRunnerWithThrowingExecutor(
            new RateLimitException("rate limited", RateLimitSource.AgentCli));

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        await _runStore.Received(1).CompleteRunAsync(
            Arg.Any<string>(),
            AgentOutcome.ERROR,
            Arg.Any<string?>(),
            FailureReason.RATE_LIMIT,
            Arg.Any<CancellationToken>());
    }
}
