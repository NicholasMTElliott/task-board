using System.Text.Json;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class AgentSchemasTests
{
    [Fact]
    public void OutcomeSchema_IsValidJson()
    {
        // Must parse without throwing
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void OutcomeSchema_RequiresOutcomeField()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        var required = doc.RootElement.GetProperty("required");

        Assert.Equal(JsonValueKind.Array, required.ValueKind);
        var items = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("outcome", items);
    }

    [Fact]
    public void OutcomeSchema_OutcomeEnumContainsAllValues()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        var outcomeEnum = doc.RootElement
            .GetProperty("properties")
            .GetProperty("outcome")
            .GetProperty("enum");

        var values = outcomeEnum.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("COMPLETE", values);
        Assert.Contains("NEEDS_INFO", values);
        Assert.Contains("ERROR", values);
    }

    [Fact]
    public void OutcomeSchema_HasDetailProperty()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        Assert.True(
            doc.RootElement.GetProperty("properties").TryGetProperty("detail", out _),
            "Schema must define a 'detail' property");
    }

    [Fact]
    public void OutcomeSchema_HasQuestionsProperty()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        Assert.True(
            doc.RootElement.GetProperty("properties").TryGetProperty("questions", out _),
            "Schema must define a 'questions' property");
    }

    [Fact]
    public void OutcomeSchema_HasRequestedStepsProperty()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        Assert.True(
            doc.RootElement.GetProperty("properties").TryGetProperty("requestedSteps", out _),
            "Schema must define a 'requestedSteps' property");
    }
}
