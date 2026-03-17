using Npgsql;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Tests.Fixtures;

namespace TaskBoard.Worker.Tests;

public class RunLogRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task WriteAsync_InsertsRowWithCorrectFields()
    {
        var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var sut = new RunLogRepository(dataSource);
        var cardId = $"card-{Guid.NewGuid():N}";

        await sut.WriteAsync(cardId, "business_analyst", "input-hash-1", "output-hash-1", "COMPLETE", CancellationToken.None);

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "select card_id, role, input_hash, output_hash, outcome from run_log where card_id = @card_id",
            conn);
        cmd.Parameters.AddWithValue("card_id", cardId);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(cardId, reader.GetString(0));
        Assert.Equal("business_analyst", reader.GetString(1));
        Assert.Equal("input-hash-1", reader.GetString(2));
        Assert.Equal("output-hash-1", reader.GetString(3));
        Assert.Equal("COMPLETE", reader.GetString(4));
    }
}
