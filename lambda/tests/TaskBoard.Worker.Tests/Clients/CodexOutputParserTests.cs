using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Direct tests for <see cref="CodexOutputParser"/> — the static helper
/// shared by <see cref="CodexAgentExecutor"/> (host) and
/// <see cref="DockerCodexAgentExecutor"/> (container).
/// </summary>
/// <remarks>
/// The host executor's instance method <c>ParseStreamOutput</c> is a thin
/// delegate over <see cref="CodexOutputParser.Parse"/>, and existing tests in
/// <see cref="CodexAgentExecutorTests"/> + <see cref="CodexParserCorpusTests"/>
/// exercise it through that delegate. This suite tests the helper directly so
/// (a) the parser is reachable from new call sites without paying the cost of
/// instantiating an executor, and (b) extraction-preserving behaviour is
/// pinned independently of the host wrapper.
/// </remarks>
public class CodexOutputParserTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ── Parse: legacy structured_output event ────────────────────────────────

    [Fact]
    public void Parse_SingleStructuredOutputLine_ReturnsCompleteOutcome()
    {
        const string stdout =
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"all done"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.COMPLETE, parsed.Outcome);
        Assert.Equal("all done", parsed.Detail);
    }

    [Fact]
    public void Parse_MultipleStructuredOutputs_ReturnsFirstOne()
    {
        // Codex shouldn't emit multiple, but if it does we use the first
        // (deterministic) and warn. Pin that behaviour.
        const string stdout =
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"first"}}""" + "\n" +
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"second"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal("first", parsed.Detail);
    }

    [Fact]
    public void Parse_EmptyStdout_ReturnsNullResult()
    {
        var (resultJson, _) = CodexOutputParser.Parse("", Logger);

        Assert.Null(resultJson);
    }

    [Fact]
    public void Parse_NoStructuredOutput_ReturnsNullResult()
    {
        // Random NDJSON noise without any structured_output and without any
        // agent_message. Parser must return null (not synthesize one).
        const string stdout =
            """{"type":"thread.started","thread_id":"abc"}""" + "\n" +
            """{"type":"turn.started"}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.Null(resultJson);
    }

    [Fact]
    public void Parse_MalformedLines_DoNotCrashAndAreSkipped()
    {
        const string stdout =
            "this is not json at all\n" +
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"recovered"}}""" + "\n" +
            "another bogus line";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.COMPLETE, parsed.Outcome);
        Assert.Equal("recovered", parsed.Detail);
    }

    // ── Parse: Codex CLI 0.125.0+ agent_message fallback ─────────────────────

    [Fact]
    public void Parse_NoStructuredOutputButAgentMessage_FallbackSynthesizesEnvelope()
    {
        // Codex CLI 0.125.0+ shape: outcome JSON in the text of the final
        // agent_message item.completed event, no top-level structured_output.
        const string stdout =
            """{"type":"thread.started"}""" + "\n" +
            """{"type":"item.completed","item":{"type":"agent_message","text":"{\"outcome\":\"COMPLETE\",\"detail\":\"via fallback\"}"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.COMPLETE, parsed.Outcome);
        Assert.Equal("via fallback", parsed.Detail);
    }

    [Fact]
    public void Parse_MultipleAgentMessages_LastWins()
    {
        // Intermediate agent_messages are common (reasoning steps); only the
        // final one carries the canonical outcome.
        const string stdout =
            """{"type":"item.completed","item":{"type":"agent_message","text":"thinking out loud"}}""" + "\n" +
            """{"type":"item.completed","item":{"type":"agent_message","text":"{\"outcome\":\"NEEDS_INFO\",\"detail\":\"need clarification\"}"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.NEEDS_INFO, parsed.Outcome);
        Assert.Equal("need clarification", parsed.Detail);
    }

    [Fact]
    public void Parse_StructuredOutputAndAgentMessageBothPresent_StructuredOutputWins()
    {
        // If both shapes appear (hybrid CLI build), the legacy structured_output
        // event takes precedence — no synthesis from agent_message.
        const string stdout =
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"legacy"}}""" + "\n" +
            """{"type":"item.completed","item":{"type":"agent_message","text":"{\"outcome\":\"ERROR\",\"detail\":\"agent-msg should not win\"}"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.COMPLETE, parsed.Outcome);
        Assert.Equal("legacy", parsed.Detail);
    }

    [Fact]
    public void Parse_AgentMessageWithoutOutcomeField_DoesNotSynthesize()
    {
        // The agent_message text must contain a recognised outcome enum value
        // for the fallback to fire. Random JSON shouldn't be promoted.
        const string stdout =
            """{"type":"item.completed","item":{"type":"agent_message","text":"{\"random\":\"object\"}"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.Null(resultJson);
    }

    [Fact]
    public void Parse_AgentMessageMalformedJson_DoesNotSynthesize()
    {
        const string stdout =
            """{"type":"item.completed","item":{"type":"agent_message","text":"this is just plain text, not JSON"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.Null(resultJson);
    }

    [Fact]
    public void Parse_AgentMessageWrappedInMarkdownFences_StillRecovered()
    {
        // Models sometimes wrap the JSON in ```json fences. The parser strips
        // them before attempting to wrap.
        const string stdout =
            """{"type":"item.completed","item":{"type":"agent_message","text":"```json\n{\"outcome\":\"COMPLETE\",\"detail\":\"fenced\"}\n```"}}""";

        var (resultJson, _) = CodexOutputParser.Parse(stdout, Logger);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.COMPLETE, parsed.Outcome);
    }

    // ── TryRecoverStructuredOutput ───────────────────────────────────────────

    [Fact]
    public void TryRecoverStructuredOutput_ValidStream_ReturnsResult()
    {
        const string stdout =
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"x"}}""";

        var recovered = CodexOutputParser.TryRecoverStructuredOutput(stdout, Logger);

        Assert.NotNull(recovered);
        Assert.Equal(AgentOutcome.COMPLETE, recovered.Outcome);
    }

    [Fact]
    public void TryRecoverStructuredOutput_GarbageStdout_ReturnsNull()
    {
        var recovered = CodexOutputParser.TryRecoverStructuredOutput("just some garbage", Logger);

        Assert.Null(recovered);
    }

    [Fact]
    public void TryRecoverStructuredOutput_EmptyStdout_ReturnsNull()
    {
        Assert.Null(CodexOutputParser.TryRecoverStructuredOutput("", Logger));
        Assert.Null(CodexOutputParser.TryRecoverStructuredOutput("   ", Logger));
    }

    [Fact]
    public void TryRecoverStructuredOutput_SingleDocumentJson_ReturnsResult()
    {
        // Backward-compat: stdout is a single JSON document with structured_output
        // at the top level (no NDJSON stream).
        const string stdout =
            """{"structured_output":{"outcome":"NEEDS_INFO","detail":"backward-compat"}}""";

        var recovered = CodexOutputParser.TryRecoverStructuredOutput(stdout, Logger);

        Assert.NotNull(recovered);
        Assert.Equal(AgentOutcome.NEEDS_INFO, recovered.Outcome);
    }

    // ── TryParseSingleDocumentStructured ─────────────────────────────────────

    [Fact]
    public void TryParseSingleDocumentStructured_ValidDocument_ReturnsTrue()
    {
        const string stdout =
            """{"structured_output":{"outcome":"COMPLETE","detail":"single-doc"}}""";

        Assert.True(CodexOutputParser.TryParseSingleDocumentStructured(stdout, out var result));
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void TryParseSingleDocumentStructured_NotJson_ReturnsFalse()
    {
        Assert.False(CodexOutputParser.TryParseSingleDocumentStructured("not json at all", out _));
    }

    [Fact]
    public void TryParseSingleDocumentStructured_JsonWithoutStructuredOutput_ReturnsFalse()
    {
        const string stdout = """{"some":"object","but":"no structured_output"}""";

        Assert.False(CodexOutputParser.TryParseSingleDocumentStructured(stdout, out _));
    }

    // ── TryWrapAgentMessageAsStructured ──────────────────────────────────────

    [Theory]
    [InlineData("COMPLETE")]
    [InlineData("NEEDS_INFO")]
    [InlineData("ERROR")]
    [InlineData("SUCCESS")]
    [InlineData("QUESTIONS")]
    public void TryWrapAgentMessageAsStructured_RecognisedOutcome_Wraps(string outcome)
    {
        var text = $$"""{"outcome":"{{outcome}}","detail":"x"}""";

        Assert.True(CodexOutputParser.TryWrapAgentMessageAsStructured(text, out var wrapped));
        Assert.StartsWith("{\"structured_output\":", wrapped);
        Assert.Contains(outcome, wrapped);
    }

    [Fact]
    public void TryWrapAgentMessageAsStructured_UnrecognisedOutcome_DoesNotWrap()
    {
        const string text = """{"outcome":"WHATEVER","detail":"x"}""";

        Assert.False(CodexOutputParser.TryWrapAgentMessageAsStructured(text, out _));
    }

    [Fact]
    public void TryWrapAgentMessageAsStructured_NotAJsonObject_DoesNotWrap()
    {
        Assert.False(CodexOutputParser.TryWrapAgentMessageAsStructured("[1,2,3]", out _));
        Assert.False(CodexOutputParser.TryWrapAgentMessageAsStructured("\"just a string\"", out _));
    }

    [Fact]
    public void TryWrapAgentMessageAsStructured_OutcomeFieldNotString_DoesNotWrap()
    {
        Assert.False(CodexOutputParser.TryWrapAgentMessageAsStructured(
            """{"outcome":42}""", out _));
    }

    // ── KnownEventTypes ──────────────────────────────────────────────────────

    [Fact]
    public void KnownEventTypes_IsCaseInsensitive()
    {
        Assert.Contains("turn.completed", CodexOutputParser.KnownEventTypes);
        Assert.Contains("Turn.Completed", CodexOutputParser.KnownEventTypes);
    }

    [Fact]
    public void KnownEventTypes_IncludesAllShapesUsedAcrossCodexVersions()
    {
        // Pin the set so that adding a new event type is a deliberate code
        // change reflected in tests and not silently silenced.
        var expected = new[]
        {
            "thread.started",
            "turn.started", "turn.completed",
            "item.started", "item.completed", "item.updated",
            "response.started", "response.completed",
            "assistant", "user", "system",
        };
        foreach (var t in expected)
            Assert.Contains(t, CodexOutputParser.KnownEventTypes);
    }

    // ── BuildNoStructuredOutputDiagnostic ────────────────────────────────────

    [Fact]
    public void BuildNoStructuredOutputDiagnostic_IncludesStdoutLineCount()
    {
        const string stdout = "line1\nline2\nline3";
        var report = CodexOutputParser.BuildNoStructuredOutputDiagnostic(stdout, "", null);

        Assert.Contains("3 non-empty lines", report);
    }

    [Fact]
    public void BuildNoStructuredOutputDiagnostic_IncludesHintWhenProvided()
    {
        var hint = new CliFailureHintDetector.FailureHint("Auth", "OPENAI_API_KEY missing");
        var report = CodexOutputParser.BuildNoStructuredOutputDiagnostic(
            "stdout sample", "stderr sample", hint);

        Assert.Contains("Auth", report);
        Assert.Contains("OPENAI_API_KEY missing", report);
    }

    [Fact]
    public void BuildNoStructuredOutputDiagnostic_TruncatesLongStderr()
    {
        var longStderr = new string('x', 5000);
        var report = CodexOutputParser.BuildNoStructuredOutputDiagnostic(
            "stdout", longStderr, null);

        Assert.Contains("truncated", report);
        // Body should not exceed reasonable size — full 5000 chars must be cut.
        Assert.True(report.Length < 4000,
            $"Expected truncation but report is {report.Length} chars long");
    }
}
