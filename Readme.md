# AI Kanban Agent Orchestrator

An autonomous, state-driven multi-agent workflow system built on top of Trello.

This project turns a Trello board into an asynchronous control plane where AI agents act as specialized roles (Business Analyst, Senior Engineer, QA, etc.), automatically progressing tickets through a structured SDLC pipeline.

The board is the human-facing surface.
The orchestrator is the automation brain.
Agents execute work in ephemeral compute environments.

---

# Vision

Use a Kanban board as an orchestration layer for autonomous AI agents.

A ticket moves into a state → a specific AI role is triggered → the agent updates the ticket → transitions it to the next state → optionally waits for human approval → continues.

The board becomes:
- Human review surface
- Approval gate
- Planning document repository
- Workflow trigger engine

The orchestrator becomes:
- State machine
- Agent runtime
- Execution log
- Safety layer

---

# Core Principles

1. Trello is the human UI.
2. All planning text lives on the card.
3. The orchestrator stores execution metadata and run history.
4. Agents are stateless and run in ephemeral compute.
5. State transitions are deterministic and idempotent.
6. Human approval is first-class.
7. Infrastructure cost approaches zero at idle.

---

# High-Level Architecture

Trello Webhook
    ↓
Cloudflare Worker (event ingestion + enqueue)
    ↓
Queue (Cloudflare Queue or SQS)
    ↓
Worker (AWS Lambda preferred)
    ↓
LLM API (OpenAI / Anthropic)
    ↓
Write back to Trello
    ↓
Update internal state store

---

# Responsibilities

## Trello

Trello is the authoritative source for:

- Requirements
- Design documents
- Decisions
- Acceptance criteria
- Questions to human
- Current pipeline state (via List)

Trello is NOT responsible for:

- Execution logs
- Agent internal reasoning
- Idempotency tracking
- Locking
- Run metadata

---

# Board Structure

## Lists (Pipeline States)

Example:

- Backlog
- Requirements Gathering
- Questions
- Ready for Design
- Technical Design
- Implementation
- Code Review
- Testing
- Done

Each list maps to exactly one AgentRole (or manual state).

---

# Card Structure

Each card contains structured planning data inside the description.

Example:

```

# Requirements

...

# Decisions

...

# Technical Design

...

# Open Questions

...

# Acceptance Criteria

...

````

Agents read and update these sections deterministically.

Comments are used for:
- Agent summaries
- Questions to human
- Status updates

A single “Agent Status” comment is updated per run (avoid spam).

---

# Workflow Configuration

Stored in database (JSON).

Example schema:

```json
{
  "states": {
    "requirements_list_id": {
      "role": "business_analyst",
      "transitions": {
        "NEEDS_INFO": "questions_list_id",
        "COMPLETE": "ready_for_design_list_id"
      }
    },
    "design_list_id": {
      "role": "senior_engineer",
      "transitions": {
        "NEEDS_INFO": "questions_list_id",
        "COMPLETE": "implementation_list_id"
      }
    }
  },
  "roles": {
    "business_analyst": {
      "model": "gpt-4.1",
      "system_prompt": "...",
      "tools": []
    },
    "senior_engineer": {
      "model": "gpt-4.1",
      "system_prompt": "...",
      "tools": ["repo_read"]
    }
  }
}
````

This allows dynamic mapping between:

State → Role
Role → Model + Personality
Agent Output → Next State

---

# Agent Contract

Each agent must return structured output:

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

The orchestrator:

1. Applies updates to card description.
2. Posts summary comment.
3. Transitions card to next list if allowed.
4. Sets approval gate if needed.

Agents do NOT directly move cards.
The orchestrator controls transitions.

---

# Event Flow

1. Trello webhook fires on:

   * Card moved between lists
   * Card updated
   * Comment added

2. Cloudflare Worker:

   * Validates webhook
   * Enqueues event
   * Returns 200 immediately

3. Worker:

   * Fetches latest card state
   * Determines transition
   * Checks lock
   * Executes agent
   * Applies updates
   * Moves card if required
   * Releases lock

---

# Idempotency and Locking

Critical to prevent duplicate runs.

## Locking

Before execution:

* Write lock in DB: `(cardId, runId)`
* If lock exists → skip

After execution:

* Release lock

## Idempotency Key

`cardId + listId + dateLastActivity`

If already processed → skip.

---

# Human-in-the-Loop

If agent returns:

```
outcome: NEEDS_INFO
```

System:

* Moves card to Questions
* Posts questions
* Sets `WaitingOnHuman = true`

Re-trigger occurs only when:

* Card moved back to previous state
  OR
* Specific marker detected in comment

---

# Storage

Minimal database tables:

## workflow_config

Stores state → role mappings.

## card_state

* cardId
* lastProcessedEvent
* currentLock
* lastKnownList

## run_log

* runId
* cardId
* role
* inputHash
* outputHash
* outcome
* timestamp

Planning text is NOT stored here.
Only metadata and audit info.

---

# Infrastructure

## Recommended v1 Stack

* Trello
* Cloudflare Worker (webhook receiver)
* Cloudflare Queue or AWS SQS
* AWS Lambda (agent execution)
* DynamoDB or minimal Postgres
* LLM API (OpenAI / Anthropic)

Why Lambda over Fargate:

* Cheaper at low volume
* Auto scale-to-zero
* Simpler operations

---

# Agent Personalities

Each role has:

* System prompt
* Behavioral constraints
* Tool permissions
* Model selection
* Output schema

Examples:

Business Analyst:

* Clarifies ambiguity
* Extracts acceptance criteria
* Refuses to assume silently

Senior Engineer:

* Produces structured technical plan
* Identifies edge cases
* Splits into tasks

QA:

* Generates test plan
* Identifies failure scenarios

---

# Safety Model

* Agents cannot transition state directly.
* Orchestrator validates all transitions.
* Optional approval gates block transitions.
* All runs logged.
* Execution is ephemeral and stateless.

Future:

* Sandboxed code execution
* Repo branch isolation
* Diff-based patch proposals

---

# Cost Strategy

Idle cost target: near $0.

Costs scale only when:

* Webhook triggered
* Agent executes
* LLM called

Primary cost driver: LLM tokens.

---

# Future Expansion

* GitHub integration
* PR creation automation
* Parallel agent branches
* SLA timers
* Retry policies
* State replay
* Multi-board multi-tenant support
* Visual “Mission Control” dashboard

---

# Non-Goals (v1)

* Replacing Trello UI
* Full CMS for workflow editing
* Autonomous production deployment
* Complex RBAC system
* Multi-repo orchestration

---

# Next Steps

1. Create Trello board with defined lists.
2. Implement webhook receiver.
3. Build idempotent queue processor.
4. Implement Business Analyst role.
5. Validate full loop: Requirements → Questions → Requirements → Ready for Design.
6. Expand roles incrementally.

---

# Summary

This system treats a Kanban board as a deterministic state machine that triggers autonomous AI roles.

Trello is the planning and approval surface.
Agents are specialized workers.
The orchestrator is the guardrail and traffic controller.

Start small.
Make transitions reliable.
Add intelligence incrementally.
Scale once the loop is stable.

```