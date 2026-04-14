using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

public sealed class CardClaimService(
    NpgsqlDataSource dataSource,
    IOptions<PgmqOptions> options,
    ITenantIdentifier tenant,
    ILogger<CardClaimService> logger) : ICardClaimService
{
    private readonly int _staleMinutes = options.Value.StaleClaimMinutes;

    public async Task<bool> TryClaimAsync(string cardId, string agentId, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // Ensure the card_state row exists
        await using (var upsert = conn.CreateCommand())
        {
            upsert.CommandText = "INSERT INTO card_state (tenant_id, card_id) VALUES ($1, $2) ON CONFLICT (tenant_id, card_id) DO NOTHING";
            upsert.Parameters.AddWithValue(tenant.Value);
            upsert.Parameters.AddWithValue(cardId);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        // Atomic claim — also reclaims stale locks
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE card_state
            SET current_lock = $3, claimed_at = NOW(), updated_at_utc = NOW()
            WHERE tenant_id = $1
              AND card_id = $2
              AND (current_lock IS NULL OR claimed_at < NOW() - make_interval(mins => $4))
            RETURNING card_id
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(cardId);
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue(_staleMinutes);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var claimed = await reader.ReadAsync(cancellationToken);

        if (claimed)
            logger.LogInformation("Claimed card {CardId} for agent {AgentId}", cardId, agentId);
        else
            logger.LogDebug("Card {CardId} already claimed by another agent", cardId);

        return claimed;
    }

    public async Task ReleaseAsync(string cardId, string agentId, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE card_state
            SET current_lock = NULL, claimed_at = NULL, updated_at_utc = NOW()
            WHERE tenant_id = $1 AND card_id = $2 AND current_lock = $3
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(cardId);
        cmd.Parameters.AddWithValue(agentId);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (rows > 0)
            logger.LogInformation("Released claim on card {CardId} for agent {AgentId}", cardId, agentId);
        else
            logger.LogDebug("No claim to release on card {CardId} (lock not held by {AgentId})", cardId, agentId);
    }
}
