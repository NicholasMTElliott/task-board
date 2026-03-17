namespace TaskBoard.Worker.Processing;

public sealed class QueueProcessingOptions
{
    public const string SectionName = "QueueProcessing";

    public int DefaultBatchSize { get; init; } = 10;

    public int DefaultVisibilityTimeoutSeconds { get; init; } = 30;

    public int LoopIdleDelaySeconds { get; init; } = 1;

    public int MaxRetries { get; init; } = 3;

    public string QueueName { get; init; } = "events";

    public int LockTtlSeconds { get; init; } = 300;

    public int ResolveBatchSize(int? value)
    {
        var candidate = value ?? DefaultBatchSize;
        return candidate > 0 ? candidate : DefaultBatchSize;
    }

    public int ResolveVisibilityTimeoutSeconds(int? value)
    {
        var candidate = value ?? DefaultVisibilityTimeoutSeconds;
        return candidate > 0 ? candidate : DefaultVisibilityTimeoutSeconds;
    }

    public int ResolveLoopIdleDelaySeconds()
    {
        return LoopIdleDelaySeconds > 0 ? LoopIdleDelaySeconds : 1;
    }
}