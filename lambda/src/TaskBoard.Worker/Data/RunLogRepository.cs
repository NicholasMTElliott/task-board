using Npgsql;

namespace TaskBoard.Worker.Data;

public sealed class RunLogRepository(NpgsqlDataSource dataSource) : IRunLogRepository
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task WriteAsync(string cardId, string role, string? inputHash,
        string? outputHash, string outcome, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into run_log (card_id, role, input_hash, output_hash, outcome)
            values (@card_id, @role, @input_hash, @output_hash, @outcome);
            """;
        command.Parameters.AddWithValue("card_id", cardId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("input_hash", (object?)inputHash ?? DBNull.Value);
        command.Parameters.AddWithValue("output_hash", (object?)outputHash ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome", outcome);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
