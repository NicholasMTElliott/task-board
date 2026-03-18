using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class AgentRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITrelloClient _trelloClient;
    private readonly StubAgentExecutor _agentExecutor;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly WorkflowConfig _workflowConfig;
    private readonly AgentRunner _runner;

    private const string DesignListId = "list-design";
    private const string TargetCardId = "card-target";
    private const string BoardId = "board-1";

    public AgentRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agentrunner-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _trelloClient = Substitute.For<ITrelloClient>();
        _agentExecutor = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildWorkflowConfig();

        _runner = new AgentRunner(
            _trelloClient,
            _agentExecutor,
            _taskFileManager,
            _gitWorkspaceManager,
            _workflowConfig,
            NullLogger<AgentRunner>.Instance);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir))
            return;

        foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_Success_CreatesCommitOnBranch()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.SUCCESS;

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.SUCCESS, result.Outcome);
        Assert.Null(result.ErrorDetail);

        // Verify branch was created
        var branch = await _gitWorkspaceManager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal($"aiboard/{TargetCardId}", branch);
    }

    [Fact]
    public async Task ExecuteAsync_Questions_CreatesCommitWithQuestions()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.QUESTIONS;

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.QUESTIONS, result.Outcome);

        // Verify task file contains questions
        var taskContent = await _taskFileManager.ReadTaskFileAsync(_tempDir, TargetCardId, CancellationToken.None);
        Assert.Contains("Questions", taskContent);
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
            .Returns(new List<TrelloCard>());

        var result = await _runner.ExecuteAsync("nonexistent", BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not found", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_CardInUnknownList_ReturnsError()
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<TrelloCard>
            {
                new(TargetCardId, "Card", "Desc", "unknown-list"),
            });

        var result = await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not in workflow config", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_WritesAllTaskFiles()
    {
        SetupBoardCards();
        _agentExecutor.NextOutcome = AgentOutcome.SUCCESS;

        await _runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Verify both cards were written as task files
        Assert.True(File.Exists(TaskFileManager.GetTaskFilePath(_tempDir, TargetCardId)));
        Assert.True(File.Exists(TaskFileManager.GetTaskFilePath(_tempDir, "card-other")));
    }

    [Fact]
    public async Task ExecuteAsync_StateHasNoRole_ReturnsError()
    {
        // Use a workflow config where the state has a null role
        var configWithNoRole = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design Review", null, "manual_gate", null,
                    new Dictionary<string, string>()),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var runner = new AgentRunner(
            _trelloClient, _agentExecutor, _taskFileManager, _gitWorkspaceManager,
            configWithNoRole, NullLogger<AgentRunner>.Instance);

        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<TrelloCard>
            {
                new(TargetCardId, "Card", "Desc", DesignListId),
            });

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("No valid role", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_AgentThrowsException_ReturnsError()
    {
        var throwingExecutor = Substitute.For<IAgentExecutor>();
        throwingExecutor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<AgentOutcome>(_ => throw new InvalidOperationException("LLM provider is down"));

        var runner = new AgentRunner(
            _trelloClient, throwingExecutor, _taskFileManager, _gitWorkspaceManager,
            _workflowConfig, NullLogger<AgentRunner>.Instance);

        SetupBoardCards();

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("LLM provider is down", result.ErrorDetail);
    }

    [Fact]
    public void ResolvePromptPlaceholders_ReplacesKnownPlaceholders()
    {
        var template = "Work on task \"{TaskName}\" ({TaskId}). Story: {UserStoryName}";
        var card = new TrelloCard("card-123", "Build Auth", "desc", "list-1");

        var result = AgentRunner.ResolvePromptPlaceholders(template, card);

        Assert.Equal("Work on task \"Build Auth\" (card-123). Story: {UserStoryName}", result);
    }

    [Fact]
    public void ResolvePromptPlaceholders_NoPlaceholders_ReturnsUnchanged()
    {
        var template = "Just a plain prompt with no placeholders.";
        var card = new TrelloCard("id", "name", "desc", "list");

        var result = AgentRunner.ResolvePromptPlaceholders(template, card);

        Assert.Equal(template, result);
    }

    private void SetupBoardCards()
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<TrelloCard>
            {
                new(TargetCardId, "Build Auth Middleware", "Implement JWT authentication", DesignListId),
                new("card-other", "Setup CI/CD", "Configure GitHub Actions", DesignListId),
            });
    }

    private static WorkflowConfig BuildWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "Design task \"{TaskName}\" ({TaskId})",
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design", "Decisions" }),
            });
    }

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".gitkeep");
        RunGitSync(path, "commit", "-m", "initial");
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
