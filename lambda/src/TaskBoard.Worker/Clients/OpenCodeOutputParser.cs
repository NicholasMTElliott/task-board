using System.Text.Json;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Extracts an Agent Contract JSON object from OpenCode CLI stdout.
/// Unlike Claude's NDJSON stream or Codex's structured_output envelope,
/// OpenCode emits free-form assistant text and the structured response is
/// whatever the model produced inside it. This parser defends against that
/// by trying several progressively less strict strategies.
/// </summary>
internal static class OpenCodeOutputParser
{
    /// <summary>
    /// Attempts to find a JSON object in <paramref name="stdout"/> that carries
    /// an <c>outcome</c> field. Returns the JSON string and, separately, any
    /// text content that preceded or surrounded it (for the conversation log).
    /// </summary>
    /// <remarks>
    /// Strategies in order:
    /// <list type="number">
    ///   <item>Markdown-fenced JSON block (<c>```json ... ```</c>) — the most
    ///         common shape when an LLM returns structured output mixed with
    ///         prose.</item>
    ///   <item>Whole-document JSON — stdout parses cleanly as a single object
    ///         with an <c>outcome</c> property.</item>
    ///   <item>Last top-level JSON object — scans for balanced <c>{...}</c>
    ///         regions from the end of stdout and returns the most recent one
    ///         containing an <c>outcome</c> property. "Last" rather than
    ///         "first" because models often narrate before settling on the
    ///         final answer.</item>
    /// </list>
    /// </remarks>
    internal static (string? ResultJson, string ConversationLog) Parse(
        string stdout, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return (null, "");

        // Strategy 1: fenced JSON
        var fenced = ExtractFencedJson(stdout);
        if (fenced is not null && HasOutcome(fenced))
        {
            logger.LogDebug(
                "OpenCode output parsed via fenced-JSON strategy ({Chars} chars)",
                fenced.Length);
            return (fenced, stdout);
        }

        // Strategy 2: whole-document JSON
        var trimmed = stdout.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '{' && HasOutcome(trimmed))
        {
            logger.LogDebug("OpenCode output parsed via whole-document JSON strategy");
            return (trimmed, "");
        }

        // Strategy 3: scan for last balanced {...} with outcome
        var lastBalanced = ExtractLastBalancedJsonWithOutcome(stdout);
        if (lastBalanced is not null)
        {
            logger.LogDebug(
                "OpenCode output parsed via last-balanced-JSON strategy ({Chars} chars)",
                lastBalanced.Length);
            return (lastBalanced, stdout);
        }

        logger.LogDebug(
            "OpenCode output contained no parseable JSON with outcome field ({StdoutChars} chars)",
            stdout.Length);
        return (null, stdout);
    }

    /// <summary>
    /// Returns the first <c>```json ... ```</c> (or <c>``` ... ```</c> whose
    /// first non-empty line starts with <c>{</c>) block in the input, or null
    /// if no such block exists.
    /// </summary>
    internal static string? ExtractFencedJson(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var fenceIdx = text.IndexOf("```", start, StringComparison.Ordinal);
            if (fenceIdx < 0) return null;

            var afterFence = fenceIdx + 3;
            // Skip the optional language tag line
            var eol = text.IndexOf('\n', afterFence);
            if (eol < 0) return null;

            var tag = text[afterFence..eol].Trim();
            var bodyStart = eol + 1;

            var closeIdx = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (closeIdx < 0) return null;

            var body = text[bodyStart..closeIdx].Trim();

            // Accept any fence whose body looks like JSON, even without a
            // "json" language tag — models often omit it.
            if (body.Length > 0 && body[0] == '{'
                && (tag.Equals("json", StringComparison.OrdinalIgnoreCase) || tag.Length == 0 || body.Length >= 2))
            {
                return body;
            }

            start = closeIdx + 3;
        }
        return null;
    }

    /// <summary>
    /// Scans the input for balanced <c>{...}</c> regions (respecting quoted strings
    /// and escaped characters) and returns the last one whose parsed object contains
    /// an <c>outcome</c> property. Returns null if no such region exists.
    /// </summary>
    internal static string? ExtractLastBalancedJsonWithOutcome(string text)
    {
        string? best = null;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{') { i++; continue; }

            var candidate = TryReadBalancedObject(text, i);
            if (candidate is null) { i++; continue; }

            if (HasOutcome(candidate)) best = candidate;
            i += candidate.Length;
        }
        return best;
    }

    private static string? TryReadBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    private static bool HasOutcome(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("outcome", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
