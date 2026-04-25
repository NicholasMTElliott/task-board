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
    ↓ create git worktree, write task files + comments file
    ↓ execute steps sequentially (each step = Claude CLI subprocess)
Post-process: update card body, upsert step comments, move to outcome column
```

### Polling Mode
```
dotnet run -- --mode polling --board-id 1 --workspace .
    ↓ scans all cards on the board
    ↓ finds cards in "Ready for" trigger columns
    ↓ picks highest-priority card (via priority field)
    ↓ runs AgentRunner.ExecuteAsync() same as agent mode
```

### Metrics Mode
```
dotnet run -- --mode metrics [--card-id N | --since 7d]
    ↓
MetricsRunner (C#)
    ↓ queries PostgreSQL via IMetricsStore / PgMetricsStore
    ↓ aggregates agent_run + step_result data (SQL views)
Formats and prints metrics table to stdout
```

### Queue-based Mode (legacy, for webhook-triggered flows)
```
Webhook → Cloudflare Worker → PGMQ on Neon → .NET EventProcessor → Orchestrator → Board API
```

> **Current focus:** Direct CLI agent mode, polling mode, and metrics mode with GitHub Projects. Queue-based flow preserved for future webhook integration.

## Core Design Patterns

### State Machine
Board columns (GitHub Projects status field / Trello lists) are the authoritative state of a card. By default, each state key in the workflow config equals the column name. With `WorkflowState.Column`, multiple states can share a single column, disambiguated by card filters (field values, assignee, labels). The centralized `WorkflowConfig.ResolveState(BoardCard)` method replaces all direct dictionary lookups — it matches card column name to effective column, evaluates filters, and throws `InvalidOperationException` on ambiguity. The AgentRunner determines transitions deterministically based on workflow config.

### Multi-Step Execution
States can define multiple sequential steps, each with its own role (and therefore model) and task prompt. Steps execute in order within the same worktree. Communication between steps happens via:
- **Task file on disk** (`/.aiboard/tasks/{id}.md`) — steps read/write sections; card body is updated after each step.
- **Comments file on disk** — refreshed after each step's comment is upserted to the board, so subsequent steps see prior step output as a conversation chain.
- **Step-specific comments** — each step gets its own comment marker (`<!-- agent-step:{step.Name} -->`) on the card.
- **Run-level comment** — posted after all steps with marker `<!-- agent-run:{runId} -->`, but **only when `gitNote` is non-null** (i.e., for `commit_and_push` states that produce a branch-push note). For `discard` states (design/test), no run-level comment is posted — only step comments appear.

If any step returns non-COMPLETE, the chain halts and the card transitions based on that outcome.

Legacy single-step states (top-level `role` + `taskPrompt`) are auto-normalized to a one-element steps array via `WorkflowState.Normalise()`.

### Current Multi-Step Configurations
**Ready for Design** (4 steps: opus 4.6 via `senior_engineer` + haiku via `estimator`):
1. `review_related_tickets` — scan related tickets, assess impact
2. `create_design` — produce technical design
3. `review_design_conflicts` — verify no cross-ticket conflicts
4. `estimate_ticket` — calibration-based size estimation (haiku via `estimator`)

**Ready for Tasking** (1 step: opus 4.6 via `senior_engineer`):
1. `generate_tasks` — decompose user story into child tasks with best-guess estimates; `generationConfig` creates `type:task` cards in "Ready for Design" linked to parent, copies priority field

**Ready for Implementation** (2 steps):
1. `implement` — write code (sonnet 4.6 via `implementer`)
2. `code_review` — review changes (sonnet 4.6 via `code_reviewer`)

**Ready for Test** (2 steps):
1. `qa_validation` — test validation (opus 4.6 via `qa`)
2. `update_documentation` — memory bank documentation update (sonnet 4.6 via `implementer`)

### Gate Checks
After all steps complete for a state, a `gateCheck` runs a lightweight agent (`gate_checker` on Haiku) to validate the output before transitioning. Gate checks are configured for all three agent states: **design, implementation, and test**. On failure, card moves to `GATE_FAIL` target (typically back to the trigger column for retry).

Gate checks can also request **optional specialist-reviewer steps** from a per-state catalog (see below).

### Optional Specialist-Reviewer Steps
Each agent state defines an `optionalSteps` catalog of specialist reviews. The gate check agent sees the catalog and can request any subset via `requestedSteps` in its output. If requested, `AgentRunner.ExecuteOptionalStepsAsync` executes them sequentially before transitioning.

**Catalogs by state:**
- **Ready for Design** — 12 optional reviews (UX, UI, infrastructure, data modeling, API, security threat model, performance, accessibility, legal/regulatory, migration, cost, observability)
- **Ready for Implementation** — 12 optional reviews (security audit, SOC2 compliance, infrastructure, performance, accessibility, API contract, database, dependency, UI/visual, error handling, i18n, privacy)
- **Ready for Test** — 10 optional reviews (security, performance, accessibility, integration, edge cases, regression risk, data integrity, compliance, disaster recovery, monitoring/alerting)

**Roles used:**
- `specialist_reviewer` (sonnet 4.6) — most optional steps
- `senior_specialist_reviewer` (opus 4.6, `effort: max`) — high-stakes reviews (legal/regulatory, SOC2 compliance, privacy)

**Prompt files:** `prompts/optional-steps/{design,impl,test}/<step_name>.md`

### IN_PROGRESS Transition
When an agent picks up a card from a "Ready for X" trigger column, it first moves the card to the "X-ing" in-progress column before starting work. Defined as `"IN_PROGRESS": "Designing"` in the state's transitions map.

### ITaskBoardClient Abstraction
Provider-agnostic interface for all board operations:
- `GetCardAsync(cardId)` / `GetBoardCardsAsync(boardId)` — read cards
- `GetCardCommentsAsync(cardId)` — read comments
- `UpdateCardBodyAsync(cardId, body)` — write card content
- `MoveCardToColumnAsync(cardId, columnId)` — state transitions
- `UpsertAgentCommentAsync(cardId, body, marker)` — agent feedback with HTML marker for idempotent upsert

Implementations: `GitHubProjectsClient` (via `gh` CLI), `TrelloClient` (HTTP), `StubTaskBoardClient` (testing).
Selection via `BOARD_PROVIDER` env var: `github`, `trello`, or default `stub`.

### Agent Contract
The agent executor (`ClaudeAgentExecutor`) uses `--json-schema` to enforce structured output:
```json
{
  "outcome": "COMPLETE | NEEDS_INFO | ERROR",
  "detail": "optional summary string",
  "questions": [{ "question": "...", "recommendations": ["..."] }]
}
```
- `outcome` is required; `detail` and `questions` are optional
- Agents do NOT move cards. The orchestrator owns all transitions.
- Backward compat: parser also accepts `SUCCESS` → `COMPLETE`, `QUESTIONS` → `NEEDS_INFO`

### Git Worktree Isolation
Agent execution uses git worktrees for isolated working directories:
- `GitWorkspaceManager.CreateWorktreeAsync(repoPath, branchName)` → returns worktree path
- Convention: `{repoPath}-worktrees/aiboard/{cardId}`
- Edge cases handled: worktree already exists (reuse), branch already exists locally or on remote (fetch + track), stale directory (prune + recreate)
- Branch lookup: `FindBranchByPrefixAsync` checks local then remote refs; if found only on remote, fetches and creates a local tracking branch
- `.aiboard/` task files are gitignored and ephemeral — not committed to git
- Main repo working tree is never modified during agent execution

### Orchestrator-Owns-Git-Writes
All git write operations (commit, push) are performed by the orchestrator on the host AFTER the agent (and any Docker container) has exited. Agents must not run git write commands.

- `AgentRunner.HandleGitBehaviorAsync` is the primary git write path; runs post-execution after session disposal
- `gitBehavior: "commit_and_push"` — orchestrator commits then pushes on COMPLETE; commits locally only on non-COMPLETE
- `gitBehavior: "commit_only"` — orchestrator commits without pushing
- `gitBehavior: "discard"` — no git writes; unexpected agent changes are discarded with a warning
- Agent commit message is read from `.aiboard/commit.md` in the worktree; falls back to auto-generated message
- **Docker enforcement:** base `.git` directory mounted read-only — git writes physically impossible inside container
- **Non-Docker enforcement:** "Git Policy" section in all system prompts prohibits write commands
- All system prompts include "Git Policy" prohibiting: `git commit`, `git push`, `git checkout`, `git reset`, `git merge`, `git rebase`, `git branch -d`, `git rm`, `git clean`
- Read-only commands (`git log`, `git status`, `git diff`, `git show`, `git blame`) are allowed inside the container

### Cross-Reference Resolution
Task files can reference other cards (e.g., `#5`, `#12`). The `CrossReferenceResolver` parses these, fetches referenced cards, and includes them in the agent's workspace. This creates tracked relationships so dependent context flows through the pipeline.

### Image Downloading
`ImageDownloader` downloads images referenced in card bodies (markdown `![](url)` and HTML `<img src>`) to `.aiboard/images/{cardId}/` in the worktree. Images are passed to the agent as local files via `TaskFileManager`.
- Named HttpClient `"ImageDownloader"` configured at startup with `User-Agent: TaskBoard-Worker/1.0`
- When `boardProvider == "github"`, a GitHub token is obtained via `gh auth token` at startup and set as a Bearer Authorization header (required for `github.com/user-attachments/assets/` URLs which return 404 without auth)
- Max 20 images per card, 10 MB per image
- Filenames: SHA256(url)[0:12] + extension (deterministic, deduplicating)
- Failures are logged and silently skipped — never block agent execution

### Agent Executor Pattern
Agent executors are registered via `AgentExecutorResolver` which resolves by provider key (`claude-cli`, `codex`, `stub`, `docker-claude-cli`). `AGENT_EXECUTOR` env var has three modes:
- `stub` — all providers mapped to stub (testing/dev)
- `docker-claude-cli` — Docker executor registered under both `docker-claude-cli` and `claude-cli` keys; `claude-cli` workflow roles transparently route to Docker without config changes; fails fast at startup if Docker unavailable
- unset / any other value — production auto-detect mode: real providers detected at startup (docker-available → registered under `docker-claude-cli`; other values trigger a deprecation warning)

**DockerClaudeAgentExecutor** (`IAgentExecutor`, provider key `docker-claude-cli`) wraps Claude CLI invocation inside `docker run -i --rm`. Key design:
- Standalone class (no inheritance from `ClaudeAgentExecutor`) — differences in process surface are too large
- Options bound to `DockerClaudeAgentOptions` (derives from `DockerAgentOptionsBase`); shared Docker-runtime settings (network/CPU/memory limits, reuse, additional mounts) live on the base so a future `DockerCodexAgentExecutor` can share them
- System prompt file translated: host directory mounted read-only at `DockerClaudeAgentOptions.PromptMountPoint` (`/mnt/aiboard/prompts`); container path computed from `Path.GetFileName`
- Container named `{prefix}-{tenantHash}-{cardId}-{random8}` (prefix: `aiboard-run`); random suffix prevents collisions, `--rm` cleans up on normal exit
- On timeout (`TimeoutException`) or cancellation (`OperationCanceledException`), `ExecuteAsync` issues explicit `docker stop -t 30` (30s SIGTERM grace, then SIGKILL) followed by `docker rm -f` fallback; cleanup is best-effort (never throws, never masks original exception); uses `CancellationToken.None` since caller token may already be cancelled
- Exit codes classified: Docker daemon errors (125/126/127/137) vs. Claude CLI errors (0–124) via `IsDockerExitCode`
- `CLAUDECODE` env var stripped from subprocess environment
- Rate-limit detection via `ClaudeAgentExecutor.IsRateLimited(stderr)` (same as host executor)
- NDJSON parsing via shared `AgentOutputParser.ParseStreamOutput`
- Workspace/credential mounts built by `DockerClaudeMountBuilder` (inherits from `DockerMountBuilderBase` for the shared worktree/`.git` mount construction; injected optionally); workspace mounts and env vars passed to `docker run` args and `SessionRequest`
- Extensible static mounts (`DockerClaudeAgentOptions.AdditionalMounts` dictionary — inherited from the base) — operator-supplied overrides beyond the standard workspace/credential set
- Registered automatically when Docker daemon is detected at startup (`PrerequisiteValidator.IsDockerAvailableAsync` runs `docker info`)

### Container Session Reuse (IAgentExecutorSession)
To avoid per-step container startup overhead, executors that support Docker can implement `ISessionableAgentExecutor`, which creates a long-lived container session spanning the full agent run pipeline (main steps + gate check + optional specialist reviews).

**Interfaces:**
- `IAgentExecutorSession` (`IAsyncDisposable`) — represents a live container; `SessionId`, `ProviderKey`, `IsAlive`, `ExecuteInSessionAsync`
- `ISessionableAgentExecutor : IAgentExecutor` — adds `ProviderKey` and `TryCreateSessionAsync(SessionRequest, CancellationToken)`
- `SessionRequest` record — `CardId`, `RunId`, `ContainerName` (`aiboard-{cardId}`), `ImageName`, optional `Mounts` (`IReadOnlyList<DockerMount>?`) and `EnvironmentVariables`

**Lifecycle in `AgentRunner.ExecuteAsync`:**
1. Before the step loop, attempt `TryCreateSessionAsync` if the resolved executor implements `ISessionableAgentExecutor` and `DockerClaudeAgentOptions.ReuseContainer` (inherited from `DockerAgentOptionsBase`) is `true`.
2. All LLM invocations (steps, gate check, optional specialist reviews) go through `ExecuteWithSessionAsync`, which routes through `session.ExecuteInSessionAsync` if the session is alive and the step's provider matches.
3. Provider mismatch (e.g., haiku gate check on `claude-cli` while session is `docker`) bypasses the session and calls `executor.ExecuteAsync` directly — logged at Debug.
4. If the session dies between steps, the invocation falls back to `executor.ExecuteAsync` transparently (logged at Warning).
5. `await using` on the session guarantees disposal on all exit paths (success, step failure, cancellation, exceptions). Disposal calls `docker stop` + `docker rm`.

**Container naming:** `aiboard-{cardId}` — one per card, mutual exclusion enforced by IN_PROGRESS column transition.

**Configuration:** `DockerClaudeAgentOptions` section (key `DockerAgents:Claude`; legacy `Docker` still honoured with a deprecation warning) in `appsettings.json`:
- `ReuseContainer` (bool, default: `true`) — set to `false` to revert to per-step `docker run`
- `ImageName` (string, default: `"aiboard-agent-sandbox:latest"`) — Docker image
- `ContainerNamePrefix` (string, default: `"aiboard-run"`) — prefix for `{prefix}-{cardId}-{random8}` container names
- `PromptMountPoint` (string, default: `"/mnt/aiboard/prompts"`) — read-only mount for system prompt files
- `MaxBudgetUsd` (decimal, default: `10.00`) — max Claude CLI budget per invocation
- `TimeoutSeconds` (int, default: `900`) — container kill timeout
- `ContainerUser` (string, default: `""`) — user to run as inside container (empty = image default)
- `MemoryLimit` (string?, default: `null`) — optional memory limit, e.g. `"4g"`
- `CpuLimit` (string?, default: `null`) — optional CPU limit, e.g. `"2.0"`
- `NetworkMode` (string, default: `"host"`) — container network mode, forwarded as `--network` to `docker run`. `"host"` gives the sandbox access to host-published ports (e.g. the local `docker-compose` Postgres/Grafana stack on `localhost:5432`, `localhost:3000`). Use a compose network name (e.g. `"task-board_default"`) to reach support services by service name. Empty/null omits the `--network` flag (Docker default bridge). `MemoryLimit`, `CpuLimit`, and `ContainerUser` are likewise forwarded to `--memory`, `--cpus`, and `--user` respectively when set — all built in `DockerClaudeAgentExecutor.BuildDockerArgumentList`
- `CredentialPath` (string, default: `""`) — host path to Claude CLI credentials; auto-detected from `~/.claude` if empty; used by `DockerClaudeMountBuilder`
- `CredentialMountPoint` (string?, default: `null`) — container path for credentials; defaults to `/home/agent/.claude` (matches `agent` user home in sandbox image)
- `AdditionalMounts` (Dictionary, default: `{}`) — operator-supplied static volume mounts (beyond standard workspace/credential set)

**Fallback strategy:** If `TryCreateSessionAsync` returns `null` (image not found, Docker unavailable), the run proceeds without a session — all steps use `executor.ExecuteAsync` directly.

**Orphaned container detection:** `PrerequisiteValidator.DetectOrphanedContainersAsync` runs `docker ps --filter name=aiboard-` at startup and logs a warning if any `aiboard-*` containers are found (prior crash cleanup). Cleanup is manual.

**Non-session executors** (`ClaudeAgentExecutor`, `CodexAgentExecutor`, `StubAgentExecutor`) do not implement `ISessionableAgentExecutor` and are completely unaffected.

### Docker Workspace and Credential Mounting

`DockerClaudeMountBuilder` produces up to four bind mounts per run (the first three come from `DockerMountBuilderBase`):

| Mount | Host path | Container path | Access |
|-------|-----------|----------------|--------|
| Worktree | `{worktreePath}` | `/workspace` | RW (agent's working dir; `-w /workspace`) |
| Base `.git` | `{baseRepoPath}/.git` | `/repo/.git` | RO (shared object store + refs) |
| `.git` file override | temp file | `/workspace/.git` | RO (shadows host-path gitdir reference) |
| Credentials | per-run temp copy of `~/.claude/` (or `CredentialPath`) | `/home/agent/.claude` (or `CredentialMountPoint`) | RW staged copy — CLI needs to create `session-env/` at runtime. Copy excludes `projects`, `shell-snapshots`, `todos`, `history`. Temp dir deleted on `DockerMountContext` disposal; host `~/.claude/` is never mutated by the agent. |

System prompt files use the pre-existing `DockerClaudeAgentOptions.PromptMountPoint` mount (unchanged).

**`.git` file override:** Git worktrees contain a `.git` file with an absolute host path (`gitdir: /host/path/.git/worktrees/{name}`). Inside the container this path doesn't exist. The override is a temp file containing the container-internal path (`gitdir: /repo/.git/worktrees/{name}`), bind-mounted over `/workspace/.git`. The `commondir` relative path (`../..`) resolves correctly without modification.

**Always-RO `.git`:** Agents do **no git writes** — commits, pushes, and index modifications are orchestrator responsibilities (see #67). The base `.git` is always mounted read-only. `GIT_OPTIONAL_LOCKS=0` env var (injected via `DockerMountContext`) allows git read commands to skip index lock acquisition.

**Path translation:** `DockerMountContext.TranslatePath(hostPath)` maps host absolute paths to container equivalents (e.g., `{worktreePath}/.aiboard/tasks/42.md` → `/workspace/.aiboard/tasks/42.md`). Used for `--append-system-prompt-file` and task file path arguments passed to the Claude CLI.

**Windows paths:** `DockerMountBuilderBase.NormalizeHostPath` converts Windows backslashes to forward slashes for Docker Desktop compatibility.

`DockerMountContext` is `IAsyncDisposable`; disposal deletes the temp `.git` override file after the container session ends.

### Claude CLI Subprocess Pattern
The .NET worker invokes the Claude CLI (`claude`) as a subprocess via `ClaudeAgentExecutor`.

**Windows invocation:**
- `ClaudeCliResolver.Resolve()` auto-detects the executable at startup by probing PATH for `claude.cmd` (Node.js/nvm4w) then `claude.exe` (native install)
- Resolved value is stored in `ClaudeCliLlmOptions.ExecutablePath` via `PostConfigure` — all consumers use the same resolved path
- Explicit `ExecutablePath` config overrides auto-detection
- Do NOT use `cmd.exe /c claude` (mangles quoted arguments) or PowerShell wrappers
- Remove `CLAUDECODE` env var from subprocess environment or the CLI refuses to run as a subprocess
- No double quotes in prompts — use plain text or single quotes

**CLI flags (required for structured output):**
- `--print` — non-interactive mode (prompt read from stdin)
- `--verbose --output-format stream-json` — NDJSON output; `--verbose` required with `stream-json`
- `--no-session-persistence` — prevents session reuse between runs
- `--json-schema <minified-json>` — must be single-line (minified)
- `--max-budget-usd` — cost control per invocation (preferred over `--max-turns`)
- `--permission-mode bypassPermissions --allowedTools *` — headless execution
- Do NOT use `--max-turns` — causes premature termination before structured output

**Prompt delivery:**
- System prompt via `--append-system-prompt-file` (file path)
- Task prompt piped via stdin (`RedirectStandardInput` + `StandardInput.WriteAsync`) — avoids Windows command-line length limits (~8,191 chars for cmd.exe)
- `providerParams` per state (e.g., `{"effort": "max"}` → `--effort` flag)

**Output parsing (NDJSON `ParseStreamOutput` → `ParseResult`):**
- Stream may contain multiple `type: "result"` messages; parser finds the one with `structured_output`
- Conversation log assembled from `type: "assistant"` messages (text content blocks)
- Timeout: event-based capture (`OutputDataReceived`/`ErrorDataReceived`) for partial output on timeout

### Merge Resolution
When a merge conflict is detected (via `MERGE_CONFLICT` transition), a `merge_resolver` agent (Sonnet 4.6) is invoked to resolve conflicts before retrying. Configured in `workflow.github.json` under `mergeResolution`.

### System Merge (Approved → Done)
The `Approved` state has `gateType: "system_merge"`. The system automatically merges the PR and moves the card to `Done`. On merge conflict, falls back to `Ready for Implementation`.

### Estimation
The design pipeline includes a calibration-based estimation step (step 4). Configuration in `workflow.github.json`:
```json
"estimation": { "calibrationTicketId": "34", "calibrationSize": 1, "fieldName": "Estimate", "scale": [1, 2, 4, 8] }
```
- `estimator` role (haiku) sizes the ticket relative to a calibration ticket
- On COMPLETE, the design state's transition uses array format with `setField` to write the estimate to the board's `Estimate` field:
  ```json
  "COMPLETE": [{ "type": "moveToColumn", "value": "Designed" }, { "type": "setField", "field": "Estimate", "value": "{{estimation}}" }]
  ```
- `AgentRunner` logs a warning when estimation is configured but no step returned a structured `estimate` field (the estimator agent may have described the estimate in `detail` text only)
- `TransitionExecutor` validates that template variables are resolved before dispatching actions: if `{{...}}` patterns remain after substitution, the action is **skipped with a warning** instead of failing silently. Guard is active only when a `templateContext` is provided.

### Child Task Generation (Story → Task Decomposition)
User stories can generate child task tickets via the `cardTypes` config:
```json
"cardTypes": { "story": { "name": "User Story", "labelPrefix": "type", "allowedChildren": ["task"] }, "task": { "name": "Task", "allowedChildren": [] } }
```

**Card type discriminators — labels, fields, or both.**
- Label-based (default): issues are labeled `type:story`, `type:task`, `type:bug`. `CardTypeDefinition.LabelPrefix` controls the format. Set `LabelPrefix` to empty/null to opt a type out of labels.
- Field-based: set `WorkflowConfig.CardTypeField` (e.g. `"Type"`) to name a project field that holds the type. `UpdateFileProcessor` writes `CardTypeDefinition.Name` into that field on create and reads it from parent `Metadata` first when resolving parent type for `AllowedChildren` enforcement; labels are checked as a fallback.
- Both mechanisms can coexist. Validator errors only when a `CardTypeDefinition` has **neither** a non-empty `LabelPrefix` nor a global `CardTypeField`.

**Setting arbitrary project fields on generated children.** `GenerationConfig.SetFields` is a literal field→value map merged into `CreateCardRequest.FieldValues` after `CopyFields` (copied from parent) and the estimate. On key collision, `SetFields` wins. Typical use: drop the child directly into its initial pipeline stage — e.g. `"setFields": { "Type": "Task", "Activity": "Design" }`.

**Pipeline flow for stories:**
Backlog → Ready for Design → Designed → **Ready for Tasking** → Tasking → **Waiting for Tasks** → Done

**Pipeline flow for tasks:**
Ready for Design → Designed → Ready for Implementation → ... → Approved → Done

**Tasking step** ("Ready for Tasking" state):
- `generate_tasks` step uses `senior_engineer` role with `generate_children.md` prompt
- `generationConfig` on the step: `targetType: "task"`, `targetColumn: "Ready for Design"`, `linkToParent: true`, `copyFields: ["priority"]`
- Agent writes `new-{slug}.md` files to `.aiboard/updates/` with `estimate` in front matter (best-guess from scale [1, 2, 4, 8])
- `UpdateFileProcessor` creates child tickets with type label, parent link, target column, copied priority field, and estimate
- Notification comments are skipped during structured generation (redundant with parent task list)
- Story estimate set to sum of child estimates via `setField` transition action

**Priority propagation:**
`GenerationConfig.CopyFields` specifies fields to copy from parent to child. `UpdateFileProcessor` fetches the parent card's metadata and passes matching values via `CreateCardRequest.FieldValues`. Currently used to copy `priority`.

**Estimate rollup:**
- `updateParentSum` transition action: finds the card's parent via `trackedInIssues` cross-refs, sums the specified field across all `sub_item` children, and sets the result on the parent card
- Configured on "Ready for Design" COMPLETE transition (updates story estimate after each child's design refines its estimate) and on "Waiting for Tasks" COMPLETE transition (final sum when story completes)
- No-op if card has no parent or cross-ref resolver is unavailable

**Completion tracking (event-driven, not polled):**
- "Waiting for Tasks" is a `holding` state — the poller ignores it entirely, avoiding deadlock
- When a child task completes merge (Approved → Done), the `completeParentIfReady` transition action fires
- It finds the parent via `trackedInIssues` cross-refs, fetches all siblings via `trackedIssues`, and checks terminal states
- If all siblings are Done → transitions parent to Done via its COMPLETE transition target
- If siblings are pending → posts/updates a progress comment on the parent (stable marker, upserted in place)
- `CompletionRunner` still exists for other `children_complete` use cases but is not used in the story-to-task flow

### Multi-Tenant DB Partitioning
Multiple projects (different GitHub repos/projects, Trello boards) can share one Postgres instance without colliding or blending data. Every per-tenant table carries a `tenant_id` column as the leading PK; every store query filters on it.

**`ITenantIdentifier`** — process-lifetime singleton resolved once at DI registration:
- `Value` — canonical `{provider}:{identifier}` string (e.g. `github:owner/repo/4`, `trello:abc123`, `stub:test`)
- `Provider` — bare prefix (`github`, `trello`, `stub`)
- `ShortHash` — 8-char lowercase hex of `SHA256(Value)`; used where the full string is too long or character-restricted (Docker container names; future PGMQ queue names)

**`TenantIdentifierFactory.Create(boardProvider, ghOpts, trelloOpts, stubName)`** — fail-fast resolution:
- `github` → requires `Owner`, `Repo`, `ProjectNumber`; throws `InvalidOperationException` naming the missing config key
- `trello` / `live` → requires `Trello:BoardId`
- `stub` (and unrecognised providers) → uses `Stub:TenantName` or defaults to `"test"`

Resolved during service registration in `Program.cs` so missing required config aborts startup before the host runs (no untenanted rows can be written).

**Tables partitioned (V15):** `agent_run`, `step_result`, `card_state`, `processed_events` — all have `tenant_id TEXT NOT NULL` as the leading PK column. Indexes lead with `tenant_id`. `step_result` FK is composite `(tenant_id, run_id) → agent_run(tenant_id, run_id)`.

**Views (V16):** `v_run_metrics`, `v_step_duration`, `v_card_metrics`, `v_card_rework` all project `tenant_id` so callers can scope queries.

**Stores:** `PgRunStore`, `PgMetricsStore`, `CardClaimService` inject `ITenantIdentifier`; every INSERT carries `tenant_id`, every SELECT/UPDATE filters on it (including run-id-keyed updates — prevents cross-tenant clobbering).

**Docker container naming:** `DockerClaudeAgentExecutor.BuildContainerName` emits `{prefix}-{tenantHash}-{cardId}-{rand}`; `AgentRunner` session container is `aiboard-{tenantHash}-{cardId}`. Two tenants with the same numeric `card_id` produce different container names. Orphaned-container detection still matches the `aiboard-` prefix.

**Not (yet) tenant-scoped:** PGMQ queues (`events`, `pings`) remain global; queue mode is legacy/secondary. `ITenantIdentifier.ShortHash` is the intended suffix when this is addressed.

**Migration path:** V15 drops & recreates tables (no data preserved by design); V16 recreates the four metrics views.

### Failure Classification
`AgentRunner.ClassifyFailure(Exception)` maps executor-thrown exceptions to the `FailureReason` enum persisted to `agent_run.failure_reason`:
- `TimeoutException` → `TIMEOUT`
- `CliInfrastructureException` → `INFRASTRUCTURE` (subclass of `InvalidOperationException`; thrown by `ClaudeAgentExecutor`, `CodexAgentExecutor`, and `DockerClaudeAgentExecutor` for shell-level launch failures — exit 126/127 for all three, plus Docker daemon errors 125/126/127/137 for the Docker wrapper)
- Everything else → `AGENT_ERROR`

`RateLimitException` is handled in its own catch block (before `ClassifyFailure` is reached) and maps to `RATE_LIMIT`, because rate-limit handling also restores the card to the trigger column for retry.

### Rate Limiting
- `CliRateLimitDetector` holds the per-CLI stderr pattern lists and a single `Matches(stderr, patterns)` helper (case-insensitive substring match). `ClaudePatterns` = "rate limit", "overloaded". `CodexDefaultPatterns` = "rate limit" / "rate-limit" / "rate_limit" / "ratelimit" / "too many requests" / "insufficient_quota" / "quota exceeded". Bare "429" is deliberately omitted (false-positive risk on numeric substrings).
- `ClaudeAgentExecutor.IsRateLimited` is a shim over the shared detector; `DockerClaudeAgentExecutor` reuses it directly. `CodexAgentExecutor.IsRateLimited` (instance method) merges `CodexDefaultPatterns` with operator-supplied `CodexCliLlmOptions.RateLimitPatterns`.
- All three executors throw `RateLimitException(RateLimitSource.AgentCli)` on: non-zero exit + matching stderr, OR exit 0 + empty stdout + matching stderr. `GitHubProjectsClient` detects board-side rate limits (HTTP 429, "abuse detection", "secondary rate") and throws with `RateLimitSource.BoardApi`.
- `AgentRunner` catches `RateLimitException`, restores card to trigger column for retry.
- `PollingRunner` applies aggressive backoff: board API = 2min base, agent CLI = 30min base (cap 2hr).

### Codex Executor Defensive Diagnostics
`CodexAgentExecutor` is instrumented for loud failure and rich diagnostics while Codex-backed runs are still stabilising. The philosophy is "visibly blow up, loudly, with enough info to tell WHY" — silent fallbacks are avoided.

- **Startup**: `CodexCliResolver.TryGetVersionAsync` runs `codex --version` (5s timeout, never throws). `Program.cs` logs the resolved path + version at Info; a warning is emitted if the probe fails, since unrecorded CLI versions make CLI-version-drift bugs hard to diagnose.
- **Per-invocation Info logs**: combined prompt size (chars + KB), output schema size + SHA256 prefix (stable correlator), effective policy (`yolo`, `fullAuto`, `sandbox` values **plus source**: `providerParams` or `config` or `(none)`), full CLI command line.
- **Per-invocation warnings**: oversize prompt (>200 KB), non-git workspace (`.git` not in ancestors), `yolo` set with `fullAuto`/`sandbox` (redundant — yolo suppresses both).
- **Stderr hint detection** (`DetectStderrFailureHint`) — substring matcher with an ordered signature table; categories: Auth (`OPENAI_API_KEY`, `not authenticated`, `invalid_api_key`, 401/403), Model (`model_not_found`, `unknown model`), VersionDrift (`unrecognized subcommand`, `unknown command`, `unexpected argument`), Schema (`schema validation`), Sandbox (`sandbox policy`), Quota (`insufficient_quota`), Network (`Failed to connect`, `timed out`). First match wins. Hint is logged at Error and also prepended to the thrown exception's `Message` as `[Hint] Category: …`.
- **Loud-failure policy — no structured_output**: when the NDJSON stream contains no `structured_output` event, the executor does NOT fall back to text-keyword scanning of raw stdout. It only accepts a backward-compat single-document JSON with `structured_output` at the top. Anything else throws `InvalidOperationException` with a diagnostic block (first 3 + last 3 stdout lines, line counts, stderr head, detected hint). This prevents the classic silent failure where "COMPLETE" appears in prompt-echo and the card is wrongly moved to Done.
- **Stream health warnings**: `ParseStreamOutput` tracks unknown top-level event types (anything outside `thread.started`, `turn.started|completed`, `item.started|completed|updated`, `response.started|completed`, `assistant`, `user`, `system`) and logs a single end-of-parse warning with counts — signals CLI-version drift. Also warns when ≥10% of non-empty NDJSON lines are malformed (min 2) and when any event with `"error"` in its type is seen.
- **Exception-message stderr cap raised** from 500 → 4000 chars; logs include up to 10 000 stderr chars at Error level.

The stubbed runner in `AgentExecutorContractTests` + per-scenario tests in `CodexAgentExecutorDiagnosticsTests` pin these behaviours. These diagnostics are intended to stay on until Codex reaches Claude-level reliability; they can be quieted to Debug later without changing behaviour.

### ProcessRunnerDelegate (test seam)
All three CLI executors (`ClaudeAgentExecutor`, `CodexAgentExecutor`, `DockerClaudeAgentExecutor`) accept an optional `ProcessRunnerDelegate` constructor parameter that defaults to the static `ProcessRunner.RunProcessAsync`. Tests pass a custom delegate to replay prerecorded `(exitCode, stdout, stderr)` without launching a real subprocess. `AgentExecutorContractTests` (abstract base in `lambda/tests/…/Clients/AgentExecutorContractTests.cs`) defines the shared scenario set every executor must satisfy; per-executor subclasses supply provider-specific stdout shapes (Claude/Docker-Claude use `{"type":"result","structured_output":{…}}`; Codex uses `{"type":"turn.completed","structured_output":{…}}`).

### Two-Phase Graceful Shutdown
Applies to `--mode polling` and `--mode queue` (not agent mode — single card, exits naturally).

**`ShutdownCoordinator`** — thread-safe singleton injected into `Program.cs`, `PollingRunner`, `QueueDrivenRunner`, and `AgentRunner`:
- `RequestShutdown()` — sets flag via `Interlocked.CompareExchange`; returns `true` only on first call; also cancels `IdleToken`
- `IsShutdownRequested` — read by runner loops before claiming new work
- `IdleToken` — `CancellationToken` cancelled on first Ctrl+C; used only for idle/backoff delays (not agent work)
- `Dispose()` — disposes internal `CancellationTokenSource`

**Two-phase Ctrl+C (in `Program.cs`):**
1. First Ctrl+C → `coordinator.RequestShutdown()` + log "Shutdown requested — finishing current work, press Ctrl+C again to force quit"
2. Second Ctrl+C → `cts.Cancel()` (hard cancel, existing behavior) + log "Force shutdown initiated."

**Runner loop behaviour:**
- `PollingRunner` / `QueueDrivenRunner`: loop condition checks `!shutdownCoordinator.IsShutdownRequested`; idle/backoff delays use a linked token (main token + `IdleToken`) so they abort immediately on first Ctrl+C
- `QueueDrivenRunner`: claim loop also checks `IsShutdownRequested` to prevent new claims; already in-flight `Task.WhenAll` tasks finish naturally

**`AgentRunner` inter-step check (`stepIndex > 0` guard):**
- Before each step after the first, checks `IsShutdownRequested`
- If set: for `commit_and_push` stages — commits and pushes partial work (best-effort, failure is logged as warning); for all stages — restores card to trigger column so it can be re-processed; returns `AgentOutcome.COMPLETE` (controlled interruption, not a failure)
- Nested try/catch isolates push failure (inner) → commit failure (outer) → card restore failure (outermost); card restore failure still returns COMPLETE

## Component Relationships

| Component | Responsibility |
|-----------|----------------|
| GitHub Projects / Trello | Human UI, planning content, state via column position |
| ITaskBoardClient | Provider-agnostic board abstraction |
| GitHubProjectsClient | GitHub Projects v2 via `gh` CLI (GraphQL + REST) |
| TrelloClient | Trello REST API |
| AgentRunner | Direct agent execution: fetch cards → worktree → steps → post-process |
| MergeRunner | Git merge + push for `system_merge` gate type |
| CompletionRunner | Polls child cards for `children_complete` gate type |
| PollingRunner | Automatic card pickup via priority-sorted polling |
| ClaudeAgentExecutor | Claude CLI subprocess with `--json-schema` structured output |
| DockerClaudeAgentExecutor | Claude CLI inside `docker run -i --rm`; provider key `docker-claude-cli`; auto-registered when Docker daemon detected |
| DockerOpenCodeAgentExecutor | OpenCode CLI inside `docker run -i --rm` targeting a local Anthropic-compatible llama.cpp server (default `http://llama-server:8080` on the `llm-net` bridge network owned by the sibling `local-llm` compose project); provider key `docker-opencode`; auto-registered when Docker detected; prompt-engineered JSON contract parsed by `OpenCodeOutputParser` with a bounded retry-on-malformed-output loop that returns `{outcome: ERROR}` with raw stdout rather than throwing when retries are exhausted; stderr hint detection via `CliFailureHintDetector.OpenCodeSignatures` (Network / Config / Model / Auth categories oriented at local-server failure modes, not OpenAI API failures). Sandbox image at `docker/opencode-sandbox/` bakes an entrypoint shell script that templates `~/.config/opencode/opencode.json` from env vars (`OPENCODE_PROVIDER_BASE_URL`, `OPENCODE_AUTH_TOKEN`, `OPENCODE_MODEL_NAME`) at container start. |
| DockerMountBuilderBase / DockerClaudeMountBuilder / DockerOpenCodeMountBuilder | Builds workspace/`.git` bind mounts (base, shared) and provider-specific extras: Claude builder adds a credential staging mount; OpenCode builder adds no extra mounts (connection details passed as env vars through `DockerMountContext.EnvironmentVariables` — no host credentials to stage because llama.cpp accepts a dummy token) |
| DockerMountContext | Disposable context: mount list, env vars (`GIT_OPTIONAL_LOCKS=0` plus provider-specific vars), path translation map, temp file cleanup |
| DockerAgentOptionsBase / DockerClaudeAgentOptions / DockerOpenCodeAgentOptions / DockerMount | Shared Docker-runtime config on the base (image, user, memory/CPU limits, network mode, reuse, additional mounts); Claude-specific config on its derived class (prompt mount, budget, credential path/mount); OpenCode-specific config on its derived class (`ProviderBaseUrl`, `AuthToken`, `ModelName`, `CliArguments`, `MaxRetriesOnMalformedOutput`, `RateLimitPatterns`) |
| OpenCodeOutputParser | Extracts Agent Contract JSON from OpenCode free-form stdout via three fallback strategies: ```json-fenced block → whole-document JSON → last balanced `{...}` region with an `outcome` field. Returns the raw flat JSON; `DockerOpenCodeAgentExecutor` wraps it as `{"structured_output": <json>}` before calling `AgentOutputParser.ParseResult` so the existing detail / questions / estimate extraction path is reused. |
| CodexAgentExecutor | OpenAI Codex CLI subprocess (secondary/legacy); rate-limit detection via `CliRateLimitDetector.CodexDefaultPatterns` ∪ operator `RateLimitPatterns`; configurable `EnvVarsToRemove` for subprocess env scrub; throws `RateLimitException(AgentCli)` on matching stderr for parity with Claude; stderr hint detection delegated to `CliFailureHintDetector.CodexSignatures` |
| CliRateLimitDetector | Shared stderr pattern matching for CLI executors; exposes `ClaudePatterns`, `CodexDefaultPatterns`, and a `Matches(stderr, patterns)` case-insensitive helper |
| CliFailureHintDetector | Shared stderr signature detection across CLI executors. Exposes `CodexSignatures` (Auth / Model / VersionDrift / Schema / Sandbox / Quota / Network) and `OpenCodeSignatures` (Network / Config / Model / Auth — oriented at local llama.cpp server failures, not OpenAI API failures). Returns a `FailureHint(Category, Hint)` that executors include in thrown exception messages. |
| ProcessRunnerDelegate | Optional delegate seam injected into each CLI executor constructor; defaults to `ProcessRunner.RunProcessAsync`; lets tests replay prerecorded process output via `AgentExecutorContractTests` |
| AgentExecutorResolver | Multi-executor registry; resolves by provider key (`claude-cli`, `docker-claude-cli`, `docker-opencode`, `codex`, `stub`) |
| GitWorkspaceManager | Git worktree lifecycle for isolated agent execution |
| ImageDownloader | Downloads card-referenced images to `.aiboard/images/{cardId}/`; authenticated via `gh auth token` for GitHub |
| TaskFileManager | Write board cards as `.aiboard/tasks/{id}.md` files + comments files |
| UpdateFileProcessor | Processes `.aiboard/updates/` files for child ticket creation and cross-card comments |
| CrossReferenceResolver | Parse card references, fetch dependent cards |
| ITenantIdentifier / TenantIdentifierFactory | Process-lifetime tenant identity (`{provider}:{id}`); fail-fast resolution from board provider config; injected into all per-tenant DB stores and Docker container naming |
| IRunStore / PgRunStore | Agent run and step result persistence (PostgreSQL); writes/reads scoped to `tenant_id`; NullRunStore for no-op |
| IMetricsStore / PgMetricsStore | Read-only analytical queries over agent_run + step_result; scoped to `tenant_id`; NullMetricsStore for no-op |
| MetricsRunner | CLI metrics mode: queries IMetricsStore and formats output for `--mode metrics` |
| SinceParser | Parses `--since` time strings (e.g., `7d`, `24h`, `1w`) into UTC DateTime offsets |
| PrerequisiteValidator | Startup validation of providers, board config, and prompt files |
| StartupConfigValidator | Pre-flight IConfiguration validation: flags unknown `BoardProvider`, incomplete GitHubProjects/Trello sections, provider-vs-section contradictions, unknown `AgentExecutor`; errors abort, warnings continue |
| ValidationRunner / IBoardShapeProbe / BoardShapeChecks | Read-only `--mode validation`: static + prompt-file + live board-shape cross-checks; `GitHubProjectShapeProbe` uses `gh project field-list` + `gh label list`; `NullBoardShapeProbe` returns null (Info, skip board checks); pure `BoardShapeChecks.Check` produces `ValidationFinding` records |
| CardSelector / CardFilterEvaluator | Polling card selection and filtering logic |
| WorkflowConfig.ResolveState | Centralized state resolution: matches card column + evaluates filters; replaces all direct `States.TryGetValue` lookups; throws on ambiguity |
| WorkflowConfig.GetEffectiveColumn | Returns state's effective column name (explicit `Column` property or state key fallback) |
| WorkflowConfig.FindStatesByColumn | Returns all states whose effective column matches a given name |
| WorkflowConfig.GetTerminalColumnNames | Returns distinct effective column names for all terminal states (use instead of `GetTerminalStateNames` for column comparisons) |
| ShutdownCoordinator | Thread-safe two-phase Ctrl+C shutdown: `IsShutdownRequested` flag + `IdleToken`; singleton shared by `Program.cs`, runners, and `AgentRunner` |
| SystemSleepInhibitor | Prevents OS sleep during polling (Windows/Mac/Linux) |
| PromptBuilder | Assembles system + task prompts for agent execution |
| workflow.github.json | Workflow config for GitHub Projects (active) |
| workflow.v1.json | Workflow config for Trello (legacy) |
| workflow.simple.example.json | Example config demonstrating shared-column multi-phase workflow (5 phases sharing "Ready" column, disambiguated by Activity field + assignee filters) |

## Workflow Configuration
**File-based** — `workflow.github.json` or `workflow.v1.json`, selected via `WORKFLOW_CONFIG_PATH` env var.

Schema:
```json
{
  "states": {
    "<state_key>": {
      "name": "<display_name>",
      "column": "<board_column_name>",
      "gateType": "agent_run | manual_gate | manual_entry | in_progress | holding | terminal | system_merge | children_complete",
      "gitBehavior": "discard | commit_only | commit_and_push",
      "providerParams": { "<key>": "<value>" },
      "includeInAgentContext": true,
      "pipelineOrder": 1,
      "steps": [
        { "name": "<step_name>", "role": "<role_key>", "taskPromptFile": "<path>",
          "generationConfig": { "targetType": "<cardType>", "targetColumn": "<state>", "linkToParent": true, "copyFields": ["<field>"] } }
      ],
      "gateCheck": {
        "role": "<role_key>",
        "taskPromptFile": "<path>"
      },
      "optionalSteps": [
        { "name": "<step_name>", "role": "<role_key>", "description": "...", "triggerCriteria": "...", "taskPromptFile": "<path>", "providerParams": {} }
      ],
      "transitions": {
        "IN_PROGRESS": "<column>",
        "COMPLETE": "<column> | [{ \"type\": \"moveToColumn\", \"value\": \"...\" }, { \"type\": \"setField\", \"field\": \"...\", \"value\": \"...\" }, { \"type\": \"updateParentSum\", \"field\": \"...\" }]",
        "NEEDS_INFO": "<column>",
        "ERROR": "<column>",
        "GATE_FAIL": "<column>",
        "MERGE_CONFLICT": "<column>"
      }
    }
  },
  "roles": {
    "<role_key>": {
      "model": "<model_id>",
      "systemPromptFile": "<path>",
      "sections": ["<section_name>"]
    }
  },
  "mergeResolution": { "role": "<role_key>", "providerParams": {} },
  "polling": { "priorityFieldName": "<field>", "priorityOrder": ["P0", "P1"] },
  "estimation": { "calibrationTicketId": "<id>", "calibrationSize": 1, "fieldName": "<field>", "scale": [1, 2, 4, 8] },
  "cardTypes": { "<type_key>": { "name": "<display>", "labelPrefix": "<prefix>", "allowedChildren": ["<type_key>"] } }
}
```

- `column`: optional board column name for the state; if omitted, falls back to the state key (backward compatible). Use when multiple states share a single board column — each state must then have `filters` to disambiguate. `WorkflowConfig.ResolveState(BoardCard)` uses `GetEffectiveColumn` + filter evaluation to resolve. Throws on ambiguity (multiple states pass filters for the same card).
- `steps` array replaces legacy top-level `role`/`taskPrompt` (auto-normalized on load)
- `taskPromptFile` takes precedence over `taskPrompt` (inline fallback preserved)
- `systemPromptFile` takes precedence over `systemPrompt`
- `providerParams` are state-level (shared across all steps)
- `sections` can be empty for roles that only produce comments (gate_checker, code_reviewer)
- `generationConfig` on a step configures child ticket creation: `targetType`, `targetColumn`, `linkToParent`, `copyFields`
- `copyFields` copies specified field values from parent card metadata to created children (e.g., `["priority"]`)
- `updateParentSum` transition action sums a field across all `sub_item` children and sets the result on the parent
- `completeParentIfReady` transition action checks if the card's parent has all children in terminal states and transitions the parent if so; posts a progress comment otherwise
- New ticket front matter supports `estimate:` field — value is set on the created card and summed for parent rollup
- State keys are column names for GitHub Projects or list IDs for Trello (unless `column` property is set)
- Validator enforces: if multiple states share a column and any has an actionable gate type (`agent_run`, `system_merge`, `children_complete`), all states on that column must have `filters`
- `--mode agent --state <stateId>` CLI parameter overrides filter-based resolution for direct card invocation

## Roles

| Role | Model | Purpose |
|------|-------|---------|
| `senior_engineer` | claude-opus-4-6 | Design and design review steps |
| `implementer` | claude-sonnet-4-6 | Code implementation (same system prompt as senior_engineer) |
| `code_reviewer` | claude-sonnet-4-6 | Post-implementation code review |
| `qa` | claude-opus-4-6 | Test validation |
| `gate_checker` | claude-haiku-4-5-20251001 | Lightweight gate checks after design, implementation, and test |
| `estimator` | claude-haiku-4-5-20251001 | Ticket estimation (design step 4, calibration-based) |
| `specialist_reviewer` | claude-sonnet-4-6 | On-demand specialist reviews requested by gate checks |
| `senior_specialist_reviewer` | claude-opus-4-6 | High-stakes specialist reviews (legal, compliance, privacy) |
| `merge_resolver` | claude-sonnet-4-6 | Merge conflict resolution |

## Prompt File Structure

| File | Used By |
|------|---------|
| `prompts/senior_engineer.md` | senior_engineer, implementer (system prompt) |
| `prompts/code_reviewer.md` | code_reviewer (system prompt) |
| `prompts/qa.md` | qa (system prompt) |
| `prompts/gate_checker.md` | gate_checker (system prompt) |
| `prompts/estimator.md` | estimator (system prompt) |
| `prompts/merge_resolver.md` | merge_resolver (system prompt) |
| `prompts/states/ready_for_design.md` | Design step 2 (create_design) |
| `prompts/states/steps/review_related_tickets.md` | Design step 1 |
| `prompts/states/steps/review_design_conflicts.md` | Design step 3 |
| `prompts/states/steps/estimate_ticket.md` | Design step 4 (estimation) |
| `prompts/states/steps/generate_children.md` | Child task generation |
| `prompts/states/ready_for_implementation.md` | Implementation step 1 (implement) |
| `prompts/states/steps/code_review.md` | Implementation step 2 |
| `prompts/states/ready_for_test.md` | Test step 1 (QA validation) |
| `prompts/states/steps/update_documentation.md` | Test step 2 (documentation update) |
| `prompts/gates/post_design.md` | Gate check after design |
| `prompts/gates/post_implementation.md` | Gate check after implementation |
| `prompts/gates/post_test.md` | Gate check after test |
| `prompts/optional-steps/design/*.md` | Optional design specialist reviews (12 files) |
| `prompts/optional-steps/impl/*.md` | Optional implementation specialist reviews (12 files) |
| `prompts/optional-steps/test/*.md` | Optional test specialist reviews (10 files) |

## Database Tables

All per-tenant tables carry `tenant_id TEXT NOT NULL` as the leading PK column (V15). Format: `{provider}:{identifier}` (e.g. `github:owner/repo/4`).

### agent_run (run tracking)
| Column | Description |
|--------|-------------|
| tenant_id | Tenant scope (PK with run_id) |
| run_id | TEXT (PK with tenant_id) |
| card_id | Card ID |
| state_name | Workflow state that triggered the run |
| started_at_utc | Run start time |
| completed_at_utc | Run end time (nullable) |
| outcome | COMPLETE / NEEDS_INFO / ERROR (nullable) |
| failure_reason | Structured failure category for ERROR outcomes: `RATE_LIMIT`, `AGENT_ERROR`, `INFRASTRUCTURE`, `TIMEOUT` (nullable; NULL for non-error outcomes) |
| estimate | Story point estimate captured from the estimation step (nullable) |
| session_startup_ms | Time (ms) to create and start the Docker container for a session-based run (nullable; NULL = no session) |

Indexes: `(tenant_id, card_id)`, `(tenant_id, card_id, state_name)`, `(tenant_id, started_at_utc)`.

### step_result (step tracking)
| Column | Description |
|--------|-------------|
| tenant_id | Tenant scope |
| id | UUID (PK) |
| run_id | FK component to agent_run (composite `(tenant_id, run_id)`) |
| step_name | Step name within the run; unique within `(tenant_id, run_id)` |
| role | Agent role executed |
| outcome | Step outcome |
| detail | Step output detail (nullable) |
| started_at_utc | Step start time |
| completed_at_utc | Step end time (nullable) |
| session_exec_ms | Time (ms) for this step's execution via `docker exec` (nullable; NULL = no session or non-Docker executor) |

### SQL Views (metrics, V16)
| View | Description |
|------|-------------|
| `v_run_metrics` | One row per completed run; projects `tenant_id`; derived `duration_seconds`, `is_complete`, `is_error`, `is_rate_limited`; `is_rate_limited` uses `failure_reason = 'RATE_LIMIT'` |
| `v_step_duration` | One row per completed step; projects `tenant_id`; `duration_seconds` derived |
| `v_card_metrics` | Per-`(tenant_id, card_id)` aggregates: cycle time, working time, waiting time |
| `v_card_rework` | `(tenant_id, card_id, state_name)` re-entered more than once; `WHERE outcome IS NOT NULL` to exclude in-progress runs |

`PgMetricsStore` queries always include `WHERE tenant_id = $1` so a single process only sees its own tenant's data.

### queue tables (PGMQ, legacy — NOT yet tenant-scoped)
| Table | Purpose |
|-------|---------|
| `pgmq.q_events` | Active event queue |
| `pgmq.a_events` | Archive table (completed/dead-lettered) |
| `pgmq.q_pings` | Active ping queue |

Queue mode is legacy/secondary; queue names are global. Multi-tenant queue partitioning is deferred (`ITenantIdentifier.ShortHash` is the intended suffix when addressed).

### processed_events (idempotency)
| Column | Description |
|--------|-------------|
| tenant_id | Tenant scope (PK with action_id) |
| action_id | Trello action ID (PK with tenant_id) |
| processed_at_utc | Timestamp |

### card_state
| Column | Description |
|--------|-------------|
| tenant_id | Tenant scope (PK with card_id) |
| card_id | Card ID (PK with tenant_id) |
| current_lock | Active run lock ID |
| claimed_at | Timestamp of current lock acquisition (nullable) |
| last_known_list | Last confirmed list ID |
| origin_list_id | Pre-Questions list (for return-path validation) |
| waiting_on_human | Boolean flag |

