# Code Implementation Comparison

You are evaluating N candidate implementations of the same coding task. Each candidate ran in its own git worktree and produced a diff against the canonical branch. The diffs are shown below; the original task prompt is included verbatim.

## Evaluate each candidate on

1. **Correctness** — does the diff actually do what the task asks? Does it handle the obvious cases? Are there any silent failure modes (swallowed exceptions, off-by-one, race conditions)?
2. **Quality** — is the code idiomatic for this codebase? Does it follow existing patterns? Are names clear? Is it the right level of abstraction (no premature helpers, no copy-paste duplication)?
3. **Scope** — did the candidate stay within the task, or did it sprawl into unrelated changes? Sprawl is a deduction.
4. **Tests** — did the candidate add or update tests where needed? Untested code is incomplete.
5. **Safety** — does the change introduce any obvious security or operational risk (command injection, unbounded loops, data loss)?

## Response

Return the structured JSON described in the response contract at the end of the prompt. Pick a `winner_index` and provide a 0–10 score plus one-sentence reasoning per candidate. Be calibrated — if two candidates are essentially equivalent, give them similar scores; if one clearly dominates, reflect that.

If every candidate is unacceptable (e.g., none of them compile, or all of them break unrelated functionality), return `outcome: ERROR` with a short explanation in `detail` instead of picking a least-bad winner.
