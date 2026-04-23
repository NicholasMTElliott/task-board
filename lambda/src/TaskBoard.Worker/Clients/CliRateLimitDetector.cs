namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared rate-limit detection for CLI-backed agent executors.
/// Each executor owns its list of stderr patterns; this helper performs
/// case-insensitive substring matching.
/// </summary>
/// <remarks>
/// Rate-limit detection is stderr-only. Agent conversation content flows through
/// stdout and may discuss rate limiting without being rate-limited.
/// </remarks>
public static class CliRateLimitDetector
{
    /// <summary>
    /// Stderr patterns that indicate Claude CLI rate limiting.
    /// Matches anywhere in stderr; case-insensitive.
    /// </summary>
    public static readonly IReadOnlyList<string> ClaudePatterns = new[]
    {
        "rate limit",
        "overloaded",
    };

    /// <summary>
    /// Stderr patterns that indicate Codex / OpenAI CLI rate limiting.
    /// Covers the phrases emitted by OpenAI API errors when relayed through
    /// the Codex CLI. Operators can extend via <c>CodexCli:RateLimitPatterns</c>.
    /// Matches anywhere in stderr; case-insensitive.
    /// </summary>
    /// <remarks>
    /// Intentionally omits bare <c>"429"</c>: a three-character substring match
    /// false-positives on unrelated numeric content (hashes, line numbers, timestamps).
    /// HTTP 429 error messages almost always include "Too Many Requests" alongside
    /// the status code, which is covered below.
    /// <para>
    /// The <c>rate limit</c> / <c>rate-limit</c> / <c>ratelimit</c> trio covers the
    /// space/dash/no-separator forms seen in API error text and in class names
    /// like OpenAI's <c>RateLimitError</c>.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> CodexDefaultPatterns = new[]
    {
        "rate limit",
        "rate-limit",
        "rate_limit",
        "ratelimit",
        "too many requests",
        "insufficient_quota",
        "quota exceeded",
    };

    /// <summary>
    /// Returns true if <paramref name="stderr"/> contains any of <paramref name="patterns"/>
    /// as a case-insensitive substring. Empty patterns are ignored.
    /// </summary>
    public static bool Matches(string stderr, IEnumerable<string> patterns)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        foreach (var p in patterns)
        {
            if (!string.IsNullOrEmpty(p)
                && stderr.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
