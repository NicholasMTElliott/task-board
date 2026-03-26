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

            foreach (var (outcome, targetStateId) in state.Transitions)
            {
                if (!config.States.ContainsKey(targetStateId))
                    errors.Add($"State '{stateId}' ({state.Name}) transition '{outcome}' targets '{targetStateId}' which does not exist in States.");
            }
        }

        foreach (var (roleId, role) in config.Roles)
        {
            if (role.Sections.Count == 0)
                errors.Add($"Role '{roleId}' has an empty Sections list.");
        }

        if (validatePolling)
            ValidatePollingConfig(config, errors);

        return errors;
    }

    private static void ValidatePollingConfig(WorkflowConfig config, List<string> errors)
    {
        var agentRunStates = config.States
            .Where(kvp => string.Equals(kvp.Value.GateType, "agent_run", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var (stateId, state) in agentRunStates)
        {
            if (state.PipelineOrder <= 0)
                errors.Add($"State '{stateId}' ({state.Name}) is agent_run but has no pipelineOrder (must be > 0 for polling mode).");
        }

        var duplicateOrders = agentRunStates
            .Where(kvp => kvp.Value.PipelineOrder > 0)
            .GroupBy(kvp => kvp.Value.PipelineOrder)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in duplicateOrders)
        {
            var names = string.Join(", ", group.Select(kvp => $"'{kvp.Key}'"));
            errors.Add($"Duplicate pipelineOrder {group.Key} on agent_run states: {names}.");
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
