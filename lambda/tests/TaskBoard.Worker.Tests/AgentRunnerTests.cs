using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests;

public class AgentRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _trelloClient;
    private readonly StubAgentExecutor _agentExecutor;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly WorkflowConfig _workflowConfig;
    private readonly AgentRunner _runner;
    private readonly string _defaultBranch;

    private const string DesignListId = "list-design";
    private const string ImplListId = "list-impl";
    private const string TargetCardId = "card-target";
    private const string TargetCardTitle = "Build Auth Middleware";
    private const string BoardId = "board-1";

    public AgentRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agentrunner-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);
        _defaultBranch = RunGitSyncWithOutput(_tempDir, "branch", "--show-current");

        _trelloClient = Substitute.For<ITaskBoardClient>();
        _agentExecutor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildWorkflowConfig().Normalised();

        _runner = new AgentRunner(
            _trelloClient,
            AgentExecutorResolver.ForSingleExecutor(_agentExecutor),
            _taskFileManager,
            _gitWorkspaceManager,
            _workflowConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, _workflowConfig, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_CommitAndPush_CreatesCommitOnBranch()
    {
        SetupBoardCards(ImplListId);
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        var runner = CreateRunnerWithConfig(BuildImplWorkflowConfig());
        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Main repo should be untouched (still on original branch)
        var mainBranch = await _gitWorkspaceManager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal(_defaultBranch, mainBranch);

        // Agent branch should exist (with slug)
        var existingBranch = await _gitWorkspaceManager.FindBranchByPrefixAsync(
            _tempDir, TargetCardId, CancellationToken.None);
        Assert.NotNull(existingBranch);
    }

    [Fact]
    public async Task ExecuteAsync_Discard_CleansUpWorktreeAndBranch()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Branch should be cleaned up for discard behavior
        var existingBranch = await _gitWorkspaceManager.FindBranchByPrefixAsync(
            _tempDir, TargetCardId, CancellationToken.None);
        Assert.Null(existingBranch);
    }

    [Fact]
    public async Task ExecuteAsync_Questions_ReturnsNeedsInfo()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.NEEDS_INFO;

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_Error_ReturnsErrorResult()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.ERROR;

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_CardNotFound_ReturnsError()
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>());

        var result = await _runner.ExecuteAsync("nonexistent", BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not found", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_CardInUnknownList_ReturnsError()
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, "Card", "Desc", "unknown-list"),
            });

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not in workflow config", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_StateHasNoRole_ReturnsError()
    {
        var configWithNoRole = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design Review", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var runner = CreateRunnerWithConfig(configWithNoRole);

        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, "Card", "Desc", DesignListId),
            });

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("No steps configured", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_AgentThrowsException_ReturnsError()
    {
        var throwingExecutor = Substitute.For<IAgentExecutor>();
        throwingExecutor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<AgentResult>(_ => throw new InvalidOperationException("LLM provider is down"));

        var runner = new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(throwingExecutor), _taskFileManager, _gitWorkspaceManager,
            BuildWorkflowConfig().Normalised(), new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, BuildWorkflowConfig().Normalised(), new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);

        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("LLM provider is down", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_WithInProgressTransition_MovesCardBeforeAgentRuns()
    {
        var configWithInProgress = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Ready for Design", "senior_engineer", "agent_run",
                    "Design task {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("list-designing"),
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design", "Decisions" }),
            }).Normalised();

        var runner = CreateRunnerWithConfig(configWithInProgress);
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Verify IN_PROGRESS move happened (first call) then COMPLETE move (second call)
        var moveCalls = _trelloClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "MoveCardToColumnAsync")
            .ToList();
        Assert.Equal(2, moveCalls.Count);
        Assert.Equal("list-designing", moveCalls[0].GetArguments()[1]);
        Assert.Equal("list-designed", moveCalls[1].GetArguments()[1]);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutInProgressTransition_SkipsInProgressMove()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Only one move call (the COMPLETE transition in post-processing)
        var moveCalls = _trelloClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "MoveCardToColumnAsync")
            .ToList();
        Assert.Single(moveCalls);
        Assert.Equal("list-review", moveCalls[0].GetArguments()[1]);
    }

    [Fact]
    public async Task ExecuteAsync_Success_PostsCommentAndUpdatesCard()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify comments were posted (step comment + run-level comment)
        await _trelloClient.Received().UpsertAgentCommentAsync(
            TargetCardId, Arg.Is<string>(s => s.Contains("Agent Complete")), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Verify card was moved to COMPLETE column
        await _trelloClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-review", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_InlinePrompt_WritesTempFileAndReturnsPath()
    {
        var role = new WorkflowRole("model", "You are a Senior Engineer.", new List<string>());
        var tempDir = Path.Combine(Path.GetTempPath(), "syspmpt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = await AgentRunner.ResolveSystemPromptFileAsync(role, "senior_engineer", tempDir, null, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal("You are a Senior Engineer.", (await File.ReadAllTextAsync(path)).Trim());
            Assert.Contains("system-prompt-senior_engineer.md", path);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_FileConfigured_ReturnsAbsolutePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "syspmptfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var promptRelPath = "prompts/role.md";
            var promptFullPath = Path.Combine(tempDir, promptRelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
            await File.WriteAllTextAsync(promptFullPath, "You are an engineer.");

            var role = new WorkflowRole("model", "", new List<string>(), SystemPromptFile: promptRelPath);
            var path = await AgentRunner.ResolveSystemPromptFileAsync(role, "engineer", tempDir, null, CancellationToken.None);

            Assert.Equal(Path.GetFullPath(promptFullPath), path);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_FileConfiguredButMissing_ThrowsFileNotFoundException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "syspmptmiss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var role = new WorkflowRole("model", "", new List<string>(), SystemPromptFile: "prompts/missing.md");
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => AgentRunner.ResolveSystemPromptFileAsync(role, "engineer", tempDir, null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveTaskPromptAsync_InlinePrompt_ResolvesPlaceholders()
    {
        var state = new WorkflowState("Design", "senior_engineer", "agent_run",
            "Work on {TaskName} ({TaskId})",
            new Dictionary<string, TransitionTarget>());
        var card = new BoardCard("card-1", "Auth Feature", "desc", "list-design");

        var prompt = await AgentRunner.ResolveTaskPromptAsync(state, "/irrelevant", card, null, CancellationToken.None);

        Assert.Equal("Work on Auth Feature (card-1)", prompt);
    }

    [Fact]
    public async Task ResolveTaskPromptAsync_FileConfigured_ReadsAndResolvesPlaceholders()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "taskprompt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var promptRelPath = "prompts/states/design.md";
            var promptFullPath = Path.Combine(tempDir, promptRelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
            await File.WriteAllTextAsync(promptFullPath, "Implement {TaskName} (id={TaskId}).");

            var state = new WorkflowState("Design", "senior_engineer", "agent_run", null,
                new Dictionary<string, TransitionTarget>(), TaskPromptFile: promptRelPath);
            var card = new BoardCard("42", "Login Flow", "desc", "list-design");

            var prompt = await AgentRunner.ResolveTaskPromptAsync(state, tempDir, card, null, CancellationToken.None);

            Assert.Equal("Implement Login Flow (id=42).", prompt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveTaskPromptAsync_FileConfiguredButMissing_ThrowsFileNotFoundException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "taskpromptmiss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var state = new WorkflowState("Design", "senior_engineer", "agent_run", null,
                new Dictionary<string, TransitionTarget>(), TaskPromptFile: "prompts/states/missing.md");
            var card = new BoardCard("1", "Card", "desc", "list");

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => AgentRunner.ResolveTaskPromptAsync(state, tempDir, card, null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_NoFileAndEmptyInline_ThrowsInvalidOperationException()
    {
        var role = new WorkflowRole("model", "", new List<string>());
        var tempDir = Path.Combine(Path.GetTempPath(), "syspmpt-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => AgentRunner.ResolveSystemPromptFileAsync(role, "engineer", tempDir, null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolvePromptPlaceholders_ReplacesKnownPlaceholders()
    {
        var template = "Work on task {TaskName} ({TaskId}). Story: {UserStoryName}";
        var card = new BoardCard("card-123", "Build Auth", "desc", "list-1");

        var result = AgentRunner.ResolvePromptPlaceholders(template, card);

        Assert.Equal("Work on task Build Auth (card-123). Story: {UserStoryName}", result);
    }

    [Fact]
    public void ResolvePromptPlaceholders_NoPlaceholders_ReturnsUnchanged()
    {
        var template = "Just a plain prompt with no placeholders.";
        var card = new BoardCard("id", "name", "desc", "list");

        var result = AgentRunner.ResolvePromptPlaceholders(template, card);

        Assert.Equal(template, result);
    }

    [Fact]
    public void FormatComment_WithGitNote_AppendsNote()
    {
        var result = new AgentResult(AgentOutcome.COMPLETE, "Done");
        var comment = AgentRunner.FormatComment(result, "Branch `aiboard/1-foo` pushed to origin.");

        Assert.Contains("Agent Complete", comment);
        Assert.Contains("Done", comment);
        Assert.Contains("Branch `aiboard/1-foo` pushed to origin.", comment);
    }

    [Fact]
    public void FormatComment_WithoutGitNote_NoExtra()
    {
        var result = new AgentResult(AgentOutcome.COMPLETE, "Done");
        var comment = AgentRunner.FormatComment(result);

        Assert.Contains("Agent Complete", comment);
        Assert.DoesNotContain("---", comment);
    }

    [Fact]
    public async Task ResolveTaskPromptAsync_RealImplementationPromptFile_ContainsTestingGuidelines()
    {
        var repoRoot = FindRepoRoot();
        var state = new WorkflowState("Ready for Implementation", "senior_engineer", "agent_run",
            null,
            new Dictionary<string, TransitionTarget>(),
            TaskPromptFile: "prompts/states/ready_for_implementation.md");
        var card = new BoardCard("card-1", "Auth Feature", "desc", "list-impl");

        var prompt = await AgentRunner.ResolveTaskPromptAsync(state, repoRoot, card, null, CancellationToken.None);

        Assert.Contains("Testing Requirements", prompt);
        Assert.Contains("contract", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pre-Completion Checklist", prompt);
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_RealSeniorEngineerFile_ContainsTestingPhilosophy()
    {
        var repoRoot = FindRepoRoot();
        var role = new WorkflowRole("claude-opus-4-6", "", new List<string> { "Technical Design" },
            SystemPromptFile: "prompts/senior_engineer.md");

        var path = await AgentRunner.ResolveSystemPromptFileAsync(role, "senior_engineer", repoRoot, null, CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("Testing Philosophy", content);
        Assert.Contains("contract", content, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find repo root (no .git directory) starting from {AppContext.BaseDirectory}");
    }

    [Fact]
    public async Task ExecuteAsync_StepsWithDifferentRoleProviders_ResolvesCorrectExecutorPerStep()
    {
        var executorA = Substitute.For<IAgentExecutor>();
        var executorB = Substitute.For<IAgentExecutor>();
        executorA.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE));
        executorB.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult(AgentOutcome.COMPLETE));

        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-multi"] = new("Multi-Provider Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps:
                    [
                        new WorkflowStep("step_one", "role_a", TaskPrompt: "First step"),
                        new WorkflowStep("step_two", "role_b", TaskPrompt: "Second step"),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["role_a"] = new("model-a", "System prompt A.", [], Provider: "claude-cli"),
                ["role_b"] = new("model-b", "System prompt B.", [], Provider: "codex"),
            }).Normalised();

        var resolver = new AgentExecutorResolver(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-cli"] = executorA,
            ["codex"] = executorB,
        });

        var runner = new AgentRunner(
            _trelloClient, resolver, _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
        SetupBoardCards("list-multi");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        await executorA.Received(1).ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
        await executorB.Received(1).ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private void SetupBoardCards(string? listId = null)
    {
        var targetList = listId ?? DesignListId;
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle, "Implement JWT authentication", targetList),
                new("card-other", "Setup CI/CD", "Configure GitHub Actions", targetList),
            });
    }

    private AgentRunner CreateRunnerWithConfig(WorkflowConfig config)
    {
        return new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(_agentExecutor), _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
    }

    private AgentRunner CreateRunnerWithConfig(WorkflowConfig config, IRunStore runStore)
    {
        return new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(_agentExecutor), _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            runStore,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
    }

    private AgentRunner CreateRunnerWithRunStore(IRunStore runStore)
    {
        return new AgentRunner(
            _trelloClient,
            AgentExecutorResolver.ForSingleExecutor(_agentExecutor),
            _taskFileManager,
            _gitWorkspaceManager,
            _workflowConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, _workflowConfig,
                new AgentIdentity("Test", "Agent", "TestMachine"),
                NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            runStore,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
    }

    private static WorkflowConfig BuildWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "Design task {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design", "Decisions" }),
            });
    }

    private static WorkflowConfig BuildImplWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [ImplListId] = new("Ready for Implementation", "senior_engineer", "agent_run",
                    "Implement task {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "commit_and_push"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design", "Decisions" }),
            }).Normalised();
    }

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    [Fact]
    public async Task ExecuteAsync_MultiStep_AllComplete_RunsBothStepsAndMovesToComplete()
    {
        var config = BuildMultiStepWorkflowConfig().Normalised();
        var runner = CreateRunnerWithConfig(config);
        SetupBoardCards("list-multi");
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Should have exactly 2 step comments; no run-level comment for discard states (gitNote is null)
        var commentCalls = _trelloClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "UpsertAgentCommentAsync")
            .ToList();
        Assert.Equal(2, commentCalls.Count);

        // Verify step markers are used
        var markers = commentCalls.Select(c => (string)c.GetArguments()[2]!).ToList();
        Assert.Contains(markers, m => m.Contains("agent-step:step_one"));
        Assert.Contains(markers, m => m.Contains("agent-step:step_two"));

        // Card should be moved to COMPLETE column
        await _trelloClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-review", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MultiStep_SecondStepNeedsInfo_HaltsAndTransitions()
    {
        var callCount = 0;
        var sequencedExecutor = Substitute.For<IAgentExecutor>();
        sequencedExecutor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // First step completes, second needs info
                return callCount == 1
                    ? new AgentResult(AgentOutcome.COMPLETE, "Step 1 done")
                    : new AgentResult(AgentOutcome.NEEDS_INFO, "Need clarification",
                        [new AgentQuestion("What scale?")]);
            });

        var config = BuildMultiStepWorkflowConfig().Normalised();
        var runner = new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(sequencedExecutor), _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
        SetupBoardCards("list-multi");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal(2, callCount); // Both steps were attempted

        // Card should be moved to NEEDS_INFO column
        await _trelloClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-questions", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MultiStep_FirstStepErrors_HaltsWithoutRunningSecond()
    {
        var callCount = 0;
        var errorExecutor = Substitute.For<IAgentExecutor>();
        errorExecutor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return new AgentResult(AgentOutcome.ERROR, "Something went wrong");
            });

        var config = BuildMultiStepWorkflowConfig().Normalised();
        var runner = new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(errorExecutor), _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
        SetupBoardCards("list-multi");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Equal(1, callCount); // Only first step ran

        // Card should be moved to ERROR column
        await _trelloClient.Received().MoveCardToColumnAsync(
            TargetCardId, "list-error", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MultiStep_RefreshesCommentsFileBetweenSteps()
    {
        var config = BuildMultiStepWorkflowConfig().Normalised();
        var runner = CreateRunnerWithConfig(config);
        SetupBoardCards("list-multi");
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        // Setup GetCardCommentsAsync to return comments (simulating step comments being fetched back)
        _trelloClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>
            {
                new("Step 1 output", "agent", DateTime.UtcNow),
            });

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // GetCardCommentsAsync should be called:
        // 1x initial fetch (before step loop) + 1x after each step (2 steps) = 3 total
        await _trelloClient.Received(3).GetCardCommentsAsync(
            TargetCardId, Arg.Any<CancellationToken>());
    }

    private static WorkflowConfig BuildMultiStepWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-multi"] = new("Multi-Step Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps:
                    [
                        new WorkflowStep("step_one", "senior_engineer", TaskPrompt: "First step for {TaskName}"),
                        new WorkflowStep("step_two", "senior_engineer", TaskPrompt: "Second step for {TaskName}"),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design", "Decisions" }),
            });
    }

    // ── Update file integration tests ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StepCreatesUpdateFiles_ProcessedAfterStep()
    {
        SetupBoardCards();
        _trelloClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
        _trelloClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns("99");

        // Use a custom executor that writes an update file to the worktree
        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<AgentExecutionContext>();
                var updatesDir = Path.Combine(ctx.WorkspacePath, ".aiboard", "updates");
                Directory.CreateDirectory(updatesDir);
                File.WriteAllText(
                    Path.Combine(updatesDir, "new-auth-bug.md"),
                    "---\ntitle: Auth Race Condition Bug\n---\n\nFound a race condition.");
                return new AgentResult(AgentOutcome.COMPLETE, "Done");
            });

        var runner = new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(executor), _taskFileManager, _gitWorkspaceManager,
            _workflowConfig, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, _workflowConfig, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // CreateCardAsync was called for the new ticket
        await _trelloClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.Title == "Auth Race Condition Bug"),
            Arg.Any<CancellationToken>());

        // Notification comment posted on source card — marker only in commentMarker param, not body
        await _trelloClient.Received(1).UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("#99") && !s.Contains("agent-created-ticket:auth-bug")),
            Arg.Is<string>(s => s.Contains("agent-created-ticket:auth-bug")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NonCompleteStep_UpdateFilesStillProcessed()
    {
        SetupBoardCards();
        _trelloClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());
        _trelloClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns("50");

        var executor = Substitute.For<IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.Arg<AgentExecutionContext>();
                var updatesDir = Path.Combine(ctx.WorkspacePath, ".aiboard", "updates");
                Directory.CreateDirectory(updatesDir);
                File.WriteAllText(
                    Path.Combine(updatesDir, "new-discovered-issue.md"),
                    "---\ntitle: Discovered Issue\n---\n\nFound while working.");
                return new AgentResult(AgentOutcome.NEEDS_INFO, "Need clarification",
                    [new AgentQuestion("What is the expected behavior?")]);
            });

        var runner = new AgentRunner(
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(executor), _taskFileManager, _gitWorkspaceManager,
            _workflowConfig, new StubCrossReferenceResolver(), new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, _workflowConfig, new AgentIdentity("Test", "Agent", "TestMachine"), NullImageUploader.Instance, NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            NullImageUploader.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);

        // Update files processed even though step returned NEEDS_INFO
        await _trelloClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.Title == "Discovered Issue"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NoUpdateFiles_ExistingBehaviorUnchanged()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        // CreateCardAsync never called (no update files)
        await _trelloClient.DidNotReceive().CreateCardAsync(
            Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    // ── RunStore / DB integration tests ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SavesStepResultToDb()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        await mockRunStore.Received(1).CreateRunAsync(
            Arg.Is<RunRecord>(r => r.CardId == TargetCardId), Arg.Any<CancellationToken>());
        await mockRunStore.Received(1).SaveStepResultAsync(
            Arg.Is<StepResultRecord>(r => r.CardId == TargetCardId && r.Outcome == AgentOutcome.COMPLETE),
            Arg.Any<CancellationToken>());
        await mockRunStore.Received(1).CompleteRunAsync(
            Arg.Any<string>(), AgentOutcome.COMPLETE, Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DbFailure_ContinuesWithoutError()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();
        mockRunStore.SaveStepResultAsync(Arg.Any<StepResultRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB connection lost"));

        var runner = CreateRunnerWithRunStore(mockRunStore);
        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome); // Run still succeeds
    }

    [Fact]
    public async Task ExecuteAsync_WithRunStore_TrimsCardBody()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        _agentExecutor.DesignContent = "## Approach\n\nSummary.\n\n---\n\n<details><summary>Detail</summary>\n\nFull detail here.\n\n</details>";
        var mockRunStore = Substitute.For<IRunStore>();

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify board received trimmed content (no <details> blocks)
        await _trelloClient.Received().UpdateCardBodyAsync(
            TargetCardId,
            Arg.Is<string>(body => !body.Contains("<details>") && body.Contains("## Approach")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithNullRunStore_PostsFullCardBody()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        _agentExecutor.DesignContent = "## Approach\n\nSummary.\n\n---\n\n<details><summary>Detail</summary>\n\nFull detail.\n\n</details>";

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify board received FULL content (NullRunStore = no trimming)
        await _trelloClient.Received().UpdateCardBodyAsync(
            TargetCardId,
            Arg.Is<string>(body => body.Contains("<details>")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithRunStore_OmitsConversationLogFromComment()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Step comments should not contain "conversation log"
        await _trelloClient.Received().UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(c => !c.Contains("Agent conversation log")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Error_RecordsRunAsError()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.ERROR;
        var mockRunStore = Substitute.For<IRunStore>();

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        await mockRunStore.Received().CompleteRunAsync(
            Arg.Any<string>(), AgentOutcome.ERROR, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MultiStep_SavesEachStepResult()
    {
        SetupBoardCards("list-multi");
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();

        var runner = CreateRunnerWithConfig(BuildMultiStepWorkflowConfig().Normalised(), mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Should save a result for each step
        await mockRunStore.Received(2).SaveStepResultAsync(
            Arg.Any<StepResultRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WritesPriorStepContext()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();
        mockRunStore.GetStepResultsForCardAsync(TargetCardId, null, Arg.Any<CancellationToken>())
            .Returns(new List<StepResultRecord>
            {
                new("prior-run", TargetCardId, "Ready for Design", "create_design", 1,
                    "senior_engineer", "claude-opus-4-6", AgentOutcome.COMPLETE,
                    "Design complete", "Full design content here", null, null, null, null,
                    DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow)
            });

        string? capturedContextContent = null;
        _agentExecutor.OnExecute = (ctx, _) =>
        {
            // Read context file during execution before worktree is discarded
            var contextFile = Path.Combine(ctx.WorkspacePath, ".aiboard", "context", "step-history.md");
            if (File.Exists(contextFile))
                capturedContextContent = File.ReadAllText(contextFile);
        };

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify GetStepResultsForCardAsync was called and context file was written
        await mockRunStore.Received(1).GetStepResultsForCardAsync(
            TargetCardId, null, Arg.Any<CancellationToken>());
        Assert.NotNull(capturedContextContent);
        Assert.Contains("create_design", capturedContextContent);
        Assert.Contains("Full design content here", capturedContextContent);
    }

    [Fact]
    public async Task ExecuteAsync_ReferenceFile_StoredInStepResult()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();

        _agentExecutor.OnExecute = (ctx, _) =>
        {
            var updatesDir = Path.Combine(ctx.WorkspacePath, ".aiboard", "updates");
            Directory.CreateDirectory(updatesDir);
            File.WriteAllText(
                Path.Combine(updatesDir, "1-reference.md"),
                "# Detailed Analysis\n\nThis is reference content.");
        };

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify reference content was passed to StepResultRecord
        await mockRunStore.Received().SaveStepResultAsync(
            Arg.Is<StepResultRecord>(r => r.ReferenceContent != null
                && r.ReferenceContent.Contains("Detailed Analysis")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LargeReferenceContent_IsTruncated()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();

        _agentExecutor.OnExecute = (ctx, _) =>
        {
            var updatesDir = Path.Combine(ctx.WorkspacePath, ".aiboard", "updates");
            Directory.CreateDirectory(updatesDir);
            var largeContent = new string('x', 150_000); // exceeds 100k limit
            File.WriteAllText(
                Path.Combine(updatesDir, "1-reference.md"),
                largeContent);
        };

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        await mockRunStore.Received().SaveStepResultAsync(
            Arg.Is<StepResultRecord>(r =>
                r.ReferenceContent != null
                && r.ReferenceContent.Length <= 100_001 + 20 // 100k + truncation marker
                && r.ReferenceContent.EndsWith("[truncated]")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PriorStepContext_IncludesReferenceContent()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.COMPLETE;
        var mockRunStore = Substitute.For<IRunStore>();
        mockRunStore.GetStepResultsForCardAsync(TargetCardId, null, Arg.Any<CancellationToken>())
            .Returns(new List<StepResultRecord>
            {
                new("prior-run", TargetCardId, "Ready for Design", "create_design", 1,
                    "senior_engineer", "claude-opus-4-6", AgentOutcome.COMPLETE,
                    "Summary", "Detail", "Reference analysis content", null, null, null,
                    DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow)
            });

        string? capturedContextContent = null;
        _agentExecutor.OnExecute = (ctx, _) =>
        {
            var contextFile = Path.Combine(ctx.WorkspacePath, ".aiboard", "context", "step-history.md");
            if (File.Exists(contextFile))
                capturedContextContent = File.ReadAllText(contextFile);
        };

        var runner = CreateRunnerWithRunStore(mockRunStore);
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturedContextContent);
        Assert.Contains("Reference Content", capturedContextContent);
        Assert.Contains("Reference analysis content", capturedContextContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FormatComment_ConversationLogControlledByFlag(bool include)
    {
        var result = new AgentResult(AgentOutcome.COMPLETE, "Done", ConversationLog: "long conversation");
        var comment = AgentRunner.FormatComment(result, includeConversationLog: include);

        if (include)
            Assert.Contains("Agent conversation log", comment);
        else
            Assert.DoesNotContain("Agent conversation log", comment);
    }

}
