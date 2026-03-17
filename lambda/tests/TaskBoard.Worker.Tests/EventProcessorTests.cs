using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class EventProcessorTests
{
    private readonly IQueueRepository _queueRepository = Substitute.For<IQueueRepository>();
    private readonly IProcessedEventsRepository _processedEventsRepository = Substitute.For<IProcessedEventsRepository>();
    private readonly Orchestrator _orchestrator;
    private readonly IOptions<QueueProcessingOptions> _options = Options.Create(new QueueProcessingOptions());
    private readonly EventProcessor _sut;

    public EventProcessorTests()
    {
        _orchestrator = new Orchestrator(
            new WorkflowConfig(new Dictionary<string, WorkflowState>(), new Dictionary<string, WorkflowRole>()),
            Substitute.For<ITrelloClient>(),
            Substitute.For<ILlmClient>(),
            Substitute.For<ICardStateRepository>(),
            Substitute.For<IRunLogRepository>(),
            Options.Create(new QueueProcessingOptions()),
            NullLogger<Orchestrator>.Instance);

        _sut = new EventProcessor(
            _queueRepository,
            _processedEventsRepository,
            _orchestrator,
            _options,
            NullLogger<EventProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessBatch_EmptyQueue_ReturnsZeroCounts()
    {
        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<QueueMessage>());

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.DeadLettered);
    }

    [Fact]
    public async Task ProcessBatch_NewMessage_ProcessesAndMarksSucceeded()
    {
        var message = new QueueMessage(1, "action-1", "card-1", "{}", DateTimeOffset.UtcNow, ReadCount: 1);

        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        _processedEventsRepository
            .TryRegisterAsync("action-1", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.DeadLettered);

        await _queueRepository.Received(1).MarkSucceededAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatch_DuplicateMessage_SkipsProcessingAndMarksSucceeded()
    {
        var message = new QueueMessage(2, "action-dup", "card-1", "{}", DateTimeOffset.UtcNow, ReadCount: 1);

        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        _processedEventsRepository
            .TryRegisterAsync("action-dup", Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.DeadLettered);

        await _queueRepository.Received(1).MarkSucceededAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatch_MixedMessages_CountsCorrectly()
    {
        var msg1 = new QueueMessage(1, "action-new", "card-1", "{}", DateTimeOffset.UtcNow, ReadCount: 1);
        var msg2 = new QueueMessage(2, "action-dup", "card-2", "{}", DateTimeOffset.UtcNow, ReadCount: 1);

        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { msg1, msg2 });

        _processedEventsRepository
            .TryRegisterAsync("action-new", Arg.Any<CancellationToken>())
            .Returns(true);
        _processedEventsRepository
            .TryRegisterAsync("action-dup", Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(2, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.DeadLettered);
    }

    [Fact]
    public async Task ProcessBatch_IdempotencyCheckThrows_CountsAsFailed()
    {
        var message = new QueueMessage(3, "action-err", "card-1", "{}", DateTimeOffset.UtcNow, ReadCount: 1);

        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        _processedEventsRepository
            .TryRegisterAsync("action-err", Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("DB down"));

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.DeadLettered);

        await _queueRepository.Received(1).MarkFailedAsync(3, "DB down", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatch_ExceedsMaxRetries_DeadLettersMessage()
    {
        var message = new QueueMessage(4, "action-poison", "card-1", "{}", DateTimeOffset.UtcNow, ReadCount: 4);

        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.DeadLettered);

        await _queueRepository.Received(1).MarkDeadLetteredAsync(
            4, "action-poison", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _processedEventsRepository.DidNotReceive()
            .TryRegisterAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatch_AtMaxRetries_StillProcesses()
    {
        var message = new QueueMessage(5, "action-retry", "card-1", "{}", DateTimeOffset.UtcNow, ReadCount: 3);

        _queueRepository
            .ClaimBatchAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        _processedEventsRepository
            .TryRegisterAsync("action-retry", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.ProcessBatchAsync(10, 30, CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.DeadLettered);
    }
}
