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
        IReadOnlyDictionary<string, string>? templateContext = null)
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
                        "Non-critical transition action '{Type}' failed for card {CardId} — continuing",
                        action.Type, cardId);
                }
            }
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
}
