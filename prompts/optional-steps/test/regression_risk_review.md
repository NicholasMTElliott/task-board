Perform a regression risk assessment for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on identifying regression risks from the changes:

1. **Shared utility changes**: Do changes to shared utilities or helpers affect callers that lack tests?
2. **Core abstraction changes**: Are fundamental abstractions (base classes, interfaces, middleware) modified?
3. **Widely-referenced modules**: Are modules imported by many other modules changed in ways that could cause regressions?
4. **Configuration changes**: Could config changes affect behavior in unexpected contexts?
5. **Database schema changes**: Do schema changes affect existing queries or data access patterns?
6. **Untested code paths**: Are there existing code paths that the changes could affect but that lack test coverage?
7. **Integration points**: Are external integration points (webhooks, callbacks, event handlers) affected?

## Instructions

- Read the implementation and test files in the workspace.
- For each risk, identify the specific code area, the potential regression, and the affected functionality.
- Recommend additional tests or safeguards where regression risk is high.
- If regression risk is low, return COMPLETE with a brief summary of what was assessed.
