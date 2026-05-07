using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TaskBoard.Worker.Clients;

public sealed class PgmqPingQueueClient(
    NpgsqlDataSource dataSource,
    IOptionsMonitor<PgmqOptions> options,
    ILogger<PgmqPingQueueClient> logger) : IPingQueueClient
{
    private readonly PgmqOptions _options = options.CurrentValue;

    public async Task<IReadOnlyList<PingMessage>> ReadPingsAsync(CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT msg_id, enqueued_at, message FROM pgmq.read($1, $2, $3)";
        cmd.Parameters.AddWithValue(_options.PingQueueName);
        cmd.Parameters.AddWithValue(_options.VisibilityTimeoutSeconds);
        cmd.Parameters.AddWithValue(_options.BatchSize);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var pings = new List<PingMessage>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var msgId = reader.GetInt64(0);
            var enqueuedAt = reader.GetFieldValue<DateTimeOffset>(1);
            var messageJson = reader.GetString(2);

            var source = "unknown";
            try
            {
                using var doc = JsonDocument.Parse(messageJson);
                if (doc.RootElement.TryGetProperty("source", out var srcEl))
                    source = srcEl.GetString() ?? "unknown";
            }
            catch (JsonException)
            {
                logger.LogWarning("Failed to parse ping message JSON: {Json}", messageJson[..Math.Min(200, messageJson.Length)]);
            }

            pings.Add(new PingMessage(msgId, source, enqueuedAt));
        }

        if (pings.Count > 0)
            logger.LogInformation("Read {Count} ping(s) from queue '{Queue}'", pings.Count, _options.PingQueueName);

        return pings;
    }

    public async Task ArchivePingAsync(long messageId, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT pgmq.archive($1, $2)";
        cmd.Parameters.AddWithValue(_options.PingQueueName);
        cmd.Parameters.AddWithValue(messageId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        logger.LogDebug("Archived ping {MessageId} from queue '{Queue}'", messageId, _options.PingQueueName);
    }
}
