using System.Text;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Handles cards in the <c>children_complete</c> gate type.
///
/// Queries child cards via ICrossReferenceResolver (sub_item references),
/// checks each against the workflow's terminal states, and transitions the
/// parent card when all children have reached a terminal state.
///
/// If no children are found, returns ERROR (per operator decision).
/// If children are still pending, no transition occurs — the card stays for re-check next poll cycle.
/// </summary>
public sealed class CompletionRunner(
    ITaskBoardClient boardClient,
    ICrossReferenceResolver crossReferenceResolver,
    WorkflowConfigProvider workflowConfigProvider,
    AgentIdentity agentIdentity,
    ILogger<CompletionRunner> logger)
{
    // Mutable snapshot: refreshed at phase entry.
    private WorkflowConfig workflowConfig = workflowConfigProvider.Current;

    public async Task<AgentRunResult> ExecuteAsync(
        string cardId, string boardId, string workspacePath, CancellationToken ct)
    {
        workflowConfig = workflowConfigProvider.Current;

        var runId = $"completion-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
        var runMarker = $"<!-- completion-check:{runId} -->";

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["AgentName"] = agentIdentity.DisplayName,
            ["RunId"] = runId
        }))
        {

        logger.LogInformation("Starting completion check {RunId} for card {CardId}", runId, cardId);

        // 1. Fetch card and validate it's in a children_complete state
        BoardCard card;
        try
        {
            card = await boardClient.GetCardAsync(cardId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch card {CardId}", cardId);
            return new AgentRunResult(AgentOutcome.ERROR, $"Failed to fetch card: {ex.Message}");
        }

        var state = workflowConfig.ResolveState(card);
        if (state is null)
            return new AgentRunResult(AgentOutcome.ERROR, $"Card column '{card.ColumnId}' not in workflow config");

        if (!string.Equals(state.GateType, GateTypes.ChildrenComplete, StringComparison.OrdinalIgnoreCase))
            return new AgentRunResult(AgentOutcome.ERROR, $"State '{state.Name}' is not a children_complete state");

        // 2. Query children via cross-reference resolver (sub_item references = tracked issues)
        IReadOnlyList<CardReference> allRefs;
        try
        {
            allRefs = await crossReferenceResolver.GetStructuredReferencesAsync(cardId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve child references for card {CardId}", cardId);
            allRefs = [];
        }

        var childRefs = allRefs.Where(r =>
            string.Equals(r.ReferenceType, "sub_item", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 3. No children → ERROR (per operator decision)
        if (childRefs.Count == 0)
        {
            logger.LogWarning("Card {CardId} has no tracked children in children_complete state", cardId);

            var noChildrenComment = BuildComment([], [], state);
            await boardClient.UpsertAgentCommentAsync(cardId, noChildrenComment, runMarker, ct);

            if (state.Transitions.TryGetValue(TransitionKeys.Error, out var errorTarget))
            {
                var templateContext = await BuildTemplateContextAsync(ct);
                await TransitionExecutor.ExecuteAsync(cardId, errorTarget, boardClient, logger, ct, templateContext, crossReferenceResolver, workflowConfig);
            }

            return new AgentRunResult(AgentOutcome.ERROR,
                "No tracked children found. Card is in a children_complete state but has no child issues linked via task list.");
        }

        // 4. Fetch each child and check terminal state (compare against board column names)
        var terminalStates = workflowConfig.GetTerminalColumnNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var completed = new List<(string Id, string Title, string Column)>();
        var pending = new List<(string Id, string Title, string Column)>();

        foreach (var childRef in childRefs)
        {
            try
            {
                var childCard = await boardClient.GetCardAsync(childRef.ReferencedCardId, ct);
                if (terminalStates.Contains(childCard.ColumnId))
                    completed.Add((childRef.ReferencedCardId, childCard.Title, childCard.ColumnId));
                else
                    pending.Add((childRef.ReferencedCardId, childCard.Title, childCard.ColumnId));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch child card {CardId} — treating as pending", childRef.ReferencedCardId);
                pending.Add((childRef.ReferencedCardId,
                    childRef.Title ?? $"#{childRef.ReferencedCardId}", "unknown"));
            }
        }

        // 5. Post status comment
        var statusComment = BuildComment(completed, pending, state);
        await boardClient.UpsertAgentCommentAsync(cardId, statusComment, runMarker, ct);

        // 6. Evaluate and transition
        if (pending.Count == 0)
        {
            logger.LogInformation("All {Count} children complete for card {CardId} — transitioning",
                completed.Count, cardId);

            if (state.Transitions.TryGetValue(TransitionKeys.Complete, out var completeTarget))
            {
                var templateContext = await BuildTemplateContextAsync(ct);
                await TransitionExecutor.ExecuteAsync(cardId, completeTarget, boardClient, logger, ct, templateContext, crossReferenceResolver, workflowConfig);
            }

            return new AgentRunResult(AgentOutcome.COMPLETE,
                $"All {completed.Count} child tasks complete.");
        }

        logger.LogInformation(
            "Card {CardId}: {Completed}/{Total} children complete, {Pending} pending",
            cardId, completed.Count, childRefs.Count, pending.Count);

        // Card stays where it is — no transition. Will be re-checked next poll cycle.
        return new AgentRunResult(AgentOutcome.NEEDS_INFO,
            $"{completed.Count}/{childRefs.Count} children complete. " +
            $"Waiting for: {string.Join(", ", pending.Select(c => $"#{c.Id}"))}");

        } // end using logger scope
    }

    private async Task<Dictionary<string, string>> BuildTemplateContextAsync(CancellationToken ct)
    {
        var context = new Dictionary<string, string>();
        try
        {
            var user = await boardClient.GetCurrentUserAsync(ct);
            context["agent"] = user;
        }
        catch { /* best effort */ }
        return context;
    }

    private string BuildComment(
        List<(string Id, string Title, string Column)> completed,
        List<(string Id, string Title, string Column)> pending,
        WorkflowState state)
    {
        var total = completed.Count + pending.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"**{agentIdentity.DisplayName}** in {state.Name}:");
        sb.AppendLine();

        if (total == 0)
        {
            sb.AppendLine("No tracked children found.");
            return sb.ToString();
        }

        sb.AppendLine($"**Children status: {completed.Count}/{total} complete**");
        sb.AppendLine();

        if (completed.Count > 0)
        {
            sb.AppendLine("Completed:");
            foreach (var c in completed)
                sb.AppendLine($"- [x] #{c.Id} — {c.Title} ({c.Column})");
        }

        if (pending.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pending:");
            foreach (var p in pending)
                sb.AppendLine($"- [ ] #{p.Id} — {p.Title} ({p.Column})");
        }

        return sb.ToString();
    }
}
