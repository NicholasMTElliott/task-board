using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class ClaudeCliLlmClientTests
{
    private static readonly ClaudeCliLlmOptions DefaultOptions = new()
    {
        ExecutablePath = "claude",
        MaxTurns = 5,
        MaxBudgetUsd = 2.00m,
        TimeoutSeconds = 300
    };

    [Fact]
    public void BuildArgumentList_ConstructsCorrectArguments()
    {
        var args = ClaudeCliLlmClient.BuildArgumentList(
            "claude-sonnet-4-20250514",
            "You are a BA.",
            "Analyze this ticket.",
            DefaultOptions);

        Assert.Contains("-p", args);
        Assert.Contains("Analyze this ticket.", args);
        Assert.Contains("--model", args);
        Assert.Contains("claude-sonnet-4-20250514", args);
        Assert.Contains("--system-prompt", args);
        Assert.Contains("You are a BA.", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("json", args);
        Assert.Contains("--json-schema", args);
        Assert.Contains("--max-budget-usd", args);
        Assert.Contains("2.00", args);
        Assert.Contains("--permission-mode", args);
        Assert.Contains("bypassPermissions", args);
    }

    [Fact]
    public void ExtractStructuredOutput_WithStructuredOutput_ReturnsIt()
    {
        var stdout = """
            {
              "result": "some text",
              "session_id": "sess-123",
              "structured_output": {
                "updates": { "Requirements": "Extracted." },
                "summaryComment": "Done.",
                "outcome": "COMPLETE",
                "approvalRequired": false
              }
            }
            """;

        var result = ClaudeCliLlmClient.ExtractStructuredOutput(stdout);

        Assert.Contains("Requirements", result);
        Assert.Contains("COMPLETE", result);
    }

    [Fact]
    public void ExtractStructuredOutput_WithResultOnly_ReturnsResultText()
    {
        var stdout = """
            {
              "result": "{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}",
              "session_id": "sess-123"
            }
            """;

        var result = ClaudeCliLlmClient.ExtractStructuredOutput(stdout);

        Assert.Contains("COMPLETE", result);
    }

    [Fact]
    public void ExtractStructuredOutput_RawText_ReturnsAsIs()
    {
        var stdout = "This is just raw text, not JSON";

        var result = ClaudeCliLlmClient.ExtractStructuredOutput(stdout);

        Assert.Equal(stdout, result);
    }

    [Fact]
    public void ExtractStructuredOutput_WithMarkdownFencedResult_StripsAndReturns()
    {
        var stdout = """
            {
              "result": "```json\n{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}\n```",
              "session_id": "sess-123"
            }
            """;

        var result = ClaudeCliLlmClient.ExtractStructuredOutput(stdout);

        Assert.Contains("COMPLETE", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ExtractStructuredOutput_WithMarkdownFencedRawStdout_StripsAndReturns()
    {
        var stdout = "```json\n{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}\n```";

        var result = ClaudeCliLlmClient.ExtractStructuredOutput(stdout);

        Assert.Contains("COMPLETE", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void BuildArgumentList_JsonSchemaIsSingleLine()
    {
        var args = ClaudeCliLlmClient.BuildArgumentList(
            "claude-haiku-4-5-20251001",
            "system",
            "user",
            DefaultOptions);

        var schemaIndex = Array.IndexOf(args, "--json-schema");
        var schemaValue = args[schemaIndex + 1];

        Assert.DoesNotContain('\n', schemaValue);
        Assert.DoesNotContain('\r', schemaValue);
    }
}
