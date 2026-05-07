using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Event-driven runner that polls a PGMQ pings queue instead of the board directly.
/// On receiving a ping, fetches the board state and processes ALL eligible cards concurrently.
/// Falls back to a safety-net board poll on a long timer if no pings arrive.
/// </summary>
public sealed class QueueDrivenRunner(
    ITaskBoardClient boardClient,
    AgentRunner agentRunner,
    MergeRunner mergeRunner,
    WorkflowConfigProvider workflowConfigProvider,
    IAgentExecutorResolver executorResolver,
    IPingQueueClient pingQueue,
    ICardClaimService cardClaim,
    AgentIdentity agentIdentity,
    IOptionsMonitor<PgmqOptions> pgmqOptions,
    ILogger<QueueDrivenRunner> logger,
    ShutdownCoordinator? shutdownCoordinator = null)
{
    private static readonly TimeSpan QueuePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FallbackBoardPollInterval = TimeSpan.FromMinutes(10);

    // Mutable snapshot: refreshed at phase entry.
    private WorkflowConfig workflowConfig = workflowConfigProvider.Current;

    public async Task RunAsync(
        string boardId,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        workflowConfig = workflowConfigProvider.Current;
        var options = pgmqOptions.CurrentValue;
        var maxConcurrent = options.MaxConcurrentAgents;
        using var semaphore = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent) : null;

        logger.LogInformation(
            "Queue-driven runner started. Queue={Queue}, MaxConcurrent={MaxConcurrent}, FallbackInterval={FallbackSec}s",
            options.PingQueueName, maxConcurrent == 0 ? "unlimited" : maxConcurrent, FallbackBoardPollInterval.TotalSeconds);

        var lastBoardPoll = DateTimeOffset.MinValue;

        // Linked token cancelled by either hard-cancel or graceful-shutdown idle signal.
        // Used for idle delays only — board processing still uses cancellationToken.
        using var idleCts = shutdownCoordinator is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownCoordinator.IdleToken)
            : null;
        var idleToken = idleCts?.Token ?? cancellationToken;

        while (!cancellationToken.IsCancellationRequested && shutdownCoordinator?.IsShutdownRequested != true)
        {
            try
            {
                var pings = await pingQueue.ReadPingsAsync(cancellationToken);

                if (pings.Count > 0)
                {
                    // Archive all pings — they're just notifications.
                    // If archiving fails, the ping reappears after visibility timeout.
                    // This is harmless (claiming prevents duplicate work) but wastes an API call,
                    // so we log failures and continue rather than retrying.
                    var archiveFailures = 0;
                    foreach (var ping in pings)
                    {
                        try { await pingQueue.ArchivePingAsync(ping.MessageId, cancellationToken); }
                        catch (Exception ex)
                        {
                            archiveFailures++;
                            logger.LogWarning(ex, "Failed to archive ping {MsgId}", ping.MessageId);
                        }
                    }
                    if (archiveFailures > 0)
                        logger.LogWarning("Failed to archive {FailCount}/{Total} pings — they will reappear after visibility timeout",
                            archiveFailures, pings.Count);

                    logger.LogInformation("Received {Count} ping(s), fetching board state", pings.Count);
                    await ProcessBoardAsync(boardId, workspacePath, semaphore, cancellationToken);
                    lastBoardPoll = DateTimeOffset.UtcNow;
                }
                else
                {
                    // No pings — check fallback timer
                    var elapsed = DateTimeOffset.UtcNow - lastBoardPoll;
                    if (elapsed >= FallbackBoardPollInterval)
                    {
                        logger.LogInformation("Fallback board poll triggered (no pings for {ElapsedMin:F1}min)", elapsed.TotalMinutes);
                        await ProcessBoardAsync(boardId, workspacePath, semaphore, cancellationToken);
                        lastBoardPoll = DateTimeOffset.UtcNow;
                    }

                    await Task.Delay(QueuePollInterval, idleToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue-driven runner cycle failed");
                try { await Task.Delay(QueuePollInterval, idleToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        var stopReason = shutdownCoordinator?.IsShutdownRequested == true ? "graceful shutdown" : "cancellation";
        logger.LogInformation("Queue-driven runner stopped ({StopReason})", stopReason);
    }

    private async Task ProcessBoardAsync(
        string boardId, string workspacePath,
        SemaphoreSlim? semaphore, CancellationToken cancellationToken)
    {
        var cards = await boardClient.GetBoardCardsAsync(boardId, cancellationToken, workflowConfig.GetTerminalColumnNames());
        var selection = CardSelector.SelectAll(cards, workflowConfig, executorResolver.AvailableProviders);

        foreach (var skipped in selection.SkippedDueToProviders)
        {
            logger.LogWarning(
                "Card {CardId} ({Title}) in '{State}' not claimable — missing providers: {Missing}",
                skipped.Card.Id, skipped.Card.Title, skipped.StateName,
                string.Join(", ", skipped.MissingProviders));
        }

        if (selection.Eligible.Count == 0)
        {
            logger.LogDebug("No eligible cards found");
            return;
        }

        logger.LogInformation("Found {Count} eligible card(s), attempting to claim and process", selection.Eligible.Count);

        var tasks = new List<Task>();

        foreach (var card in selection.Eligible)
        {
            // Do not claim new cards when graceful shutdown has been requested.
            // Already-claimed in-flight cards (running in Task.WhenAll) are allowed to finish.
            if (shutdownCoordinator?.IsShutdownRequested == true)
            {
                logger.LogInformation(
                    "Shutdown requested — skipping claim for card {CardId} ({Title})",
                    card.Id, card.Title);
                continue;
            }

            var claimed = await cardClaim.TryClaimAsync(card.Id, agentIdentity.DisplayName, cancellationToken);
            if (!claimed)
            {
                logger.LogDebug("Card {CardId} ({Title}) claimed by another agent, skipping", card.Id, card.Title);
                continue;
            }

            tasks.Add(ProcessCardWithThrottleAsync(card, boardId, workspacePath, semaphore, cancellationToken));
        }

        if (tasks.Count > 0)
        {
            logger.LogInformation("Processing {Count} claimed card(s) concurrently", tasks.Count);
            await Task.WhenAll(tasks);
        }
    }

    private async Task ProcessCardWithThrottleAsync(
        BoardCard card, string boardId, string workspacePath,
        SemaphoreSlim? semaphore, CancellationToken cancellationToken)
    {
        if (semaphore is not null)
            await semaphore.WaitAsync(cancellationToken);

        try
        {
            await ProcessCardAsync(card, boardId, workspacePath, cancellationToken);
        }
        finally
        {
            semaphore?.Release();
            try
            {
                await cardClaim.ReleaseAsync(card.Id, agentIdentity.DisplayName, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release claim on card {CardId}", card.Id);
            }
        }
    }

    private async Task ProcessCardAsync(
        BoardCard card, string boardId, string workspacePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = workflowConfig.ResolveState(card);
            var result = string.Equals(state?.GateType, GateTypes.SystemMerge, StringComparison.OrdinalIgnoreCase)
                ? await mergeRunner.ExecuteAsync(card.Id, boardId, workspacePath, cancellationToken)
                : await agentRunner.ExecuteAsync(card.Id, boardId, workspacePath, cancellationToken);

            logger.LogInformation("Card {CardId} ({Title}) completed: {Outcome}", card.Id, card.Title, result.Outcome);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Card {CardId} ({Title}) processing failed", card.Id, card.Title);
        }
    }
}
