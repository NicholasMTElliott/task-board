using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

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

        _trelloClient = Substitute.For<ITaskBoardClient>();
        _agentExecutor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildWorkflowConfig().Normalised();

        _runner = new AgentRunner(
            _trelloClient,
            _agentExecutor,
            _taskFileManager,
            _gitWorkspaceManager,
            _workflowConfig,
            new StubCrossReferenceResolver(),
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
        Assert.True(mainBranch == "main" || mainBranch == "master",
            $"Main repo should still be on main/master, got '{mainBranch}'");

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
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<BoardCard>());

        var result = await _runner.ExecuteAsync("nonexistent", BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not found", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_CardInUnknownList_ReturnsError()
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
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
                    new Dictionary<string, string>()),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var runner = CreateRunnerWithConfig(configWithNoRole);

        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
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
            _trelloClient, throwingExecutor, _taskFileManager, _gitWorkspaceManager,
            BuildWorkflowConfig().Normalised(), new StubCrossReferenceResolver(), NullLogger<AgentRunner>.Instance);

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
                    new Dictionary<string, string>
                    {
                        ["IN_PROGRESS"] = "list-designing",
                        ["COMPLETE"] = "list-designed",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
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
            new Dictionary<string, string>());
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
                new Dictionary<string, string>(), TaskPromptFile: promptRelPath);
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
                new Dictionary<string, string>(), TaskPromptFile: "prompts/states/missing.md");
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
            new Dictionary<string, string>(),
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

    private void SetupBoardCards(string? listId = null)
    {
        var targetList = listId ?? DesignListId;
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, TargetCardTitle, "Implement JWT authentication", targetList),
                new("card-other", "Setup CI/CD", "Configure GitHub Actions", targetList),
            });
    }

    private AgentRunner CreateRunnerWithConfig(WorkflowConfig config)
    {
        return new AgentRunner(
            _trelloClient, _agentExecutor, _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), NullLogger<AgentRunner>.Instance);
    }

    private static WorkflowConfig BuildWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "Design task {TaskName} ({TaskId})",
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
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
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
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

        // Should have 2 step comments + 1 run-level comment = at least 3 upsert calls
        var commentCalls = _trelloClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "UpsertAgentCommentAsync")
            .ToList();
        Assert.True(commentCalls.Count >= 3,
            $"Expected at least 3 comment upserts (2 steps + 1 run), got {commentCalls.Count}");

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
            _trelloClient, sequencedExecutor, _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), NullLogger<AgentRunner>.Instance);
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
            _trelloClient, errorExecutor, _taskFileManager, _gitWorkspaceManager,
            config, new StubCrossReferenceResolver(), NullLogger<AgentRunner>.Instance);
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
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
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

    private static void RunGitSync(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
    }
}
