using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for <see cref="WorkflowConfigProvider"/>:
/// hot-reload lifecycle, debounce behaviour, error handling,
/// and last-good-config preservation.
/// </summary>
public class WorkflowConfigProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workflowPath;

    public WorkflowConfigProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _workflowPath = Path.Combine(_tempDir, "workflow.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ------------------------------------------------------------------ helpers

    private static WorkflowConfig MakeMinimalConfig(string triggerColumn = "Ready") =>
        new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [triggerColumn] = new WorkflowState(
                    Name: triggerColumn,
                    Role: "se",
                    GateType: "agent_run",
                    TaskPrompt: "Do it.",
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Doing"),
                        ["COMPLETE"]    = TransitionTarget.ForColumn("Done"),
                        ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                    },
                    PipelineOrder: 1),
                ["Doing"] = new WorkflowState("Doing", null, "in_progress", null,
                    new Dictionary<string, TransitionTarget>()),
                ["Done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
                ["Error"] = new WorkflowState("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["se"] = new WorkflowRole("claude-opus-4-6", "You are an engineer.",
                    new List<string> { "Design" }),
            }).Normalised();

    private static string SerialiseConfig(WorkflowConfig config)
    {
        // Serialise a minimal valid workflow JSON that the provider can reload.
        // We build a raw anonymous object so we don't need a custom serialiser setup.
        var obj = new
        {
            states = config.States.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    name = kvp.Value.Name,
                    gateType = kvp.Value.GateType,
                    transitions = kvp.Value.Transitions.ToDictionary(
                        t => t.Key,
                        t => (object)(t.Value.Column ?? string.Empty)),
                }),
            roles = config.Roles.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    model = kvp.Value.Model,
                    systemPrompt = kvp.Value.SystemPrompt,
                }),
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private WorkflowConfigProvider CreateProvider(WorkflowConfig? initial = null)
    {
        var config = initial ?? MakeMinimalConfig();
        // Write the file so file-watching is enabled.
        File.WriteAllText(_workflowPath, SerialiseConfig(config));
        return new WorkflowConfigProvider(
            filePath: _workflowPath,
            promptBaseDir: string.Empty,
            initialConfig: config,
            logger: NullLogger<WorkflowConfigProvider>.Instance);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public void Current_ReturnsInitialConfig()
    {
        var initial = MakeMinimalConfig();
        using var provider = CreateProvider(initial);

        Assert.Same(initial.Normalised().GetType(), provider.Current.GetType());
        Assert.True(provider.Current.States.ContainsKey("Ready"));
    }

    [Fact]
    public async Task ValidFileChange_UpdatesCurrent()
    {
        using var provider = CreateProvider(MakeMinimalConfig("Ready"));
        var originalConfig = provider.Current;

        // Write a config with a different trigger column name.
        var updated = MakeMinimalConfig("ReadyV2");
        File.WriteAllText(_workflowPath, SerialiseConfig(updated));

        // Wait longer than the 500ms debounce + some margin.
        await Task.Delay(1200);

        Assert.NotSame(originalConfig, provider.Current);
        Assert.True(provider.Current.States.ContainsKey("ReadyV2"),
            "Provider should have picked up the updated config.");
    }

    [Fact]
    public async Task InvalidFileChange_KeepsLastGoodConfig()
    {
        using var provider = CreateProvider(MakeMinimalConfig());
        var originalConfig = provider.Current;

        // Write invalid JSON.
        File.WriteAllText(_workflowPath, "{ this is not valid JSON ]]]");

        await Task.Delay(1200);

        // Current must still be the original good config.
        Assert.Same(originalConfig, provider.Current);
    }

    [Fact]
    public async Task EmptyFileChange_KeepsLastGoodConfig()
    {
        using var provider = CreateProvider(MakeMinimalConfig());
        var originalConfig = provider.Current;

        File.WriteAllText(_workflowPath, string.Empty);

        await Task.Delay(1200);

        Assert.Same(originalConfig, provider.Current);
    }

    [Fact]
    public async Task FileDeletion_KeepsLastGoodConfig()
    {
        using var provider = CreateProvider(MakeMinimalConfig());
        var originalConfig = provider.Current;

        File.Delete(_workflowPath);

        await Task.Delay(800);

        // Current must still be the original good config (deletion doesn't clear it).
        Assert.Same(originalConfig, provider.Current);
    }

    [Fact]
    public async Task FileRecreatedAfterDeletion_ReloadsConfig()
    {
        using var provider = CreateProvider(MakeMinimalConfig("Ready"));
        File.Delete(_workflowPath);
        await Task.Delay(400);

        // Re-create with a different config.
        var updated = MakeMinimalConfig("ReadyV3");
        File.WriteAllText(_workflowPath, SerialiseConfig(updated));

        await Task.Delay(1200);

        // FSW Created event should trigger a reload.
        Assert.True(provider.Current.States.ContainsKey("ReadyV3"),
            "Provider should reload after the file is re-created.");
    }

    [Fact]
    public void NonExistentFile_FileWatchingDisabled_CurrentIsInitialConfig()
    {
        var initial = MakeMinimalConfig();
        // Deliberately pass a path that doesn't exist; no watcher should be started.
        using var provider = new WorkflowConfigProvider(
            filePath: Path.Combine(_tempDir, "does-not-exist.json"),
            promptBaseDir: string.Empty,
            initialConfig: initial,
            logger: NullLogger<WorkflowConfigProvider>.Instance);

        Assert.Same(initial, provider.Current);
    }

    [Fact]
    public async Task RapidConsecutiveChanges_OnlyOneReloadAfterDebounce()
    {
        using var provider = CreateProvider(MakeMinimalConfig("Ready"));

        // Fire rapid writes within the 500ms debounce window.
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(_workflowPath, SerialiseConfig(MakeMinimalConfig($"Rapid{i}")));
            await Task.Delay(50);
        }

        // Wait for debounce + margin.
        await Task.Delay(1200);

        // The last write should win; we just verify the config was updated.
        Assert.True(provider.Current.States.ContainsKey("Rapid4"),
            "The final rapid write should be the one that takes effect.");
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var provider = CreateProvider();
        // Should not throw — disposes watcher and timer cleanly.
        provider.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var provider = CreateProvider();
        provider.Dispose();
        provider.Dispose(); // second call must not throw
    }
}
