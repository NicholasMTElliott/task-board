using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Integration test for the cross-provider init-file mirroring at the
/// AgentRunner injection site (<c>ExecuteWithSessionAsync</c>). The unit
/// suite (<see cref="AgentInitFileResolverTests"/>) covers the helper logic
/// in isolation; this test confirms the call-site is wired up so a real run
/// produces the mirror file before the executor sees the workspace.
/// </summary>
public class AgentRunnerInitFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    private const string TargetCardId = "card-init-file";
    private const string TargetCardTitle = "Init File Test";
    private const string BoardId = "board-1";
    private const string DesignListId = "list-design";

    public AgentRunnerInitFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "agentrunner-init-file-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        // Commit a CLAUDE.md so it survives `git checkout` into the worktree.
        // The resolver should mirror this to AGENTS.md when the role's
        // provider is docker-opencode.
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"),
            "# Project orientation\nKeep changes small.\n");
        RunGitSync(_tempDir, "add", "CLAUDE.md");
        RunGitSync(_tempDir, "commit", "-m", "add CLAUDE.md");

        _boardClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktreeBase, recursive: true); } catch { }
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_OpenCodeRole_AgentsMirroredFromClaude_BeforeExecutorRuns()
    {
        // Capture whether AGENTS.md existed at the moment the executor was called.
        // If the resolver wasn't wired, the bind-mount-equivalent host path
        // would have only CLAUDE.md and the OpenCode agent would run blind.
        bool? agentsExistedWhenExecutorRan = null;
        string? observedWorkspace = null;

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ctx = call.Arg<AgentExecutionContext>();
                observedWorkspace = ctx.WorkspacePath;
                var agentsPath = Path.Combine(ctx.WorkspacePath, "AGENTS.md");
                agentsExistedWhenExecutorRan = File.Exists(agentsPath);
                return Task.FromResult(new AgentResult(AgentOutcome.COMPLETE, "ok"));
            });

        var runner = CreateRunner(executor, BuildOpenCodeRoleConfig());
        SetupBoardCard();

        var result = await runner.ExecuteAsync(
            TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(observedWorkspace);
        Assert.True(agentsExistedWhenExecutorRan,
            $"AGENTS.md should exist at {observedWorkspace}/AGENTS.md when the docker-opencode executor runs");
    }

    [Fact]
    public async Task ExecuteAsync_ClaudeRole_NoMirrorNeeded_ClaudeMdAlreadyPresent()
    {
        // Negative-control: when the role's provider already expects the
        // file the project committed (CLAUDE.md → claude-cli), the resolver
        // is a no-op. AGENTS.md stays absent.
        bool? agentsExisted = null;
        bool? claudeExisted = null;

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ctx = call.Arg<AgentExecutionContext>();
                agentsExisted = File.Exists(Path.Combine(ctx.WorkspacePath, "AGENTS.md"));
                claudeExisted = File.Exists(Path.Combine(ctx.WorkspacePath, "CLAUDE.md"));
                return Task.FromResult(new AgentResult(AgentOutcome.COMPLETE, "ok"));
            });

        var runner = CreateRunner(executor, BuildClaudeRoleConfig());
        SetupBoardCard();

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.True(claudeExisted, "CLAUDE.md should be present (committed in test setup)");
        Assert.False(agentsExisted, "AGENTS.md should NOT be created when claude-cli is the provider");
    }

    private AgentRunner CreateRunner(IAgentExecutor executor, WorkflowConfig config)
    {
        var normalisedConfig = config.Normalised();
        return new AgentRunner(
            _boardClient, AgentExecutorResolver.ForSingleExecutor(executor),
            _taskFileManager, _gitWorkspaceManager,
            normalisedConfig, new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, normalisedConfig,
                new AgentIdentity("Agent", "TestMachine"),
                NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);
    }

    private void SetupBoardCard()
    {
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle, "Body", DesignListId),
            });
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
    }

    private static WorkflowConfig BuildOpenCodeRoleConfig() =>
        BuildConfigWithProvider("docker-opencode");

    private static WorkflowConfig BuildClaudeRoleConfig() =>
        BuildConfigWithProvider("claude-cli");

    private static WorkflowConfig BuildConfigWithProvider(string provider) =>
        new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new(
                    "Design", "implementer", "agent_run",
                    "Work on {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard"),
                ["list-done"] = new("Done", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-questions"] = new("Questions", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new(
                    Model: provider == "docker-opencode" ? "qwen3.6-35b-a3b" : "claude-sonnet-4-6",
                    SystemPrompt: "you are an implementer",
                    Sections: [],
                    SystemPromptFile: null,
                    Provider: provider),
            });
}
