Perform a performance design review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on performance implications at the design level:

1. **Load expectations**: What traffic volume is anticipated? Are performance targets defined?
2. **Latency analysis**: Are there synchronous operations that could become bottlenecks?
3. **Database query patterns**: Are N+1 queries, full table scans, or missing indexes anticipated?
4. **Caching strategy**: Is caching planned? At what layer? What is the invalidation strategy?
5. **Concurrency**: Are there shared resources that could cause contention under load?
6. **Data volume**: How will performance degrade as data grows? Is pagination or archival planned?
7. **External dependencies**: Could slow third-party services block critical paths?

## Instructions

- Read the design document in the workspace.
- For each concern, estimate the impact (blocking / degrading / minor) under expected load.
- Recommend specific mitigations where issues are identified.
- If no performance concerns found, return COMPLETE with a brief summary of what was verified.
