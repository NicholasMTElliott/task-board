# Progress

## Current Status
🟡 **Architecture hardened, ready for orchestration** — Critical issues resolved; unit tests added; next step is orchestration pipeline.

## What Works
- README fully authored and committed
- v1 scope, stack, and pipeline confirmed
- POC minimum architecture finalized (PGMQ SQL-only + Npgmq, actionId idempotency, file-based config)
- Cloudflare Worker with proper Trello HMAC-SHA1 webhook validation, HEAD handling, non-card event filtering
- Lambda worker with reusable processor core modes (`one`, `wait`, `loop`) and `/drain` host adapter
- Flyway migration chain: V1 (processed_events), V2 (pgmq core), V3 (events queue), V4 (card_state), V5 (run_log)
- Prototype docs added (`docs/prototype-config.md`, `docs/pgmq-neon-spike-results.md`)
- Npgmq library integrated; queue repository archives before deleting (audit trail preserved)
- Worker enqueue write to Neon PGMQ implemented (`pgmq.send` in webhook flow)
- Worker supports optional Lambda kick (skip when URL unset; fail-open with error logging)
- Queue processing defaults configurable via `QueueProcessing` settings
- Single-source local secret workflow (`scripts/local-secrets.ps1`, `.env.example`)
- Flyway Docker migration flow executed successfully against Neon (schema at v3; v4/v5 pending apply)
- `workflow.v1.json` created with all 10 states and 3 agent roles (placeholder list IDs)
- Interfaces extracted (`IQueueRepository`, `IProcessedEventsRepository`) for testability
- Unit test project with 10 passing tests (EventProcessor + QueueProcessingOptions)

## What Is Left to Build

### Infrastructure
- [ ] Trello board created with 9 pipeline lists + Questions list (list IDs captured)
- [x] Cloudflare Worker webhook scaffold (with HMAC-SHA1 validation)
- [x] Neon queue backend decision (PGMQ spike pass/fail)
- [x] Execute PGMQ-on-Neon smoke test
- [x] Initial idempotency schema (`processed_events`) created
- [x] card_state and run_log migrations created (V4, V5)
- [ ] Apply V4/V5 migrations to Neon
- [ ] IaC definition (Terraform / SAM / CDK — not yet decided)

### Core Orchestrator (Lambda / C#)
- [x] Worker entry point scaffold (`/drain` handler)
- [x] Webhook handler with HMAC-SHA1 auth, parse, kick
- [x] Idempotency insert-once implementation (`processed_events`)
- [x] Implement Neon enqueue write from Worker
- [x] Support local-only enqueue flow by making Lambda kick optional in Worker
- [x] Implement queue claim/ack/fail with Npgmq (archive-first, configurable defaults)
- [x] Generic processor runtime modes (`one`, `wait`, `loop`)
- [x] workflow.v1.json created (placeholder list IDs)
- [ ] Locking mechanism (card_state.current_lock)
- [ ] Workflow config loader
- [ ] State → role resolver
- [ ] Card description parser (structured sections)
- [ ] Card description writer
- [ ] Comment upsert (single Agent Status comment)
- [ ] Card list mover (orchestrator-controlled transitions)
- [ ] Agent contract enforcer / output validator

### Agent Roles
- [ ] Business Analyst agent
- [ ] Senior Engineer agent (Design state)
- [ ] Senior Engineer agent (Implementation state)
- [ ] QA agent

### Human-in-the-Loop
- [ ] NEEDS_INFO → Questions card move
- [ ] Re-trigger mechanism on card return

### Validation
- [x] Unit tests for EventProcessor and QueueProcessingOptions (10 passing)
- [ ] BA loop smoke test (Requirements → Questions → Requirements → Requirements Review)
- [ ] Full pipeline smoke test (Backlog to Done)

## Known Issues
- Local tooling expects `/.env.local`; using a different filename requires passing `-EnvFile` to `scripts/local-secrets.ps1`.
- Docker daemon must be running before executing `scripts/migrate.ps1`.
- `MarkFailedAsync` is a no-op — failing messages retry indefinitely (no max-retry or dead-letter).
- `PGMQ_QUEUE_NAME` read from environment directly in `QueueRepository` instead of via options binding.
- `docs/prototype-config.md` references old env var `TRELLO_WEBHOOK_SECRET` — needs update to `TRELLO_API_SECRET` + `TRELLO_WEBHOOK_CALLBACK_URL`.

## Milestones
| Milestone | Status |
|-----------|--------|
| Architecture defined | ✅ Done |
| Memory bank initialized | ✅ Done |
| POC minimum architecture agreed | ✅ Done |
| Prototype architecture pivot agreed | ✅ Done |
| Webhook/lambda scaffold created | ✅ Done |
| PGMQ-on-Neon spike | ✅ Done |
| Npgmq processor runtime modes implemented | ✅ Done |
| Architecture review & hardening | ✅ Done |
| Unit test suite | ✅ Done |
| Trello board set up | ⬜ Not started |
| Orchestration pipeline | ⬜ Not started |
| BA loop validated | ⬜ Not started |
| Full pipeline validated | ⬜ Not started |
