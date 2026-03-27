Perform a data integrity and validation test review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on data integrity test coverage:

1. **Constraint enforcement tests**: Are database constraints (foreign keys, unique, not-null) tested to reject invalid data?
2. **Referential integrity tests**: Are cascading deletes and updates tested? Are orphaned records prevented?
3. **Migration rollback tests**: Is the migration script's rollback path tested?
4. **Data transformation tests**: Are data conversion/mapping operations verified for correctness and edge cases?
5. **Concurrency integrity**: Are concurrent writes tested for race conditions that could corrupt data?
6. **Data corruption scenarios**: Are tests verifying recovery from partial writes or interrupted transactions?
7. **Audit trail integrity**: Are audit log entries tested for completeness and accuracy?

## Instructions

- Read the test files and migration scripts in the workspace.
- For each gap, describe the data integrity scenario lacking coverage and the risk.
- If data integrity test coverage is adequate, return COMPLETE with a summary of what was verified.
