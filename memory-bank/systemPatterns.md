# System Patterns

## Architecture Overview

```
Trello Webhook
    ↓
Cloudflare Worker (HTTP endpoint, webhook receiver)
    ↓
Neon Postgres queue (PGMQ SQL-only install confirmed)
    ↓
Reusable .NET processor core (one/wait/loop modes) with host adapters (local CLI, Lambda Function URL, future container)
    ↓
OpenAI API (LLM)
    ↓
Trello API (card updates, comment writes, list moves)
    ↓
Postgres (queue tables, processed_events, card_state, run_log)
```

> **Prototype pivot:** Cloudflare Worker + Neon + reusable .NET processor adopted for cost-first validation.
> **Queue backend:** PGMQ-on-Neon confirmed via SQL-only install script.

## Core Design Patterns

### State Machine
Trello lists are the authoritative state of a card. Each list maps to exactly one role or manual gate. The orchestrator determines transitions deterministically based on workflow config.

### Agent Contract
Every agent returns a structured JSON payload:
```json
{
  "updates": {
    "requirements": "...",
    "design": "...",
    "decisions": "...",
    "questions": []
  },
  "summary_comment": "...",
  "outcome": "COMPLETE | NEEDS_INFO | BLOCKED",
  "approval_required": true | false
}
```
Agents do NOT move cards. The orchestrator owns all transitions.

### Webhook Authentication
Trello webhooks are validated using HMAC-SHA1 signature verification:
- Trello sends `x-trello-webhook` header with Base64-encoded HMAC-SHA1 digest
- Digest is computed over `(request body + callback URL)` using `TRELLO_API_SECRET` as key
- Worker performs constant-time comparison to prevent timing attacks
- HEAD requests to `/webhooks/trello` return 200 (Trello URL verification)
- Non-card events (board, list, member changes) are accepted but not enqueued

### Idempotency
Idempotency key: **Trello `actionId`** (from webhook payload) — unique per event at source.  
A unique constraint on the `processed_events` table enforces exactly-once execution.  
If `actionId` already exists → skip entire execution before any agent invocation.

### Queue Backend Strategy (Prototype)
Queue backend uses **PGMQ on Neon** with SQL-only install script applied per environment.  
Fallback `jobs` table remains available as contingency.

Current PGMQ semantics:
- `pgmq.send()` — enqueue webhook event (raw JSON payload)
- `pgmq.read()` with visibility timeout — claim a message (replaces `FOR UPDATE SKIP LOCKED`)
- `pgmq.delete()` — ack on successful execution
- `pgmq.archive()` — move to audit/replay archive table on completion (replaces custom dead-letter)
- Visibility timeout auto-returns unacked messages for retry

Current implementation note:
- .NET queue repository now uses strongly typed Npgmq methods (`ReadBatchAsync<string>`, `DeleteAsync`, `ArchiveAsync`) to avoid reflection and improve AOT/trimming compatibility.

### Migration Pattern
Schema migrations are SQL-first and versioned under `db/migrations` for Flyway execution.

Execution pattern:
- Migrations are applied through `scripts/migrate.ps1`, which invokes Flyway via Docker (`redgate/flyway`).
- This avoids local Flyway installation and keeps migration runtime aligned across developer machines.

Current tracked chain:
- `V1__processed_events.sql`
- `V2__pgmq_core.sql`
- `V3__pgmq_create_events_queue.sql`

Contingency fallback migration (`db/sql/0003_jobs_fallback.sql`) is intentionally manual and not part of the primary Flyway chain.

Fallback semantics (if PGMQ fails on Neon):
- `jobs.status = pending|claimed|succeeded|failed`
- claim via transactional `FOR UPDATE SKIP LOCKED`
- retry by returning failed jobs to pending with backoff

### Locking
Before execution: write lock `(cardId, runId)` to Postgres.  
If lock exists → skip.  
After execution: release lock.  
Prevents duplicate agent runs from concurrent worker instances claiming different jobs for the same card.

### Human-in-the-Loop
`NEEDS_INFO` outcome → card moved to Questions sidebar list, `WaitingOnHuman = true` set in `card_state`.  
**Re-trigger (v1 rule):** operator moves card back to prior agent state. Comment-marker re-trigger is explicitly out of scope for v1.

### Manual Gates
Requirements Review, Design Review, and Code Review are passive lists. The orchestrator takes no action on cards in these states. Only operator card movement triggers the next agent.

### Card Description Structure
Agents read and write structured sections inside the card description:
```
# Requirements
# Decisions
# Technical Design
# Open Questions
# Acceptance Criteria
```
Agents update only their designated sections.

### Comment Strategy
One "Agent Status" comment per run — upserted, not appended. Prevents notification spam.

## Component Relationships

| Component | Responsibility |
|-----------|----------------|
| Trello | Human UI, planning content, state via list position |
| Cloudflare Worker | Webhook ingestion, auth validation, enqueue, Lambda kick |
| Neon Postgres | Queue storage + idempotency + run metadata |
| .NET Processor Core (C#) | Queue claim/ack, idempotency enforcement, orchestration execution |
| Host Adapters | Local CLI, Lambda Function URL, future container runtime |
| OpenAI | LLM for all agent roles in v1 |
| Postgres | queue tables, card_state, run_log, processed_events |
| workflow.v1.json | Workflow config (file-based for POC) |

## Workflow Configuration
**POC: file-based** — `workflow.v1.json` in repository, loaded at Lambda startup.  
This eliminates early DB config complexity. Migration to DB-stored config is a future step once the pipeline is stable.

Schema:
```json
{
  "states": {
    "<list_id>": {
      "role": "<role_key>",
      "transitions": {
        "NEEDS_INFO": "<questions_list_id>",
        "COMPLETE": "<next_list_id>"
      }
    }
  },
  "roles": {
    "<role_key>": {
      "model": "gpt-4.1",
      "system_prompt": "...",
      "tools": []
    }
  }
}
```

## Database Tables

### queue tables
Queue tables are managed by PGMQ in `pgmq` schema:

| Table | Purpose |
|-------|---------|
| `pgmq.q_events` | Active event queue (pending/claimed messages) |
| `pgmq.a_events` | Archive table (completed/dead-lettered messages for audit/replay) |

If runtime issues emerge later, fallback is application-managed `jobs` table with claim/update semantics.

In both paths, `actionId` uniqueness is enforced by application-layer `processed_events` constraint.

### processed_events (idempotency)
| Column | Description |
|--------|-------------|
| actionId | Trello action ID (PK) |
| processedAt | Timestamp |

### card_state
| Column | Description |
|--------|-------------|
| cardId | Trello card ID |
| lastProcessedEvent | Idempotency key |
| currentLock | Active run lock |
| lastKnownList | Last confirmed list ID |
| waitingOnHuman | Boolean flag |

### run_log
| Column | Description |
|--------|-------------|
| runId | UUID |
| cardId | Trello card ID |
| role | Agent role executed |
| inputHash | Hash of card state at execution time |
| outputHash | Hash of agent output |
| outcome | COMPLETE / NEEDS_INFO / BLOCKED |
| timestamp | Execution time |
