Perform an integration and end-to-end test review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on integration and end-to-end test coverage:

1. **External service interactions**: Are calls to external APIs tested with realistic responses, including failure modes?
2. **Cross-component flows**: Are the boundaries between components (API → service → database) tested end-to-end?
3. **Event-driven flows**: If events or messages are involved, are publish/consume scenarios tested?
4. **Failure scenario coverage**: Are network timeouts, service unavailability, and partial failures tested?
5. **Contract tests**: For service boundaries, are consumer-driven or provider-driven contract tests present?
6. **Database integration**: Are tests running against a real database (or realistic substitute) rather than mocks?

## Instructions

- Read the test files and implementation in the workspace.
- For each gap, describe the integration scenario lacking coverage and its risk.
- If integration test coverage is adequate, return COMPLETE with a summary of what was verified.
