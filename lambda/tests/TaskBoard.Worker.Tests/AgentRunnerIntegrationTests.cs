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
/// Uses the actual repo root as the workspace so Claude has proper
/// workspace trust and project context.
///
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
    private readonly string _repoRoot;
    private readonly ITrelloClient _trelloClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly WorkflowConfig _workflowConfig;
    private readonly ITestOutputHelper _output;
    private readonly bool _enabled;
    private readonly string? _originalBranch;
    private readonly string _testBranchName;

    private const string DesignListId = "list-design";
    private const string TargetCardId = "card-integration-test";
    private const string BoardId = "board-integration";

    public AgentRunnerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _enabled =
            true;
            //!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_INTEGRATION_TESTS"));

        _repoRoot = FindRepoRoot();
        _testBranchName = $"aiboard/{TargetCardId}";

        _output.WriteLine($"Workspace (repo root): {_repoRoot}");

        if (_enabled)
        {
            // Remember current branch so we can restore it in cleanup
            _originalBranch = GetCurrentBranchSync(_repoRoot);
            _output.WriteLine($"Original branch: {_originalBranch}");
        }

        _trelloClient = Substitute.For<ITrelloClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildDesignWorkflowConfig();
    }

    public void Dispose()
    {
        if (!_enabled) return;

        // Allow subprocesses to release file handles
        Thread.Sleep(500);

        try
        {
            // Switch back to original branch
            if (!string.IsNullOrEmpty(_originalBranch))
            {
                RunGitSync(_repoRoot, "checkout", _originalBranch);
            }

            // Delete the test branch (best effort)
            try
            {
                RunGitSync(_repoRoot, "branch", "-D", _testBranchName);
            }
            catch { /* branch may not exist if test failed early */ }

            // Remove .aiboard directory if it was created
            var aiboardDir = Path.Combine(_repoRoot, ".aiboard");
            if (Directory.Exists(aiboardDir))
            {
                Directory.Delete(aiboardDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Cleanup warning: {ex.Message}");
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

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _repoRoot, CancellationToken.None);

        _output.WriteLine($"Outcome: {result.Outcome}");
        _output.WriteLine($"ErrorDetail: {result.ErrorDetail ?? "(none)"}");

        Assert.True(result.Outcome == AgentOutcome.SUCCESS,
            $"Expected SUCCESS but got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");

        // Read the task file after execution
        var taskContent = await _taskFileManager.ReadTaskFileAsync(_repoRoot, TargetCardId, CancellationToken.None);
        _output.WriteLine($"Task file length: {taskContent.Length} chars");

        // Claude should have added meaningful design content
        Assert.True(taskContent.Length > description.Length + 100,
            $"Expected task file to be substantially larger than original description. Got {taskContent.Length} chars.");

        // Verify the branch exists
        var branch = await _gitWorkspaceManager.GetCurrentBranchAsync(_repoRoot, CancellationToken.None);
        Assert.Equal($"aiboard/{TargetCardId}", branch);
    }

    [Fact]
    public async Task Integration_Design_VagueSpec_ReturnsQuestions()
    {
        if (!_enabled) return;

        var runner = CreateRunner(CreateRealExecutor(maxBudgetUsd: 0.50m, timeoutSeconds: 180));
        SetupBoardCards("Make it better");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _repoRoot, CancellationToken.None);

        _output.WriteLine($"Outcome: {result.Outcome}");
        _output.WriteLine($"ErrorDetail: {result.ErrorDetail ?? "(none)"}");

        // With a description this vague, Claude should ask questions
        Assert.True(result.Outcome == AgentOutcome.QUESTIONS,
            $"Expected QUESTIONS but got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");

        var taskContent = await _taskFileManager.ReadTaskFileAsync(_repoRoot, TargetCardId, CancellationToken.None);
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

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _repoRoot, CancellationToken.None);

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

    /// <summary>
    /// Walk up from the test assembly directory to find the repo root (where .git lives).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find repo root (no .git directory) starting from {AppContext.BaseDirectory}");
    }

    private static string GetCurrentBranchSync(string repoPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("--abbrev-ref");
        psi.ArgumentList.Add("HEAD");

        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
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
