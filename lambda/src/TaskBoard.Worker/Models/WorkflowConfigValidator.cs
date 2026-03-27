namespace TaskBoard.Worker.Models;

public static class WorkflowConfigValidator
{
    public static List<string> Validate(WorkflowConfig config, bool validatePolling = false)
    {
        var errors = new List<string>();

        if (config.States.Count == 0)
            errors.Add("States dictionary must not be empty.");

        if (config.Roles.Count == 0)
            errors.Add("Roles dictionary must not be empty.");

        foreach (var (stateId, state) in config.States)
        {
            if (string.Equals(state.GateType, "agent_run", StringComparison.OrdinalIgnoreCase))
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

            foreach (var (outcome, targetStateId) in state.Transitions)
            {
                if (!config.States.ContainsKey(targetStateId))
                    errors.Add($"State '{stateId}' ({state.Name}) transition '{outcome}' targets '{targetStateId}' which does not exist in States.");
            }
        }

        // Note: Sections can be empty for roles that don't write to card body
        // (e.g., gate_checker, code_reviewer — they produce comments only)

        if (validatePolling)
            ValidatePollingConfig(config, errors);

        return errors;
    }

    private static void ValidatePollingConfig(WorkflowConfig config, List<string> errors)
    {
        var runnableStates = config.States
            .Where(kvp => kvp.Value.GateType is "agent_run" or "system_merge")
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
