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

public sealed class DependencyGuard(
    ITaskBoardClient boardClient,
    ICardDependencyClient dependencyClient,
    WorkflowConfig workflowConfig,
    IDependencyWaitStore waitStore,
    ILogger<DependencyGuard> logger)
{
    private static readonly string[] DefaultEnforcedStates =
    [
        "Ready for Implementation",
        "Ready for Test",
        "Approved"
    ];

    public async Task<DependencyGuardResult> CheckAsync(
        BoardCard card,
        WorkflowState state,
        string source,
        CancellationToken ct)
    {
        var policy = workflowConfig.DependencyPolicy;
        if (policy?.Enabled != true || !IsEnforced(card, state, policy))
            return DependencyGuardResult.NotBlocked;

        IReadOnlyList<CardDependency> blockers;
        try
        {
            blockers = await dependencyClient.GetBlockersAsync(card.Id, ct);
        }
        catch (Exception ex)
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
            var hydrated = await HydrateBlockerAsync(blocker, ct);
            if (IsSatisfied(hydrated, satisfiedColumns))
                continue;
            unresolved.Add(hydrated);
        }

        await waitStore.RecordBlockedAsync(card.Id, unresolved, source, ct);

        if (unresolved.Count == 0)
            return DependencyGuardResult.NotBlocked;

        if (policy.CommentOnBlocked)
        {
            var marker = "<!-- aiboard:dependency-blocked -->";
            await boardClient.UpsertAgentCommentAsync(card.Id, BuildBlockedComment(unresolved), marker, ct);
        }

        logger.LogInformation(
            "Card {CardId} is blocked by {Count} unresolved dependency/dependencies",
            card.Id, unresolved.Count);
        return new DependencyGuardResult(true, unresolved);
    }

    private bool IsEnforced(BoardCard card, WorkflowState state, DependencyPolicy policy)
    {
        IReadOnlyList<string> enforced = policy.EnforcedStates is { Count: > 0 }
            ? policy.EnforcedStates
            : DefaultEnforcedStates;

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

    private async Task<CardDependency> HydrateBlockerAsync(CardDependency blocker, CancellationToken ct)
    {
        try
        {
            var card = await boardClient.GetCardAsync(blocker.CardId, ct);
            return blocker with
            {
                Title = string.IsNullOrWhiteSpace(blocker.Title) ? card.Title : blocker.Title,
                ColumnId = string.IsNullOrWhiteSpace(card.ColumnId) ? blocker.ColumnId : card.ColumnId,
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not hydrate blocker card {CardId}; using dependency payload", blocker.CardId);
            return blocker;
        }
    }

    private static bool IsSatisfied(CardDependency blocker, HashSet<string> satisfiedColumns) =>
        (blocker.ColumnId is not null && satisfiedColumns.Contains(blocker.ColumnId))
        || (blocker.IsClosed == true
            && (blocker.StateReason is null
                || string.Equals(blocker.StateReason, "completed", StringComparison.OrdinalIgnoreCase)));

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
