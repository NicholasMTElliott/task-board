using System.Text.Json;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared parsing utilities for structured agent output.
/// Used by both <see cref="ClaudeAgentExecutor"/> and <see cref="CodexAgentExecutor"/>.
/// </summary>
internal static class AgentOutputParser
{
    /// <summary>
    /// Parses a single JSON message into an <see cref="AgentResult"/>.
    /// Tries structured_output first, then result/output fields, then keyword fallback.
    /// </summary>
    internal static AgentResult ParseResult(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Try structured_output first (preferred path — schema-validated response)
            if (root.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind == JsonValueKind.Object
                && structured.TryGetProperty("outcome", out var structuredOutcome))
            {
                var outcome = ParseOutcomeString(structuredOutcome.GetString());
                var detail = structured.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var questions = ParseQuestions(structured);
                var requestedSteps = ParseRequestedSteps(structured);
                double? estimate = null;
                if (structured.TryGetProperty("estimate", out var estEl)
                    && estEl.ValueKind == JsonValueKind.Number)
                {
                    estimate = estEl.GetDouble();
                }
                return new AgentResult(outcome, detail, questions, null, requestedSteps, estimate);
            }

            // Try result field
            if (root.TryGetProperty("result", out var result))
            {
                var resultText = result.GetString() ?? "";
                return new AgentResult(TryParseOutcomeFromText(resultText));
            }

            // Try output field (Responses API shape)
            if (root.TryGetProperty("output", out var output))
            {
                var outputText = output.ValueKind == JsonValueKind.String
                    ? output.GetString() ?? ""
                    : output.ToString();
                return new AgentResult(TryParseOutcomeFromText(outputText));
            }
        }
        catch (JsonException)
        {
            // Raw text output — fall through to keyword scan
        }

        return new AgentResult(TryParseOutcomeFromText(stdout));
    }

    internal static List<AgentQuestion>? ParseQuestions(JsonElement structured)
    {
        if (!structured.TryGetProperty("questions", out var questionsEl)
            || questionsEl.ValueKind != JsonValueKind.Array)
            return null;

        var questions = new List<AgentQuestion>();
        foreach (var item in questionsEl.EnumerateArray())
        {
            if (!item.TryGetProperty("question", out var q))
                continue;

            List<string>? recommendations = null;
            if (item.TryGetProperty("recommendations", out var recsEl)
                && recsEl.ValueKind == JsonValueKind.Array)
            {
                recommendations = [];
                foreach (var rec in recsEl.EnumerateArray())
                {
                    var val = rec.GetString();
                    if (val is not null)
                        recommendations.Add(val);
                }
            }

            questions.Add(new AgentQuestion(q.GetString()!, recommendations));
        }

        return questions.Count > 0 ? questions : null;
    }

    internal static IReadOnlyList<string>? ParseRequestedSteps(JsonElement structured)
    {
        if (!structured.TryGetProperty("requestedSteps", out var stepsEl)
            || stepsEl.ValueKind != JsonValueKind.Array)
            return null;

        var steps = stepsEl.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();

        return steps.Count > 0 ? steps : null;
    }

    internal static AgentOutcome ParseOutcomeString(string? outcome)
    {
        return outcome?.ToUpperInvariant() switch
        {
            "COMPLETE" => AgentOutcome.COMPLETE,
            "SUCCESS" => AgentOutcome.COMPLETE,       // backward compat
            "NEEDS_INFO" => AgentOutcome.NEEDS_INFO,
            "QUESTIONS" => AgentOutcome.NEEDS_INFO,   // backward compat
            "ERROR" => AgentOutcome.ERROR,
            _ => AgentOutcome.ERROR
        };
    }

    internal static AgentOutcome TryParseOutcomeFromText(string text)
    {
        // Try parsing as JSON (might be embedded in result field)
        try
        {
            var stripped = StripMarkdownFences(text.Trim());
            using var doc = JsonDocument.Parse(stripped);
            if (doc.RootElement.TryGetProperty("outcome", out var outcome))
            {
                return ParseOutcomeString(outcome.GetString());
            }
        }
        catch (JsonException) { }

        // Last resort: look for outcome keywords
        if (text.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
            return AgentOutcome.COMPLETE;
        if (text.Contains("NEEDS_INFO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("QUESTIONS", StringComparison.OrdinalIgnoreCase))
            return AgentOutcome.NEEDS_INFO;

        return AgentOutcome.ERROR;
    }

    internal static string StripMarkdownFences(string text)
    {
        if (!text.StartsWith("```"))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var inner = text[(firstNewline + 1)..];
        var lastFence = inner.LastIndexOf("```");
        if (lastFence >= 0)
            inner = inner[..lastFence];

        return inner.Trim();
    }

    /// <summary>
    /// Parses Claude CLI NDJSON stream output into a result JSON string and a conversation log.
    /// Finds the first <c>type="result"</c> message that carries a valid
    /// <c>structured_output</c>, falling back to the last result message.
    /// Assembles conversation log from all <c>type="assistant"</c> text blocks.
    /// </summary>
    internal static (string? ResultJson, string ConversationLog) ParseStreamOutput(
        string stdout, ILogger logger)
    {
        const int MaxConversationLogChars = 50_000;
        string? resultWithStructuredOutput = null;
        int resultWithStructuredOutputLine = 0;
        string? lastResultJson = null;
        int resultMessageCount = 0;
        int structuredOutputCount = 0;
        var conversationLog = new System.Text.StringBuilder();
        var lineNumber = 0;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl))
                    continue;

                var type = typeEl.GetString();

                logger.LogDebug("NDJSON line {LineNumber}: type={Type}, raw={Raw}",
                    lineNumber, type, trimmed[..Math.Min(500, trimmed.Length)]);

                var hasStructuredOutput = root.TryGetProperty("structured_output", out var so)
                    && so.ValueKind == System.Text.Json.JsonValueKind.Object
                    && so.TryGetProperty("outcome", out _);

                if (hasStructuredOutput)
                {
                    resultMessageCount++;
                    lastResultJson = trimmed;

                    if (type != "result")
                    {
                        logger.LogWarning(
                            "Found structured_output in unexpected message type={Type} at line {LineNumber}: {Raw}",
                            type, lineNumber, trimmed[..Math.Min(1000, trimmed.Length)]);
                    }

                    logger.LogInformation("NDJSON structured_output #{Count} at line {LineNumber}: {Raw}",
                        structuredOutputCount + 1, lineNumber, trimmed[..Math.Min(2000, trimmed.Length)]);

                    if (structuredOutputCount == 0)
                    {
                        resultWithStructuredOutput = trimmed;
                        resultWithStructuredOutputLine = lineNumber;
                    }
                    else
                    {
                        logger.LogWarning(
                            "Multiple structured_output events! " +
                            "Previous at line {PrevLine}, current at line {CurrLine}. Using first.",
                            resultWithStructuredOutputLine, lineNumber);
                    }
                    structuredOutputCount++;
                }
                else if (type == "result")
                {
                    resultMessageCount++;
                    lastResultJson = trimmed;
                    logger.LogInformation("NDJSON result message (no structured_output) at line {LineNumber}", lineNumber);
                }
                else if (type == "assistant")
                {
                    if (root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var content)
                        && content.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var blockType)
                                && blockType.GetString() == "text"
                                && block.TryGetProperty("text", out var text))
                            {
                                var textValue = text.GetString();
                                if (!string.IsNullOrEmpty(textValue) && conversationLog.Length < MaxConversationLogChars)
                                {
                                    if (conversationLog.Length > 0)
                                        conversationLog.AppendLine("\n---\n");
                                    conversationLog.Append(textValue);
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                logger.LogDebug("NDJSON line {LineNumber}: malformed JSON, skipping", lineNumber);
            }
        }

        var resultJson = resultWithStructuredOutput ?? lastResultJson;

        if (resultWithStructuredOutput is not null && lastResultJson is not null
            && resultWithStructuredOutput != lastResultJson)
        {
            logger.LogWarning(
                "Used result from line {Line} (has structured_output), not the final result message. " +
                "Total result messages: {Count}",
                resultWithStructuredOutputLine, resultMessageCount);
        }

        logger.LogInformation(
            "NDJSON parsing complete: {TotalLines} lines, resultMessages={ResultCount}, " +
            "structuredOutputMessages={StructuredCount}, conversationLogChars={LogChars}",
            lineNumber, resultMessageCount, structuredOutputCount, conversationLog.Length);

        var log = conversationLog.Length > MaxConversationLogChars
            ? conversationLog.ToString()[..MaxConversationLogChars] + "\n...[truncated]"
            : conversationLog.ToString();

        return (resultJson, log);
    }

    internal static string MinifyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    /// <summary>
    /// Writes the agent prompt to a temp file and logs a reproduction block with
    /// the exact command, workspace path, and prompt file path. Called on agent failure
    /// to enable local reproduction of the error.
    /// </summary>
    internal static void LogReproductionInfo(
        ILogger logger,
        string agentName,
        string executable,
        string[] args,
        string? stdinData,
        string workspacePath)
    {
        string? promptFile = null;
        if (!string.IsNullOrEmpty(stdinData))
        {
            try
            {
                promptFile = Path.Combine(Path.GetTempPath(), $"aiboard-repro-{Guid.NewGuid():N}.txt");
                File.WriteAllText(promptFile, stdinData);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write reproduction prompt to temp file");
                promptFile = null;
            }
        }

        // Build the full untruncated command for copy-paste reproduction
        var fullArgs = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var reproCommand = promptFile is not null
            ? $"cat \"{promptFile}\" | {executable} {fullArgs}"
            : $"{executable} {fullArgs}";

        logger.LogError(
            "\n=== {AgentName} REPRODUCTION INFO ===\n" +
            "Worktree:  {WorkspacePath}\n" +
            "Command:   {Executable} {Args}\n" +
            (promptFile is not null ? "Prompt:    {PromptFile}\n" : "") +
            "Repro:     {ReproCommand}\n" +
            "===",
            agentName, workspacePath, executable, fullArgs,
            promptFile ?? "", reproCommand);
    }
}
