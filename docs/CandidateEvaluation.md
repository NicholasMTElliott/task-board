# Multi-Agent Candidate Evaluation

Run a step with N agents in parallel against the same task, then have an evaluator pick a winner. The losers get discarded; the winner's branch is promoted into the canonical workflow. Per-(role, provider) win-rate and quality-score metrics build up over time so you can decide whether a free local model (e.g. Qwen via OpenCode) is "close enough" to a paid one (e.g. Claude Opus) for a given role.

This is **opt-in per step**. Steps without `candidates` keep the existing single-agent path with zero behavioural changes.

---

## Why this exists

The original problem: workflow.json hardcodes one provider per role. If you want to know whether `gate_checker` should switch from Claude Haiku to Qwen3.6, you have to either A/B by hand or commit to a switch and judge the result subjectively. Either way you can't put a number on it.

This feature pays for redundant work for a while in exchange for hard data:
- **Win rate per (role, provider)** — over the last N runs, which provider's output did the evaluator pick?
- **Average quality score per (role, provider)** — how close was it? "Opus wins 80% but only by 0.5 points on a 10-point scale" is useful information.
- **Head-to-head** — when providers A and B competed in the same group, what was the score?

Once the data is in, you can route each role to the best (or cheapest acceptable) provider and turn candidates off.

---

## How it works

For a step that declares `candidates`, the runtime:

1. Generates a `candidate_group_id` (UUID).
2. For each candidate:
   - Creates a new branch off the canonical worktree's HEAD: `aiboard-cand/{cardId}-{groupShort}-{index}-{provider}`.
   - Spins up a fresh worktree, copies the canonical `.aiboard/{tasks,comments,images}/` into it.
   - Runs the candidate's executor (`docker-claude-cli`, `docker-opencode`, etc.) against that worktree with the candidate's optional model + providerParams overrides.
   - Commits the candidate's changes (commit-based git behaviors only — see "Limitations" below).
   - Saves a `step_result` row with `candidate_group_id`, `candidate_index`, `provider`, `selected = NULL`.
3. **Short-circuits to ERROR** if every candidate returned a non-`COMPLETE` outcome — no point running the evaluator on nothing but failures.
4. Runs the evaluator step (a different role, e.g. `evaluator` → Claude Opus). The evaluator sees the original task prompt + each candidate's `detail` + each candidate's git diff against the canonical branch. Saves an evaluator `step_result` row (no `candidate_group_id`; correlated by step_name suffix `:evaluator`).
5. Parses the evaluator's verdict — `winner_index` (0-indexed) plus optional 0–10 score + reasoning per candidate. Updates the candidate rows: `selected = (index == winner_index)`, `quality_score`, `evaluator_reasoning`.
6. **Promotes the winner**: `git reset --hard {winner-branch}` on the canonical worktree so subsequent steps see the winner's commits.
7. Cleans up: deletes loser worktrees + branches, removes the winner's worktree (the canonical one now points at its commits).
8. Posts per-candidate audit comments to the card (markers `<!-- agent-step:{step}:cand-{i}:{provider} -->`) so the human reviewer can see what each candidate produced.

The evaluator's `AgentResult` becomes the step's outcome — AgentRunner uses it to drive the state's transitions exactly as it would for a non-candidate step.

---

## Configuring a candidate-group step

Add `candidates` and `evaluator` to any agent_run step. Example for the implementation phase:

```json
{
  "name": "implement",
  "role": "implementer",
  "taskPromptFile": "prompts/states/ready_for_implementation.md",
  "candidates": [
    { "provider": "docker-claude-cli" },
    { "provider": "docker-opencode", "model": "qwen3.6-35b-a3b" }
  ],
  "evaluator": {
    "role": "evaluator",
    "taskPromptFile": "prompts/evaluator/code_review_candidates.md",
    "scoring": "WinnerWithScores"
  }
}
```

### Field reference

| Field | Required | Notes |
|---|---|---|
| `candidates[].provider` | yes | Must resolve to a registered executor at runtime. Validator errors on duplicates. |
| `candidates[].model` | no | Override the role's default model for this candidate. |
| `candidates[].providerParams` | no | Override per-candidate. Merged on top of the state's providerParams. |
| `evaluator.role` | yes | Must be a key in `roles`. Typically a strong reasoning model (`evaluator` → opus by default in `workflow.github.json`). |
| `evaluator.taskPromptFile` | one of | Markdown file with the per-step-type evaluator prompt (see `prompts/evaluator/`). |
| `evaluator.taskPrompt` | one of | Inline alternative to `taskPromptFile`. |
| `evaluator.scoring` | no | `WinnerWithScores` (default) records 0–10 quality scores per candidate. `WinnerOnly` skips the scores and just records who won. |

### Validator constraints

| Check | Errors when |
|---|---|
| Candidates require evaluator | `candidates` is non-empty but `evaluator` is null. |
| Evaluator requires candidates | `evaluator` is set but `candidates` is empty. |
| Cap of 4 candidates per step | More than 4 entries in `candidates`. |
| Unique providers per group | The same provider key appears twice. |
| Commit-based git only | The state's `gitBehavior` is `discard` (winner promotion needs git commits). |
| Evaluator role exists | `evaluator.role` is not in `roles`. |
| Evaluator has a prompt | Both `taskPrompt` and `taskPromptFile` are unset. |

---

## Evaluator response contract

The evaluator returns the standard Agent Contract JSON (`outcome` + `detail`) extended with two optional fields the parser knows how to extract:

```json
{
  "outcome": "COMPLETE",
  "winner_index": 1,
  "scores": [
    { "index": 0, "score": 6.5, "reasoning": "Mostly works but skips the rate-limit case" },
    { "index": 1, "score": 8.5, "reasoning": "Cleaner, handles rate limits" }
  ],
  "detail": "**Winner: Candidate 1** — ..."
}
```

- `outcome = COMPLETE` + a valid `winner_index` → winner promoted, candidate rows updated.
- `outcome = COMPLETE` + invalid/missing `winner_index` → no winner promoted; candidate worktrees torn down, canonical worktree unchanged. AgentRunner sees `COMPLETE` and follows the state's COMPLETE transition.
- `outcome = NEEDS_INFO` / `ERROR` → no winner; cleanup; AgentRunner follows the matching transition.

The parser accepts the JSON either as a top-level object (entire response) or inside a ```json fenced block. If both prose and JSON appear, the JSON must come last.

---

## Reading the metrics

`dotnet run --project lambda/src/TaskBoard.Worker -- --mode metrics` adds two new sections when candidate-group data exists:

```
── Provider × Role Metrics (candidate runs) ─────────────
  Role                        Provider                Runs  Wins    Win%  AvgScore    AvgDur
  ──────────────────────────  ──────────────────────  ────  ────  ──────  ────────  ────────
  implementer                 docker-claude-cli         12    9    75.0%      8.20      4.2m
  implementer                 docker-opencode           12    3    25.0%      6.80      3.8m

── Head-to-Head (candidate pairs) ───────────────────────
  Role                        Provider A              Provider B                A    B  Tie
  ──────────────────────────  ──────────────────────  ──────────────────────  ───  ───  ───
  implementer                 docker-claude-cli       docker-opencode           9    3    0
```

Sections are suppressed when no candidate groups have run — no header noise for users who haven't opted in.

The **Grafana** dashboard (auto-provisioned in the local-dev stack) gets two new bar-gauge panels: "Candidate Win Rate by Provider × Role" and "Avg Quality Score by Provider × Role", both sourced from the new `v_provider_role_metrics` view (created by migration V18).

### What "win" and "score" mean (and don't mean)

- **Win** = the evaluator picked this candidate as `winner_index`. It's a *short-loop* signal — the verdict happens within a single agent run.
- **Score** = the evaluator's calibrated 0–10 quality assessment of this candidate's output, on the rubric supplied by the per-step-type evaluator prompt.
- **Downstream acceptance** (does the winner's work actually pass the gate check, the human Tested gate, and final merge?) is computed post-hoc from `v_card_metrics` + `v_card_rework` + `selected`. v1 ships only the short-loop signal in the metrics surface; downstream acceptance is a follow-up if the win-rate signal isn't expressive enough on its own.

---

## Limitations (v1)

- **Commit-based git only.** `gitBehavior: discard` states (design + test in the default workflow) cannot use candidates yet — the winner-promotion path relies on `git reset --hard {winner-branch}`. Expanding to discard would require copying winner artifacts (task file, updates dir) directly between worktrees; deferred.
- **Sessions disabled per candidate.** Each candidate runs as its own short-lived process; the optional Docker container session is bypassed because each candidate has a different worktree mount. This is a per-candidate cold start, not a per-run one.
- **`.aiboard/updates/` from the winner is not propagated** when the winner generated child tickets via the `updates/` mechanism in a candidate worktree. Implementation steps usually don't write to `updates/`, so this rarely bites; if you do hit it, generate children in a separate non-candidate step.
- **Cap of 4 candidates per group.** Prevents evaluator prompts from exploding past sensible context limits. Easy to raise later if needed.
- **Cost tracking deferred.** Use `session_exec_ms` × hourly rate per provider as a proxy for now; explicit USD totals would require pulling token counts out of each CLI's output.

---

## Schema

Migration V17 widens `step_result`:

| Column | Type | Meaning |
|---|---|---|
| `provider` | TEXT NOT NULL | Provider key the executor ran under. Backfilled to `claude-cli` for pre-V17 rows. |
| `candidate_group_id` | UUID NULL | Set on every row in a candidate group; NULL on traditional steps + the evaluator row. |
| `candidate_index` | INT NULL | Position within the group (0..N-1). Paired with `candidate_group_id` (CHECK constraint enforces). |
| `selected` | BOOLEAN NULL | True for the winner, false for losers, NULL until the evaluator runs. |
| `quality_score` | NUMERIC(4,2) NULL | Evaluator's 0.00–10.00 score. |
| `evaluator_reasoning` | TEXT NULL | Free-text reasoning per candidate. |

V18 adds two new metrics views:

- **`v_candidate_outcomes`** — one row per candidate run, surfacing the evaluator's verdict + duration without needing a join.
- **`v_provider_role_metrics`** — aggregates per `(role, provider)`: total runs, wins, win rate, average quality score, average duration. The view is filtered to candidate rows only (`candidate_group_id IS NOT NULL`), so it never double-counts traditional steps.

`v_step_duration` was also recreated in V18 to project the new candidate columns.

---

## Operational tips

- **Roll out gradually.** Start with a single low-stakes step (e.g. `gate_check`) and confirm the evaluator's verdicts feel right before extending to higher-stakes roles.
- **Watch the cost meter.** Three candidates × one Opus evaluator per step is real money. The metrics console output and the Grafana dashboard both show `AvgDur` so you can ballpark the cost overhead.
- **Don't pit a model against itself.** The validator forbids duplicate provider keys per group; if you want N samples from one provider, that's a different feature.
- **Use a strong evaluator.** A weak evaluator picks weak winners. Default config routes the `evaluator` role to Opus; if you swap it, expect noisier verdicts.
- **Failed candidates can't win.** If every candidate's outcome is non-`COMPLETE`, the runtime short-circuits to ERROR without running the evaluator. Don't spend the evaluator tokens trying to pick a least-bad failure.
