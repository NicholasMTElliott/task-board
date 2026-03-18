using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class ClaudeAgentExecutorTests
{
    [Fact]
    public void ParseOutcome_StructuredOutput_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "some text",
              "structured_output": {
                "outcome": "SUCCESS"
              }
            }
            """;

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.SUCCESS, outcome);
    }

    [Fact]
    public void ParseOutcome_StructuredOutput_Questions()
    {
        var stdout = """
            {
              "result": "I have questions",
              "structured_output": {
                "outcome": "QUESTIONS",
                "detail": "Need more info"
              }
            }
            """;

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.QUESTIONS, outcome);
    }

    [Fact]
    public void ParseOutcome_ResultFieldWithJson_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "{\"outcome\":\"QUESTIONS\"}",
              "session_id": "sess-123"
            }
            """;

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.QUESTIONS, outcome);
    }

    [Fact]
    public void ParseOutcome_ResultFieldWithMarkdownFences_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "```json\n{\"outcome\":\"ERROR\"}\n```",
              "session_id": "sess-123"
            }
            """;

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.ERROR, outcome);
    }

    [Fact]
    public void ParseOutcome_RawText_FallsBackToKeywordMatch()
    {
        var stdout = "The task was completed with SUCCESS.";

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.SUCCESS, outcome);
    }

    [Fact]
    public void ParseOutcome_UnrecognizedText_ReturnsError()
    {
        var stdout = "Something went completely sideways, no outcome keyword present.";

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.ERROR, outcome);
    }

    [Fact]
    public void ParseOutcome_CaseInsensitive_Works()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "success"
              }
            }
            """;

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.SUCCESS, outcome);
    }

    [Fact]
    public void ParseOutcome_EmptyString_ReturnsError()
    {
        var outcome = ClaudeAgentExecutor.ParseOutcome("");

        Assert.Equal(AgentOutcome.ERROR, outcome);
    }

    [Fact]
    public void ParseOutcome_NullOutcomeInStructuredOutput_ReturnsError()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": null
              }
            }
            """;

        var outcome = ClaudeAgentExecutor.ParseOutcome(stdout);

        Assert.Equal(AgentOutcome.ERROR, outcome);
    }
}
