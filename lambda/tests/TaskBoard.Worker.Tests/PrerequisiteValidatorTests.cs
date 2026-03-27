using System.Text.Json;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests;

public class PrerequisiteValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Builds a minimal WorkflowConfig with one agent_run state that uses
    /// prompt files under the given base directory.
    /// </summary>
    private static WorkflowConfig MakeConfigWithFiles(
        string baseDir,
        string taskPromptFile,
        string systemPromptFile,
        string? gateCheckFile = null)
    {
        var gateCheck = gateCheckFile is not null
            ? new GateCheckConfig("gate_checker", TaskPromptFile: gateCheckFile)
            : null;

        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["s1"] = new WorkflowState(
                    "S1", null, "agent_run", null,
                    new Dictionary<string, string>(),
                    Steps: [new WorkflowStep("step1", "ba", TaskPromptFile: taskPromptFile)],
                    GateCheck: gateCheck),
                ["done"] = new WorkflowState(
                    "Done", null, "terminal", null,
                    new Dictionary<string, string>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "", new List<string>(),
                    SystemPromptFile: systemPromptFile),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>())
            });
    }

    // ── Prompt file validation ───────────────────────────────────────────────

    [Fact]
    public void ValidatePromptFiles_AllExist_NoErrors()
    {
        var repoRoot = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(repoRoot, "workflow.github.json"));
        var config = JsonSerializer.Deserialize<WorkflowConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Normalised();

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, repoRoot);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatePromptFiles_MissingTaskPromptFile_ReportsError()
    {
        var tempDir = Path.GetTempPath();
        var sysPromptFile = Path.GetTempFileName();
        try
        {
            var config = MakeConfigWithFiles(tempDir, "missing/task.md", sysPromptFile);
            var errors = PrerequisiteValidator.ValidatePromptFiles(config, tempDir);

            Assert.Contains(errors, e => e.Contains("missing/task.md") && e.Contains("not found"));
        }
        finally
        {
            File.Delete(sysPromptFile);
        }
    }

    [Fact]
    public void ValidatePromptFiles_MissingSystemPromptFile_ReportsError()
    {
        var tempDir = Path.GetTempPath();
        var taskPromptFile = Path.GetTempFileName();
        try
        {
            var config = MakeConfigWithFiles(tempDir, taskPromptFile, "missing/system.md");
            var errors = PrerequisiteValidator.ValidatePromptFiles(config, tempDir);

            Assert.Contains(errors, e => e.Contains("missing/system.md") && e.Contains("not found"));
        }
        finally
        {
            File.Delete(taskPromptFile);
        }
    }

    [Fact]
    public void ValidatePromptFiles_MissingGateCheckPromptFile_ReportsError()
    {
        var tempDir = Path.GetTempPath();
        var taskPromptFile = Path.GetTempFileName();
        var sysPromptFile = Path.GetTempFileName();
        try
        {
            var config = MakeConfigWithFiles(tempDir, taskPromptFile, sysPromptFile,
                gateCheckFile: "missing/gate.md");
            var errors = PrerequisiteValidator.ValidatePromptFiles(config, tempDir);

            Assert.Contains(errors, e => e.Contains("missing/gate.md") && e.Contains("not found"));
        }
        finally
        {
            File.Delete(taskPromptFile);
            File.Delete(sysPromptFile);
        }
    }

    [Fact]
    public void ValidatePromptFiles_InlinePrompt_NoFileCheck()
    {
        // Steps with inline taskPrompt (no file) should not produce file errors
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["s1"] = new WorkflowState(
                    "S1", null, "agent_run", null,
                    new Dictionary<string, string>(),
                    Steps: [new WorkflowStep("step1", "ba", TaskPrompt: "Do it.")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "inline system prompt", new List<string>())
            });

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, Path.GetTempPath());

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatePromptFiles_DuplicateFiles_ReportedOnce()
    {
        // Two steps referencing the same missing file → error appears only once
        var tempDir = Path.GetTempPath();
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["s1"] = new WorkflowState(
                    "S1", null, "agent_run", null,
                    new Dictionary<string, string>(),
                    Steps:
                    [
                        new WorkflowStep("step1", "ba", TaskPromptFile: "shared/missing.md"),
                        new WorkflowStep("step2", "ba", TaskPromptFile: "shared/missing.md"),
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "prompt", new List<string>())
            });

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, tempDir);

        Assert.Single(errors, e => e.Contains("shared/missing.md"));
    }

    [Fact]
    public void ValidatePromptFiles_NonAgentRunStates_Skipped()
    {
        // manual_gate states with prompt file references should not trigger file checks
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["s1"] = new WorkflowState(
                    "S1", null, "manual_gate", null,
                    new Dictionary<string, string>(),
                    TaskPromptFile: "non_existent.md")
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "prompt", new List<string>())
            });

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, Path.GetTempPath());

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatePromptFiles_AbsolutePath_ResolvedCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Use an absolute path for the task prompt file
            var config = new WorkflowConfig(
                States: new Dictionary<string, WorkflowState>
                {
                    ["s1"] = new WorkflowState(
                        "S1", null, "agent_run", null,
                        new Dictionary<string, string>(),
                        Steps: [new WorkflowStep("step1", "ba", TaskPromptFile: tempFile)])
                },
                Roles: new Dictionary<string, WorkflowRole>
                {
                    ["ba"] = new WorkflowRole("opus", "prompt", new List<string>())
                });

            // Even with a different base dir, the absolute path should be found
            var errors = PrerequisiteValidator.ValidatePromptFiles(config, "C:\\nonexistent");

            Assert.Empty(errors);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ValidatePromptFiles_UnusedRole_NotChecked()
    {
        // A role defined in config but not referenced by any step should not be validated
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["s1"] = new WorkflowState(
                    "S1", null, "agent_run", null,
                    new Dictionary<string, string>(),
                    Steps: [new WorkflowStep("step1", "ba", TaskPrompt: "Do it.")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "prompt", new List<string>()),
                ["unused_role"] = new WorkflowRole("opus", "", new List<string>(),
                    SystemPromptFile: "prompts/never_checked.md") // would fail if checked
            });

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, Path.GetTempPath());

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatePromptFiles_MergeResolutionRole_Checked()
    {
        // The merge resolution role's system prompt file should be validated
        var tempDir = Path.GetTempPath();
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["s1"] = new WorkflowState(
                    "S1", null, "agent_run", null,
                    new Dictionary<string, string>(),
                    Steps: [new WorkflowStep("step1", "ba", TaskPrompt: "Do it.")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "prompt", new List<string>()),
                ["merge_resolver"] = new WorkflowRole("sonnet", "", new List<string>(),
                    SystemPromptFile: "prompts/missing_merge_resolver.md")
            },
            MergeResolution: new MergeResolutionConfig("merge_resolver"));

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, tempDir);

        Assert.Contains(errors, e => e.Contains("missing_merge_resolver.md") && e.Contains("not found"));
    }

    // ── GitHub options validation ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_GitHubProvider_NullOptions_ReportsError()
    {
        var config = MakeMinimalConfig();
        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "github", "stub", Path.GetTempPath(),
            githubOptions: null);

        Assert.Contains(errors, e => e.Contains("GitHubProjectsOptions not configured"));
    }

    [Fact]
    public async Task ValidateAsync_GitHubProvider_MissingOwner_ReportsError()
    {
        var config = MakeMinimalConfig();
        var options = new GitHubProjectsOptions { Owner = "", Repo = "user/repo", ProjectNumber = "1" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "github", "stub", Path.GetTempPath(),
            githubOptions: options);

        Assert.Contains(errors, e => e.Contains("GitHubProjects__Owner"));
    }

    [Fact]
    public async Task ValidateAsync_GitHubProvider_MissingRepo_ReportsError()
    {
        var config = MakeMinimalConfig();
        var options = new GitHubProjectsOptions { Owner = "user", Repo = "", ProjectNumber = "1" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "github", "stub", Path.GetTempPath(),
            githubOptions: options);

        Assert.Contains(errors, e => e.Contains("GitHubProjects__Repo"));
    }

    [Fact]
    public async Task ValidateAsync_GitHubProvider_MissingProjectNumber_ReportsError()
    {
        var config = MakeMinimalConfig();
        var options = new GitHubProjectsOptions { Owner = "user", Repo = "user/repo", ProjectNumber = "" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "github", "stub", Path.GetTempPath(),
            githubOptions: options);

        Assert.Contains(errors, e => e.Contains("GitHubProjects__ProjectNumber"));
    }

    // ── Trello options validation ────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_TrelloProvider_MissingApiKey_ReportsError()
    {
        var config = MakeMinimalConfig();
        var options = new TrelloClientOptions { ApiKey = "", ApiToken = "token" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "trello", "stub", Path.GetTempPath(),
            trelloOptions: options);

        Assert.Contains(errors, e => e.Contains("Trello API key"));
    }

    [Fact]
    public async Task ValidateAsync_TrelloProvider_MissingApiToken_ReportsError()
    {
        var config = MakeMinimalConfig();
        var options = new TrelloClientOptions { ApiKey = "key", ApiToken = "" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "trello", "stub", Path.GetTempPath(),
            trelloOptions: options);

        Assert.Contains(errors, e => e.Contains("Trello API token"));
    }

    [Fact]
    public async Task ValidateAsync_LiveProvider_TrelloAlias_ValidatesOptions()
    {
        // "live" is an alias for "trello" in the board provider switch
        var config = MakeMinimalConfig();
        var options = new TrelloClientOptions { ApiKey = "", ApiToken = "" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "live", "stub", Path.GetTempPath(),
            trelloOptions: options);

        Assert.Contains(errors, e => e.Contains("Trello API key"));
        Assert.Contains(errors, e => e.Contains("Trello API token"));
    }

    // ── Stub provider ────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_StubProvider_SkipsProviderChecks()
    {
        var config = MakeMinimalConfig();

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "stub", "stub", Path.GetTempPath());

        // Stub providers produce no provider-specific errors
        Assert.DoesNotContain(errors, e =>
            e.Contains("GitHubProjects") ||
            e.Contains("Trello") ||
            e.Contains("gh CLI"));
    }

    [Fact]
    public async Task ValidateAsync_UnrecognizedProvider_SkipsProviderChecks()
    {
        var config = MakeMinimalConfig();

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "some_future_provider", "stub", Path.GetTempPath());

        Assert.DoesNotContain(errors, e =>
            e.Contains("GitHubProjects") ||
            e.Contains("Trello") ||
            e.Contains("gh CLI"));
    }

    // ── Git availability ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_GitAvailable_NoGitError()
    {
        // git is expected to be on PATH in any dev/CI environment
        var config = MakeMinimalConfig();

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "stub", "stub", Path.GetTempPath());

        Assert.DoesNotContain(errors, e => e.Contains("git") && e.Contains("not found"));
    }

    // ── Error collection (all errors reported, not fail-fast) ───────────────

    [Fact]
    public async Task ValidateAsync_MultipleConfigErrors_AllReported()
    {
        var config = MakeMinimalConfig();
        // GitHub options with all three fields missing
        var options = new GitHubProjectsOptions { Owner = "", Repo = "", ProjectNumber = "" };

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "github", "stub", Path.GetTempPath(),
            githubOptions: options);

        // All three missing-field errors should appear together
        Assert.Contains(errors, e => e.Contains("Owner"));
        Assert.Contains(errors, e => e.Contains("Repo"));
        Assert.Contains(errors, e => e.Contains("ProjectNumber"));
    }

    // ── Helper ──────────────────────────────────────────────────────────────

    /// <summary>Minimal config with no prompt files to validate.</summary>
    private static WorkflowConfig MakeMinimalConfig() =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                ["done"] = new WorkflowState(
                    "Done", null, "terminal", null,
                    new Dictionary<string, string>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("opus", "prompt", new List<string>())
            });
}
