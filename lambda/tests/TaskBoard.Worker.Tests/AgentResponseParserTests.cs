using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests;

public class AgentResponseParserTests
{
    [Fact]
    public void ValidJson_ReturnsSuccess()
    {
        var json = """
            {
                "updates": {"Requirements": "content"},
                "summaryComment": "Done.",
                "outcome": "COMPLETE",
                "approvalRequired": false
            }
            """;

        var result = AgentResponseParser.TryParse(json, out var response, out var error);

        Assert.True(result);
        Assert.NotNull(response);
        Assert.Null(error);
        Assert.Equal("COMPLETE", response.Outcome);
        Assert.Equal("Done.", response.SummaryComment);
        Assert.Single(response.Updates);
    }

    [Fact]
    public void MissingOutcome_ReturnsFailure()
    {
        var json = """
            {
                "updates": {},
                "summaryComment": "Done.",
                "approvalRequired": false
            }
            """;

        var result = AgentResponseParser.TryParse(json, out var response, out var error);

        Assert.False(result);
        Assert.Null(response);
        Assert.Contains("outcome", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingUpdates_ReturnsFailure()
    {
        var json = """
            {
                "summaryComment": "Done.",
                "outcome": "COMPLETE",
                "approvalRequired": false
            }
            """;

        var result = AgentResponseParser.TryParse(json, out var response, out var error);

        Assert.False(result);
        Assert.Null(response);
        Assert.Contains("updates", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletelyInvalidJson_ReturnsFailure()
    {
        var json = "this is not json at all";

        var result = AgentResponseParser.TryParse(json, out var response, out var error);

        Assert.False(result);
        Assert.Null(response);
        Assert.Contains("Invalid JSON", error);
    }

    [Fact]
    public void ExtraFieldsIgnored_ReturnsSuccess()
    {
        var json = """
            {
                "updates": {},
                "summaryComment": "Done.",
                "outcome": "COMPLETE",
                "approvalRequired": false,
                "extraField": "should be ignored",
                "anotherExtra": 42
            }
            """;

        var result = AgentResponseParser.TryParse(json, out var response, out var error);

        Assert.True(result);
        Assert.NotNull(response);
        Assert.Null(error);
    }
}
