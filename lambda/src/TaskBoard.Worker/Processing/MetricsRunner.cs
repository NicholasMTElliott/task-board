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
            var providerRole = await metricsStore.GetProviderRoleMetricsAsync(since, ct);
            var headToHead = await metricsStore.GetCandidateHeadToHeadAsync(since, ct);
            var evalReliability = await metricsStore.GetEvaluatorReliabilityAsync(since, ct);
            var fastPath = await metricsStore.GetFastPathHitRateAsync(since, ct);

            PrintRunSummary(summary);
            PrintCardMetrics(cards);
            PrintTopStepDurations(steps);
            PrintRework(rework);
            PrintCycleTimePerPoint(cyclePerPoint);
            PrintProviderRoleMetrics(providerRole);
            PrintHeadToHead(headToHead);
            PrintEvaluatorReliability(evalReliability);
            PrintFastPathHitRate(fastPath);
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

    private static void PrintProviderRoleMetrics(IReadOnlyList<ProviderRoleMetric> rows)
    {
        // Suppressed entirely when no candidate-group runs have been recorded —
        // the section header on its own would just be noise for users who haven't
        // opted into multi-agent evaluation yet.
        if (rows.Count == 0) return;

        Console.WriteLine("── Provider × Role Metrics (candidate runs) ─────────────");
        Console.WriteLine(
            $"  {"Role",-26}  {"Provider",-22}  {"Runs",4}  {"Wins",4}  {"Win%",6}  {"AvgScore",8}  {"AvgDur",8}  {"Cost$",10}  {"InTok",10}  {"OutTok",10}  {"Struct%",7}");
        Console.WriteLine(
            $"  {"──────────────────────────",-26}  {"──────────────────────",-22}  {"────",4}  {"────",4}  {"──────",6}  {"────────",8}  {"────────",8}  {"──────────",10}  {"──────────",10}  {"──────────",10}  {"───────",7}");

        foreach (var r in rows)
        {
            var winPct = r.WinRatePercent.HasValue ? $"{r.WinRatePercent.Value:F1}%" : "—";
            var score = r.AvgQualityScore.HasValue ? r.AvgQualityScore.Value.ToString("F2") : "—";
            var dur = r.AvgDurationSeconds.HasValue ? FormatDuration(r.AvgDurationSeconds.Value) : "—";
            var cost = r.TotalCostUsd.HasValue ? $"${r.TotalCostUsd.Value:F4}" : "—";
            var inTok = r.TotalInputTokens.HasValue ? FormatTokens(r.TotalInputTokens.Value) : "—";
            var outTok = r.TotalOutputTokens.HasValue ? FormatTokens(r.TotalOutputTokens.Value) : "—";
            var structPct = r.StructurerFallbackRatePercent.HasValue
                ? $"{r.StructurerFallbackRatePercent.Value:F1}%"
                : "—";
            Console.WriteLine(
                $"  {r.Role,-26}  {r.Provider,-22}  {r.TotalRuns,4}  {r.Wins,4}  {winPct,6}  {score,8}  {dur,8}  {cost,10}  {inTok,10}  {outTok,10}  {structPct,7}");
        }

        Console.WriteLine();
    }

    private static void PrintEvaluatorReliability(IReadOnlyList<EvaluatorReliabilityRecord> rows)
    {
        if (rows.Count == 0) return;

        Console.WriteLine("── Evaluator Reliability (winner regression rate) ───────");
        Console.WriteLine(
            $"  {"Role",-26}  {"Provider",-22}  {"Verdicts",8}  {"Regressed",9}  {"Rate",6}");
        Console.WriteLine(
            $"  {"──────────────────────────",-26}  {"──────────────────────",-22}  {"────────",8}  {"─────────",9}  {"──────",6}");

        foreach (var r in rows)
        {
            var rate = r.RegressionRatePercent.HasValue ? $"{r.RegressionRatePercent.Value:F1}%" : "—";
            Console.WriteLine(
                $"  {r.EvaluatorRole,-26}  {r.EvaluatorProvider,-22}  {r.TotalVerdicts,8}  {r.RegressedCount,9}  {rate,6}");
        }

        Console.WriteLine();
    }

    private static void PrintFastPathHitRate(IReadOnlyList<FastPathHitRecord> rows)
    {
        if (rows.Count == 0) return;

        Console.WriteLine("── Re-run Fast-Path Hit Rate ────────────────────────────");
        Console.WriteLine(
            $"  {"State",-22}  {"Step",-26}  {"Role",-22}  {"Total",5}  {"Hits",5}  {"Rate",6}");
        Console.WriteLine(
            $"  {"──────────────────────",-22}  {"──────────────────────────",-26}  {"──────────────────────",-22}  {"─────",5}  {"─────",5}  {"──────",6}");

        foreach (var r in rows)
        {
            var rate = r.HitRatePercent.HasValue ? $"{r.HitRatePercent.Value:F1}%" : "—";
            Console.WriteLine(
                $"  {r.StateName,-22}  {r.StepName,-26}  {r.Role,-22}  {r.TotalInvocations,5}  {r.FastPathHits,5}  {rate,6}");
        }

        Console.WriteLine();
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens < 1_000) return tokens.ToString();
        if (tokens < 1_000_000) return $"{tokens / 1_000.0:F1}k";
        return $"{tokens / 1_000_000.0:F1}M";
    }

    private static void PrintHeadToHead(IReadOnlyList<HeadToHeadRecord> rows)
    {
        if (rows.Count == 0) return;

        Console.WriteLine("── Head-to-Head (candidate pairs) ───────────────────────");
        Console.WriteLine(
            $"  {"Role",-26}  {"Provider A",-22}  {"Provider B",-22}  {"A",3}  {"B",3}  {"Tie",3}");
        Console.WriteLine(
            $"  {"──────────────────────────",-26}  {"──────────────────────",-22}  {"──────────────────────",-22}  {"───",3}  {"───",3}  {"───",3}");

        foreach (var r in rows)
        {
            Console.WriteLine(
                $"  {r.Role,-26}  {r.ProviderA,-22}  {r.ProviderB,-22}  {r.AWins,3}  {r.BWins,3}  {r.Ties,3}");
        }

        Console.WriteLine();
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
