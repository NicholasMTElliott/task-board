using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class QueueProcessingOptionsTests
{
    [Fact]
    public void ResolveBatchSize_NullValue_ReturnsDefault()
    {
        var options = new QueueProcessingOptions { DefaultBatchSize = 5 };
        Assert.Equal(5, options.ResolveBatchSize(null));
    }

    [Fact]
    public void ResolveBatchSize_ValidValue_ReturnsProvided()
    {
        var options = new QueueProcessingOptions { DefaultBatchSize = 5 };
        Assert.Equal(20, options.ResolveBatchSize(20));
    }

    [Fact]
    public void ResolveBatchSize_ZeroValue_FallsBackToDefault()
    {
        var options = new QueueProcessingOptions { DefaultBatchSize = 5 };
        Assert.Equal(5, options.ResolveBatchSize(0));
    }

    [Fact]
    public void ResolveBatchSize_NegativeValue_FallsBackToDefault()
    {
        var options = new QueueProcessingOptions { DefaultBatchSize = 5 };
        Assert.Equal(5, options.ResolveBatchSize(-1));
    }

    [Fact]
    public void ResolveLoopIdleDelaySeconds_ZeroConfig_ReturnsOne()
    {
        var options = new QueueProcessingOptions { LoopIdleDelaySeconds = 0 };
        Assert.Equal(1, options.ResolveLoopIdleDelaySeconds());
    }
}
