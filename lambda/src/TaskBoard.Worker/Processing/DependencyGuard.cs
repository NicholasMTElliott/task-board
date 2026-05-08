using System.Text;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed record DependencyGuardResult(
    bool IsBlocked,
    IReadOnlyList<CardDependency> UnresolvedBlockers)
{
    public static readonly DependencyGuardResult NotBlocked = new(false, []);
}

/// <summary>
/// Centralised pre-flight check that blocks cards from progressing through
/// gated states when their declared dependencies haven't been satisfied.
///
/// <para>
/// <c>DependencyPolicy.EnforcedStates</c> entries match against
/// <see cref="WorkflowState.Name"/> first and the card's column name as a
/// fallback — so an operator can write either the workflow state name
/// (e.g. <c>"Ready for Implementation"</c>) or, for a 1:1 state-to-column
/// mapping, the column name. Column matches are coarser: in shared-column
/// workflows where multiple states live on one column, listing the column
/// enforces on every state on that column.
/// </para>
///
/// <para>
/// <c>DependencyPolicy.SatisfiedColumns</c> matches against the blocker's
/// column on the project board. A blocker is also treated as satisfied when
/// the upstream provider reports it as closed with no <c>state_reason</c>
/// or <c>state_reason = "completed"</c> — so closed-as-not-planned issues
/// remain blocking.
/// </para>
///
/// <para>
/// Transient lookup failures (auth, 5xx, network) are caught and treated as
/// "not blocked" so the pipeline keeps moving. Contract violations
/// (<see cref="DependencyApiContractException"/>) are intentionally NOT
/// caught — they signal the upstream API shape changed and surface as an
/// error so the operator notices.
/// </para>
/// </summary>
public sealed class DependencyGuard(
    ITaskBoardClient boardClient,
    ICardDependencyClient dependencyClient,
    WorkflowConfig workflowConfig,
    IDependencyWaitStore waitStore,
    ILogger<DependencyGuard> logger,
    // Rerun redesign Problem 2: optional router. When wired, the blocked-comment
    // is posted as kind:dependency_blocked → delete_and_repost so the latest
    // status is always at the bottom of the chronological log instead of edited
    // in place. Null falls back to legacy upsert with the BlockedCommentMarker.
    ICommentRouter? commentRouter = null)
{
    private const string BlockedCommentMarker = "<!-- agent-dependency-blocked -->";

    public Task<DependencyGuardResult> CheckAsync(
        BoardCard card,
        WorkflowState state,
        string source,
        CancellationToken ct) =>
        CheckAsync(card, state, source, ct, boardCardCache: null, recordSideEffects: true);

    /// <summary>
    /// Same as <see cref="CheckAsync(BoardCard, WorkflowState, string, CancellationToken)"/>
    /// but lets a caller (typically the polling loop) supply a per-cycle cache
    /// of board cards. Two enforced cards on the same poll cycle that share a
    /// blocker only fetch the blocker's card body once.
    /// </summary>
    public Task<DependencyGuardResult> CheckAsync(
        BoardCard card,
        WorkflowState state,
        string source,
        CancellationToken ct,
        Dictionary<string, BoardCard>? boardCardCache) =>
        CheckAsync(card, state, source, ct, boardCardCache, recordSideEffects: true);

    /// <summary>
    /// Full-control variant. Set <paramref name="recordSideEffects"/> to
    /// <c>false</c> for read-only diagnostics (e.g. <c>--mode diagnose</c>):
    /// the result is returned without writing to <c>card_dependency_wait</c>,
    /// posting the blocked-comment, or logging the blocking decision.
    /// </summary>
    public async Task<DependencyGuardResult> CheckAsync(
        BoardCard card,
        WorkflowState state,
        string source,
        CancellationToken ct,
        Dictionary<string, BoardCard>? boardCardCache,
        bool recordSideEffects)
    {
        var policy = workflowConfig.DependencyPolicy;
        if (policy?.Enabled != true || !IsEnforced(card, state, policy))
            return DependencyGuardResult.NotBlocked;

        IReadOnlyList<CardDependency> blockers;
        try
        {
            blockers = await dependencyClient.GetBlockersAsync(card.Id, ct);
        }
        catch (Exception ex) when (ex is not DependencyApiContractException
                                    and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dependency lookup failed for card {CardId}; treating as not blocked", card.Id);
            return DependencyGuardResult.NotBlocked;
        }

        if (blockers.Count == 0)
            return DependencyGuardResult.NotBlocked;

        var satisfiedColumns = GetSatisfiedColumns(policy);
        var unresolved = new List<CardDependency>();
        foreach (var blocker in blockers)
        {
            // Closed-and-completed blockers don't need a column lookup —
            // they're satisfied on state alone, so skip the hydration call.
            if (IsClosedSatisfied(blocker))
                continue;

            var hydrated = await HydrateBlockerAsync(blocker, boardCardCache, ct);
            if (IsSatisfied(hydrated, satisfiedColumns))
                continue;
            unresolved.Add(hydrated);
        }

        if (recordSideEffects)
            await waitStore.RecordBlockedAsync(card.Id, unresolved, source, ct);

        if (unresolved.Count == 0)
            return DependencyGuardResult.NotBlocked;

        if (recordSideEffects && policy.CommentOnBlocked)
        {
            var blockedBody = BuildBlockedComment(unresolved);
            if (commentRouter is not null)
            {
                var marker = AiboardLogMarker.Build(
                    AiboardLogMarker.KindDependencyBlocked,
                    new[] { KeyValuePair.Create("card", card.Id) });
                await commentRouter.PostAsync(
                    card.Id, AiboardLogMarker.KindDependencyBlocked,
                    blockedBody, marker, ct);
            }
            else
            {
                await boardClient.UpsertAgentCommentAsync(
                    card.Id, blockedBody, BlockedCommentMarker, ct);
            }
        }

        if (recordSideEffects)
        {
            logger.LogInformation(
                "Card {CardId} is blocked by {Count} unresolved dependency/dependencies",
                card.Id, unresolved.Count);
        }
        return new DependencyGuardResult(true, unresolved);
    }

    private static bool IsEnforced(BoardCard card, WorkflowState state, DependencyPolicy policy)
    {
        // Empty / null EnforcedStates → policy doesn't enforce on anything.
        // Operators must list the states or columns they want gated; there is
        // no implicit fallback (the previous in-code default duplicated the
        // workflow JSON and was the wrong place to keep it).
        if (policy.EnforcedStates is not { Count: > 0 } enforced)
            return false;

        // Match either the resolved workflow state name OR the card's column
        // name. State names are the precise unit; column names work for 1:1
        // mappings and are coarser in shared-column workflows.
        return enforced.Any(s =>
            string.Equals(s, state.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, card.ColumnId, StringComparison.OrdinalIgnoreCase));
    }

    private HashSet<string> GetSatisfiedColumns(DependencyPolicy policy)
    {
        var configured = policy.SatisfiedColumns is { Count: > 0 }
            ? policy.SatisfiedColumns
            : workflowConfig.GetTerminalColumnNames();
        return configured.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<CardDependency> HydrateBlockerAsync(
        CardDependency blocker,
        Dictionary<string, BoardCard>? cache,
        CancellationToken ct)
    {
        if (cache is not null && cache.TryGetValue(blocker.CardId, out var cachedCard))
            return Merge(blocker, cachedCard);

        try
        {
            var fetched = await boardClient.GetCardAsync(blocker.CardId, ct);
            cache?.TryAdd(blocker.CardId, fetched);
            return Merge(blocker, fetched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not hydrate blocker card {CardId}; using dependency payload", blocker.CardId);
            return blocker;
        }
    }

    private static CardDependency Merge(CardDependency blocker, BoardCard card) =>
        blocker with
        {
            Title = string.IsNullOrWhiteSpace(blocker.Title) ? card.Title : blocker.Title,
            ColumnId = string.IsNullOrWhiteSpace(card.ColumnId) ? blocker.ColumnId : card.ColumnId,
        };

    private static bool IsClosedSatisfied(CardDependency blocker) =>
        blocker.IsClosed == true
        && (blocker.StateReason is null
            || string.Equals(blocker.StateReason, "completed", StringComparison.OrdinalIgnoreCase));

    private static bool IsSatisfied(CardDependency blocker, HashSet<string> satisfiedColumns) =>
        (blocker.ColumnId is not null && satisfiedColumns.Contains(blocker.ColumnId))
        || IsClosedSatisfied(blocker);

    private static string BuildBlockedComment(IReadOnlyList<CardDependency> unresolved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Blocked by unresolved dependencies:");
        sb.AppendLine();
        foreach (var blocker in unresolved)
        {
            var title = string.IsNullOrWhiteSpace(blocker.Title) ? "" : $" — {blocker.Title}";
            var column = string.IsNullOrWhiteSpace(blocker.ColumnId) ? "" : $" ({blocker.ColumnId})";
            sb.AppendLine($"- #{blocker.CardId}{title}{column}");
        }
        return sb.ToString();
    }
}
