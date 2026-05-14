# Onboarding a new project

Five aiboard CLI modes cover the lifecycle from blank repo to running pipeline. They're listed here in the order you'll typically use them.

| Mode | Purpose | Writes? |
|---|---|---|
| `--mode init` | Scaffolds `./.aiboard/` from a bundled template. | Local files only. |
| `--mode validation` | Read-only check of workflow vs. board. | No. |
| `--mode scaffold-board` | Creates missing project fields and labels via `gh`. | Project board + repo labels (only with `--apply`). |
| `--mode diagnose --card-id N` | Explains why a specific card isn't being picked up. | No. |
| `--mode polling` | Long-running pickup loop. | Cards (moves, comments, branch pushes). |

The bundled `/aiboard` Claude Code skill (under [skills/aiboard/](../skills/aiboard/)) wraps these modes in conversation; install it once into `~/.claude/skills/aiboard/` and `/aiboard` in any Claude Code session routes you through the right command.

---

## Step 1 — `aiboard --mode init`

Pick one of three templates based on which AI provider you want every role to run on:

| Template | Provider key | Models |
|---|---|---|
| `from-scratch-claude` *(default)* | `docker-claude-cli` | opus / sonnet / haiku tiered per role |
| `from-scratch-codex` | `codex` | gpt-5.5 / 5.4 / 5.4-mini / 5.3-codex tiered per role |
| `from-scratch-opencode` | `docker-opencode` | Qwen3.6 (`-think` for design/QA, no-think for impl/gates) |

All three produce the same shared-column example workflow: one `Ready` column for the entire active pipeline, an `Activity` field (Design → Implementation → Review → Test → Merge) discriminating which state runs, and an **`assignee isEmpty` filter** that turns assignment into the work-in-progress lock.

Interactive form (prompts for owner / repo / project number, auto-detects via `gh repo view`):

```bash
aiboard --mode init --template from-scratch-claude
```

Non-interactive (CI / scripts):

```bash
aiboard --mode init --template from-scratch-codex --non-interactive \
    --github-owner acme --github-repo acme/widgets --github-project 4
```

Output: `./.aiboard/{appsettings.json, workflow.json}`. The `appsettings.json` placeholders (`__OWNER__`, `__OWNER_SLASH_REPO__`, `__PROJECT_NUMBER__`) are substituted from your inputs.

`--force` overwrites an existing `./.aiboard/` *but preserves operator-added files* (e.g. a hand-edited `appsettings.user.json` for local secrets). Only files present in the template get re-written.

To switch providers later, re-run with `--force` and a different `--template`.

**Then add the canonical `.aiboard/` `.gitignore` snippet** to the project's root `.gitignore`:

```gitignore
.aiboard/*
!.aiboard/workflow.json
!.aiboard/appsettings.json
```

This excludes the runtime ephemera (`.aiboard/tasks/`, `.aiboard/comments/`, `.aiboard/images/`, `.aiboard/updates/`) — written and rewritten on every agent run — while keeping the project's workflow + appsettings tracked in git so the config travels with the repo. If you renamed the workflow file (e.g. `workflow.github.json`), add a matching `!.aiboard/<name>.json` line. `aiboard --mode init` prints this snippet at the end of its "Next steps" output as a reminder.

---

## Step 2 — `aiboard --mode validation --board-id N`

Reads the project board and cross-checks it against the workflow. Surfaces three severities:

- **Error** — workflow references a column / field / option that doesn't exist; aiboard would fail at runtime. Exit 1.
- **Warning** — a label is missing (auto-created on first use) or a field option is non-canonical. Exit 0.
- **Info** — provider doesn't support introspection; checks skipped.

Run this after `init` to see what the board is missing.

---

## Step 3 — `aiboard --mode scaffold-board --board-id N [--apply]`

Default is **dry-run**: prints a plan, exits 0 without touching the board. Re-run with `--apply` to execute.

Plan output groups changes:

```
Scaffold plan for board 4:

  CREATE 2 field(s):
    + Activity: single-select with options [Design, Implementation, Review, Test, Merge]
    + priority: single-select with options [Critical, Desired, Important, Urgent]

  CREATE 2 label(s):
    + type:story
    + type:task

  WARN 1 existing field(s) need option(s) added manually:
    Estimate (existing: [1, 2]) missing: [4, 8]
    → Open the project's field settings (single-select edit) to add these options.

  WARN 3 column(s) need to be added to the Status field manually:
    In progress, Problems, Questions
    → Open the project, click the Status header → field settings → add options.

Re-run with --apply to perform the CREATE actions.
Manual actions (WARN) require operator work in the project UI; --apply does not handle them.
```

**CREATE** actions run `gh project field-create` and `gh label create`. **WARN** actions are manual because:

- Adding options to existing fields requires GraphQL mutations whose surface is brittle. Click into the field settings — adding an option in the GitHub UI is one click.
- Status column manipulation is similarly delicate. Click the Status header → field settings → add the missing options.

`--apply` is **idempotent**. Re-running after partial completion reports `=` (already-exists) markers, not failures. Safe to retry after fixing a manual step.

If `gh` isn't on PATH (or auth failed) the apply pass surfaces each action as `Failed` with the underlying error, then exits 1. The plan can still be generated against an already-introspected board even when `gh` is broken downstream.

---

## Step 4 — Drop a card and start polling

With the board scaffolded, the operator workflow is:

1. Create an issue, add it to the project board, set `Activity = Design`, leave the assignee empty.
2. Run:
   ```bash
   aiboard --mode polling --board-id 4
   ```
3. Watch the card move through Design → Implementation → Review → Test → Merge → Done. Press Ctrl+C once to stop after the current card; press it twice to force-quit.

Polling skips cards where the `assignee isEmpty` filter fails — assignment is the WIP lock. When an agent picks up a card, it self-assigns; when the run completes (or fails into a holding column), it unassigns. Manual assignment by the operator is the override: assign the card to anyone, polling will leave it alone.

---

## When a card isn't moving — `aiboard --mode diagnose --card-id N`

Read-only triage. Output is structured: header (id / title / column / assignees / fields), then a single `Pickup result: ...` line with the verdict, then a "Most likely cause" hint when applicable.

The headline failure mode this exists to catch is **"the card is assigned"**. On the shared-column example workflow the `assignee isEmpty` filter is invisible to most operators. When a card sits in `Ready` with someone assigned, polling silently skips it. Diagnose surfaces this and prints the exact `gh issue edit N --remove-assignee @user` command to unstick it.

Other branches:

| Result | What it means |
|---|---|
| `ELIGIBLE` | Card matches an actionable state. If polling isn't running it, check polling is running and that no higher-priority card is ahead. |
| `HOLDING` | Card is in Questions or Problems — read the latest comment, fix the issue in the card body, move back to a Ready column to retry. |
| `IN PROGRESS` | An agent is currently working it (or a prior run didn't unwind cleanly — check for orphaned `aiboard-*` Docker containers). |
| `WAITING ON HUMAN` | Manual gate — move the card to advance. |
| `DONE` | Terminal column. Nothing scheduled. |
| `ENTRY` | Manual-entry column (e.g. Backlog) — move the card to a Ready column to trigger work. |
| `NOT IN WORKFLOW` | Card's column isn't part of the workflow at all. The output lists the actionable columns to move toward. |
| `SKIPPED (no state filter passes)` | Per-state filter walk follows. |
| `BLOCKED BY DEPENDENCIES` | Card otherwise matches an actionable state, but has unresolved blockers under the workflow's `dependencyPolicy`. Output lists each blocker with column and prints an exact `gh api -X DELETE …` command if the link was declared in error. |

Filter walk surfaces the *first failing predicate per state* in priority order: assignee → field → label. A "Most likely cause" headline picks the highest-priority failure across all states.

---

## Optional: enabling dependency policy

By default the from-scratch templates do not enforce ticket dependencies — every card is pickable as long as it matches a state filter. If your workflow has hard sequencing (e.g. an API task that can't start until a database task is done), you can opt in.

**1. The repo-side prerequisite.** Ticket dependencies live in GitHub Issues' built-in sub-issues / dependencies feature. Most repos have it on by default; if you don't see "Dependencies" in the Issue side panel, enable it in the repo's Settings → Features.

**2. Edit `.aiboard/workflow.json` to enable the policy.** Add (or uncomment) a `dependencyPolicy` block alongside `polling` / `estimation`:

```json
"dependencyPolicy": {
  "enabled": true,
  "enforcedStates": ["Ready for Implementation", "Ready for Test", "Approved"],
  "satisfiedColumns": ["Done"],
  "commentOnBlocked": true
}
```

- `enforcedStates` — the workflow state names (or shared-column names) where blocking is checked. Polling, direct agent runs, and merge runs all consult the policy before transitioning a card to in-progress.
- `satisfiedColumns` — a blocker counts as satisfied when it lands in any of these columns (or when the upstream issue is closed-as-completed). Defaults to your terminal columns if omitted, but listing it explicitly is recommended.
- `commentOnBlocked` — when true, blocked cards get an upserted `<!-- agent-dependency-blocked -->` comment listing the unresolved blockers.

There is no in-code default for `enforcedStates`: enabling the policy without listing any states is a no-op. List the states you want gated.

**3. Declare dependencies on individual cards.** Two paths:

- **Manually**, from the GitHub Issue UI (Add → Add a dependency → search for the blocking issue), or via `gh`:
  ```bash
  gh api -X POST repos/<owner>/<repo>/issues/<blocked>/dependencies/blocked_by -f issue_id=<blocker-database-id>
  ```
- **From an agent**, by writing dependency front matter into a `new-{slug}.md` file in `.aiboard/updates/`. The orchestrator parses `blockedBy` and `blocks` lists when it creates the new ticket. See [docs/CardTypesAndGeneration.md](CardTypesAndGeneration.md) for the front-matter syntax.

**4. Diagnose blocked cards.** A card whose blockers aren't satisfied will report `Pickup result: BLOCKED BY DEPENDENCIES` from `aiboard --mode diagnose --card-id N` rather than the generic `ELIGIBLE`. The output lists each unresolved blocker so you can either drive it forward, close it, or remove the link.

**5. No migration step needed.** The observational `card_dependency_wait` table (V23) is applied automatically by Flyway on `docker compose up -d`.

---

## When to deviate from these defaults

The bundled templates are deliberately small. The full configuration surface (multi-candidate evaluation, optional specialist reviewers, story-task decomposition, custom card types, column-per-state shape) is documented under:

- [docs/CandidateEvaluation.md](CandidateEvaluation.md) — head-to-head model A/B per step.
- [docs/CardTypesAndGeneration.md](CardTypesAndGeneration.md) — story-task decomposition, label vs. field discrimination.
- [Agent.md](../Agent.md) (top-level) — the LLM-targeted comprehensive reference.

Switching from the from-scratch templates to these patterns is a workflow.json edit, not a re-init — every from-scratch template ships the same role catalog (`board_analyst`, `senior_engineer`, `implementer`, `code_reviewer`, `qa`, `gate_checker`, `specialist_reviewer`, `merge_resolver`) so adding `candidates` + `evaluator` to a step or adding a tasking state are local edits.
