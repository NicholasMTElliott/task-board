Perform an observability design review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on logging, monitoring, alerting, and debugging concerns:

1. **Structured logging**: Are log events well-structured? Do they include enough context to diagnose issues?
2. **Metrics**: What metrics should be emitted? Are SLIs/SLOs defined for this feature?
3. **Distributed tracing**: If the feature spans services, is trace propagation planned?
4. **Alerting**: What conditions should trigger alerts? Are alert thresholds defined?
5. **Dashboards**: What operational dashboards are needed to monitor this feature in production?
6. **Error observability**: Are errors surfaced with enough context to diagnose root cause?
7. **Debugging support**: Can a developer diagnose issues in production without code changes?

## Instructions

- Read the design document in the workspace.
- For each gap, specify what observability artifact is missing and why it matters.
- If no observability concerns found, return COMPLETE with a brief summary of what was verified.
