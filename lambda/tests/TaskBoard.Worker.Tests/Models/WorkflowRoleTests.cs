using System.Text.Json;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Models;

public class WorkflowRoleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void WorkflowRole_Deserialization_WithProvider_DeserializesCorrectly()
    {
        var json = """{"model":"codex-5.3","systemPrompt":"","sections":[],"provider":"codex"}""";

        var role = JsonSerializer.Deserialize<WorkflowRole>(json, JsonOptions)!;

        Assert.Equal("codex", role.Provider);
    }

    [Fact]
    public void WorkflowRole_Deserialization_WithoutProvider_DefaultsToClaudeCli()
    {
        var json = """{"model":"claude-opus-4-6","systemPrompt":"","sections":[]}""";

        var role = JsonSerializer.Deserialize<WorkflowRole>(json, JsonOptions)!;

        Assert.Equal("claude-cli", role.Provider);
    }

    [Fact]
    public void WorkflowRole_Constructor_DefaultProvider_IsClaudeCli()
    {
        var role = new WorkflowRole("model", "prompt", []);

        Assert.Equal("claude-cli", role.Provider);
    }
}
