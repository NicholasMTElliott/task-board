# aiboard project templates

Each subdirectory is a fresh-start scaffold for a new project. `aiboard --mode init` picks one of these and copies its contents into the project's `.aiboard/` directory, substituting placeholders.

## Available templates

| Template | Provider key | Models | When to pick |
|---|---|---|---|
| `from-scratch-claude` | `docker-claude-cli` | opus / sonnet / haiku tiered per role | You have an Anthropic API key (or Claude Pro/Max) and want the highest-quality default. |
| `from-scratch-codex` | `codex` | gpt-5.5 / 5.4 / 5.4-mini / 5.3-codex tiered per role | You have OpenAI Codex CLI access and prefer that surface. |
| `from-scratch-opencode` | `docker-opencode` | Qwen3.6 (`-think` for design/QA, no-think for impl/gates) | You want zero per-token cost via a local llama.cpp server; requires Docker + the `llm-net` bridge + a llama-server reachable as `llama-server:8080`. |

## What every template includes

A KvA-shape workflow (single "Ready" column with an `Activity` field discriminator and an `assignee isEmpty` lock) running this pipeline:

```
Backlog → Ready[Design] → Ready[Implementation] → Ready[Review] → Ready[Test] → Ready[Merge] → Done
```

Five active states all share the **Ready** column, distinguished by the value of the `Activity` project field. The agent assigns itself when it picks a card up (so other pollers skip it) and unassigns on completion. Holding columns: **In progress**, **Questions**, **Problems**.

Each state runs **one agent per step** (no candidate-evaluation fan-out). The provider/model is set on the role itself; switching templates means re-running `aiboard --mode init` with a different `--template` flag.

## What's deliberately not in these templates

- **Multi-candidate evaluation.** Useful for benchmarking models head-to-head but expensive. Add `candidates` + `evaluator` per step manually if you want it. See `docs/CandidateEvaluation.md`.
- **Story decomposition (Tasking).** The KvA pipeline doesn't include a Ready-for-Tasking phase. If you want stories to spawn child tasks automatically, add a Tasking state with `generationConfig` — see `workflow.github.example.json` (the column-per-state legacy template).
- **Trello.** All three from-scratch templates assume GitHub Projects. Trello operators should hand-author or extend.

## Canonical `.gitignore` for `.aiboard/`

The `.aiboard/` directory mixes two kinds of files: **project config** (`workflow.json`, `appsettings.json` — should be committed and travel with the repo) and **runtime ephemera** (`tasks/`, `comments/`, `images/`, `updates/` — written by the orchestrator on every agent run, churns constantly, must never be committed).

After running `aiboard --mode init`, add this snippet to the project's root `.gitignore`:

```gitignore
.aiboard/*
!.aiboard/workflow.json
!.aiboard/appsettings.json
```

If you renamed the workflow file (e.g. `workflow.github.json`), add a matching `!.aiboard/<name>.json` line. `aiboard --mode init` prints the snippet at the end of its "Next steps" output as a reminder.

## Substitutions

`appsettings.json` ships with placeholders that `aiboard --mode init` replaces:

| Placeholder | Substituted with |
|---|---|
| `__OWNER__` | The GitHub user/org that owns the project (e.g. `acme-corp`) |
| `__OWNER_SLASH_REPO__` | The full `owner/repo` form (e.g. `acme-corp/widgets`) |
| `__PROJECT_NUMBER__` | The GitHub project number (e.g. `4`) |

## Conversational front-end

A Claude Code skill (`skills/aiboard/SKILL.md`, bundled next to `aiboard.exe`) wraps these templates plus the `init` / `scaffold-board` / `diagnose` / `validation` modes in a slash command. Install it once via:

```bash
mkdir -p ~/.claude/skills/aiboard
cp -r skills/aiboard/* ~/.claude/skills/aiboard/
```

Then `/aiboard` in any Claude Code conversation routes you through "no `.aiboard/` yet → init", "card stuck → diagnose", "set up board fields → scaffold-board", etc., without needing to memorise CLI flags. See `skills/README.md` for details.

## Comparing to the legacy templates

The repo also ships:

- `workflow.github.example.json` — column-per-state shape (one column per workflow state). Use this when you're integrating with an existing GitHub Projects board that already has a column-per-state layout.
- `workflow.simple.example.json` — small example demonstrating shared-column multi-phase workflows for documentation.
- `workflow.v1.json` — Trello-flavored legacy reference.

These remain available for reference; new projects should prefer the `from-scratch-*` templates above.
