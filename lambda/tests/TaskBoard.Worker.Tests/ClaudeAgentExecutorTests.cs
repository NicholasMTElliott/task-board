using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class ClaudeAgentExecutorTests
{
    [Fact]
    public void ParseResult_StructuredOutput_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "some text",
              "structured_output": {
                "outcome": "SUCCESS"
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_StructuredOutput_NeedsInfo()
    {
        var stdout = """
            {
              "result": "I have questions",
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "detail": "Need more info"
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Need more info", result.Detail);
    }

    [Fact]
    public void ParseResult_StructuredOutput_WithQuestions()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "detail": "Spec is too vague",
                "questions": [
                  {
                    "question": "What is the target component?",
                    "recommendations": ["Auth module", "API gateway"]
                  },
                  {
                    "question": "What problem are we solving?"
                  }
                ]
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Spec is too vague", result.Detail);
        Assert.NotNull(result.Questions);
        Assert.Equal(2, result.Questions!.Count);
        Assert.Equal("What is the target component?", result.Questions[0].Question);
        Assert.NotNull(result.Questions[0].Recommendations);
        Assert.Equal(2, result.Questions[0].Recommendations!.Count);
        Assert.Equal("Auth module", result.Questions[0].Recommendations![0]);
        Assert.Equal("What problem are we solving?", result.Questions[1].Question);
        Assert.Null(result.Questions[1].Recommendations);
    }

    [Fact]
    public void ParseResult_StructuredOutput_EmptyQuestions_ReturnsNull()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "questions": []
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Null(result.Questions);
    }

    [Fact]
    public void ParseResult_ResultFieldWithJson_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "{\"outcome\":\"QUESTIONS\"}",
              "session_id": "sess-123"
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    [Fact]
    public void ParseResult_ResultFieldWithMarkdownFences_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "```json\n{\"outcome\":\"ERROR\"}\n```",
              "session_id": "sess-123"
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_RawText_FallsBackToKeywordMatch()
    {
        var stdout = "The task was completed with SUCCESS.";

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_UnrecognizedText_ReturnsError()
    {
        var stdout = "Something went wrong sideways, no outcome keyword present.";

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_CaseInsensitive_Works()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "success"
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_EmptyString_ReturnsError()
    {
        var result = ClaudeAgentExecutor.ParseResult("");

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_NullOutcomeInStructuredOutput_ReturnsError()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": null
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }
}
