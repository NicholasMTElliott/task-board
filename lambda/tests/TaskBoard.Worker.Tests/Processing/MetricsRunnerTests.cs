using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Tests for MetricsRunner. Uses console output capture to verify behavior.
/// Uses NullMetricsStore (no-DB path) and a stub metrics store (with-DB path).
/// </summary>
public class MetricsRunnerTests
{
    // ── No-database path ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithNullMetricsStore_PrintsConfigurationMessage()
    {
        var runner = new MetricsRunner(NullMetricsStore.Instance, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, null, CancellationToken.None));

        Assert.Contains("Database:ConnectionString", output);
        Assert.Contains("unavailable", output, StringComparison.OrdinalIgnoreCase);
    }

    // ── With-data path ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithEmptyStore_PrintsNoDataMessages()
    {
        var store = new StubMetricsStore();
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, null, CancellationToken.None));

        Assert.Contains("Run Summary", output);
        Assert.Contains("0", output); // total runs
        Assert.Contains("No completed runs found", output);
        Assert.Contains("No step data found", output);
        Assert.Contains("No rework detected", output);
    }

    [Fact]
    public async Task RunAsync_WithCardData_PrintsCardMetrics()
    {
        var store = new StubMetricsStore
        {
            Cards =
            [
                new CardMetrics("42", DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow, 7200, 3600, 3600, 2, 4)
            ]
        };
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, null, CancellationToken.None));

        Assert.Contains("42", output);
        Assert.Contains("Card Metrics", output);
    }

    [Fact]
    public async Task RunAsync_WithSince_IncludesSinceLabelInHeader()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var store = new StubMetricsStore();
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, since, CancellationToken.None));

        Assert.Contains("since", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WithCardId_IncludesCardIdInHeader()
    {
        var store = new StubMetricsStore();
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync("42", null, CancellationToken.None));

        Assert.Contains("42", output);
    }

    [Fact]
    public async Task RunAsync_WithReworkData_PrintsReworkTable()
    {
        var store = new StubMetricsStore
        {
            Rework = [new CardRework("55", "Ready for Implementation", 3, 2)]
        };
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, null, CancellationToken.None));

        Assert.Contains("55", output);
        Assert.Contains("Ready for Implementation", output);
        Assert.DoesNotContain("No rework detected", output);
    }

    [Fact]
    public async Task RunAsync_WithSuccessRate_PrintsPercentage()
    {
        var store = new StubMetricsStore
        {
            Summary = new RunSummary(10, 8, 1, 1, 0, 80.0)
        };
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, null, CancellationToken.None));

        Assert.Contains("80", output);
        Assert.Contains("%", output);
    }

    [Fact]
    public async Task RunAsync_WithCycleTimePerPoint_PrintsWindowTable()
    {
        var store = new StubMetricsStore
        {
            CycleTimePerPoint = new CycleTimePerPointSummary(
                Overall: new CycleTimePerPoint(3600, 600),
                Last24h: new CycleTimePerPoint(null, null),
                Last7d: new CycleTimePerPoint(7200, 0),
                Last30d: new CycleTimePerPoint(5400, 300))
        };
        var runner = new MetricsRunner(store, NullLogger<MetricsRunner>.Instance);

        var output = await CaptureConsoleAsync(
            () => runner.RunAsync(null, null, CancellationToken.None));

        Assert.Contains("Cycle Time per Story Point", output);
        Assert.Contains("All time", output);
        Assert.Contains("Last 7d", output);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> CaptureConsoleAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return writer.ToString();
    }

    /// <summary>Controllable stub for IMetricsStore used in unit tests.</summary>
    private sealed class StubMetricsStore : IMetricsStore
    {
        public RunSummary Summary { get; set; } = new RunSummary(0, 0, 0, 0, 0, 0);
        public List<CardMetrics> Cards { get; set; } = [];
        public List<StepDuration> Steps { get; set; } = [];
        public List<CardRework> Rework { get; set; } = [];
        public CycleTimePerPointSummary CycleTimePerPoint { get; set; } =
            new(new CycleTimePerPoint(null, null), new CycleTimePerPoint(null, null),
                new CycleTimePerPoint(null, null), new CycleTimePerPoint(null, null));
        public List<ProviderRoleMetric> ProviderRoleMetrics { get; set; } = [];
        public List<HeadToHeadRecord> HeadToHead { get; set; } = [];

        public Task<RunSummary> GetRunSummaryAsync(DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult(Summary);

        public Task<IReadOnlyList<CardMetrics>> GetCardMetricsAsync(string? cardId, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardMetrics>>(Cards);

        public Task<IReadOnlyList<StepDuration>> GetTopStepDurationsAsync(int top, DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepDuration>>(Steps);

        public Task<IReadOnlyList<CardRework>> GetReworkCardsAsync(DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardRework>>(Rework);

        public Task<CycleTimePerPointSummary> GetCycleTimePerPointAsync(DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult(CycleTimePerPoint);

        public Task<IReadOnlyList<ProviderRoleMetric>> GetProviderRoleMetricsAsync(DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderRoleMetric>>(ProviderRoleMetrics);

        public Task<IReadOnlyList<HeadToHeadRecord>> GetCandidateHeadToHeadAsync(DateTimeOffset? since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HeadToHeadRecord>>(HeadToHead);
    }
}
