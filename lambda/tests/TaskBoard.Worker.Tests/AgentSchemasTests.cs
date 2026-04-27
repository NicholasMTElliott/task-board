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

    // ── Evaluator schema (Claude / generic) ──────────────────────────────────

    [Fact]
    public void EvaluatorOutcomeSchema_IsValidJson()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchema);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void EvaluatorOutcomeSchema_DeclaresWinnerIndexAndScores()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchema);
        var props = doc.RootElement.GetProperty("properties");
        Assert.True(props.TryGetProperty("winner_index", out _));
        Assert.True(props.TryGetProperty("scores", out _));
    }

    [Fact]
    public void EvaluatorOutcomeSchema_RequiresWinnerIndexAlwaysAsNullableInteger()
    {
        // v0.0.17: Replaced the prior if/then "required when outcome=COMPLETE"
        // formulation, which Claude CLI's --json-schema treated as a soft hint
        // rather than enforcing at the wire level. "Always required" with a
        // ["integer", "null"] type is reliably enforced; parser-side defense
        // in CandidateExecutor rejects outcome=COMPLETE with a null winner_index.
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchema);

        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("winner_index", required);

        var winnerType = doc.RootElement
            .GetProperty("properties")
            .GetProperty("winner_index")
            .GetProperty("type")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(new[] { "integer", "null" }, winnerType);
    }

    [Fact]
    public void EvaluatorOutcomeSchema_DoesNotUseIfThenConstruct()
    {
        // The if/then keywords were dropped because Claude CLI's --json-schema
        // does not enforce them at the wire level. Regression guard: if anyone
        // re-introduces them, the schema fix from issue #2 is silently lost.
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchema);
        Assert.False(doc.RootElement.TryGetProperty("if", out _),
            "EvaluatorOutcomeSchema must not use 'if' — Claude CLI does not enforce conditional schemas.");
        Assert.False(doc.RootElement.TryGetProperty("then", out _),
            "EvaluatorOutcomeSchema must not use 'then' — Claude CLI does not enforce conditional schemas.");
    }

    // ── Evaluator schema (OpenAI) ────────────────────────────────────────────

    [Fact]
    public void EvaluatorOutcomeSchemaOpenAI_IsValidJson()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchemaOpenAI);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void EvaluatorOutcomeSchemaOpenAI_RequiresWinnerIndexFieldAlways()
    {
        // OpenAI structured outputs don't support if/then, so winner_index is
        // listed in `required` (typed ["integer", "null"]) and the parser
        // rejects null when outcome=COMPLETE.
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchemaOpenAI);
        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("winner_index", required);

        var winnerType = doc.RootElement
            .GetProperty("properties")
            .GetProperty("winner_index")
            .GetProperty("type")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(new[] { "integer", "null" }, winnerType);
    }

    [Fact]
    public void EvaluatorOutcomeSchemaOpenAI_AdditionalPropertiesIsFalse()
    {
        // OpenAI structured outputs require additionalProperties:false on every object.
        using var doc = JsonDocument.Parse(AgentSchemas.EvaluatorOutcomeSchemaOpenAI);
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
    }
}
