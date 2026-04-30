# Multi-Agent Candidate Evaluation

Run a step with N agents racing on the same task, then have an evaluator pick a winner. The losers get discarded; the winner's branch is promoted into the canonical workflow. Per-(role, provider) win-rate and quality-score metrics build up over time so you can decide whether a free local model (e.g. Qwen via OpenCode) is "close enough" to a paid one (e.g. Claude Opus) for a given role.

**Concurrency model**: candidates **of different providers run in parallel**; **candidates sharing a provider run sequentially within that group**. So `[claude×2, opencode×1, codex×2]` runs as three concurrent provider tracks (claude, opencode, codex), with each track processing its own candidates one at a time. Wall-clock time is bounded by the slowest provider track (sum of its candidates), not by the sum of all candidates. The constraint exists because a single CLI / credential pool / rate-limit window per provider makes concurrent same-provider invocations a fast route to a 429, while different providers don't contend on each other.

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
2. Groups candidates by provider (case-insensitive) and runs the groups concurrently. Within each group, candidates run in declaration order. For each candidate:
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
| `candidates[].provider` | yes | Must resolve to a registered executor at runtime. Duplicate providers are now allowed (same-provider model A/B testing is a legitimate use case). |
| `candidates[].model` | no | Override the role's default model for this candidate. |
| `candidates[].providerParams` | no | Override per-candidate. Merged on top of the state's providerParams. |
| `candidates[].retries` | no | Number of in-place retries on transient failures. Default 0. |
| `candidates[].retryOn` | no | Which `FailureReason` values trigger retry. Default `["RATE_LIMIT", "TIMEOUT"]`. |
| `evaluator.role` | yes | Must be a key in `roles`. Typically a strong reasoning model (`evaluator` → opus by default in `workflow.github.json`). |
| `evaluator.taskPromptFile` | one of | Markdown file with the per-step-type evaluator prompt (see `prompts/evaluator/`). |
| `evaluator.taskPrompt` | one of | Inline alternative to `taskPromptFile`. |
| `evaluator.scoring` | no | `WinnerWithScores` (default) records 0–10 quality scores per candidate. `WinnerOnly` skips the scores and just records who won. |

### Slot-based fallback chains (the `slots` shape)

For multi-stage failover (e.g. "race subscription providers; if both fail, fall back to local Qwen"), use `slots[]` instead of step-level `candidates`+`evaluator`. Each slot is its own parallel candidate group.

```jsonc
{
  "name": "implement",
  "role": "implementer",
  "taskPromptFile": "prompts/states/ready_for_implementation.md",
  "slots": [
    {
      "candidates": [
        { "provider": "docker-claude-cli", "retries": 0 },
        { "provider": "codex",             "model": "gpt-5.4", "retries": 1 }
      ],
      "evaluator": {
        "role": "evaluator",
        "taskPromptFile": "prompts/evaluator/code_review_candidates.md"
      }
    },
    {
      "candidates": [
        { "provider": "docker-opencode", "model": "qwen3.6-35b-a3b", "retries": 3 }
      ]
    }
  ]
}
```

**How slot iteration works:**
1. Slot 0 runs (parallel candidates + evaluator → winner).
2. If slot 0 returned a winner → that's the step result; **slot 1 is NOT invoked**.
3. If slot 0 winner returned NEEDS_INFO → step ends with NEEDS_INFO; slot 1 NOT invoked. (User chose this so a cheap fallback can't repeatedly avoid a real question.)
4. If slot 0 failed (all candidates non-COMPLETE, OR evaluator returned ERROR / no winner_index, OR promotion failed) → slot 1 fires.
5. After all slots exhausted: surface the LAST slot's result.

**Single-candidate slots may omit the evaluator** — the runtime surfaces that candidate's outcome directly. This is the natural shape for the last fallback ("just run local Qwen and use whatever it produces").

**Per-candidate retries** fire in-place on `RATE_LIMIT` and `TIMEOUT` (configurable via `retryOn`). Backoff: 30s base, 2× exponential, 5min cap, ±20% jitter. Retries do NOT cross slot boundaries — that's what fallback slots are for. Failed retries are NOT persisted as separate `step_result` rows; only the final outcome is saved.

**Validator rules for slots:**
- A step may set EITHER `slots` OR step-level `candidates+evaluator`, never both.
- Each slot's `candidates` must be non-empty.
- A slot with 2+ candidates requires `evaluator`. A 1-candidate slot's evaluator is optional.
- Each `retries` must be ≥ 0; each `retryOn` entry must be a valid `FailureReason`.

**Audit warning** (non-fatal): if the **final** slot has all candidates with 0 retries, the validator warns. The point of fallbacks is graceful degradation; if the last line of defence has no retry budget, a transient blip parks the card in Error.

**Backward compatibility:** legacy step-level `candidates+evaluator` workflows continue to work unchanged — the runtime adapts them to a single-slot config under the hood. No JSON edits required.

**Persistence:** every candidate row from every attempted slot is saved to `step_result`. The new `slot_index` column (NULL for single-slot steps) disambiguates which slot a row belonged to. The `v_slot_outcomes` view aggregates "how often does slot N actually carry the day vs needing further fallback?" — useful for tuning slot ordering. Per-`(role, provider)` metrics in `v_provider_role_metrics` are slot-agnostic.

### Validator constraints

| Check | Errors when |
|---|---|
| Candidates require evaluator | `candidates` is non-empty but `evaluator` is null. |
| Evaluator requires candidates | `evaluator` is set but `candidates` is empty. |
| Provider non-empty | A candidate has an empty/whitespace `provider`. |
| Evaluator role exists | `evaluator.role` is not in `roles`. |
| Evaluator has a prompt | Both `taskPrompt` and `taskPromptFile` are unset. |

**Things the validator no longer rejects** (previously did, removed because they were over-conservative):

- Candidate count is unconstrained. Eval-prompt size and wall-time are user-managed concerns.
- Duplicate providers are allowed — the common reason is same-provider model A/B testing (`docker-claude-cli + opus` vs `docker-claude-cli + sonnet`). Variance-measurement runs (the same agent twice) are also legitimate.
- `gitBehavior: discard` works (design / tasking states). Winner promotion uses file copy of `.aiboard/{tasks,updates}/` from the winner worktree to the canonical worktree instead of `git reset --hard` (see "Winner promotion modes" below).

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
- `outcome = COMPLETE` + missing/null `winner_index` → **schema violation**. The orchestrator overrides the result to `ERROR` with a diagnostic detail (instead of silently cleaning up with no winner promoted, which is the v0.0.15 KvA-reported bug). The evaluator schema (`AgentSchemas.EvaluatorOutcomeSchema`) makes `winner_index` required when `outcome = COMPLETE` via JSON Schema `if`/`then`; the OpenAI variant types it `["integer", "null"]` and relies on parser-side enforcement.
- `outcome = NEEDS_INFO` / `ERROR` → no winner; cleanup; AgentRunner follows the matching transition.

The parser accepts the JSON either as a top-level object (entire response) or inside a ```json fenced block. If both prose and JSON appear, the JSON must come last.

### Picking the right evaluator prompt

`prompts/evaluator/` ships three task-prompt templates. Pick the one that matches the step type you're putting candidates around — the rubric in each is calibrated for that step type, and the wrong rubric will anchor the evaluator on the wrong signals:

| Prompt | Use for | Compares on |
|---|---|---|
| `code_review_candidates.md` | Implementation steps (`gitBehavior: commit_*`) | `git diff` from each candidate against the canonical branch (Correctness / Quality / Scope / Tests / Safety) |
| `design_candidates.md` | Design / tasking steps and other write-ups (`gitBehavior: discard`) | Each candidate's `.aiboard/tasks/{cardId}.md` body and `.aiboard/updates/*.md` files (Completeness / Correctness / Actionability / Scope / Conventions / Conciseness). `git diff` is empty in discard mode and not used here. |
| `gate_check_candidates.md` | Gate-check candidate groups (rare today) | Each candidate's pass/fail verdict and reasoning (Calibration / Reasoning quality / Conciseness) |

Rule of thumb: if your step's `gitBehavior` is `discard`, don't point at `code_review_candidates.md` — its rubric anchors on diffs that won't exist, and the evaluator will spuriously conclude "no design artifact was produced."

---

## Reading the metrics

`aiboard --mode metrics` adds two new sections when candidate-group data exists:

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

## Winner promotion modes

The promotion mechanism switches based on the state's `gitBehavior`:

| `gitBehavior` | Promotion | Comparison material in evaluator prompt |
|---|---|---|
| `commit_only` / `commit_and_push` | `git reset --hard {winner-branch}` on the canonical worktree. Winner branch survives; loser branches deleted. | Per-candidate `git diff` against the canonical branch. |
| `discard` | Copy winner's `.aiboard/tasks/` and `.aiboard/updates/` contents into the canonical worktree (clearing canonical's copies first so file-removal is reflected). All candidate branches torn down. Canonical's git state untouched. | Per-candidate `.aiboard/tasks/{cardId}.md` (the card body) + each `.aiboard/updates/*.md` content (since `git diff` is empty for discard candidates — `.aiboard/` is gitignored). |

After promotion, AgentRunner's existing post-step processors run unchanged: `TaskFileManager` reads the promoted task file and updates the card body, `UpdateFileProcessor` reads the promoted updates files and creates child cards.

## Limitations (v1)

- **Sessions disabled per candidate.** Each candidate runs as its own short-lived process; the optional Docker container session is bypassed because each candidate has a different worktree mount. This is a per-candidate cold start, not a per-run one.
- **Gate checks can't have candidates yet.** `gateCheck` is a state-level field, not a `steps[]` entry. To compare gate checkers (e.g. Qwen vs Haiku for the `gate_checker` role), run separate cards with each provider routed and compare in the metrics. A future change could either (a) extend `GateCheckConfig` to support `candidates` + `evaluator` directly, or (b) inline the gate as a regular step.
- **Cost tracking deferred.** Use `session_exec_ms` × hourly rate per provider as a proxy for now; explicit USD totals would require pulling token counts out of each CLI's output.

## Re-run fast-path

When a card returns from a Questions column for a re-run, candidate-group steps are subject to the same fast-path detection as single-agent steps. The runtime checks for the canonical step marker on the card (`<!-- agent-step:{step.Name} -->`) and the prior run's evaluator outcome (`step_result` row whose name ends with `:evaluator` or `:slot-N:evaluator`, with `outcome = COMPLETE`). If both signals fire, every candidate in every slot receives a "RE-RUN; bail with COMPLETE if nothing relevant changed" preamble that includes the prior winning output. Candidates that agree return `outcome: COMPLETE` with `detail: "Confirmed prior output remains accurate."` in seconds; candidates that disagree produce updated outputs.

The evaluator gets its own variant of the preamble: "if all candidates confirmed prior, pick any with `winner_index: 0`; otherwise evaluate normally." So a unanimous "no change" re-run completes cheaply with the existing winner re-elected; a re-run where one candidate proposes updates produces a real comparison.

**Force a fresh re-run** by deleting the canonical `<!-- agent-step:{step.Name} -->` comment from the card before moving back to "Ready". With the marker gone, the preamble is suppressed and all candidates run from scratch. Slot ordering is unaffected — slot 0 still tries first.

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
- **Same-provider model A/B is fine.** Two `docker-claude-cli` candidates with different `model` (e.g. Opus vs Sonnet) work and produce useful comparison data. Identical (provider, model) twice also works as a variance-measurement run.
- **Use a strong evaluator.** A weak evaluator picks weak winners. Default config routes the `evaluator` role to Opus; if you swap it, expect noisier verdicts.
- **Failed candidates can't win.** If every candidate's outcome is non-`COMPLETE`, the runtime short-circuits to ERROR without running the evaluator. Don't spend the evaluator tokens trying to pick a least-bad failure.
