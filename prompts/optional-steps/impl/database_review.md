Perform a database review of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on database and schema concerns:

1. **Migration safety**: Is the migration script additive? Can it be rolled back safely? Avoids locking large tables?
2. **Query correctness**: Do queries correctly express the intended logic? Are edge cases handled?
3. **Index strategy**: Are new queries using indexed columns? Will existing indexes still be used efficiently?
4. **Transaction handling**: Are multi-step operations wrapped in transactions where needed? Deadlock risk?
5. **Data integrity constraints**: Are foreign keys, unique constraints, and check constraints correctly applied?
6. **SQL injection prevention**: Are all queries parameterized? No string concatenation in queries?
7. **Connection management**: Are connections properly opened and released? No connection leaks?

## Instructions

- Read all changed database-related files (migrations, repositories, queries) in the workspace.
- For each finding, cite the file path and line number.
- If no database concerns found, return COMPLETE with a brief summary of what was verified.
