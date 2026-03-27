Perform a performance review of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on performance concerns in the code changes:

1. **N+1 queries**: Are database queries being made inside loops? Are batch or eager-load alternatives available?
2. **Unnecessary allocations**: Are objects, strings, or collections allocated in hot paths when they could be reused?
3. **Blocking I/O**: Is synchronous I/O (file reads, HTTP calls) performed where async is possible?
4. **Missing indexes**: Do new queries use columns that lack database indexes?
5. **Cache effectiveness**: Are expensive operations cached? Is cache invalidation correct?
6. **Algorithmic complexity**: Are there O(n²) or worse algorithms in performance-sensitive paths?
7. **Connection pooling**: Are database/HTTP connections properly pooled and released?

## Instructions

- Read all changed files in the workspace.
- For each finding, cite the file path and line number, describe the performance risk, and estimate impact.
- If no performance concerns found, return COMPLETE with a brief summary of what was verified.
