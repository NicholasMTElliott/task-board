using Npgsql;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

public sealed class PgDependencyWaitStore(
    NpgsqlDataSource dataSource,
    ITenantIdentifier tenant,
    ILogger<PgDependencyWaitStore> logger) : IDependencyWaitStore
{
    public async Task RecordBlockedAsync(
        string cardId,
        IReadOnlyList<CardDependency> unresolvedBlockers,
        string source,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var blocker in unresolvedBlockers)
        {
            await using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO card_dependency_wait
                    (tenant_id, card_id, blocker_card_id, source, first_seen_at_utc, last_seen_at_utc,
                     resolved_at_utc, last_blocker_title, last_blocker_column)
                VALUES ($1, $2, $3, $4, NOW(), NOW(), NULL, $5, $6)
                ON CONFLICT (tenant_id, card_id, blocker_card_id, source)
                DO UPDATE SET
                    last_seen_at_utc = NOW(),
                    resolved_at_utc = NULL,
                    last_blocker_title = EXCLUDED.last_blocker_title,
                    last_blocker_column = EXCLUDED.last_blocker_column
                """;
            upsert.Parameters.AddWithValue(tenant.Value);
            upsert.Parameters.AddWithValue(cardId);
            upsert.Parameters.AddWithValue(blocker.CardId);
            upsert.Parameters.AddWithValue(source);
            upsert.Parameters.AddWithValue(blocker.Title is null ? DBNull.Value : (object)blocker.Title);
            upsert.Parameters.AddWithValue(blocker.ColumnId is null ? DBNull.Value : (object)blocker.ColumnId);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using var resolve = conn.CreateCommand();
        resolve.Transaction = tx;
        resolve.CommandText = """
            UPDATE card_dependency_wait
            SET resolved_at_utc = NOW()
            WHERE tenant_id = $1
              AND card_id = $2
              AND source = $3
              AND resolved_at_utc IS NULL
              AND NOT (blocker_card_id = ANY($4))
            """;
        resolve.Parameters.AddWithValue(tenant.Value);
        resolve.Parameters.AddWithValue(cardId);
        resolve.Parameters.AddWithValue(source);
        resolve.Parameters.AddWithValue(unresolvedBlockers.Select(b => b.CardId).ToArray());
        await resolve.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        logger.LogDebug(
            "Recorded {Count} unresolved dependency wait(s) for card {CardId}",
            unresolvedBlockers.Count, cardId);
    }
}
