namespace TaskBoard.Worker.Models;

public static class WorkflowConfigValidator
{
    private static readonly HashSet<string> KnownGateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        GateTypes.AgentRun, GateTypes.SystemMerge, GateTypes.ChildrenComplete,
        GateTypes.InProgress, GateTypes.ManualGate, GateTypes.ManualEntry,
        GateTypes.Holding, GateTypes.Terminal,
    };

    private static readonly HashSet<string> KnownGitBehaviors = new(StringComparer.OrdinalIgnoreCase)
    {
        "discard", "commit_only", "commit_and_push",
    };

    public static List<string> Validate(WorkflowConfig config, bool validatePolling = false)
    {
        var errors = new List<string>();

        if (config.States.Count == 0)
            errors.Add("States dictionary must not be empty.");

        if (config.Roles.Count == 0)
            errors.Add("Roles dictionary must not be empty.");

        // Role.model non-empty (weak check — don't validate model IDs)
        foreach (var (roleId, role) in config.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Model))
                errors.Add($"Role '{roleId}' has no model.");
        }

        // Track optional step names across the whole config (uniqueness)
        var globalOptionalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (stateId, state) in config.States)
        {
            // gateType enum check
            if (!string.IsNullOrWhiteSpace(state.GateType) && !KnownGateTypes.Contains(state.GateType))
                errors.Add($"State '{stateId}' ({state.Name}) has unknown gateType '{state.GateType}'. Expected one of: {string.Join(", ", KnownGateTypes)}.");

            // gitBehavior enum check
            if (!string.IsNullOrWhiteSpace(state.GitBehavior) && !KnownGitBehaviors.Contains(state.GitBehavior))
                errors.Add($"State '{stateId}' ({state.Name}) has unknown gitBehavior '{state.GitBehavior}'. Expected one of: {string.Join(", ", KnownGitBehaviors)}.");

            if (string.Equals(state.GateType, GateTypes.AgentRun, StringComparison.OrdinalIgnoreCase))
            {
                if (state.Steps is { Count: > 0 })
                {
                    // Multi-step validation
                    var stepNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var step in state.Steps)
                    {
                        if (string.IsNullOrWhiteSpace(step.Name))
                            errors.Add($"State '{stateId}' ({state.Name}) has a step with an empty name.");

                        if (!stepNames.Add(step.Name))
                            errors.Add($"State '{stateId}' ({state.Name}) has duplicate step name '{step.Name}'.");

                        if (string.IsNullOrWhiteSpace(step.Role))
                            errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' has no role.");
                        else if (!config.Roles.ContainsKey(step.Role))
                            errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' references role '{step.Role}' which does not exist in Roles.");

                        if (string.IsNullOrWhiteSpace(step.TaskPrompt) && string.IsNullOrWhiteSpace(step.TaskPromptFile))
                            errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' has no taskPrompt or taskPromptFile.");
                    }

                    // Validate roles referenced by steps have system prompts
                    foreach (var step in state.Steps)
                    {
                        if (!string.IsNullOrWhiteSpace(step.Role) && config.Roles.TryGetValue(step.Role, out var stepRole))
                        {
                            if (string.IsNullOrWhiteSpace(stepRole.SystemPrompt) && string.IsNullOrWhiteSpace(stepRole.SystemPromptFile))
                                errors.Add($"Role '{step.Role}' has no systemPrompt or systemPromptFile.");
                        }
                    }
                }
                else
                {
                    // Legacy single-step validation (state without steps array)
                    if (string.IsNullOrWhiteSpace(state.Role))
                        errors.Add($"State '{stateId}' ({state.Name}) is agent_run but has no role.");
                    else if (!config.Roles.ContainsKey(state.Role))
                        errors.Add($"State '{stateId}' ({state.Name}) references role '{state.Role}' which does not exist in Roles.");

                    if (string.IsNullOrWhiteSpace(state.TaskPrompt) && string.IsNullOrWhiteSpace(state.TaskPromptFile))
                        errors.Add($"State '{stateId}' ({state.Name}) is agent_run but has no taskPrompt or taskPromptFile.");

                    if (!string.IsNullOrWhiteSpace(state.Role) && config.Roles.TryGetValue(state.Role, out var role))
                    {
                        if (string.IsNullOrWhiteSpace(role.SystemPrompt) && string.IsNullOrWhiteSpace(role.SystemPromptFile))
                            errors.Add($"Role '{state.Role}' has no systemPrompt or systemPromptFile.");
                    }
                }
            }

            // Gate check validation
            if (state.GateCheck is not null)
            {
                if (!config.Roles.ContainsKey(state.GateCheck.Role))
                    errors.Add($"State '{stateId}' ({state.Name}) gateCheck references role '{state.GateCheck.Role}' which does not exist in Roles.");

                if (string.IsNullOrWhiteSpace(state.GateCheck.TaskPromptFile) && string.IsNullOrWhiteSpace(state.GateCheck.TaskPrompt))
                    errors.Add($"State '{stateId}' ({state.Name}) gateCheck has no taskPromptFile or taskPrompt.");
            }

            // Optional steps validation
            if (state.OptionalSteps is { Count: > 0 })
            {
                if (state.GateCheck is null)
                    errors.Add($"State '{stateId}' ({state.Name}) has optionalSteps but no gateCheck. Optional steps can only be requested by a gate check.");

                var optionalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var optStep in state.OptionalSteps)
                {
                    if (string.IsNullOrWhiteSpace(optStep.Name))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) has an optional step with an empty name.");
                        continue;
                    }

                    if (!optionalNames.Add(optStep.Name))
                        errors.Add($"State '{stateId}' ({state.Name}) has duplicate optional step name '{optStep.Name}'.");

                    if (globalOptionalNames.TryGetValue(optStep.Name, out var otherState))
                    {
                        if (!string.Equals(otherState, stateId, StringComparison.OrdinalIgnoreCase))
                            errors.Add($"Optional step name '{optStep.Name}' is defined in both state '{otherState}' and state '{stateId}'. Names must be unique across the whole config.");
                    }
                    else
                    {
                        globalOptionalNames[optStep.Name] = stateId;
                    }

                    if (string.IsNullOrWhiteSpace(optStep.Role))
                        errors.Add($"State '{stateId}' ({state.Name}) optional step '{optStep.Name}' has no role.");
                    else if (!config.Roles.ContainsKey(optStep.Role))
                        errors.Add($"State '{stateId}' ({state.Name}) optional step '{optStep.Name}' references role '{optStep.Role}' which does not exist in Roles.");

                    if (string.IsNullOrWhiteSpace(optStep.TaskPrompt) && string.IsNullOrWhiteSpace(optStep.TaskPromptFile))
                        errors.Add($"State '{stateId}' ({state.Name}) optional step '{optStep.Name}' has neither taskPrompt nor taskPromptFile.");

                    if (state.Steps?.Any(s => string.Equals(s.Name, optStep.Name, StringComparison.OrdinalIgnoreCase)) == true)
                        errors.Add($"State '{stateId}' ({state.Name}) optional step '{optStep.Name}' collides with a mandatory step name.");
                }
            }

            // Validate transition targets and actions
            foreach (var (outcome, target) in state.Transitions)
            {
                // Validate moveToColumn targets reference valid effective column names
                if (target.Column is string col
                    && !config.States.Keys.Any(k =>
                        string.Equals(config.GetEffectiveColumn(k), col, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"State '{stateId}' ({state.Name}) transition '{outcome}' targets column '{col}' which is not mapped to any state.");
                }

                // Validate action types and required fields
                foreach (var action in target.Actions)
                {
                    if (action.Type is not (ActionTypes.MoveToColumn or ActionTypes.AddLabel or ActionTypes.RemoveLabel
                        or ActionTypes.Assign or ActionTypes.Unassign or ActionTypes.SetField or ActionTypes.ClearField
                        or ActionTypes.UpdateParentSum or ActionTypes.CompleteParentIfReady))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) transition '{outcome}' has unknown action type '{action.Type}'.");
                    }

                    if (action.Type is ActionTypes.MoveToColumn or ActionTypes.AddLabel or ActionTypes.RemoveLabel
                        or ActionTypes.Assign or ActionTypes.SetField
                        && string.IsNullOrEmpty(action.Value))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) transition '{outcome}' action '{action.Type}' requires a value.");
                    }

                    if (action.Type is ActionTypes.SetField or ActionTypes.ClearField or ActionTypes.UpdateParentSum
                        && string.IsNullOrEmpty(action.Field))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) transition '{outcome}' action '{action.Type}' requires a field name.");
                    }
                }
            }

            // Validate filters
            if (state.Filters is { Count: > 0 })
            {
                foreach (var filter in state.Filters)
                {
                    if (filter.Type is not (FilterTypes.Label or FilterTypes.Assignee or FilterTypes.Field))
                        errors.Add($"State '{stateId}' ({state.Name}) filter has unknown type '{filter.Type}'.");

                    var validOps = filter.Type switch
                    {
                        FilterTypes.Label    => new[] { FilterOperators.Exists, FilterOperators.NotExists },
                        FilterTypes.Assignee => new[] { FilterOperators.IsEmpty, FilterOperators.IsNotEmpty,
                                                        FilterOperators.Equals, FilterOperators.NotEquals },
                        FilterTypes.Field    => new[] { FilterOperators.Equals, FilterOperators.NotEquals,
                                                        FilterOperators.IsEmpty, FilterOperators.IsNotEmpty },
                        _                    => Array.Empty<string>()
                    };
                    if (!validOps.Contains(filter.Operator))
                        errors.Add($"State '{stateId}' ({state.Name}) filter operator '{filter.Operator}' is not valid for type '{filter.Type}'.");

                    if (filter.Type == FilterTypes.Label && string.IsNullOrEmpty(filter.Value))
                        errors.Add($"State '{stateId}' ({state.Name}) label filter requires a value.");

                    if (filter.Operator is FilterOperators.Equals or FilterOperators.NotEquals
                        && string.IsNullOrEmpty(filter.Value))
                        errors.Add($"State '{stateId}' ({state.Name}) filter with operator '{filter.Operator}' requires a value.");

                    if (filter.Type == FilterTypes.Field && string.IsNullOrEmpty(filter.Field))
                        errors.Add($"State '{stateId}' ({state.Name}) field filter requires a 'field' property.");
                }
            }

            // ── children_complete state validation ───────────────────────────
            if (string.Equals(state.GateType, GateTypes.ChildrenComplete, StringComparison.OrdinalIgnoreCase))
            {
                if (!state.Transitions.ContainsKey(TransitionKeys.Complete))
                    errors.Add($"State '{stateId}' ({state.Name}) is children_complete but has no 'COMPLETE' transition.");
            }

            // ── generationConfig validation ──────────────────────────────────
            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    if (step.GenerationConfig is not { } genCfg) continue;

                    if (string.IsNullOrWhiteSpace(genCfg.TargetType))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' generationConfig has empty targetType.");
                        continue;
                    }

                    // targetType must exist in cardTypes (if cardTypes is defined)
                    if (config.CardTypes is not null
                        && !config.CardTypes.ContainsKey(genCfg.TargetType))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' generationConfig.targetType '{genCfg.TargetType}' is not defined in cardTypes.");
                    }

                    // targetColumn (if specified) must reference a known effective column name
                    if (genCfg.TargetColumn is not null
                        && !config.States.Keys.Any(k =>
                            string.Equals(config.GetEffectiveColumn(k), genCfg.TargetColumn, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' generationConfig.targetColumn '{genCfg.TargetColumn}' is not a known column.");
                    }

                    // setFields: keys non-empty, values non-empty
                    if (genCfg.SetFields is { Count: > 0 })
                    {
                        foreach (var (k, v) in genCfg.SetFields)
                        {
                            if (string.IsNullOrWhiteSpace(k))
                                errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' generationConfig.setFields has an empty field name.");
                            if (string.IsNullOrWhiteSpace(v))
                                errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' generationConfig.setFields['{k}'] has an empty value.");
                        }
                    }
                }
            }
        }

        // ── cardTypes allowedChildren cross-check ────────────────────────────
        if (config.CardTypes is not null)
        {
            foreach (var (typeName, typeDef) in config.CardTypes)
            {
                if (typeDef.AllowedChildren is not { Count: > 0 }) continue;

                foreach (var childType in typeDef.AllowedChildren)
                {
                    if (!config.CardTypes.ContainsKey(childType))
                        errors.Add($"cardTypes['{typeName}'].allowedChildren contains '{childType}' which is not a defined card type.");
                }
            }
        }

        // Note: Sections can be empty for roles that don't write to card body
        // (e.g., gate_checker, code_reviewer — they produce comments only)

        // ── Shared-column filter enforcement ────────────────────────────────────
        // When multiple states share the same effective column, all states sharing that column
        // must have filters if any of them is actionable (agent_run, system_merge, children_complete).
        var statesByColumn = config.States
            .GroupBy(kvp => config.GetEffectiveColumn(kvp.Key), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in statesByColumn)
        {
            var hasActionable = group.Any(kvp =>
                string.Equals(kvp.Value.GateType, GateTypes.AgentRun, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kvp.Value.GateType, GateTypes.SystemMerge, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kvp.Value.GateType, GateTypes.ChildrenComplete, StringComparison.OrdinalIgnoreCase));

            if (!hasActionable) continue;

            foreach (var (sid, st) in group)
            {
                if (st.Filters is null or { Count: 0 })
                    errors.Add($"State '{sid}' ({st.Name}) shares column '{group.Key}' with another actionable state but has no filters. Add filters to disambiguate.");
            }
        }

        // ── Estimation config sanity ─────────────────────────────────────────
        if (config.Estimation is { } est)
        {
            if (est.Scale is { Count: > 0 })
            {
                if (est.Scale.Any(v => v <= 0))
                    errors.Add("estimation.scale contains non-positive values.");

                for (var i = 1; i < est.Scale.Count; i++)
                {
                    if (est.Scale[i] <= est.Scale[i - 1])
                    {
                        errors.Add("estimation.scale must be strictly monotonic increasing.");
                        break;
                    }
                }

                if (!est.Scale.Contains(est.CalibrationSize))
                    errors.Add($"estimation.calibrationSize ({est.CalibrationSize}) is not present in estimation.scale.");
            }
            else if (est.Scale is not null)
            {
                errors.Add("estimation.scale is present but empty.");
            }
        }

        // ── cardTypes discriminator rule ─────────────────────────────────────
        // A card type must have a way to be identified on the board: either a non-empty
        // labelPrefix (label-based) or workflowConfig.CardTypeField set (field-based).
        // The two mechanisms may be combined; only absence of both is an error.
        if (config.CardTypes is not null)
        {
            var hasFieldDiscriminator = !string.IsNullOrWhiteSpace(config.CardTypeField);
            foreach (var (typeName, typeDef) in config.CardTypes)
            {
                var hasLabelDiscriminator = !string.IsNullOrWhiteSpace(typeDef.LabelPrefix);
                if (!hasLabelDiscriminator && !hasFieldDiscriminator)
                    errors.Add($"cardTypes['{typeName}'] has no discriminator: set labelPrefix on the type or cardTypeField on the workflow.");
            }
        }

        if (validatePolling)
            ValidatePollingConfig(config, errors);

        return errors;
    }

    /// <summary>
    /// Non-fatal audit checks — return soft warnings about configurations that are
    /// valid but might not be what the operator intended. Callers log these as
    /// warnings; they do not block startup.
    /// </summary>
    public static List<string> Audit(WorkflowConfig config)
    {
        var warnings = new List<string>();
        AuditCodexSandboxDefaults(config, warnings);
        return warnings;
    }

    /// <summary>
    /// Keys in <c>providerParams</c> that make the Codex sandbox choice explicit.
    /// Any one of these being set counts as an intentional choice.
    /// </summary>
    private static readonly string[] CodexSandboxParamKeys = { "sandbox", "yolo", "fullAuto" };

    /// <summary>
    /// Warns when a Codex role is invoked by a step whose effective provider params
    /// set none of <see cref="CodexSandboxParamKeys"/>. The Codex CLI will still run
    /// (falling back to <see cref="Clients.CodexCliLlmOptions.FullAuto"/>), but an
    /// implicit sandbox choice is easy to miss on review — surfacing it lets the
    /// operator opt in to a specific policy per role.
    /// </summary>
    private static void AuditCodexSandboxDefaults(WorkflowConfig config, List<string> warnings)
    {
        foreach (var (stateId, state) in config.States)
        {
            if (!string.Equals(state.GateType, GateTypes.AgentRun, StringComparison.OrdinalIgnoreCase))
                continue;

            var stateHasSandboxChoice = HasAnyKey(state.ProviderParams, CodexSandboxParamKeys);

            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    if (!IsCodexRole(config, step.Role)) continue;
                    if (stateHasSandboxChoice) continue;

                    warnings.Add(
                        $"State '{stateId}' ({state.Name}) step '{step.Name}' uses Codex role '{step.Role}' " +
                        $"but no sandbox policy is configured in providerParams. " +
                        $"Set one of {string.Join("/", CodexSandboxParamKeys)} on the state's providerParams " +
                        $"to make the sandbox choice explicit (Codex will otherwise fall back to CodexCliLlmOptions.FullAuto).");
                }
            }

            if (state.OptionalSteps is { Count: > 0 })
            {
                foreach (var optStep in state.OptionalSteps)
                {
                    if (!IsCodexRole(config, optStep.Role)) continue;
                    var stepHasSandboxChoice = HasAnyKey(optStep.ProviderParams, CodexSandboxParamKeys);
                    if (stateHasSandboxChoice || stepHasSandboxChoice) continue;

                    warnings.Add(
                        $"State '{stateId}' ({state.Name}) optional step '{optStep.Name}' uses Codex role " +
                        $"'{optStep.Role}' but no sandbox policy is configured in providerParams (state or step). " +
                        $"Set one of {string.Join("/", CodexSandboxParamKeys)} to make the sandbox choice explicit.");
                }
            }

            // Legacy single-role states (no steps array) — Normalise() will convert them,
            // but Audit may run before normalisation. Check the top-level Role too.
            if ((state.Steps is null or { Count: 0 })
                && state.Role is { Length: > 0 }
                && IsCodexRole(config, state.Role)
                && !stateHasSandboxChoice)
            {
                warnings.Add(
                    $"State '{stateId}' ({state.Name}) uses Codex role '{state.Role}' but no sandbox policy " +
                    $"is configured in providerParams. Set one of {string.Join("/", CodexSandboxParamKeys)} " +
                    "to make the sandbox choice explicit.");
            }
        }
    }

    private static bool IsCodexRole(WorkflowConfig config, string? roleId)
    {
        if (string.IsNullOrEmpty(roleId)) return false;
        return config.Roles.TryGetValue(roleId, out var role)
            && string.Equals(role.Provider, "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyKey(IReadOnlyDictionary<string, string>? dict, IEnumerable<string> keys)
    {
        if (dict is null) return false;
        foreach (var k in keys)
            if (dict.ContainsKey(k)) return true;
        return false;
    }

    private static void ValidatePollingConfig(WorkflowConfig config, List<string> errors)
    {
        var runnableStates = config.States
            .Where(kvp => kvp.Value.GateType is GateTypes.AgentRun or GateTypes.SystemMerge or GateTypes.ChildrenComplete)
            .ToList();

        foreach (var (stateId, state) in runnableStates)
        {
            if (state.PipelineOrder <= 0)
                errors.Add($"State '{stateId}' ({state.Name}) is {state.GateType} but has no pipelineOrder (must be > 0 for polling mode).");
        }

        var duplicateOrders = runnableStates
            .Where(kvp => kvp.Value.PipelineOrder > 0)
            .GroupBy(kvp => kvp.Value.PipelineOrder)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in duplicateOrders)
        {
            var names = string.Join(", ", group.Select(kvp => $"'{kvp.Key}'"));
            errors.Add($"Duplicate pipelineOrder {group.Key} on runnable states: {names}.");
        }

        if (config.Polling is not null)
        {
            if (!string.IsNullOrWhiteSpace(config.Polling.PriorityFieldName)
                && (config.Polling.PriorityOrder is null || config.Polling.PriorityOrder.Count == 0))
            {
                errors.Add("polling.priorityFieldName is set but polling.priorityOrder is missing or empty.");
            }

            if (config.Polling.PriorityOrder is not null)
            {
                var duplicateValues = config.Polling.PriorityOrder
                    .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                if (duplicateValues.Count > 0)
                    errors.Add($"polling.priorityOrder contains duplicate values: {string.Join(", ", duplicateValues)}.");
            }
        }
    }
}
