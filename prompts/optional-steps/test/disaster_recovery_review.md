Perform a disaster recovery and failure mode test review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on failure mode and disaster recovery test coverage:

1. **Service outage handling**: Are tests verifying graceful behavior when dependencies are unavailable?
2. **Data recovery tests**: Is recovery from data corruption or loss verified?
3. **Failover tests**: If high-availability is required, is failover behavior tested?
4. **Backup and restore tests**: Are backup creation and restoration procedures tested?
5. **Partial failure tests**: Are scenarios where some (but not all) operations succeed handled correctly?
6. **Timeout and cancellation tests**: Are long-running operations tested with timeouts and cancellation?
7. **Idempotent retry tests**: For operations with retry logic, is idempotency under retry tested?

## Instructions

- Read the test files and implementation in the workspace.
- For each gap, describe the failure scenario lacking coverage and the operational risk.
- If disaster recovery test coverage is adequate, return COMPLETE with a summary of what was verified.
