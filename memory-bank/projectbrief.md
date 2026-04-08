# Project Brief: AI Kanban Agent Orchestrator

## Purpose
Turn a kanban board (GitHub Projects, Trello, or other providers) into an asynchronous, state-driven control plane where autonomous AI agents act as specialized SDLC roles (Senior Engineer, QA), automatically progressing tickets through a structured pipeline.

## v1 Success Target
A single founder/operator can place a card in a "Ready for" column on their kanban board, run a CLI command, and the system will autonomously execute the appropriate agent, update the card, and move it to the next state — pausing at manual approval gates for human confirmation.

## v1 Scope

### In Scope
- Full end-to-end pipeline from Backlog to Done (including automated merge)
- Multi-step agent execution within states (e.g., implement → code review)
- Senior Engineer, QA, Implementer, Code Reviewer, Gate Checker, Estimator, and Merge Resolver roles
- Manual approval gates at Designed and Tested
- Provider-agnostic board abstraction (`ITaskBoardClient`) — supports GitHub Projects and Trello
- IN_PROGRESS transitions (card moves to "X-ing" column while agent works)
- Human-in-the-loop via Questions holding columns
- Direct CLI invocation (`--mode agent --card-id N`) and polling mode (`--mode polling`)
- Git worktree isolation for agent execution
- Gate checks after design, implementation, and test steps
- Optional specialist-reviewer steps triggered by gate checks
- Single board, single operator

### Out of Scope (v1)
- Webhook-triggered automation (currently manual CLI invocation or polling)
- Parallel agent branches
- SLA timers / retry policies
- State replay
- Multi-board / multi-tenant support
- Mission Control dashboard
- Complex RBAC
- Autonomous production deployment

## Active State Pipeline (GitHub Projects)
| # | State | Role(s) | Gate Type |
|---|-------|---------|-----------|
| 1 | Backlog | — | Manual entry |
| 2 | Ready for Design | Senior Engineer + Estimator (4 steps + optional specialist reviews) | agent_run |
| 3 | Designing | — | In-progress |
| 4 | Design Questions | — | Holding (NEEDS_INFO) |
| 5 | Designed | — | Manual gate |
| 6 | Ready for Tasking | Senior Engineer (story decomposition + best-guess estimates) | agent_run |
| 7 | Tasking | — | In-progress |
| 8 | Waiting for Tasks | — | holding (event-driven via completeParentIfReady) |
| 9 | Ready for Implementation | Implementer + Code Reviewer (2 steps + optional specialist reviews) | agent_run |
| 10 | Implementing | — | In-progress |
| 11 | Implementation Questions | — | Holding (NEEDS_INFO) |
| 12 | Ready for Test | QA + Doc Updater (2 steps + optional specialist reviews) | agent_run |
| 13 | Testing | — | In-progress |
| 14 | Tested | — | Manual gate |
| 15 | Approved | — | system_merge |
| 16 | Merging | — | In-progress |
| 17 | Done | — | Terminal |
| 15 | Error | — | Holding |
