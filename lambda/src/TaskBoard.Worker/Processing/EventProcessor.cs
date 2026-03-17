using Microsoft.Extensions.Options;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class EventProcessor(
    IQueueRepository queueRepository,
    IProcessedEventsRepository processedEventsRepository,
    Orchestrator orchestrator,
    IOptions<QueueProcessingOptions> queueOptions,
    ILogger<EventProcessor> logger)
{
    private readonly IQueueRepository _queueRepository = queueRepository;
    private readonly IProcessedEventsRepository _processedEventsRepository = processedEventsRepository;
    private readonly Orchestrator _orchestrator = orchestrator;
    private readonly QueueProcessingOptions _queueOptions = queueOptions.Value;
    private readonly ILogger<EventProcessor> _logger = logger;

    public Task<ProcessorResult> ProcessOneAsync(CancellationToken cancellationToken)
    {
        return ProcessBatchAsync(1, _queueOptions.ResolveVisibilityTimeoutSeconds(null), cancellationToken);
    }

    public async Task<ProcessorResult> ProcessOneOrWaitAsync(TimeSpan waitDuration, CancellationToken cancellationToken)
    {
        var endAt = DateTimeOffset.UtcNow.Add(waitDuration);

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await ProcessOneAsync(cancellationToken);
            if (result.Claimed > 0)
            {
                return result;
            }

            if (DateTimeOffset.UtcNow >= endAt)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return new ProcessorResult(0, 0, 0, 0, 0, DateTimeOffset.UtcNow);
    }

    public async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await ProcessBatchAsync(
                _queueOptions.ResolveBatchSize(null),
                _queueOptions.ResolveVisibilityTimeoutSeconds(null),
                cancellationToken);

            if (result.Claimed == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_queueOptions.ResolveLoopIdleDelaySeconds()), cancellationToken);
            }
        }
    }

    public async Task<ProcessorResult> ProcessBatchAsync(int batchSize, int visibilityTimeoutSeconds, CancellationToken cancellationToken)
    {
        var messages = await _queueRepository.ClaimBatchAsync(batchSize, visibilityTimeoutSeconds, cancellationToken);

        var processed = 0;
        var duplicates = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var message in messages)
        {
            try
            {
                if (message.ReadCount > _queueOptions.MaxRetries)
                {
                    deadLettered++;
                    await _queueRepository.MarkDeadLetteredAsync(
                        message.MessageId,
                        message.ActionId,
                        $"Exceeded max retries ({_queueOptions.MaxRetries})",
                        cancellationToken);
                    continue;
                }

                var isFirstProcessing = await _processedEventsRepository.TryRegisterAsync(message.ActionId, cancellationToken);
                if (!isFirstProcessing)
                {
                    duplicates++;
                    await _queueRepository.MarkSucceededAsync(message.MessageId, cancellationToken);
                    continue;
                }

                await ProcessMessageAsync(message, cancellationToken);
                processed++;
                await _queueRepository.MarkSucceededAsync(message.MessageId, cancellationToken);
            }
            catch (Exception exception)
            {
                failed++;
                await _queueRepository.MarkFailedAsync(message.MessageId, exception.Message, cancellationToken);
                _logger.LogError(exception, "Failed to process message {MessageId} for action {ActionId}", message.MessageId, message.ActionId);
            }
        }

        return new ProcessorResult(messages.Count, processed, duplicates, failed, deadLettered, DateTimeOffset.UtcNow);
    }

    private Task ProcessMessageAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing message {MessageId} action {ActionId} card {CardId}",
            message.MessageId,
            message.ActionId,
            message.CardId ?? "unknown");

        return _orchestrator.ExecuteAsync(message, cancellationToken);
    }
}

public sealed record ProcessorResult(
    int Claimed,
    int Processed,
    int Duplicates,
    int Failed,
    int DeadLettered,
    DateTimeOffset ProcessedAtUtc);