Verify the decomposition produced for story '{TaskName}' ({TaskId}).

This is a **decomposition gate**: the prior step(s) were supposed to break this story into child tickets. Your job is to confirm that the work was actually done — child tickets exist on the board — and that the decomposition is reasonable.

## Original Story Requirements

{TaskBody}

## Tickets Created During This Run

{CreatedTickets}

## Update File Processing

{UnrecognizedFiles}

## Dependency Relationship Updates

{DependencyUpdates}

## Agent Self-Report

{AgentReport}

## Verification Checklist

1. **Were tickets actually created?** Check the "Tickets Created During This Run" list above. If the list says `_(no tickets created during this run)_` and the agent's self-report claims it produced tasks, something failed silently. Look at "Update File Processing" — if it shows skipped files (likely missing the `new-` prefix), this is a `GATE_FAIL` (or ERROR) and the operator must re-run after fixing the prompt.
2. **Coverage** — does the decomposition cover the story's acceptance criteria? If the story's requirements obviously demand work that no created ticket addresses, this is a gap.
3. **Scope** — are any of the created tickets clearly outside the story's scope? Cross-cutting concerns or unrelated tech debt should not have been created here.
4. **Duplication** — within the list, do any two tickets describe the same work with different titles? If yes, flag.
5. **Dependency sequencing** — obvious hard blockers from the story/body should be encoded as native dependency relationships. If the dependency section reports failed writes, this is an ERROR unless the requested relationship is clearly invalid.
6. **Self-report consistency** — does the count and titles in "Tickets Created" match the self-report? Mismatches indicate either a parser issue or an agent miscount.

## Output

- Return `outcome=COMPLETE` if all checks pass.
- Return `outcome=COMPLETE` with `requestedSteps` populated if the catalog has reviews that would catch issues you spotted (e.g. you suspect scope creep — request a scope review).
- Return `outcome=NEEDS_INFO` if there's a question only the operator can answer (rare — the operator already wrote the story).
- Return `outcome=ERROR` if "Tickets Created" is empty but the self-report says tickets were created (silent failure mode), OR if the "Update File Processing" section flags rejected files that should have been new-ticket files, OR if required dependency relationship updates failed.

Be brief — the verdict should be 5–10 lines. Do not re-list the tickets in your output; the operator can read them above.
