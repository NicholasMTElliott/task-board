using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public static class CardSelector
{
    public static BoardCard? SelectNext(
        IReadOnlyList<BoardCard> cards,
        WorkflowConfig workflowConfig)
    {
        var eligible = new List<(BoardCard Card, WorkflowState State, int Position)>();

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (!workflowConfig.States.TryGetValue(card.ColumnId, out var state))
                continue;
            if (state.GateType is not ("agent_run" or "system_merge"))
                continue;

            // Apply state-level filters (AND-combined)
            if (!CardFilterEvaluator.PassesAll(card, state.Filters))
                continue;

            eligible.Add((card, state, i));
        }

        if (eligible.Count == 0)
            return null;

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

        return eligible[0].Card;
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
