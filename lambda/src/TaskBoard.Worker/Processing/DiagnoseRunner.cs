using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// "Why isn't this card being picked up?" — a read-only diagnostic that fetches
/// the card, classifies its column, walks state filters, and explains the
/// first mismatch in plain English.
///
/// <para>
/// Targets the most-asked operator question on the simplified shared-column example
/// workflow: card sitting in <c>Ready</c> with someone still assigned to it,
/// or with the wrong <c>Activity</c> field value, or in a holding column the
/// operator forgot about.
/// </para>
///
/// <para>
/// Returns 0 when the diagnosis succeeds (regardless of whether the card is
/// pickup-eligible). Returns 1 only on hard errors (card not found, board
/// fetch failure).
/// </para>
/// </summary>
public sealed class DiagnoseRunner
{
    private readonly ITaskBoardClient board;
    private readonly WorkflowConfig workflow;
    private readonly ILogger logger;
    private readonly TextWriter stdout;
    private readonly DependencyGuard? dependencyGuard;

    public DiagnoseRunner(
        ITaskBoardClient board,
        WorkflowConfig workflow,
        ILogger logger,
        TextWriter? stdout = null,
        DependencyGuard? dependencyGuard = null)
    {
        this.board = board;
        this.workflow = workflow;
        this.logger = logger;
        this.stdout = stdout ?? Console.Out;
        this.dependencyGuard = dependencyGuard;
    }

    public async Task<int> RunAsync(string cardId, CancellationToken cancellationToken)
    {
        BoardCard card;
        try
        {
            card = await board.GetCardAsync(cardId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch card {CardId}.", cardId);
            return 1;
        }

        if (card is null)
        {
            logger.LogError("Card {CardId} not found.", cardId);
            return 1;
        }

        WriteHeader(card);

        // 1. Find every state whose effective column matches the card's column.
        var statesInColumn = FindStatesAndIdsByColumn(card.ColumnId);

        if (statesInColumn.Count == 0)
        {
            WriteUnknownColumn(card);
            return 0;
        }

        // 2. Try to fully resolve the state (column + filters all pass).
        WorkflowState? resolved;
        try
        {
            resolved = workflow.ResolveState(card);
        }
        catch (InvalidOperationException ex)
        {
            stdout.WriteLine("Pickup result: AMBIGUOUS");
            stdout.WriteLine();
            stdout.WriteLine($"  {ex.Message}");
            stdout.WriteLine();
            stdout.WriteLine("Action:");
            stdout.WriteLine("  Add or tighten filters in workflow.json so exactly one state matches this card.");
            return 0;
        }

        if (resolved is not null)
        {
            // Dependency policy gates only actionable states, and only when
            // the operator opted in. Surface "blocked by dependencies" before
            // the generic ELIGIBLE verdict so a blocked-but-otherwise-clean
            // card doesn't read as "looks fine, why isn't polling picking it
            // up?". `recordSideEffects: false` keeps the check read-only —
            // diagnose must not write to card_dependency_wait or post the
            // blocked-comment.
            if (dependencyGuard is not null && IsActionable(resolved.GateType))
            {
                var dep = await dependencyGuard.CheckAsync(
                    card, resolved, "diagnose", cancellationToken,
                    boardCardCache: null, recordSideEffects: false);
                if (dep.IsBlocked)
                {
                    WriteDependencyBlocked(resolved, dep.UnresolvedBlockers);
                    return 0;
                }
            }
            WriteResolvedState(resolved, card.Id);
            return 0;
        }

        // 3. Column has states but no filters pass — walk per state.
        WriteFilterMismatch(card, statesInColumn);
        return 0;
    }

    /// <summary>
    /// Same as <see cref="WorkflowConfig.FindStatesByColumn"/> but returns
    /// the dictionary key alongside the state, since diagnose output names
    /// states by their workflow-config key (their "id").
    /// </summary>
    private List<(string Id, WorkflowState State)> FindStatesAndIdsByColumn(string columnName)
    {
        var result = new List<(string, WorkflowState)>();
        foreach (var (stateId, state) in workflow.States)
        {
            if (string.Equals(workflow.GetEffectiveColumn(stateId), columnName, StringComparison.OrdinalIgnoreCase))
                result.Add((stateId, state));
        }
        return result;
    }

    private void WriteHeader(BoardCard card)
    {
        stdout.WriteLine($"Card {card.Id}: {Truncate(card.Title, 80)}");
        stdout.WriteLine($"  Column:    {card.ColumnId}");
        if (card.Assignees is { Count: > 0 })
            stdout.WriteLine($"  Assignees: {string.Join(", ", card.Assignees.Select(a => "@" + a))}");
        else
            stdout.WriteLine("  Assignees: (none)");
        if (card.Labels is { Count: > 0 })
            stdout.WriteLine($"  Labels:    {string.Join(", ", card.Labels)}");
        if (card.Metadata is { Count: > 0 })
        {
            var fields = string.Join(", ",
                card.Metadata.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                              .Select(kv => $"{kv.Key}={kv.Value}"));
            if (!string.IsNullOrEmpty(fields))
                stdout.WriteLine($"  Fields:    {fields}");
        }
        stdout.WriteLine();
    }

    private void WriteUnknownColumn(BoardCard card)
    {
        stdout.WriteLine("Pickup result: NOT IN WORKFLOW");
        stdout.WriteLine();
        stdout.WriteLine($"  No workflow state runs from column '{card.ColumnId}'.");
        stdout.WriteLine();

        // List columns that DO have actionable states.
        var actionableColumns = workflow.States
            .Where(kv => IsActionable(kv.Value.GateType))
            .Select(kv => workflow.GetEffectiveColumn(kv.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (actionableColumns.Count > 0)
        {
            stdout.WriteLine("  To trigger work, move the card to one of these columns:");
            foreach (var col in actionableColumns)
            {
                var states = workflow.FindStatesByColumn(col);
                var hasFilters = states.Any(s => s.Filters is { Count: > 0 });
                var note = hasFilters
                    ? $"  (set fields/assignee per the matching state's filters; {states.Count} state(s) share this column)"
                    : "";
                stdout.WriteLine($"    - {col}{note}");
            }
        }
    }

    private void WriteDependencyBlocked(
        WorkflowState state, IReadOnlyList<CardDependency> unresolved)
    {
        var gateType = state.GateType ?? "(none)";
        stdout.WriteLine($"Resolved state: {state.Name} (gateType={gateType})");
        stdout.WriteLine();
        stdout.WriteLine("Pickup result: BLOCKED BY DEPENDENCIES");
        stdout.WriteLine();
        stdout.WriteLine($"  This card matches an actionable state, but {unresolved.Count} dependency/dependencies");
        stdout.WriteLine("  are unresolved. Polling skips blocked cards; direct agent and merge runs refuse them.");
        stdout.WriteLine();
        stdout.WriteLine("  Unresolved blockers:");
        foreach (var b in unresolved)
        {
            var title = string.IsNullOrWhiteSpace(b.Title) ? "" : $" — {b.Title}";
            var column = string.IsNullOrWhiteSpace(b.ColumnId) ? "" : $" ({b.ColumnId})";
            stdout.WriteLine($"    - #{b.CardId}{title}{column}");
        }
        stdout.WriteLine();
        stdout.WriteLine("  Action:");
        stdout.WriteLine("    - Drive each blocker to a satisfied column (`dependencyPolicy.satisfiedColumns`),");
        stdout.WriteLine("      OR close it as completed on the upstream provider.");
        stdout.WriteLine("    - Or, if the dependency was declared in error, remove the link via:");
        stdout.WriteLine("      gh api -X DELETE repos/<owner>/<repo>/issues/<this>/dependencies/blocked_by/<blocker-id>");
    }

    private void WriteResolvedState(WorkflowState state, string cardId)
    {
        var gateType = state.GateType ?? "(none)";
        stdout.WriteLine($"Resolved state: {state.Name} (gateType={gateType})");
        stdout.WriteLine();

        switch (gateType.ToLowerInvariant())
        {
            case GateTypes.AgentRun:
            case GateTypes.SystemMerge:
            case GateTypes.ChildrenComplete:
                stdout.WriteLine("Pickup result: ELIGIBLE");
                stdout.WriteLine();
                stdout.WriteLine("  This card matches an actionable state. Polling should pick it up.");
                stdout.WriteLine("  If it isn't, check:");
                stdout.WriteLine("    - Is `aiboard --mode polling` actually running for this project?");
                stdout.WriteLine("    - Is a higher-priority card claiming each polling tick? Run with");
                stdout.WriteLine("      `--mode metrics` to see recent activity.");
                stdout.WriteLine($"    - Try a direct run: aiboard --mode agent --card-id {cardId}");
                break;

            case GateTypes.ManualGate:
                stdout.WriteLine("Pickup result: WAITING ON HUMAN (manual gate)");
                stdout.WriteLine();
                stdout.WriteLine("  This is a manual approval column. The card advances only when you move it.");
                var nextTargets = ExtractMoveTargets(state);
                if (nextTargets.Count > 0)
                {
                    stdout.WriteLine("  Likely next column(s):");
                    foreach (var t in nextTargets)
                        stdout.WriteLine($"    - {t}");
                }
                break;

            case GateTypes.Holding:
                stdout.WriteLine("Pickup result: HOLDING (waiting on human)");
                stdout.WriteLine();
                stdout.WriteLine("  This is a holding column. Polling intentionally skips holding states.");
                stdout.WriteLine("  Common holding shapes:");
                stdout.WriteLine("    - Questions: agent posted questions; answer in the card body, then move back");
                stdout.WriteLine("                 to the trigger column to re-run.");
                stdout.WriteLine("    - Problems:  the run errored. Read the latest comment for the failure detail,");
                stdout.WriteLine("                 fix the underlying issue, and move back to trigger column to retry.");
                break;

            case GateTypes.Terminal:
                stdout.WriteLine("Pickup result: DONE");
                stdout.WriteLine();
                stdout.WriteLine("  This is a terminal column. The card is finished — no further work scheduled.");
                break;

            case GateTypes.InProgress:
                stdout.WriteLine("Pickup result: IN PROGRESS");
                stdout.WriteLine();
                stdout.WriteLine("  An agent is currently working this card (or a previous run did not unwind cleanly).");
                stdout.WriteLine("  If no run is actually active, the card may be stuck. Check for:");
                stdout.WriteLine("    - Running `aiboard` processes on this machine");
                stdout.WriteLine("    - Orphaned `aiboard-*` Docker containers (`docker ps`)");
                stdout.WriteLine("    - The card's assignee — if it's the agent username, an active run holds it.");
                break;

            case GateTypes.ManualEntry:
                stdout.WriteLine("Pickup result: ENTRY (manual)");
                stdout.WriteLine();
                stdout.WriteLine($"  This is a manual-entry column ('{state.Name}'). Move the card to a 'Ready'");
                stdout.WriteLine("  column to trigger work.");
                break;

            default:
                stdout.WriteLine($"Pickup result: UNRECOGNISED gateType '{gateType}'");
                break;
        }
    }

    private void WriteFilterMismatch(
        BoardCard card,
        IReadOnlyList<(string Id, WorkflowState State)> statesInColumn)
    {
        stdout.WriteLine("Pickup result: SKIPPED (no state filter passes)");
        stdout.WriteLine();
        stdout.WriteLine(
            $"  {statesInColumn.Count} state(s) share column '{card.ColumnId}', but none match this card's");
        stdout.WriteLine("  filters. First failing filter per state:");
        stdout.WriteLine();

        var explanations = new List<(string StateId, IReadOnlyList<FilterFailure> Failures)>();
        foreach (var (id, state) in statesInColumn)
            explanations.Add((id, WalkFilters(card, state)));

        var maxIdWidth = explanations.Max(e => e.StateId.Length);
        foreach (var (id, failures) in explanations)
        {
            var idPadded = id.PadRight(maxIdWidth);
            var first = failures.FirstOrDefault()?.Description ?? "(no filters; should have matched)";
            stdout.WriteLine($"    {idPadded}  {first}");
        }

        stdout.WriteLine();

        // Routing prioritises the operator-pitfall ladder (assignee → field → label).
        // Driven by structured FilterFailure.Filter, never substring-match against the
        // description text — that brittle coupling cost an entire afternoon of debugging.
        var allFailures = explanations.SelectMany(e => e.Failures).ToList();

        var assigneeIsEmptyFail = allFailures.FirstOrDefault(f =>
            f.Filter.Type == FilterTypes.Assignee &&
            f.Filter.Operator == FilterOperators.IsEmpty);
        if (assigneeIsEmptyFail is not null && card.Assignees is { Count: > 0 })
        {
            stdout.WriteLine("Most likely cause: card is assigned.");
            stdout.WriteLine();
            stdout.WriteLine($"  Assignment is the work-in-progress lock — polling skips assigned cards.");
            stdout.WriteLine($"  Currently assigned to: {string.Join(", ", card.Assignees.Select(a => "@" + a))}");
            stdout.WriteLine($"  → To re-trigger: unassign the card on the board UI, or run:");
            stdout.WriteLine($"      gh issue edit {card.Id} {string.Join(" ", card.Assignees.Select(a => "--remove-assignee " + a))}");
            return;
        }

        if (allFailures.Any(f => f.Filter.Type == FilterTypes.Field))
        {
            stdout.WriteLine("Most likely cause: a field value doesn't match any state.");
            stdout.WriteLine();
            stdout.WriteLine("  Set the field on the board UI to one of the values listed above, then re-run.");
            return;
        }

        if (allFailures.Any(f => f.Filter.Type == FilterTypes.Label))
        {
            stdout.WriteLine("Most likely cause: a label is missing.");
            stdout.WriteLine();
            stdout.WriteLine("  Add the required label(s) listed above, then re-run.");
            return;
        }
    }

    private sealed record FilterFailure(CardFilter Filter, string Description);

    /// <summary>
    /// Walks <paramref name="state"/>'s filters and returns the ones that
    /// failed against <paramref name="card"/>. Ordered by likelihood
    /// (assignee → field → label) so the caller picks the first entry to
    /// surface as the headline failure for that state.
    /// </summary>
    private static IReadOnlyList<FilterFailure> WalkFilters(BoardCard card, WorkflowState state)
    {
        var failures = new List<FilterFailure>();
        if (state.Filters is null) return failures;

        var ordered = state.Filters
            .Select((f, idx) => (Filter: f, Idx: idx))
            .OrderBy(x => x.Filter.Type switch
            {
                FilterTypes.Assignee => 0,
                FilterTypes.Field    => 1,
                FilterTypes.Label    => 2,
                _ => 3,
            })
            .ThenBy(x => x.Idx);

        foreach (var (filter, _) in ordered)
        {
            if (CardFilterEvaluator.Evaluate(card, filter)) continue;
            failures.Add(new FilterFailure(filter, DescribeFailure(card, filter)));
        }
        return failures;
    }

    private static string DescribeFailure(BoardCard card, CardFilter filter)
    {
        switch (filter.Type)
        {
            case FilterTypes.Assignee:
                {
                    var assignees = card.Assignees ?? Array.Empty<string>();
                    var actual = assignees.Count == 0
                        ? "(unassigned)"
                        : string.Join(", ", assignees.Select(a => "@" + a));
                    return filter.Operator switch
                    {
                        FilterOperators.IsEmpty    => $"assignee filter (isEmpty) ✗ assigned to {actual}",
                        FilterOperators.IsNotEmpty => $"assignee filter (isNotEmpty) ✗ {actual}",
                        FilterOperators.Equals     => $"assignee filter (equals @{filter.Value}) ✗ {actual}",
                        FilterOperators.NotEquals  => $"assignee filter (notEquals @{filter.Value}) ✗ {actual}",
                        _ => $"assignee filter ({filter.Operator}) ✗",
                    };
                }
            case FilterTypes.Field:
                {
                    string? actual = null;
                    if (card.Metadata is not null && filter.Field is not null)
                    {
                        foreach (var kvp in card.Metadata)
                        {
                            if (string.Equals(kvp.Key, filter.Field, StringComparison.OrdinalIgnoreCase))
                            {
                                actual = kvp.Value;
                                break;
                            }
                        }
                    }
                    var actualStr = string.IsNullOrEmpty(actual) ? "(unset)" : $"'{actual}'";
                    return filter.Operator switch
                    {
                        FilterOperators.Equals     => $"field {filter.Field} = '{filter.Value}' ✗ is {actualStr}",
                        FilterOperators.NotEquals  => $"field {filter.Field} ≠ '{filter.Value}' ✗ is {actualStr}",
                        FilterOperators.IsEmpty    => $"field {filter.Field} isEmpty ✗ is {actualStr}",
                        FilterOperators.IsNotEmpty => $"field {filter.Field} isNotEmpty ✗ is {actualStr}",
                        _ => $"field {filter.Field} ({filter.Operator}) ✗",
                    };
                }
            case FilterTypes.Label:
                {
                    var labels = card.Labels is { Count: > 0 } l
                        ? string.Join(", ", l)
                        : "(none)";
                    return filter.Operator switch
                    {
                        FilterOperators.Exists    => $"label '{filter.Value}' required ✗ has {labels}",
                        FilterOperators.NotExists => $"label '{filter.Value}' must NOT be present ✗ has {labels}",
                        _ => $"label '{filter.Value}' ({filter.Operator}) ✗",
                    };
                }
            default:
                return $"unknown filter type '{filter.Type}'";
        }
    }

    private static bool IsActionable(string? gateType) =>
        gateType is GateTypes.AgentRun or GateTypes.SystemMerge or GateTypes.ChildrenComplete;

    private static IReadOnlyList<string> ExtractMoveTargets(WorkflowState state)
    {
        var result = new List<string>();
        foreach (var (key, target) in state.Transitions)
        {
            // Skip the same-state IN_PROGRESS hop — it's where the card lives
            // while a run is active, not where it's heading next.
            if (string.Equals(key, TransitionKeys.InProgress, StringComparison.Ordinal)) continue;
            foreach (var act in target.Actions)
            {
                if (string.Equals(act.Type, ActionTypes.MoveToColumn, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(act.Value))
                    result.Add(act.Value);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
