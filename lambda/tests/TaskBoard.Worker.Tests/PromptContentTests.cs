using System.Text.Json;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Regression tests that verify prompt files contain required testing guidelines.
/// These read the actual deployed prompt files and assert on key content,
/// catching accidental removal of testing guidance during future edits.
/// </summary>
public class PromptContentTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void SeniorEngineerPrompt_ContainsTestingPhilosophy()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "senior_engineer.md"));

        Assert.Contains("Testing Philosophy", content);
        Assert.Contains("contract", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unit tests", content);
        Assert.Contains("Integration tests", content);
        Assert.Contains("requirement", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplementationPrompt_ContainsTestingRequirements()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "states", "ready_for_implementation.md"));

        Assert.Contains("Testing Requirements", content);
        Assert.Contains("business logic", content);
        Assert.Contains("contract", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("integration tests", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requirement", content, StringComparison.OrdinalIgnoreCase);
        // References to existing test infrastructure
        Assert.True(
            content.Contains("xUnit") || content.Contains("NSubstitute") || content.Contains("StubTaskBoardClient"),
            "Implementation prompt should reference existing test infrastructure");
    }

    [Fact]
    public void ImplementationPrompt_ContainsPreCompletionChecklist()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "states", "ready_for_implementation.md"));

        Assert.Contains("Pre-Completion Checklist", content);
        Assert.Contains("builds successfully", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("All tests pass", content);
    }

    [Fact]
    public void QaPrompt_ContainsBlockingVsEnhancementDistinction()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "qa.md"));

        Assert.Contains("Blocking", content);
        Assert.Contains("Enhancement", content);
        Assert.True(
            content.Contains("enhance with value") || content.Contains("do not block out of routine"),
            "QA prompt should contain guidance about not blocking for enhancement-level gaps");
    }

    [Fact]
    public void TestPrompt_ContainsBlockingVsEnhancementDistinction()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "states", "ready_for_test.md"));

        Assert.Contains("blocking", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            content.Contains("enhancement", StringComparison.OrdinalIgnoreCase)
            || content.Contains("recommend", StringComparison.OrdinalIgnoreCase),
            "Test prompt should contain enhancement/recommend language");
    }

    [Fact]
    public void DesignPrompt_RequiresTestingStrategy()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "states", "ready_for_design.md"));

        Assert.Contains("testing strategy", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesignPrompt_ContainsOutputFormatSection()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "states", "ready_for_design.md"));

        Assert.Contains("Output Format", content);
        Assert.Contains("Design Review Summary", content);
        Assert.Contains("<details>", content);
    }

    [Fact]
    public void DesignPrompt_ContainsSummaryPolicy()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot, "prompts", "states", "ready_for_design.md"));

        Assert.Contains("Key Decisions", content);
        Assert.Contains("Assumptions", content);
        Assert.Contains("Scope", content);
        Assert.Contains("Risk", content);
    }

    [Fact]
    public void SeniorEngineerRole_IncludesDesignReviewSummarySection()
    {
        var configPath = Path.Combine(RepoRoot, "workflow.github.json");
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<WorkflowConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        var sections = config.Roles["senior_engineer"].Sections;
        Assert.Contains("Design Review Summary", sections);
    }

    [Fact]
    public void AllWorkflowPromptFiles_Exist()
    {
        var configPath = Path.Combine(RepoRoot, "workflow.github.json");
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<WorkflowConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        var missingFiles = new List<string>();

        foreach (var (stateKey, state) in config.States)
        {
            if (!string.IsNullOrEmpty(state.TaskPromptFile))
            {
                var fullPath = Path.Combine(RepoRoot, state.TaskPromptFile);
                if (!File.Exists(fullPath))
                    missingFiles.Add($"State '{stateKey}' taskPromptFile: {state.TaskPromptFile}");
            }

            if (state.Steps is not null)
            {
                foreach (var step in state.Steps)
                {
                    if (!string.IsNullOrEmpty(step.TaskPromptFile))
                    {
                        var fullPath = Path.Combine(RepoRoot, step.TaskPromptFile);
                        if (!File.Exists(fullPath))
                            missingFiles.Add($"Step '{step.Name}' in state '{stateKey}' taskPromptFile: {step.TaskPromptFile}");
                    }
                }
            }
        }

        foreach (var (roleKey, role) in config.Roles)
        {
            if (!string.IsNullOrEmpty(role.SystemPromptFile))
            {
                var fullPath = Path.Combine(RepoRoot, role.SystemPromptFile);
                if (!File.Exists(fullPath))
                    missingFiles.Add($"Role '{roleKey}' systemPromptFile: {role.SystemPromptFile}");
            }
        }

        Assert.True(missingFiles.Count == 0,
            $"Missing prompt files referenced in workflow.github.json:\n{string.Join("\n", missingFiles)}");
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
}
