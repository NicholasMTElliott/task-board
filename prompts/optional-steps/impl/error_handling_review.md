Perform an error handling review of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on error handling completeness and correctness:

1. **Exception coverage**: Are all exception types that can be thrown handled appropriately?
2. **User-facing error messages**: Are error messages informative for users but free of internal details?
3. **Retry logic**: For transient failures (network, throttling), is retry with backoff implemented?
4. **Graceful degradation**: When a non-critical dependency fails, does the system degrade gracefully?
5. **Error propagation**: Are errors propagated correctly through async call chains? No swallowed exceptions?
6. **Error logging**: Are errors logged with sufficient context (correlation ID, user context, input data)?
7. **Circuit breakers**: For external service calls, are circuit breakers used to prevent cascade failures?

## Instructions

- Read all changed files in the workspace.
- For each finding, cite the file path, line number, and the specific error handling gap.
- If no error handling concerns found, return COMPLETE with a brief summary of what was verified.
