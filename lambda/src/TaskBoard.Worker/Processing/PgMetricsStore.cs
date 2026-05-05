using Npgsql;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// PostgreSQL implementation of IMetricsStore.
/// Queries the v_run_metrics, v_step_duration, v_card_metrics, and v_card_rework views
/// created by migration V12, plus direct agent_run queries for cycle-time-per-point.
/// All queries accept an optional DateTimeOffset cutoff for time-window filtering.
/// </summary>
public sealed class PgMetricsStore(
    NpgsqlDataSource dataSource,
    ITenantIdentifier tenant,
    ILogger<PgMetricsStore> logger) : IMetricsStore
{
    public async Task<RunSummary> GetRunSummaryAsync(DateTimeOffset? since, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE outcome IS NOT NULL)                          AS total_runs,
                COUNT(*) FILTER (WHERE outcome = 'COMPLETE')                         AS complete_runs,
                COUNT(*) FILTER (WHERE outcome = 'NEEDS_INFO')                       AS needs_info_runs,
                COUNT(*) FILTER (WHERE outcome = 'ERROR')                            AS error_runs,
                COUNT(*) FILTER (WHERE failure_reason = 'RATE_LIMIT')               AS rate_limited_runs
            FROM agent_run
            WHERE tenant_id = $1
              AND completed_at_utc IS NOT NULL
              AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new RunSummary(0, 0, 0, 0, 0, 0);

        var total = reader.GetInt32(0);
        var complete = reader.GetInt32(1);
        var needsInfo = reader.GetInt32(2);
        var error = reader.GetInt32(3);
        var rateLimited = reader.GetInt32(4);
        var successRate = total > 0 ? complete * 100.0 / total : 0.0;

        return new RunSummary(total, complete, needsInfo, error, rateLimited, successRate);
    }

    public async Task<IReadOnlyList<CardMetrics>> GetCardMetricsAsync(
        string? cardId, DateTimeOffset? since, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                card_id,
                MIN(started_at_utc)                                                  AS first_run_start,
                MAX(completed_at_utc)                                                AS last_run_end,
                EXTRACT(EPOCH FROM (MAX(completed_at_utc) - MIN(started_at_utc)))   AS cycle_time_seconds,
                SUM(EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)))        AS working_time_seconds,
                EXTRACT(EPOCH FROM (MAX(completed_at_utc) - MIN(started_at_utc))) -
                    SUM(EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)))    AS waiting_time_seconds,
                COUNT(*)                                                             AS total_runs,
                MAX(estimate)                                                        AS estimate
            FROM agent_run
            WHERE tenant_id = $1
              AND completed_at_utc IS NOT NULL
              AND ($2::text IS NULL OR card_id = $2)
              AND ($3::timestamptz IS NULL OR started_at_utc >= $3)
            GROUP BY card_id
            ORDER BY MIN(started_at_utc) DESC
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(cardId is not null ? (object)cardId : DBNull.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        var results = new List<CardMetrics>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CardMetrics(
                CardId: reader.GetString(0),
                FirstRunStart: reader.GetFieldValue<DateTimeOffset>(1),
                LastRunEnd: reader.GetFieldValue<DateTimeOffset>(2),
                CycleTimeSeconds: reader.GetDouble(3),
                WorkingTimeSeconds: reader.GetDouble(4),
                WaitingTimeSeconds: reader.GetDouble(5),
                TotalRuns: (int)reader.GetInt64(6),
                Estimate: reader.IsDBNull(7) ? null : (double?)reader.GetDouble(7)));
        }

        logger.LogDebug("GetCardMetricsAsync returned {Count} card(s)", results.Count);
        return results;
    }

    public async Task<IReadOnlyList<StepDuration>> GetTopStepDurationsAsync(
        int top, DateTimeOffset? since, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                card_id,
                state_name,
                step_name,
                role,
                model,
                EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
            FROM step_result
            WHERE tenant_id = $1
              AND completed_at_utc IS NOT NULL
              AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            ORDER BY duration_seconds DESC
            LIMIT $3
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(top);

        var results = new List<StepDuration>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new StepDuration(
                CardId: reader.GetString(0),
                StateName: reader.GetString(1),
                StepName: reader.GetString(2),
                Role: reader.GetString(3),
                Model: reader.GetString(4),
                DurationSeconds: reader.GetDouble(5)));
        }

        return results;
    }

    public async Task<IReadOnlyList<CardRework>> GetReworkCardsAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT card_id, state_name, COUNT(*) AS entry_count, COUNT(*) - 1 AS rework_count
            FROM agent_run
            WHERE tenant_id = $1
              AND outcome IS NOT NULL
              AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            GROUP BY card_id, state_name
            HAVING COUNT(*) > 1
            ORDER BY rework_count DESC, card_id
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        var results = new List<CardRework>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CardRework(
                CardId: reader.GetString(0),
                StateName: reader.GetString(1),
                EntryCount: (int)reader.GetInt64(2),
                ReworkCount: (int)reader.GetInt64(3)));
        }

        return results;
    }

    public async Task<CycleTimePerPointSummary> GetCycleTimePerPointAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        var overall = await GetCycleTimePerPointWindowAsync(since, ct);
        var last24h = await GetCycleTimePerPointWindowAsync(DateTimeOffset.UtcNow.AddHours(-24), ct);
        var last7d = await GetCycleTimePerPointWindowAsync(DateTimeOffset.UtcNow.AddDays(-7), ct);
        var last30d = await GetCycleTimePerPointWindowAsync(DateTimeOffset.UtcNow.AddDays(-30), ct);

        return new CycleTimePerPointSummary(overall, last24h, last7d, last30d);
    }

    public async Task<IReadOnlyList<ProviderRoleMetric>> GetProviderRoleMetricsAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        // We can't use v_provider_role_metrics directly with a since-filter,
        // because the view doesn't carry started_at_utc. Inline the same
        // aggregation here so we can apply the time window.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                role,
                provider,
                COUNT(*)                                                   AS total_runs,
                COUNT(*) FILTER (WHERE selected = true)                    AS wins,
                COUNT(*) FILTER (WHERE selected IS NOT NULL)               AS runs_with_decision,
                CASE
                    WHEN COUNT(*) FILTER (WHERE selected IS NOT NULL) = 0 THEN NULL
                    ELSE 100.0 *
                        COUNT(*) FILTER (WHERE selected = true) /
                        COUNT(*) FILTER (WHERE selected IS NOT NULL)
                END                                                         AS win_rate_percent,
                AVG(quality_score) FILTER (WHERE quality_score IS NOT NULL) AS avg_quality_score,
                AVG(EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc))) AS avg_duration_seconds,
                SUM(cost_usd)                                               AS total_cost_usd,
                AVG(cost_usd) FILTER (WHERE cost_usd IS NOT NULL)           AS avg_cost_usd,
                SUM(input_tokens)                                           AS total_input_tokens,
                SUM(output_tokens)                                          AS total_output_tokens,
                AVG(input_tokens) FILTER (WHERE input_tokens IS NOT NULL)   AS avg_input_tokens,
                AVG(output_tokens) FILTER (WHERE output_tokens IS NOT NULL) AS avg_output_tokens,
                SUM(cache_read_tokens)                                      AS total_cache_read_tokens,
                SUM(cache_creation_tokens)                                  AS total_cache_creation_tokens,
                COUNT(*) FILTER (WHERE structurer_fallback_used = true)     AS structurer_fallback_count,
                CASE
                    WHEN COUNT(*) = 0 THEN NULL
                    ELSE 100.0 *
                        COUNT(*) FILTER (WHERE structurer_fallback_used = true) /
                        COUNT(*)
                END                                                         AS structurer_fallback_rate
            FROM step_result
            WHERE tenant_id = $1
              AND candidate_group_id IS NOT NULL
              AND completed_at_utc IS NOT NULL
              AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            GROUP BY role, provider
            ORDER BY role, provider
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        var results = new List<ProviderRoleMetric>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProviderRoleMetric(
                Role: reader.GetString(0),
                Provider: reader.GetString(1),
                TotalRuns: (int)reader.GetInt64(2),
                Wins: (int)reader.GetInt64(3),
                RunsWithDecision: (int)reader.GetInt64(4),
                WinRatePercent: reader.IsDBNull(5) ? null : (double?)reader.GetDouble(5),
                AvgQualityScore: reader.IsDBNull(6) ? null : (double?)(double)reader.GetDecimal(6),
                AvgDurationSeconds: reader.IsDBNull(7) ? null : (double?)reader.GetDouble(7),
                TotalCostUsd: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                AvgCostUsd: reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                TotalInputTokens: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                TotalOutputTokens: reader.IsDBNull(11) ? null : reader.GetInt64(11),
                AvgInputTokens: reader.IsDBNull(12) ? null : (double?)(double)reader.GetDecimal(12),
                AvgOutputTokens: reader.IsDBNull(13) ? null : (double?)(double)reader.GetDecimal(13),
                TotalCacheReadTokens: reader.IsDBNull(14) ? null : reader.GetInt64(14),
                TotalCacheCreationTokens: reader.IsDBNull(15) ? null : reader.GetInt64(15),
                StructurerFallbackCount: (int)reader.GetInt64(16),
                StructurerFallbackRatePercent: reader.IsDBNull(17) ? null : (double?)reader.GetDouble(17)));
        }

        logger.LogDebug("GetProviderRoleMetricsAsync returned {Count} (role, provider) row(s)", results.Count);
        return results;
    }

    public async Task<IReadOnlyList<EvaluatorReliabilityRecord>> GetEvaluatorReliabilityAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        // Inline equivalent of v_evaluator_reliability with a since-filter applied.
        // The join must include step_index AND slot_index — a multi-slot fallback
        // chain in a single run can produce multiple evaluator rows with different
        // slot_index but the same state_name. Without those join keys, a slot 0
        // evaluator that failed (no winner) would cross-match slot 1's winner.
        // IS NOT DISTINCT FROM handles the NULL=NULL case for single-slot setups
        // (where both evaluator and winner have slot_index = NULL). Filter to
        // outcome='COMPLETE' so only verdict-bearing evaluator rows count.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH evaluator_steps AS (
                SELECT tenant_id, run_id, state_name, step_index, slot_index, role, provider, started_at_utc
                FROM step_result
                WHERE tenant_id = $1
                  AND step_name LIKE '%:evaluator'
                  AND candidate_group_id IS NULL
                  AND outcome = 'COMPLETE'
                  AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            ), winners AS (
                SELECT tenant_id, run_id, state_name, step_index, slot_index, winner_regressed
                FROM step_result
                WHERE tenant_id = $1
                  AND selected = true
                  AND candidate_group_id IS NOT NULL
                  AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            )
            SELECT
                e.role,
                e.provider,
                COUNT(*)                                                   AS total_verdicts,
                COUNT(*) FILTER (WHERE w.winner_regressed = true)          AS regressed,
                CASE
                    WHEN COUNT(*) FILTER (WHERE w.winner_regressed IS NOT NULL) = 0 THEN NULL
                    ELSE 100.0 *
                        COUNT(*) FILTER (WHERE w.winner_regressed = true) /
                        COUNT(*) FILTER (WHERE w.winner_regressed IS NOT NULL)
                END                                                         AS regression_rate_percent
            FROM evaluator_steps e
            LEFT JOIN winners w
                   ON w.tenant_id  = e.tenant_id
                  AND w.run_id     = e.run_id
                  AND w.state_name = e.state_name
                  AND w.step_index = e.step_index
                  AND w.slot_index IS NOT DISTINCT FROM e.slot_index
            GROUP BY e.role, e.provider
            ORDER BY e.role, e.provider
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        var results = new List<EvaluatorReliabilityRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new EvaluatorReliabilityRecord(
                EvaluatorRole: reader.GetString(0),
                EvaluatorProvider: reader.GetString(1),
                TotalVerdicts: (int)reader.GetInt64(2),
                RegressedCount: (int)reader.GetInt64(3),
                RegressionRatePercent: reader.IsDBNull(4) ? null : (double?)reader.GetDouble(4)));
        }
        return results;
    }

    public async Task<IReadOnlyList<FastPathHitRecord>> GetFastPathHitRateAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                state_name,
                step_name,
                role,
                provider,
                COUNT(*)                                              AS total,
                COUNT(*) FILTER (WHERE fast_path_hit = true)          AS hits,
                CASE
                    WHEN COUNT(*) FILTER (WHERE fast_path_hit IS NOT NULL) = 0 THEN NULL
                    ELSE 100.0 *
                        COUNT(*) FILTER (WHERE fast_path_hit = true) /
                        COUNT(*) FILTER (WHERE fast_path_hit IS NOT NULL)
                END                                                    AS hit_rate_percent
            FROM step_result
            WHERE tenant_id = $1
              AND completed_at_utc IS NOT NULL
              AND fast_path_hit IS NOT NULL
              AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
            GROUP BY state_name, step_name, role, provider
            ORDER BY state_name, step_name, role, provider
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        var results = new List<FastPathHitRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new FastPathHitRecord(
                StateName: reader.GetString(0),
                StepName: reader.GetString(1),
                Role: reader.GetString(2),
                Provider: reader.GetString(3),
                TotalInvocations: (int)reader.GetInt64(4),
                FastPathHits: (int)reader.GetInt64(5),
                HitRatePercent: reader.IsDBNull(6) ? null : (double?)reader.GetDouble(6)));
        }
        return results;
    }

    public async Task<IReadOnlyList<HeadToHeadRecord>> GetCandidateHeadToHeadAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        // Self-join on candidate_group_id, restricting to providerA < providerB
        // (alphabetical) so each pair shows up exactly once. For each pair, count:
        //   AWins  — groups where A.selected = true
        //   BWins  — groups where B.selected = true
        //   Ties   — groups where neither selected = true (e.g., evaluator NEEDS_INFO,
        //            or both lost to a third candidate that competed in the same group)
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH pairs AS (
                SELECT
                    a.role,
                    a.provider AS provider_a,
                    b.provider AS provider_b,
                    a.candidate_group_id,
                    a.selected AS a_selected,
                    b.selected AS b_selected
                FROM step_result a
                JOIN step_result b
                  ON a.tenant_id = b.tenant_id
                 AND a.candidate_group_id = b.candidate_group_id
                 AND a.role = b.role
                 AND a.provider < b.provider
                WHERE a.tenant_id = $1
                  AND a.candidate_group_id IS NOT NULL
                  AND a.completed_at_utc IS NOT NULL
                  AND b.completed_at_utc IS NOT NULL
                  AND ($2::timestamptz IS NULL OR a.started_at_utc >= $2)
            )
            SELECT
                role,
                provider_a,
                provider_b,
                COUNT(*) FILTER (WHERE a_selected = true)                                   AS a_wins,
                COUNT(*) FILTER (WHERE b_selected = true)                                   AS b_wins,
                COUNT(*) FILTER (WHERE COALESCE(a_selected, false) = false
                                   AND COALESCE(b_selected, false) = false)                AS ties
            FROM pairs
            GROUP BY role, provider_a, provider_b
            ORDER BY role, provider_a, provider_b
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        var results = new List<HeadToHeadRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new HeadToHeadRecord(
                Role: reader.GetString(0),
                ProviderA: reader.GetString(1),
                ProviderB: reader.GetString(2),
                AWins: (int)reader.GetInt64(3),
                BWins: (int)reader.GetInt64(4),
                Ties: (int)reader.GetInt64(5)));
        }

        logger.LogDebug("GetCandidateHeadToHeadAsync returned {Count} pair row(s)", results.Count);
        return results;
    }

    private async Task<CycleTimePerPoint> GetCycleTimePerPointWindowAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        // Use DISTINCT ON to get the most recent estimate per card (reviewer directive).
        // Then join to all runs for that card to compute the full cycle time.
        cmd.CommandText = """
            WITH latest_estimate AS (
                SELECT DISTINCT ON (card_id)
                    card_id,
                    estimate
                FROM agent_run
                WHERE tenant_id = $1
                  AND outcome IS NOT NULL
                  AND completed_at_utc IS NOT NULL
                  AND estimate IS NOT NULL
                  AND ($2::timestamptz IS NULL OR started_at_utc >= $2)
                ORDER BY card_id, started_at_utc DESC
            ),
            card_cycle AS (
                SELECT
                    ar.card_id,
                    le.estimate,
                    EXTRACT(EPOCH FROM (MAX(ar.completed_at_utc) - MIN(ar.started_at_utc)))
                        AS cycle_time_seconds
                FROM agent_run ar
                JOIN latest_estimate le ON ar.card_id = le.card_id
                WHERE ar.tenant_id = $1
                  AND ar.completed_at_utc IS NOT NULL
                  AND ($2::timestamptz IS NULL OR ar.started_at_utc >= $2)
                GROUP BY ar.card_id, le.estimate
            ),
            per_point AS (
                SELECT cycle_time_seconds / NULLIF(estimate, 0) AS cycle_time_per_point
                FROM card_cycle
                WHERE estimate > 0
            )
            SELECT
                AVG(cycle_time_per_point)              AS avg_seconds_per_point,
                COALESCE(STDDEV(cycle_time_per_point), 0) AS stddev_seconds_per_point
            FROM per_point
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
            return new CycleTimePerPoint(null, null);

        return new CycleTimePerPoint(
            AvgCycleTimePerPointSeconds: reader.GetDouble(0),
            StdDevCycleTimePerPointSeconds: reader.IsDBNull(1) ? 0 : reader.GetDouble(1));
    }
}
