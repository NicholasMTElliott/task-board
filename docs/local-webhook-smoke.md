# Local Webhook Smoke + Contract Validation

This document defines the local validation workflow for Trello webhook ingestion through Neon queue write and .NET processor execution.

## Prerequisites

- `/.env.local` populated from `.env.example`
- Docker running (for Flyway migration step)
- Worker running locally:

```bash
cd worker
npx wrangler dev
```

## End-to-End Smoke (enqueue + process)

Run from repo root:

```bash
pwsh -File scripts/run-local-webhook-smoke.ps1
```

What it does:

1. Runs migrations (unless `-SkipMigrate`)
2. Resets queue state for local test IDs (`act_%`) unless `-SkipReset`
3. Syncs local secrets
4. Simulates Trello webhook POST (`/webhooks/trello`)
5. Verifies queue row exists in `pgmq.q_<queue>`
6. Runs `.NET` processor once (`--mode one`) unless `-SkipDotnetDrain`
7. Verifies `processed_events` row exists

Useful flags:

- `-SkipMigrate`
- `-SkipReset`
- `-SkipDotnetDrain`
- `-DotnetRunTimeoutSeconds 30`
- `-WorkerUrl http://127.0.0.1:8787/webhooks/trello`
- `-PayloadPath tests/fixtures/trello/webhook.valid.json`

Queue reset utility can be run directly:

```bash
pwsh -File scripts/reset-queue-state.ps1
```

Default behavior deletes only local synthetic action IDs (`act_%`) from:
- `pgmq.q_<queue>`
- `pgmq.a_<queue>`
- `processed_events`

To clear all rows for the configured queue and all `processed_events` rows, use:

```bash
pwsh -File scripts/reset-queue-state.ps1 -IncludeAll
```

When drain is enabled, each `run-dotnet` attempt is now timeout-guarded. If a run exceeds the configured timeout, the smoke script fails with a non-zero exit (`124`).

## Worker Contract Checks

Run from repo root:

```bash
pwsh -File scripts/run-worker-contract-tests.ps1
```

Current contract checks:

- valid webhook returns `200`
- missing `action.id` returns `400`
- invalid webhook secret returns `401`

## Individual Utilities

### Simulate one webhook call

```bash
pwsh -File scripts/simulate-trello-webhook.ps1 -PayloadPath tests/fixtures/trello/webhook.valid.json
```

### Verify queue/processed state for specific action

```bash
pwsh -File scripts/verify-queue-state.ps1 -ActionId act_local_smoke_123 -RequireQueueFound
pwsh -File scripts/verify-queue-state.ps1 -ActionId act_local_smoke_123 -RequireProcessedFound
```

## Latest Validation Snapshot

Date: `2026-03-03`

- Duplicate/idempotency test: **PASS**
  - ActionId: `act_dup_resume4_1772569015496`
  - Outcome: processed once, duplicate observed in drain logs, queue cleared
- Concurrency test: **PASS**
  - Prefix: `act_conc_resume3_1772569025829`
  - Outcome: 6/6 processed, 0 remaining in queue
- Idle-wake bounded execution: **PASS**
  - ActionId: `act_idle_resume3_1772569066380`
  - Outcome: idle loop bounded by timeout (`124`), follow-up event processed and cleared
