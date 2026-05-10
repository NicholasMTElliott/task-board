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

                // Evaluator-specific fields. Present when the structured output
                // followed AgentSchemas.EvaluatorOutcomeSchema; null for every
                // other agent role. Promoting these from structured_output here
                // (rather than re-extracting from detail markdown later) is the
                // fix for the v0.0.16/17 evaluator regression — when the Claude
                // model DOES fill winner_index in the schema response, we now
                // capture it instead of throwing it away and re-parsing prose.
                var winnerIndex = ParseWinnerIndex(structured);
                var scores = ParseEvaluatorScores(structured);

                // Usage / cost lives on the wrapper event next to structured_output,
                // not inside structured_output itself. Pull from root.
                var usage = ParseUsage(root);

                // Rerun redesign: section_update is the agent's structured directive
                // for updating its managed step section. Null on legacy roles or
                // when the agent omitted the field; the orchestrator's writer is
                // mechanical and a null result simply means "no description change".
                // The raw JSON text is captured alongside the parsed object so it
                // can be persisted into step_result.section_update_json (V24)
                // for replay/debugging.
                var section = ParseSectionUpdate(structured, out var sectionRawJson);

                return new AgentResult(
                    outcome, detail, questions, null, requestedSteps, estimate,
                    winnerIndex, scores, usage,
                    StructurerFallbackUsed: null,
                    Section: section,
                    SectionUpdateJson: sectionRawJson);
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

    /// <summary>
    /// Reads the <c>section_update</c> object from the structured-output JSON.
    /// Returns null when the field is missing, explicitly null, or malformed
    /// (e.g. unrecognized strategy value). The orchestrator treats a null
    /// SectionUpdate as "no description change for this step" — equivalent to
    /// strategy=leave on a step that already has a section. The first-run
    /// placeholder coercion happens in the description writer, not here.
    /// </summary>
    internal static SectionUpdate? ParseSectionUpdate(JsonElement structured)
        => ParseSectionUpdate(structured, out _);

    /// <summary>
    /// Same as <see cref="ParseSectionUpdate(JsonElement)"/> but also returns
    /// the raw <c>section_update</c> JSON text via <paramref name="rawJson"/>.
    /// The raw form is persisted to <c>step_result.section_update_json</c>
    /// (V24) for replay and debugging. <paramref name="rawJson"/> is only set
    /// when the field exists and parses to a valid <see cref="SectionUpdate"/>;
    /// callers can treat null as "no directive to record."
    /// </summary>
    internal static SectionUpdate? ParseSectionUpdate(
        JsonElement structured, out string? rawJson)
    {
        rawJson = null;
        if (!structured.TryGetProperty("section_update", out var el)
            || el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty("strategy", out var strategyEl)
            || strategyEl.ValueKind != JsonValueKind.String)
            return null;

        var strategy = strategyEl.GetString() switch
        {
            "leave" => SectionUpdateStrategy.Leave,
            "replace" => SectionUpdateStrategy.Replace,
            "append_with_revision_notes" => SectionUpdateStrategy.AppendWithRevisionNotes,
            _ => (SectionUpdateStrategy?)null,
        };
        if (strategy is null)
            return null;

        string? content = null;
        if (el.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
            content = cEl.GetString();

        // Enforce content non-empty for non-leave strategies. The OpenAI schema
        // requires `content` in `required` (with type ["string","null"]) and
        // many providers treat the leave-strategy fall-through as ambiguous —
        // a missing/empty content under `replace` / `append_with_revision_notes`
        // would silently coerce to the FirstRunLeavePlaceholder in
        // DescriptionWriter, turning a malformed agent output into bogus
        // durable state. Reject as malformed instead so the cache treats this
        // step as drifted and a re-run produces a real section.
        if (strategy.Value != SectionUpdateStrategy.Leave
            && string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var openQuestions = ReadStringArray(el, "open_questions");
        var resolvedDecisions = ReadStringArray(el, "resolved_decisions");

        // Capture the raw JSON for replay/debugging persistence (V24
        // section_update_json column). GetRawText preserves whatever the agent
        // sent, including any extra fields beyond the typed schema.
        rawJson = el.GetRawText();

        return new SectionUpdate(strategy.Value, content, openQuestions, resolvedDecisions);
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Reads <c>winner_index</c> from the structured-output JSON. Tolerates the
    /// OpenAI variant's <c>["integer", "null"]</c> typing (returns null when the
    /// field is explicitly null, missing, or not a number).
    /// </summary>
    internal static int? ParseWinnerIndex(JsonElement structured)
    {
        if (!structured.TryGetProperty("winner_index", out var el))
            return null;
        if (el.ValueKind != JsonValueKind.Number)
            return null;
        return el.TryGetInt32(out var idx) ? idx : null;
    }

    /// <summary>
    /// Reads the <c>scores</c> array from the structured-output JSON, applying
    /// the same sanitisation rules <see cref="Processing.CandidateExecutor"/>
    /// uses internally: scores outside [0, 10] are dropped to null (reasoning
    /// preserved); duplicate indices are deduplicated last-write-wins; entries
    /// without an integer <c>index</c> are skipped.
    /// </summary>
    internal static IReadOnlyList<EvaluatorScore>? ParseEvaluatorScores(JsonElement structured)
    {
        if (!structured.TryGetProperty("scores", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;

        var byIndex = new Dictionary<int, EvaluatorScore>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("index", out var iEl)
                || !iEl.TryGetInt32(out var idx) || idx < 0)
                continue;

            decimal? score = null;
            if (item.TryGetProperty("score", out var sEl)
                && sEl.ValueKind == JsonValueKind.Number
                && sEl.TryGetDecimal(out var raw)
                && raw >= 0m && raw <= 10m)
            {
                score = raw;
            }

            string? reasoning = null;
            if (item.TryGetProperty("reasoning", out var rEl)
                && rEl.ValueKind == JsonValueKind.String)
            {
                reasoning = rEl.GetString();
            }

            byIndex[idx] = new EvaluatorScore(idx, score, reasoning);
        }

        return byIndex.Count > 0
            ? byIndex.Values.OrderBy(s => s.Index).ToList()
            : null;
    }

    /// <summary>
    /// Pulls token / cost data from the result event wrapper. Both Claude
    /// (<c>{"type":"result","usage":{...},"total_cost_usd":N}</c>) and Codex
    /// (<c>{"type":"turn.completed","usage":{...}}</c>, no cost) follow the
    /// same shape — a top-level <c>usage</c> object with at minimum
    /// <c>input_tokens</c> / <c>output_tokens</c>, plus optionally
    /// <c>cache_read_input_tokens</c> / <c>cache_creation_input_tokens</c> on
    /// Claude. Returns null when no usage is present at all so callers can
    /// distinguish "executor didn't report" from "executor reported zero."
    /// </summary>
    internal static UsageInfo? ParseUsage(JsonElement root)
    {
        long? input = null, output = null, cacheRead = null, cacheCreate = null;
        decimal? cost = null;

        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            input = ReadLong(usage, "input_tokens");
            output = ReadLong(usage, "output_tokens");
            cacheRead = ReadLong(usage, "cache_read_input_tokens");
            cacheCreate = ReadLong(usage, "cache_creation_input_tokens");
        }

        if (root.TryGetProperty("total_cost_usd", out var costEl)
            && costEl.ValueKind == JsonValueKind.Number
            && costEl.TryGetDecimal(out var c))
        {
            cost = c;
        }

        if (input is null && output is null && cacheRead is null
            && cacheCreate is null && cost is null)
        {
            return null;
        }

        return new UsageInfo(input, output, cacheRead, cacheCreate, cost);
    }

    private static long? ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number) return null;
        return el.TryGetInt64(out var v) ? v : null;
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
    /// Scans Claude CLI NDJSON stdout for a <c>rate_limit_event</c> whose
    /// <c>rate_limit_info.status</c> is anything other than <c>"allowed"</c>.
    /// Returns true on the first match.
    /// </summary>
    /// <remarks>
    /// Why this exists: Claude CLI in <c>--output-format stream-json</c> mode emits
    /// the rate-limit signal in <b>stdout</b>, not stderr. The shape is:
    /// <code>
    /// {"type":"rate_limit_event","rate_limit_info":{
    ///   "status":"rejected",
    ///   "rateLimitType":"five_hour",
    ///   "overageStatus":"rejected",
    ///   "overageDisabledReason":"out_of_credits"
    /// }}
    /// </code>
    /// On a 5-hour-window rejection (or out-of-credits with overage disabled), the
    /// CLI exits non-zero with empty stderr and only the init + rate_limit_event in
    /// stdout. <see cref="CliRateLimitDetector.Matches"/> is stderr-only, so without
    /// this helper the failure flows through as <c>InvalidOperationException</c> →
    /// <c>AGENT_ERROR</c>, the card moves to Problems, and the poller never backs off.
    /// <para>
    /// Forward-compatible: any non-<c>"allowed"</c> status (including hypothetical
    /// future <c>"queued"</c>/<c>"throttled"</c>) is treated as rate-limited.
    /// Malformed NDJSON lines are skipped; the function never throws.
    /// </para>
    /// <para>
    /// Conservative: only fires when the <c>type</c> field IS <c>"rate_limit_event"</c>.
    /// An assistant message that happens to mention the words "rate limit" in prose
    /// will not match.
    /// </para>
    /// </remarks>
    internal static bool HasRejectedRateLimitEvent(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return false;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String
                    || typeEl.GetString() != "rate_limit_event")
                    continue;

                if (!root.TryGetProperty("rate_limit_info", out var info)
                    || info.ValueKind != JsonValueKind.Object)
                    continue;

                if (!info.TryGetProperty("status", out var statusEl)
                    || statusEl.ValueKind != JsonValueKind.String)
                    continue;

                var status = statusEl.GetString();
                if (!string.Equals(status, "allowed", StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // Malformed line — keep scanning.
            }
            finally
            {
                doc?.Dispose();
            }
        }

        return false;
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
