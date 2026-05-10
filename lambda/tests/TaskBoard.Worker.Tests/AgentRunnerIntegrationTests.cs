using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;
using Xunit.Abstractions;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Integration tests that invoke real Claude CLI as a coding agent.
/// Uses the actual repo root as the workspace so Claude has proper
/// workspace trust and project context.
///
/// The agent runs inside a git worktree (isolated directory on its own branch),
/// so the main repo working tree is never modified.
///
/// With "discard" gitBehavior, the worktree and branch are cleaned up after execution.
/// Verification is done through board client mock interactions.
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

    private const string DesignListId = "list-design";
    private const string TargetCardId = "card-integration-test";
    private const string BoardId = "board-integration";

    public AgentRunnerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_INTEGRATION_TESTS"));

        _repoRoot = FindRepoRoot();

        _output.WriteLine($"Workspace (repo root): {_repoRoot}");

        _trelloClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
        _workflowConfig = BuildDesignWorkflowConfig().Normalised();
    }

    public void Dispose()
    {
        if (!_enabled) return;

        // Allow subprocesses to release file handles
        Thread.Sleep(500);

        try
        {
            // With discard behavior, worktree and branch should already be cleaned up.
            // Cleanup here is best-effort for any leftover state.

            // Find any leftover branches for the test card
            var branchSearch = RunGitSyncWithOutput(_repoRoot, "branch", "--list", $"aiboard/{TargetCardId}-*");
            var legacyBranch = RunGitSyncWithOutput(_repoRoot, "branch", "--list", $"aiboard/{TargetCardId}");

            foreach (var branch in (branchSearch + "\n" + legacyBranch)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.TrimStart('*', ' ')))
            {
                var worktreePath = GitWorkspaceManager.GetWorktreePath(_repoRoot, branch);
                var fullPath = Path.GetFullPath(worktreePath);
                if (Directory.Exists(fullPath))
                {
                    try { RunGitSync(_repoRoot, "worktree", "remove", fullPath, "--force"); } catch { }
                }

                try { RunGitSync(_repoRoot, "branch", "-D", branch); } catch { }
            }

            RunGitSync(_repoRoot, "worktree", "prune");

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

        // With discard behavior, the card body should have been updated via the board client
        await _trelloClient.Received(1).UpdateCardBodyAsync(
            TargetCardId,
            Arg.Is<string>(s => s.Length > description.Length + 100),
            Arg.Any<CancellationToken>());

        // Verify main repo was not modified
        var mainBranch = await _gitWorkspaceManager.GetCurrentBranchAsync(_repoRoot, CancellationToken.None);
        _output.WriteLine($"Main repo branch after test: {mainBranch}");
        Assert.DoesNotContain("aiboard", mainBranch);

        // With discard behavior, branch should be cleaned up
        var existingBranch = await _gitWorkspaceManager.FindBranchByPrefixAsync(
            _repoRoot, TargetCardId, CancellationToken.None);
        Assert.Null(existingBranch);
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
            _trelloClient, AgentExecutorResolver.ForSingleExecutor(executor), _taskFileManager, _gitWorkspaceManager,
            _workflowConfig, new StubCrossReferenceResolver(), new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(_trelloClient, _workflowConfig, new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            new XUnitLogger<AgentRunner>(_output));
    }

    private void SetupBoardCards(string targetDescription, string targetTitle = "User Registration Endpoint")
    {
        _trelloClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
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
                [DesignListId] = new("Design", null, "agent_run",
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
                        new WorkflowStep("senior_engineer", "senior_engineer", TaskPrompt: "You are working on the task {TaskName} ({TaskId}).  All project tasks are available in /.aiboard/tasks/ for context. Your job is to update ONLY the file for this task to add a detailed technical design approach. Include: architecture decisions, component interactions, data flow, edge cases, and implementation notes.  Do not modify any other file. You should include enough information for an unambiguous implementation. You may respond with questions where there is ambiguity, conflict, or mistakes; you should only proceed when you are fully confident you understand the request and the subject material fully.  It is always appropriate to say 'I do not understand', 'I need help', or 'This does not seem correct'."),
                    ]),
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
            var gitPath = Path.Combine(dir, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find repo root (no .git directory) starting from {AppContext.BaseDirectory}");
    }

}
