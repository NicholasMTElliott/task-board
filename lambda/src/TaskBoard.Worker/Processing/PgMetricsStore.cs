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
