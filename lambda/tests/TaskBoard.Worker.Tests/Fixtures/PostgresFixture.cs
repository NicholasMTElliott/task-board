using Npgsql;
using Testcontainers.PostgreSql;

namespace TaskBoard.Worker.Tests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply migrations V1–V5 (queue, idempotency, card_state, run_log)
        var migrationsDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "db", "migrations"));

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        foreach (var file in new[]
        {
            "V1__processed_events.sql",
            "V2__pgmq_core.sql",
            "V3__pgmq_create_events_queue.sql",
            "V4__card_state.sql",
            "V5__run_log.sql",
        })
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(migrationsDir, file));
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
