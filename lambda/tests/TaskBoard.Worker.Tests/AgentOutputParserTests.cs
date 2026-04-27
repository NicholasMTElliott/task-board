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

    // ──────────────────────────────────────────────────────────────────────────
    // ParseResult — evaluator-specific fields (winner_index, scores)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_StructuredOutputWithWinnerIndex_PopulatesWinnerIndex()
    {
        // v0.0.18: when the model fills winner_index in the schema response,
        // capture it on AgentResult so ParseEvaluatorVerdict doesn't have to
        // re-parse the detail markdown for it. Closes the v0.0.16/17 gap where
        // schema-conformant responses lost their winner to a markdown-only
        // ParseEvaluatorVerdict path.
        var json = """{"structured_output":{"outcome":"COMPLETE","detail":"Done","winner_index":2}}""";
        var result = AgentOutputParser.ParseResult(json);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(2, result.WinnerIndex);
    }

    [Fact]
    public void ParseResult_StructuredOutputWithNullWinnerIndex_LeavesWinnerIndexNull()
    {
        // The OpenAI evaluator schema variant types winner_index as
        // ["integer", "null"] — both must round-trip cleanly.
        var json = """{"structured_output":{"outcome":"NEEDS_INFO","detail":"q","winner_index":null}}""";
        var result = AgentOutputParser.ParseResult(json);
        Assert.Null(result.WinnerIndex);
    }

    [Fact]
    public void ParseResult_StructuredOutputWithoutWinnerIndex_LeavesWinnerIndexNull()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","detail":"Done"}}""";
        var result = AgentOutputParser.ParseResult(json);
        Assert.Null(result.WinnerIndex);
    }

    [Fact]
    public void ParseResult_StructuredOutputWithScores_PopulatesScores()
    {
        var json = """
            {"structured_output":{"outcome":"COMPLETE","winner_index":1,"scores":[
                {"index":0,"score":6.5,"reasoning":"good but generic"},
                {"index":1,"score":8,"reasoning":"clear winner"}
            ]}}
            """;
        var result = AgentOutputParser.ParseResult(json);
        Assert.NotNull(result.Scores);
        Assert.Equal(2, result.Scores!.Count);
        Assert.Equal(0, result.Scores[0].Index);
        Assert.Equal(6.5m, result.Scores[0].Score);
        Assert.Equal("good but generic", result.Scores[0].Reasoning);
        Assert.Equal(1, result.Scores[1].Index);
        Assert.Equal(8m, result.Scores[1].Score);
    }

    [Fact]
    public void ParseResult_ScoresOutsideRange_DroppedToNullKeepReasoning()
    {
        // Sanitisation: scores outside [0, 10] are dropped to null so they
        // don't pollute v_provider_role_metrics.avg_quality_score; reasoning
        // is preserved either way (the absence of a score is itself useful data).
        var json = """
            {"structured_output":{"outcome":"COMPLETE","scores":[
                {"index":0,"score":15,"reasoning":"out of range high"},
                {"index":1,"score":-2,"reasoning":"out of range low"}
            ]}}
            """;
        var result = AgentOutputParser.ParseResult(json);
        Assert.NotNull(result.Scores);
        Assert.Equal(2, result.Scores!.Count);
        Assert.Null(result.Scores[0].Score);
        Assert.Equal("out of range high", result.Scores[0].Reasoning);
        Assert.Null(result.Scores[1].Score);
    }

    [Fact]
    public void ParseResult_DuplicateScoreIndices_LastWriteWins()
    {
        // If the evaluator emits two entries with the same index (revision
        // after second thought), keep the last occurrence so the final answer
        // wins. Mirrors the existing ParseEvaluatorVerdict behaviour for the
        // detail-extraction path.
        var json = """
            {"structured_output":{"outcome":"COMPLETE","scores":[
                {"index":0,"score":3,"reasoning":"first take"},
                {"index":0,"score":7,"reasoning":"on reflection"}
            ]}}
            """;
        var result = AgentOutputParser.ParseResult(json);
        Assert.NotNull(result.Scores);
        Assert.Single(result.Scores!);
        Assert.Equal(7m, result.Scores![0].Score);
        Assert.Equal("on reflection", result.Scores[0].Reasoning);
    }
}
