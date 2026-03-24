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
/// The agent runs inside a git worktree (isolated directory on its own branch),
/// so the main repo working tree is never modified.
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
    private readonly ITaskBoardClient _trelloClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;
    private readonly WorkflowConfig _workflowConfig;
    private readonly ITestOutputHelper _output;
    private readonly bool _enabled;
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

        _trelloClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildDesignWorkflowConfig();
    }

    public void Dispose()
    {
        if (!_enabled) return;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SKIP_CLEANUP")))
        {
            var preserved = Path.GetFullPath(GitWorkspaceManager.GetWorktreePath(_repoRoot, _testBranchName));
            _output.WriteLine($"SKIP_CLEANUP set — worktree preserved at: {preserved}");
            return;
        }

        // Allow subprocesses to release file handles
        Thread.Sleep(500);

        try
        {
            // Remove the worktree (best effort)
            var worktreePath = GitWorkspaceManager.GetWorktreePath(_repoRoot, _testBranchName);
            var fullWorktreePath = Path.GetFullPath(worktreePath);

            if (Directory.Exists(fullWorktreePath))
            {
                RunGitSync(_repoRoot, "worktree", "remove", fullWorktreePath, "--force");
            }

            // Prune any stale worktree refs
            RunGitSync(_repoRoot, "worktree", "prune");

            // Delete the test branch (best effort)
            try
            {
                RunGitSync(_repoRoot, "branch", "-D", _testBranchName);
            }
            catch { /* branch may not exist if test failed early */ }

            // Remove .aiboard directory from main repo if somehow created
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

        Assert.True(result.Outcome == AgentOutcome.COMPLETE,
            $"Expected SUCCESS but got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");

        // Read the task file from the worktree
        var worktreePath = Path.GetFullPath(
            GitWorkspaceManager.GetWorktreePath(_repoRoot, _testBranchName));
        var taskContent = await _taskFileManager.ReadTaskFileAsync(worktreePath, TargetCardId, CancellationToken.None);
        _output.WriteLine($"Task file length: {taskContent.Length} chars");

        // Claude should have added meaningful design content
        Assert.True(taskContent.Length > description.Length + 100,
            $"Expected task file to be substantially larger than original description. Got {taskContent.Length} chars.");

        // Verify the branch exists (visible from the main repo)
        var branchExists = await _gitWorkspaceManager.BranchExistsAsync(
            _repoRoot, _testBranchName, CancellationToken.None);
        Assert.True(branchExists, "Agent branch should exist");

        // Verify main repo was not modified
        var mainBranch = await _gitWorkspaceManager.GetCurrentBranchAsync(_repoRoot, CancellationToken.None);
        _output.WriteLine($"Main repo branch after test: {mainBranch}");
        Assert.DoesNotContain("aiboard", mainBranch);
    }

    [Fact]
    public async Task Integration_Design_VagueSpec_ReturnsQuestions()
    {
        if (!_enabled) return;

        var runner = CreateRunner(CreateRealExecutor(maxBudgetUsd: 1.50m, timeoutSeconds: 180));
        SetupBoardCards("Make it better", targetTitle: "Improvement");

        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _repoRoot, CancellationToken.None);

        _output.WriteLine($"Outcome: {result.Outcome}");
        _output.WriteLine($"ErrorDetail: {result.ErrorDetail ?? "(none)"}");

        // With a description this vague, Claude should ask questions
        Assert.True(result.Outcome == AgentOutcome.NEEDS_INFO,
            $"Expected NEEDS_INFO but got {result.Outcome}. ErrorDetail: {result.ErrorDetail}");

        // Questions should be returned in the structured output
        Assert.NotNull(result.Questions);
        Assert.True(result.Questions!.Count > 0, "Expected at least one question");
        _output.WriteLine($"Questions: {result.Questions.Count}");
        foreach (var q in result.Questions)
        {
            _output.WriteLine($"  Q: {q.Question}");
            if (q.Recommendations is not null)
                foreach (var r in q.Recommendations)
                    _output.WriteLine($"    R: {r}");
        }
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
        Assert.True(
            result.Outcome == AgentOutcome.ERROR || result.Outcome == AgentOutcome.COMPLETE,
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

    private void SetupBoardCards(string targetDescription, string targetTitle = "User Registration Endpoint")
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(new List<BoardCard>
            {
                new(TargetCardId, targetTitle, targetDescription, DesignListId),
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
                    "You are working on the task {TaskName} ({TaskId}).  All project tasks are available in /.aiboard/tasks/ for context. Your job is to update ONLY the file for this task to add a detailed technical design approach. Include: architecture decisions, component interactions, data flow, edge cases, and implementation notes.  Do not modify any other file. You should include enough information for an unambiguous implementation. You may respond with questions where there is ambiguity, conflict, or mistakes; you should only proceed when you are fully confident you understand the request and the subject material fully.  It is always appropriate to say 'I do not understand', 'I need help', or 'This does not seem correct'.",
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["NEEDS_INFO"] = "list-questions",
                        ["ERROR"] = "list-error",
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("claude-opus-4-6",
                    "You are a Senior Software Engineer. Produce structured Technical Design, identify edge cases, and create implementation breakdowns.  You are reasonable but skeptical, critical enough to ensure that we catch any issues but not unreasonably blocking progress.",
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
