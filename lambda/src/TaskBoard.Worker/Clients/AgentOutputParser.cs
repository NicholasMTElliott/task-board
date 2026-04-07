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
