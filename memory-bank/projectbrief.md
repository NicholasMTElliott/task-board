# Project Brief: AI Kanban Agent Orchestrator

## Purpose
Turn a Trello board into an asynchronous, state-driven control plane where autonomous AI agents act as specialized SDLC roles (Business Analyst, Senior Engineer, QA), automatically progressing tickets through a structured pipeline.

## v1 Success Target
A single founder/operator can place a card in Requirements on their Trello board and the system will autonomously drive it through every agent-run state to Done, pausing at each manual approval gate for human confirmation.

## v1 Scope

### In Scope
- Full end-to-end pipeline from Backlog to Done
- Business Analyst, Senior Engineer, and QA agent roles
- Manual approval gates at Requirements Review, Design Review, and Code Review
- Idempotent event processing (no duplicate runs)
- Human-in-the-loop via card movement as re-trigger
- Questions/NEEDS_INFO handling
- Single Trello board, single operator

### Out of Scope (v1)
- GitHub / PR automation
- Parallel agent branches
- SLA timers / retry policies
- State replay
- Multi-board / multi-tenant support
- Mission Control dashboard
- Complex RBAC
- Autonomous production deployment
- Final production cloud topology (prototype currently runs Cloudflare + Neon + AWS)

## v1 State Pipeline
| # | State | Role | Gate Type |
|---|-------|------|-----------|
| 1 | Backlog | — | Manual entry |
| 2 | Requirements | Business Analyst | Agent-run |
| 3 | Requirements Review | — | Manual gate |
| 4 | Design | Senior Engineer | Agent-run |
| 5 | Design Review | — | Manual gate |
| 6 | Implementation | Senior Engineer | Agent-run |
| 7 | Code Review | — | Manual gate |
| 8 | Testing | QA | Agent-run |
| 9 | Done | — | Terminal |
