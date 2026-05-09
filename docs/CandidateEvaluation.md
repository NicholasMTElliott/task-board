# Multi-Agent Candidate Evaluation

Run a step with N agents racing on the same task, then have an evaluator pick a winner. The losers get discarded; the winner's branch is promoted into the canonical workflow. Per-(role, provider) win-rate and quality-score metrics build up over time so you can decide whether a free local model (e.g. Qwen via OpenCode) is "close enough" to a paid one (e.g. Claude Opus) for a given role.

**Concurrency model**: every candidate in a slot runs **fully in parallel**, regardless of provider. So `[claude×2, opencode×1, codex×2]` runs all five concurrently — wall-clock time is bounded by the slowest *single* candidate, not by the sum or by per-provider group totals. Account-level rate limits (Anthropic, OpenAI) are handled by the per-candidate retry loop, which converts a 429 to an ERROR-with-rate-limit-flag for slot/chain-level fallback. Genuine concurrency caps (a single shared local llama.cpp server backing both `docker-opencode` and `docker-claude-qwen`) are expressed declaratively via the [resource pool](#named-resource-concurrency-pool). Earlier versions of aiboard grouped candidates by provider and serialised same-provider candidates within each group; that grouping was removed in v0.0.24 because it cost wall-clock without preventing real failures.

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
2. Runs all candidates concurrently via a single `Task.WhenAll`. For each candidate:
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
8. Posts per-candidate audit comments to the card (`<!-- aiboard-log kind:candidate ... -->`) so the human reviewer can see what each candidate produced.

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

`aiboard --mode metrics` surfaces four candidate-related sections when data exists:

```
── Provider × Role Metrics (candidate runs) ─────────────
  Role                        Provider                Runs  Wins    Win%  AvgScore    AvgDur       Cost$       InTok      OutTok  Struct%
  ──────────────────────────  ──────────────────────  ────  ────  ──────  ────────  ────────  ──────────  ──────────  ──────────  ───────
  implementer                 docker-claude-cli         12    9    75.0%      8.20      4.2m     $0.4128       18.2k        2.4k       —
  implementer                 docker-opencode           12    3    25.0%      6.80      3.8m           —       42.1k        5.1k    18.2%

── Head-to-Head (candidate pairs) ───────────────────────
  Role                        Provider A              Provider B                A    B  Tie
  ──────────────────────────  ──────────────────────  ──────────────────────  ───  ───  ───
  implementer                 docker-claude-cli       docker-opencode           9    3    0

── Evaluator Reliability (winner regression rate) ───────
  Role                        Provider                Verdicts  Regressed    Rate
  ──────────────────────────  ──────────────────────  ────────  ─────────  ──────
  evaluator                   docker-claude-cli             24          1    4.2%

── Cache Hit Rate (deterministic skip) ──────────────────
  State                   Step                        Role                    Total   Hits    Rate
  ──────────────────────  ──────────────────────────  ──────────────────────  ─────  ─────  ──────
  Ready for Design        review_related_tickets      board_analyst              17     11   64.7%
```

Sections are suppressed when their underlying tables are empty — no header noise for users who haven't opted into the relevant feature.

The **Grafana** dashboard (auto-provisioned in the local-dev stack) gets two new bar-gauge panels: "Candidate Win Rate by Provider × Role" and "Avg Quality Score by Provider × Role", both sourced from the `v_provider_role_metrics` view (created by migration V18, extended in V22).

### What each column means (and doesn't)

- **Win** = the evaluator picked this candidate as `winner_index`. *Short-loop* signal — the verdict happens within a single agent run.
- **Score** = the evaluator's calibrated 0–10 quality assessment on the rubric supplied by the per-step-type evaluator prompt.
- **Cost$ / InTok / OutTok** (V22) = total USD cost and total input/output tokens consumed by candidates of this `(role, provider)` over the time window. Cost is null for Codex (ChatGPT subscription) and local-LLM providers (no monetary cost). Tokens are populated whenever the CLI's wire format reports `usage.{input_tokens, output_tokens}` — for local LLMs against llama.cpp this is the **headline signal for context-fill pressure** on Qwen-target steps. Cache tokens (Claude only) are summed in the underlying view but not in the console table to keep it readable; query `v_provider_role_metrics` directly when you need them.
- **Struct%** (V22) = share of this provider's candidates where the DockerOpenCode no-think structurer fallback recovered the result from prose narrative instead of the agent emitting a clean JSON envelope. High rates flag a thinking model that's struggling with the structured-output contract; consider routing through `docker-claude-qwen` (server-enforced schema) for that role instead.
- **Evaluator Reliability** (V22) = `regression_rate_percent` is the share of evaluator verdicts where the picked winner was later flagged `winner_regressed = true` by the same run's gate check. Answers "is this evaluator a reliable judge?" with data instead of opinion. Cross-run regressions are not yet detected.
- **Cache Hit Rate** (V25) = share of step invocations where the rerun-redesign deterministic-skip cache reused a prior `COMPLETE` step_result instead of invoking the LLM. High rate means re-runs with unchanged inputs are being detected; low rate on a step that re-runs often signals input-bundle drift (operator-edited managed sections, comment churn, prompt or model changes). Replaces the V22 "Fast-Path Hit Rate" metric, which was tied to the (now-removed) LLM-judgment preamble.
- **Downstream acceptance** (does the winner's work actually pass the gate check, the human Tested gate, and final merge?) is the union of `winner_regressed` (in-run signal, V22) plus the existing post-hoc analysis over `v_card_metrics` + `v_card_rework` + `selected`. The v1 metrics surface ships the in-run signal directly; cross-run analysis remains a join you write yourself.

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
- **Evaluator reliability is in-run only.** `winner_regressed` is set when the same run's gate check fires GATE_FAIL after a candidate group. A winner that survived the gate but later broke at the human Tested gate or in production is not flagged; that's a cross-run signal we haven't wired yet. The `selected` × `v_card_rework` join covers it manually.

## Re-run cache (deterministic skip)

When a card returns from a Questions column for a re-run, candidate-group steps participate in the same deterministic-skip cache as single-agent steps (rerun redesign Problem 1). The runtime hashes the input bundle and compares against the prior `COMPLETE` `step_result` row's `input_hash`. On match, the entire step is skipped — no LLM invocation, no candidate fan-out, no evaluator. The prior winner's output is reused via the existing canonical row, and a `kind:cache_hit` aiboard-log comment marks the timeline.

The input bundle hashes:

- **Operator-authored portion of the card body** — the prefix before any managed section markers. Edits here invalidate.
- **Operator comments** — every comment whose body does NOT carry an `aiboard-log` marker (i.e. human-authored, not orchestrator-emitted). Adding, editing, or deleting these invalidates the cache. Agent-generated comments (`kind:step`, `kind:candidate`, `kind:evaluator`, `kind:gate`, `kind:optional`, `kind:cache_hit`) are filtered out at hash time and do **not** participate.
- **Prior section output hashes** — managed-section content from earlier steps in the same state. If an operator edited an earlier step's managed section, downstream steps cache-miss on this signal.
- **Step config** — provider, model, candidate / slot configuration, retry policy, prompt file references, generation config. Changes to `workflow.json` invalidate.
- **System prompt contents** — file contents of the role's system prompt.
- **Task prompt contents** — file contents (or inline string) of the step's task prompt.

Plus a separate "section drift" check: if the step's *own* managed-section content was edited externally between runs, the cache misses on that signal independently of the input bundle hash.

**Force a fresh re-run** by editing any input that participates above. Practical levers:

- **Edit the card body**: change anything in the operator-authored prefix (the portion before `<!-- step-section:... -->` markers), or edit a managed section in place to trigger section drift.
- **Edit or delete an operator comment**: any non-`aiboard-log` comment. *Deleting an agent comment (one that carries an `aiboard-log` marker) does not invalidate the cache* — those are filtered out of the hash.
- **Edit `workflow.json`**: bump a model, adjust a candidate's retries, swap a prompt file path. Even cosmetic config changes count.
- **Edit the prompt file contents**: changing `prompts/<role>.md` or `prompts/states/.../*.md` invalidates every step that references it.

The previous LLM-judgment preamble ("if your prior output remains accurate, respond COMPLETE") was removed in Round-4 of the rerun redesign — the deterministic cache handles "no change" decisively without the LLM judging itself.

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

### V22 columns (usage capture + reliability signals)

V22 widens `step_result` further:

| Column | Type | Meaning |
|---|---|---|
| `cost_usd` | NUMERIC(10,6) NULL | Claude only (`total_cost_usd` from `stream-json`); null for Codex / local-LLM. |
| `input_tokens` / `output_tokens` | BIGINT NULL | Token counts from the CLI's `usage` block. Local-LLM rows include these so you can reason about Qwen prefix-cache pressure. |
| `cache_read_tokens` / `cache_creation_tokens` | BIGINT NULL | Claude-specific cache token counts. |
| `fast_path_hit` | BOOLEAN NULL | **Deprecated** (Round-4 rerun redesign). Was true when the re-run preamble was injected AND outcome was COMPLETE; column preserved on the schema for back-compat but no longer populated by the runtime. The cache decision is recorded in `execution_kind` instead (`'cache_hit'` vs `'full_run'`). |
| `structurer_fallback_used` | BOOLEAN NULL | True only on DockerOpenCode rows where the no-think structurer recovered the result from prose narrative. |
| `evaluator_prompt_chars` | INT NULL | Set on `:evaluator` rows so context-truncation trends are visible. |
| `winner_regressed` | BOOLEAN NULL | Set true on candidate winners (`selected = true`) when the same run's gate check returned GATE_FAIL. |

V22 also adds `agent_run.rate_limit_events INT NOT NULL DEFAULT 0` (incremented per `RateLimitException` caught), recreates `v_step_duration` and `v_candidate_outcomes` to project the new columns, extends `v_provider_role_metrics` with cost / token / cache / structurer aggregates, and introduces two new views:

- **`v_evaluator_reliability`** — per-`(evaluator-role, evaluator-provider)` regression rate via LEFT JOIN of evaluator step rows to their `selected = true` siblings within the same `(run_id, state_name)`. Surfaces evaluators whose verdicts don't survive scrutiny.
- **`v_fast_path_hit_rate`** — **dropped in V25** (Round-5 rerun redesign). Replaced by `v_cache_hit_rate`, which reports the same operator question ("how often is this step skipped?") sourced from `execution_kind = 'cache_hit'` instead of the deprecated `fast_path_hit` column.

---

## Named-resource concurrency pool

Multiple providers can share the same external resource and shouldn't all hammer it concurrently. The canonical case is `docker-opencode` and `docker-claude-qwen` both targeting the local llama.cpp server on `llm-net` — `local-llm`'s `llama-server` runs `--parallel 1`, so two concurrent Qwen-target candidates fight over the single slot, the second blocks for minutes waiting on the first, and the inactivity timer fires before any tokens stream back.

**This is required, not optional, when both Qwen-target providers are present in the same candidate slot.** Earlier versions of aiboard happened to mask the contention by serialising same-provider candidates, but candidates now run fully in parallel (see Concurrency model above), so explicit declaration is mandatory.

Declare resources with caps in `appsettings.json` (or `./.aiboard/appsettings.json`):

```json
"ResourcePool": {
  "Pools": {
    "local-llm": { "MaxConcurrent": 1 }
  },
  "ProviderResources": {
    "docker-opencode": ["local-llm"],
    "docker-claude-qwen": ["local-llm"]
  }
}
```

The pool acquires a slot on every declared resource for the calling provider before invoking the executor and releases on dispose. Resources are sorted alphabetically before acquisition so multiple providers with overlapping resource sets can't deadlock. Unknown resource names in a provider's list are filtered out with a warning at startup so config typos degrade gracefully.

The pool is opt-in: an empty `ResourcePool` config (the default) registers a singleton with no pools, so every `AcquireAsync` returns a no-op lease — zero overhead for users who don't need it.

**Wired at three call sites**: regular step / gate / optional reviewer (via `AgentRunner.ExecuteWithSessionAsync`), per-candidate retry attempts, and the evaluator. The lease wraps only the `executor.ExecuteAsync` call itself — not the retry backoff or post-step git operations — so a waiting candidate makes progress as soon as the previous one releases the resource.

**Use it for**: shared local-LLM servers; per-provider rate-limit budgets you want to enforce conservatively (declare a `claude-cli-budget` resource with cap 2 if you want at most two concurrent Claude CLI calls regardless of which providers spawned them); host CPU caps on memory-heavy Docker workloads (declare `host-cpu` with cap N where N is sized for your machine).

---

## Operational tips

- **Roll out gradually.** Start with a single low-stakes step (e.g. `gate_check`) and confirm the evaluator's verdicts feel right before extending to higher-stakes roles.
- **Watch the cost meter.** Three candidates × one Opus evaluator per step is real money. The metrics console output and the Grafana dashboard both show `AvgDur` so you can ballpark the cost overhead.
- **Same-provider model A/B is fine.** Two `docker-claude-cli` candidates with different `model` (e.g. Opus vs Sonnet) work and produce useful comparison data. Identical (provider, model) twice also works as a variance-measurement run.
- **Use a strong evaluator.** A weak evaluator picks weak winners. Default config routes the `evaluator` role to Opus; if you swap it, expect noisier verdicts.
- **Failed candidates can't win.** If every candidate's outcome is non-`COMPLETE`, the runtime short-circuits to ERROR without running the evaluator. Don't spend the evaluator tokens trying to pick a least-bad failure.
