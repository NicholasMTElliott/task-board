using Npgsql;

namespace TaskBoard.Worker.Data;

public sealed class CardStateRepository(NpgsqlDataSource dataSource) : ICardStateRepository
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task<bool> TryAcquireLockAsync(string cardId, string lockId, TimeSpan lockTtl, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into card_state (card_id, current_lock, updated_at_utc)
            values (@card_id, @lock_id, now())
            on conflict (card_id) do update
                set current_lock = @lock_id, updated_at_utc = now()
                where card_state.current_lock is null
                   or card_state.updated_at_utc < now() - @lock_ttl::interval;
            """;
        command.Parameters.AddWithValue("card_id", cardId);
        command.Parameters.AddWithValue("lock_id", lockId);
        command.Parameters.AddWithValue("lock_ttl", lockTtl);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<CardState?> GetStateAsync(string cardId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select card_id, origin_list_id, waiting_on_human
            from card_state
            where card_id = @card_id;
            """;
        command.Parameters.AddWithValue("card_id", cardId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new CardState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2));
    }

    public async Task ReleaseLockAsync(string cardId, string lockId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update card_state
            set current_lock = null, updated_at_utc = now()
            where card_id = @card_id and current_lock = @lock_id;
            """;
        command.Parameters.AddWithValue("card_id", cardId);
        command.Parameters.AddWithValue("lock_id", lockId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateStateAsync(string cardId, string lastProcessedEvent,
        string lastKnownList, string? originListId, bool waitingOnHuman, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into card_state (card_id, last_processed_event, last_known_list, origin_list_id, waiting_on_human, updated_at_utc)
            values (@card_id, @last_processed_event, @last_known_list, @origin_list_id, @waiting_on_human, now())
            on conflict (card_id) do update
                set last_processed_event = @last_processed_event,
                    last_known_list = @last_known_list,
                    origin_list_id = @origin_list_id,
                    waiting_on_human = @waiting_on_human,
                    updated_at_utc = now();
            """;
        command.Parameters.AddWithValue("card_id", cardId);
        command.Parameters.AddWithValue("last_processed_event", lastProcessedEvent);
        command.Parameters.AddWithValue("last_known_list", lastKnownList);
        command.Parameters.AddWithValue("origin_list_id", (object?)originListId ?? DBNull.Value);
        command.Parameters.AddWithValue("waiting_on_human", waitingOnHuman);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
