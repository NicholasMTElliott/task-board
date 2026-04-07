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
  --mode agent --card-id N    (direct, single card)
  --mode polling --board-id 1 (automatic, priority-sorted pickup)

AgentRunner flow (agent_run states):
  Fetch card → move to IN_PROGRESS column
  → create git worktree, resolve cross-references
  → write task files + comments file
  → execute steps sequentially (each step = Claude CLI subprocess)
  → optional gate check (lightweight Haiku validation)
  → post-process: upsert step comments, handle git, move to outcome column

MergeRunner flow (system_merge states):
  Fetch card → find work branch → merge to main (--no-ff)
  → on conflict: abort + transition to MERGE_CONFLICT target
  → on success: push, cleanup branch, move to Done

CompletionRunner flow (children_complete states):
  Poll child cards → check all reached terminal state → move parent to Done
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
| 6 | Ready for Implementation | Implementer + Code Reviewer (2 steps + optional specialist reviews) | agent_run |
| 7 | Implementing | -- | In-progress |
| 8 | Implementation Questions | -- | Holding (NEEDS_INFO) |
| 9 | Ready for Test | QA + Doc Updater (2 steps + optional specialist reviews) | agent_run |
| 10 | Testing | -- | In-progress |
| 11 | Tested | -- | Manual gate |
| 12 | Approved | -- | system_merge |
| 13 | Merging | -- | In-progress |
| 14 | Done | -- | Terminal |
| 15 | Error | -- | Holding |

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

1. Operator creates an issue, adds it to the project board in **Backlog**.
2. Operator writes requirements/scope and moves card to **Ready for Design**.
3. Operator runs: `.\scripts\run_once.ps1 -CardId 3` (or uses `--mode polling` for automatic pickup)
4. Design runs 4 steps: review related tickets -> create technical design -> review for cross-ticket conflicts -> estimate ticket size. Gate check validates output and may trigger optional specialist reviews. Card moves to **Designed** with estimate written to board field.
5. Operator reviews design, approves by moving to **Ready for Implementation**.
6. Implementation runs 2 steps: implement code (Sonnet) -> code review (Sonnet). Gate check validates output and may trigger optional specialist reviews. Card moves to **Ready for Test**.
7. Test runs 2 steps: QA agent validates implementation -> documentation agent updates memory bank if needed. Gate check validates output (including doc updates) and may trigger optional specialist reviews. Moves to **Tested** on success.
8. Operator approves by moving to **Approved**. System auto-merges the PR branch and moves to **Done**.

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
  "cardTypes": {
    "story": { "name": "User Story", "labelPrefix": "type", "allowedChildren": ["task"] },
    "task": { "name": "Task", "allowedChildren": [] }
  }
}
```

- `steps` array defines sequential agent invocations within a state (each with its own role and prompt)
- `gateCheck` runs a lightweight agent after all steps complete to validate output
- `providerParams` passes executor-specific flags (e.g., `effort` for Claude CLI)
- `gitBehavior`: `discard` (design/test), `commit_and_push` (implementation)
- `taskPromptFile` / `systemPromptFile` point to markdown files under `prompts/`
- `pipelineOrder` determines polling priority (higher = picked first)
- `transitions` values can be a string (column name) or an array of actions (`moveToColumn`, `setField`)
- `estimation` configures calibration-based ticket sizing (scale, calibration ticket, board field)
- `cardTypes` defines card type hierarchy for child task generation (e.g., stories → tasks)

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Board provider | GitHub Projects v2 (via `gh` CLI) or Trello (REST API) |
| Board abstraction | `ITaskBoardClient` interface |
| Orchestrator | C# / .NET 10 |
| Agent executor | Claude CLI subprocess (`--output-format stream-json` + `--json-schema`) |
| Git isolation | Git worktrees (`GitWorkspaceManager`) |
| Task files | `.aiboard/tasks/{id}.md` (ephemeral, gitignored) |

---

## Quick Start

### Prerequisites

- .NET 10 SDK
- Docker (for local PostgreSQL)
- `gh` CLI authenticated with `project` + `repo` scopes
- `claude` CLI installed and authenticated

### Start the database

```powershell
docker compose up -d
```

This launches PostgreSQL on `localhost:5432` and runs Flyway migrations automatically.
The default connection string in `appsettings.json` connects to this local instance.

### Run an agent on a card

```powershell
.\scripts\run_once.ps1 -CardId 3
```

### Run in polling mode (automatic pickup)

```powershell
.\scripts\run_polling.ps1
```

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
```

---

## Safety Model

- Agents cannot transition state directly -- the orchestrator validates all transitions.
- Manual approval gates block progression until a human moves the card.
- Automated gate checks (Haiku) validate agent output before state transitions.
- Agent execution happens in isolated git worktrees; the main repo is never modified.
- System merge includes conflict detection with retry logic (max 3 attempts).
- All agent output is posted as upserted comments per step (no spam).

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
