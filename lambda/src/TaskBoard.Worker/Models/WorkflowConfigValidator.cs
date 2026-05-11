using TaskBoard.Worker.Configuration;

namespace TaskBoard.Worker.Models;

public static class WorkflowConfigValidator
{
    /// <summary>
    /// Validates a role's <see cref="WorkflowRole.Fallbacks"/> chain (if any).
    /// Errors:
    /// <list type="bullet">
    ///   <item>Each fallback's <c>provider</c> must be non-empty.</item>
    ///   <item>Each fallback's <c>provider</c> must be a recognised executor key
    ///         (per <see cref="StartupConfigValidator.KnownAgentExecutors"/>).
    ///         A typo here would surface as a runtime resolver miss when the
    ///         fallback fires — possibly hours into a polling run.</item>
    ///   <item><c>fallbackOn</c> without any fallbacks is invalid because it can
    ///         never take effect.</item>
    /// </list>
    /// </summary>
    private static void ValidateRoleFallback(string roleId, WorkflowRole role, List<string> errors)
    {
        if (role.Fallbacks is { Count: > 0 } fallbacks)
        {
            for (var i = 0; i < fallbacks.Count; i++)
            {
                var fb = fallbacks[i];
                if (string.IsNullOrWhiteSpace(fb.Provider))
                {
                    errors.Add($"Role '{roleId}' fallback #{i} has an empty provider.");
                    continue;
                }

                if (!StartupConfigValidator.KnownAgentExecutors.Contains(
                        fb.Provider.Trim().ToLowerInvariant()))
                {
                    errors.Add(
                        $"Role '{roleId}' fallback #{i} references unknown provider '{fb.Provider}'. " +
                        $"Accepted values: {string.Join(", ", StartupConfigValidator.KnownAgentExecutors)}.");
                }
            }
        }

        if (role.FallbackOn is { Count: > 0 })
        {
            if ((role.Fallbacks is null or { Count: 0 }))
            {
                errors.Add(
                    $"Role '{roleId}' has fallbackOn set but no fallbacks declared. The set would " +
                    "never apply — either add fallbacks or remove fallbackOn.");
            }
        }
    }
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

            ValidateRoleFallback(roleId, role, errors);
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

            // Legacy state-level shape rejection (v0.0.25+).
            // Pre-v0.0.25 a state could declare a single role + taskPrompt at
            // the state level and the runtime auto-normalised it into a
            // one-element steps array. The legacy shape is gone — every
            // agent_run state must declare a `steps` array. We surface the
            // error regardless of `gateType` because operators sometimes leave
            // these fields set on a state they later flipped to a different
            // gate type, and silent accept is the wrong behaviour.
            if (state.Role is { Length: > 0 })
                errors.Add(
                    $"State '{stateId}' ({state.Name}) has a top-level 'role' field. " +
                    "This legacy shape was removed in v0.0.25 — declare a 'steps' array instead. " +
                    "Move the role into a step entry: \"steps\": [{ \"name\": \"<step>\", \"role\": \"" + state.Role + "\", ... }].");

            if (state.TaskPrompt is { Length: > 0 })
                errors.Add(
                    $"State '{stateId}' ({state.Name}) has a top-level 'taskPrompt' field. " +
                    "This legacy shape was removed in v0.0.25 — declare a 'steps' array with the prompt on a step entry instead.");

            if (state.TaskPromptFile is { Length: > 0 })
                errors.Add(
                    $"State '{stateId}' ({state.Name}) has a top-level 'taskPromptFile' field. " +
                    "This legacy shape was removed in v0.0.25 — declare a 'steps' array with the prompt file on a step entry instead.");

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
                    // No legacy fallback: every agent_run state needs a steps array.
                    errors.Add(
                        $"State '{stateId}' ({state.Name}) is agent_run but has no 'steps' array. " +
                        "Declare at least one step: \"steps\": [{ \"name\": \"<step>\", \"role\": \"<role>\", \"taskPromptFile\": \"<path>\" }].");
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

            // ── candidate / evaluator / slots validation ─────────────────────
            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    ValidateStepSlots(stateId, state, step, config, errors);
                }
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

        if (config.DependencyPolicy is { } dep)
        {
            if (dep.EnforcedStates is { Count: > 0 })
            {
                foreach (var stateName in dep.EnforcedStates)
                {
                    if (!config.States.Any(kvp =>
                            string.Equals(kvp.Value.Name, stateName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(config.GetEffectiveColumn(kvp.Key), stateName, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"dependencyPolicy.enforcedStates contains '{stateName}' which is not a workflow state name or effective column.");
                    }
                }
            }

            if (dep.SatisfiedColumns is { Count: > 0 })
            {
                // Accept either a workflow state name or an effective column —
                // symmetric with the enforcedStates check above. The runtime
                // ultimately compares against the blocker's column on the board,
                // so a state-name entry only "works" when its effective column
                // equals the state name (the common 1:1 case).
                foreach (var column in dep.SatisfiedColumns)
                {
                    if (!config.States.Any(kvp =>
                            string.Equals(kvp.Value.Name, column, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(config.GetEffectiveColumn(kvp.Key), column, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"dependencyPolicy.satisfiedColumns contains '{column}' which is not a known workflow state name or effective column.");
                    }
                }
            }
        }

        if (validatePolling)
            ValidatePollingConfig(config, errors);

        ValidateRerunConfig(config, errors);

        return errors;
    }

    /// <summary>
    /// Comment kinds known to the rerun-redesign retention map. Unknown keys in
    /// <c>rerun.comments.retentionPolicy</c> are surfaced as a soft warning by
    /// <see cref="Audit"/> so operator typos don't silently fall through to the
    /// hardcoded default in <see cref="Processing.CommentRouter.DefaultFor"/>.
    /// </summary>
    internal static readonly HashSet<string> KnownRerunCommentKinds = new(StringComparer.Ordinal)
    {
        "step", "candidate", "evaluator", "gate", "optional", "cache_hit",
        "dependency_blocked", "completion_progress", "rate_limit_notice",
        "shutdown_notice", "cross_card_notification",
        "created_ticket_dedupe",
    };

    /// <summary>
    /// Retention policy values accepted by <see cref="Processing.CommentRouter.ResolveRetention"/>.
    /// Anything else falls through to the hardcoded default and would be
    /// silently ignored at runtime — promote to a hard error at validation.
    /// </summary>
    private static readonly HashSet<string> KnownRerunRetentionPolicies = new(StringComparer.Ordinal)
    {
        "append", "delete_and_repost", "upsert",
    };

    /// <summary>
    /// Hard errors only — bad values that would silently fall through at runtime.
    /// Soft warnings (unknown kind keys, etc.) are emitted via <see cref="Audit"/>.
    /// </summary>
    private static void ValidateRerunConfig(WorkflowConfig config, List<string> errors)
    {
        if (config.Rerun is not { } rerun) return;

        // defaultBranch: null = auto-detect; empty/whitespace string = misconfigured.
        if (rerun.DefaultBranch is not null && string.IsNullOrWhiteSpace(rerun.DefaultBranch))
        {
            errors.Add("rerun.defaultBranch is set to an empty/whitespace string. Use null to auto-detect via 'git symbolic-ref --short refs/remotes/origin/HEAD', or set a non-empty branch name.");
        }

        // diff.summaryThresholdBytes: must be a positive byte count when set.
        // The runtime divides on this value, and a non-positive threshold would
        // make the summary-mode branch fire on every diff (or never).
        if (rerun.Diff is { SummaryThresholdBytes: { } threshold } && threshold <= 0)
        {
            errors.Add($"rerun.diff.summaryThresholdBytes must be a positive byte count; got {threshold}. Defaults to 51200 (50 KB) when omitted.");
        }

        // comments.retentionPolicy values: typos here silently fall through to
        // CommentRouter.DefaultFor at runtime (with a Warning log buried in
        // polling output). Promote to hard error so operators catch typos
        // pre-flight.
        if (rerun.Comments is { RetentionPolicy: { Count: > 0 } policy })
        {
            foreach (var (kind, raw) in policy)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    errors.Add($"rerun.comments.retentionPolicy['{kind}'] is empty. Set to one of: {string.Join(", ", KnownRerunRetentionPolicies)}.");
                    continue;
                }
                var normalised = raw.Trim().ToLowerInvariant();
                if (!KnownRerunRetentionPolicies.Contains(normalised))
                {
                    errors.Add($"rerun.comments.retentionPolicy['{kind}'] = '{raw}' is not a recognised policy. Expected one of: {string.Join(", ", KnownRerunRetentionPolicies)}.");
                }
            }
        }
    }

    /// <summary>
    /// Per-step validation of candidate / evaluator / slots. Walks both the
    /// legacy step-level <c>Candidates+Evaluator</c> and the new <c>Slots</c>
    /// list via <see cref="WorkflowStep.GetEffectiveSlots"/> so the rules are
    /// uniform across both shapes.
    /// </summary>
    /// <remarks>
    /// Mixing the legacy and new shape (setting both <c>Candidates</c> and
    /// <c>Slots</c> on the same step) is rejected up front — the runtime would
    /// silently prefer one and the user would not notice. Likewise an evaluator
    /// without candidates remains an error.
    /// </remarks>
    private static void ValidateStepSlots(
        string stateId, WorkflowState state, WorkflowStep step,
        WorkflowConfig config, List<string> errors)
    {
        var hasLegacyCandidates = step.Candidates is { Count: > 0 };
        var hasLegacyEvaluator = step.Evaluator is not null;
        var hasSlots = step.Slots is { Count: > 0 };

        // ── shape: legacy vs new ────────────────────────────────────────────
        if (hasSlots && (hasLegacyCandidates || hasLegacyEvaluator))
            errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' sets BOTH 'slots' and the legacy 'candidates'/'evaluator' fields. Use one form: either 'candidates'+'evaluator' (single slot) OR 'slots' (ordered fallback chain).");

        if (!hasLegacyCandidates && hasLegacyEvaluator)
            errors.Add($"State '{stateId}' ({state.Name}) step '{step.Name}' has an evaluator but no candidates. Remove the evaluator or add at least one candidate.");

        // ── per-slot validation (uniform across legacy + new) ───────────────
        var slots = step.GetEffectiveSlots();
        for (var slotIdx = 0; slotIdx < slots.Count; slotIdx++)
        {
            var slot = slots[slotIdx];
            var slotLabel = slots.Count == 1 && !hasSlots
                ? $"step '{step.Name}'"
                : $"step '{step.Name}' slot {slotIdx}";

            if (slot.Candidates is null or { Count: 0 })
            {
                errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} has no candidates. Every slot must have at least one candidate.");
                continue;
            }

            // 2+ candidates require an evaluator (single-candidate slots may
            // omit it — the runtime surfaces the candidate's result directly).
            if (slot.Candidates.Count >= 2 && slot.Evaluator is null)
                errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} has {slot.Candidates.Count} candidates but no evaluator. Multi-candidate slots need an evaluator to pick the winner.");

            // Per-candidate field checks
            for (var ci = 0; ci < slot.Candidates.Count; ci++)
            {
                var cand = slot.Candidates[ci];
                if (string.IsNullOrWhiteSpace(cand.Provider))
                    errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} candidate #{ci} has an empty provider.");

                if (cand.Retries < 0)
                    errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} candidate #{ci} has negative retries={cand.Retries}. Retries must be 0 or greater.");
            }

            // Evaluator validation (when set)
            if (slot.Evaluator is { } ev)
            {
                if (string.IsNullOrWhiteSpace(ev.Role))
                    errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} evaluator has no role.");
                else if (!config.Roles.ContainsKey(ev.Role))
                    errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} evaluator references role '{ev.Role}' which does not exist in Roles.");

                if (string.IsNullOrWhiteSpace(ev.TaskPrompt) && string.IsNullOrWhiteSpace(ev.TaskPromptFile))
                    errors.Add($"State '{stateId}' ({state.Name}) {slotLabel} evaluator has neither taskPrompt nor taskPromptFile.");
            }
        }
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
        AuditCrossProviderCandidateModels(config, warnings);
        AuditFinalSlotRetries(config, warnings);
        AuditRerunCommentKinds(config, warnings);
        return warnings;
    }

    /// <summary>
    /// Soft warning when <c>rerun.comments.retentionPolicy</c> contains a kind
    /// key the runtime doesn't recognise. This is one level below
    /// <see cref="ValidateRerunConfig"/>'s hard errors: typo'd policy values
    /// silently fall through (hard error), but typo'd kind keys never get
    /// consulted at all (soft warning — could be a typo or a future kind).
    /// </summary>
    private static void AuditRerunCommentKinds(WorkflowConfig config, List<string> warnings)
    {
        if (config.Rerun?.Comments?.RetentionPolicy is not { Count: > 0 } policy) return;

        foreach (var kind in policy.Keys)
        {
            if (!KnownRerunCommentKinds.Contains(kind))
            {
                warnings.Add(
                    $"rerun.comments.retentionPolicy contains kind '{kind}' which is not a recognised comment kind. " +
                    $"The override will never apply — emit sites only consult known kinds. Known kinds: {string.Join(", ", KnownRerunCommentKinds.OrderBy(k => k, StringComparer.Ordinal))}.");
            }
        }
    }

    /// <summary>
    /// Warns when a step's final fallback slot has all candidates with zero
    /// retries. A transient outage of that slot's provider(s) (rate limit,
    /// timeout) will surface as a step-level error rather than being absorbed
    /// by retry. The point of fallbacks is graceful degradation; if every slot
    /// is "expensive subscription, no retries" the operator gets paged on
    /// every blip. Worth flagging on review even though the config is valid.
    /// </summary>
    private static void AuditFinalSlotRetries(WorkflowConfig config, List<string> warnings)
    {
        foreach (var (stateId, state) in config.States)
        {
            if (state.Steps is not { Count: > 0 }) continue;

            foreach (var step in state.Steps)
            {
                var slots = step.GetEffectiveSlots();
                if (slots.Count == 0) continue;

                var lastSlot = slots[^1];
                if (lastSlot.Candidates is null or { Count: 0 }) continue;
                if (lastSlot.Candidates.All(c => c.Retries == 0))
                {
                    var slotsCount = slots.Count;
                    var location = slotsCount > 1
                        ? $"step '{step.Name}' final slot (index {slotsCount - 1})"
                        : $"step '{step.Name}' (single slot)";
                    warnings.Add(
                        $"State '{stateId}' ({state.Name}) {location} has all candidates with 0 retries. " +
                        "A transient outage (rate limit, timeout) of this slot's provider(s) will surface " +
                        "as a step-level error with no graceful degradation. Add 'retries' to at least one " +
                        "candidate, or add a cheaper fallback slot, to make this step outage-tolerant.");
                }
            }
        }
    }

    /// <summary>
    /// Warns when a candidate's <c>provider</c> differs from its step role's
    /// configured provider AND the candidate doesn't pin a <c>model</c>.
    /// In that case the runtime will deliberately NOT pass the role's default
    /// model to the candidate's executor — the role default is provider-scoped
    /// and would be nonsense for a different provider (e.g. Codex CLI rejects
    /// <c>--model claude-opus-4-6</c> with "model not supported when using
    /// Codex with a ChatGPT account"). The candidate's executor will fall back
    /// to its own default model, which may not be what the operator wanted.
    /// Pin a model on the candidate to silence this warning.
    /// </summary>
    private static void AuditCrossProviderCandidateModels(WorkflowConfig config, List<string> warnings)
    {
        foreach (var (stateId, state) in config.States)
        {
            if (state.Steps is not { Count: > 0 }) continue;

            foreach (var step in state.Steps)
            {
                if (!config.Roles.TryGetValue(step.Role, out var role)) continue;

                var slots = step.GetEffectiveSlots();
                if (slots.Count == 0) continue;

                for (var slotIdx = 0; slotIdx < slots.Count; slotIdx++)
                {
                    var slot = slots[slotIdx];
                    if (slot.Candidates is null or { Count: 0 }) continue;

                    var slotLabel = slots.Count == 1
                        ? $"step '{step.Name}'"
                        : $"step '{step.Name}' slot {slotIdx}";

                    for (var i = 0; i < slot.Candidates.Count; i++)
                    {
                        var candidate = slot.Candidates[i];
                        if (string.IsNullOrEmpty(candidate.Provider)) continue;
                        if (string.Equals(candidate.Provider, role.Provider, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrWhiteSpace(candidate.Model)) continue;

                        warnings.Add(
                            $"State '{stateId}' ({state.Name}) {slotLabel} candidate #{i} uses provider " +
                            $"'{candidate.Provider}' on role '{step.Role}' (role.provider='{role.Provider}', " +
                            $"role.model='{role.Model}'). The role's default model will NOT be passed to the " +
                            $"candidate's executor (it would mis-route across providers — e.g. a Claude model name " +
                            $"on a Codex executor). The candidate's executor will use its own default. " +
                            $"Set 'model' on the candidate to pin a specific model for this provider.");
                    }
                }
            }
        }
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

            // Legacy single-role state-level audit removed in v0.0.25:
            // the legacy shape is now a hard validator error, so audit
            // doesn't need a side path for it.
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
