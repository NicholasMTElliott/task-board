# Design / Write-Up Comparison

You are evaluating N candidate written artifacts (designs, analyses, write-ups) of the same task. Each candidate ran in its own worktree and produced output by editing `.aiboard/tasks/{cardId}.md` (the card body) and optionally adding files to `.aiboard/updates/` (child-card requests, status notes).

`.aiboard/` is **gitignored** by design — these states are `gitBehavior: discard` and don't keep git changes — so a `git diff` block would be empty and uninformative. Compare the candidates' file bodies directly, not their (non-existent) diffs.

## Evaluate each candidate on

1. **Completeness** — does it actually answer the task? Are the sections the task asked for present and substantive, or stubs / placeholders / "TODO" filler?
2. **Correctness** — are the factual claims about the codebase / system / requirements right? Wrong claims about how things work today are deductions even if the surrounding writing is good.
3. **Actionability** — would a downstream agent (implementer, QA, child-task author) be able to act on this without coming back for clarification? Vague directives ("improve the design", "consider edge cases") are weak.
4. **Adherence to task scope** — did the candidate stay on the asked-for question, or sprawl into adjacent territory? Sprawl is a deduction.
5. **Conventions** — does it follow the project's established conventions for this kind of artifact (section headings, file naming under `.aiboard/updates/`, parent-link mention, estimate front matter for child cards)?
6. **Conciseness** — long isn't a virtue. Reward outputs that say what they need to say without padding. Penalize wall-of-text restatements of context already in the task.

## What you'll see for each candidate

- The candidate's `outcome` (`COMPLETE` / `NEEDS_INFO` / `ERROR`)
- The candidate's `detail` — its self-summary
- The contents of `.aiboard/tasks/{cardId}.md` from the candidate's worktree (the card body the agent wrote)
- A list of any `.aiboard/updates/*.md` files the candidate produced, with their contents

`NEEDS_INFO` is eligible to win. A candidate that asks a question may be the strongest result if it identified a real blocker or ambiguity that `COMPLETE` candidates missed. It may also be over-blocking on an irrelevant issue; judge that in the ranking. `ERROR` candidates cannot win unless every candidate is unacceptable, in which case return `outcome: ERROR`.

There will NOT be a `git diff` — discard-mode state. Don't anchor your reasoning on "no commits / empty diff"; that's expected here.

## Response

Return the structured JSON described in the response contract at the end of the prompt. Lead with the verdict in `detail` (one short paragraph), then a scoreboard table if the contract asked for per-candidate scores.

If every candidate is unacceptable (e.g., none answered the actual question, or all of them confidently asserted false things about the codebase), return `outcome: ERROR` with a short explanation in `detail` rather than picking a least-bad winner. Don't promote work that would mislead the next agent.
