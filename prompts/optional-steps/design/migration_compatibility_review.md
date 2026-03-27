Perform a migration and backward compatibility review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on migration safety and backward compatibility concerns:

1. **Breaking changes**: What existing behavior, APIs, or data formats does this change incompatibly?
2. **Client impact**: Are there API consumers, mobile apps, or integrations that must be updated?
3. **Database migration safety**: Can schema changes be applied without data loss or downtime?
4. **Rollback plan**: If the migration fails, can it be safely rolled back? Is data recoverable?
5. **Feature flags**: Should this be behind a feature flag to allow gradual rollout?
6. **Deprecation timeline**: If existing behavior is being deprecated, is the timeline clear?
7. **Version coexistence**: Do old and new versions need to run simultaneously during migration?

## Instructions

- Read the design document in the workspace.
- For each compatibility risk, specify the affected component and the migration strategy needed.
- If no migration or compatibility concerns found, return COMPLETE with a brief summary of what was verified.
