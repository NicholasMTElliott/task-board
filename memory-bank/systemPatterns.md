# System Patterns

## Architecture Overview

### Direct Agent Mode (current primary flow)
```
CLI invocation: dotnet run -- --mode agent --card-id N --board-id 1 --workspace .
    ↓
AgentRunner (C#)
    ↓ fetch cards
ITaskBoardClient (GitHub Projects / Trello / Stub)
    ↓ move to IN_PROGRESS column
    ↓ create git worktree, write task files
ClaudeAgentExecutor (claude CLI subprocess)
    ↓ parse structured output
Post-process: update card body, upsert comment, move to outcome column
```

### Queue-based Mode (legacy, for webhook-triggered flows)
```
Webhook → Cloudflare Worker → PGMQ on Neon → .NET EventProcessor → Orchestrator → Board API
```

> **Current focus:** Direct CLI agent mode with GitHub Projects. Queue-based flow preserved for future webhook integration.

## Core Design Patterns

### State Machine
Board columns (GitHub Projects status field / Trello lists) are the authoritative state of a card. Each column maps to exactly one role or manual gate. The AgentRunner determines transitions deterministically based on workflow config.

### IN_PROGRESS Transition
When an agent picks up a card from a "Ready for X" trigger column, it first moves the card to the "X-ing" in-progress column before starting work. This provides visual feedback on the board that work is happening. Defined as `"IN_PROGRESS": "Designing"` in the state's transitions map. Optional — if not defined, the agent runs in-place.

### ITaskBoardClient Abstraction
Provider-agnostic interface for all board operations:
- `GetCardAsync(cardId)` / `GetBoardCardsAsync(boardId)` — read cards
- `UpdateCardBodyAsync(cardId, body)` — write card content
- `MoveCardToColumnAsync(cardId, columnId)` — state transitions
- `UpsertAgentCommentAsync(cardId, body)` — agent feedback

Implementations: `GitHubProjectsClient` (via `gh` CLI), `TrelloClient` (HTTP), `StubTaskBoardClient` (testing).
Selection via `BOARD_PROVIDER` env var: `github`, `trello`/`live`, or default `stub`.

### Agent Contract
The agent executor (`ClaudeAgentExecutor`) uses `--json-schema` to enforce structured output:
```json
{
  "outcome": "COMPLETE | NEEDS_INFO | ERROR",
  "detail": "optional summary string",
  "questions": [
    {
      "question": "What is the target component?",
      "recommendations": ["Auth module", "API gateway"]
    }
  ]
}
```
- `outcome` is required; `detail` and `questions` are optional
- `questions` array is populated when outcome is `NEEDS_INFO`
- Each question has optional `recommendations` (suggested answers)
- Agents do NOT move cards. The orchestrator owns all transitions.
- The C# `AgentOutcome` enum uses: `COMPLETE`, `NEEDS_INFO`, `ERROR`
- The `AgentResult` record carries: `Outcome`, `Detail`, `Questions`
- Backward compat: parser also accepts `SUCCESS` → `COMPLETE`, `QUESTIONS` → `NEEDS_INFO`

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

### Dead-Letter / Max-Retry
Messages that exceed `MaxRetries` (default 3) are dead-lettered: archived to `pgmq.a_events` and removed from the active queue.
Detection uses `NpgmqMessage<T>.ReadCt` (PGMQ's built-in read counter).
Check happens before idempotency or processing — poison messages are caught early.
Configurable via `QueueProcessing:MaxRetries` in appsettings / environment.

### Migration Pattern
Schema migrations are SQL-first and versioned under `db/migrations` for Flyway execution.

Execution pattern:
- Migrations are applied through `scripts/migrate.ps1`, which invokes Flyway via Docker (`redgate/flyway`).
- This avoids local Flyway installation and keeps migration runtime aligned across developer machines.

Current tracked chain:
- `V1__processed_events.sql`
- `V2__pgmq_core.sql`
- `V3__pgmq_create_events_queue.sql`
- `V4__card_state.sql`
- `V5__run_log.sql`

Contingency fallback migration (`db/sql/0003_jobs_fallback.sql`) is intentionally manual and not part of the primary Flyway chain.

Fallback semantics (if PGMQ fails on Neon):
- `jobs.status = pending|claimed|succeeded|failed`
- claim via transactional `FOR UPDATE SKIP LOCKED`
- retry by returning failed jobs to pending with backoff

### Git Worktree Isolation
Agent execution uses git worktrees for isolated working directories:
- `GitWorkspaceManager.CreateWorktreeAsync(repoPath, branchName)` → returns worktree path
- Convention: `{repoPath}-worktrees/aiboard/{cardId}`
- Edge cases handled: worktree already exists (reuse), branch already exists (attach without `-b`), stale directory (prune + recreate)
- `.aiboard/` task files are gitignored and ephemeral — not committed to git
- `CommitAsync` uses `git add .` (respects `.gitignore`) with `HasStagedChangesAsync` check before committing
- Main repo working tree is never modified during agent execution

### Locking
Before execution: write lock `(cardId, runId)` to Postgres.
If lock exists → skip.
After execution: release lock.
Prevents duplicate agent runs from concurrent worker instances claiming different jobs for the same card.

### Human-in-the-Loop
`NEEDS_INFO` outcome → card moved to Questions sidebar list, `WaitingOnHuman = true` and `origin_list_id` set in `card_state`.
**Re-trigger (v1 rule):** operator moves card back to prior agent state. `origin_list_id` enables the orchestrator to validate the return move.
Comment-marker re-trigger is explicitly out of scope for v1.

### Manual Gates
"Designed" and "Ready for Implementation" are passive columns. The orchestrator takes no action on cards in these states. Only operator card movement triggers the next agent.

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

### Claude CLI Subprocess Pattern
The .NET worker can invoke the Claude CLI (`claude`) as a subprocess via `ClaudeCliLlmClient`.

**Windows invocation:**
- Set `startInfo.FileName = "claude.cmd"` directly with `ArgumentList`
- Do NOT use `cmd.exe /c claude` (mangles quoted arguments) or PowerShell wrappers (unnecessary indirection)
- Remove `CLAUDECODE` env var from subprocess environment or the CLI refuses to run as a subprocess
- **Argument ordering:** Flag-style args (`--model`, `--output-format`, `--permission-mode`, etc.) must come BEFORE content args (`--system-prompt`, `-p`). On Windows, `claude.cmd` runs through `cmd.exe` which misparses double quotes — if a content arg with quotes appears early, all subsequent flags are corrupted.
- **No double quotes in prompts:** Prompt templates must not contain `"` characters. Use plain text or single quotes instead. Double quotes in `-p` or `--system-prompt` values trigger `cmd.exe` quote-state mangling.

**CLI flags (required for structured output):**
- `--output-format json` — returns JSON envelope with `result` and `structured_output` fields
- `--json-schema <minified-json>` — schema must be single-line (minified); produces `structured_output` object in response
- `--max-budget-usd` — cost control per invocation (preferred over `--max-turns`)
- `--permission-mode bypassPermissions --allowedTools *` — headless execution without permission prompts
- Do NOT use `--max-turns` — causes premature termination before structured output is produced

**Prompt constraints:**
- Prompts passed as CLI arguments must not contain literal newlines — escape them as `\\n`
- Schema instruction in system prompt is redundant when `--json-schema` is used (the CLI handles structured output natively)

**Output parsing (`ParseResult`):**
- Primary path: parse `structured_output` object from JSON envelope → extract `outcome`, `detail`, `questions`
- Fallback: parse `result` string field, stripping markdown fences if present
- Last resort: treat raw stdout as content, strip markdown fences
- Returns `AgentResult` record with typed `Outcome`, `Detail`, and `Questions` list

**Timeout observability:**
- Use event-based capture (`OutputDataReceived`/`ErrorDataReceived` + `StringBuilder`) instead of `ReadToEndAsync`
- `ReadToEndAsync` blocks until the process exits — unusable for partial output on timeout
- On timeout, partial stdout/stderr is included in the exception message for diagnostics

## Component Relationships

| Component | Responsibility |
|-----------|----------------|
| GitHub Projects / Trello | Human UI, planning content, state via column position |
| ITaskBoardClient | Provider-agnostic board abstraction |
| GitHubProjectsClient | GitHub Projects v2 via `gh` CLI (GraphQL + REST) |
| TrelloClient | Trello REST API |
| AgentRunner | Direct agent execution: fetch cards → worktree → agent → post-process |
| ClaudeAgentExecutor | Claude CLI subprocess with `--json-schema` structured output |
| GitWorkspaceManager | Git worktree lifecycle for isolated agent execution |
| TaskFileManager | Write board cards as `.aiboard/tasks/{id}.md` files |
| Cloudflare Worker | Webhook ingestion (legacy queue path) |
| Neon Postgres | Queue storage + idempotency + run metadata (legacy queue path) |
| workflow.github.json | Workflow config for GitHub Projects |
| workflow.v1.json | Workflow config for Trello (legacy) |

## Workflow Configuration
**File-based** — `workflow.github.json` or `workflow.v1.json` in repository, selected via `WORKFLOW_CONFIG_PATH` env var.

Schema:
```json
{
  "states": {
    "<column_name>": {
      "name": "<display_name>",
      "role": "<role_key>",
      "gateType": "agent_run | manual_gate | manual_entry | in_progress | holding | terminal",
      "taskPrompt": "<prompt with {TaskName} {TaskId} placeholders>",
      "transitions": {
        "IN_PROGRESS": "<in_progress_column>",
        "COMPLETE": "<next_column>",
        "NEEDS_INFO": "<questions_column>",
        "ERROR": "<error_column>"
      }
    }
  },
  "roles": {
    "<role_key>": {
      "model": "claude-sonnet-4-6",
      "systemPrompt": "...",
      "sections": ["Technical Design", "Decisions"]
    }
  }
}
```

State keys are **column names** for GitHub Projects (status option names) or **list IDs** for Trello.

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
| action_id | Trello action ID (PK) |
| processed_at_utc | Timestamp (default `now()`) |

### card_state
| Column | Description |
|--------|-------------|
| card_id | Trello card ID (PK) |
| last_processed_event | Last processed action ID |
| current_lock | Active run lock ID |
| last_known_list | Last confirmed list ID |
| origin_list_id | List the card was in before moving to Questions (for return-path validation) |
| waiting_on_human | Boolean flag |
| updated_at_utc | Last update timestamp |

### run_log
| Column | Description |
|--------|-------------|
| run_id | UUID (PK) |
| card_id | Trello card ID |
| role | Agent role executed |
| input_hash | Hash of card state at execution time |
| output_hash | Hash of agent output |
| outcome | COMPLETE / NEEDS_INFO / BLOCKED / ERROR |
| created_at_utc | Execution time |
