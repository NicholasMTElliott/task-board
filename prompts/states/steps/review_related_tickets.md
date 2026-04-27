You are working on the task '{TaskName}' ({TaskId}). Your job in this step is **board-only cross-reference analysis** — read the other tickets on the board, identify relationships to this one, and document them. The next step (design) is where codebase analysis happens; this step is not.

If a conversation history file exists for this task, read it first.

## What "related" means here

A ticket is related to this one when **its text** (not its code) describes:
- **Dependency** — this work needs that work first, or vice versa.
- **Overlap** — both tickets propose changes to the same component / API / data model. Risk: merge conflicts or duplicated effort.
- **Constraint** — a design decision in another ticket fences in what's allowed here.
- **Sequencing** — both tickets touch the same area; ordering matters.
- **Spillover** — a decision made here will affect another ticket that's not yet aware of it.

If you can't identify the relationship from the OTHER ticket's text alone — without opening source files or running anything — it doesn't belong in this analysis. Surface unknowns as questions for the design step instead.

## What to do

1. Read this ticket's task file (`.aiboard/tasks/{TaskId}.md`).
2. Read every other task file in `.aiboard/tasks/` (and any subdirectories of related-card folders if cross-references already exist).
3. For each candidate related ticket, ask: does this ticket's TEXT name something this one's text also names (component, system area, API, data model, file path, ticket number)? If yes, document the relationship. If no, skip it.
4. Write findings into this ticket's task file under a `## Related Ticket Analysis` section. For each related ticket: card number + title, nature of the relationship, specific impact area as named in the other ticket's text, risk or constraint for the upcoming design.

## What this step is NOT

- Not a code review.
- Not a design.
- Not a feasibility study.
- Not a "let me run the tests / build / launch the app to see what this ticket touches" exercise. The next step has that scope; this one does not.

A typical run of this step takes **under a minute** for a small board and a few minutes for a board with dozens of tickets. If you're spending time looking at source code or executing anything, you've drifted out of scope — wrap up your findings from board text and stop.

## Cross-references

When this ticket genuinely depends on or is significantly affected by another card, add a reference using the card number (e.g. `#5`, `#12`) so the orchestrator pulls that card's context into later phases. Only reference cards where the relationship is meaningful.

## Outcome

- `COMPLETE` — you reviewed the available tickets and documented findings (even if there are no related tickets, that's a valid finding).
- `NEEDS_INFO` — a ticket TEXT raises a cross-cutting question only the operator can resolve before the next step proceeds (e.g., two tickets propose contradictory changes to the same field; the design step can't pick without input).
- `ERROR` — only if you cannot read `.aiboard/tasks/` at all, or hit a blocking technical problem.
