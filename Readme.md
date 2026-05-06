# AI Kanban Agent Orchestrator

An autonomous, state-driven multi-agent workflow system built on top of a kanban board.

This project turns a GitHub Projects board (or Trello) into an asynchronous control plane where AI agents act as specialized SDLC roles (Senior Engineer, Implementer, Code Reviewer, QA, Gate Checker, Estimator, Merge Resolver), automatically progressing tickets through a structured pipeline.

The board is the human-facing surface.
The orchestrator is the automation brain.
Agents execute work in isolated git worktrees.

---

## Vision

Use a Kanban board as an orchestration layer for autonomous AI agents.

A ticket moves into a "Ready for" state -> a specific AI role is triggered -> the agent does the work -> transitions the ticket to the next state -> optionally waits for human approval -> continues.

The board becomes:
- Human review surface
- Approval gate
- Planning document repository
- Workflow trigger engine

The orchestrator becomes:
- State machine
- Agent runtime
- Safety layer

---

## Core Principles

1. The kanban board is the human UI.
2. All planning text lives on the card/issue.
3. Agents are stateless and run in isolated git worktrees.
4. State transitions are deterministic.
5. Human approval is first-class.
6. Infrastructure cost approaches zero at idle.

---

## Architecture

```
Execution modes:
  --mode init                                 (scaffold ./.aiboard/ for a new project)
  --mode agent --card-id N                    (direct, single card)
  --mode polling --board-id 1                 (automatic, priority-sorted pickup)
  --mode metrics [--card-id N | --since 7d]   (operational metrics report)
  --mode validation --board-id N              (read-only workflow vs. board check)
  --mode diagnose --card-id N                 (explain why a card isn't being picked up)
  --mode scaffold-board --board-id N [--apply] (create missing fields/labels via gh)
  --install                                   (install bundled Claude Code skill(s) into ~/.claude/skills/; combinable with any --mode)

Conversational front-end (optional):
  /aiboard                                    (Claude Code skill bundled in skills/aiboard/, install once via `aiboard --install`)

AgentRunner flow (agent_run states):
  Fetch card → move to IN_PROGRESS column
  → create git worktree, resolve cross-references
  → write task files + comments file
  → [optional] create Docker container session (ISessionableAgentExecutor)
  → execute steps sequentially; each step routed through session (docker exec) or direct executor
  → optional gate check (lightweight Haiku validation) — also routed through session
  → optional specialist reviews — also routed through session
  → dispose session (docker stop + docker rm)
  → post-process: upsert step comments, handle git, move to outcome column

MergeRunner flow (system_merge states):
  Fetch card → find work branch → merge to main (--no-ff)
  → on conflict: abort + transition to MERGE_CONFLICT target
  → on success: push, cleanup branch, move to Done

CompletionRunner flow (children_complete states):
  Poll child cards → check all reached terminal state → move parent to Done

Event-driven parent completion (completeParentIfReady):
  Child task reaches Done → check parent's siblings → all done? → transition parent to Done
```

---

## Board Structure (GitHub Projects)

| # | Column | Role(s) | Gate Type |
|---|--------|---------|-----------|
| 1 | Backlog | -- | Manual entry |
| 2 | Ready for Design | Senior Engineer + Estimator (4 steps + optional specialist reviews) | agent_run |
| 3 | Designing | -- | In-progress |
| 4 | Design Questions | -- | Holding (NEEDS_INFO) |
| 5 | Designed | -- | Manual gate |
| 6 | Ready for Tasking | Senior Engineer (1 step: decompose story into tasks) | agent_run |
| 7 | Tasking | -- | In-progress |
| 8 | Waiting for Tasks | -- | holding (event-driven via completeParentIfReady) |
| 9 | Ready for Implementation | Implementer + Code Reviewer (2 steps + optional specialist reviews) | agent_run |
| 10 | Implementing | -- | In-progress |
| 11 | Implementation Questions | -- | Holding (NEEDS_INFO) |
| 12 | Ready for Test | QA + Doc Updater (2 steps + optional specialist reviews) | agent_run |
| 13 | Testing | -- | In-progress |
| 14 | Tested | -- | Manual gate |
| 15 | Approved | -- | system_merge |
| 16 | Merging | -- | In-progress |
| 17 | Done | -- | Terminal |
| 18 | Error | -- | Holding |

---

## Roles

| Role | Model | Purpose |
|------|-------|---------|
| `senior_engineer` | claude-opus-4-6 | Design pipeline (3 steps: review related tickets, create design, review conflicts) |
| `implementer` | claude-sonnet-4-6 | Code implementation (uses senior_engineer system prompt) |
| `code_reviewer` | claude-sonnet-4-6 | Post-implementation code review |
| `qa` | claude-opus-4-6 | Test validation |
| `gate_checker` | claude-haiku-4-5-20251001 | Lightweight gate checks after design, implementation, and test |
| `estimator` | claude-haiku-4-5-20251001 | Ticket estimation (design step 4, calibration-based sizing) |
| `specialist_reviewer` | claude-sonnet-4-6 | On-demand specialist reviews requested by gate checks |
| `senior_specialist_reviewer` | claude-opus-4-6 | High-stakes specialist reviews (legal, compliance, privacy) |
| `merge_resolver` | claude-sonnet-4-6 | Merge conflict resolution |

---

## How It Works

1. Operator creates an issue, adds it to the project board in **Backlog**. Issues are labeled `type:story`, `type:task`, or `type:bug` to indicate their card type.
2. Operator writes requirements/scope and moves card to **Ready for Design**.
3. Operator runs: `.\scripts\run_once.ps1 -CardId 3` (or uses `--mode polling` for automatic pickup)
4. Design runs 4 steps: review related tickets -> create technical design -> review for cross-ticket conflicts -> estimate ticket size. Gate check validates output and may trigger optional specialist reviews. Card moves to **Designed** with estimate written to board field. If the card has a parent story, the story's estimate is recalculated as the sum of its children.
5. Operator reviews design. **For user stories**, approves by moving to **Ready for Tasking**. **For tasks/bugs**, approves by moving to **Ready for Implementation**.
6. *(Stories only)* Tasking decomposes the story into child tasks. Each task inherits the story's priority, gets a best-guess estimate, and is placed in **Ready for Design**. The story moves to **Waiting for Tasks** and completes automatically when all child tasks reach **Done**.
7. Implementation runs 2 steps: implement code (Sonnet) -> code review (Sonnet). Gate check validates output and may trigger optional specialist reviews. Card moves to **Ready for Test**.
8. Test runs 2 steps: QA agent validates implementation -> documentation agent updates memory bank if needed. Gate check validates output (including doc updates) and may trigger optional specialist reviews. Moves to **Tested** on success.
9. Operator approves by moving to **Approved**. System auto-merges the PR branch and moves to **Done**.

If the agent needs more information, the card moves to a **Questions** column with questions posted as a comment. The operator answers and moves the card back to re-trigger.

On error, the card moves to **Error** with details posted as a comment.

---

## Agent Contract

Each agent returns structured JSON output:

```json
{
  "outcome": "COMPLETE | NEEDS_INFO | ERROR",
  "detail": "GitHub-flavored markdown summary",
  "questions": [
    {
      "question": "What is the target component?",
      "recommendations": ["Auth module", "API gateway"]
    }
  ]
}
```

The orchestrator:
1. Posts the `detail` as a markdown comment on the issue (one comment per step).
2. Transitions the card to the next column based on `outcome`.
3. Agents do NOT move cards directly -- the orchestrator controls all transitions.

---

## Workflow Configuration

File-based config (`workflow.github.json`) maps columns to roles and transitions:

```json
{
  "states": {
    "Ready for Design": {
      "name": "Ready for Design",
      "gateType": "agent_run",
      "gitBehavior": "discard",
      "pipelineOrder": 1,
      "providerParams": { "effort": "max" },
      "steps": [
        { "name": "review_related_tickets", "role": "senior_engineer", "taskPromptFile": "prompts/states/steps/review_related_tickets.md" },
        { "name": "create_design", "role": "senior_engineer", "taskPromptFile": "prompts/states/ready_for_design.md" },
        { "name": "review_design_conflicts", "role": "senior_engineer", "taskPromptFile": "prompts/states/steps/review_design_conflicts.md" },
        { "name": "estimate_ticket", "role": "estimator", "taskPromptFile": "prompts/states/steps/estimate_ticket.md" }
      ],
      "gateCheck": {
        "role": "gate_checker",
        "taskPromptFile": "prompts/gates/post_design.md"
      },
      "transitions": {
        "IN_PROGRESS": "Designing",
        "COMPLETE": [
          { "type": "moveToColumn", "value": "Designed" },
          { "type": "setField", "field": "Estimate", "value": "{{estimation}}" }
        ],
        "NEEDS_INFO": "Design Questions",
        "ERROR": "Error",
        "GATE_FAIL": "Ready for Design"
      }
    }
  },
  "roles": {
    "senior_engineer": {
      "model": "claude-opus-4-6",
      "systemPromptFile": "prompts/senior_engineer.md",
      "sections": ["Technical Design", "Decisions", "Implementation"]
    }
  },
  "polling": {
    "priorityFieldName": "priority",
    "priorityOrder": ["P0", "P1", "P2"]
  },
  "estimation": {
    "calibrationTicketId": "34",
    "calibrationSize": 1,
    "fieldName": "Estimate",
    "scale": [1, 2, 4, 8]
  },
  "dependencyPolicy": {
    "enabled": false,
    "enforcedStates": ["Ready for Implementation", "Ready for Test", "Approved"],
    "satisfiedColumns": ["Done"],
    "commentOnBlocked": true
  },
  "cardTypes": {
    "story": { "name": "User Story", "labelPrefix": "type", "allowedChildren": ["task"] },
    "task": { "name": "Task", "allowedChildren": [] }
  },
  "cardTypeField": "Type"
}
```

- `steps` array defines sequential agent invocations within a state (each with its own role and prompt)
- `gateCheck` runs a lightweight agent after all steps complete to validate output
- `providerParams` passes executor-specific flags (e.g., `effort` for Claude CLI)
- `gitBehavior`: `discard` (design/test/tasking), `commit_and_push` (implementation)
- `taskPromptFile` / `systemPromptFile` point to markdown files under `prompts/`
- `pipelineOrder` determines polling priority (higher = picked first)
- `transitions` values can be a string (column name) or an array of actions (`moveToColumn`, `setField`, `updateParentSum`)
- `estimation` configures calibration-based ticket sizing (scale, calibration ticket, board field)
- `dependencyPolicy` enables blocking dependencies. When enabled, polling skips blocked cards and direct agent/merge runs refuse them until blockers are in `satisfiedColumns` (or closed when the provider exposes only issue state)
- `cardTypes` defines card type hierarchy for child task generation (e.g., stories → tasks). `labelPrefix` is optional — set it to apply `{prefix}:{typeKey}` labels, or omit/null it to opt that type out of labels entirely
- `cardTypeField` (optional, workflow-level) — name of a project field (e.g. `"Type"`) that holds each card's type. When set, generated children write their `CardTypeDefinition.Name` to this field, and parent-type lookups for `allowedChildren` enforcement read from this field first and fall back to labels. Labels and fields can be used together; each `cardTypes[k]` must have either a `labelPrefix` or a global `cardTypeField`
- `generationConfig` on a step specifies child ticket creation: `targetType`, `targetColumn`, `linkToParent`, `copyFields` (fields to inherit from parent, e.g., priority), `setFields` (literal field→value map applied to the new card — useful for putting children into a specific pipeline stage, e.g. `{"Type": "Task", "Activity": "Design"}`; wins over `copyFields` on key collision)
- `updateParentSum` transition action recalculates a parent card's field as the sum of its children's values (used for estimate rollup)

See [docs/CardTypesAndGeneration.md](docs/CardTypesAndGeneration.md) for a walkthrough of label-based vs. field-based type discrimination, `generationConfig.setFields`, and dependency front matter on generated tickets.

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Board provider | GitHub Projects v2 (via `gh` CLI) or Trello (REST API) |
| Board abstraction | `ITaskBoardClient` interface |
| Orchestrator | C# / .NET 10 |
| Agent executors | Six implementations behind `IAgentExecutor` — sandboxed by default (`docker-claude-cli`, `docker-codex`, `docker-opencode`, `docker-claude-qwen`) plus host CLI variants (`claude-cli`, `codex`) gated behind `--unsafe`. See [Agent.md §7](Agent.md) for the full provider table, or per-sandbox how-tos: [docs/DockerSandbox.md](docs/DockerSandbox.md), [docs/CodexSandbox.md](docs/CodexSandbox.md), [docs/OpenCodeSandbox.md](docs/OpenCodeSandbox.md), [docs/ClaudeQwenSandbox.md](docs/ClaudeQwenSandbox.md) |
| Multi-agent candidate evaluation | `CandidateExecutor` — opt-in per step. Runs N agents in parallel against the same task, an evaluator picks a winner, the winner's branch is promoted, and per-(role, provider) win-rate + quality-score + cost / token / structurer-fallback / evaluator-reliability / re-run-fast-path-hit metrics accumulate. See [docs/CandidateEvaluation.md](docs/CandidateEvaluation.md) |
| Named-resource concurrency pool | `IResourcePool` / `ResourcePool` — opt-in via `ResourcePool` config section. Operators declare named resources (e.g. a single shared local llama.cpp server) with concurrency caps, and tag providers that need them. Wired around every executor invocation site so cross-provider candidates can't stomp on a shared backend. See [Agent.md §10.3](Agent.md). |
| Agent sandbox image (Claude) | `docker/agent-sandbox/Dockerfile` — node:22-slim + Claude CLI + git + ripgrep; `aiboard-agent-sandbox:latest` |
| Agent sandbox image (Codex) | `docker/codex-sandbox/Dockerfile` — node:22-slim + Codex CLI + git + ripgrep; `aiboard-codex-sandbox:latest` |
| Git isolation | Git worktrees (`GitWorkspaceManager`) |
| Task files | `.aiboard/tasks/{id}.md` (ephemeral, gitignored) |

---

## For LLM coding agents

If you're an AI coding agent setting this system up for the first time, read **[Agent.md](Agent.md)** before anything else. It's a single-file guide written specifically for LLM consumption that covers the architecture, the JSON schemas for `appsettings.json` and `workflow.*.json`, every available agent executor and model with pros/cons + when-to-use guidance, the role catalog, multi-agent candidate evaluation, and a step-by-step setup flow for a new project. The file ships in the release distribution alongside `aiboard.exe`.

A Claude Code skill (`skills/aiboard/SKILL.md`) is bundled with the distribution. Install it once per machine with `aiboard --install` (copies every bundled skill into `~/.claude/skills/<name>/`). After that, `/aiboard` in any Claude Code conversation routes you through the right `aiboard --mode ...` invocation — the skill detects your project state and picks the right mode. The `--install` flag can also be combined with another mode (e.g. `aiboard --install --mode init`) to install the skill and continue with the requested work. See [skills/README.md](skills/README.md) for details.

For a narrative human-targeted walkthrough of the onboarding lifecycle (`init` → `validation` → `scaffold-board` → `polling`, plus `diagnose` for stuck cards), see [docs/Onboarding.md](docs/Onboarding.md).

---

## Quick Start

### Install (released binary — recommended for non-developers)

Each release on the [Releases page](https://github.com/NicholasMTElliott/task-board/releases/latest) ships two flavors per platform:

- `aiboard-{rid}.zip` / `.tar.gz` — **self-contained single-file** (default). Bundles the .NET runtime; runs on any host without a separate install. ~33 MB compressed.
- `aiboard-{rid}-fdd.zip` / `.tar.gz` — **framework-dependent**. Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on `PATH`. ~5–10 MB compressed.

Where `{rid}` is one of `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. **If you don't know which to pick, use self-contained** — it's the path most users want.

See [QUICKSTART.md](QUICKSTART.md) for what to do after extracting.

### Prerequisites (when building from source)

- .NET 10 SDK
- Docker (for local PostgreSQL)
- `gh` CLI authenticated with `project` + `repo` scopes
- `claude` CLI installed and authenticated

### Start the database

```powershell
docker compose up -d
```

This launches PostgreSQL on `localhost:5432`, runs Flyway migrations automatically, and starts a Grafana instance on `http://localhost:3000` (admin/admin) with a pre-provisioned metrics dashboard.
The default connection string in `appsettings.json` connects to this local instance.

### Build the agent sandbox image (optional)

Only needed if using Docker-based agent execution. See [docs/DockerSandbox.md](docs/DockerSandbox.md) for the full enable-and-verify how-to.

```powershell
.\scripts\build-sandbox.ps1
```

Or via docker compose:

```powershell
docker compose --profile build up agent-sandbox
```

Build args: `-BaseImage`, `-AgentUid`, `-AgentGid`, `-ClaudeCliVersion`, `-Tag`, `-NoCache`.

To enable the sandbox at runtime, set `AGENT_EXECUTOR=docker-claude-cli`. It is off by default.

### Build the Codex sandbox image (recommended, for sandboxed Codex)

A separate sandbox wraps the OpenAI Codex CLI for sandboxed use against a real codebase. Filesystem isolation is provided by Docker, so the agent runs with `--yolo` by default — fast, autonomous, and contained. See [docs/CodexSandbox.md](docs/CodexSandbox.md) for the full setup.

```powershell
.\scripts\build-codex-sandbox.ps1
```

Auto-registered when Docker is detected. Provider key `docker-codex`. Migrate workflow roles from `codex` (host) to `docker-codex` to keep them usable without `--unsafe`.

### Build the OpenCode sandbox image (optional, for local-LLM roles)

A separate sandbox wraps the OpenCode CLI for use with a local llama.cpp server (e.g. Qwen3.6 served by the sibling `local-llm` compose project). Use it when you want to route low-stakes roles (gate checks, estimation, simple reviews) to a free local model. See [docs/OpenCodeSandbox.md](docs/OpenCodeSandbox.md) for the full setup, including role-suitability guidance.

```powershell
.\scripts\build-opencode-sandbox.ps1
```

Requires the `llm-net` Docker network (owned by the `local-llm` project) before any role routes to it. Enable at runtime with `AGENT_EXECUTOR=docker-opencode`.

### Use the Claude CLI against the local Qwen server (optional, for schema-enforced local runs)

The third executor, `docker-claude-qwen`, runs the regular Claude CLI in the same `aiboard-agent-sandbox` image but redirected to the local llama.cpp proxy via `ANTHROPIC_BASE_URL`. Headline benefit: server-side `--json-schema` enforcement — the proxy translates the schema to a tool-call constraint that llama.cpp enforces during generation. Useful when output reliability matters (e.g. the candidate-evaluation evaluator). See [docs/ClaudeQwenSandbox.md](docs/ClaudeQwenSandbox.md).

```powershell
$env:AGENT_EXECUTOR = "docker-claude-qwen"
```

### Add project-specific tooling to the sandbox (optional)

If your project's agent work needs a runtime that the upstream sandbox doesn't ship (a game engine, a JVM, a specific compiler, etc.), don't fork the upstream `Dockerfile`. Overlay it: a tiny `FROM aiboard-X-sandbox:latest` Dockerfile in your project repo, retag, and point `DockerAgents:*:ImageName` at the new tag. See [docs/ProjectOverlays.md](docs/ProjectOverlays.md) for the pattern, build-script template, and a worked Godot example.

No separate image build needed — reuses the Claude sandbox built above. Pair with `docker-opencode` in a candidate group to A/B them on real workloads.

### Run an agent on a card

```powershell
.\scripts\run_once.ps1 -CardId 3
```

### Run in polling mode (automatic pickup)

```powershell
.\scripts\run_polling.ps1
```

### Graceful shutdown (polling and queue modes)

Press **Ctrl+C once** to request a graceful shutdown — the runner finishes the current card and exits cleanly.
Press **Ctrl+C twice** to force quit immediately (may leave a card stuck in an in-progress column).

### Manual invocation with env vars

```powershell
$env:BOARD_PROVIDER = "github"
$env:AGENT_EXECUTOR = "claude-cli"
$env:WORKFLOW_CONFIG_PATH = "workflow.github.json"
$env:GitHubProjects__Owner = "YourGitHubUser"
$env:GitHubProjects__Repo = "YourUser/your-repo"
$env:GitHubProjects__ProjectNumber = "1"

# Single card
dotnet run --project lambda/src/TaskBoard.Worker -- --mode agent --card-id 3 --board-id 1 --workspace .

# Polling (auto-pickup highest priority card from "Ready for" columns)
dotnet run --project lambda/src/TaskBoard.Worker -- --mode polling --board-id 1 --workspace .

# Metrics report (all time)
dotnet run --project lambda/src/TaskBoard.Worker -- --mode metrics

# Metrics report (last 7 days)
dotnet run --project lambda/src/TaskBoard.Worker -- --mode metrics --since 7d

# Metrics report (single card)
dotnet run --project lambda/src/TaskBoard.Worker -- --mode metrics --card-id 3
```

---

## Safety Model

- **Sandboxed by default.** Only Docker-based providers (`docker-claude-cli`, `docker-codex`, `docker-opencode`, `docker-claude-qwen`) are usable out of the box. The agent runs inside a container with a read-only base `.git` and no host filesystem access beyond the worktree.
- **Host CLI agents require `--unsafe`.** The `claude-cli` and `codex` providers run on the host with full credentials and filesystem access; workflows that reference them refuse to start unless you pass `--unsafe` (or set `Unsafe: true` in config). Migrate to `docker-claude-cli` / `docker-codex` to keep working without the flag.
- Agents cannot transition state directly -- the orchestrator validates all transitions.
- Manual approval gates block progression until a human moves the card.
- Automated gate checks (Haiku) validate agent output before state transitions.
- Agent execution happens in isolated git worktrees; the main repo is never modified.
- System merge includes conflict detection with retry logic (max 3 attempts).
- All agent output is posted as upserted comments per step (no spam).

---

## Multi-Tenant Database

Multiple projects (different GitHub repos/projects, Trello boards) can share one Postgres instance without conflicting. Every per-tenant table (`agent_run`, `step_result`, `card_state`, `processed_events`) carries a `tenant_id` column as the leading PK; metrics views project it; stores filter on it.

The tenant identifier is `{provider}:{identifier}` — for example `github:NicholasMTElliott/kva/4` or `trello:abc123`. It's resolved from the merged configuration at startup (`GitHubProjects:Owner/Repo/ProjectNumber` or `Trello:BoardId`); missing required values fail the startup with a diagnostic naming the offending key. Each worker process is single-tenant — to run two projects against one DB, run two processes with different `.aiboard/` configs.

Docker container names also embed an 8-char tenant hash (`aiboard-{hash}-{cardId}-…`) so concurrent runs across tenants with the same numeric card ID never collide.

PGMQ queues are not yet tenant-scoped (queue mode is legacy/secondary).

---

## Cost Strategy

Idle cost: $0. Costs scale only when an agent executes (LLM tokens are the primary cost driver).

---

## Future Expansion

- Webhook-triggered automation (currently manual CLI or polling)
- PR creation automation
- Parallel agent branches
- SLA timers / retry policies
- Multi-board / multi-tenant support
- Visual dashboard

---

## Non-Goals (v1)

- Replacing the board UI
- Full CMS for workflow editing
- Autonomous production deployment
- Complex RBAC
- Multi-repo orchestration
