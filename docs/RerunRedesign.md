# Re-run Architecture Redesign

## Context

When a card is re-run through a phase (after NEEDS_INFO answered, infrastructure failure fixed, gate fail, operator clarification, etc.), four problems emerge:

1. **Skip detection is unreliable.** `RerunPreambleBuilder` injects a "bail with COMPLETE if nothing relevant changed" prompt. The LLM's judgment is the only enforcement; no audit trail; no way for downstream steps to distinguish "skipped because cached" from "didn't run."
2. **Comments are confusing.** Step comments are upserted by marker, replacing prior runs' content. Operators can't tell what happened across runs; prior reasoning is lost; the chronology of problems, reruns, and operator clarifications is invisible.
3. **Gate checks fail multi-run tickets.** Gate sees only the current run's diff (`runStartCanonicalSha` → HEAD), missing work committed in prior runs. Comments don't help because they were upserted/replaced. Result: gate rejects work that was actually performed correctly across runs.
4. **Truncated diff rejection.** When the diff exceeds the gate-check role's context budget, the gate rejects with "diff truncated, can't verify."

## Migration Assumptions

- This redesign ships as one coordinated change set, not as independent incremental releases.
- The project is unreleased and in active development. No backward compatibility with legacy comments, legacy fast-path markers, or mixed old/new ticket state is required.
- Fresh tickets use the new description/comments model wholesale.
- Existing development cards may be manually cleaned up or recreated; no automated legacy migration is required.

## Goals

- Skip steps deterministically when their inputs haven't changed.
- Provide a single clear "current state" view (description) and a complete chronological log (comments).
- Let gate checks see cumulative work across runs.
- Stop rejecting on truncated diffs.
- Allow operator edits to managed step sections without treating the card as corrupted.
- Keep comment history useful without letting high-volume candidate runs drown the card.

## Implementation Order

All four fixes ship together. The order below is implementation sequencing only, not release sequencing. Each Problem lands as its own commit on a single branch; the branch ships as one PR.

1. **Problem 4** (truncated diff packet) — independent, fully spec'd; clean warm-up commit.
2. **Problem 2** (description/comments split) — creates the managed-section substrate, the new comment API, the schema, and the legacy-marker replacement.
3. **Problem 1** (deterministic skip detection) — uses section markers + persisted hashes; replaces `RerunPreambleBuilder` entirely.
4. **Problem 3** (gate-check awareness) — shifts gate diff base and feeds persisted step records / cache decisions into gate context.
5. **Follow-up**: comment compaction mode — useful, but not required for the rerun redesign MVP.

---

## Problem 2 — Description-as-State / Comments-as-Log

The core architectural shift: separate "current truth" (description) from "transaction history" (comments).

### Description Structure

```markdown
<operator-authored requirements; never overwritten by agents>

<!-- aiboard:managed-section-start -->

<!-- step-section:review_related_tickets -->
## Related Tickets Review
<concise current state for this step>
<!-- /step-section:review_related_tickets -->

<!-- step-section:create_design -->
## Technical Design
<concise current state>

<!-- open-questions-start -->
### Open Questions
- Should we use Postgres or MySQL?
  - **Operator:** Postgres please.
<!-- open-questions-end -->

<!-- resolved-decisions-start -->
### Resolved Decisions
- Postgres chosen over MySQL: index requirements
<!-- resolved-decisions-end -->
<!-- /step-section:create_design -->

<!-- aiboard:managed-section-end -->
```

**Rules:**
- Everything before `aiboard:managed-section-start` is operator-authored; agents never modify it.
- Each step gets exactly one section, delimited by `<!-- step-section:NAME -->` and `<!-- /step-section:NAME -->`.
- Sections appear in **visitation order**: each state appends its own section the first time it writes. There is no pre-declared pipeline order in the description.
- Sections hold *current truth* — concise, polished, latest iteration. Not journals; never accumulate "Run 3: no new info / Run 4: still no new info."
- Optional specialist reviewers triggered by gate failure also write description sections, in their own `<!-- step-section:NAME -->` block. Section header text comes from the optional step's name (snake_case prettified to Title Case). Their sections appear in visitation order alongside main-step sections.
- `### Open Questions` is an optional subsection where the agent surfaces unanswered questions. It is wrapped in `<!-- open-questions-start -->` ... `<!-- open-questions-end -->` HTML markers so the parser can find it robustly even if an operator reformats the contents. Operators may inline-answer (`**Operator:** ...`) or answer via comment.
- `### Resolved Decisions` is an optional subsection capturing durable choices worth preserving across iterations. It is wrapped in `<!-- resolved-decisions-start -->` ... `<!-- resolved-decisions-end -->` HTML markers for the same reason.
- Operator clarifications anywhere in the operator-authored area, or operator edits to managed-section content, trigger deterministic hash invalidation (Problem 1). The example's inline `**Operator:** ...` text is illustrative; operator intent is inferred from section-hash drift, not from any inline label or marker.

### Operator Edits to Managed Sections

Operators are allowed to edit managed step sections. We cannot and should not prevent this.

Policy:
- Edits to earlier completed sections are preserved and become normal inputs to later steps. Example: operator edits the design section after design approval; implementation reads the edited design.
- Edits to the current step's section or a future step's section are visible to the re-running agent, but the agent is not obligated to preserve them. If the step writes `replace`, its new section becomes current truth.
- The system detects edits by persisting `section_output_hash` for each completed step. If the current section hash differs from the persisted output hash, that section has been externally modified.
- External modification invalidates deterministic cache for the step that owns the modified section, and (transitively) for every downstream step that depends on its `section_output_hash`.
- No special "operator edit" markup is required. Human intent is inferred from section hash drift, not from inline labels.
- Malformed or duplicate step markers make the relevant step ineligible for cache and produce a clear diagnostic comment.

### Agent Contract Schema Change

Extend the agent's structured output with an explicit section-update field:

```json
{
  "outcome": "COMPLETE | NEEDS_INFO | ERROR",
  "detail": "string — full reasoning, goes into the chronological comment",
  "questions": [...],
  "section_update": {
    "strategy": "leave | replace | append_with_revision_notes",
    "content": "string — new section content (markdown); required when strategy != leave",
    "open_questions": ["string", ...],
    "resolved_decisions": ["string", ...]
  }
}
```

- `leave` — orchestrator does not modify the existing step section. Used when the latest iteration found nothing new. On the very first run for a step (no prior section), `leave` is coerced to a placeholder section (`_No durable section update from this step._`) so future hashing has a stable input.
- `replace` — orchestrator replaces section content wholesale.
- `append_with_revision_notes` — same as replace, but `content` includes lessons-learned / durable-decision notes from prior iterations.
- `open_questions` and `resolved_decisions` are agent-supplied lists; orchestrator renders them into the section's marker-wrapped subsections.

The agent decides strategy based on prompt instructions; the orchestrator is mechanical.

**Writer responsibilities by role:**
- Main steps and optional reviewers: write their own section.
- Gates: do not write the description. Verdicts go in comments.
- Candidate group: only the winning candidate's `section_update` is applied; the **evaluator** commits it on the group's behalf. Losing candidates' sections are discarded. Their reasoning stays visible in the human audit log, but is not written into downstream agent context.
- Evaluator: does not write its own meta-section. Its only description-write responsibility is committing the winner's update.

### Comment Structure

No upsert for chronological log entries. Every step posts a fresh comment per run, with a standard preamble:

```markdown
<!-- aiboard-log kind:step state:"Ready for Design" step:create_design run:a3f8c2 attempt:3 outcome:COMPLETE -->

> **create_design** · attempt 3 · COMPLETE
> Smith Cooper on aimachine-1 · docker-claude-cli / claude-opus-4-6 · run a3f8c2

<agent's full reasoning — content from the `detail` field>

### Open Questions
- <repeated from section_update.open_questions for scroll-to-bottom visibility>
```

Every comment carries an `<!-- aiboard-log kind:... ... -->` marker. The marker classifies the comment as agent-generated for hashing, correlates it with step / run / attempt records, and routes it through one of three retention policies.

**Append (chronological log entries):**
- `kind:run` — run lifecycle feedback such as merge kickback, git post-processing, or unexpected run error
- `kind:step` — main step output
- `kind:candidate` — per-candidate output within a slot
- `kind:evaluator` — evaluator verdict + scores
- `kind:gate` — gate check pass/fail
- `kind:optional` — optional specialist reviewer output
- `kind:cache_hit` — "step skipped — inputs unchanged since run X / attempt Y"

**Delete-and-repost (status notices that should stay current chronologically):**
- `kind:dependency_blocked` — card waiting on blocker
- `kind:completion_progress` — parent waiting on children
- `kind:rate_limit_notice` — provider backoff in effect
- `kind:shutdown_notice` — partial-progress notice on Ctrl+C
- `kind:cross_card_notification` — cross-card linkage notification

**Upsert (true dedupe keys, not status):**
- `kind:created_ticket_dedupe` — prevents double-creation when the same source step retries

When a provider lacks a comment-delete API, `delete_and_repost` falls back to upsert-with-timestamp-prefix in the body (e.g. `> Updated 2026-05-08 11:32 — still blocked by #5`). The full retention map is operator-overridable in `workflow.json` under `rerun.comments.retentionPolicy`.

**Preamble fields:**
- Line 1: step name · attempt number · outcome
- Line 2: agent given+family name on machine · provider / model · run ID short

**Attempt counting:** `step_result` row count for `(tenant, card, state, step) WHERE candidate_index IS NULL OR candidate_index = 0`, +1. One row per attempt regardless of candidate fan-out. Multi-slot fallback within one run counts each slot try as an attempt. Confirmed semantics: "attempt 1 of run, candidate 1 of 5" — re-run after operator answers becomes "attempt 2," with its own candidate set. Cache-hits also increment the counter (a cache-hit is a real attempt, just with a no-op outcome).

**Multi-candidate variant** — each candidate posts its own comment:

```markdown
<!-- aiboard-log kind:candidate state:"Ready for Design" step:create_design slot:0 candidate:3 run:a3f8c2 attempt:2 outcome:COMPLETE -->

> **create_design** · attempt 2 · cand 3 of 5 · COMPLETE
> Adams Cooper on aimachine-1 · codex / gpt-5.4-mini · run a3f8c2

<candidate's full output>
```

Evaluator posts a separate comment marked `evaluator`:

```markdown
<!-- aiboard-log kind:evaluator state:"Ready for Design" step:create_design slot:0 run:a3f8c2 attempt:2 outcome:COMPLETE -->

> **create_design** · attempt 2 · evaluator · COMPLETE
> Brooks Cooper on aimachine-1 · docker-claude-cli / claude-opus-4-6 · run a3f8c2

**Winner:** Candidate 3 (codex / gpt-5.4-mini, score 8.5/10)

<evaluator's reasoning + per-candidate scores>
```

The evaluator commits the winner's `section_update` to the description on the group's behalf. Losing candidates' output stays visible in the human audit log, but downstream agent context uses only the winning section and canonical step rows. Per-candidate rows, evaluator rows, and candidate/evaluator audit comments are excluded from agent-visible context.

### Gate-Check Comments

Same chronological pattern. Gate fail in attempt 1, gate pass in attempt 2 = two separate comments, both visible:

```markdown
<!-- aiboard-log kind:gate state:"Ready for Design" step:gate_check run:a3f8c2 attempt:1 outcome:ERROR -->

> **gate_check (post_design)** · attempt 1 · GATE_FAIL
> Cole Cooper on aimachine-1 · docker-claude-cli / claude-haiku-4-5 · run a3f8c2

<gate's reasoning>
```

Default: gate checks do **not** write to the description (their judgment is in the comment). Configurable per role via `writesDescriptionSection`.

### Migration

No automated legacy-marker migration. Existing development cards are recreated. Legacy markers (`<!-- agent-step:... -->`, `<!-- agent-run:... -->`, `<!-- agent-dependency-blocked -->`, `<!-- completion-check:... -->`, `<!-- agent-rate-limit:... -->`, `<!-- agent-shutdown:... -->`, `<!-- agent-cross-comment:... -->`, `<!-- agent-created-ticket:... -->`) are removed wholesale; the new system emits only `<!-- aiboard-log kind:... -->`. `TaskFileManager.ContainsAgentMarker` is rewritten to check for `aiboard-log` only.

### Implementation Work

1. **Schema** — add `section_update` to the agent contract JSON schemas (`AgentSchemas.OutcomeSchema`, `OutcomeSchemaOpenAI`, evaluator variants).
2. **Parser** — extract `section_update` in `AgentOutputParser.ParseResult`; surface on `AgentResult` as a new optional record.
3. **Description writer** — new component: takes the existing card body + step name + `section_update` and returns the new body. Find-and-replace within the managed section, preserving operator content. Initializes the managed-section markers at the end of the description on first write. Sections appear in visitation order — each state appends its own section the first time it writes; updates preserve position.
4. **Comment poster** — all chronological comments append via a new `ITaskBoardClient.AppendAgentCommentAsync`. Status notices use delete-and-repost via a new `ITaskBoardClient.DeleteCommentAsync` (fallback: upsert-with-timestamp-prefix where the provider lacks a delete API). The retention map is workflow-config-driven (`rerun.comments.retentionPolicy`). `UpsertAgentCommentAsync` is retained only for true dedupe keys (`created_ticket_dedupe`).
5. **Attempt counter** — new `IRunStore.GetStepAttemptCountAsync(tenant, card, state, step) → int`, computed as `COUNT(*) WHERE candidate_index IS NULL OR candidate_index = 0`.
6. **System prompt updates** — all roles instructed on:
   - Section purpose: "current truth, concise, not a journal"
   - Strategy choice: when to `leave` vs `replace` vs `append_with_revision_notes`
   - Question handling: `open_questions` go in section AND at bottom of comment
   - Length expectations: short for "no information / no actions needed" cases
7. **Workflow config additions** — new top-level `rerun` block:
   - `rerun.defaultBranch` (null → auto-detect via `git symbolic-ref --short refs/remotes/origin/HEAD` at startup)
   - `rerun.comments.retentionPolicy` — per-kind append / delete_and_repost / upsert map (defaults shown above; operator may override per kind)
   - `rerun.diff.summaryThresholdBytes` (Problem 4 threshold, default 51200)
   Plus per-step `writesDescriptionSection: bool` (default true; gates and evaluators are explicit false; main steps and optional reviewers default true).
8. **Comment classification** — replace `TaskFileManager.ContainsAgentMarker`'s legacy marker checks with `<!-- aiboard-log ... -->` only. Legacy marker constants removed from the codebase.
9. **Persistence** — persist `section_output_hash`, normalized `section_update` content (raw JSON in a JSONB column for replay/debugging + rendered section hash in a TEXT column for the cache-key path), and log metadata needed for cache/gate context.
10. **Tests** — description writer (initialization, operator-content preservation, replace/leave/append semantics, ordering, malformed markers, operator edit hash drift, first-run `leave` placeholder); comment poster (preamble shape, metadata markers, multi-candidate, evaluator, retention-policy routing per kind, delete-and-repost fallback); attempt counter; agent-contract schema (section_update parsing edge cases).

### Resolved Design Decisions

- **Marker grammar:** locked to a single `<!-- aiboard-log kind:... -->` shape with kind-specific fields. Routing (append / delete_and_repost / upsert) is per-kind via workflow config.
- **Open Questions / Resolved Decisions HTML markers:** present (`<!-- open-questions-start -->` / `<!-- resolved-decisions-start -->` and matching close markers) so the parser finds them robustly even after operator reformatting.
- **`strategy: "leave"` on first run:** orchestrator coerces to a placeholder section (`_No durable section update from this step._`) so future hashing has a stable input.
- **DB representation of `section_update`:** both raw JSON in a JSONB column (replay/debugging) and a separate TEXT column for `section_output_hash` (cache-key path).

---

## Problem 1 — Deterministic Skip Detection

**Approach:** persist `(tenant, card, state, step) → (input_hash, section_output_hash, completed_at, output_summary, source_run_id)` on every COMPLETE. On step entry: compute current input hash; if it matches the persisted record, the prior outcome was COMPLETE, and the relevant output section has not drifted unexpectedly, skip the LLM call.

This replaces `RerunPreambleBuilder`. The LLM no longer decides whether a step can skip. The orchestrator decides mechanically.

**Cache-hit recording:** do not add a new agent outcome enum unless truly needed. Prefer:
- `outcome = COMPLETE`
- `execution_kind = "cache_hit"` (new `step_result` column)
- `source_run_id = <prior run id>`
- `source_step_result_id = <prior step result id>`
- `detail = "Skipped: inputs unchanged since run X / attempt Y."`

**Input hash composition** is uniform across steps:

- All human/operator comments — any comment without an `<!-- aiboard-log ... -->` marker, full set, regardless of when posted
- Operator-authored portion of the description (everything before `<!-- aiboard:managed-section-start -->`)
- All prior steps' `section_output_hash` values, concatenated in visitation order
- The step's own workflow config (comprehensive: `providerParams`, candidate / slot config, retry policy — anything that affects the agent's approach or how the run is structured)
- The step's role system prompt file contents
- The step's task prompt file contents

Per-step input declarations are deferred. All steps treat all prior steps' `section_output_hash` values as inputs. Future per-step input declarations may narrow this when the cost becomes measurable.

The step's own agent-managed section is **not** included as a normal input, because it is the step output and would create a feedback loop. Instead, compare its current normalized hash to the persisted `section_output_hash`:
- same hash: output section unchanged; cache may hit if input hash also matches;
- different hash: section was externally modified; owning step must re-run or at minimum cache miss;
- malformed/missing section: cache miss with diagnostic.

**Transitive invalidation:** if upstream step A re-runs and produces a byte-identical `section_update`, downstream step B sees the same upstream `section_output_hash` in its bundle and caches. If A produces a different section, B's bundle hash differs and B re-runs. Re-runs do NOT cascade unconditionally — the cascade is gated on output equality.

**Hash exclusions:**
- agent log comments — any `<!-- aiboard-log kind:... -->` marker, including `kind:cache_hit` (otherwise the first cache hit would invalidate every subsequent run by changing the operator-comment set)
- timestamps
- run IDs
- attempt numbers
- agent names
- generated wording noise outside declared input sections

**Normalization for hashing:** LF line endings, trim trailing whitespace per line, NFC Unicode normalization, single trailing newline at section close. UTF-8 bytes hashed with SHA-256.

**Cache hit handling:** when a step skips, the orchestrator still posts a brief comment (`kind:cache_hit`: "step skipped — inputs unchanged since run X / attempt Y, prior result reused") so the chronological log captures the decision. The description's section is not touched. Attempt counter increments.

**Force-rerun:** deterministic cache invalidation should be data-based, not marker-deletion-based. Operators can force a re-run by editing relevant input text/sections or using an explicit future CLI flag (`--force-step create_design`) if needed. Note: deleting an operator comment is itself an input change and re-runs dependent steps — this is intentional, since the agent's prior reasoning may have been informed by that comment. Editing a step's workflow config (provider, model, candidate count, providerParams, prompt files) likewise invalidates that step's cache.

---

## Problem 3 — Gate Check Awareness

**Two changes:**

1. **Diff base shift.** Each `agent_run` row stores a `state_entry_canonical_sha` captured at the IN_PROGRESS column transition. On first claim for `(tenant, card, state)` the SHA is read from the canonical worktree's HEAD. Subsequent runs in the same state copy the value forward from the earliest matching run. If the column is null on a run that needs it, fall back to `git merge-base HEAD <default-branch>` and persist the result back into the row. Default branch is read at startup via `git symbolic-ref --short refs/remotes/origin/HEAD`; aiboard hard-fails with an operator hint if origin/HEAD is not configured (`git remote set-head origin --auto` to fix). So a re-run gate sees the cumulative committed work across runs, not just this run's possibly-empty diff.
2. **Persisted-step-record consumption.** Gate prompt receives the `step_result` records for the current state across all runs (or a digested view of them), so a step that completed in a prior run — or bail-skipped via Problem 1's cache — is visible to the gate as "yes, this happened, here's the prior output." The gate stops rejecting on "this run's comments don't show all the required actions."

Gate context should include:
- current managed description sections for the state;
- current run step records;
- prior successful step records reused by cache hits;
- cumulative diff or large-diff summary;
- created tickets / rejected update files;
- build/test evidence when available.

**Gate-check caching.** Gate checks themselves participate in the deterministic-skip system. The gate's input bundle is the gate prompt template + diff base + all upstream `section_output_hash` values + the current state's persisted step records. If the bundle hash matches a prior gate run with `outcome = COMPLETE`, the gate skips with `kind:cache_hit`.

---

## Problem 4 — Truncated Diff (independent)

**Approach:** when the raw diff exceeds a configurable threshold (e.g. 50 KB; configured via `rerun.diff.summaryThresholdBytes`), send a structured large-diff packet instead of rejecting outright.

Packet:
- `git diff --name-status`
- per-file line counts
- top-K files by change magnitude inline
- omitted-file list
- build/test command output if available
- explicit note that this is summary mode

Gate prompt rule:
- A gate may pass in summary mode only when the visible files + tests/build evidence are sufficient for the configured gate.
- If not sufficient, it should return NEEDS_INFO or ERROR with specific omitted files/evidence needed, not a generic "diff truncated" rejection.

## Follow-up — Comment Compaction

High-volume reruns and candidate groups can produce too many comments. Do not solve this inside the main rerun path. Add a separate maintenance operation later.

Proposed mode:

```powershell
aiboard --mode compact --card-id N
```

Behavior:
- Reads chronological agent comments, managed sections, and `step_result` history.
- Produces a compact "Run History Summary" managed section or one summary comment.
- Preserves human/operator comments as authoritative inputs by default.
- May delete/archive old agent comments only when the provider supports it and the operator explicitly opts in.
- Never deletes evidence needed for deterministic hashing; hashes use persisted records and current sections, not old comments.

This should be a separate ticket after the rerun redesign lands.
