Perform an edge case and boundary condition test review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on edge case and boundary condition test coverage:

1. **Null and empty inputs**: Are null values, empty strings, empty collections, and zero handled?
2. **Boundary values**: Are minimum and maximum values tested (off-by-one errors, int overflow)?
3. **Concurrent access**: Are race conditions and concurrent modification scenarios tested?
4. **Large inputs**: Are very large payloads, files, or data sets tested?
5. **Special characters**: Are inputs with Unicode, escaped characters, or control characters handled?
6. **State machine transitions**: Are invalid state transitions rejected? Are all valid paths exercised?
7. **Idempotency**: For operations that should be idempotent, is duplicate execution tested?

## Instructions

- Read the test files and implementation in the workspace.
- For each gap, describe the edge case lacking coverage and the failure mode it could cause.
- If edge case coverage is thorough, return COMPLETE with a summary of what was verified.
