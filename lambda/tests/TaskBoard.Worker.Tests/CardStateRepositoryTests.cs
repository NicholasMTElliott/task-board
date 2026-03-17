using Npgsql;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Tests.Fixtures;

namespace TaskBoard.Worker.Tests;

public class CardStateRepositoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private CardStateRepository CreateSut()
    {
        var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        return new CardStateRepository(dataSource);
    }

    [Fact]
    public async Task TryAcquireLock_NewCard_ReturnsTrue()
    {
        var sut = CreateSut();
        var cardId = $"card-{Guid.NewGuid():N}";

        var result = await sut.TryAcquireLockAsync(cardId, "lock-1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task TryAcquireLock_AlreadyLocked_ReturnsFalse()
    {
        var sut = CreateSut();
        var cardId = $"card-{Guid.NewGuid():N}";

        await sut.TryAcquireLockAsync(cardId, "lock-1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var result = await sut.TryAcquireLockAsync(cardId, "lock-2", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReleaseLock_ClearsLock_AllowsReacquire()
    {
        var sut = CreateSut();
        var cardId = $"card-{Guid.NewGuid():N}";

        await sut.TryAcquireLockAsync(cardId, "lock-1", TimeSpan.FromMinutes(5), CancellationToken.None);
        await sut.ReleaseLockAsync(cardId, "lock-1", CancellationToken.None);
        var result = await sut.TryAcquireLockAsync(cardId, "lock-2", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task UpdateState_InsertsAndUpdates()
    {
        var sut = CreateSut();
        var cardId = $"card-{Guid.NewGuid():N}";

        // Insert
        await sut.UpdateStateAsync(cardId, "event-1", "list-a", null, false, CancellationToken.None);

        // Update
        await sut.UpdateStateAsync(cardId, "event-2", "list-b", "list-a", true, CancellationToken.None);

        // Verify via direct read
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "select last_processed_event, last_known_list, origin_list_id, waiting_on_human from card_state where card_id = @card_id",
            conn);
        cmd.Parameters.AddWithValue("card_id", cardId);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("event-2", reader.GetString(0));
        Assert.Equal("list-b", reader.GetString(1));
        Assert.Equal("list-a", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
    }

    [Fact]
    public async Task TryAcquireLock_StaleLock_Succeeds()
    {
        var sut = CreateSut();
        var cardId = $"card-{Guid.NewGuid():N}";

        // Acquire lock
        await sut.TryAcquireLockAsync(cardId, "lock-1", TimeSpan.FromMinutes(5), CancellationToken.None);

        // Backdate updated_at_utc to simulate stale lock
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "update card_state set updated_at_utc = now() - interval '10 minutes' where card_id = @card_id",
            conn);
        cmd.Parameters.AddWithValue("card_id", cardId);
        await cmd.ExecuteNonQueryAsync();

        // New lock should succeed because old one is stale
        var result = await sut.TryAcquireLockAsync(cardId, "lock-2", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task GetState_ExistingCard_ReturnsState()
    {
        var sut = CreateSut();
        var cardId = $"card-{Guid.NewGuid():N}";

        await sut.UpdateStateAsync(cardId, "event-1", "list-a", "list-origin", true, CancellationToken.None);

        var state = await sut.GetStateAsync(cardId, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(cardId, state.CardId);
        Assert.Equal("list-origin", state.OriginListId);
        Assert.True(state.WaitingOnHuman);
    }

    [Fact]
    public async Task GetState_NoCard_ReturnsNull()
    {
        var sut = CreateSut();

        var state = await sut.GetStateAsync("nonexistent-card", CancellationToken.None);

        Assert.Null(state);
    }
}
