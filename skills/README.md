# aiboard skills

This directory bundles a Claude Code skill that wraps the aiboard CLI. The skill is conversational glue — it routes user intents into the right `aiboard --mode ...` invocation, translates structured output into next-action sentences, and refuses to run cost-spending commands without explicit consent.

## What's here

| Skill | Triggered by | What it does |
|---|---|---|
| `aiboard/` | `/aiboard` (in Claude Code) | Detects project state (cwd is git repo? `./.aiboard/` exists? `aiboard` on PATH?) and routes the user through init / scaffold-board / diagnose / validation / agent / polling / metrics. |

## Install

Skills live under `~/.claude/skills/` (user scope, available everywhere) or `.claude/skills/` (project scope, only in that project's repo).

User-scope install:

```powershell
# Windows PowerShell, from the aiboard distribution directory
$dst = Join-Path $env:USERPROFILE ".claude/skills/aiboard"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Recurse -Force "$PSScriptRoot/skills/aiboard/*" $dst
```

```bash
# Linux/macOS, from the aiboard distribution directory
mkdir -p ~/.claude/skills/aiboard
cp -r ./skills/aiboard/* ~/.claude/skills/aiboard/
```

Project-scope install (only that repo gets the skill):

```bash
mkdir -p .claude/skills/aiboard
cp -r /path/to/aiboard/skills/aiboard/* .claude/skills/aiboard/
```

After install, reload Claude Code. `/aiboard` should appear in the slash-command list.

## What the skill does NOT do

- It doesn't invoke `aiboard` directly via SDK — it shells out to the installed binary on the user's PATH. Make sure `aiboard` is on PATH first (or run `aiboard --help` from the directory containing the binary to confirm it works).
- It doesn't edit `./.aiboard/workflow.json` or `./.aiboard/appsettings.json` — those are operator-owned. To switch providers, re-run `aiboard --mode init --template from-scratch-<other> --force`.
- It doesn't apply scaffold-board changes without showing the plan first. Default is dry-run.
- It doesn't run polling or agent modes (which spend tokens) without explicit user consent.
