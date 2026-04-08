using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class NullMetricsStoreTests
{
    private readonly IMetricsStore _store = NullMetricsStore.Instance;

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(NullMetricsStore.Instance, NullMetricsStore.Instance);
    }

    [Fact]
    public async Task GetRunSummaryAsync_ReturnsZeroSummary()
    {
        var result = await _store.GetRunSummaryAsync(null, CancellationToken.None);

        Assert.Equal(0, result.TotalRuns);
        Assert.Equal(0, result.CompleteRuns);
        Assert.Equal(0, result.NeedsInfoRuns);
        Assert.Equal(0, result.ErrorRuns);
        Assert.Equal(0, result.RateLimitedRuns);
        Assert.Equal(0, result.SuccessRatePercent);
    }

    [Fact]
    public async Task GetRunSummaryAsync_WithSince_ReturnsZeroSummary()
    {
        var result = await _store.GetRunSummaryAsync(DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);
        Assert.Equal(0, result.TotalRuns);
    }

    [Fact]
    public async Task GetCardMetricsAsync_ReturnsEmptyList()
    {
        var result = await _store.GetCardMetricsAsync(null, null, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCardMetricsAsync_WithCardId_ReturnsEmptyList()
    {
        var result = await _store.GetCardMetricsAsync("42", null, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTopStepDurationsAsync_ReturnsEmptyList()
    {
        var result = await _store.GetTopStepDurationsAsync(10, null, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReworkCardsAsync_ReturnsEmptyList()
    {
        var result = await _store.GetReworkCardsAsync(null, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCycleTimePerPointAsync_ReturnsEmptySummary()
    {
        var result = await _store.GetCycleTimePerPointAsync(null, CancellationToken.None);

        Assert.Null(result.Overall.AvgCycleTimePerPointSeconds);
        Assert.Null(result.Overall.StdDevCycleTimePerPointSeconds);
        Assert.Null(result.Last24h.AvgCycleTimePerPointSeconds);
        Assert.Null(result.Last7d.AvgCycleTimePerPointSeconds);
        Assert.Null(result.Last30d.AvgCycleTimePerPointSeconds);
    }
}
