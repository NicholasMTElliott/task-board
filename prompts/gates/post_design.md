Verify the technical design produced for task '{TaskName}' ({TaskId}).

## Original Task Requirements

{TaskBody}

## Design Output

{Diff}

## Tickets Created During This Run

{CreatedTickets}

## Dependency Relationship Updates

{DependencyUpdates}

## Agent Self-Report

{AgentReport}

## Verification Checklist

1. Does the design address every requirement listed in the task?
2. Does the design introduce scope not present in the requirements?
3. Are there any obvious gaps (e.g., requirements mentioned but not designed for)?
4. If the design or cross-ticket review discovered hard sequencing constraints, were they encoded in "Dependency Relationship Updates"?
5. If this state was supposed to produce child tickets (decomposition / generation), does the "Tickets Created" list above match what the agent's self-report claims? An empty list when the self-report claims tickets were made is a real failure — the agent may have written files without the required `new-` prefix or the orchestrator may have rejected them.

## When the design output is in Summary Mode

If the section above starts with `## Summary mode (large diff)`, the raw output
exceeded the configured threshold and you are seeing a structured packet: a
per-file change table, the top files by churn shown inline, and an omitted-file
list. Decide based on the visible files plus the agent's self-report. Do not
reject solely because the full output is not present; only return `NEEDS_INFO`
or `ERROR` when the visible evidence is genuinely insufficient — and name the
specific files or evidence you would need.
