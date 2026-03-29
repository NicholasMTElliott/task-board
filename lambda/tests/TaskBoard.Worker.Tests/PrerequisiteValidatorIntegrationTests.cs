using System.Text.Json;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Integration tests for PrerequisiteValidator that use the real workflow.github.json
/// and real filesystem. These tests verify the full validation flow end-to-end.
/// </summary>
public class PrerequisiteValidatorIntegrationTests
{
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

    private static WorkflowConfig LoadProductionConfig()
    {
        var repoRoot = FindRepoRoot();
        var configPath = Path.Combine(repoRoot, "workflow.github.json");
        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<WorkflowConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Normalised();
    }

    [Fact]
    public async Task ValidateAsync_ProductionConfig_StubProviders_GitAvailable_NoErrors()
    {
        // Full validation with real workflow.github.json + real filesystem + stub providers.
        // The only CLI check that runs is `git --version` (always validated).
        // This is the baseline "can the system start?" test.
        var repoRoot = FindRepoRoot();
        var config = LoadProductionConfig();

        var errors = await PrerequisiteValidator.ValidateAsync(
            config, "stub", "stub", repoRoot);

        Assert.True(errors.Count == 0,
            $"Prerequisite validation failed with stub providers:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public void ValidatePromptFiles_ProductionConfig_AllFilesExist()
    {
        // Validates that every prompt file referenced in workflow.github.json
        // actually exists in the repository. This catches typos and deleted files.
        var repoRoot = FindRepoRoot();
        var config = LoadProductionConfig();

        var errors = PrerequisiteValidator.ValidatePromptFiles(config, repoRoot);

        Assert.True(errors.Count == 0,
            $"Production workflow.github.json references missing prompt files:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public async Task ValidateAsync_MissingPromptFile_CollectsAllErrors()
    {
        // Verifies that when multiple prompt files are missing, all errors are
        // collected and returned rather than failing on the first missing file.
        var tempDir = Directory.CreateTempSubdirectory("prereq_test_").FullName;
        try
        {
            // Create a config with two missing files
            var config = new WorkflowConfig(
                States: new Dictionary<string, WorkflowState>
                {
                    ["s1"] = new WorkflowState(
                        "S1", null, "agent_run", null,
                        new Dictionary<string, TransitionTarget>(),
                        Steps:
                        [
                            new WorkflowStep("step1", "role_a", TaskPromptFile: "missing_a.md"),
                            new WorkflowStep("step2", "role_b", TaskPromptFile: "missing_b.md"),
                        ]),
                    ["done"] = new WorkflowState(
                        "Done", null, "terminal", null,
                        new Dictionary<string, TransitionTarget>())
                },
                Roles: new Dictionary<string, WorkflowRole>
                {
                    ["role_a"] = new WorkflowRole("opus", "prompt_a", new List<string>()),
                    ["role_b"] = new WorkflowRole("opus", "prompt_b", new List<string>())
                });

            var errors = await PrerequisiteValidator.ValidateAsync(
                config, "stub", "stub", tempDir);

            // Both missing files should be reported
            Assert.Contains(errors, e => e.Contains("missing_a.md"));
            Assert.Contains(errors, e => e.Contains("missing_b.md"));
            Assert.True(errors.Count >= 2,
                $"Expected at least 2 errors but got {errors.Count}: {string.Join(", ", errors)}");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
