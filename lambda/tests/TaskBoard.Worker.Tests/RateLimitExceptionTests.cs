using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class RateLimitExceptionTests
{
    [Fact]
    public void Constructor_DefaultSource_IsBoardApi()
    {
        var ex = new RateLimitException("msg");
        Assert.Equal(RateLimitSource.BoardApi, ex.Source);
    }

    [Fact]
    public void Constructor_AgentCliSource_SetsCorrectly()
    {
        var ex = new RateLimitException("msg", RateLimitSource.AgentCli);
        Assert.Equal(RateLimitSource.AgentCli, ex.Source);
    }

    [Fact]
    public void Constructor_WithResetAt_SetsProperty()
    {
        var resetAt = new DateTimeOffset(2026, 4, 7, 12, 0, 0, TimeSpan.Zero);
        var ex = new RateLimitException("msg", RateLimitSource.AgentCli, resetAt);
        Assert.Equal(resetAt, ex.ResetAt);
    }

    [Fact]
    public void Constructor_DefaultResetAt_IsNull()
    {
        var ex = new RateLimitException("msg");
        Assert.Null(ex.ResetAt);
    }

    [Fact]
    public void Constructor_MessageIsPreserved()
    {
        var ex = new RateLimitException("rate limited!");
        Assert.Equal("rate limited!", ex.Message);
    }

    [Fact]
    public void Exception_IsInvalidOperationException()
    {
        var ex = new RateLimitException("msg");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
