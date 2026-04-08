using System.Globalization;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Executes a TransitionTarget's action list against a board card.
/// Failure semantics:
///   - moveToColumn failures propagate (state machine integrity — the card must move).
///   - All other action failures are caught, logged as warnings, and execution continues.
/// Template variables in action values/fields are resolved before dispatch.
/// </summary>
public static class TransitionExecutor
{
    public static async Task ExecuteAsync(
        string cardId,
        TransitionTarget target,
        ITaskBoardClient boardClient,
        ILogger logger,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? templateContext = null,
        ICrossReferenceResolver? crossRefResolver = null,
        WorkflowConfig? workflowConfig = null)
    {
        foreach (var action in target.Actions)
        {
            var resolvedValue = ResolveTemplate(action.Value, templateContext);
            var resolvedField = ResolveTemplate(action.Field, templateContext);

            if (action.Type == ActionTypes.MoveToColumn)
            {
                // Strict — propagate exceptions (state machine integrity)
                await boardClient.MoveCardToColumnAsync(cardId, resolvedValue!, cancellationToken);
            }
            else
            {
                // Lenient — log warnings but continue
                // Only check for unresolved templates when a context was provided (templates are expected to resolve)
                if (templateContext is not null && (HasUnresolvedTemplates(resolvedValue) || HasUnresolvedTemplates(resolvedField)))
                {
                    logger.LogWarning(
                        "Transition action '{Type}' for card {CardId} has unresolved template variable " +
                        "(field='{Field}', value='{Value}') — skipping. Check that the preceding steps " +
                        "returned the expected structured output fields.",
                        action.Type, cardId, resolvedField ?? action.Field, resolvedValue ?? action.Value);
                    continue;
                }

                try
                {
                    switch (action.Type)
                    {
                        case ActionTypes.AddLabel:
                            await boardClient.AddLabelAsync(cardId, resolvedValue!, cancellationToken);
                            break;
                        case ActionTypes.RemoveLabel:
                            await boardClient.RemoveLabelAsync(cardId, resolvedValue!, cancellationToken);
                            break;
                        case ActionTypes.Assign:
                            await boardClient.AssignAsync(cardId, resolvedValue!, cancellationToken);
                            break;
                        case ActionTypes.Unassign:
                            await boardClient.UnassignAsync(cardId, resolvedValue, cancellationToken);
                            break;
                        case ActionTypes.SetField:
                            await boardClient.SetFieldAsync(cardId, resolvedField!, resolvedValue!, cancellationToken);
                            break;
                        case ActionTypes.ClearField:
                            await boardClient.ClearFieldAsync(cardId, resolvedField!, cancellationToken);
                            break;
                        case ActionTypes.UpdateParentSum:
                            await UpdateParentSumAsync(cardId, resolvedField!, boardClient, crossRefResolver, logger, cancellationToken);
                            break;
                        case ActionTypes.CompleteParentIfReady:
                            await CompleteParentIfReadyAsync(cardId, boardClient, crossRefResolver, workflowConfig, logger, cancellationToken);
                            break;
                        default:
                            logger.LogWarning(
                                "Unknown transition action type '{Type}' for card {CardId} — skipping",
                                action.Type, cardId);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Non-critical transition action '{Type}' for card {CardId} failed " +
                        "(field='{Field}', value='{Value}') — continuing",
                        action.Type, cardId, resolvedField ?? action.Field, resolvedValue ?? action.Value);
                }
            }
        }
    }

    /// <summary>
    /// Finds this card's parent, sums the specified field across all sibling children, and sets
    /// the result on the parent card. No-op if the card has no parent or no cross-ref resolver.
    /// </summary>
    private static async Task UpdateParentSumAsync(
        string cardId,
        string fieldName,
        ITaskBoardClient boardClient,
        ICrossReferenceResolver? crossRefResolver,
        ILogger logger,
        CancellationToken ct)
    {
        if (crossRefResolver is null)
        {
            logger.LogWarning("updateParentSum skipped for card {CardId}: no cross-reference resolver available", cardId);
            return;
        }

        // Find parent card
        var refs = await crossRefResolver.GetStructuredReferencesAsync(cardId, ct);
        var parentRef = refs.FirstOrDefault(r =>
            string.Equals(r.ReferenceType, "parent_item", StringComparison.OrdinalIgnoreCase));

        if (parentRef is null)
        {
            logger.LogDebug("updateParentSum: card {CardId} has no parent — skipping", cardId);
            return;
        }

        var parentId = parentRef.ReferencedCardId;

        // Get all children of the parent
        var parentRefs = await crossRefResolver.GetStructuredReferencesAsync(parentId, ct);
        var childIds = parentRefs
            .Where(r => string.Equals(r.ReferenceType, "sub_item", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ReferencedCardId)
            .ToList();

        if (childIds.Count == 0)
        {
            logger.LogDebug("updateParentSum: parent {ParentId} has no children — skipping", parentId);
            return;
        }

        // Sum the field across all children
        double sum = 0;
        int counted = 0;
        foreach (var childId in childIds)
        {
            try
            {
                var childCard = await boardClient.GetCardAsync(childId, ct);
                if (childCard.Metadata is not null
                    && childCard.Metadata.TryGetValue(fieldName, out var raw)
                    && double.TryParse(raw, CultureInfo.InvariantCulture, out var val))
                {
                    sum += val;
                    counted++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "updateParentSum: failed to fetch child {ChildId} — skipping", childId);
            }
        }

        if (counted == 0)
        {
            logger.LogDebug("updateParentSum: no children have field '{Field}' set — skipping", fieldName);
            return;
        }

        var sumStr = sum.ToString("G", CultureInfo.InvariantCulture);
        await boardClient.SetFieldAsync(parentId, fieldName, sumStr, ct);
        logger.LogInformation(
            "updateParentSum: set {Field}={Sum} on parent {ParentId} (summed from {Count}/{Total} children)",
            fieldName, sumStr, parentId, counted, childIds.Count);
    }

    /// <summary>
    /// Checks if the completed card has a parent story waiting for children.
    /// If all sibling children are in terminal states, transitions the parent to its COMPLETE target.
    /// Otherwise, posts/updates a progress comment on the parent.
    /// </summary>
    private static async Task CompleteParentIfReadyAsync(
        string cardId,
        ITaskBoardClient boardClient,
        ICrossReferenceResolver? crossRefResolver,
        WorkflowConfig? workflowConfig,
        ILogger logger,
        CancellationToken ct)
    {
        if (crossRefResolver is null || workflowConfig is null)
        {
            logger.LogDebug("completeParentIfReady skipped for card {CardId}: missing resolver or config", cardId);
            return;
        }

        // 1. Find parent card
        var refs = await crossRefResolver.GetStructuredReferencesAsync(cardId, ct);
        var parentRef = refs.FirstOrDefault(r =>
            string.Equals(r.ReferenceType, "parent_item", StringComparison.OrdinalIgnoreCase));

        if (parentRef is null)
        {
            logger.LogDebug("completeParentIfReady: card {CardId} has no parent — skipping", cardId);
            return;
        }

        var parentId = parentRef.ReferencedCardId;

        // 2. Fetch parent card and verify it's in a waiting state
        BoardCard parentCard;
        try
        {
            parentCard = await boardClient.GetCardAsync(parentId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "completeParentIfReady: failed to fetch parent {ParentId}", parentId);
            return;
        }

        // 3. Get all children of the parent
        var parentRefs = await crossRefResolver.GetStructuredReferencesAsync(parentId, ct);
        var childIds = parentRefs
            .Where(r => string.Equals(r.ReferenceType, "sub_item", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ReferencedCardId)
            .ToList();

        if (childIds.Count == 0)
        {
            logger.LogDebug("completeParentIfReady: parent {ParentId} has no children — skipping", parentId);
            return;
        }

        // 4. Check terminal states
        var terminalStates = workflowConfig.GetTerminalStateNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var completed = new List<string>();
        var pending = new List<string>();

        foreach (var childId in childIds)
        {
            try
            {
                var childCard = await boardClient.GetCardAsync(childId, ct);
                if (terminalStates.Contains(childCard.ColumnId))
                    completed.Add(childId);
                else
                    pending.Add(childId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "completeParentIfReady: failed to fetch child {ChildId} — treating as pending", childId);
                pending.Add(childId);
            }
        }

        var marker = "<!-- aiboard:children-progress -->";

        if (pending.Count == 0)
        {
            // All children complete — transition parent
            logger.LogInformation(
                "completeParentIfReady: all {Count} children complete for parent {ParentId} — transitioning",
                completed.Count, parentId);

            // Look up parent's current state and execute its COMPLETE transition
            if (workflowConfig.States.TryGetValue(parentCard.ColumnId, out var parentState)
                && parentState.Transitions.TryGetValue(TransitionKeys.Complete, out var completeTarget))
            {
                var comment = $"All {completed.Count} child tasks complete. Transitioning to Done.";
                await boardClient.UpsertAgentCommentAsync(parentId, comment, marker, ct);

                // Execute the parent's COMPLETE transition (recursive call — but no infinite loop
                // because the parent transitions to a terminal state which has no completeParentIfReady)
                await ExecuteAsync(parentId, completeTarget, boardClient, logger, ct,
                    crossRefResolver: crossRefResolver, workflowConfig: workflowConfig);
            }
            else
            {
                logger.LogWarning(
                    "completeParentIfReady: parent {ParentId} in state '{State}' has no COMPLETE transition",
                    parentId, parentCard.ColumnId);
            }
        }
        else
        {
            // Post/update progress comment
            var comment = $"Child task #{cardId} complete. Progress: {completed.Count}/{childIds.Count} tasks done. " +
                          $"Waiting for: {string.Join(", ", pending.Select(id => $"#{id}"))}";
            await boardClient.UpsertAgentCommentAsync(parentId, comment, marker, ct);

            logger.LogInformation(
                "completeParentIfReady: {Completed}/{Total} children complete for parent {ParentId}",
                completed.Count, childIds.Count, parentId);
        }
    }

    private static string? ResolveTemplate(string? value, IReadOnlyDictionary<string, string>? context)
    {
        if (value is null || context is null)
            return value;

        foreach (var (key, replacement) in context)
            value = value.Replace($"{{{{{key}}}}}", replacement);

        return value;
    }

    private static bool HasUnresolvedTemplates(string? value) =>
        value is not null && value.Contains("{{") && value.Contains("}}");
}
