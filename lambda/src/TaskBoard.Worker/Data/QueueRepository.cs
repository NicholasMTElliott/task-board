using System.Text.Json;
using Npgmq;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Data;

public sealed class QueueRepository(NpgmqClient npgmqClient, ILogger<QueueRepository> logger)
{
    private readonly NpgmqClient _npgmqClient = npgmqClient;
    private readonly ILogger<QueueRepository> _logger = logger;
    private readonly string _queueName = Environment.GetEnvironmentVariable("PGMQ_QUEUE_NAME") ?? "events";

    public async Task<IReadOnlyList<QueueMessage>> ClaimBatchAsync(
        int maxBatchSize,
        int visibilityTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var messages = await _npgmqClient.ReadBatchAsync<string>(
            _queueName,
            visibilityTimeoutSeconds,
            maxBatchSize,
            cancellationToken);

        return ConvertBatch(messages);
    }

    public async Task MarkSucceededAsync(long messageId, CancellationToken cancellationToken)
    {
        var deleted = await _npgmqClient.DeleteAsync(_queueName, messageId, cancellationToken);
        if (deleted)
        {
            return;
        }

        var archived = await _npgmqClient.ArchiveAsync(_queueName, messageId, cancellationToken);
        if (!archived)
        {
            throw new InvalidOperationException("NpgmqClient does not expose DeleteAsync or ArchiveAsync compatible methods.");
        }
    }

    public Task MarkFailedAsync(long messageId, string reason, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Message {MessageId} failed and will be retried after visibility timeout. Reason: {Reason}", messageId, reason);
        return Task.CompletedTask;
    }

    private static IReadOnlyList<QueueMessage> ConvertBatch(IReadOnlyList<NpgmqMessage<string>> messages)
    {
        var queueMessages = new List<QueueMessage>();

        foreach (var item in messages)
        {
            var payloadJson = string.IsNullOrWhiteSpace(item.Message)
                ? "{}"
                : item.Message;

            string? actionId = null;
            string? cardId = null;

            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                actionId = root.TryGetProperty("actionId", out var actionIdValue) ? actionIdValue.GetString() : null;
                cardId = root.TryGetProperty("cardId", out var cardIdValue) ? cardIdValue.GetString() : null;
            }
            catch
            {
                // Keep raw payload for diagnostics.
            }

            if (string.IsNullOrWhiteSpace(actionId))
            {
                continue;
            }

            var enqueuedAtUtc = item.EnqueuedAt;

            queueMessages.Add(new QueueMessage(item.MsgId, actionId, cardId, payloadJson, enqueuedAtUtc));
        }

        return queueMessages;
    }
}