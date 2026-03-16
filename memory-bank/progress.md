# Progress

## Current Status
🟡 **Prototype runtime in progress** — PGMQ-on-Neon confirmed; .NET Npgmq-based processor core implemented.

## What Works
- README fully authored and committed
- v1 scope, stack, and pipeline confirmed
- POC minimum architecture finalized (PGMQ SQL-only + Npgmq, actionId idempotency, file-based config)
- Cloudflare Worker scaffold created (`/health`, `/webhooks/trello`, auth + lambda-kick flow)
- Lambda worker now includes reusable processor core modes (`one`, `wait`, `loop`) and `/drain` host adapter
- SQL scripts created for `processed_events`, PGMQ SQL-only install (`0002_pgmq_create.sql`), and fallback `jobs` queue
- Prototype docs added (`docs/prototype-config.md`, `docs/pgmq-neon-spike-results.md`)
- Npgmq library integrated in .NET project
- Worker enqueue write to Neon PGMQ implemented (`pgmq.send` in webhook flow)
- Worker supports optional Lambda kick (skip when URL unset; fail-open with error logging when configured kick fails)
- Queue processing defaults are now configurable in Lambda via `QueueProcessing` settings (batch size, visibility timeout, loop idle delay)
- `/drain` now supports optional `visibilityTimeoutSeconds` in addition to `batchSize`, with fallback to configured defaults
- Local webhook smoke and contract validation harness scripts added (PowerShell + Node verifier)
- Trello webhook payload fixtures added for valid and missing-action-id cases
- Local validation docs added in `docs/local-webhook-smoke.md` and linked from prototype config
- Local smoke scripts hardened for shell/runtime compatibility (`pwsh` fallback, PowerShell 5 web-request handling)
- Worker enqueue path now accepts string `message_id` values returned by Neon driver
- Local smoke script executed successfully against local Worker and Neon dev DB (with `-SkipMigrate`)
- Local script safety improved: `scripts/local-secrets.ps1 -Action run-dotnet` now supports timeout guard (`-RunTimeoutSeconds`) and mode-first execution (`-Mode one|wait|loop`)
- Timeout behavior validated locally:
  - `-Mode loop -RunTimeoutSeconds 2` exits with timeout code `124`
  - `-Mode one -RunTimeoutSeconds 30` completes successfully (`0`) when a queued message exists
- Resumed local runtime validation completed with timeout guards enabled:
  - Duplicate/idempotency test passed (`act_dup_resume4_1772569015496`)
  - Concurrency processing test passed (`act_conc_resume3_1772569025829`, 6/6 processed)
  - Idle-wake bounded execution test passed (`act_idle_resume3_1772569066380`, timeout `124` + successful follow-up processing)
- Memory bank synchronized to current prototype architecture and runtime state
- Single-source local secret workflow added:
  - root `.env.example` template
  - gitignored `/.env.local`
  - `scripts/local-secrets.ps1` for env sync + local .NET runs + Worker `.dev.vars` generation
  - documented usage in `docs/prototype-config.md`
- Queue repository is now strongly typed (reflection removed) for better AOT/trimming compatibility.
- Flyway migrations are now designed to run via Docker through `scripts/migrate.ps1` (no local Flyway install required).
- Flyway Docker migration flow has been executed successfully against Neon; schema now at version `v3` (`processed_events`, `pgmq core`, `events` queue create).
- Automated queue reset flow is now implemented for local smoke runs:
  - `scripts/reset-queue-state.ps1` + `worker/scripts/reset-queue-state.mjs`
  - default cleanup scope targets synthetic local IDs (`act_%`) in active queue, archive queue, and `processed_events`
  - optional full reset via `-IncludeAll`
  - integrated into `scripts/run-local-webhook-smoke.ps1` with opt-out `-SkipReset`
- Full local webhook smoke pass (default reset flow) has now been re-validated end-to-end:
  - `act_local_smoke_1772642809008` and `act_local_smoke_1772642879195`
  - migration check passed (schema at `v3`)
  - webhook simulation returned `200`
  - queue verification found enqueued message
  - drain processed exactly one message with zero duplicates/failures
  - processed event persisted

## What Is Left to Build

### Infrastructure
- [ ] Trello board created with 9 pipeline lists + Questions list (list IDs captured)
- [x] Cloudflare Worker webhook scaffold
- [x] Neon queue backend decision (PGMQ spike pass/fail)
- [x] Execute PGMQ-on-Neon smoke test (`pgmq.create/send/read/delete/archive`)
- [x] Initial idempotency schema (`processed_events`) created
- [ ] IaC definition (Terraform / SAM / CDK — not yet decided)

### Core Orchestrator (Lambda / C#)
- [x] Worker entry point scaffold (`/drain` handler)
- [x] Webhook handler scaffold in Worker (auth, parse, kick)
- [x] Idempotency insert-once implementation (`processed_events`)
- [x] Implement Neon enqueue write from Worker
- [x] Support local-only enqueue flow by making Lambda kick optional in Worker
- [~] Implement queue claim/ack/fail with Npgmq in Lambda repositories (configurable read defaults added)
- [x] Generic processor runtime modes (`one`, `wait`, `loop`)
- [ ] Locking mechanism
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
- [x] Local webhook simulation harness (enqueue + process verification) implemented and executed successfully
- [x] Automated queue cleanup/reset strategy added before smoke runs
- [x] Full local smoke pass using default reset flow executed and verified
- [ ] BA loop smoke test (Requirements → Questions → Requirements → Requirements Review)
- [ ] Full pipeline smoke test (Backlog to Done)

## Known Issues
- Local tooling expects `/.env.local`; using a different filename requires passing `-EnvFile` to `scripts/local-secrets.ps1`.
- Local run requires PGMQ bootstrap in target Neon DB (`0002_pgmq_create.sql` + `pgmq.create(...)`), otherwise read path fails with `schema "pgmq" does not exist`.
- Docker daemon must be running before executing `scripts/migrate.ps1`.
- Flyway may print a sign-in prompt; this is informational and not required for migration success in this project.
- `-Mode one` in local runs can still block until queue read returns; use `-RunTimeoutSeconds` to force bounded execution in scripts.

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
| Trello board set up | ⬜ Not started |
| Queue backend implementation | ⬜ In progress |
| Webhook receiver live | ⬜ In progress |
| BA loop validated | ⬜ Not started |
| Full pipeline validated | ⬜ Not started |
