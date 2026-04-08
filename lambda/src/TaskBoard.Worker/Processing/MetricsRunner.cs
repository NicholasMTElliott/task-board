namespace TaskBoard.Worker.Processing;

/// <summary>
/// Orchestrates metrics queries and renders results to the console.
/// Invoked by --mode metrics dispatch in Program.cs.
/// Detects NullMetricsStore and shows a clear configuration message instead.
/// </summary>
public sealed class MetricsRunner(
    IMetricsStore metricsStore,
    ILogger<MetricsRunner> logger)
{
    public async Task RunAsync(string? cardId, DateTimeOffset? since, CancellationToken ct)
    {
        if (metricsStore is NullMetricsStore)
        {
            Console.WriteLine("Metrics are unavailable: no database configured.");
            Console.WriteLine("Set Database:ConnectionString (or Database__ConnectionString env var) to enable metrics.");
            return;
        }

        var sinceLabel = since.HasValue
            ? $"since {since.Value:yyyy-MM-dd HH:mm} UTC"
            : "all time";
        var cardLabel = cardId is not null ? $" (card #{cardId})" : "";
        Console.WriteLine($"=== aiboard metrics — {sinceLabel}{cardLabel} ===");
        Console.WriteLine();

        try
        {
            var summary = await metricsStore.GetRunSummaryAsync(since, ct);
            var cards = await metricsStore.GetCardMetricsAsync(cardId, since, ct);
            var steps = await metricsStore.GetTopStepDurationsAsync(10, since, ct);
            var rework = await metricsStore.GetReworkCardsAsync(since, ct);
            var cyclePerPoint = await metricsStore.GetCycleTimePerPointAsync(since, ct);

            PrintRunSummary(summary);
            PrintCardMetrics(cards);
            PrintTopStepDurations(steps);
            PrintRework(rework);
            PrintCycleTimePerPoint(cyclePerPoint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Metrics query failed");
            Console.WriteLine($"Error querying metrics: {ex.Message}");
        }
    }

    private static void PrintRunSummary(RunSummary summary)
    {
        Console.WriteLine("── Run Summary ──────────────────────────────────────────");
        Console.WriteLine($"  Total runs:      {summary.TotalRuns}");
        Console.WriteLine($"  Complete:        {summary.CompleteRuns}");
        Console.WriteLine($"  Needs info:      {summary.NeedsInfoRuns}");
        Console.WriteLine($"  Errors:          {summary.ErrorRuns}");
        Console.WriteLine($"  Rate limited:    {summary.RateLimitedRuns}");
        Console.WriteLine($"  Success rate:    {summary.SuccessRatePercent:F1}%");
        Console.WriteLine();
    }

    private static void PrintCardMetrics(IReadOnlyList<CardMetrics> cards)
    {
        if (cards.Count == 0)
        {
            Console.WriteLine("── Card Metrics ─────────────────────────────────────────");
            Console.WriteLine("  No completed runs found.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("── Card Metrics ─────────────────────────────────────────");
        Console.WriteLine(
            $"  {"Card",-8}  {"Cycle",-10}  {"Working",-10}  {"Waiting",-10}  {"Runs",-5}  {"Est",4}");
        Console.WriteLine(
            $"  {"────",-8}  {"──────────",-10}  {"──────────",-10}  {"──────────",-10}  {"────",-5}  {"────",4}");

        foreach (var c in cards)
        {
            var est = c.Estimate.HasValue ? c.Estimate.Value.ToString("F0") : "—";
            Console.WriteLine(
                $"  {c.CardId,-8}  {FormatDuration(c.CycleTimeSeconds),-10}  {FormatDuration(c.WorkingTimeSeconds),-10}  {FormatDuration(c.WaitingTimeSeconds),-10}  {c.TotalRuns,-5}  {est,4}");
        }

        Console.WriteLine();
    }

    private static void PrintTopStepDurations(IReadOnlyList<StepDuration> steps)
    {
        if (steps.Count == 0)
        {
            Console.WriteLine("── Top Step Durations ───────────────────────────────────");
            Console.WriteLine("  No step data found.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("── Top Step Durations ───────────────────────────────────");
        Console.WriteLine(
            $"  {"Card",-8}  {"Step",-30}  {"Role",-22}  {"Duration",-10}");
        Console.WriteLine(
            $"  {"────",-8}  {"──────────────────────────────",-30}  {"──────────────────────",-22}  {"──────────",-10}");

        foreach (var s in steps)
        {
            Console.WriteLine(
                $"  {s.CardId,-8}  {s.StepName,-30}  {s.Role,-22}  {FormatDuration(s.DurationSeconds),-10}");
        }

        Console.WriteLine();
    }

    private static void PrintRework(IReadOnlyList<CardRework> rework)
    {
        if (rework.Count == 0)
        {
            Console.WriteLine("── Rework ───────────────────────────────────────────────");
            Console.WriteLine("  No rework detected.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("── Rework ───────────────────────────────────────────────");
        Console.WriteLine($"  {"Card",-8}  {"State",-30}  {"Entries",-7}  {"Rework",6}");
        Console.WriteLine($"  {"────",-8}  {"──────────────────────────────",-30}  {"───────",-7}  {"──────",6}");

        foreach (var r in rework)
        {
            Console.WriteLine($"  {r.CardId,-8}  {r.StateName,-30}  {r.EntryCount,-7}  {r.ReworkCount,6}");
        }

        Console.WriteLine();
    }

    private static void PrintCycleTimePerPoint(CycleTimePerPointSummary summary)
    {
        Console.WriteLine("── Cycle Time per Story Point ───────────────────────────");
        Console.WriteLine($"  {"Window",-10}  {"Avg",-12}  {"Std Dev",-12}");
        Console.WriteLine($"  {"──────",-10}  {"──────────────",-12}  {"──────────────",-12}");

        PrintCycleTimeRow("All time", summary.Overall);
        PrintCycleTimeRow("Last 30d", summary.Last30d);
        PrintCycleTimeRow("Last 7d", summary.Last7d);
        PrintCycleTimeRow("Last 24h", summary.Last24h);

        Console.WriteLine();
    }

    private static void PrintCycleTimeRow(string label, CycleTimePerPoint row)
    {
        var avg = row.AvgCycleTimePerPointSeconds.HasValue
            ? FormatDuration(row.AvgCycleTimePerPointSeconds.Value)
            : "—";
        var stddev = row.StdDevCycleTimePerPointSeconds.HasValue
            ? FormatDuration(row.StdDevCycleTimePerPointSeconds.Value)
            : "—";
        Console.WriteLine($"  {label,-10}  {avg,-12}  {stddev,-12}");
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
            return $"{seconds:F0}s";
        if (seconds < 3600)
            return $"{seconds / 60:F1}m";
        if (seconds < 86400)
            return $"{seconds / 3600:F1}h";
        return $"{seconds / 86400:F1}d";
    }
}
