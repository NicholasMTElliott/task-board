using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Design state-focused tests using StubAgentExecutor with realistic card data.
/// Verifies task file content, git state, and prompt composition.
/// </summary>
public class AgentRunnerDesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITrelloClient _trelloClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly WorkflowConfig _workflowConfig;

    private const string DesignListId = "list-design";
    private const string TargetCardId = "card-auth-endpoint";
    private const string BoardId = "board-1";

    private const string WellSpecifiedDescription =
        "Build a REST API endpoint for user registration.\n\n" +
        "Requirements:\n" +
        "- Accept email and password in POST body\n" +
        "- Validate email format\n" +
        "- Hash password with bcrypt\n" +
        "- Store in PostgreSQL users table\n" +
        "- Return JWT token on success\n\n" +
        "Tech stack: Node.js, Express, PostgreSQL";

    private const string VagueDescription = "Make the app faster";

    public AgentRunnerDesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "design-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _trelloClient = Substitute.For<ITrelloClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildDesignWorkflowConfig();
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
    public async Task Design_Success_TaskFileContainsTechnicalDesign()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.SUCCESS,
            DesignContent = "## Technical Design\n\n### Architecture\nREST endpoint with bcrypt hashing and JWT.\n\n### Data Flow\nPOST /register → validate → hash → insert → sign JWT → return"
        };
        var runner = CreateRunner(stub);
        SetupBoardCards(WellSpecifiedDescription);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.SUCCESS, result.Outcome);
        Assert.Null(result.ErrorDetail);

        // Verify task file has design content
        var taskContent = await _taskFileManager.ReadTaskFileAsync(_tempDir, TargetCardId, CancellationToken.None);
        Assert.Contains("## Technical Design", taskContent);
        Assert.Contains("Architecture", taskContent);
        Assert.Contains("Data Flow", taskContent);

        // Verify branch name
        var branch = await _gitWorkspaceManager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal($"aiboard/{TargetCardId}", branch);

        // Verify commit exists with correct message
        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            _tempDir, ["log", "--oneline", "-1"], CancellationToken.None);
        Assert.Contains("Design complete", logOutput);
    }

    [Fact]
    public async Task Design_Questions_TaskFileContainsQuestionSection()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.QUESTIONS
        };
        var runner = CreateRunner(stub);
        SetupBoardCards(VagueDescription);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.QUESTIONS, result.Outcome);

        // Verify task file has questions
        var taskContent = await _taskFileManager.ReadTaskFileAsync(_tempDir, TargetCardId, CancellationToken.None);
        Assert.Contains("## Questions", taskContent);
        Assert.Contains("?", taskContent); // At least one question

        // Verify commit message
        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            _tempDir, ["log", "--oneline", "-1"], CancellationToken.None);
        Assert.Contains("questions for", logOutput);
    }

    [Fact]
    public async Task Design_Error_TaskFileContainsErrorSection()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.ERROR
        };
        var runner = CreateRunner(stub);
        SetupBoardCards(WellSpecifiedDescription);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);

        // Verify task file has error section
        var taskContent = await _taskFileManager.ReadTaskFileAsync(_tempDir, TargetCardId, CancellationToken.None);
        Assert.Contains("## Error", taskContent);
    }

    [Fact]
    public async Task Design_Success_OtherCardFilesUnmodified()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.SUCCESS
        };
        var runner = CreateRunner(stub);

        var otherCards = new[]
        {
            new TrelloCard("card-other-1", "Setup CI", "Configure pipelines", DesignListId),
            new TrelloCard("card-other-2", "Add logging", "Structured logging", DesignListId),
            new TrelloCard("card-other-3", "Write docs", "API documentation", DesignListId),
        };

        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<TrelloCard>(otherCards)
            {
                new(TargetCardId, "Build Auth Endpoint", WellSpecifiedDescription, DesignListId),
            });

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Read other cards' files — they should have only frontmatter + original description
        foreach (var other in otherCards)
        {
            var content = await _taskFileManager.ReadTaskFileAsync(_tempDir, other.Id, CancellationToken.None);
            // Should NOT contain agent-added sections
            Assert.DoesNotContain("## Technical Design", content);
            Assert.DoesNotContain("## Questions", content);
            Assert.DoesNotContain("## Error", content);
            // Should contain original description
            Assert.Contains(other.Desc, content);
        }
    }

    [Fact]
    public async Task Design_PromptContainsTaskNameAndId()
    {
        var capturingExecutor = new CapturingAgentExecutor();
        var runner = CreateRunner(capturingExecutor);
        SetupBoardCards(WellSpecifiedDescription);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.NotNull(capturingExecutor.CapturedContext);
        Assert.Contains("Build Auth Endpoint", capturingExecutor.CapturedContext!.TaskPrompt);
        Assert.Contains(TargetCardId, capturingExecutor.CapturedContext.TaskPrompt);
        Assert.Contains("Senior Software Engineer", capturingExecutor.CapturedContext.SystemPrompt);
        Assert.Equal("opus-4.6", capturingExecutor.CapturedContext.Model);
    }

    private AgentRunner CreateRunner(IAgentExecutor executor)
    {
        return new AgentRunner(
            _trelloClient, executor, _taskFileManager, _gitWorkspaceManager,
            _workflowConfig, NullLogger<AgentRunner>.Instance);
    }

    private void SetupBoardCards(string targetDescription)
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<TrelloCard>
            {
                new(TargetCardId, "Build Auth Endpoint", targetDescription, DesignListId),
                new("card-other", "Setup CI/CD", "Configure GitHub Actions pipeline", DesignListId),
            });
    }

    private static WorkflowConfig BuildDesignWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "You are working on the task \"{TaskName}\" ({TaskId}). All project tasks are available in /.aiboard/tasks/ for context. Your job is to update ONLY the file for this task to add a detailed technical design approach.",
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6",
                    "You are a Senior Software Engineer. Produce structured Technical Design, identify edge cases, and create implementation breakdowns.",
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

/// <summary>
/// Test double that captures the execution context for assertion,
/// without modifying any files.
/// </summary>
internal sealed class CapturingAgentExecutor : IAgentExecutor
{
    public AgentExecutionContext? CapturedContext { get; private set; }
    public AgentOutcome NextOutcome { get; set; } = AgentOutcome.SUCCESS;

    public Task<AgentOutcome> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
    {
        CapturedContext = context;
        // Don't modify files — just capture and return
        return Task.FromResult(NextOutcome);
    }
}
