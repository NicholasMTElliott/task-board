using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Evaluates card filter predicates defined in WorkflowState.Filters.
/// All filters are AND-combined: a card must pass every filter to be eligible.
/// Filters are only evaluated in polling mode (CardSelector.SelectNext).
/// In direct agent mode (--mode agent --card-id N), filters are not applied.
/// </summary>
public static class CardFilterEvaluator
{
    /// <summary>
    /// Returns true if the card passes all filters, or if filters is null/empty.
    /// </summary>
    public static bool PassesAll(BoardCard card, IReadOnlyList<CardFilter>? filters)
    {
        if (filters is null or { Count: 0 })
            return true;

        return filters.All(f => Evaluate(card, f));
    }

    /// <summary>Evaluates a single filter predicate against the card.</summary>
    public static bool Evaluate(BoardCard card, CardFilter filter) => filter.Type switch
    {
        FilterTypes.Label    => EvaluateLabel(card, filter),
        FilterTypes.Assignee => EvaluateAssignee(card, filter),
        FilterTypes.Field    => EvaluateField(card, filter),
        _ => throw new InvalidOperationException($"Unknown filter type: '{filter.Type}'")
    };

    private static bool EvaluateLabel(BoardCard card, CardFilter filter)
    {
        var labels = card.Labels ?? [];
        var hasLabel = labels.Any(l =>
            string.Equals(l, filter.Value, StringComparison.OrdinalIgnoreCase));

        return filter.Operator switch
        {
            FilterOperators.Exists    => hasLabel,
            FilterOperators.NotExists => !hasLabel,
            _ => throw new InvalidOperationException(
                $"Invalid operator '{filter.Operator}' for label filter. Valid operators: exists, notExists")
        };
    }

    private static bool EvaluateAssignee(BoardCard card, CardFilter filter)
    {
        var assignees = card.Assignees ?? [];

        return filter.Operator switch
        {
            FilterOperators.IsEmpty    => assignees.Count == 0,
            FilterOperators.IsNotEmpty => assignees.Count > 0,
            FilterOperators.Equals     => assignees.Any(a =>
                string.Equals(a, filter.Value, StringComparison.OrdinalIgnoreCase)),
            FilterOperators.NotEquals  => !assignees.Any(a =>
                string.Equals(a, filter.Value, StringComparison.OrdinalIgnoreCase)),
            _ => throw new InvalidOperationException(
                $"Invalid operator '{filter.Operator}' for assignee filter. Valid operators: isEmpty, isNotEmpty, equals, notEquals")
        };
    }

    private static bool EvaluateField(BoardCard card, CardFilter filter)
    {
        string? value = null;
        if (card.Metadata is not null && filter.Field is not null)
        {
            foreach (var kvp in card.Metadata)
            {
                if (string.Equals(kvp.Key, filter.Field, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    break;
                }
            }
        }

        return filter.Operator switch
        {
            FilterOperators.Equals     => string.Equals(value, filter.Value, StringComparison.OrdinalIgnoreCase),
            FilterOperators.NotEquals  => !string.Equals(value, filter.Value, StringComparison.OrdinalIgnoreCase),
            FilterOperators.IsEmpty    => string.IsNullOrEmpty(value),
            FilterOperators.IsNotEmpty => !string.IsNullOrEmpty(value),
            _ => throw new InvalidOperationException(
                $"Invalid operator '{filter.Operator}' for field filter. Valid operators: equals, notEquals, isEmpty, isNotEmpty")
        };
    }
}
