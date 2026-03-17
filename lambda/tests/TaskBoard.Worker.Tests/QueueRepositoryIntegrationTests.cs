using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgmq;
using Npgsql;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Fixtures;

namespace TaskBoard.Worker.Tests;

public sealed class QueueRepositoryIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public QueueRepositoryIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// JSON payload matching the exact shape the Cloudflare Worker produces.
    /// This is the shared contract between the two systems.
    /// </summary>
    private static string MakeWorkerPayload(string actionId, string? cardId) =>
        $$"""
        {
            "actionId": "{{actionId}}",
            "cardId": {{(cardId is null ? "null" : $"\"{cardId}\"")}},
            "receivedAtUtc": "2026-03-11T00:00:00.000Z",
            "payload": {
                "action": {
                    "id": "{{actionId}}",
                    "data": {
                        "card": {{(cardId is null ? "null" : $"{{ \"id\": \"{cardId}\" }}")}}
                    }
                }
            }
        }
        """;

    private QueueRepository CreateRepository(NpgmqClient npgmqClient) =>
        new(
            npgmqClient,
            Options.Create(new QueueProcessingOptions { QueueName = "events" }),
            NullLogger<QueueRepository>.Instance);

    private async Task<long> SendMessage(string payloadJson)
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT pgmq.send('events', $1::jsonb)",
            conn);
        cmd.Parameters.AddWithValue(payloadJson);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private async Task DeleteAllMessages()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        // Purge the queue so tests are isolated
        await using var cmd = new NpgsqlCommand("DELETE FROM pgmq.q_events", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RoundTrip_SendThenClaim_ReturnsCorrectQueueMessage()
    {
        await DeleteAllMessages();
        var payloadJson = MakeWorkerPayload("act_rt_1", "card_rt_1");
        await SendMessage(payloadJson);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var npgmqClient = new NpgmqClient(dataSource);
        var repo = CreateRepository(npgmqClient);

        var messages = await repo.ClaimBatchAsync(10, 30, CancellationToken.None);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal("act_rt_1", msg.ActionId);
        Assert.Equal("card_rt_1", msg.CardId);
        Assert.Contains("act_rt_1", msg.PayloadJson);
        Assert.True(msg.MessageId > 0);
        Assert.Equal(1, msg.ReadCount);
    }

    [Fact]
    public async Task Claim_MissingActionId_SkipsMessage()
    {
        await DeleteAllMessages();
        // Send a malformed payload without actionId
        await SendMessage("""{"someField": "value"}""");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var npgmqClient = new NpgmqClient(dataSource);
        var repo = CreateRepository(npgmqClient);

        var messages = await repo.ClaimBatchAsync(10, 30, CancellationToken.None);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task Claim_NullCardId_IsPreserved()
    {
        await DeleteAllMessages();
        var payloadJson = MakeWorkerPayload("act_null_card", null);
        await SendMessage(payloadJson);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var npgmqClient = new NpgmqClient(dataSource);
        var repo = CreateRepository(npgmqClient);

        var messages = await repo.ClaimBatchAsync(10, 30, CancellationToken.None);

        Assert.Single(messages);
        Assert.Equal("act_null_card", messages[0].ActionId);
        Assert.Null(messages[0].CardId);
    }

    [Fact]
    public async Task MarkSucceeded_ArchivesMessage()
    {
        await DeleteAllMessages();
        var payloadJson = MakeWorkerPayload("act_archive", "card_archive");
        await SendMessage(payloadJson);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var npgmqClient = new NpgmqClient(dataSource);
        var repo = CreateRepository(npgmqClient);

        var messages = await repo.ClaimBatchAsync(10, 30, CancellationToken.None);
        Assert.Single(messages);
        var msgId = messages[0].MessageId;

        await repo.MarkSucceededAsync(msgId, CancellationToken.None);

        // Verify gone from active queue
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT count(*) FROM pgmq.q_events WHERE msg_id = $1", conn);
        checkCmd.Parameters.AddWithValue(msgId);
        var activeCount = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
        Assert.Equal(0, activeCount);

        // Verify present in archive
        await using var archiveCmd = new NpgsqlCommand(
            "SELECT count(*) FROM pgmq.a_events WHERE msg_id = $1", conn);
        archiveCmd.Parameters.AddWithValue(msgId);
        var archiveCount = Convert.ToInt64(await archiveCmd.ExecuteScalarAsync());
        Assert.Equal(1, archiveCount);
    }

    [Fact]
    public async Task MarkDeadLettered_ArchivesMessage()
    {
        await DeleteAllMessages();
        var payloadJson = MakeWorkerPayload("act_dl", "card_dl");
        await SendMessage(payloadJson);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var npgmqClient = new NpgmqClient(dataSource);
        var repo = CreateRepository(npgmqClient);

        var messages = await repo.ClaimBatchAsync(10, 30, CancellationToken.None);
        Assert.Single(messages);
        var msgId = messages[0].MessageId;

        await repo.MarkDeadLetteredAsync(msgId, "act_dl", "exceeded max retries", CancellationToken.None);

        // Verify gone from active queue
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var checkCmd = new NpgsqlCommand(
            "SELECT count(*) FROM pgmq.q_events WHERE msg_id = $1", conn);
        checkCmd.Parameters.AddWithValue(msgId);
        var activeCount = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
        Assert.Equal(0, activeCount);

        // Verify present in archive
        await using var archiveCmd = new NpgsqlCommand(
            "SELECT count(*) FROM pgmq.a_events WHERE msg_id = $1", conn);
        archiveCmd.Parameters.AddWithValue(msgId);
        var archiveCount = Convert.ToInt64(await archiveCmd.ExecuteScalarAsync());
        Assert.Equal(1, archiveCount);
    }

    [Fact]
    public async Task ReadCount_IncrementsOnReRead()
    {
        await DeleteAllMessages();
        var payloadJson = MakeWorkerPayload("act_reread", "card_reread");
        await SendMessage(payloadJson);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var npgmqClient = new NpgmqClient(dataSource);
        var repo = CreateRepository(npgmqClient);

        // First read with short visibility timeout
        var firstRead = await repo.ClaimBatchAsync(10, 1, CancellationToken.None);
        Assert.Single(firstRead);
        Assert.Equal(1, firstRead[0].ReadCount);

        // Wait for visibility timeout to expire
        await Task.Delay(1500);

        // Second read — ReadCount should increment
        var secondRead = await repo.ClaimBatchAsync(10, 30, CancellationToken.None);
        Assert.Single(secondRead);
        Assert.Equal(2, secondRead[0].ReadCount);
    }
}
