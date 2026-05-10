# Gate Check Comparison

You are evaluating N candidate gate-check verdicts on the same agent output. Each candidate read the same step output and returned its own pass/fail decision. Your job is to pick the candidate whose verdict is most calibrated.

## Evaluate each candidate on

1. **Calibration** — does the verdict match the actual quality of the underlying work? A "PASS" on broken work is a serious miss; a "FAIL" on solid work is overcautious.
2. **Reasoning quality** — when the candidate flags concerns, are the concerns specific and actionable, or vague nits?
3. **Conciseness** — gate checks should be terse. Wall-of-text reasoning is a deduction unless the underlying work warranted it.

`NEEDS_INFO` is eligible to win. A candidate that asks a question may be the strongest result if it identified a real blocker or ambiguity that `COMPLETE` candidates missed. It may also be over-blocking on an irrelevant issue; judge that in the ranking. `ERROR` candidates cannot win unless every candidate is unacceptable, in which case return `outcome: ERROR`.

## Response

Return the structured JSON described in the response contract. The "winner" is the candidate whose verdict you'd trust most for routing decisions. Score 0–10 with calibrated honesty: if the candidates agree and are both reasonable, scores should be close.

If every candidate's verdict seems wrong (e.g., they all missed an obvious issue), return `outcome: ERROR` rather than picking a least-bad option.
