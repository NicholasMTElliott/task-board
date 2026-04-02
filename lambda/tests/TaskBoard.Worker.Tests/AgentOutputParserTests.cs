using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class AgentOutputParserTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // ParseResult
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_StructuredOutput_Complete()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","detail":"Done"}}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal("Done", result.Detail);
    }

    [Fact]
    public void ParseResult_StructuredOutput_NeedsInfo_WithQuestions()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "questions": [
                  {"question": "Which module?", "recommendations": ["Auth", "API"]},
                  {"question": "Deadline?"}
                ]
              }
            }
            """;

        var result = AgentOutputParser.ParseResult(json);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.NotNull(result.Questions);
        Assert.Equal(2, result.Questions!.Count);
        Assert.Equal("Which module?", result.Questions[0].Question);
        Assert.Equal(2, result.Questions[0].Recommendations!.Count);
        Assert.Null(result.Questions[1].Recommendations);
    }

    [Fact]
    public void ParseResult_StructuredOutput_RequestedSteps()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","requestedSteps":["security_review"]}}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.NotNull(result.RequestedSteps);
        Assert.Single(result.RequestedSteps!);
        Assert.Equal("security_review", result.RequestedSteps[0]);
    }

    [Fact]
    public void ParseResult_ResultField_FallsBackToKeywords()
    {
        var json = """{"result":"The task is COMPLETE."}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_OutputField_String()
    {
        var json = """{"output":"NEEDS_INFO: missing context"}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    [Fact]
    public void ParseResult_RawText_KeywordMatch()
    {
        Assert.Equal(AgentOutcome.COMPLETE, AgentOutputParser.ParseResult("COMPLETE").Outcome);
        Assert.Equal(AgentOutcome.NEEDS_INFO, AgentOutputParser.ParseResult("NEEDS_INFO").Outcome);
        Assert.Equal(AgentOutcome.ERROR, AgentOutputParser.ParseResult("something unknown").Outcome);
    }

    [Fact]
    public void ParseResult_BackwardCompat_Success()
    {
        var json = """{"structured_output":{"outcome":"SUCCESS"}}""";
        Assert.Equal(AgentOutcome.COMPLETE, AgentOutputParser.ParseResult(json).Outcome);
    }

    [Fact]
    public void ParseResult_BackwardCompat_Questions()
    {
        var json = """{"structured_output":{"outcome":"QUESTIONS"}}""";
        Assert.Equal(AgentOutcome.NEEDS_INFO, AgentOutputParser.ParseResult(json).Outcome);
    }

    [Fact]
    public void ParseResult_EmptyString_ReturnsError()
    {
        Assert.Equal(AgentOutcome.ERROR, AgentOutputParser.ParseResult("").Outcome);
    }

    [Fact]
    public void ParseResult_CaseInsensitive()
    {
        var json = """{"structured_output":{"outcome":"complete"}}""";
        Assert.Equal(AgentOutcome.COMPLETE, AgentOutputParser.ParseResult(json).Outcome);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // StripMarkdownFences
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StripMarkdownFences_NoFences_ReturnsInput()
    {
        Assert.Equal("hello", AgentOutputParser.StripMarkdownFences("hello"));
    }

    [Fact]
    public void StripMarkdownFences_WithFences_StripsThemAndTrims()
    {
        var input = "```json\n{\"outcome\":\"COMPLETE\"}\n```";
        Assert.Equal("{\"outcome\":\"COMPLETE\"}", AgentOutputParser.StripMarkdownFences(input));
    }

    [Fact]
    public void StripMarkdownFences_FenceOnly_ReturnsOriginal()
    {
        Assert.Equal("```", AgentOutputParser.StripMarkdownFences("```"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MinifyJson
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MinifyJson_RemovesWhitespace()
    {
        var input = """
            {
              "key": "value"
            }
            """;

        var result = AgentOutputParser.MinifyJson(input);

        Assert.Equal("""{"key":"value"}""", result);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ParseQuestions edge cases
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_EmptyQuestionsArray_ReturnsNullQuestions()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","questions":[]}}""";
        var result = AgentOutputParser.ParseResult(json);
        Assert.Null(result.Questions);
    }

    [Fact]
    public void ParseResult_EmptyRequestedSteps_ReturnsNull()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","requestedSteps":[]}}""";
        var result = AgentOutputParser.ParseResult(json);
        Assert.Null(result.RequestedSteps);
    }

    [Fact]
    public void ParseResult_MarkdownFencedJson_Parses()
    {
        var text = "```json\n{\"outcome\":\"ERROR\"}\n```";
        Assert.Equal(AgentOutcome.ERROR, AgentOutputParser.ParseResult(text).Outcome);
    }
}
