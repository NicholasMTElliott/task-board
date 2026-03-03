using Npgsql;

namespace TaskBoard.Worker.Data;

public sealed class ProcessedEventsRepository(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    /// <summary>
    /// Attempts to register an action id as processed.
    /// Returns false when the action id already exists.
    /// </summary>
    public async Task<bool> TryRegisterAsync(string actionId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into processed_events (action_id)
            values (@action_id)
            on conflict (action_id) do nothing;
            """;
        command.Parameters.AddWithValue("action_id", actionId);

        var rowsInserted = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsInserted > 0;
    }
}