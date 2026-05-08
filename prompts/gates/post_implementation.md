Verify the implementation produced for task '{TaskName}' ({TaskId}).

## Original Task Requirements

{TaskBody}

## Code Changes (diff)

{Diff}

## Agent Self-Report

{AgentReport}

## Verification Checklist

1. Does the diff contain changes for every requirement in the task?
2. Does the diff contain changes not described in the requirements?
3. Are there any obvious issues visible in the diff (syntax errors, placeholder
   code, TODO comments for required features, missing imports)?
4. Does the diff contain test coverage for new or changed requirements?

## When the diff is in Summary Mode

If the section above starts with `## Summary mode (large diff)`, the raw diff
exceeded the configured threshold and you are seeing a structured packet: a
per-file change table, the top files by churn shown inline, and an omitted-file
list. Treat this as the source of truth; the agent's self-report covers the
rest.

- Judge based on the visible files, line counts, and the agent's narrative.
- Do **not** return a generic "diff truncated, cannot verify" rejection.
- Only return `NEEDS_INFO` or `ERROR` when the visible evidence is genuinely
  insufficient for the verification checklist — and when you do, name the
  specific files or evidence you would need.
