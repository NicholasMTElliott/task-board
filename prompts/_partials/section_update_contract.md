<!--
Source-of-truth for the Section Update Contract guidance inlined into each
agent role's system prompt. This file is NOT loaded by the orchestrator at
runtime — agents only read their own role's prompt file. Maintainers: when
this snippet changes, copy the new text into every prompt that includes
"## Section Update Contract" so all writer/non-writer roles stay aligned.
-->

# Writer-role variant (main steps + optional reviewers)

## Section Update Contract

Your structured response includes a `section_update` field that drives the card's "current truth" managed description section. This is separate from `detail` (which goes into a chronological comment). `section_update` writes / replaces / leaves your step's named section on the card.

Use `section_update.strategy`:
- `replace` — your section's content is wholesale replaced with the value of `content`. Use this on the first run, or when refined understanding supersedes the previous iteration.
- `leave` — your section is not modified. Use this only when re-running and you have nothing to add: the prior section is still accurate.
- `append_with_revision_notes` — same as `replace`, but `content` should include a brief revision-notes preamble explaining what changed and why (e.g. "Updated after operator clarified scope: removed Postgres option, kept MySQL").

Fill these fields when `strategy` is `replace` or `append_with_revision_notes`:
- `content` — markdown body of YOUR step's section. Concise, polished current truth — not a journal. Do NOT write run logs or "Run 3: no new info / Run 4: still no new info." Length matches the value: short for "no actions needed" cases, fuller when meaningful new information.
- `open_questions` — array of strings, each a single open question for the operator. The orchestrator renders these into a marker-wrapped `### Open Questions` subsection. Operators may reply inline or via comment.
- `resolved_decisions` — array of strings, each a durable decision worth preserving across iterations (e.g. "Postgres chosen over MySQL: index requirements met"). Rendered into a `### Resolved Decisions` subsection.

When `strategy` is `leave`:
- Omit `content`, `open_questions`, and `resolved_decisions` (or set them null/empty).
- The first time a step ever writes to a card, a `leave` is automatically coerced to a placeholder so future runs have something to compare against — but your operator-visible signal is still that you found nothing new.

What goes where:
- `section_update.content` — current truth for THIS step's section. Polished, concise. Not run-by-run history.
- `detail` — your full reasoning, including run-by-run history, intermediate exploration, and decisions that didn't make it into the final answer. Lives in a chronological comment.
- top-level `questions` — same questions as `section_update.open_questions`. Repeating them at the top level surfaces them in the comment timeline AND in the description.

# Non-writer-role variant (gates, merge resolver)

## Section Update Contract

You do **not** write a description section. Set `section_update.strategy` to `leave` and omit the other section fields. Your verdict goes in `detail` (rendered as a chronological comment). The orchestrator will not apply your section update.

# Evaluator variant

## Section Update Contract

You do **not** write your own description section. Set `section_update.strategy` to `leave` and omit the other section fields. The orchestrator separately applies the **winning candidate's** `section_update` on your behalf — that happens automatically once you set `winner_index`. Do not echo, summarize, or re-invent the winner's section update; just pick the winner.
