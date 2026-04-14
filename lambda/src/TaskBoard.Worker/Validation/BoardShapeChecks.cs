using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Validation;

/// <summary>
/// Pure checks of a <see cref="WorkflowConfig"/> against a live <see cref="BoardShape"/>.
/// No I/O. Exercised directly in unit tests.
/// </summary>
public static class BoardShapeChecks
{
    public static List<ValidationFinding> Check(WorkflowConfig config, BoardShape shape)
    {
        var findings = new List<ValidationFinding>();
        var columns = new HashSet<string>(shape.ColumnNames, StringComparer.Ordinal);
        var columnsCi = new HashSet<string>(shape.ColumnNames, StringComparer.OrdinalIgnoreCase);
        var fieldsByName = shape.Fields.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(shape.Labels, StringComparer.OrdinalIgnoreCase);

        // #8: every state's effective column exists on the board
        foreach (var (stateId, _) in config.States)
        {
            var col = config.GetEffectiveColumn(stateId);
            if (columns.Contains(col)) continue;

            var hint = columnsCi.Contains(col)
                ? $"case mismatch; board has '{shape.ColumnNames.First(c => c.Equals(col, StringComparison.OrdinalIgnoreCase))}'"
                : NearestColumn(col, shape.ColumnNames);

            findings.Add(new ValidationFinding(
                ValidationSeverity.Error, "board", $"states[{stateId}].column",
                $"Effective column '{col}' does not exist on the board.",
                hint));
        }

        // #9: every moveToColumn action target exists (covers generationConfig.targetColumn too)
        foreach (var (stateId, state) in config.States)
        {
            foreach (var (outcome, target) in state.Transitions)
            {
                foreach (var (action, idx) in target.Actions.Select((a, i) => (a, i)))
                {
                    if (action.Type != ActionTypes.MoveToColumn || string.IsNullOrEmpty(action.Value))
                        continue;
                    if (columnsCi.Contains(action.Value)) continue;

                    findings.Add(new ValidationFinding(
                        ValidationSeverity.Error, "board",
                        $"states[{stateId}].transitions.{outcome}[{idx}].value",
                        $"moveToColumn target '{action.Value}' does not exist on the board.",
                        NearestColumn(action.Value, shape.ColumnNames)));
                }
            }

            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    if (step.GenerationConfig?.TargetColumn is { } tc && !columnsCi.Contains(tc))
                    {
                        findings.Add(new ValidationFinding(
                            ValidationSeverity.Error, "board",
                            $"states[{stateId}].steps[{step.Name}].generationConfig.targetColumn",
                            $"targetColumn '{tc}' does not exist on the board.",
                            NearestColumn(tc, shape.ColumnNames)));
                    }
                }
            }
        }

        // #10: polling priority field + values
        if (config.Polling?.PriorityFieldName is { Length: > 0 } pfn)
        {
            if (!fieldsByName.TryGetValue(pfn, out var pField))
            {
                findings.Add(new ValidationFinding(
                    ValidationSeverity.Error, "board", "polling.priorityFieldName",
                    $"Field '{pfn}' does not exist on the board."));
            }
            else if (config.Polling.PriorityOrder is { Count: > 0 } pOrder)
            {
                if (pField.Options is { Count: > 0 } opts)
                {
                    var optNames = new HashSet<string>(opts.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var v in pOrder)
                    {
                        if (!optNames.Contains(v))
                            findings.Add(new ValidationFinding(
                                ValidationSeverity.Error, "board", "polling.priorityOrder",
                                $"Value '{v}' is not an option of field '{pfn}'."));
                    }
                }
                else
                {
                    findings.Add(new ValidationFinding(
                        ValidationSeverity.Info, "board", "polling.priorityFieldName",
                        $"Field '{pfn}' is not single-select; cannot verify priorityOrder values."));
                }
            }
        }

        // #11: estimation field
        if (config.Estimation is { } est)
        {
            if (!fieldsByName.TryGetValue(est.FieldName, out var estField))
            {
                findings.Add(new ValidationFinding(
                    ValidationSeverity.Error, "board", "estimation.fieldName",
                    $"Field '{est.FieldName}' does not exist on the board."));
            }
            else if (estField.Options is { Count: > 0 } opts && est.Scale is { Count: > 0 })
            {
                var optNames = new HashSet<string>(opts.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var s in est.Scale)
                {
                    if (!optNames.Contains(s.ToString()))
                        findings.Add(new ValidationFinding(
                            ValidationSeverity.Error, "board", "estimation.scale",
                            $"Scale value '{s}' is not an option of single-select field '{est.FieldName}'."));
                }
            }
        }

        // #12: setField actions reference existing fields; hard-coded values validated where possible
        foreach (var (stateId, state) in config.States)
        {
            foreach (var (outcome, target) in state.Transitions)
            {
                foreach (var (action, idx) in target.Actions.Select((a, i) => (a, i)))
                {
                    if (action.Type == ActionTypes.SetField && !string.IsNullOrEmpty(action.Field))
                    {
                        if (!fieldsByName.TryGetValue(action.Field, out var f))
                        {
                            findings.Add(new ValidationFinding(
                                ValidationSeverity.Error, "board",
                                $"states[{stateId}].transitions.{outcome}[{idx}].field",
                                $"setField references field '{action.Field}' which does not exist on the board."));
                        }
                        else if (!string.IsNullOrEmpty(action.Value)
                                 && !IsTemplate(action.Value)
                                 && f.Options is { Count: > 0 } opts
                                 && !opts.Any(o => o.Name.Equals(action.Value, StringComparison.OrdinalIgnoreCase)))
                        {
                            findings.Add(new ValidationFinding(
                                ValidationSeverity.Error, "board",
                                $"states[{stateId}].transitions.{outcome}[{idx}].value",
                                $"setField value '{action.Value}' is not an option of field '{action.Field}'."));
                        }
                    }
                    else if (action.Type == ActionTypes.ClearField && !string.IsNullOrEmpty(action.Field))
                    {
                        if (!fieldsByName.ContainsKey(action.Field))
                            findings.Add(new ValidationFinding(
                                ValidationSeverity.Error, "board",
                                $"states[{stateId}].transitions.{outcome}[{idx}].field",
                                $"clearField references field '{action.Field}' which does not exist on the board."));
                    }
                    else if (action.Type == ActionTypes.UpdateParentSum && !string.IsNullOrEmpty(action.Field))
                    {
                        if (!fieldsByName.ContainsKey(action.Field))
                            findings.Add(new ValidationFinding(
                                ValidationSeverity.Error, "board",
                                $"states[{stateId}].transitions.{outcome}[{idx}].field",
                                $"updateParentSum references field '{action.Field}' which does not exist on the board."));
                    }
                }
            }
        }

        // #13: field-type filters
        foreach (var (stateId, state) in config.States)
        {
            if (state.Filters is null) continue;
            foreach (var (filter, idx) in state.Filters.Select((f, i) => (f, i)))
            {
                if (filter.Type != FilterTypes.Field || string.IsNullOrEmpty(filter.Field)) continue;

                if (!fieldsByName.TryGetValue(filter.Field, out var f))
                {
                    findings.Add(new ValidationFinding(
                        ValidationSeverity.Error, "board",
                        $"states[{stateId}].filters[{idx}].field",
                        $"Filter references field '{filter.Field}' which does not exist on the board."));
                    continue;
                }

                if (filter.Operator is FilterOperators.Equals or FilterOperators.NotEquals
                    && !string.IsNullOrEmpty(filter.Value)
                    && f.Options is { Count: > 0 } opts
                    && !opts.Any(o => o.Name.Equals(filter.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    findings.Add(new ValidationFinding(
                        ValidationSeverity.Error, "board",
                        $"states[{stateId}].filters[{idx}].value",
                        $"Filter value '{filter.Value}' is not an option of field '{filter.Field}'."));
                }
            }
        }

        // #15: cardTypes labels (Warning — AddLabelAsync can create on demand)
        if (config.CardTypes is not null && shape.Labels.Count > 0)
        {
            foreach (var (typeKey, typeDef) in config.CardTypes)
            {
                var labelName = $"{typeDef.LabelPrefix}:{typeKey}";
                if (!labels.Contains(labelName))
                    findings.Add(new ValidationFinding(
                        ValidationSeverity.Warning, "board", $"cardTypes[{typeKey}]",
                        $"Label '{labelName}' is not defined on the repo. It will be created on first use."));
            }
        }

        return findings;
    }

    private static bool IsTemplate(string value) =>
        value.Contains("{{") && value.Contains("}}");

    private static string? NearestColumn(string target, IReadOnlyList<string> columns)
    {
        var ci = columns.FirstOrDefault(c => c.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (ci is not null) return $"did you mean '{ci}'?";

        // cheap: any column containing the trimmed target, or trimmed target containing the column
        var t = target.Trim();
        var guess = columns.FirstOrDefault(c =>
            c.Contains(t, StringComparison.OrdinalIgnoreCase)
            || t.Contains(c, StringComparison.OrdinalIgnoreCase));
        return guess is null ? null : $"did you mean '{guess}'?";
    }
}
