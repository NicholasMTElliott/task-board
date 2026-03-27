Perform a data modeling review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on data model and storage design concerns:

1. **Schema design**: Are entities, relationships, and cardinalities correctly modeled?
2. **Normalization**: Is the schema appropriately normalized? Are there redundancies or anomalies?
3. **Migration safety**: Can schema changes be applied without downtime? Is rollback possible?
4. **Data flow**: How does data move through the system? Are transformation steps well-defined?
5. **Storage strategy**: Is the chosen storage type (relational, document, key-value, etc.) appropriate?
6. **Indexing strategy**: Are indexes planned for expected query patterns?
7. **Data lifecycle**: Retention, archival, and deletion policies.

## Instructions

- Read the design document in the workspace.
- Reference specific data entities or schema decisions in your findings.
- Flag any design decisions that could cause migration complexity or data integrity issues.
- If no data modeling concerns found, return COMPLETE with a brief summary of what was verified.
