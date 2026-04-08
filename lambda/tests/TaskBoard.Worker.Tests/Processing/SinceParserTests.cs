using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class SinceParserTests
{
    // ── Null / empty input ────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullInput_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse(null));
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse(""));
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse("   "));
    }

    // ── Single-character (too short) ─────────────────────────────────────────

    [Fact]
    public void Parse_SingleChar_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse("d"));
    }

    // ── Invalid format ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_InvalidUnit_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse("7m"));
    }

    [Fact]
    public void Parse_NonNumericValue_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse("xd"));
    }

    [Fact]
    public void Parse_ZeroValue_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse("0d"));
    }

    [Fact]
    public void Parse_NegativeValue_ReturnsNull()
    {
        Assert.Null(SinceParser.Parse("-1d"));
    }

    // ── Hours ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_24h_ReturnsApproximately24HoursAgo()
    {
        var before = DateTimeOffset.UtcNow;
        var result = SinceParser.Parse("24h");
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result);
        Assert.InRange(result!.Value, before.AddHours(-24).AddSeconds(-1), after.AddHours(-24).AddSeconds(1));
    }

    [Fact]
    public void Parse_1H_UpperCase_ReturnsResult()
    {
        var result = SinceParser.Parse("1H");
        Assert.NotNull(result);
        Assert.True(result!.Value < DateTimeOffset.UtcNow);
        Assert.True(result.Value > DateTimeOffset.UtcNow.AddHours(-2));
    }

    // ── Days ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_7d_ReturnsApproximately7DaysAgo()
    {
        var before = DateTimeOffset.UtcNow;
        var result = SinceParser.Parse("7d");
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result);
        Assert.InRange(result!.Value, before.AddDays(-7).AddSeconds(-1), after.AddDays(-7).AddSeconds(1));
    }

    [Fact]
    public void Parse_1D_UpperCase_ReturnsResult()
    {
        var result = SinceParser.Parse("1D");
        Assert.NotNull(result);
    }

    [Fact]
    public void Parse_30d_Returns30DaysAgo()
    {
        var before = DateTimeOffset.UtcNow;
        var result = SinceParser.Parse("30d");
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result);
        Assert.InRange(result!.Value, before.AddDays(-30).AddSeconds(-1), after.AddDays(-30).AddSeconds(1));
    }

    // ── Weeks ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_2w_Returns14DaysAgo()
    {
        var before = DateTimeOffset.UtcNow;
        var result = SinceParser.Parse("2w");
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result);
        Assert.InRange(result!.Value, before.AddDays(-14).AddSeconds(-1), after.AddDays(-14).AddSeconds(1));
    }

    [Fact]
    public void Parse_1W_UpperCase_Returns7DaysAgo()
    {
        var before = DateTimeOffset.UtcNow;
        var result = SinceParser.Parse("1W");
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result);
        Assert.InRange(result!.Value, before.AddDays(-7).AddSeconds(-1), after.AddDays(-7).AddSeconds(1));
    }

    // ── Whitespace trimming ───────────────────────────────────────────────────

    [Fact]
    public void Parse_WithLeadingTrailingWhitespace_ParsesCorrectly()
    {
        var result = SinceParser.Parse("  7d  ");
        Assert.NotNull(result);
    }

    // ── Ordering: more time ago = earlier DateTimeOffset ─────────────────────

    [Fact]
    public void Parse_LargerValue_ReturnsEarlierOffset()
    {
        var one = SinceParser.Parse("1d");
        var seven = SinceParser.Parse("7d");

        Assert.NotNull(one);
        Assert.NotNull(seven);
        Assert.True(seven!.Value < one!.Value);
    }
}
