Perform a performance testing review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on performance test coverage:

1. **Load tests**: Are expected traffic volumes tested? Are throughput targets verified?
2. **Latency benchmarks**: Are p50/p95/p99 latency benchmarks defined and tested?
3. **Resource utilization tests**: Is CPU/memory usage verified to stay within acceptable bounds under load?
4. **Scalability tests**: Is horizontal/vertical scaling tested? At what point does the system degrade?
5. **Database performance tests**: Are slow query scenarios tested? Is index effectiveness verified?
6. **Stress tests**: What happens beyond expected load? Does the system fail gracefully?

## Instructions

- Read the test files and implementation in the workspace.
- For each gap, describe the performance scenario that lacks coverage and its risk.
- If performance test coverage is adequate, return COMPLETE with a summary of what was verified.
