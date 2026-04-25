using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Unit tests for <see cref="OpenCodeOutputParser"/> isolated from the executor
/// so parser strategy bugs show up without Docker/process plumbing noise.
/// </summary>
public class OpenCodeOutputParserTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void Parse_FencedJsonBlock_Returned()
    {
        var stdout = "prose\n\n```json\n{\"outcome\":\"COMPLETE\",\"detail\":\"ok\"}\n```\nend";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("\"outcome\":\"COMPLETE\"", json!);
    }

    [Fact]
    public void Parse_FencedJsonWithoutLanguageTag_Returned()
    {
        // Many models skip the "json" tag
        var stdout = "look:\n```\n{\"outcome\":\"NEEDS_INFO\",\"detail\":\"?\"}\n```";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("NEEDS_INFO", json!);
    }

    [Fact]
    public void Parse_WholeDocumentJson_Returned()
    {
        var stdout = "{\"outcome\":\"ERROR\",\"detail\":\"boom\"}";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("\"outcome\":\"ERROR\"", json!);
    }

    [Fact]
    public void Parse_TrailingInlineJson_Returned()
    {
        var stdout = "I considered {\"not_outcome\": true}, and in the end:\n"
            + "{\"outcome\":\"COMPLETE\",\"detail\":\"final\"}";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("\"detail\":\"final\"", json!);
    }

    [Fact]
    public void Parse_NoJsonInOutput_ReturnsNull()
    {
        var stdout = "I thought about this but I can't produce JSON.";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.Null(json);
    }

    [Fact]
    public void Parse_JsonWithoutOutcomeField_ReturnsNull()
    {
        var stdout = "{\"status\":\"done\",\"result\":42}";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.Null(json);
    }

    [Fact]
    public void Parse_MultipleOutcomeObjects_ReturnsLast()
    {
        // Model might emit a draft then a final answer.
        var stdout =
            "Draft: {\"outcome\":\"NEEDS_INFO\",\"detail\":\"wait\"}\n" +
            "Revision: {\"outcome\":\"COMPLETE\",\"detail\":\"final\"}";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("\"detail\":\"final\"", json!);
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsNull()
    {
        var (json, _) = OpenCodeOutputParser.Parse("", Logger);
        Assert.Null(json);
    }

    [Fact]
    public void Parse_JsonStringContainingBraces_DoesNotConfuseBalancer()
    {
        // A JSON string value containing { or } must not break the balance tracker.
        var stdout = "{\"outcome\":\"COMPLETE\",\"detail\":\"some text with { and } inside\"}";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("with { and }", json!);
    }

    [Fact]
    public void Parse_EscapedQuotesInString_DoesNotBreakExtraction()
    {
        var stdout = "prose {\"outcome\":\"COMPLETE\",\"detail\":\"he said \\\"hi\\\"\"}";
        var (json, _) = OpenCodeOutputParser.Parse(stdout, Logger);
        Assert.NotNull(json);
        Assert.Contains("said", json!);
    }
}
