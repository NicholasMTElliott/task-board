using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Design state-focused tests using StubAgentExecutor with realistic card data.
/// Verifies card body updates, comments, git state, and prompt composition.
/// With "discard" gitBehavior, worktrees are cleaned up after execution,
/// so we verify effects through board client mock interactions.
/// </summary>
public class AgentRunnerDesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _trelloClient;
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
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _trelloClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildDesignWorkflowConfig();
    }

    public void Dispose()
    {
        // Clean up worktrees first, then prune, then delete repos
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    [Fact]
    public async Task Design_Success_UpdatesCardBodyWithDesign()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.COMPLETE,
            DesignContent = "## Technical Design\n\n### Architecture\nREST endpoint with bcrypt hashing and JWT.\n\n### Data Flow\nPOST /register → validate → hash → insert → sign JWT → return"
        };
        var runner = CreateRunner(stub);
        SetupBoardCards(WellSpecifiedDescription);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);

        // Card body should be updated with design content
        await _trelloClient.Received(1).UpdateCardBodyAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("## Technical Design") && s.Contains("Architecture")),
            Arg.Any<CancellationToken>());

        // Main repo should still be on its original branch (untouched)
        var mainBranch = await _gitWorkspaceManager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.True(mainBranch == "main" || mainBranch == "master",
            $"Main repo should be on main/master, got '{mainBranch}'");

        // Branch should be cleaned up (discard behavior)
        var existingBranch = await _gitWorkspaceManager.FindBranchByPrefixAsync(
            _tempDir, TargetCardId, CancellationToken.None);
        Assert.Null(existingBranch);
    }

    [Fact]
    public async Task Design_Questions_PostsQuestionsComment()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.NEEDS_INFO
        };
        var runner = CreateRunner(stub);
        SetupBoardCards(VagueDescription);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);

        // Comment should contain questions
        await _trelloClient.Received(1).UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Questions")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Design_Error_PostsErrorComment()
    {
        var stub = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)
        {
            NextOutcome = AgentOutcome.ERROR
        };
        var runner = CreateRunner(stub);
        SetupBoardCards(WellSpecifiedDescription);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);

        // Comment should contain error info
        await _trelloClient.Received(1).UpsertAgentCommentAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Contains("Agent Error")),
            Arg.Any<CancellationToken>());
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

        // WorkspacePath should be the worktree, not the repo root
        Assert.Contains("-worktrees", capturingExecutor.CapturedContext.WorkspacePath);
        Assert.NotEqual(_tempDir, capturingExecutor.CapturedContext.WorkspacePath);
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
            .Returns(new List<BoardCard>
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
                    "You are working on the task {TaskName} ({TaskId}). All project tasks are available in /.aiboard/tasks/ for context. Your job is to update ONLY the file for this task to add a detailed technical design approach.",
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
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
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
}

/// <summary>
/// Test double that captures the execution context for assertion,
/// without modifying any files.
/// </summary>
internal sealed class CapturingAgentExecutor : IAgentExecutor
{
    public AgentExecutionContext? CapturedContext { get; private set; }
    public AgentOutcome NextOutcome { get; set; } = AgentOutcome.COMPLETE;

    public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
    {
        CapturedContext = context;
        // Don't modify files — just capture and return
        return Task.FromResult(new AgentResult(NextOutcome));
    }
}
