# Agent.md — LLM operator's guide to AI Board

This file is the **single source of truth** an LLM coding agent should consume to understand AI Board, configure a new project, and choose the right executor + model + role + workflow shape for the work at hand. Read it end-to-end once; subsequent edits to `appsettings.user.json` or `workflow.*.json` should be informed by it without going back to the source code.

---

## 1. What AI Board is

AI Board turns a Kanban board (GitHub Projects v2 or Trello) into an asynchronous control plane for autonomous AI agents. A card moves into a **"Ready for X"** column → the orchestrator picks it up → invokes a specific AI role with the card's context → agent posts results back as a comment + (optionally) commits code in an isolated git worktree → orchestrator transitions the card to the next column based on the structured `outcome` the agent returned. Manual approval gates are first-class — the system pauses at human-readable columns until a person drags the card forward.

**Core invariants the orchestrator enforces:**

- The board is the only human UI. All planning lives on the card.
- Agents are stateless. Each invocation gets a fresh git worktree; the main repo working tree is never modified.
- State transitions are deterministic. Agents return `{outcome: COMPLETE|NEEDS_INFO|ERROR}` and the orchestrator decides which column the card moves to.
- Agents do NOT do git writes. The orchestrator commits + pushes after the agent exits (Docker enforces this with a read-only `.git` mount; non-Docker enforces it via prompt policy).
- Cost approaches zero at idle. Spend scales only with active steps.

**Execution modes** (CLI flags):

- `--mode agent --card-id N` — process one card directly
- `--mode polling --board-id 1` — watch all "Ready for" columns, pick highest-priority card automatically
- `--mode metrics [--card-id N | --since 7d]` — read-only metrics report
- `--mode validation --board-id N` — read-only check that the workflow config is consistent with the live board (fields exist, options match, prompts present)
- `--mode queue` — legacy webhook-driven queue mode

Press **Ctrl+C once** for graceful shutdown (finishes the current card); **twice** to force quit.

---

## 2. Architecture in one diagram

```
GitHub Projects / Trello (the board)
        │
        ▼
ITaskBoardClient (GitHubProjectsClient, TrelloClient, StubTaskBoardClient)
        │
        ▼
PollingRunner / AgentRunner (the orchestrator)
        │
        ├─► GitWorkspaceManager.CreateWorktreeAsync         (isolated working dir)
        ├─► CrossReferenceResolver / ImageDownloader        (fill the workspace)
        ├─► TaskFileManager                                 (write .aiboard/tasks/{id}.md)
        ├─► AgentExecutorResolver.Resolve(role.Provider)    (pick the right executor)
        │   ├─► DockerClaudeAgentExecutor                   (docker-claude-cli)
        │   ├─► DockerCodexAgentExecutor                    (docker-codex)
        │   ├─► DockerOpenCodeAgentExecutor                 (docker-opencode → local Qwen)
        │   ├─► DockerClaudeQwenAgentExecutor               (docker-claude-qwen → local Qwen)
        │   ├─► ClaudeAgentExecutor                         (claude-cli, host — REQUIRES --unsafe)
        │   ├─► CodexAgentExecutor                          (codex, host — REQUIRES --unsafe)
        │   └─► StubAgentExecutor                           (testing)
        ├─► CandidateExecutor                               (optional: parallel A/B + evaluator)
        ├─► HandleGitBehaviorAsync                          (commit / push / discard)
        └─► IRunStore.SaveStepResultAsync                   (PostgreSQL row per step)
```

For a deeper drill-down see the `docs/` folder (narrative explanations of each subsystem).

---

## 3. The Agent Contract — what every agent must return

Every agent invocation produces structured JSON. Schema (loose form):

```json
{
  "outcome": "COMPLETE | NEEDS_INFO | ERROR",
  "detail": "GitHub-flavored markdown summary, posted as the step's comment on the card",
  "questions": [
    { "question": "What is the target component?", "recommendations": ["Auth module", "API gateway"] }
  ],
  "requestedSteps": ["security_audit", "performance_review"],
  "estimate": 4
}
```

Field semantics:

- `outcome` — required. The orchestrator routes the card based on this.
  - `COMPLETE` → state's `transitions.COMPLETE` action(s) fire (typically: move to next column).
  - `NEEDS_INFO` → state's `transitions.NEEDS_INFO` (typically: move to a "Questions" holding column; `questions[]` is posted as a comment for a human to answer).
  - `ERROR` → state's `transitions.ERROR` (typically: move to "Error" column with `detail` posted).
- `detail` — markdown. Posted as a step-scoped comment on the card via an HTML marker (`<!-- agent-step:{step.Name} -->`) so it can be upserted on retry without spamming.
- `questions[]` — only used when `outcome=NEEDS_INFO`. Each `recommendations[]` becomes a numbered option the human can pick from in their reply.
- `requestedSteps[]` — only meaningful from a `gate_checker` agent. Names of optional specialist-reviewer steps the gate wants to run (must match `optionalSteps[].name` for that state).
- `estimate` — only meaningful from the `estimator` role. An integer matching one of the values in `workflow.json → estimation.scale`. Persisted to `agent_run.estimate` and (via a `setField` transition action) written to the board's `Estimate` field.

The schema is enforced server-side when the executor supports it: Claude CLI's `--json-schema` flag or Codex's `--output-schema` (both reject malformed output at the wire). OpenCode prompt-engineers the schema and validates client-side.

**Outcome values are also normalized** — historical `SUCCESS` → `COMPLETE`, `QUESTIONS` → `NEEDS_INFO` for backward compat with older agent outputs.

---

## 4. Configuration files and their precedence

> **⚠️ READ THIS FIRST IF YOU ARE AN AGENT CONFIGURING aiboard FOR YOUR PROJECT**
>
> Configuration is layered, and **the directory of the project you are working on
> takes precedence over the directory where `aiboard.exe` is installed**. If you are
> setting up aiboard for the project at the current working directory:
>
> - **DO** create `./.aiboard/appsettings.json` and `./.aiboard/workflow.json` (or
>   `workflow.github.json`) **inside the project's repo root**. These files travel
>   with the project, get committed (or gitignored) per the project's policy, and
>   are loaded automatically when `aiboard` runs from anywhere inside the repo.
> - **DO NOT** edit the `appsettings.json` next to `aiboard.exe` itself. Per-project
>   `.aiboard/` files override that global file silently — your edits to the global
>   file will appear to do nothing because the project's local `.aiboard/` copy
>   wins. This is a common confusion that costs hours of debugging when the global
>   file *was* recently updated and the agent assumes its own edits took effect.
>
> Same rule for `workflow.json`: the project-local one wins. If the project ships
> a `.aiboard/workflow.json` it must be edited in place, not at the global install.

Sources merge to produce the runtime configuration. **Later overrides earlier on key collision.**

1. **`appsettings.json`** (next to `aiboard.exe`) — committed defaults shipped with the release. **Operators rarely edit this**; it's the floor.
2. **`./.aiboard/appsettings.json`** (in the agent workspace's repo root) — **per-project config; the file an agent should edit when setting up aiboard for a new project.** Override-on-collision: any key here wins over the global `appsettings.json`. Travels with the project (committed or gitignored as the project's `.gitignore` dictates).
3. **`--config <path>`** CLI argument — explicit override file (one-off; rarely used in normal flows).
4. **`appsettings.user.json`** (next to `aiboard.exe`, **gitignored**) — the operator's machine-local secrets that span every project. Useful for keys that don't belong in any individual project's `.aiboard/` (e.g., a personal Anthropic API key, a Trello token).
5. **Environment variables** — standard .NET configuration provider (e.g. `GitHubProjects__Owner=acme` overrides `GitHubProjects.Owner`).
6. **CLI flags** — highest precedence (`--card-id`, `--board-id`, `--mode`, `--workspace`, `--state`, `--since`).

**Decision rule for where to put a setting:**

| Setting type | Where it belongs |
|---|---|
| Project's GitHub repo / project number / Trello board ID | `./.aiboard/appsettings.json` (project-local — different per project) |
| Project's workflow shape (states, roles, transitions) | `./.aiboard/workflow.json` (project-local) |
| Personal LLM API keys / tokens (any provider) | `appsettings.user.json` (next to `aiboard.exe`, gitignored, machine-local) |
| Default executor or model preferences across all your projects | `appsettings.user.json` |
| Release defaults that ship to every operator | `appsettings.json` (next to `aiboard.exe` — only the maintainer edits this) |

If the worker can't find a required value (e.g. `GitHubProjects.Owner` when `BoardProvider=github`), it lists every config source that was probed in the failure message — no silent defaults.

`workflow.*.json` paths are resolved relative to the directory containing the active `appsettings*.json` first, then the working directory. The recommended pattern is to put both `appsettings.json` and `workflow.json` (or `workflow.github.json`) under `./.aiboard/` in the project repo, and set `WorkflowConfigPath: "./workflow.github.json"` (relative path, resolved against `.aiboard/`).

---

## 5. `appsettings.json` schema (full reference)

Most operators will only edit a small subset of keys. A complete annotated example with every supported section:

```json
{
  // === Top-level selection ===
  "BoardProvider": "github",                     // github | trello | stub
  "AgentExecutor": "claude-cli",                 // see §7
  "WorkflowConfigPath": "./workflow.github.json",

  // === Database (run + step tracking, metrics) ===
  // Omit to disable DB persistence (NullRunStore + NullMetricsStore activate)
  "Database": {
    "ConnectionString": "Host=localhost;Port=5432;Database=aiboard;Username=aiboard;Password=aiboard"
  },

  // === Board provider configs (only the active one needs values) ===
  "GitHubProjects": {
    "Owner": "your-github-username",             // required when BoardProvider=github
    "Repo": "your-username/your-repo",           // required (full owner/repo form)
    "ProjectNumber": "1"                         // required, the project number from the URL
  },
  "Trello": {
    "ApiKey": "...",                             // required when BoardProvider=trello
    "ApiToken": "...",
    "BoardId": "abc123"
  },
  "Stub": {
    "TenantName": "test"                         // optional; default "test" when BoardProvider=stub
  },

  // === Stub tenant naming ===
  // tenant_id format = "{provider}:{identifier}"
  // Example: github:owner/repo/4 — partitions all DB rows so multiple projects can share one Postgres

  // === Agent executor configs ===

  // Required only when AgentExecutor is set to claude-cli or any state's role has provider=claude-cli
  // (auto-resolved if "claude" is on PATH).
  "ClaudeCli": {
    "ExecutablePath": "claude",                  // auto-detected: claude.cmd → claude.exe
    "TimeoutSeconds": 900,
    "MaxBudgetUsd": 10.00,
    "MaxTurns": 0                                // 0 = unlimited (preferred)
  },

  // Required when AgentExecutor=docker-claude-cli OR a role uses provider=docker-claude-cli
  "DockerAgents": {
    "Claude": {                                  // section: DockerAgents:Claude
      "ImageName": "aiboard-agent-sandbox:latest",
      "ContainerNamePrefix": "aiboard-run",
      "ReuseContainer": true,                    // session-reuse across steps (faster)
      "TimeoutSeconds": 900,
      "MaxBudgetUsd": 10.00,
      "NetworkMode": "host",                     // host | llm-net | task-board_default | <other>
      "MemoryLimit": null,                       // e.g. "4g"
      "CpuLimit": null,                          // e.g. "2.0"
      "ContainerUser": "",                       // empty = image default
      "PromptMountPoint": "/mnt/aiboard/prompts",
      "CredentialPath": null,                    // auto-detect ~/.claude when null
      "CredentialMountPoint": null,
      "AdditionalMounts": {}                     // operator-supplied static mounts
    },

    // Required when AgentExecutor=docker-opencode OR a role uses provider=docker-opencode
    "OpenCode": {                                // section: DockerAgents:OpenCode
      "ImageName": "aiboard-opencode-sandbox:latest",
      "NetworkMode": "llm-net",                  // must attach to local-llm's bridge network
      "ContainerNamePrefix": "aiboard-oc",
      "ProviderBaseUrl": "http://llama-server:8080/v1",  // /v1 suffix REQUIRED
      "AuthToken": "local",                      // dummy; llama.cpp validates nothing
      "ModelName": "qwen3.6-35b-a3b",            // default; per-role model overrides this
      "TimeoutSeconds": 600,
      "MaxRetriesOnMalformedOutput": 2,
      "EnableStructurer": true,                  // v0.0.22+: one-shot no-think structurer fallback on parse failure
      "StructurerModelName": "qwen3.6-35b-a3b",  // structurer always uses no-think (mechanical extraction)
      "StructurerTimeoutSeconds": 180,
      "PromptMountPoint": "/mnt/aiboard/prompts",
      "CliArguments": ["run"],
      "RateLimitPatterns": []                    // operator-extensible stderr substrings
    },

    // Required when AgentExecutor=docker-claude-qwen OR a role uses provider=docker-claude-qwen
    "ClaudeQwen": {                              // section: DockerAgents:ClaudeQwen
      "ImageName": "aiboard-agent-sandbox:latest",   // reuses the Claude sandbox
      "NetworkMode": "llm-net",
      "ContainerNamePrefix": "aiboard-cq",
      "ProviderBaseUrl": "http://llama-server:8080", // NO /v1 suffix (Anthropic adapter appends it)
      "AuthToken": "local",
      "ModelName": "qwen3.6-35b-a3b",
      "MaxBudgetUsd": 50.00,
      "TimeoutSeconds": 600,
      "DisableAttributionHeader": true,          // keeps llama.cpp prefix cache warm
      "DisableNonessentialTraffic": true         // suppresses telemetry pings to api.anthropic.com
    }
  },

  // Required only when a role uses provider=codex (legacy/secondary path)
  "CodexCli": {
    "ExecutablePath": "codex",
    "TimeoutSeconds": 900,
    "FullAuto": true,
    "Sandbox": null,                             // workspace-write | read-only | docker-network | null
    "Yolo": false,
    "RateLimitPatterns": [],
    "EnvVarsToRemove": []
  },

  // === Runtime ===
  "WorktreeBasePath": null,                      // override the default {repo}-worktrees location
  "PollIntervalSeconds": 30,                     // polling mode: how often to scan the board

  // === Logging (standard .NET) ===
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft": "Warning" }
  }
}
```

`StartupConfigValidator` flags inconsistencies before the host starts:
- **Errors (exit)**: unknown `BoardProvider`; `BoardProvider=github` missing any of Owner/Repo/ProjectNumber; `BoardProvider=trello` missing any of ApiKey/ApiToken/BoardId.
- **Warnings (continue)**: GitHubProjects populated but provider≠github (and vice versa for Trello); both populated (winner is named); legacy `Docker` section in use (migrate to `DockerAgents:Claude`); unrecognized `AgentExecutor` value.

---

## 6. `workflow.*.json` schema (full reference)

The workflow file maps board columns to roles, prompts, and transition actions. Top-level shape:

```json
{
  "states":          { "<stateKey>": { /* state definition */ } },
  "roles":           { "<roleKey>":  { /* role definition */ } },
  "mergeResolution": { "role": "merge_resolver", "providerParams": { "effort": "medium" } },
  "polling":         { "priorityFieldName": "priority", "priorityOrder": ["P0", "P1", "P2"] },
  "estimation":      { "calibrationTicketId": "34", "calibrationSize": 1, "fieldName": "Estimate", "scale": [1, 2, 4, 8] },
  "cardTypes":       { "<typeKey>": { /* card type definition */ } },
  "cardTypeField":   "Type"   // optional: project field that holds card type (alternative to labels)
}
```

### 6.1 State definition

```json
{
  "name": "Ready for Design",
  "column": "Ready for Design",                  // optional; defaults to state key
  "filters": [                                   // optional; required if multiple states share a column
    { "field": "Activity", "values": ["Design"] }
  ],
  "gateType": "agent_run",                       // see enum below
  "gitBehavior": "discard",                      // discard | commit_only | commit_and_push
  "pipelineOrder": 1,                            // higher = picked first by polling
  "includeInAgentContext": false,
  "providerParams": { "effort": "max" },         // forwarded to executor (Claude --effort, Codex sandbox, etc.)
  "steps": [ /* see 6.2 */ ],
  "gateCheck": { /* see 6.3 */ },
  "optionalSteps": [ /* see 6.4 */ ],
  "transitions": { /* see 6.5 */ }
}
```

**`gateType` enum:**

| Value | Meaning |
|---|---|
| `agent_run` | Run the configured `steps[]` (then optional `gateCheck` + requested `optionalSteps[]`). |
| `manual_gate` | Wait for a human to drag the card forward. |
| `manual_entry` | Card was placed here by a human (e.g. Backlog). |
| `in_progress` | Transient column (e.g. "Designing", "Implementing"). Set by the orchestrator's IN_PROGRESS transition. |
| `holding` | Card sits indefinitely (Questions / Error / Waiting for Tasks). |
| `terminal` | End of pipeline (Done). |
| `system_merge` | Orchestrator runs a `git merge --no-ff` instead of an agent (Approved → Done). |
| `children_complete` | Polled completion check (legacy; new flows use the `completeParentIfReady` transition action). |

**`gitBehavior` enum:**

| Value | Behavior |
|---|---|
| `discard` | Worktree changes are dropped after the run (design / test). |
| `commit_only` | Orchestrator commits but does not push. |
| `commit_and_push` | Orchestrator commits and pushes the work branch (implementation states). |

### 6.2 Step definition (within `steps[]`)

```json
{
  "name": "implement",                           // unique within the state
  "role": "implementer",                         // must exist in workflow.roles
  "taskPromptFile": "prompts/states/ready_for_implementation.md",
  "taskPrompt": null,                            // alternative: inline prompt string
  "providerParams": { "effort": "high" },        // optional per-step override of state-level params

  // === Optional: parallel candidate group (multi-agent A/B + evaluator) ===
  "candidates": [
    { "provider": "docker-opencode",    "model": "qwen3.6-35b-a3b" },
    { "provider": "docker-claude-qwen", "model": "qwen3.6-35b-a3b" },
    { "provider": "docker-claude-cli",  "model": "claude-sonnet-4-6" }
  ],
  "evaluator": {
    "role": "evaluator",                         // role must exist in workflow.roles
    "taskPromptFile": "prompts/evaluator/code_review_candidates.md",
    "scoring": "WinnerWithScores"                // WinnerOnly | WinnerWithScores
  },

  // === Optional: child ticket generation (story → task decomposition) ===
  "generationConfig": {
    "targetType": "task",                        // must exist in workflow.cardTypes
    "targetColumn": "Ready for Design",
    "linkToParent": true,
    "copyFields": ["priority"],                  // copy parent → child on creation
    "setFields": { "Type": "Task", "Activity": "Design" }   // literal field→value (wins on collision with copyFields)
  }
}
```

### 6.3 Gate check (per-state, lightweight pass/fail)

```json
"gateCheck": {
  "role": "gate_checker",
  "taskPromptFile": "prompts/gates/post_implementation.md"
}
```

Runs after all `steps[]` complete. The gate agent receives the step outputs and returns:
- `outcome=COMPLETE` → state's `transitions.COMPLETE` fires.
- `outcome=ERROR` with a different transition path → state's `transitions.GATE_FAIL` fires (typically loops back to the trigger column for retry).
- `requestedSteps[]` → if set, those names are looked up in this state's `optionalSteps[]` and run sequentially before the COMPLETE transition.

### 6.4 Optional specialist-reviewer step

```json
{
  "name": "security_audit",
  "role": "specialist_reviewer",
  "taskPromptFile": "prompts/optional-steps/impl/security_audit.md",
  "description": "Reviews code for security vulnerabilities, injection risks, auth flaws, and data exposure.",
  "triggers": "Changes to authentication, authorization, API endpoints, user input processing, or cryptography.",
  "providerParams": { "effort": "medium" }
}
```

The `description` and `triggers` are passed to the gate-check agent so it can decide whether to request this step.

### 6.5 Transitions

A transition value is **either a string** (column name) or an **array of actions**:

```json
"transitions": {
  "IN_PROGRESS": "Designing",
  "COMPLETE": [
    { "type": "moveToColumn", "value": "Designed" },
    { "type": "setField",     "field": "Estimate",  "value": "{{estimation}}" },
    { "type": "updateParentSum", "field": "Estimate" },
    { "type": "completeParentIfReady" }
  ],
  "NEEDS_INFO":     "Design Questions",
  "ERROR":          "Error",
  "GATE_FAIL":      "Ready for Design",
  "MERGE_CONFLICT": "Ready for Implementation"
}
```

**Action types:**

| `type` | Required fields | Effect |
|---|---|---|
| `moveToColumn` | `value` (column name) | Move the card. |
| `setField` | `field`, `value` (supports `{{templateVar}}`) | Set a project field. Skipped with a warning if templates unresolved. |
| `clearField` | `field` | Clear a project field. |
| `updateParentSum` | `field` | If card has a parent (via `trackedInIssues`), recompute parent's `field` as the sum across `sub_item` children. |
| `completeParentIfReady` | (none) | If card's parent has all siblings in terminal states, fire the parent's COMPLETE transition; otherwise upsert a progress comment on the parent. |

**Outcome keys** (which transition fires):

- `IN_PROGRESS` — moves the card to the in-progress column at agent pickup
- `COMPLETE` / `NEEDS_INFO` / `ERROR` — match the agent's `outcome`
- `GATE_FAIL` — the gate check rejected the result
- `MERGE_CONFLICT` — `system_merge` couldn't auto-merge

### 6.6 Role definition

```json
"senior_engineer": {
  "model": "claude-opus-4-6",
  "provider": "claude-cli",                      // optional; defaults to AgentExecutor setting
  "systemPromptFile": "prompts/senior_engineer.md",
  "systemPrompt": null,                          // alternative: inline string
  "sections": ["Technical Design", "Decisions", "Implementation"]   // markdown sections expected on the card body
}
```

The `provider` field is what makes per-role executor selection work — you can have an `implementer` role on `docker-opencode` (free local Qwen) and a `senior_engineer` role on `claude-cli` (paid Anthropic) in the same workflow.

### 6.7 Estimation block

```json
"estimation": {
  "calibrationTicketId": "34",                   // a ticket the operator agrees is size N
  "calibrationSize": 1,                          // … which is this many points
  "fieldName": "Estimate",                       // board field to write the result to
  "scale": [1, 2, 4, 8]                          // allowed sizes (Fibonacci-like)
}
```

The `estimator` role sizes the ticket relative to the calibration. Use `{{estimation}}` in a `setField` action to write the result to the board.

### 6.8 Card types and child generation

```json
"cardTypes": {
  "story": { "name": "User Story", "labelPrefix": "type", "allowedChildren": ["task"] },
  "task":  { "name": "Task",       "labelPrefix": "type", "allowedChildren": [] }
}
```

- `labelPrefix: "type"` → cards get labeled `type:story` / `type:task` (label-based discrimination).
- Set `cardTypeField: "Type"` at workflow root → cards' type is read from a project field instead (field-based). Both can coexist.
- `allowedChildren` → enforces what a story can spawn via `generationConfig`.

See `docs/CardTypesAndGeneration.md` for the full discriminator + `setFields` walkthrough.

### 6.9 Polling

```json
"polling": {
  "priorityFieldName": "priority",               // name of the priority field on the board
  "priorityOrder": ["P0", "P1", "P2"]           // sort order (highest first)
}
```

In `--mode polling`, the runner scans every "Ready for" column, picks the highest-priority card, then runs `pipelineOrder` as the tiebreaker.

---

## 7. Agent executors — what's available and when to use which

There are **six real executors** plus a stub. All implement `IAgentExecutor`; some additionally implement `ISessionableAgentExecutor` for container reuse across steps. Provider keys are matched case-insensitively.

**Sandboxed by default.** As of this version, the Docker-wrapped executors are the default. The host CLI executors (`claude-cli`, `codex`) are gated behind a `--unsafe` CLI flag (or `Unsafe: true` in config) because they bypass the container filesystem boundary and run with full host credentials. Workflows referencing them refuse to start without `--unsafe`. Migrate `claude-cli` → `docker-claude-cli` and `codex` → `docker-codex` to keep working without the flag.

| Provider key | Class | Backend | Schema enforcement | Cost | Sandboxed | When to use |
|---|---|---|---|---|---|---|
| `docker-claude-cli` | `DockerClaudeAgentExecutor` | Claude CLI inside Docker | Server-side via `--json-schema` | Anthropic pricing | yes | Default Claude path. `.git` mounted RO so agents can't push. Supports session reuse across steps for a card. |
| `docker-codex` | `DockerCodexAgentExecutor` | OpenAI Codex CLI inside Docker | Server-side via `--output-schema` | OpenAI pricing | yes | Default Codex path. Defaults to `--yolo` since Docker provides the filesystem sandbox — fast and autonomous. See [docs/CodexSandbox.md](docs/CodexSandbox.md). |
| `docker-opencode` | `DockerOpenCodeAgentExecutor` | OpenCode CLI in Docker → local llama.cpp proxy | Prompt-engineered + client-side parser + bounded retry | $0 (local) | yes | Free, low-stakes roles on a local Qwen3.6 server. |
| `docker-claude-qwen` | `DockerClaudeQwenAgentExecutor` | Claude CLI in Docker → same local llama.cpp proxy | Server-side via `--json-schema` → tool-call → llama.cpp grammar | $0 (local) | yes | Free with **wire-enforced** schema. Same llama-server as `docker-opencode` but via Anthropic Messages format. |
| `claude-cli` | `ClaudeAgentExecutor` | Claude CLI subprocess on the host | Server-side via `--json-schema` | Anthropic pricing | **NO — requires `--unsafe`** | Single-machine ad-hoc dev where you trust the agent and don't want a Docker dependency. |
| `codex` | `CodexAgentExecutor` | OpenAI Codex CLI subprocess on the host | Server-side via `--output-schema` | OpenAI pricing | **NO — requires `--unsafe`** | Single-machine ad-hoc dev. The defensive diagnostics path is shared with `docker-codex`, so prefer the Docker variant for sandbox parity. |
| `stub` | `StubAgentExecutor` | Fake responses | n/a | $0 | yes (no agent runs) | Tests / dev. Set `AgentExecutor=stub` to map every provider key to the stub. |

**Auto-detection vs. explicit selection:**

- `AgentExecutor` env var = `stub` → all keys map to stub. The `--unsafe` gate is bypassed in stub mode (no real CLI runs, so there's nothing to sandbox), so existing test workflows referencing `claude-cli` or `codex` keep working without `--unsafe`.
- `AgentExecutor` = `docker-claude-cli` → fail-fast if Docker isn't running; transparently routes `claude-cli` workflow roles through Docker. Workflows referencing `claude-cli` in this mode do NOT need `--unsafe` because the actual execution is sandboxed via the redirect. (Workflows referencing `codex` still need `--unsafe` — there's no `docker-claude-cli` redirect for codex.)
- `AgentExecutor` = `docker-codex` → fail-fast if Docker isn't running; **distinct** key, no aliasing
- `AgentExecutor` = `docker-opencode` → fail-fast if Docker isn't running; **distinct** key, no aliasing
- `AgentExecutor` = `docker-claude-qwen` → fail-fast if Docker isn't running; **distinct** key, no aliasing
- `AgentExecutor` unset (or any other value) → production auto-detect: every Docker-wrapped executor whose CLI is available is registered. Host CLIs (`claude-cli`, `codex`) are auto-registered only when `--unsafe` is also passed.

The full unsafe-gate logic lives in `UnsafeGate.Evaluate`; the source repo's `UnsafeGateTests` covers the exhaustive scenario matrix.

**Per-role provider override** (the recommended pattern in production): leave `AgentExecutor` unset, then set each role's `provider` field in `workflow.json`:

```json
"roles": {
  "gate_checker":   { "model": "qwen3.6-35b-a3b",       "provider": "docker-opencode",   ... },
  "estimator":      { "model": "qwen3.6-35b-a3b",       "provider": "docker-opencode",   ... },
  "implementer":    { "model": "claude-sonnet-4-6",     "provider": "docker-claude-cli", ... },
  "senior_engineer":{ "model": "claude-opus-4-6",       "provider": "claude-cli",        ... },
  "evaluator":      { "model": "qwen3.6-35b-a3b-think", "provider": "docker-claude-qwen",... }
}
```

---

## 8. Models — what's available and how to pick

### 8.1 Anthropic Claude (via `claude-cli` or `docker-claude-cli`)

| Model ID | Tier | Strengths | Use for |
|---|---|---|---|
| `claude-opus-4-6` | Top | Best reasoning, slowest, most expensive | Design (`senior_engineer`), QA (`qa`), high-stakes specialist reviews (legal, privacy, SOC2) |
| `claude-sonnet-4-6` | Mid | Strong coding, good speed/cost balance | Implementation (`implementer`), code review (`code_reviewer`), standard specialist reviews, merge conflict resolution |
| `claude-haiku-4-5-20251001` | Light | Fast, cheap, structured tasks | Gate checks (`gate_checker`), estimation (`estimator`) |

`providerParams.effort` accepts `low | medium | high | max`. The Opus tier respects `max` to give the most thorough output; Sonnet/Haiku usage with `max` is wasted spend.

### 8.2 OpenAI (via `codex`)

Codex CLI accepts an explicit `--model` arg. The runtime passes it from the role's `model` field (or a candidate's `model` override). Models valid against a Codex / ChatGPT account today:

| Model | Tier | Use for |
|---|---|---|
| `gpt-5.5` | Top | Hardest reasoning, slowest, most expensive — design / specialist reviews when you want to A/B against Opus |
| `gpt-5.4` | Mid | Strong general use; the standard implementation / review pick on the OpenAI side |
| `gpt-5.4-mini` | Light | Cheap and fast — gate checks, estimation, mechanical structured extraction |
| `gpt-5.3-codex` | Coding-tuned | Implementation tasks where you want a coding-specialised pretrain over generality |

`providerParams` exposed for Codex execution:
- `sandbox: workspace-write | read-only | docker-network` — sandbox policy (auto-warned if neither set)
- `fullAuto: true | false` — full autonomy
- `yolo: true | false` — bypass all safety prompts (overrides `fullAuto` and `sandbox`)

Workflow roles using `provider=codex` should pin one of `sandbox` / `yolo` / `fullAuto` in their `providerParams` — the workflow validator emits a warning otherwise.

**Cross-provider candidates:** if a step's role has `provider: claude-cli` (so its `model` is e.g. `claude-opus-4-6`) but a candidate sets `provider: codex` without overriding `model`, the runtime now suppresses `--model` rather than passing the Anthropic name to Codex CLI (which would error). Pin `model` on the candidate to a value from the table above to silence the validator's audit warning.

### 8.3 Local Qwen3.6 (via `docker-opencode` or `docker-claude-qwen`)

Both executors target the same llama.cpp proxy (`local-llm` sibling project) via two virtual model aliases:

| Model alias | Mode | Wall time per turn | Use for |
|---|---|---|---|
| `qwen3.6-35b-a3b` | No reasoning, ~65 tok/s, 100–500 token answers | A few seconds | Tool-call loops, gate checks, estimation, mechanical implementation, code-review pre-checks, structured extraction |
| `qwen3.6-35b-a3b-think` | Reasoning emitted, same per-token rate, 1500–3000 token answers | 30–60 s | Design synthesis, code-review write-ups, QA test plans, summarization, "explain why", evaluator step in candidate groups |

**Rule of thumb:** single-shot synthesis → `-think`, tool-call loops → base.

**Honest caveats** (from the `local-llm/Qwen-3.6.md` model card):
- Below Claude Opus on architecture reasoning (SWE-Bench 73.4% vs 80.8%). Don't use for irreversible architectural calls without human review.
- First request after `docker compose up` or long idle takes 30–120s (cold prefix cache). `TimeoutSeconds: 600` is sized for this.
- Single concurrent slot — `local-llm`'s `llama-server` runs `--parallel 1`. Two agents hitting it concurrently destroy each other's prefix caches. Polling mode runs one card at a time, which matches.
- Occasional hallucinated identifiers in the `-think` mode. Verify before code-gen acts on names.

---

## 9. Pre-built role catalog (in the example workflow)

If you copy `workflow.github.example.json` and adjust, you start with these roles:

| Role | Default model | Default provider | Purpose | Sections written to card body |
|---|---|---|---|---|
| `senior_engineer` | `claude-opus-4-6` | `claude-cli` | Design (4-step pipeline), tasking, design review | `Technical Design`, `Decisions`, `Implementation` |
| `implementer` | `claude-sonnet-4-6` | `claude-cli` | Code implementation. Uses senior_engineer's system prompt. | (same as senior_engineer) |
| `code_reviewer` | `claude-sonnet-4-6` | `claude-cli` | Post-implementation code review | (none — comment only) |
| `qa` | `claude-opus-4-6` | `claude-cli` | Test plan + validation | `Test Plan`, `Test Results` |
| `gate_checker` | `claude-haiku-4-5-20251001` | `claude-cli` | Lightweight pass/fail validation after each agent_run state | (none) |
| `estimator` | `claude-haiku-4-5-20251001` | `claude-cli` | Calibration-based ticket sizing | (none) |
| `board_analyst` | `claude-haiku-4-5-20251001` | `claude-cli` | **Narrow** cross-reference analysis: scan other tickets on the board, identify dependencies/overlap/conflicts. Strict scope — does NOT read source, run tests, or build. Used for the `review_related_tickets` step that precedes `create_design`. | (none — writes a `## Related Ticket Analysis` section into the task file) |
| `specialist_reviewer` | `claude-sonnet-4-6` | `claude-cli` | On-demand specialist reviews requested by gate checks | (none) |
| `senior_specialist_reviewer` | `claude-opus-4-6` | `claude-cli` | High-stakes specialist reviews (legal, compliance, privacy) | (none) |
| `merge_resolver` | `claude-sonnet-4-6` | `claude-cli` | Merge conflict resolution | (none) |
| `evaluator` | `claude-opus-4-6` | `claude-cli` | Picks winner in a multi-agent candidate group | (none) |

> **`board_analyst` vs `senior_engineer` for `review_related_tickets`**: prior to v0.0.19 this step used `senior_engineer`, whose "design + testing" system-prompt context primed agents (especially Codex with `--full-auto`) to interrogate the codebase, run tests, and exercise the application during what should be a board-only scan. Real KvA runs took 8–12 minutes and one timed out. The dedicated `board_analyst` role pairs a haiku model with a system prompt that explicitly forbids code reading and subprocess execution; the same step now runs in well under a minute. **If you're using the candidate-evaluation feature for this step, also pin `"providerParams": { "sandbox": "read-only" }` on Codex candidates** — the role-level system prompt is advisory; the sandbox flag is enforced by the CLI.

**Adapt for cost / local routing**: swap `provider` values per the §7 table. Common patterns:

- All-local (free, slower, less reliable on synthesis): all roles → `docker-opencode`, with `senior_engineer` and `qa` on `qwen3.6-35b-a3b-think`, the rest on `qwen3.6-35b-a3b`.
- Hybrid (recommended starter): cheap roles (gate_checker, estimator, code_reviewer) → `docker-opencode` or `docker-claude-qwen`; design + QA + specialist reviews → `claude-cli` (Anthropic).
- A/B mode: turn one or two steps into candidate groups (see §10) so `v_provider_role_metrics` accumulates head-to-head data for routing decisions.

---

## 10. Multi-agent candidate evaluation (`CandidateExecutor`)

Opt-in per step. Lets you race N agents on the same task, have an evaluator pick a winner, promote the winner's branch, and accumulate per-(role, provider) win-rate metrics.

**Concurrency model**: candidates are grouped by their `provider` key (case-insensitive). Different provider groups run **in parallel**; same-provider candidates run **sequentially within their group**. Wall-clock time is bounded by the slowest provider group's total duration, not by the sum of all candidates. Why: a single CLI / credential pool / rate-limit window per provider makes concurrent same-provider invocations a fast route to a 429, while different providers (e.g. codex + docker-claude-cli + docker-opencode) don't contend on each other. So `[claude×2, opencode×1, codex×2]` runs as 3 concurrent provider tracks; if each candidate is ~3 minutes, total wall-clock is ~6 minutes (the slowest two-candidate track), not ~15.

**How to opt in** — add `candidates[]` and `evaluator` to a step:

```json
{
  "name": "implement",
  "role": "implementer",
  "taskPromptFile": "prompts/states/ready_for_implementation.md",
  "candidates": [
    { "provider": "docker-claude-cli",  "model": "claude-sonnet-4-6" },
    { "provider": "docker-opencode",    "model": "qwen3.6-35b-a3b" },
    { "provider": "docker-claude-qwen", "model": "qwen3.6-35b-a3b" }
  ],
  "evaluator": {
    "role": "evaluator",
    "taskPromptFile": "prompts/evaluator/code_review_candidates.md",
    "scoring": "WinnerWithScores"
  }
}
```

**Constraints (validator-enforced):**
- `candidates` requires `evaluator` and vice versa.
- Each candidate must specify a `provider`.
- Evaluator's role must exist; evaluator must have `taskPrompt` or `taskPromptFile`.

**Pick the right evaluator prompt for the step type** — `prompts/evaluator/` ships three task-prompt templates calibrated to different step shapes:

| Prompt | Use for | Compares on |
|---|---|---|
| `code_review_candidates.md` | Implementation steps (`gitBehavior: commit_*`) | Per-candidate `git diff` (Correctness/Quality/Scope/Tests/Safety) |
| `design_candidates.md` | Design / tasking steps (`gitBehavior: discard`) | Per-candidate `.aiboard/tasks/*.md` body and `updates/*.md` files (Completeness/Correctness/Actionability/Scope/Conventions) |
| `gate_check_candidates.md` | Gate-check candidate groups (rare) | Per-candidate verdicts (Calibration/Reasoning/Conciseness) |

If your step is `gitBehavior: discard`, do NOT route the evaluator to `code_review_candidates.md` — its rubric anchors on diffs that won't exist for discard candidates, and the evaluator will spuriously conclude "no design artifact was produced."

**Schema enforcement on `winner_index`** — the evaluator's response schema (`AgentSchemas.EvaluatorOutcomeSchema`) makes `winner_index` required when `outcome=COMPLETE` (via JSON Schema `if`/`then`; the OpenAI variant uses parser-side enforcement). If the model returns `outcome=COMPLETE` without a `winner_index` (or with null), the orchestrator overrides the result to ERROR with a diagnostic detail rather than silently cleaning up all candidates with no promotion.

**Cross-provider candidate model leak** — when a candidate's `provider` differs from the role's `provider` and the candidate doesn't pin its own `model`, the runtime suppresses `--model` rather than passing the role's default name (e.g. an Anthropic model name) to the wrong executor (e.g. Codex CLI, which would error). Pin `model` on the candidate to silence the validator's audit warning and give yourself a known model identity to attribute metrics to.

**No artificial caps**: candidate count, duplicate-provider, and `gitBehavior=discard` were all rejected by the validator in earlier versions. They aren't anymore — eval-prompt size and wall-time are user-managed concerns; same-provider model A/B (Opus vs Sonnet on Claude) is a legitimate use case; design / tasking states (`gitBehavior: discard`) are now supported via file-based winner promotion (see below).

**What happens at runtime:**
1. N candidate worktrees spawn off canonical HEAD: `aiboard-cand/{cardId}-{groupShort}-{index}-{provider}`.
2. Candidates run grouped by provider — each provider group's candidates execute in declaration order, but the groups themselves run concurrently (`Task.WhenAll`). Per-candidate `step_result` rows persist with `candidate_group_id`, `candidate_index`, `provider`.
3. Evaluator role runs against the canonical worktree with comparison material per candidate. **The material differs by `gitBehavior`:**
   - **commit modes**: each candidate's `git diff` against the canonical branch (the actual code change).
   - **discard mode**: each candidate's `.aiboard/tasks/{cardId}.md` contents (the design / card body) plus any `.aiboard/updates/*.md` files (child-card requests). Diffs are useless for discard candidates because nothing is committed and `.aiboard/` is gitignored.
   Returns `{outcome: COMPLETE, winner_index: N, scores: [{index, score, reasoning}, ...]}`. Saved as a `step_result` with name suffix `:evaluator` (no `candidate_group_id` so metrics views don't double-count).
4. **Winner promotion** depends on `gitBehavior`:
   - **commit modes** (`commit_only` / `commit_and_push`): `git reset --hard` the canonical worktree to the winner's branch HEAD. Winner's branch survives; loser worktrees + branches are cleaned up.
   - **discard mode**: copy the winner's `.aiboard/tasks/` and `.aiboard/updates/` contents into the canonical worktree (clearing the canonical files first so a winner that *removed* a file is reflected). All candidate branches torn down; canonical's git state is unchanged. AgentRunner's normal post-step processors (TaskFileManager updates the card body, UpdateFileProcessor creates child cards) then read the winner's outputs from canonical as if a single agent had run.
   Per-candidate rows are updated with `selected`, `quality_score`, `evaluator_reasoning`.
5. **All-failed merge logic**: if every candidate returns non-COMPLETE, the merged outcome prefers `NEEDS_INFO` over `ERROR` (recoverable wins over terminal). Card routes to Questions instead of Error.

**Score sanitisation**: scores outside `[0, 10]` are dropped to null (kept `reasoning`); duplicate `index` entries are deduped last-write-wins. The V19 partial UNIQUE index on `(tenant_id, candidate_group_id, candidate_index)` ensures a runtime bug that double-saves a candidate row fails loudly at the DB.

**Metrics** — query `v_provider_role_metrics` (or use `--mode metrics`):

```
role            | provider           | total_runs | wins | win_rate_percent | avg_quality_score
implementer     | docker-claude-cli  | 30         | 18   | 60.0             | 8.2
implementer     | docker-claude-qwen | 30         | 9    | 30.0             | 7.4
implementer     | docker-opencode    | 30         | 3    | 10.0             | 6.1
```

See `docs/CandidateEvaluation.md` for the full mechanics.

### 10.1 Slot-based fallback chains (subscription failover + per-candidate retries)

The single `candidates[]` + `evaluator` shape (above) is now also available as a **slot** — one of an ordered list of fallbacks. Use slots when:

1. **Your primary provider hits a quota mid-run** — Codex CLI's per-plan ChatGPT cap, Claude API monthly limits — and you want to fall through to a cheaper or local backup instead of parking the card in the Questions column.
2. **A provider is intermittently unavailable** — a transient outage of one upstream shouldn't block the step.
3. **You want different retry budgets per provider** — `gpt-5.5` is expensive (zero retries), `gpt-5.4-mini` worth one, local Qwen worth three (free).

**Shape:** add `slots[]` (ordered) instead of step-level `candidates`/`evaluator`. Each slot is its own parallel candidate group:

```jsonc
{
  "name": "implement",
  "role": "implementer",
  "taskPromptFile": "prompts/states/ready_for_implementation.md",
  "slots": [
    {
      // Slot 0: race two subscription providers
      "candidates": [
        { "provider": "docker-claude-cli", "model": "claude-sonnet-4-6", "retries": 0 },
        { "provider": "codex",             "model": "gpt-5.4",            "retries": 1 }
      ],
      "evaluator": {
        "role": "evaluator",
        "taskPromptFile": "prompts/evaluator/code_review_candidates.md"
      }
    },
    {
      // Slot 1: free local Qwen as fallback. Single candidate -> no evaluator needed.
      "candidates": [
        { "provider": "docker-opencode", "model": "qwen3.6-35b-a3b", "retries": 3 }
      ]
    }
  ]
}
```

**Semantics:**
- Slots are tried **sequentially**: slot 0 first; on slot 0 failure, slot 1 fires; etc.
- **Inside** a slot, candidates run in **parallel** (just like the legacy single-slot flow). The evaluator picks one winner.
- A slot **succeeds** when its evaluator picks a winner whose outcome is COMPLETE — or, for a single-candidate slot, when the candidate completes.
- A slot **fails** (→ try next slot) when: every candidate returns non-COMPLETE, OR the evaluator returns ERROR / no `winner_index`, OR winner promotion fails.
- A slot returning **NEEDS_INFO** (winner has legitimate questions for the operator) **propagates up — no fallback**. Operator answers and re-runs from slot 0. The user explicitly chose this so a cheap fallback can't repeatedly avoid a real question by guessing.
- **Retries** fire in-place within a slot, only on `RATE_LIMIT` and `TIMEOUT` (defaults). 30s base, 2× exponential, 5min cap, ±20% jitter. Retries do NOT cross slot boundaries — that's what fallback slots are for. Override per candidate with `"retryOn": ["RATE_LIMIT"]` to opt out of timeout retries (e.g. for an expensive model where a 5-minute timeout is genuinely terminal).

**Migration from `candidates`/`evaluator` to `slots`:**
- The legacy step-level `candidates`+`evaluator` form **still works unchanged** — the runtime adapts it to a single-slot config under the hood. No JSON edits required to keep existing workflows running.
- Mixing `candidates`/`evaluator` AND `slots[]` on the same step is rejected by the validator (configure one form only).
- A 1-candidate slot with no evaluator is now legal (single-candidate slots may omit the evaluator). Previously this was an error in the legacy form too; the broadening lets you write the "single fallback provider" case naturally.

**Three common patterns:**

1. **Pure fallback chain** (no head-to-head):
   ```jsonc
   "slots": [
     { "candidates": [{ "provider": "codex", "retries": 1 }] },
     { "candidates": [{ "provider": "docker-claude-cli", "retries": 0 }] },
     { "candidates": [{ "provider": "docker-opencode", "retries": 3 }] }
   ]
   ```
   Try `codex` (with one retry on transient failure). On terminal failure, try Claude. On Claude failure, try local Qwen with up to 3 retries.

2. **Head-to-head with fallback** — two slots, each with multiple parallel candidates and an evaluator. Slot 0 races subscription providers; slot 1 races local-only providers as fallback.

3. **Single candidate, just retries** — one slot with one candidate that retries up to N times. Same shape as today's single-agent step but with bounded automatic retry on rate limits.

**Persistence:** every candidate row from every attempted slot is persisted to `step_result` with a new `slot_index` column (NULL for single-slot steps). The `v_slot_outcomes` view surfaces "how often does slot 0 actually carry the day vs needing fallback?" — useful for tuning slot ordering. Per-`(role, provider)` win-rate metrics in `v_provider_role_metrics` are slot-agnostic, so existing dashboards keep working.

**Audit warning** — the validator emits a non-fatal warning if a step's **final** slot has all candidates with 0 retries. The point of fallbacks is graceful degradation; if the last line of defence has no retry budget, a transient blip on its provider parks the card in Error. Add `"retries": N` to at least one candidate, or add a cheaper-still slot.

**Not yet supported (deliberate gaps):**

- **Gate-check candidates.** `gateCheck` is a state-level field, not an entry in `steps[]`, so it can't carry `candidates[]` / `evaluator`. To compare gate checkers (e.g. Qwen vs Haiku for `gate_checker` role), run separate cards with each provider routed to the gate role and compare metrics manually, OR temporarily inline the gate as a regular `steps[]` entry that supports candidates. This is a real limitation — file an issue if blocking.
- **Cross-step session reuse for candidates.** Each candidate spawns its own short-lived process; no `IAgentExecutorSession` reuse across candidate runs (each has a different worktree mount, so sessions wouldn't help anyway).

### 10.2 Re-run fast-path (skip-on-rerun)

When a card returns from a Questions column for a re-run, every step in the state would re-execute from scratch by default — including ones that already completed successfully on the first pass. For multi-step states (design has 4 steps; impl has 2; test has 2), that wastes minutes and tokens on cycles that have nothing new to add.

The runtime detects re-runs automatically and prepends a "RE-RUN; bail with COMPLETE if nothing relevant changed" preamble to the agent's task prompt. The agent reads its prior output (embedded in the preamble) and the latest comments (which include the user's clarification), then either:

- Confirms with `outcome: COMPLETE` and `detail: "Confirmed prior output remains accurate."` in seconds, or
- Produces a refreshed output that incorporates the new context.

**No config knobs.** This is automatic for every step type — single-agent steps, candidate groups, gate checks, optional specialist reviewers, and the evaluator itself. The agent decides what's relevant; the orchestrator just frames the question.

**Detection criteria (both must hold):**

1. The step's marker is on the card (e.g. `<!-- agent-step:create_design -->`, `<!-- gate-check:Ready for Design -->`, `<!-- agent-step:optional:security_review -->`).
2. The most recent `step_result` row for this `(card, state, step)` from a *prior* run (excluding the in-flight one) has `outcome = COMPLETE`. Prior `NEEDS_INFO` or `ERROR` outcomes don't qualify — those need to re-run normally.

**Force a fresh re-run.** Delete the step's comment from the card before moving the card back to "Ready for X". With the marker gone, the preamble is suppressed and the step runs from scratch. This is operator-controllable per-step: keep the comments for steps that don't need refreshing, delete the ones that should re-evaluate.

**The previously-NEEDS_INFO step always runs fully.** The step that asked the questions in the prior run gets no fast-path preamble — it needs to consider the operator's answers from scratch.

**Logged signals.** Look for `Re-run preamble injected for step '...' on card N` (Information level) to verify the fast-path is firing. Absence of the log line on a re-run means: marker not found on card, OR prior outcome wasn't COMPLETE, OR DB drift (also logs a Warning in the last case).

**No re-run preamble for first runs.** When there's no prior comment marker on the card, the step runs identically to today. The fast-path is purely additive.

---

## 11. Setting up a brand-new project (step by step)

This is the canonical bootstrap flow. Follow it once when wiring AI Board to a new GitHub Projects board.

### 11.1 Prerequisites on the operator machine

- .NET 10 SDK (only needed if building from source; releases ship self-contained binaries)
- Docker Desktop running (only required for `docker-*` executors)
- `gh` CLI authenticated with `project` + `repo` scopes (`gh auth login`)
- `claude` CLI authenticated (only required for `claude-cli` / `docker-claude-cli`)
- A GitHub Project (v2) attached to the target repo

### 11.2 Place the binaries + config

Unpack the release zip into a directory (call it `aiboard/`). This is the **install directory** — it stays put once unpacked. Confirm the layout:

```
aiboard/                            # install directory (one per machine)
├── aiboard.exe                     # the worker binary (or aiboard on Linux/macOS)
├── appsettings.json                # shipped defaults — DO NOT EDIT
├── appsettings.user.json           # (you create this if you have machine-wide secrets — gitignored)
├── appsettings.user.example.json   # template
├── workflow.github.example.json    # template — DO NOT EDIT; copy into your project's .aiboard/
├── prompts/                        # all role + state + gate prompts
├── docs/                           # human docs
├── db/migrations/                  # Flyway SQL — needed if running Postgres
├── docker-compose.yml              # local Postgres + Grafana stack
├── QUICKSTART.md
└── Agent.md                        # (this file)
```

Per-project config lives in **the project repo's root**, NOT in this install directory:

```
your-project/                       # any number of projects can use the same install
├── .git/
├── .aiboard/                       # CREATE THIS for per-project config
│   ├── appsettings.json            # board ID, workflow path, repo (project-specific)
│   └── workflow.github.json        # workflow shape (states, roles, transitions)
├── src/
└── ...
```

The project-local `.aiboard/` files override the install-directory `appsettings.json` silently. If `aiboard` is run from anywhere inside `your-project/`, the project's `.aiboard/` is loaded automatically.

### 11.3 Create the project's `.aiboard/` directory

In **the project repo's root** (NOT next to `aiboard.exe`), create a `.aiboard/` directory and put `appsettings.json` inside it:

```json
// {project-repo-root}/.aiboard/appsettings.json
{
  "BoardProvider": "github",
  "WorkflowConfigPath": "./workflow.github.json",
  "GitHubProjects": {
    "Owner": "<your-github-username-or-org>",
    "Repo": "<owner>/<repo>",
    "ProjectNumber": "<the project number from the URL>"
  },
  "Database": {
    "ConnectionString": "Host=localhost;Port=5432;Database=aiboard;Username=aiboard;Password=aiboard"
  }
}
```

Leave `AgentExecutor` unset to enable per-role provider selection (recommended). If you only have Claude CLI on the host and no Docker, set `"AgentExecutor": "claude-cli"`.

> **Why `.aiboard/` and not the global `appsettings.json`?** The project's `.aiboard/appsettings.json` overrides the shipped `appsettings.json` next to `aiboard.exe`. Putting per-project values (board ID, workflow path, repo) in the project keeps them committed alongside the project code and prevents one project's config from contaminating another's. Editing the global file silently fails to take effect once a project ships its own `.aiboard/`. See §4 for the full precedence rules.

If you have **personal credentials that span every project** (e.g., a personal Anthropic API key, a Trello token), put those in `appsettings.user.json` next to `aiboard.exe` instead — gitignored, machine-local, never committed:

```json
// {aiboard-install-dir}/appsettings.user.json
{
  "ClaudeCli": { "ExecutablePath": "claude" }
}
```

> **Canonical `.gitignore` for `.aiboard/`.** The directory mixes **two kinds of files**: project config (workflow + appsettings — should be committed and travel with the repo) and runtime ephemera (`tasks/`, `comments/`, `images/`, `updates/` — written by the orchestrator on every agent run, churns constantly, must never be committed). Add this snippet to the project repo's root `.gitignore`:
>
> ```gitignore
> .aiboard/*
> !.aiboard/workflow.json
> !.aiboard/appsettings.json
> ```
>
> If you renamed the workflow file (e.g. `workflow.github.json`), add a matching `!.aiboard/<name>.json` line. If your project also keeps a `appsettings.user.json` for machine-local secrets (different from the install-dir one — same name, different scope), do **not** add a `!`-line for it — it should stay gitignored as the wildcard already excludes it. `aiboard --mode init` prints this snippet at the end of its output as a reminder.

### 11.4 Create the project's `.aiboard/workflow.github.json`

Copy `workflow.github.example.json` from the aiboard install directory into the project's `.aiboard/workflow.github.json` (NOT alongside the example file at the install). The example wires the full SDLC pipeline (Backlog → Ready for Design → Designing → Designed → Ready for Implementation → ... → Done). At minimum:

1. Verify the **state keys match your board column names** (case-sensitive). `WorkflowConfig.ResolveState` throws on mismatch.
2. Decide which roles route to which providers (§7 + §9).
3. Set `polling.priorityFieldName` to the actual priority field on your board, and `priorityOrder` to its option values (e.g. `["P0", "P1", "P2"]`).
4. Set `estimation.calibrationTicketId` to a real ticket on your board you'd call "size N" (where N = `calibrationSize`). This anchors the estimator.
5. Set `estimation.fieldName` to the project field that holds estimates.
6. Confirm `cardTypes` matches how you label cards (defaults assume `type:story` / `type:task` labels).

### 11.5 Validate the wiring before running anything

```powershell
.\aiboard.exe --mode validation --board-id <projectNumber>
```

This is read-only. It cross-checks every column / field / option / prompt referenced in `workflow.github.json` against the live board and the filesystem. **Fix every Error before running any agent.** Warnings are advisory.

### 11.6 Bring up the database (optional but recommended)

```powershell
docker compose up -d
```

Postgres on `localhost:5432`, Flyway migrations run automatically, Grafana dashboard at `http://localhost:3000` (admin/admin).

### 11.7 Build the sandbox image (only if using docker-* executors)

```powershell
.\scripts\build-sandbox.ps1               # for docker-claude-cli + docker-claude-qwen
.\scripts\build-codex-sandbox.ps1         # for docker-codex
.\scripts\build-opencode-sandbox.ps1      # for docker-opencode
```

For Qwen-target executors, also bring up the sibling [`local-llm`](../local-llm/) project (`cd ..\local-llm && docker compose up -d`) and verify `docker network ls | Select-String llm-net`.

If your project's agent work needs additional tooling baked into the sandbox (a game engine, a JVM, a specific compiler, etc.), don't fork the upstream `Dockerfile` — overlay it. See [docs/ProjectOverlays.md](docs/ProjectOverlays.md) for the `FROM aiboard-X-sandbox:latest` pattern, build-script template, and `DockerAgents:*:ImageName` wiring.

### 11.8 First run

```powershell
.\aiboard.exe --mode agent --card-id <id> --board-id <projectNumber> --workspace .
```

…or for hands-off polling:

```powershell
.\aiboard.exe --mode polling --board-id <projectNumber> --workspace .
```

### 11.9 Iterate via metrics

```powershell
.\aiboard.exe --mode metrics --since 7d
```

Use the per-(role, provider) data to refine routing. If a role's win rate drops, change its `provider` in `workflow.github.json`.

---

## 12. Operational notes worth knowing

- **Multi-tenant DB partitioning.** Every per-tenant table carries `tenant_id TEXT NOT NULL` (format `{provider}:{identifier}`, e.g. `github:owner/repo/4`). Two projects can share one Postgres without colliding. Resolved fail-fast at startup from `BoardProvider` + the active board's config.

- **Graceful shutdown.** Polling and queue modes honor two-phase Ctrl+C. First press finishes the current card; second press force-quits. The runner restores in-progress cards to their trigger column on shutdown.

- **Worktree paths.** Worktrees are created at `{repo}-worktrees/aiboard/{cardId}` by default. Override with `WorktreeBasePath` in `appsettings.user.json`. Candidate worktrees live at `{repo}-worktrees/aiboard-cand/{cardId}-{groupShort}-{index}-{provider}` (sibling — never nested).

- **Image downloading.** `ImageDownloader` pulls images referenced in card bodies into `.aiboard/images/{cardId}/`. GitHub's `user-attachments` URLs need a Bearer token; `ImageDownloader` obtains one via `gh auth token` at startup when `BoardProvider=github`.

- **Cross-references.** `#5`, `#12` etc. in card bodies are resolved by `CrossReferenceResolver` and the referenced cards are pulled into the agent's workspace.

- **Comment markers.** Each step writes to a comment with marker `<!-- agent-step:{step.Name} -->`. Rerunning a step upserts the same comment (no spam).

- **Failure classification.** `agent_run.failure_reason` enum: `RATE_LIMIT` (CLI rate-limit detected → card restored to trigger column for retry), `INFRASTRUCTURE` (CLI shell-level launch failure or Docker daemon error), `TIMEOUT`, `AGENT_ERROR` (everything else).

- **Codex defensive diagnostics.** When a role uses `provider=codex`, the executor runs in loud-failure mode: startup `codex --version` probe, per-invocation Info logs of prompt size + schema SHA + effective sandbox policy, stderr signature detection, no silent text-keyword fallback.

- **Schema migrations.** Flyway in `docker-compose.yml`. Latest migration is V19 (partial UNIQUE index on candidate slot). When adding a new migration, prefer `DROP VIEW IF EXISTS` before `CREATE VIEW` in any view that adds/reorders columns — Postgres rejects column reorders in `CREATE OR REPLACE VIEW`.

---

## 13. Troubleshooting quick reference

| Symptom | Likely cause | Fix |
|---|---|---|
| `No agent executor registered for provider 'X'` | The provider key in `workflow.json` doesn't match a registered executor, or its prerequisite (Docker / CLI / image) is missing. | `--mode validation` lists what's available. Build the sandbox image; check `AGENT_EXECUTOR`. |
| `BoardProvider=github but GitHubProjects:Owner missing` (and similar) | Required config not in any source. | Set in `appsettings.user.json` or via env (`GitHubProjects__Owner`). |
| Flyway migration aborts with "cannot change name of view column" | A `CREATE OR REPLACE VIEW` is reordering columns. | Add `DROP VIEW IF EXISTS <name>;` immediately before it. |
| Card stuck in "Designing" / "Implementing" after a crash | Orchestrator died between IN_PROGRESS and COMPLETE. | Manually drag back to the "Ready for X" column to retry. |
| `ApplicationException: rate limited` then retry | Rate limit detected from CLI stderr (`AgentCli`) or board API (`BoardApi`). | Wait — `PollingRunner` backs off 30 min for CLI, 2 min for board. |
| Qwen first request takes 60+ seconds | Cold prefix cache. Normal. | `TimeoutSeconds: 600` in DockerAgents:OpenCode / DockerAgents:ClaudeQwen. |
| Two concurrent candidates against Qwen are slow | `local-llm` server is `--parallel 1`. Concurrent calls bust the prefix cache. | `CandidateExecutor` runs candidates sequentially; if you ran them in parallel manually, don't. |
| OpenCode keeps returning ERROR with raw stdout | Model isn't producing parseable JSON; bounded retry exhausted. **Check the structurer ran** — startup logs `Docker/OpenCode invoking structurer for card N` on first parse failure (v0.0.22+). If the structurer also failed, look for `falling through to retry loop` followed by `OpenCode CLI produced no parseable Agent Contract JSON after N attempts`. | Check `CliFailureHintDetector.OpenCodeSignatures` matches in logs. May indicate model misconfiguration or upstream rate-limit on the proxy. If the agent's narrative looks correct but doesn't end with JSON, the structurer should be recovering it — verify `EnableStructurer: true` in `DockerAgents:OpenCode`. |
| `Schema validation` stderr from Claude / Codex | Schema mismatch between the CLI version and what `AgentSchemas.OutcomeSchema` expects. | Update the CLI image; check schema SHA in startup logs. |

---

## 14. Where to read deeper

The `docs/` folder ships in the release. After reading this file, target reads based on the work:

- `docs/DockerSandbox.md` — real-Anthropic Docker sandbox how-to (build image, mount layout)
- `docs/OpenCodeSandbox.md` — OpenCode → local Qwen, including dual-model setup
- `docs/ClaudeQwenSandbox.md` — Claude CLI → local Qwen, including A/B framing vs OpenCode
- `docs/CandidateEvaluation.md` — multi-agent candidate evaluation in depth
- `docs/CardTypesAndGeneration.md` — label-based vs field-based card type discrimination + `setFields`
- `QUICKSTART.md` — operator-friendly first-run walkthrough (less LLM-targeted than this file)

The source repository additionally maintains a `memory-bank/` directory (architecture-as-data notes, decision lists) that's kept in lockstep with code changes. Those files are not bundled with the release; if you have access to the source tree, prefer them for the most authoritative architectural detail.
