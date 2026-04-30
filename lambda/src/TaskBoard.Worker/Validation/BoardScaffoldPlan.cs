using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Validation;

/// <summary>A field the workflow needs but the board doesn't have at all.</summary>
public sealed record MissingField(string Name, IReadOnlyList<string> Options);

/// <summary>An option the workflow references on a field that exists on the board but lacks the option.</summary>
public sealed record FieldOptionGap(
    string FieldName,
    IReadOnlyList<string> ExistingOptions,
    IReadOnlyList<string> MissingOptions);

/// <summary>
/// What needs to change on the board so the workflow's filters / setField /
/// moveToColumn actions all reference real shape. Pure data — produced by
/// <see cref="BoardScaffoldDiff.Compute"/>, consumed by ScaffoldBoardRunner.
/// </summary>
public sealed record BoardScaffoldPlan(
    IReadOnlyList<MissingField> MissingFields,
    IReadOnlyList<FieldOptionGap> FieldOptionGaps,
    IReadOnlyList<string> MissingLabels,
    IReadOnlyList<string> MissingColumns)
{
    public bool IsEmpty =>
        MissingFields.Count == 0 &&
        FieldOptionGaps.Count == 0 &&
        MissingLabels.Count == 0 &&
        MissingColumns.Count == 0;

    /// <summary>True iff there is something we can actually CREATE via gh CLI.</summary>
    public bool HasCreatableActions =>
        MissingFields.Count > 0 || MissingLabels.Count > 0;

    /// <summary>True iff there is something the operator needs to do manually.</summary>
    public bool HasManualActions =>
        FieldOptionGaps.Count > 0 || MissingColumns.Count > 0;
}

/// <summary>
/// Pure helper: walks <see cref="WorkflowConfig"/> to discover the fields
/// (with options) and labels and columns it references, then diffs that
/// against an introspected <see cref="BoardShape"/>. Returns a
/// <see cref="BoardScaffoldPlan"/> the runner can render and (selectively)
/// apply.
/// </summary>
public static class BoardScaffoldDiff
{
    public static BoardScaffoldPlan Compute(WorkflowConfig config, BoardShape shape)
    {
        // ── 1. Aggregate workflow requirements ───────────────────────────────
        // field name → required option set (case-sensitive comparison; we keep
        // canonical case for option creation but fold case for membership).
        var requiredFields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var requiredLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRequiredField(string name, string? option)
        {
            if (!requiredFields.TryGetValue(name, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                requiredFields[name] = set;
            }
            if (!string.IsNullOrEmpty(option) && !IsTemplate(option))
                set.Add(option);
        }

        // 1a. Filters (field equals/notEquals contribute options; isEmpty/isNotEmpty don't)
        foreach (var (_, state) in config.States)
        {
            if (state.Filters is null) continue;
            foreach (var f in state.Filters)
            {
                if (f.Type == FilterTypes.Field && !string.IsNullOrEmpty(f.Field))
                {
                    if (f.Operator is FilterOperators.Equals or FilterOperators.NotEquals)
                        AddRequiredField(f.Field, f.Value);
                    else
                        AddRequiredField(f.Field, null);
                }
                else if (f.Type == FilterTypes.Label && f.Operator == FilterOperators.Exists
                         && !string.IsNullOrEmpty(f.Value))
                {
                    requiredLabels.Add(f.Value);
                }
            }
        }

        // 1b. Transition actions (setField, clearField, moveToColumn, addLabel, removeLabel)
        foreach (var (_, state) in config.States)
        {
            foreach (var (_, target) in state.Transitions)
            {
                foreach (var act in target.Actions)
                {
                    switch (act.Type)
                    {
                        case ActionTypes.SetField when !string.IsNullOrEmpty(act.Field):
                            AddRequiredField(act.Field, act.Value);
                            break;
                        case ActionTypes.ClearField when !string.IsNullOrEmpty(act.Field):
                            AddRequiredField(act.Field, null);
                            break;
                        case ActionTypes.UpdateParentSum when !string.IsNullOrEmpty(act.Field):
                            AddRequiredField(act.Field, null);
                            break;
                        case ActionTypes.MoveToColumn when !string.IsNullOrEmpty(act.Value)
                                                          && !IsTemplate(act.Value):
                            requiredColumns.Add(act.Value);
                            break;
                        // addLabel + removeLabel are symmetric: both reference a label that must
                        // exist in the repo (gh issue edit --remove-label fails on unknown labels).
                        case ActionTypes.AddLabel when !string.IsNullOrEmpty(act.Value)
                                                     && !IsTemplate(act.Value):
                        case ActionTypes.RemoveLabel when !string.IsNullOrEmpty(act.Value)
                                                        && !IsTemplate(act.Value):
                            requiredLabels.Add(act.Value!);
                            break;
                    }
                }
            }
        }

        // 1c. Effective column for each state (needs to exist as a Status option)
        foreach (var (stateId, _) in config.States)
            requiredColumns.Add(config.GetEffectiveColumn(stateId));

        // 1d. Polling priority field + values
        if (config.Polling is { } poll)
        {
            if (!string.IsNullOrEmpty(poll.PriorityFieldName) && poll.PriorityOrder is { Count: > 0 })
            {
                foreach (var v in poll.PriorityOrder)
                    AddRequiredField(poll.PriorityFieldName, v);
            }
        }

        // 1e. Estimation field + scale values
        if (config.Estimation is { } est)
        {
            if (est.Scale is { Count: > 0 })
            {
                foreach (var s in est.Scale)
                    AddRequiredField(est.FieldName, s.ToString());
            }
            else
            {
                AddRequiredField(est.FieldName, null);
            }
        }

        // 1f. cardTypeField (discriminator) — must exist with each cardType's Name as an option
        if (!string.IsNullOrWhiteSpace(config.CardTypeField) && config.CardTypes is not null)
        {
            foreach (var (_, def) in config.CardTypes)
                AddRequiredField(config.CardTypeField, def.Name);
        }

        // 1g. cardTypes labels (only when LabelPrefix is non-empty)
        if (config.CardTypes is not null)
        {
            foreach (var (typeKey, def) in config.CardTypes)
            {
                if (!string.IsNullOrWhiteSpace(def.LabelPrefix))
                    requiredLabels.Add($"{def.LabelPrefix}:{typeKey}");
            }
        }

        // ── 2. Diff against the introspected shape ───────────────────────────
        var fieldsByName = shape.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var existingColumns = new HashSet<string>(shape.ColumnNames, StringComparer.OrdinalIgnoreCase);
        var existingLabels = new HashSet<string>(shape.Labels, StringComparer.OrdinalIgnoreCase);

        var missingFields = new List<MissingField>();
        var fieldOptionGaps = new List<FieldOptionGap>();

        foreach (var (fieldName, requiredOpts) in requiredFields)
        {
            if (!fieldsByName.TryGetValue(fieldName, out var existing))
            {
                // Brand-new field — create with all required options.
                missingFields.Add(new MissingField(fieldName, requiredOpts.OrderBy(s => s, StringComparer.Ordinal).ToList()));
                continue;
            }

            // Field exists. Diff its options against required ones.
            if (existing.Options is null || existing.Options.Count == 0)
            {
                // Field exists but isn't a single-select — can't gap-check.
                // If we required specific options, that's a manual operator action
                // (the field type would need to change, beyond scaffold's scope).
                if (requiredOpts.Count > 0)
                {
                    fieldOptionGaps.Add(new FieldOptionGap(
                        fieldName,
                        Array.Empty<string>(),
                        requiredOpts.OrderBy(s => s, StringComparer.Ordinal).ToList()));
                }
                continue;
            }

            var existingOptNames = existing.Options
                .Select(o => o.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = requiredOpts
                .Where(o => !existingOptNames.Contains(o))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            if (missing.Count > 0)
            {
                fieldOptionGaps.Add(new FieldOptionGap(
                    fieldName,
                    existing.Options.Select(o => o.Name).ToList(),
                    missing));
            }
        }

        var missingLabels = requiredLabels
            .Where(l => !existingLabels.Contains(l))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Columns: only flag when the introspected shape actually returned
        // columns. An empty column list usually means the probe couldn't see
        // the Status field (auth scope / gh permission), and listing every
        // workflow column as "missing" would just be noise.
        var missingColumns = shape.ColumnNames.Count == 0
            ? new List<string>()
            : requiredColumns
                .Where(c => !existingColumns.Contains(c))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

        return new BoardScaffoldPlan(
            missingFields, fieldOptionGaps, missingLabels, missingColumns);
    }

    private static bool IsTemplate(string? value) =>
        value is not null && value.Contains("{{", StringComparison.Ordinal);
}
