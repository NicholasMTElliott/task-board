using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class CliRateLimitDetectorTests
{
    // ── Matches() ─────────────────────────────────────────────────────────────

    [Fact]
    public void Matches_EmptyStderr_ReturnsFalse()
    {
        Assert.False(CliRateLimitDetector.Matches("", new[] { "rate limit" }));
        Assert.False(CliRateLimitDetector.Matches(null!, new[] { "rate limit" }));
    }

    [Fact]
    public void Matches_EmptyPatterns_ReturnsFalse()
    {
        Assert.False(CliRateLimitDetector.Matches("anything", Array.Empty<string>()));
    }

    [Fact]
    public void Matches_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(CliRateLimitDetector.Matches("RATE LIMIT reached", new[] { "rate limit" }));
        Assert.True(CliRateLimitDetector.Matches("Rate Limit Exceeded", new[] { "rate limit" }));
    }

    [Fact]
    public void Matches_SubstringMatch_ReturnsTrue()
    {
        // Pattern appears anywhere in stderr, not just as full line
        Assert.True(CliRateLimitDetector.Matches(
            "Error: request failed: too many requests (429)",
            new[] { "too many requests" }));
    }

    [Fact]
    public void Matches_EmptyPatternInList_IsIgnored()
    {
        Assert.False(CliRateLimitDetector.Matches("some error", new[] { "", "  " }));
        Assert.True(CliRateLimitDetector.Matches("some rate limit", new[] { "", "rate limit" }));
    }

    [Fact]
    public void Matches_NoMatch_ReturnsFalse()
    {
        Assert.False(CliRateLimitDetector.Matches(
            "Generic auth error",
            new[] { "rate limit", "overloaded" }));
    }

    // ── ClaudePatterns ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rate limit reached for 500K tokens per minute")]
    [InlineData("RATE LIMIT")]
    [InlineData("server is overloaded; please try later")]
    [InlineData("OVERLOADED")]
    public void ClaudePatterns_KnownSignals_Match(string stderr)
    {
        Assert.True(CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.ClaudePatterns));
    }

    [Theory]
    [InlineData("")]
    [InlineData("generic error")]
    [InlineData("too many requests")] // Codex-style, not Claude-style
    public void ClaudePatterns_NonRateLimitSignals_DoNotMatch(string stderr)
    {
        Assert.False(CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.ClaudePatterns));
    }

    // ── CodexDefaultPatterns ──────────────────────────────────────────────────

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("Error: rate_limit_exceeded")]
    [InlineData("openai.RateLimitError: you are being throttled")]
    [InlineData("HTTP 429: Too Many Requests")]
    [InlineData("too many requests")]
    [InlineData("error: insufficient_quota")]
    [InlineData("quota exceeded for this account")]
    [InlineData("rate-limit hit")]
    public void CodexDefaultPatterns_KnownSignals_Match(string stderr)
    {
        Assert.True(CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.CodexDefaultPatterns));
    }

    [Theory]
    [InlineData("")]
    [InlineData("generic error")]
    [InlineData("auth failed")]
    [InlineData("model not found")]
    // Intentionally NOT matched: numeric substring "429" used to false-positive on
    // unrelated content (hashes, line numbers, timestamps). Errors that genuinely
    // represent HTTP 429 also include "Too Many Requests", which still matches.
    [InlineData("processed 1429 items in 30s")]
    [InlineData("sha256: abc429def")]
    public void CodexDefaultPatterns_NonRateLimitSignals_DoNotMatch(string stderr)
    {
        Assert.False(CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.CodexDefaultPatterns));
    }

    // ── Pattern lists are independent ─────────────────────────────────────────

    [Fact]
    public void ClaudePatterns_ContainsOverloaded_ButCodexDoesNot()
    {
        // "overloaded" is a Claude-specific pattern
        Assert.True(CliRateLimitDetector.Matches("overloaded", CliRateLimitDetector.ClaudePatterns));
        Assert.False(CliRateLimitDetector.Matches("overloaded", CliRateLimitDetector.CodexDefaultPatterns));
    }

    [Fact]
    public void CodexPatterns_ContainsQuotaVariants_ButClaudeDoesNot()
    {
        Assert.True(CliRateLimitDetector.Matches("insufficient_quota", CliRateLimitDetector.CodexDefaultPatterns));
        Assert.False(CliRateLimitDetector.Matches("insufficient_quota", CliRateLimitDetector.ClaudePatterns));
    }
}
