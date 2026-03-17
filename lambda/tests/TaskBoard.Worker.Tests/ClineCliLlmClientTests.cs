using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class ClineCliLlmClientTests
{
    private static readonly ClineCliLlmOptions DefaultOptions = new()
    {
        ExecutablePath = "cline",
        TimeoutSeconds = 300
    };

    [Fact]
    public void BuildArgumentList_ConstructsCorrectArguments()
    {
        var args = ClineCliLlmClient.BuildArgumentList(
            "claude-sonnet-4-20250514",
            "combined prompt here",
            DefaultOptions);

        Assert.Contains("-y", args);
        Assert.Contains("--json", args);
        Assert.Contains("--model", args);
        Assert.Contains("claude-sonnet-4-20250514", args);
        Assert.Contains("--timeout", args);
        Assert.Contains("300", args);
        Assert.Contains("combined prompt here", args);
    }

    [Fact]
    public void BuildCombinedPrompt_IncludesSystemAndUserPrompt()
    {
        var result = ClineCliLlmClient.BuildCombinedPrompt(
            "You are a BA.", "Analyze this ticket.");

        Assert.Contains("You are a BA.", result);
        Assert.Contains("Analyze this ticket.", result);
        Assert.Contains("Respond ONLY with valid JSON", result);
        Assert.Contains("User request:", result);
    }

    [Fact]
    public void ExtractResponseFromNdjson_WithSayMessages_ReturnsLastText()
    {
        var stdout = string.Join("\n",
            """{"type":"say","text":"Starting analysis...","ts":1000}""",
            """{"type":"say","text":"{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}","ts":2000}""");

        var result = ClineCliLlmClient.ExtractResponseFromNdjson(stdout);

        Assert.Contains("COMPLETE", result);
        Assert.Contains("Done.", result);
    }

    [Fact]
    public void ExtractResponseFromNdjson_WithPartialMessages_IgnoresThem()
    {
        var stdout = string.Join("\n",
            """{"type":"say","text":"partial content","ts":1000,"partial":true}""",
            """{"type":"say","text":"{\"updates\":{},\"summaryComment\":\"Final.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}","ts":2000}""");

        var result = ClineCliLlmClient.ExtractResponseFromNdjson(stdout);

        Assert.Contains("Final.", result);
        Assert.DoesNotContain("partial content", result);
    }

    [Fact]
    public void ExtractResponseFromNdjson_WithMarkdownFences_StripsAndReturns()
    {
        var stdout = """{"type":"say","text":"```json\n{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}\n```","ts":1000}""";

        var result = ClineCliLlmClient.ExtractResponseFromNdjson(stdout);

        Assert.Contains("COMPLETE", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ExtractResponseFromNdjson_EmptyOutput_Throws()
    {
        Assert.Throws<LlmApiException>(() =>
            ClineCliLlmClient.ExtractResponseFromNdjson(""));
    }

    [Fact]
    public void ExtractResponseFromNdjson_NoSayMessages_Throws()
    {
        var stdout = """{"type":"ask","text":"What should I do?","ts":1000}""";

        Assert.Throws<LlmApiException>(() =>
            ClineCliLlmClient.ExtractResponseFromNdjson(stdout));
    }

    [Fact]
    public void ExtractResponseFromNdjson_SkipsNonJsonLines()
    {
        var stdout = string.Join("\n",
            "some debug output",
            """{"type":"say","text":"{\"updates\":{},\"summaryComment\":\"OK.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}","ts":1000}""",
            "more debug");

        var result = ClineCliLlmClient.ExtractResponseFromNdjson(stdout);

        Assert.Contains("COMPLETE", result);
    }

    [Fact]
    public void ExtractResponseFromNdjson_PrefersCompletionResult()
    {
        var stdout = string.Join("\n",
            """{"type":"say","say":"task","text":"Starting task...","ts":1000}""",
            """{"type":"say","say":"text","text":"Working on it...","ts":2000}""",
            """{"type":"say","say":"completion_result","text":"{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}","ts":3000}""",
            """{"type":"say","say":"text","text":"Task completed.","ts":4000}""");

        var result = ClineCliLlmClient.ExtractResponseFromNdjson(stdout);

        Assert.Contains("COMPLETE", result);
        Assert.Contains("Done.", result);
    }

    [Fact]
    public void ExtractResponseFromNdjson_ExtractsEmbeddedJson()
    {
        // Simulates GPT responding conversationally with JSON embedded in text
        var stdout = """{"type":"say","say":"completion_result","text":"COMPLETE\n\n{\"updates\":{\"Requirements\":\"Captured.\"},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}","ts":1000}""";

        var result = ClineCliLlmClient.ExtractResponseFromNdjson(stdout);

        Assert.Contains("COMPLETE", result);
        Assert.Contains("Captured.", result);
        Assert.StartsWith("{", result);
    }

    [Fact]
    public void ExtractEmbeddedJson_FindsJsonInText()
    {
        var text = "COMPLETE\n\n{\"updates\":{},\"summaryComment\":\"Done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}";

        var result = ClineCliLlmClient.ExtractEmbeddedJson(text);

        Assert.NotNull(result);
        Assert.Contains("COMPLETE", result);
        Assert.StartsWith("{", result);
    }

    [Fact]
    public void ExtractEmbeddedJson_ReturnsNullWhenNoJson()
    {
        var text = "COMPLETE\n\nNo JSON content here, just plain text summary.";

        var result = ClineCliLlmClient.ExtractEmbeddedJson(text);

        Assert.Null(result);
    }

    [Fact]
    public void NormalizeResponse_MapsStatusToOutcome()
    {
        var json = """{"status":"COMPLETE","summary":"Done.","updates":[]}""";

        var result = ClineCliLlmClient.NormalizeResponse(json);

        Assert.Contains("\"outcome\":\"COMPLETE\"", result);
        Assert.Contains("\"summaryComment\":\"Done.\"", result);
        Assert.Contains("\"approvalRequired\":false", result);
        // updates array should become empty object
        Assert.Contains("\"updates\":{}", result);
    }

    [Fact]
    public void NormalizeResponse_PassesThroughCorrectFields()
    {
        var json = """{"outcome":"NEEDS_INFO","summaryComment":"Need more info.","updates":{"Questions":"What is the scope?"},"approvalRequired":false}""";

        var result = ClineCliLlmClient.NormalizeResponse(json);

        Assert.Contains("\"outcome\":\"NEEDS_INFO\"", result);
        Assert.Contains("\"summaryComment\":\"Need more info.\"", result);
        Assert.Contains("\"Questions\":\"What is the scope?\"", result);
        Assert.Contains("\"approvalRequired\":false", result);
    }

    [Fact]
    public void NormalizeResponse_DefaultsApprovalRequiredToFalse()
    {
        var json = """{"outcome":"COMPLETE","summaryComment":"Done.","updates":{}}""";

        var result = ClineCliLlmClient.NormalizeResponse(json);

        Assert.Contains("\"approvalRequired\":false", result);
    }

    [Fact]
    public void NormalizeResponse_ConvertsUpdatesArrayToEmptyObject()
    {
        var json = """{"status":"COMPLETE","summary":"Done.","updates":["item1"]}""";

        var result = ClineCliLlmClient.NormalizeResponse(json);

        Assert.Contains("\"updates\":{}", result);
    }
}
