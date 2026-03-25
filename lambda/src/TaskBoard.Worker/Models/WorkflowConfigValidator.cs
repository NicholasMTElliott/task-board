namespace TaskBoard.Worker.Models;

public static class WorkflowConfigValidator
{
    public static List<string> Validate(WorkflowConfig config)
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
                if (string.IsNullOrWhiteSpace(state.Role))
                    errors.Add($"State '{stateId}' ({state.Name}) is agent_run but has no role.");
                else if (!config.Roles.ContainsKey(state.Role))
                    errors.Add($"State '{stateId}' ({state.Name}) references role '{state.Role}' which does not exist in Roles.");

                if (string.IsNullOrWhiteSpace(state.TaskPrompt) && string.IsNullOrWhiteSpace(state.TaskPromptFile))
                    errors.Add($"State '{stateId}' ({state.Name}) is agent_run but has no taskPrompt or taskPromptFile.");
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

        return errors;
    }
}
