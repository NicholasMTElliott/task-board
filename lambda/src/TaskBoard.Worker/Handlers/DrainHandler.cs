using Microsoft.Extensions.Options;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Handlers;

public sealed class DrainHandler(
    EventProcessor eventProcessor,
    IOptions<QueueProcessingOptions> queueOptions)
{
    private readonly EventProcessor _eventProcessor = eventProcessor;
    private readonly QueueProcessingOptions _queueOptions = queueOptions.Value;

    public async Task<DrainResult> HandleAsync(int? batchSize, int? visibilityTimeoutSeconds, CancellationToken cancellationToken)
    {
        var effectiveBatchSize = _queueOptions.ResolveBatchSize(batchSize);
        var effectiveVisibilityTimeoutSeconds = _queueOptions.ResolveVisibilityTimeoutSeconds(visibilityTimeoutSeconds);
        var processorResult = await _eventProcessor.ProcessBatchAsync(effectiveBatchSize, effectiveVisibilityTimeoutSeconds, cancellationToken);

        return new DrainResult(
            processorResult.Claimed,
            processorResult.Processed,
            processorResult.Duplicates,
            processorResult.Failed,
            processorResult.DeadLettered,
            processorResult.ProcessedAtUtc);
    }
}

public sealed record DrainResult(
    int Claimed,
    int Processed,
    int Duplicates,
    int Failed,
    int DeadLettered,
    DateTimeOffset ProcessedAtUtc);