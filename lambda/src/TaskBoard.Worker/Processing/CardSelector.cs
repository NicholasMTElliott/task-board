using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed record CardSelectionResult(
    BoardCard? Selected,
    IReadOnlyList<SkippedCard> SkippedDueToProviders);

public sealed record SkippedCard(
    BoardCard Card,
    string StateName,
    IReadOnlyList<string> MissingProviders);

public sealed record MultiCardSelectionResult(
    IReadOnlyList<BoardCard> Eligible,
    IReadOnlyList<SkippedCard> SkippedDueToProviders);

public static class CardSelector
{
    public static CardSelectionResult SelectNext(
        IReadOnlyList<BoardCard> cards,
        WorkflowConfig workflowConfig,
        IReadOnlySet<string>? availableProviders = null)
    {
        var eligible = new List<(BoardCard Card, WorkflowState State, int Position)>();
        var skippedCards = new List<SkippedCard>();

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (!workflowConfig.States.TryGetValue(card.ColumnId, out var state))
                continue;
            if (state.GateType is not (GateTypes.AgentRun or GateTypes.SystemMerge))
                continue;

            // Apply state-level filters (AND-combined)
            if (!CardFilterEvaluator.PassesAll(card, state.Filters))
                continue;

            // Provider eligibility check
            if (availableProviders is not null)
            {
                var required = workflowConfig.GetRequiredProviders(state);
                var missing = required.Except(availableProviders, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                {
                    skippedCards.Add(new SkippedCard(card, state.Name, missing));
                    continue;
                }
            }

            eligible.Add((card, state, i));
        }

        if (eligible.Count == 0)
            return new CardSelectionResult(null, skippedCards);

        var pollingConfig = workflowConfig.Polling;

        eligible.Sort((a, b) =>
        {
            // 1. Pipeline order descending (later stage first)
            var orderCmp = b.State.PipelineOrder.CompareTo(a.State.PipelineOrder);
            if (orderCmp != 0) return orderCmp;

            // 2. Priority rank ascending (lower rank = higher priority)
            var priorityCmp = GetPriorityRank(a.Card, pollingConfig)
                .CompareTo(GetPriorityRank(b.Card, pollingConfig));
            if (priorityCmp != 0) return priorityCmp;

            // 3. Board position ascending (higher on board first)
            var posCmp = a.Position.CompareTo(b.Position);
            if (posCmp != 0) return posCmp;

            // 4. Issue number ascending (earliest created first)
            var aNum = int.TryParse(a.Card.Id, out var an) ? an : int.MaxValue;
            var bNum = int.TryParse(b.Card.Id, out var bn) ? bn : int.MaxValue;
            return aNum.CompareTo(bNum);
        });

        return new CardSelectionResult(eligible[0].Card, skippedCards);
    }

    /// <summary>
    /// Returns ALL eligible cards sorted by priority (same logic as <see cref="SelectNext"/>).
    /// Used by <see cref="QueueDrivenRunner"/> to process multiple cards concurrently.
    /// </summary>
    public static MultiCardSelectionResult SelectAll(
        IReadOnlyList<BoardCard> cards,
        WorkflowConfig workflowConfig,
        IReadOnlySet<string>? availableProviders = null)
    {
        var result = SelectNext(cards, workflowConfig, availableProviders);

        // SelectNext already filters, sorts, and returns the first card.
        // To get ALL eligible cards, we re-run the same filter logic.
        // This is a small duplication but avoids restructuring SelectNext.
        var eligible = new List<BoardCard>();
        var skippedCards = new List<SkippedCard>();

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (!workflowConfig.States.TryGetValue(card.ColumnId, out var state))
                continue;
            if (state.GateType is not (GateTypes.AgentRun or GateTypes.SystemMerge))
                continue;
            if (!CardFilterEvaluator.PassesAll(card, state.Filters))
                continue;
            if (availableProviders is not null)
            {
                var required = workflowConfig.GetRequiredProviders(state);
                var missing = required.Except(availableProviders, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                {
                    skippedCards.Add(new SkippedCard(card, state.Name, missing));
                    continue;
                }
            }

            eligible.Add(card);
        }

        // Sort using same criteria as SelectNext (priority already embedded in card order from board)
        return new MultiCardSelectionResult(eligible, skippedCards);
    }

    private static int GetPriorityRank(BoardCard card, PollingConfig? pollingConfig)
    {
        if (pollingConfig?.PriorityFieldName is null || pollingConfig.PriorityOrder is null)
            return int.MaxValue;

        var fieldName = pollingConfig.PriorityFieldName.ToLowerInvariant();

        string? value = null;
        if (card.Metadata is not null)
        {
            // gh CLI lowercases field names; do case-insensitive lookup
            foreach (var kvp in card.Metadata)
            {
                if (string.Equals(kvp.Key, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    break;
                }
            }
        }

        if (value is null)
            return int.MaxValue;

        var index = pollingConfig.PriorityOrder.FindIndex(
            v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

        return index < 0 ? int.MaxValue : index;
    }
}
