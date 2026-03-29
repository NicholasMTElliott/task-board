using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Tests for prompt path resolution using ConfigDirectory (portable deployment behavior).
/// </summary>
public class AgentRunnerPathResolutionTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _worktreeDir;

    public AgentRunnerPathResolutionTests()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _configDir = Path.Combine(Path.GetTempPath(), $"configdir-{id}");
        _worktreeDir = Path.Combine(Path.GetTempPath(), $"worktree-{id}");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_worktreeDir);
    }

    public void Dispose()
    {
        DeleteDir(_configDir);
        DeleteDir(_worktreeDir);
    }

    private static void DeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        Directory.Delete(path, recursive: true);
    }

    // ── ResolveSystemPromptFileAsync ──────────────────────────────────────────

    [Fact]
    public async Task ResolveSystemPromptFileAsync_WithConfigDirectory_ResolvesFromConfigDir()
    {
        // Arrange: prompt exists in configDir but NOT in worktreeDir
        var promptRelPath = "prompts/senior_engineer.md";
        var promptFullPath = Path.Combine(_configDir, promptRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
        await File.WriteAllTextAsync(promptFullPath, "You are a senior engineer.");

        var role = new WorkflowRole("model", "", new List<string>(), SystemPromptFile: promptRelPath);

        // Act
        var path = await AgentRunner.ResolveSystemPromptFileAsync(
            role, "senior_engineer", _worktreeDir, _configDir, CancellationToken.None);

        // Assert: resolved to configDir, not worktreeDir
        Assert.Equal(Path.GetFullPath(promptFullPath), path);
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_WithNullConfigDirectory_FallsBackToWorktree()
    {
        // Arrange: prompt exists in worktreeDir
        var promptRelPath = "prompts/qa.md";
        var promptFullPath = Path.Combine(_worktreeDir, promptRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
        await File.WriteAllTextAsync(promptFullPath, "You are a QA engineer.");

        var role = new WorkflowRole("model", "", new List<string>(), SystemPromptFile: promptRelPath);

        // Act: pass null for configDirectory — should fall back to worktreePath
        var path = await AgentRunner.ResolveSystemPromptFileAsync(
            role, "qa", _worktreeDir, null, CancellationToken.None);

        // Assert: resolved to worktreeDir
        Assert.Equal(Path.GetFullPath(promptFullPath), path);
    }

    [Fact]
    public async Task ResolveSystemPromptFileAsync_InlinePrompt_TempFileWrittenToWorktree()
    {
        // Arrange: role has inline SystemPrompt (no file), configDirectory is set
        var role = new WorkflowRole("model", "You are an agent.", new List<string>());

        // Act
        var path = await AgentRunner.ResolveSystemPromptFileAsync(
            role, "agent", _worktreeDir, _configDir, CancellationToken.None);

        // Assert: temp file is written to worktreeDir/.aiboard/, not configDir
        Assert.StartsWith(_worktreeDir, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    // ── ResolveStepTaskPromptAsync ────────────────────────────────────────────

    [Fact]
    public async Task ResolveStepTaskPromptAsync_WithConfigDirectory_ResolvesFromConfigDir()
    {
        // Arrange: task prompt exists in configDir but NOT in worktreeDir
        var promptRelPath = "prompts/states/ready_for_design.md";
        var promptFullPath = Path.Combine(_configDir, promptRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
        await File.WriteAllTextAsync(promptFullPath, "Design task {TaskName} ({TaskId}).");

        var step = new WorkflowStep("create_design", "senior_engineer", TaskPromptFile: promptRelPath);
        var card = new BoardCard("card-42", "Auth Module", "desc", "list-design");

        // Act
        var prompt = await AgentRunner.ResolveStepTaskPromptAsync(
            step, _worktreeDir, card, _configDir, CancellationToken.None);

        // Assert: prompt loaded from configDir with placeholders resolved
        Assert.Equal("Design task Auth Module (card-42).", prompt);
    }

    [Fact]
    public async Task ResolveStepTaskPromptAsync_WithNullConfigDirectory_FallsBackToWorktree()
    {
        // Arrange: task prompt exists in worktreeDir
        var promptRelPath = "prompts/states/ready_for_implementation.md";
        var promptFullPath = Path.Combine(_worktreeDir, promptRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
        await File.WriteAllTextAsync(promptFullPath, "Implement {TaskName}.");

        var step = new WorkflowStep("implement", "implementer", TaskPromptFile: promptRelPath);
        var card = new BoardCard("card-1", "Login Feature", "desc", "list-impl");

        // Act
        var prompt = await AgentRunner.ResolveStepTaskPromptAsync(
            step, _worktreeDir, card, null, CancellationToken.None);

        // Assert
        Assert.Equal("Implement Login Feature.", prompt);
    }

    // ── ResolveTaskPromptAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveTaskPromptAsync_WithConfigDirectory_ResolvesFromConfigDir()
    {
        // Arrange: task prompt exists in configDir
        var promptRelPath = "prompts/states/ready_for_test.md";
        var promptFullPath = Path.Combine(_configDir, promptRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
        await File.WriteAllTextAsync(promptFullPath, "Test task {TaskName} ({TaskId}).");

        var state = new WorkflowState("Ready for Test", "qa", "agent_run", null,
            new Dictionary<string, TransitionTarget>(), TaskPromptFile: promptRelPath);
        var card = new BoardCard("card-5", "Payment Flow", "desc", "list-test");

        // Act
        var prompt = await AgentRunner.ResolveTaskPromptAsync(
            state, _worktreeDir, card, _configDir, CancellationToken.None);

        // Assert
        Assert.Equal("Test task Payment Flow (card-5).", prompt);
    }

    [Fact]
    public async Task ResolveTaskPromptAsync_WithNullConfigDirectory_FallsBackToWorktree()
    {
        // Arrange: task prompt exists in worktreeDir
        var promptRelPath = "prompts/states/ready_for_test.md";
        var promptFullPath = Path.Combine(_worktreeDir, promptRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(promptFullPath)!);
        await File.WriteAllTextAsync(promptFullPath, "Test {TaskName}.");

        var state = new WorkflowState("Ready for Test", "qa", "agent_run", null,
            new Dictionary<string, TransitionTarget>(), TaskPromptFile: promptRelPath);
        var card = new BoardCard("card-3", "Search Feature", "desc", "list-test");

        // Act
        var prompt = await AgentRunner.ResolveTaskPromptAsync(
            state, _worktreeDir, card, null, CancellationToken.None);

        // Assert
        Assert.Equal("Test Search Feature.", prompt);
    }

    // ── WorkflowConfig.ConfigDirectory ───────────────────────────────────────

    [Fact]
    public void WorkflowConfig_ConfigDirectory_CanBeSetAndRead()
    {
        var config = new WorkflowConfig(
            new Dictionary<string, WorkflowState>(),
            new Dictionary<string, WorkflowRole>());

        config.ConfigDirectory = "/some/config/dir";

        Assert.Equal("/some/config/dir", config.ConfigDirectory);
    }

    [Fact]
    public void WorkflowConfig_ConfigDirectory_IsNotSerialised()
    {
        var config = new WorkflowConfig(
            new Dictionary<string, WorkflowState>(),
            new Dictionary<string, WorkflowRole>())
        {
            ConfigDirectory = "/some/path"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        Assert.DoesNotContain("configDirectory", json);
        Assert.DoesNotContain("/some/path", json);
    }

    [Fact]
    public void WorkflowConfig_Normalised_PreservesConfigDirectory()
    {
        var config = new WorkflowConfig(
            new Dictionary<string, WorkflowState>(),
            new Dictionary<string, WorkflowRole>())
        {
            ConfigDirectory = "/config/dir"
        };

        var normalised = config.Normalised();

        // ConfigDirectory is set after Normalised() in production, but confirm the with expression
        // copies it if it was set before
        Assert.Equal("/config/dir", normalised.ConfigDirectory);
    }
}
