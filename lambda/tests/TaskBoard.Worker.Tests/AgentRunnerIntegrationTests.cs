using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;
using Xunit.Abstractions;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Integration tests that invoke real Claude CLI as a coding agent.
/// Gated behind AGENT_INTEGRATION_TESTS=true environment variable.
/// Uses claude-sonnet-4-6 with a $0.50 budget cap per test.
///
/// IMPORTANT: Run from VS Test Explorer or a standalone terminal.
/// Running via `dotnet test` inside a Claude Code session will fail because
/// the Claude CLI subprocess inherits environment state from the parent session.
/// </summary>
[Trait("Category", "Integration")]
public class AgentRunnerIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITrelloClient _trelloClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly WorkflowConfig _workflowConfig;
    private readonly ITestOutputHelper _output;
    private readonly bool _enabled;

    private const string DesignListId = "list-design";
    private const string TargetCardId = "card-integration-test";
    private const string BoardId = "board-integration";

    public AgentRunnerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _enabled = 
            true;
            //!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_INTEGRATION_TESTS"));

        _tempDir = Path.Combine(Path.GetTempPath(), "agent-integ-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        if (_enabled)
        {
            _output.WriteLine($"Workspace: {_tempDir}");
            InitGitRepo(_tempDir);
        }

        _trelloClient = Substitute.For<ITrelloClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildDesignWorkflowConfig();
    }

    public void Dispose()
    {
        // Allow subprocesses to release file handles
        Thread.Sleep(500);

        if (!Directory.Exists(_tempDir))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — temp dir will be cleaned up by OS
        }
    }

    [Fact]
    public async Task Integration_Design_ClearSpec_ReturnsSuccess()
    {
        if (!_enabled) return;

        var description =
            "Build a REST API endpoint for user registration.\n\n" +
            "Requirements:\n" +
            "- Accept email and password in POST body\n" +
            "- Validate email format with regex\n" +
            "- Hash password with bcrypt (10 rounds)\n" +
            "- Store in PostgreSQL users table (id, email, password_hash, created_at)\n" +
            "- Return JWT token on success, 400 on validation failure, 409 on duplicate email\n\n" +
            "Tech stack: Node.js, Express, PostgreSQL, jsonwebtoken, bcryptjs";

        var runner = CreateRunner(CreateRealExecutor(maxBudgetUsd: 0.50m, timeoutSeconds: 180));
        SetupBoardCards(description);

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        _output.WriteLine($"Outcome: {result.Outcome}");
        _output.WriteLine($"ErrorDetail: {result.ErrorDetail ?? "(none)"}");

        Assert.True(result.Outcome == AgentOutcome.SUCCESS,
            $"Expected SUCCESS but got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");

        // Read the task file after execution
        var taskContent = await _taskFileManager.ReadTaskFileAsync(_tempDir, TargetCardId, CancellationToken.None);
        _output.WriteLine($"Task file length: {taskContent.Length} chars");

        // Claude should have added meaningful design content
        Assert.True(taskContent.Length > description.Length + 100,
            $"Expected task file to be substantially larger than original description. Got {taskContent.Length} chars.");

        // Verify the branch exists
        var branch = await _gitWorkspaceManager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal($"aiboard/{TargetCardId}", branch);
    }

    [Fact]
    public async Task Integration_Design_VagueSpec_ReturnsQuestions()
    {
        if (!_enabled) return;

        var runner = CreateRunner(CreateRealExecutor(maxBudgetUsd: 0.50m, timeoutSeconds: 180));
        SetupBoardCards("Make it better");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        _output.WriteLine($"Outcome: {result.Outcome}");
        _output.WriteLine($"ErrorDetail: {result.ErrorDetail ?? "(none)"}");

        // With a description this vague, Claude should ask questions
        Assert.True(result.Outcome == AgentOutcome.QUESTIONS,
            $"Expected QUESTIONS but got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");

        var taskContent = await _taskFileManager.ReadTaskFileAsync(_tempDir, TargetCardId, CancellationToken.None);
        _output.WriteLine($"Task file length: {taskContent.Length} chars");
        // Should contain question indicators
        Assert.Contains("?", taskContent);
    }

    [Fact]
    public async Task Integration_Design_TinyBudget_ReturnsErrorOrCompletes()
    {
        if (!_enabled) return;

        // Use extremely low budget — may error or may complete quickly on a simple task
        var runner = CreateRunner(CreateRealExecutor(maxBudgetUsd: 0.01m, timeoutSeconds: 60));
        SetupBoardCards("Build a comprehensive microservices architecture");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        _output.WriteLine($"Outcome: {result.Outcome}");
        _output.WriteLine($"ErrorDetail: {result.ErrorDetail ?? "(none)"}");

        // We accept either ERROR (budget exceeded) or SUCCESS (if it completed cheaply)
        // The key assertion is: no crash, no unhandled exception
        Assert.True(
            result.Outcome == AgentOutcome.ERROR || result.Outcome == AgentOutcome.SUCCESS,
            $"Expected ERROR or SUCCESS, got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");
    }

    private ClaudeAgentExecutor CreateRealExecutor(decimal maxBudgetUsd, int timeoutSeconds)
    {
        var options = Options.Create(new ClaudeCliLlmOptions
        {
            ExecutablePath = "claude",
            MaxBudgetUsd = maxBudgetUsd,
            TimeoutSeconds = timeoutSeconds,
        });

        return new ClaudeAgentExecutor(options, new XUnitLogger<ClaudeAgentExecutor>(_output));
    }

    private AgentRunner CreateRunner(IAgentExecutor executor)
    {
        return new AgentRunner(
            _trelloClient, executor, _taskFileManager, _gitWorkspaceManager,
            _workflowConfig, new XUnitLogger<AgentRunner>(_output));
    }

    private void SetupBoardCards(string targetDescription)
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<TrelloCard>
            {
                new(TargetCardId, "User Registration Endpoint", targetDescription, DesignListId),
                new("card-context-1", "Setup Database Migrations", "Configure Flyway for PostgreSQL schema management", DesignListId),
                new("card-context-2", "Add Auth Middleware", "JWT validation middleware for protected routes", DesignListId),
            });
    }

    private static WorkflowConfig BuildDesignWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [DesignListId] = new("Design", "senior_engineer", "agent_run",
                    "You are working on the task \"{TaskName}\" ({TaskId}). All project tasks are available in /.aiboard/tasks/ for context. Your job is to update ONLY the file for this task to add a detailed technical design approach. Include: architecture decisions, component interactions, data flow, edge cases, and implementation notes. Do not modify any other file.",
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("claude-sonnet-4-6",
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
