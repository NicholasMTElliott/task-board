# Project Brief: AI Kanban Agent Orchestrator

## Purpose
Turn a kanban board (GitHub Projects, Trello, or other providers) into an asynchronous, state-driven control plane where autonomous AI agents act as specialized SDLC roles (Senior Engineer, QA), automatically progressing tickets through a structured pipeline.

## v1 Success Target
A single founder/operator can place a card in a "Ready for" column on their kanban board, run a CLI command, and the system will autonomously execute the appropriate agent, update the card, and move it to the next state — pausing at manual approval gates for human confirmation.

## v1 Scope

### In Scope
- Full end-to-end pipeline from Backlog to Tested
- Senior Engineer and QA agent roles
- Manual approval gates at Designed and Ready for Implementation
- Provider-agnostic board abstraction (`ITaskBoardClient`) — supports GitHub Projects and Trello
- IN_PROGRESS transitions (card moves to "X-ing" column while agent works)
- Human-in-the-loop via Questions holding columns
- Questions/NEEDS_INFO handling with per-phase question columns
- Direct CLI invocation (`--mode agent --card-id N`)
- Git worktree isolation for agent execution
- Single board, single operator

### Out of Scope (v1)
- Webhook-triggered automation (currently manual CLI invocation)
- Parallel agent branches
- SLA timers / retry policies
- State replay
- Multi-board / multi-tenant support
- Mission Control dashboard
- Complex RBAC
- Autonomous production deployment

## Active State Pipeline (GitHub Projects)
| # | State | Role | Gate Type |
|---|-------|------|-----------|
| 1 | Backlog | — | Manual entry |
| 2 | Ready for Design | Senior Engineer | Trigger (agent_run) |
| 3 | Designing | — | In-progress |
| 4 | Design Questions | — | Holding (NEEDS_INFO) |
| 5 | Designed | — | Manual gate |
| 6 | Ready for Implementation | Senior Engineer | Trigger (agent_run) |
| 7 | Implementing | — | In-progress |
| 8 | Implementation Questions | — | Holding (NEEDS_INFO) |
| 9 | Ready for Test | QA | Agent-run |
| 10 | Tested | — | Terminal |
| 11 | Error | — | Holding |
