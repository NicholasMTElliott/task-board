namespace TaskBoard.Worker.Processing;

/// <summary>
/// Distributed card locking via the <c>card_state</c> table in Postgres.
/// Ensures only one agent processes a given card at a time.
/// </summary>
public interface ICardClaimService
{
    /// <summary>
    /// Atomically claim a card for processing. Returns true if the claim succeeded.
    /// Reclaims stale locks (older than the configured threshold) in the same operation.
    /// </summary>
    Task<bool> TryClaimAsync(string cardId, string agentId, CancellationToken cancellationToken);

    /// <summary>Release a previously claimed card.</summary>
    Task ReleaseAsync(string cardId, string agentId, CancellationToken cancellationToken);
}
