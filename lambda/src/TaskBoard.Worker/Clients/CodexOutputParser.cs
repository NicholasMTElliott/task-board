using System.Text;
using System.Text.Json;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// NDJSON parsing for the OpenAI Codex CLI's <c>codex exec --json</c> stream
/// output. Shared between <see cref="CodexAgentExecutor"/> (host CLI) and
/// <see cref="DockerCodexAgentExecutor"/> (containerized CLI) — the wire shape
/// is identical regardless of where the CLI runs.
/// </summary>
/// <remarks>
/// Defensive against version drift: warns on unknown top-level event types,
/// reports malformed-line ratios, surfaces error-shaped events. Codex CLI
/// 0.125.0+ moved the canonical structured outcome from a top-level
/// <c>structured_output</c> event into the <c>text</c> of the final
/// <c>item.completed</c> whose item type is <c>agent_message</c>; this parser
/// handles both shapes (legacy event takes precedence when both are present).
/// </remarks>
internal static class CodexOutputParser
{
    /// <summary>
    /// Top-level NDJSON event types the parser knows how to handle. Anything
    /// outside this set is surfaced as a warning at end-of-parse so CLI-version
    /// drift is visible.
    /// </summary>
    internal static readonly HashSet<string> KnownEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "thread.started",
        "turn.started",
        "turn.completed",
        "item.started",
        "item.completed",
        "item.updated",
        "response.completed",
        "response.started",
        "assistant",
        "user",
        "system",
    };

    /// <summary>
    /// Parses the NDJSON stream emitted by <c>codex exec --json</c>.
    /// </summary>
    /// <returns>
    /// <c>ResultJson</c> is the raw JSON line containing
    /// <c>structured_output.outcome</c> (or a synthetic envelope built from
    /// the final <c>agent_message</c> when the legacy event is absent), or
    /// <c>null</c> when no outcome was found. <c>ConversationLog</c> is the
    /// concatenated assistant text content for diagnostic logging, capped at
    /// 50_000 chars.
    /// </returns>
    internal static (string? ResultJson, string ConversationLog) Parse(string stdout, ILogger logger)
    {
        const int MaxConversationLogChars = 50_000;
        string? resultWithStructuredOutput = null;
        int resultWithStructuredOutputLine = 0;
        string? lastResultJson = null;
        int resultMessageCount = 0;
        int structuredOutputCount = 0;
        int malformedLineCount = 0;
        int nonEmptyLineCount = 0;
        var conversationLog = new StringBuilder();
        var lineNumber = 0;
        var unknownTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var errorEventSamples = new List<string>();
        // Codex CLI 0.125.0+ shape: agent's structured outcome lives in the
        // text of the final item.completed whose item.type == "agent_message".
        // Track the most recent so we can fall back after the loop.
        string? lastAgentMessageText = null;
        int lastAgentMessageLine = 0;
        int agentMessageCount = 0;
        // Usage lives on turn.completed in BOTH the v0.124 and v0.125 wire shapes,
        // separate from the result/agent_message line. Track the JSON of the most
        // recent turn.completed so we can merge its usage into the final resultJson.
        string? lastTurnCompletedJson = null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            nonEmptyLineCount++;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                logger.LogDebug("NDJSON line {LineNumber}: type={Type}, raw={Raw}",
                    lineNumber, type ?? "(no-type)", Truncate(trimmed, 500));

                if (type is not null && !KnownEventTypes.Contains(type))
                {
                    unknownTypeCounts[type] = unknownTypeCounts.TryGetValue(type, out var c) ? c + 1 : 1;
                }

                if (type is not null
                    && type.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && errorEventSamples.Count < 3)
                {
                    errorEventSamples.Add($"line {lineNumber}: {Truncate(trimmed, 500)}");
                }

                var hasStructuredOutput = root.TryGetProperty("structured_output", out var so)
                    && so.ValueKind == JsonValueKind.Object
                    && so.TryGetProperty("outcome", out _);

                if (hasStructuredOutput)
                {
                    resultMessageCount++;
                    lastResultJson = trimmed;

                    var eventType = type ?? "(no-type)";
                    logger.LogInformation(
                        "NDJSON structured_output in event type={Type} #{Count} at line {LineNumber}: {Raw}",
                        eventType, structuredOutputCount + 1, lineNumber,
                        Truncate(trimmed, 2000));

                    if (structuredOutputCount == 0)
                    {
                        resultWithStructuredOutput = trimmed;
                        resultWithStructuredOutputLine = lineNumber;
                    }
                    else
                    {
                        logger.LogWarning(
                            "Multiple structured_output events! Previous at line {PrevLine}, current at line {CurrLine}. Using first.",
                            resultWithStructuredOutputLine, lineNumber);
                    }
                    structuredOutputCount++;
                }

                if (string.Equals(type, "turn.completed", StringComparison.OrdinalIgnoreCase))
                {
                    lastTurnCompletedJson = trimmed;
                }

                if (string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("item", out var agentMsgItem)
                    && agentMsgItem.ValueKind == JsonValueKind.Object
                    && agentMsgItem.TryGetProperty("type", out var agentMsgType)
                    && string.Equals(agentMsgType.GetString(), "agent_message", StringComparison.OrdinalIgnoreCase)
                    && agentMsgItem.TryGetProperty("text", out var agentMsgText)
                    && agentMsgText.ValueKind == JsonValueKind.String)
                {
                    var text = agentMsgText.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lastAgentMessageText = text;
                        lastAgentMessageLine = lineNumber;
                        agentMessageCount++;
                    }
                }

                ExtractTextContent(root, type, conversationLog, MaxConversationLogChars, logger, lineNumber);
            }
            catch (JsonException)
            {
                malformedLineCount++;
                logger.LogDebug("NDJSON line {LineNumber}: malformed JSON, skipping. Raw: {Raw}",
                    lineNumber, Truncate(trimmed, 200));
            }
        }

        if (resultWithStructuredOutput is null && lastAgentMessageText is not null)
        {
            var stripped = AgentOutputParser.StripMarkdownFences(lastAgentMessageText.Trim());
            if (TryWrapAgentMessageAsStructured(stripped, out var wrapped))
            {
                logger.LogInformation(
                    "No top-level structured_output event found; using agent_message fallback " +
                    "(Codex CLI 0.125.0+ shape) from item.completed at line {Line} ({Count} agent_messages total).",
                    lastAgentMessageLine, agentMessageCount);
                resultWithStructuredOutput = wrapped;
                resultWithStructuredOutputLine = lastAgentMessageLine;
                structuredOutputCount = 1;
                resultMessageCount++;
            }
            else
            {
                logger.LogWarning(
                    "agent_message at line {Line} did not parse as a structured-outcome JSON " +
                    "(text length {Length} chars). Falling through to no-structured-output diagnostic.",
                    lastAgentMessageLine, lastAgentMessageText.Length);
            }
        }

        var resultJson = resultWithStructuredOutput ?? lastResultJson;

        // Merge usage from the trailing turn.completed event into resultJson so
        // AgentOutputParser.ParseUsage (which inspects the result line's root)
        // can pick it up. Codex emits usage and structured-outcome on separate
        // events; without this merge we'd silently discard token counts.
        if (resultJson is not null && lastTurnCompletedJson is not null)
        {
            var merged = TryMergeUsageIntoResult(resultJson, lastTurnCompletedJson, logger);
            if (merged is not null)
                resultJson = merged;
        }

        if (resultWithStructuredOutput is not null && lastResultJson is not null
            && resultWithStructuredOutput != lastResultJson)
        {
            logger.LogWarning(
                "Used structured_output result from line {Line}, not the last result event. " +
                "Total result events: {Count}",
                resultWithStructuredOutputLine, resultMessageCount);
        }

        if (unknownTypeCounts.Count > 0)
        {
            var summary = string.Join(", ", unknownTypeCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            logger.LogWarning(
                "NDJSON parsing saw {Count} unknown top-level event type(s): {Summary}. " +
                "This is likely Codex CLI version drift — update KnownEventTypes or verify parser coverage.",
                unknownTypeCounts.Count, summary);
        }

        if (nonEmptyLineCount > 0 && malformedLineCount >= 2
            && (double)malformedLineCount / nonEmptyLineCount > 0.10)
        {
            logger.LogWarning(
                "NDJSON parsing saw {Malformed}/{Total} malformed lines ({Pct:P1}). " +
                "Codex stream output may be corrupt or buffered across event boundaries.",
                malformedLineCount, nonEmptyLineCount,
                (double)malformedLineCount / nonEmptyLineCount);
        }

        if (errorEventSamples.Count > 0)
        {
            logger.LogWarning(
                "NDJSON stream contained error-shaped event(s):\n{Samples}",
                string.Join("\n", errorEventSamples));
        }

        logger.LogInformation(
            "NDJSON parsing complete: {TotalLines} lines ({NonEmpty} non-empty, {Malformed} malformed), " +
            "resultEvents={ResultCount}, structuredOutputEvents={StructuredCount}, " +
            "agentMessages={AgentMessageCount}, unknownTypes={UnknownTypeCount}, " +
            "conversationLogChars={LogChars}",
            lineNumber, nonEmptyLineCount, malformedLineCount,
            resultMessageCount, structuredOutputCount, agentMessageCount,
            unknownTypeCounts.Count, conversationLog.Length);

        var log = conversationLog.Length > MaxConversationLogChars
            ? conversationLog.ToString()[..MaxConversationLogChars] + "\n...[truncated]"
            : conversationLog.ToString();

        return (resultJson, log);
    }

    /// <summary>
    /// Best-effort recovery of a structured outcome from arbitrary stdout.
    /// Tries the NDJSON path first; falls back to a single-document JSON
    /// containing top-level <c>structured_output</c>. Returns null when no
    /// path produced a parseable outcome — the caller then handles the
    /// no-structured-output failure mode.
    /// </summary>
    internal static AgentResult? TryRecoverStructuredOutput(string stdout, ILogger logger)
    {
        try
        {
            var (resultJson, conversationLog) = Parse(stdout, logger);
            if (resultJson is not null)
            {
                var result = AgentOutputParser.ParseResult(resultJson);
                return result with { ConversationLog = conversationLog };
            }

            if (TryParseSingleDocumentStructured(stdout, out var singleDocResult))
                return singleDocResult;
        }
        catch
        {
            // Defensive: never let recovery itself throw.
        }
        return null;
    }

    /// <summary>
    /// Backward-compat path: stdout as a single JSON document with a top-level
    /// <c>structured_output.outcome</c>. Returns false for anything else —
    /// callers should fail loudly, not guess.
    /// </summary>
    internal static bool TryParseSingleDocumentStructured(string stdout, out AgentResult result)
    {
        result = default!;
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (!root.TryGetProperty("structured_output", out var so)
                || so.ValueKind != JsonValueKind.Object
                || !so.TryGetProperty("outcome", out _))
                return false;
        }
        catch (JsonException)
        {
            return false;
        }

        result = AgentOutputParser.ParseResult(trimmed);
        return true;
    }

    /// <summary>
    /// Codex CLI 0.125.0+ shape: wrap the final agent_message text into a
    /// synthetic <c>{ "structured_output": &lt;parsed&gt; }</c> envelope so the
    /// existing <see cref="AgentOutputParser.ParseResult"/> path consumes it
    /// unchanged. Returns false (caller falls through to the no-structured-output
    /// diagnostic) when the text doesn't parse as a JSON object with a
    /// recognised <c>outcome</c> enum value.
    /// </summary>
    internal static bool TryWrapAgentMessageAsStructured(string text, out string wrapped)
    {
        wrapped = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("outcome", out var outcomeEl)) return false;
            if (outcomeEl.ValueKind != JsonValueKind.String) return false;
            var outcome = outcomeEl.GetString();
            if (outcome is not ("COMPLETE" or "NEEDS_INFO" or "ERROR" or "SUCCESS" or "QUESTIONS"))
                return false;
            wrapped = "{\"structured_output\":" + text + "}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compact, high-signal diagnostic block for the "no structured_output"
    /// failure path. First/last stdout lines, line counts, stderr head, hint.
    /// </summary>
    internal static string BuildNoStructuredOutputDiagnostic(
        string stdout, string stderr, CliFailureHintDetector.FailureHint? hint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Codex no-structured-output diagnostic ===");
        if (hint is not null)
            sb.AppendLine($"Hint: {hint.Category}: {hint.Hint}");

        var stdoutLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        sb.AppendLine($"stdout: {stdout.Length} chars across {stdoutLines.Length} non-empty lines");

        if (stdoutLines.Length > 0)
        {
            sb.AppendLine("first 3 lines:");
            foreach (var line in stdoutLines.Take(3))
                sb.AppendLine($"  | {Truncate(line.Trim(), 500)}");

            if (stdoutLines.Length > 6)
            {
                sb.AppendLine("...");
                sb.AppendLine("last 3 lines:");
                foreach (var line in stdoutLines.Skip(Math.Max(0, stdoutLines.Length - 3)))
                    sb.AppendLine($"  | {Truncate(line.Trim(), 500)}");
            }
            else if (stdoutLines.Length > 3)
            {
                foreach (var line in stdoutLines.Skip(3))
                    sb.AppendLine($"  | {Truncate(line.Trim(), 500)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine($"stderr (first 2000 chars):");
            sb.AppendLine(Truncate(stderr, 2000));
        }

        sb.Append("===");
        return sb.ToString();
    }

    /// <summary>
    /// Inject <c>usage</c> and <c>total_cost_usd</c> from the trailing
    /// <c>turn.completed</c> event into the result JSON line. Returns the merged
    /// JSON string, or null if either side was malformed (caller falls through
    /// to the unmerged result — usage just won't appear).
    /// </summary>
    internal static string? TryMergeUsageIntoResult(
        string resultJson, string turnCompletedJson, ILogger logger)
    {
        try
        {
            using var resultDoc = JsonDocument.Parse(resultJson);
            using var turnDoc = JsonDocument.Parse(turnCompletedJson);

            // If result already has usage, prefer it (e.g. Claude-style streams
            // that put usage directly on the result event).
            if (resultDoc.RootElement.TryGetProperty("usage", out _))
                return null;

            var hasUsage = turnDoc.RootElement.TryGetProperty("usage", out var usage);
            var hasCost = turnDoc.RootElement.TryGetProperty("total_cost_usd", out var cost);
            if (!hasUsage && !hasCost) return null;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in resultDoc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                if (hasUsage)
                {
                    writer.WritePropertyName("usage");
                    usage.WriteTo(writer);
                }
                if (hasCost)
                {
                    writer.WritePropertyName("total_cost_usd");
                    cost.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to merge turn.completed usage into resultJson; usage will be dropped.");
            return null;
        }
    }

    private static void ExtractTextContent(
        JsonElement root, string? type, StringBuilder conversationLog,
        int maxChars, ILogger logger, int lineNumber)
    {
        if (conversationLog.Length >= maxChars) return;

        if (root.TryGetProperty("output", out var outputEl))
        {
            if (outputEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputEl.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textEl))
                        AppendConversationText(conversationLog, textEl.GetString(), maxChars);
                }
                return;
            }
            if (outputEl.ValueKind == JsonValueKind.String)
            {
                AppendConversationText(conversationLog, outputEl.GetString(), maxChars);
                return;
            }
        }

        if (root.TryGetProperty("item", out var itemEl))
        {
            if (itemEl.TryGetProperty("text", out var textEl))
                AppendConversationText(conversationLog, textEl.GetString(), maxChars);
            else if (itemEl.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
                AppendConversationText(conversationLog, contentEl.GetString(), maxChars);
            return;
        }

        if (root.TryGetProperty("message", out var messageEl))
        {
            if (messageEl.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in contentEl.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var blockType)
                            && blockType.GetString() == "text"
                            && block.TryGetProperty("text", out var textEl))
                            AppendConversationText(conversationLog, textEl.GetString(), maxChars);
                    }
                }
                else if (contentEl.ValueKind == JsonValueKind.String)
                {
                    AppendConversationText(conversationLog, contentEl.GetString(), maxChars);
                }
            }
            return;
        }

        if (type?.Contains("delta", StringComparison.OrdinalIgnoreCase) == true
            && root.TryGetProperty("delta", out var deltaEl)
            && deltaEl.TryGetProperty("text", out _))
        {
            logger.LogDebug("NDJSON line {LineNumber}: delta event skipped from conversation log", lineNumber);
        }
    }

    private static void AppendConversationText(StringBuilder sb, string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || sb.Length >= maxChars) return;
        if (sb.Length > 0) sb.AppendLine("\n---\n");
        sb.Append(text);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...[truncated]";
}
