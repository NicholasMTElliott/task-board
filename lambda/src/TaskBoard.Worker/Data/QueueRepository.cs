using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgmq;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Data;

public sealed class QueueRepository(NpgmqClient npgmqClient, IOptions<QueueProcessingOptions> queueOptions, ILogger<QueueRepository> logger) : IQueueRepository
{
    private readonly NpgmqClient _npgmqClient = npgmqClient;
    private readonly ILogger<QueueRepository> _logger = logger;
    private readonly string _queueName = queueOptions.Value.QueueName;

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

    public async Task<long> EnqueueAsync(string messageJson, CancellationToken cancellationToken)
    {
        var messageId = await _npgmqClient.SendAsync(_queueName, messageJson, cancellationToken);
        _logger.LogInformation("Enqueued message {MessageId} to queue '{QueueName}'", messageId, _queueName);
        return messageId;
    }

    public async Task MarkSucceededAsync(long messageId, CancellationToken cancellationToken)
    {
        // Archive first to preserve audit trail, fall back to delete if archive fails
        var archived = await _npgmqClient.ArchiveAsync(_queueName, messageId, cancellationToken);
        if (archived)
        {
            return;
        }

        var deleted = await _npgmqClient.DeleteAsync(_queueName, messageId, cancellationToken);
        if (!deleted)
        {
            throw new InvalidOperationException($"Failed to archive or delete message {messageId} from queue '{_queueName}'.");
        }

        _logger.LogWarning("Message {MessageId} was deleted instead of archived — audit trail not preserved.", messageId);
    }

    public Task MarkFailedAsync(long messageId, string reason, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Message {MessageId} failed and will be retried after visibility timeout. Reason: {Reason}", messageId, reason);
        return Task.CompletedTask;
    }

    public async Task MarkDeadLetteredAsync(long messageId, string actionId, string reason, CancellationToken cancellationToken)
    {
        _logger.LogError(
            "Message {MessageId} (action {ActionId}) dead-lettered: {Reason}",
            messageId, actionId, reason);

        var archived = await _npgmqClient.ArchiveAsync(_queueName, messageId, cancellationToken);
        if (archived)
        {
            return;
        }

        var deleted = await _npgmqClient.DeleteAsync(_queueName, messageId, cancellationToken);
        if (!deleted)
        {
            _logger.LogError("Failed to archive or delete dead-lettered message {MessageId}.", messageId);
        }
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

            queueMessages.Add(new QueueMessage(item.MsgId, actionId, cardId, payloadJson, enqueuedAtUtc, item.ReadCt));
        }

        return queueMessages;
    }
}