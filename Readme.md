# AI Kanban Agent Orchestrator

An autonomous, state-driven multi-agent workflow system built on top of a kanban board.

This project turns a GitHub Projects board (or Trello) into an asynchronous control plane where AI agents act as specialized SDLC roles (Senior Engineer, QA), automatically progressing tickets through a structured pipeline.

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
CLI invocation: .\scripts\run_once.ps1 -CardId N
    |
AgentRunner (C#)
    | fetch cards
ITaskBoardClient (GitHub Projects / Trello / Stub)
    | move to IN_PROGRESS column
    | create git worktree, write task files
ClaudeAgentExecutor (claude CLI subprocess)
    | parse structured output (NDJSON stream)
Post-process: upsert comment, move to outcome column
```

---

## Board Structure (GitHub Projects)

| # | Column | Role | Gate Type |
|---|--------|------|-----------|
| 1 | Backlog | -- | Manual entry |
| 2 | Ready for Design | Senior Engineer | Agent trigger |
| 3 | Designing | -- | In-progress |
| 4 | Design Questions | -- | Holding (NEEDS_INFO) |
| 5 | Designed | -- | Manual gate |
| 6 | Ready for Implementation | Senior Engineer | Agent trigger |
| 7 | Implementing | -- | In-progress |
| 8 | Implementation Questions | -- | Holding (NEEDS_INFO) |
| 9 | Ready for Test | QA | Agent trigger |
| 10 | Testing | -- | In-progress |
| 11 | Tested | -- | Terminal |
| 12 | Error | -- | Holding |

Each column maps to exactly one agent role or manual gate.

---

## How It Works

1. Operator creates an issue, adds it to the project board in **Backlog**.
2. Operator writes requirements and moves card to **Ready for Design**.
3. Operator runs: `.\scripts\run_once.ps1 -CardId 3`
4. Senior Engineer agent moves card to **Designing**, produces a Technical Design, moves to **Designed**.
5. Operator reviews design, approves by moving to **Ready for Implementation**.
6. Operator runs CLI again. Agent moves to **Implementing**, writes code in an isolated worktree, commits and pushes, moves to **Ready for Test**.
7. Operator runs CLI again. QA agent validates the implementation, moves to **Tested** on success.

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
1. Posts the `detail` as a markdown comment on the issue.
2. Transitions the card to the next column based on `outcome`.
3. Agents do NOT move cards directly -- the orchestrator controls all transitions.

---

## Workflow Configuration

File-based config (`workflow.github.json`) maps columns to roles and transitions:

```json
{
  "states": {
    "Ready for Design": {
      "role": "senior_engineer",
      "gateType": "agent_run",
      "gitBehavior": "discard",
      "taskPromptFile": "prompts/states/ready_for_design.md",
      "providerParams": { "effort": "max" },
      "transitions": {
        "IN_PROGRESS": "Designing",
        "COMPLETE": "Designed",
        "NEEDS_INFO": "Design Questions",
        "ERROR": "Error"
      }
    }
  },
  "roles": {
    "senior_engineer": {
      "model": "claude-opus-4-6",
      "systemPromptFile": "prompts/senior_engineer.md",
      "sections": ["Technical Design", "Decisions", "Implementation"]
    }
  }
}
```

- `providerParams` passes executor-specific flags (e.g., `effort` for Claude CLI)
- `gitBehavior`: `discard` (design/test), `commit_and_push` (implementation)
- `taskPromptFile` / `systemPromptFile` point to markdown files under `prompts/`

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
- `gh` CLI authenticated with `project` + `repo` scopes
- `claude` CLI installed and authenticated

### Run an agent on a card

```powershell
.\scripts\run_once.ps1 -CardId 3
```

Or manually with env vars:

```powershell
$env:BOARD_PROVIDER = "github"
$env:AGENT_EXECUTOR = "claude-cli"
$env:WORKFLOW_CONFIG_PATH = "workflow.github.json"
$env:GitHubProjects__Owner = "YourGitHubUser"
$env:GitHubProjects__Repo = "YourUser/your-repo"
$env:GitHubProjects__ProjectNumber = "1"

dotnet run --project lambda/src/TaskBoard.Worker -- --mode agent --card-id 3 --board-id 1 --workspace .
```

---

## Safety Model

- Agents cannot transition state directly -- the orchestrator validates all transitions.
- Manual approval gates block progression until a human moves the card.
- Agent execution happens in isolated git worktrees; the main repo is never modified.
- All agent output is posted as a single upserted comment (no spam).

---

## Cost Strategy

Idle cost: $0. Costs scale only when an agent executes (LLM tokens are the primary cost driver).

---

## Future Expansion

- Webhook-triggered automation (currently manual CLI invocation)
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
