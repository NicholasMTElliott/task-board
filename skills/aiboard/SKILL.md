---
name: aiboard
description: Set up, debug, and operate aiboard (the AI Kanban Agent Orchestrator) on a project. Use when the user asks to scaffold a board, diagnose a stuck card, validate a workflow, or run agents on a card.
---

# aiboard skill

You are routing the user through the `aiboard` CLI. Stay close to the CLI surface — don't invent flags, don't simulate work yourself, don't run agents without explicit consent.

## Step 1 — Detect state

Before anything else, run these in parallel via Bash:

| Check | Command |
|---|---|
| Is `aiboard` on PATH? | `aiboard --help` (exit 0 → installed) |
| Is cwd a git repo? | `git rev-parse --is-inside-work-tree` |
| Does `./.aiboard/` exist? | `test -d .aiboard && ls .aiboard/` |
| Recent git origin (auto-detect repo)? | `gh repo view --json owner,name 2>/dev/null` |

If `aiboard` is not on PATH, stop and tell the user to install it first (the binary ships next to `aiboard.exe` on Windows or `aiboard` on Linux/Mac in their `aiboard/` distribution directory).

## Step 2 — Route by intent

Pick the branch that matches both the user's words and the detected state. If unclear, ask one short clarifying question — never guess.

### A. No `./.aiboard/` exists yet → Initialize

The user wants to set up a new project. Walk these steps:

1. Ask which agent provider they want to use unless they already said:
   - **claude** (Anthropic API; needs `claude` CLI authed)
   - **codex** (OpenAI Codex CLI; needs `codex` authed)
   - **opencode** (local Qwen3.6 via llama.cpp; needs Docker + `llm-net` + a llama-server)

2. Run init in non-interactive mode if you've auto-detected the repo via `gh`, otherwise interactive:
   ```
   aiboard --mode init --template from-scratch-{provider} \
       --github-owner <owner> --github-repo <owner/repo> --github-project <N> \
       --non-interactive
   ```
   Without `--non-interactive`, the CLI prompts for missing values. Pass values you know via flags; let it prompt for the rest.

3. After init, immediately run validation against the project so the operator sees what's still missing on the board:
   ```
   aiboard --mode validation --board-id <N>
   ```

4. If validation reports missing fields/labels, offer scaffold-board (continue at branch B). If everything is clean, point the operator at polling.

### B. `./.aiboard/` exists, board shape may be incomplete → Scaffold

The user is asking to set up the board fields/labels, OR validation just flagged missing items.

1. Show the plan (dry-run is the default — never apply without showing first):
   ```
   aiboard --mode scaffold-board --board-id <N>
   ```

2. Read the plan output. CREATE actions can be applied automatically. WARN actions (existing field missing options, missing Status columns) need the operator in the project UI — explain those clearly with the URL of the project if you can construct it from the configured owner+number.

3. If the user agrees with the CREATE actions, apply them:
   ```
   aiboard --mode scaffold-board --board-id <N> --apply
   ```

   `--apply` is idempotent — re-running after partial completion reports `=` (already-exists) markers, not errors.

### C. A specific card isn't moving → Diagnose

The user mentions a card by ID and says it's stuck, ignored, sitting in Ready, etc.

1. Run:
   ```
   aiboard --mode diagnose --card-id <N>
   ```

2. The output is structured. Translate the headline into one short next-action sentence:
   - `Pickup result: SKIPPED (no state filter passes)` with `Most likely cause: card is assigned` → tell the user to unassign (the output even prints the exact `gh issue edit ... --remove-assignee ...` command — surface that command verbatim).
   - `Most likely cause: a field value doesn't match` → tell them which field and what to set it to.
   - `Pickup result: HOLDING / DONE / ENTRY / IN PROGRESS / WAITING ON HUMAN` → describe what that means and the next move.
   - `NOT IN WORKFLOW` → suggest moving the card to one of the listed columns.

### D. Workflow / board sanity check → Validate

The user wants to know if their workflow JSON matches the board shape, or if there are config errors.

1. Run:
   ```
   aiboard --mode validation --board-id <N>
   ```

2. Walk the Errors first (these block runs), then Warnings (don't block but indicate latent issues), then Infos.

### E. Run on a specific card → Agent mode

The user explicitly wants to run an agent on a card right now (rare — usually polling is preferred).

```
aiboard --mode agent --card-id <N> --board-id <BOARD>
```

Confirm with the user before launching — agent runs spend tokens. If the card has multiple candidate slots configured, costs scale by candidate count.

### F. Start the polling loop → Polling

The user wants the long-running auto-pickup loop:

```
aiboard --mode polling --board-id <BOARD>
```

This is **long-running**. Confirm explicitly before launching, and remind the user:
- Ctrl+C once = graceful shutdown (finishes the current card, exits cleanly)
- Ctrl+C twice = force quit (may leave a card in IN_PROGRESS column)

### G. See recent run history / costs → Metrics

```
aiboard --mode metrics                       # all-time
aiboard --mode metrics --since 7d            # last 7 days
aiboard --mode metrics --card-id <N>         # one card
```

## Hard rules

- **Don't invent flags or modes.** If unsure, run `aiboard --help` and read the canonical surface.
- **Don't run polling or agent modes without explicit user consent** — they spend money. `init`, `validation`, `diagnose`, `metrics`, and `scaffold-board` (without `--apply`) are read-only and safe.
- **Don't apply scaffold-board changes without showing the plan first.** Default is dry-run for a reason.
- **Don't edit `./.aiboard/workflow.json` or `appsettings.json` from inside this skill** — that's beyond scaffold's scope. If the user wants to switch providers, re-run `aiboard --mode init --template from-scratch-<other> --force`.
- **When the user says "fix it"**, run `aiboard --mode diagnose` first to see what "it" actually is. The CLI's output is the source of truth — translate it, don't second-guess it.
