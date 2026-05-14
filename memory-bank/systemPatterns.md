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
- **Step-specific comments** — each step appends an `<!-- aiboard-log kind:step ... -->` comment on the card.
- **Run-level comment** — posted as `<!-- aiboard-log kind:run ... -->` when run lifecycle feedback is needed: merge kickback, git post-processing note, or unexpected run error.

If any step returns non-COMPLETE, the chain halts and the card transitions based on that outcome.

**Step-only state shape.** Valid `agent_run` states declare a non-empty `Steps` array. `WorkflowConfigValidator.Validate` rejects `state.Role`, `state.TaskPrompt`, `state.TaskPromptFile`, and any `agent_run` state without `Steps`. Legacy fields remain on `WorkflowState` only for JSON deserialisation. `WorkflowConfig.Normalised()` is a no-op pass-through kept for compatibility with existing callers.

**Deterministic re-run cache.** `RerunCacheGate.EvaluateAsync` runs before each step (single-agent and candidate-group). It hashes operator-authored description prefix, non-`aiboard-log` comments, prior steps' `section_output_hash` values, step config JSON (provider/model/candidates/slots/prompts), system prompt contents, and task prompt contents, then compares against the most recent `COMPLETE` canonical `step_result` for `(card, state, step)` (`step_name = step.Name AND candidate_group_id IS NULL`). On matching `input_hash` and `section_output_hash`, `AgentRunner` skips the LLM call, returns a synthetic `COMPLETE` `AgentResult` from cached `output_summary`, persists a cache-hit row, and appends a `kind:cache_hit` comment. Force re-run requires editing an operator-side input or managed section; deleting generated `aiboard-log` comments does not invalidate the cache because `TaskFileManager.ContainsAgentMarker` filters them out.

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

**GitHub body encoding invariant:** `GitHubProjectsClient` must not send issue/comment/PR body text through `gh --body ...` or stdin (`--input -`) on Windows. All body writes use UTF-8 no-BOM temp files (`--body-file` or `gh api --input <file>`). `ProcessRunner.ConfigureUtf8Io` sets redirected stdin/stdout/stderr encoding to UTF-8 and injects `PYTHONIOENCODING=utf-8`, `DOTNET_SYSTEM_CONSOLE_UTF8IO=1` for child tools. Rationale: Windows OEM code pages can transcode non-ASCII (`→`, `—`, `✓`, CJK) into mojibake when piped to `gh`, and repeated edits compound corruption.

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
- **Mode-bit changes via `.aiboard/git-mode-changes.txt` manifest:** executable-bit changes are declared as `+x path/from/repo/root` or `-x path/from/repo/root` entries. Blank lines and `#` comments are allowed. `GitWorkspaceManager.CommitAsync` reads the manifest after `git add .` and before `git commit`, applies `git update-index --chmod=+x|-x <path>`, logs per-entry failures, and continues. Validation rejects rooted paths and `..` segments. The manifest is under gitignored `.aiboard/`, applies to single-agent commits and per-candidate commits, and is a no-op for discard-mode states. This avoids relying on host filesystem mode bits on Windows/NTFS or Docker bind mounts.

### Cross-Reference Resolution
Task files can reference other cards (e.g., `#5`, `#12`). The `CrossReferenceResolver` parses these, fetches referenced cards, and includes them in the agent's workspace. This creates tracked relationships so dependent context flows through the pipeline.

### Blocking Dependencies
Provider-agnostic dependency surface:
- `ICardDependencyClient`: `GetBlockersAsync`, `GetBlockedCardsAsync`, `AddBlockedByAsync`, `RemoveBlockedByAsync`.
- `DependencyPolicy` config: `enabled` (default false), `enforcedStates` (no implicit default — empty/null is a no-op so operators must explicitly list the states/columns to gate), `satisfiedColumns` (default terminal columns), `commentOnBlocked`. `enforcedStates` matches against `state.Name` first and the card's column name as a fallback (column matches are coarser in shared-column workflows). Validator (`WorkflowConfigValidator`) accepts state names OR effective columns for both `enforcedStates` and `satisfiedColumns`.
- `DependencyGuard`: runs before IN_PROGRESS transition. Polling skips blocked cards and selects the next eligible card. Direct agent and merge runs return `NEEDS_INFO` without moving the card. Per-cycle hydration cache (`Dictionary<string, BoardCard>?` parameter on `CheckAsync`) — `PollingRunner` allocates one per cycle so two enforced cards sharing a blocker only fetch the blocker's card body once. Closed-and-completed blockers short-circuit hydration entirely (column lookup unnecessary). Blocked-comment marker: `<!-- aiboard-log kind:dependency_blocked ... -->`. Read-only callers (`DiagnoseRunner`) pass `recordSideEffects: false` to the full-control `CheckAsync` overload — the result is returned without writing to `card_dependency_wait`, posting the blocked-comment, or logging the blocking decision.
- Satisfaction: blocker column in `satisfiedColumns` OR provider reports closed-and-completed (closed-as-not_planned still blocks).
- Failure modes: transient lookup errors (auth, 5xx, network) are caught and treated as "not blocked" so the pipeline keeps moving. **Contract violations** (`DependencyApiContractException` — non-array root from `gh api`, non-JSON body) propagate up unswallowed so operators notice when the upstream API shape changes rather than silently treating every blocked card as clear.
- `GitHubIssueDependencyClient`: caches issue numeric→database id resolution in a `ConcurrentDictionary<string, long>` for the lifetime of the singleton (immutable mapping); avoids re-shelling `gh api` on every `AddBlockedByAsync`/`RemoveBlockedByAsync` for repeated blockers.
- `IDependencyWaitStore`: observational DB writes only; provider remains source of truth. `PgDependencyWaitStore` binds the resolution UPDATE's `text[]` parameter explicitly via `NpgsqlDbType.Array | NpgsqlDbType.Text` (Npgsql's inference from `string[]` is brittle).

Ticket creation dependency contract:
```yaml
blockedBy:
  - same-batch-slug
  - "#123"
  - current
blocks:
  - follow-up-slug
```
- `blockedBy`: created ticket waits for referenced cards.
- `blocks`: referenced cards wait for created ticket.
- Refs: same-batch `new-{slug}.md`, existing issue number, or `current` source card.
- `UpdateFileProcessor` parses all `new-*.md` files first (sorted alphabetically by filename for deterministic outcome across filesystems — NTFS happens to return name-ordered, ext4 with dir_index does not), creates cards, resolves slugs, rejects self-dependencies and **direct A↔B cycles only** (deeper cycles like A→B→C→A are the provider's responsibility — GitHub Issues rejects them itself), then applies links via `ICardDependencyClient`.

### Image Downloading
`ImageDownloader` downloads images referenced in card bodies (markdown `![](url)` and HTML `<img src>`) to `.aiboard/images/{cardId}/` in the worktree. Images are passed to the agent as local files via `TaskFileManager`.
- Named HttpClient `"ImageDownloader"` configured at startup with `User-Agent: TaskBoard-Worker/1.0`
- When `boardProvider == "github"`, a GitHub token is obtained via `gh auth token` at startup and set as a Bearer Authorization header (required for `github.com/user-attachments/assets/` URLs which return 404 without auth)
- Max 20 images per card, 10 MB per image
- Filenames: SHA256(url)[0:12] + extension (deterministic, deduplicating)
- Failures are logged and silently skipped — never block agent execution

### Agent Executor Pattern
Agent executors are registered via `AgentExecutorResolver` which resolves by provider key (`claude-cli`, `codex`, `stub`, `docker-claude-cli`, `docker-codex`, `docker-opencode`, `docker-claude-qwen`). `AGENT_EXECUTOR` env var has these modes:
- `stub` — all providers mapped to stub (testing/dev)
- `docker-claude-cli` — Docker executor registered under both `docker-claude-cli` and `claude-cli` keys; `claude-cli` workflow roles transparently route to Docker without config changes; fails fast at startup if Docker unavailable
- `docker-codex`, `docker-opencode`, `docker-claude-qwen` — fail-fast if Docker unavailable; distinct provider keys with no aliasing
- unset / any other value — production auto-detect mode: Docker-backed executors are registered when Docker is available; host CLIs are registered only when allowed by the unsafe gate; other values trigger a deprecation warning

**DockerClaudeAgentExecutor** (`IAgentExecutor`, provider key `docker-claude-cli`) wraps Claude CLI invocation inside `docker run -i --rm`. Key design:
- Standalone class (no inheritance from `ClaudeAgentExecutor`) — differences in process surface are too large
- Options bound to `DockerClaudeAgentOptions` (derives from `DockerAgentOptionsBase`); shared Docker-runtime settings (network/CPU/memory limits, reuse, host Docker socket mount, additional mounts) live on the base so Docker executors share them
- System prompt file translated: host directory mounted read-only at `DockerClaudeAgentOptions.PromptMountPoint` (`/mnt/aiboard/prompts`); container path computed from `Path.GetFileName`
- Container named `{prefix}-{tenantHash}-{cardId}-{random8}` (prefix: `aiboard-run`); random suffix prevents collisions, `--rm` cleans up on normal exit
- On timeout (`TimeoutException`) or cancellation (`OperationCanceledException`), `ExecuteAsync` issues explicit `docker stop -t 30` (30s SIGTERM grace, then SIGKILL) followed by `docker rm -f` fallback; cleanup is best-effort (never throws, never masks original exception); uses `CancellationToken.None` since caller token may already be cancelled
- Exit codes classified: Docker daemon errors (125/126/127/137) vs. Claude CLI errors (0–124) via `IsDockerExitCode`
- `CLAUDECODE` env var stripped from subprocess environment
- Rate-limit detection via `ClaudeAgentExecutor.IsRateLimited(stderr)` (same as host executor)
- NDJSON parsing via shared `AgentOutputParser.ParseStreamOutput`
- Workspace/credential mounts built by `DockerClaudeMountBuilder` (inherits from `DockerMountBuilderBase` for the shared worktree/`.git` mount construction; injected optionally); workspace mounts and env vars passed to `docker run` args and `SessionRequest`
- Host Docker socket passthrough (`MountHostDockerSocket=true`) adds writable `-v {HostDockerSocketPath}:{ContainerDockerSocketPath}` before `AdditionalMounts`; default both paths `/var/run/docker.sock`. Requires Docker CLI/Compose inside the image. Grants host-Docker control. If socket permissions block UID 1000, operator keeps `ContainerUser` unset and sets `GroupAdd` to the socket group id. `ContainerUser=root` is startup-invalid: Claude CLI rejects bypass-permissions under root/sudo; other CLIs can drift HOME/credential lookup to `/root`.
- Extensible static mounts (`DockerClaudeAgentOptions.AdditionalMounts` dictionary — inherited from the base) — operator-supplied overrides beyond the standard workspace/credential set
- Performance volume overlays (`DockerAgentOptionsBase.PerformanceVolumes`) — opt-in workspace-relative paths mounted as Docker named volumes after the `/workspace` bind mount. Intended for reproducible dependency/cache dirs (`node_modules`, `.pnpm-store`, `.gradle`, `target`, `.godot/imported`) so Docker Desktop Windows avoids slow host-bind small-file reads. Invalid paths (absolute, `..`, `.git`, root) are startup-invalid. Volume name = deterministic per `(worktreePath, relPath)` via SHA-256 prefix; label `aiboard-perf=1`; first init runs as root in the configured image and applies `PerformanceVolumeOwner` (default `agent:agent`) once via sentinel.
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
- `TimeoutSeconds` (int, default: `7200`) — hard wall-clock cap; `InactivityTimeoutSeconds` is the normal stuck detector
- `InactivityTimeoutSeconds` (int?, default: `1200`) — kill when no stdout/stderr has been observed for this many seconds; null disables inactivity checking
- `ContainerUser` (string, default: `""`) — user to run as inside container (empty = image default)
- `GroupAdd` (`List<string>`, default `[]`) — forwarded as repeated `docker run --group-add`; preferred Docker-socket permission fix for the non-root `agent` user
- `MemoryLimit` (string?, default: `null`) — optional memory limit, e.g. `"4g"`
- `CpuLimit` (string?, default: `null`) — optional CPU limit, e.g. `"2.0"`
- `NetworkMode` (string, default: `"host"`) — container network mode, forwarded as `--network` to `docker run`. `"host"` gives the sandbox access to host-published ports (e.g. local `docker-compose` Postgres/Grafana on `localhost`). Use a compose network name to reach support services by service name. Empty/null omits `--network`. `MemoryLimit`, `CpuLimit`, `ContainerUser`, and `GroupAdd` forward to `--memory`, `--cpus`, `--user`, and repeated `--group-add`.
- `MountHostDockerSocket` (bool, default: `false`) — when true, bind-mounts host Docker socket; paired paths `HostDockerSocketPath` / `ContainerDockerSocketPath` default `/var/run/docker.sock`
- `CredentialPath` (string, default: `""`) — host path to Claude CLI credentials; auto-detected from `~/.claude` if empty; used by `DockerClaudeMountBuilder`
- `CredentialMountPoint` (string?, default: `null`) — container path for credentials; defaults to `/home/agent/.claude` (matches `agent` user home in sandbox image)
- `AdditionalMounts` (Dictionary, default: `{}`) — operator-supplied static volume mounts (beyond standard workspace/credential set)
- `PerformanceVolumes` (List, default: `[]`) — Docker named-volume overlays for dependency/cache paths under `/workspace`; all Docker executors honour it.
- `PerformanceVolumeOwner` (String, default: `agent:agent`) — owner used during first volume init; empty skips chown.

**Fallback strategy:** If `TryCreateSessionAsync` returns `null` (image not found, Docker unavailable), the run proceeds without a session — all steps use `executor.ExecuteAsync` directly.

**Orphaned container detection:** `PrerequisiteValidator.DetectOrphanedContainersAsync` runs `docker ps --filter name=aiboard-` at startup, then `FilterOrphanContainerNames` narrows to agent-run prefixes: `aiboard-run-`, `aiboard-cdx-`, `aiboard-cq-`, and `aiboard-oc-`. Operator-managed support containers such as `aiboard-grafana` and `aiboard-postgres` are excluded. Maintainer rule: add each new Docker-backed executor `ContainerNamePrefix` to `OrphanRunPrefixes` in `PrerequisiteValidator.cs`. Cleanup is manual.

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
- **Stdout NDJSON detection.** Claude CLI `--output-format stream-json` can emit rate limits as stdout events shaped like `{"type":"rate_limit_event","rate_limit_info":{"status":"rejected",...}}`. `AgentOutputParser.HasRejectedRateLimitEvent(stdout)` walks NDJSON and returns true for any `rate_limit_event` whose `rate_limit_info.status` is not `"allowed"`. Matching is type-discriminated; malformed lines are skipped. `ClaudeAgentExecutor.IsRateLimitedStdout(stdout)` wraps the helper, and all Claude-family executors (`ClaudeAgentExecutor`, `DockerClaudeAgentExecutor`, `DockerClaudeQwenAgentExecutor`) check it on non-zero exits after Docker infra exit-code classification (125/126/127/137). Codex has no equivalent event shape.
- All three executors throw `RateLimitException(RateLimitSource.AgentCli)` on: non-zero exit + matching stderr, OR non-zero exit + matching stdout `rate_limit_event`, OR exit 0 + empty stdout + matching stderr. `GitHubProjectsClient` detects board-side rate limits (HTTP 429, "abuse detection", "secondary rate") and throws with `RateLimitSource.BoardApi`.
- `AgentRunner` catches `RateLimitException`, restores card to trigger column for retry.
- `PollingRunner` applies aggressive backoff: board API = 2min base, agent CLI = 30min base (cap 2hr).

### Codex Executor Defensive Diagnostics
`CodexAgentExecutor` is instrumented for loud failure and rich diagnostics. Silent text fallbacks are avoided.

- **Startup**: `CodexCliResolver.TryGetVersionAsync` runs `codex --version` (5s timeout, never throws). `Program.cs` logs the resolved path + version at Info; a warning is emitted if the probe fails, since unrecorded CLI versions make CLI-version-drift bugs hard to diagnose.
- **Per-invocation Info logs**: combined prompt size (chars + KB), output schema size + SHA256 prefix (stable correlator), effective policy (`yolo`, `fullAuto`, `sandbox` values **plus source**: `providerParams` or `config` or `(none)`), full CLI command line.
- **Per-invocation warnings**: oversize prompt (>200 KB), non-git workspace (`.git` not in ancestors), `yolo` set with `fullAuto`/`sandbox` (redundant — yolo suppresses both).
- **Stderr hint detection** (`DetectStderrFailureHint`) — substring matcher with an ordered signature table; categories: Auth (`OPENAI_API_KEY`, `not authenticated`, `invalid_api_key`, 401/403), Model (`model_not_found`, `unknown model`), VersionDrift (`unrecognized subcommand`, `unknown command`, `unexpected argument`), Schema (`schema validation`), Sandbox (`sandbox policy`), Quota (`insufficient_quota`), Network (`Failed to connect`, `timed out`). First match wins. Hint is logged at Error and also prepended to the thrown exception's `Message` as `[Hint] Category: …`.
- **Loud-failure policy — no structured_output**: when the NDJSON stream contains no `structured_output` event, the executor does NOT fall back to text-keyword scanning of raw stdout. It only accepts a backward-compat single-document JSON with `structured_output` at the top. Anything else throws `InvalidOperationException` with a diagnostic block (first 3 + last 3 stdout lines, line counts, stderr head, detected hint). This prevents the classic silent failure where "COMPLETE" appears in prompt-echo and the card is wrongly moved to Done.
- **Stream health warnings**: `ParseStreamOutput` tracks unknown top-level event types (anything outside `thread.started`, `turn.started|completed`, `item.started|completed|updated`, `response.started|completed`, `assistant`, `user`, `system`) and logs a single end-of-parse warning with counts — signals CLI-version drift. Also warns when ≥10% of non-empty NDJSON lines are malformed (min 2) and when any event with `"error"` in its type is seen.
- **Exception-message stderr cap:** thrown exception messages include up to 4000 stderr chars; Error logs include up to 10000 stderr chars.

### ProcessRunnerDelegate (test seam)
All three CLI executors (`ClaudeAgentExecutor`, `CodexAgentExecutor`, `DockerClaudeAgentExecutor`) accept an optional `ProcessRunnerDelegate` constructor parameter that defaults to the static `ProcessRunner.RunProcessAsync`. Tests pass a custom delegate to replay prerecorded `(exitCode, stdout, stderr)` without launching a real subprocess. `AgentExecutorContractTests` (abstract base in `lambda/tests/…/Clients/AgentExecutorContractTests.cs`) defines the shared scenario set every executor must satisfy; per-executor subclasses supply provider-specific stdout shapes (Claude/Docker-Claude use `{"type":"result","structured_output":{…}}`; Codex uses `{"type":"turn.completed","structured_output":{…}}`).

### Inactivity Timer
Per-process stuck detection is layered on top of the hard wall-clock cap. `ProcessRunner.RunProcessAsync` accepts optional `int? inactivityTimeoutSeconds`. When set, a background monitor tracks the most recent stdout/stderr line via `Interlocked.Exchange` and polls every `max(1, min(30, threshold/4))` seconds. If the gap exceeds the threshold while the process is running, it cancels a scoped linked token and converts the resulting `OperationCanceledException` into `InactivityTimeoutException : TimeoutException`. Existing Docker cleanup paths, `AgentRunner.ClassifyFailure`, and candidate retry-on-timeout handling consume it as `TimeoutException`. `InactivityTimeoutSeconds = null` disables the check. Defaults: `TimeoutSeconds=7200`, `InactivityTimeoutSeconds=1200` on `DockerAgentOptionsBase`, `ClaudeCliLlmOptions`, and `CodexCliLlmOptions`.

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
| ICardDependencyClient | Provider-agnostic blocking dependency abstraction |
| GitHubProjectsClient | GitHub Projects v2 via `gh` CLI (GraphQL + REST) |
| GitHubIssueDependencyClient | GitHub issue dependencies via `gh api` REST |
| TrelloClient | Trello REST API |
| AgentRunner | Direct agent execution: fetch cards → worktree → steps → post-process. Per step, `step.GetEffectiveSlots()` selects the single-agent path or ordered slot execution through `CandidateExecutor.ExecuteSlotAsync`. Slot loop: `Failed` advances to the next slot; `Won` and `NeedsInfo` short-circuit; if all slots fail, the last slot result is surfaced. Prior-step context includes only canonical rows, excluding `candidate_group_id`/`candidate_index`, `:cand-` step names, and `:evaluator` rows from `.aiboard/context/step-history.md` and gate `{StepHistory}`. |
| CandidateExecutor | Optional slot/candidate service. `ExecuteSlotAsync(SlotConfig, slotIndex, totalSlots, request, ct) → SlotResult` creates candidate branches/worktrees from canonical HEAD via `GitWorkspaceManager.CreateWorktreeFromStartPointAsync`, runs all candidates concurrently via one `Task.WhenAll`, preserves declaration-order `candidate_index`, persists per-candidate `step_result` rows (`candidate_group_id`, `candidate_index`, `provider`, `slot_index`), runs an evaluator row (`:evaluator`, with `:slot-N` infix for multi-slot, no `candidate_group_id`), parses `winner_index`/`scores`, updates evaluation rows, and promotes the winner. Per-candidate retries catch `RateLimitException`/`TimeoutException` according to `CandidateOverride.Retries` and `RetryOn` (default `[RATE_LIMIT, TIMEOUT]`) with 30s base, 2x exponential, 5min cap, and ±20% jitter; failed retries are not separate rows. Single-candidate slots may omit `Evaluator`; COMPLETE promotes, NEEDS_INFO returns NeedsInfo, ERROR returns Failed. Commit modes promote with `git reset --hard {winner-branch}`; discard mode uses `PromoteDiscardWinnerArtifacts` to mirror winner `.aiboard/{tasks,updates}/` into canonical without changing git state. Candidate worktrees and branches are best-effort cleaned after promotion. Commit-mode evaluator prompts use `git diff` against `slotStartCanonicalSha`; discard-mode prompts use `.aiboard/tasks/{cardId}.md` plus `.aiboard/updates/*.md`. Gate checks diff against `agent_run.state_entry_canonical_sha` or `runStartCanonicalSha`; the state-entry SHA advances when pre-step merge pulls `origin/<default-branch>` into the card branch so merged mainline progress is excluded. Candidate branch shape: `aiboard-cand/{cardId}-{groupShort}-{index}-{provider}`. |
| RerunCacheGate | Deterministic skip cache. `EvaluateAsync` builds input-bundle and managed-section hashes, looks up the most recent `COMPLETE` canonical `step_result` for `(card, state, step)`, and returns `IsHit=true` when both hashes match. AgentRunner skips the LLM call and emits `kind:cache_hit` on hit. Only operator-authored inputs and managed-section content can force a re-run. |
| AgentInitFileResolver | Static helper invoked at agent invocation sites. Provider→filename map: Claude → `CLAUDE.md`; OpenCode and Codex → `AGENTS.md`. If the expected init file is missing and a sibling provider file exists, mirrors via relative symlink and falls back to copy on `UnauthorizedAccessException`/`IOException`. Injection sites: `AgentRunner.ExecuteWithSessionAsync`, `CandidateExecutor.ExecuteSingleCandidateAsync`, `CandidateExecutor.RunEvaluatorAsync`. `EnsureInitFile` returns an `InitFileMirror?` token; call sites cleanup in `finally`. Cleanup removes symlinks and byte-identical copies only; edited copies are preserved with a warning. Pre-existing expected files opt out. |
| MergeRunner | Git merge + push for `system_merge` gate type |
| CompletionRunner | Polls child cards for `children_complete` gate type |
| PollingRunner | Automatic card pickup via priority-sorted polling |
| DependencyGuard | Enforces `dependencyPolicy` before agent/merge execution and during polling selection |
| ClaudeAgentExecutor | Claude CLI subprocess with `--json-schema` structured output |
| DockerClaudeAgentExecutor | Claude CLI inside `docker run -i --rm`; provider key `docker-claude-cli`; auto-registered when Docker daemon detected |
| DockerCodexAgentExecutor | OpenAI Codex CLI inside `docker run -i --rm --init`; provider key `docker-codex`; auto-registered when Docker daemon detected. Image `aiboard-codex-sandbox:latest` from `docker/codex-sandbox/Dockerfile`. Container isolation makes `Yolo=true` the default; `NetworkMode=host` provides outbound HTTPS to `api.openai.com`. Auth uses a per-run staged copy of host `~/.codex/`: allowlisted top-level credential files (`auth.json`, `config.toml`, `cap_sid`, `version.json`, `.personality_migration`) mount read-only under the agent-owned `/home/agent/.codex`; runtime state files (`models_cache.json`, `state_*.sqlite*`, `logs_*.sqlite*`, `sandbox.log`, `history.jsonl`) are neither copied nor mounted. Heavy subdirs (`sessions`, `log`, `screenshots`) are excluded; surviving subdirs are not mounted. `installation_id` is supplied via `CODEX_INSTALLATION_ID`, generated at `<UserProfile>/.aiboard/codex_installation_id` unless `DockerCodexAgentOptions.InstallationId` overrides it; the sandbox entrypoint writes it into agent-owned `~/.codex/installation_id`. Parser behavior is shared with host Codex via `CodexOutputParser` (agent_message fallback, stdout-over-exit recovery except infra exit codes 125/126/127/137, version-drift warnings). |
| DockerOpenCodeAgentExecutor | OpenCode CLI inside `docker run -i --rm` targeting a local OpenAI-compatible llama.cpp server; default `ProviderBaseUrl=http://llama-server:8080/v1`, network `llm-net`, provider key `docker-opencode`. Sandbox bakes Qwen virtual models `qwen3.6-35b-a3b` and `qwen3.6-35b-a3b-think` under `@ai-sdk/openai-compatible`; per-call `AgentExecutionContext.Model` becomes `OPENCODE_MODEL_NAME` in `opencode.json`; `small_model` is the `-think` variant. Inherits `TimeoutSeconds=7200` and `InactivityTimeoutSeconds=1200`. `OpenCodeOutputParser` handles prompt-engineered JSON with bounded retry-on-malformed-output; exhausted retries return `{outcome: ERROR}` with raw stdout. `CliFailureHintDetector.OpenCodeSignatures` covers Network / Config / Model / Auth / Path. Fatal hint categories (`Network`, `Auth`, `Config`, `Path`) throw `CliInfrastructureException` immediately; `Model` remains retryable. No-think structurer fallback is enabled by `DockerOpenCodeAgentOptions.EnableStructurer=true`: on first-attempt unparseable output, a one-shot `{ContainerNamePrefix}-struct-{tenantHash}-{cardId}-{rand}` container runs `StructurerModelName=qwen3.6-35b-a3b` with `StructurerTimeoutSeconds=180`; recovered results set `StructurerFallbackUsed=true` and add a conversation marker. Structurer failures fall through to the normal retry loop. |
| DockerClaudeQwenAgentExecutor | Third Qwen-target executor: Claude CLI inside `docker run -i --rm` redirected to the local llama.cpp proxy (default `http://llama-server:8080` — bare host:port, no `/v1` suffix; the Anthropic Messages adapter inside the CLI appends `/v1/messages` itself); provider key `docker-claude-qwen`; auto-registered when Docker detected. Reuses `aiboard-agent-sandbox:latest` (the existing Claude image) — the Qwen redirect is purely runtime env-var work. Same Claude CLI invocation as `DockerClaudeAgentExecutor` (`--json-schema`, NDJSON parsing via `AgentOutputParser.ParseStreamOutput`, exit-code classification, rate-limit detection) with the **headline benefit** that `--json-schema` is enforced server-side: the proxy translates the schema to a tool-call constraint that llama.cpp's `qwen3_coder` jinja template constrains during generation. Per-call model selection via `AgentExecutionContext.Model` → `ANTHROPIC_MODEL` env var, same per-role mechanism as `docker-opencode`. Stderr hint detection via `CliFailureHintDetector.ClaudeQwenSignatures` (Network / Model / Auth / Config / Schema categories with Anthropic-style hint texts). Lives alongside `docker-opencode` so candidate evaluation can A/B them on the same task. No session reuse implemented (each step spawns a fresh container — fine for the candidate-evaluation use case where worktree mounts differ per candidate anyway). |
| DockerMountBuilderBase / DockerClaudeMountBuilder / DockerCodexMountBuilder / DockerOpenCodeMountBuilder / DockerClaudeQwenMountBuilder | Builds shared workspace/`.git` bind mounts plus provider-specific extras. Claude copies host `~/.claude` to a per-run RW staging mount at `/home/agent/.claude`. Codex copies only `auth.json`, `config.toml`, `cap_sid`, `version.json`, and `.personality_migration` from `~/.codex`, mounts each file read-only under `/home/agent/.codex/{filename}`, excludes `sessions`/`log`/`screenshots`, never mutates the host directory, never mounts subdirs, and provides `installation_id` via `CODEX_INSTALLATION_ID` plus sandbox entrypoint write. OpenCode adds no credential mounts; connection is env-only. Claude→Qwen writes a synthetic `~/.claude` (onboarding settings + dummy credentials) and never mounts the operator's real Anthropic credentials. All builders inject provider env through `DockerMountContext.EnvironmentVariables`. |
| DockerMountContext | Disposable context: mount list, env vars (`GIT_OPTIONAL_LOCKS=0` plus provider-specific vars), path translation map, temp file cleanup |
| DockerAgentOptionsBase / DockerClaudeAgentOptions / DockerCodexAgentOptions / DockerOpenCodeAgentOptions / DockerClaudeQwenAgentOptions / DockerMount | Shared Docker-runtime config on the base (image, user, memory/CPU limits, network mode, reuse, additional mounts); Claude-specific config on its derived class (prompt mount, budget, credential path/mount); Codex-specific config on its derived class (`Yolo` defaulting to `true` since the container is the sandbox, `FullAuto`, `Sandbox`, `CredentialPath`/`CredentialMountPoint` for `~/.codex` staging, `RateLimitPatterns`, `EnvVarsToRemove`); OpenCode-specific config on its derived class (`ProviderBaseUrl` defaulting to `http://llama-server:8080/v1`, `AuthToken`, `ModelName` as the per-process default — overridable per-call via `AgentExecutionContext.Model`, `CliArguments`, `MaxRetriesOnMalformedOutput`, `RateLimitPatterns`); Claude→Qwen specific config on its derived class (`ProviderBaseUrl` defaulting to `http://llama-server:8080` — bare host:port, NO `/v1` suffix because Claude CLI's Anthropic Messages adapter appends `/v1/messages` itself, `AuthToken`, `ModelName`, `MaxBudgetUsd`, `DisableAttributionHeader`, `DisableNonessentialTraffic` — both default true to keep llama.cpp's prefix cache warm and prevent the CLI pinging api.anthropic.com for telemetry) |
| OpenCodeOutputParser | Extracts Agent Contract JSON from OpenCode free-form stdout via three fallback strategies: ```json-fenced block → whole-document JSON → last balanced `{...}` region with an `outcome` field. Returns the raw flat JSON; `DockerOpenCodeAgentExecutor` wraps it as `{"structured_output": <json>}` before calling `AgentOutputParser.ParseResult` so the existing detail / questions / estimate extraction path is reused. |
| CodexAgentExecutor | OpenAI Codex CLI subprocess (secondary/legacy); rate-limit detection via `CliRateLimitDetector.CodexDefaultPatterns` ∪ operator `RateLimitPatterns`; configurable `EnvVarsToRemove` for subprocess env scrub; throws `RateLimitException(AgentCli)` on matching stderr for parity with Claude; stderr hint detection delegated to `CliFailureHintDetector.CodexSignatures` |
| CliRateLimitDetector | Shared stderr pattern matching for CLI executors; exposes `ClaudePatterns`, `CodexDefaultPatterns`, and a `Matches(stderr, patterns)` case-insensitive helper |
| CliFailureHintDetector | Shared stderr signature detection across CLI executors. Exposes `CodexSignatures` (Auth / Model / VersionDrift / Schema / Sandbox / Quota / Network) and `OpenCodeSignatures` (Network / Config / Model / Auth — oriented at local llama.cpp server failures, not OpenAI API failures). Returns a `FailureHint(Category, Hint)` that executors include in thrown exception messages. |
| ProcessRunnerDelegate | Optional delegate seam injected into each CLI executor constructor; defaults to `ProcessRunner.RunProcessAsync`; lets tests replay prerecorded process output via `AgentExecutorContractTests` |
| AgentExecutorResolver | Multi-executor registry; resolves by provider key (`claude-cli`, `docker-claude-cli`, `docker-codex`, `docker-opencode`, `docker-claude-qwen`, `codex`, `stub`). **Host CLI keys (`claude-cli`, `codex`) only register when the `--unsafe` flag is set**; otherwise they're filtered out at startup and any workflow referencing them fails fast with a migration message. |
| CodexOutputParser | Static class shared by `CodexAgentExecutor` (host) and `DockerCodexAgentExecutor` (container) for NDJSON parsing of `codex exec --json` stream output. Methods: `Parse(stdout, logger) → (resultJson, conversationLog)`, `TryRecoverStructuredOutput`, `TryParseSingleDocumentStructured`, `TryWrapAgentMessageAsStructured`, `BuildNoStructuredOutputDiagnostic`. Supports Codex CLI 0.125.0+ `agent_message` fallback (outcome JSON in final `item.completed` text), version-drift warnings, malformed-line ratio warnings, and error-event sample collection. Host executor instance methods delegate to the shared parser. |
| GitWorkspaceManager | Git worktree lifecycle for isolated agent execution |
| ImageDownloader | Downloads card-referenced images to `.aiboard/images/{cardId}/`; authenticated via `gh auth token` for GitHub |
| TaskFileManager | Write board cards as `.aiboard/tasks/{id}.md` files + comments files. Comments file filters `aiboard-log kind:candidate` + `kind:evaluator`; downstream agents see operator comments + canonical agent history only, never loser/evaluator audit output. |
| UpdateFileProcessor | Processes `.aiboard/updates/` files for child ticket creation, dependency linking, and cross-card comments |
| CrossReferenceResolver | Parse card references, fetch dependent cards |
| ITenantIdentifier / TenantIdentifierFactory | Process-lifetime tenant identity (`{provider}:{id}`); fail-fast resolution from board provider config; injected into all per-tenant DB stores and Docker container naming |
| IRunStore / PgRunStore | Agent run and step result persistence (PostgreSQL); writes/reads scoped to `tenant_id`; NullRunStore for no-op |
| IMetricsStore / PgMetricsStore | Read-only analytical queries over agent_run + step_result; scoped to `tenant_id`; NullMetricsStore for no-op |
| MetricsRunner | CLI metrics mode: queries IMetricsStore and formats output for `--mode metrics` |
| SinceParser | Parses `--since` time strings (e.g., `7d`, `24h`, `1w`) into UTC DateTime offsets |
| PrerequisiteValidator | Startup validation of providers, board config, and prompt files |
| StartupConfigValidator | Pre-flight IConfiguration validation: flags unknown `BoardProvider`, incomplete GitHubProjects/Trello sections, provider-vs-section contradictions, unknown `AgentExecutor`; errors abort, warnings continue |
| ValidationRunner / IBoardShapeProbe / BoardShapeChecks | Read-only `--mode validation`: static + prompt-file + live board-shape cross-checks; `GitHubProjectShapeProbe` uses `gh project field-list` + `gh label list`; `NullBoardShapeProbe` returns null (Info, skip board checks); pure `BoardShapeChecks.Check` produces `ValidationFinding` records |
| InitRunner | `--mode init`: scaffolds `./.aiboard/` from a bundled template under `{exeDir}/templates/<name>/`. Substitutes `__OWNER__` / `__OWNER_SLASH_REPO__` / `__PROJECT_NUMBER__` placeholders from CLI flags / env / prompts. Auto-detects via `gh repo view` when interactive. `--force` overwrites template files but **preserves operator-added files** (e.g. `appsettings.user.json` for local secrets). Bails out before host build (no DI, no Docker probe, no DB). Three bundled templates `from-scratch-{claude,codex,opencode}` — shared-column example (single Ready + Activity + assignee-isEmpty lock), single-candidate per step, provider/model tiered per role. |
| InstallRunner | `--install` (bare boolean flag): copies bundled Claude Code skill(s) from `{exeDir}/skills/<name>/` into `{userHome}/.claude/skills/<name>/`. Skill = any subdirectory under `{exeDir}/skills/` containing a `SKILL.md`; top-level files (`skills/README.md`) ignored. Per-file overwrite (idempotent re-install upgrades in place); operator-added files at the destination are preserved. Cross-platform home via `Environment.SpecialFolder.UserProfile`. Wired in `Program.cs` BEFORE the init bail-out so combined invocation `aiboard --install --mode init` installs the skill then scaffolds; standalone `aiboard --install` returns when no Mode resolves from merged config. Test seams: `getExeDir`, `getUserHome`, `stdout` (mirroring InitRunner's pattern). Returns 0 on success, 1 on missing source dir / unresolvable home / IO failure; warns + returns 0 when source skills/ dir is empty. |
| DiagnoseRunner | `--mode diagnose --card-id N`: read-only "why isn't this card being picked up?" triage. Fetches the card, classifies the column, then either describes the resolved state's gateType (ELIGIBLE / BLOCKED BY DEPENDENCIES / HOLDING / DONE / ENTRY / IN PROGRESS / manual gate / ambiguous) or walks each candidate state's filter chain. Per-state listing shows the first failing filter (ordered assignee→field→label by operator-pitfall frequency); a single "Most likely cause" headline picks the highest-priority failure across all states. **Routing uses structured `FilterFailure(CardFilter, string)` records** matched on `Filter.Type`/`Filter.Operator` — never substring-match against description text. Headline branch (assignee-isEmpty filter fails AND card is assigned) prints the exact `gh issue edit N --remove-assignee @user` command — assignment-as-WIP-lock is the highest-frequency operator pitfall on shared-column example workflows. **Dependency-blocked branch** (when a `DependencyGuard` is constructor-injected and the resolved state's gateType is actionable): consults the guard with `recordSideEffects: false` so the diagnose call is fully read-only (no `card_dependency_wait` writes, no blocked-comment upsert, no Info log), and on a blocked verdict surfaces `BLOCKED BY DEPENDENCIES` with the unresolved-blocker list and the exact `gh api -X DELETE …` command for removing the link. Closes the gap where a card with unresolved blockers but no other filter failures would diagnose as ELIGIBLE while polling silently skipped it. Returns 0 on successful diagnosis, 1 only on board-fetch failure. |
| BoardScaffoldDiff / BoardScaffoldPlan / IBoardShapeApplier / GitHubProjectShapeApplier / NullBoardShapeApplier / ScaffoldBoardRunner | `--mode scaffold-board --board-id N [--apply]`: dry-run-by-default board provisioner. Pure `BoardScaffoldDiff.Compute(WorkflowConfig, BoardShape) → BoardScaffoldPlan` walks every requirement source (filters, all transition action types incl. **symmetric `addLabel`+`removeLabel`** since `gh issue edit --remove-label` fails on unknown labels, polling priority field+order, estimation field+scale, cardTypeField+type names, cardTypes labels) and diffs against the introspected shape — template values `{{...}}` skipped. `IBoardShapeApplier` is the write companion to `IBoardShapeProbe`: `GitHubProjectShapeApplier` runs `gh project field-create --data-type SINGLE_SELECT` and `gh label create --repo`; `NullBoardShapeApplier` returns `CanApply=false` for stub/Trello so the runner short-circuits before writes. **Idempotency**: stderr matched against `"already exists"` / `"name has already been taken"` returns `ApplyOutcome.AlreadyExists` (`=` marker). Bare `"validation failed"` is intentionally NOT matched (would silently swallow real failures). **Process-startup robustness**: gh missing from PATH (Win32Exception/FileNotFoundException) is caught and surfaced as Failed; `OperationCanceledException` propagates for graceful shutdown. **Comma-in-option guard**: option names with `,` are rejected locally before shelling out. Plan-then-apply UX mirrors `terraform plan`/`apply`. v1 surface limited to **creating** new fields/labels; adding options to existing fields and Status columns are WARN items (one click in the GitHub UI; GHv2 GraphQL for option mutation is brittle). |
| CardSelector / CardFilterEvaluator | Polling card selection and filtering logic |
| WorkflowConfig.ResolveState | Centralized state resolution: matches card column + evaluates filters; replaces all direct `States.TryGetValue` lookups; throws on ambiguity |
| WorkflowConfig.GetEffectiveColumn | Returns state's effective column name (explicit `Column` property or state key fallback) |
| WorkflowConfig.FindStatesByColumn | Returns all states whose effective column matches a given name |
| WorkflowConfig.GetTerminalColumnNames | Returns distinct effective column names for all terminal states (use instead of `GetTerminalStateNames` for column comparisons) |
| AgentIdentity | Singleton process-lifetime record `(FamilyName, MachineName)`. **Family name** is random per process session — all agents in one polling run share the same family name. **Given name** is deterministically derived from `provider:model` via SHA-256 (big-endian 4-byte modulo 200-entry pool) so the same provider+model always resolves to the same given name across any session or machine. `ResolveGivenName(provider, model)` is a static lookup. `FormatAgentName(provider, model)` returns `"GivenName FamilyName on MachineName"`; falls back to `DisplayName` (`"FamilyName on MachineName"`) when provider is null. Registered as DI singleton in `Program.cs`; `agent_run.agent_identity` stores `DisplayName`. Step-level comments use the full `FormatAgentName` output; session-level contexts (rate-limit, error, merge, completion) use `DisplayName`. `CandidateExecutor` accepts optional `AgentIdentity?` for per-candidate comment naming. |
| ShutdownCoordinator | Thread-safe two-phase Ctrl+C shutdown: `IsShutdownRequested` flag + `IdleToken`; singleton shared by `Program.cs`, runners, and `AgentRunner` |
| IResourcePool / ResourcePool | Named-resource concurrency caps. Operators declare resources (e.g. `local-llm`) with `MaxConcurrent` slots and tag providers that need them. `AcquireAsync(providerKey, ct)` returns an `IAsyncDisposable` lease holding a `SemaphoreSlim` slot per declared resource, sorted alphabetically to prevent deadlock and released in reverse order. Providers without declared resources get a no-op lease. Wrapped at `AgentRunner.ExecuteWithSessionAsync`, `CandidateExecutor.ExecuteCandidateWithRetriesAsync`, and `CandidateExecutor.RunEvaluatorAsync`. Projects that run both `docker-opencode` and `docker-claude-qwen` against the same local llama.cpp server should map both providers to a `local-llm` pool with `MaxConcurrent=1`. Empty config registers no pools and every acquire is a no-op. |
| SystemSleepInhibitor | Prevents OS sleep during polling (Windows/Mac/Linux) |
| PromptBuilder | Assembles system + task prompts for agent execution |
| workflow.github.json | Workflow config for GitHub Projects (active) |
| workflow.v1.json | Workflow config for Trello (legacy) |
| workflow.simple.example.json | Example config demonstrating shared-column multi-phase workflow (5 phases sharing "Ready" column, disambiguated by Activity field + assignee filters). Ships at install root via csproj `<Content Include>`. |
| workflow.story-decomposition.example.json | Example config demonstrating end-to-end parent-story → child-task decomposition: `Ready for Tasking` (story-only, label-filtered) with `generate_tasks` step + `generationConfig`, `Waiting for Tasks` holding state, `updateParentSum` on design COMPLETE for estimate roll-up, `completeParentIfReady` on terminal Done for event-driven parent advance. Reference for remote-Claude workflow design — diff against existing `workflow.json` and merge the decomposition machinery. Ships at install root. |

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
  "dependencyPolicy": { "enabled": false, "enforcedStates": ["Ready for Implementation"], "satisfiedColumns": ["Done"], "commentOnBlocked": true },
  "cardTypes": { "<type_key>": { "name": "<display>", "labelPrefix": "<prefix>", "allowedChildren": ["<type_key>"] } }
}
```

- `column`: optional board column name for the state; if omitted, falls back to the state key (backward compatible). Use when multiple states share a single board column — each state must then have `filters` to disambiguate. `WorkflowConfig.ResolveState(BoardCard)` uses `GetEffectiveColumn` + filter evaluation to resolve. Throws on ambiguity (multiple states pass filters for the same card).
- `steps` array is required for `agent_run` states; legacy top-level `role`/`taskPrompt` fields are startup-invalid.
- `taskPromptFile` takes precedence over `taskPrompt` (inline fallback preserved)
- `systemPromptFile` takes precedence over `systemPrompt`
- `providerParams` are state-level (shared across all steps)
- `sections` can be empty for roles that only produce comments (gate_checker, code_reviewer)
- `generationConfig` on a step configures child ticket creation: `targetType`, `targetColumn`, `linkToParent`, `copyFields`
- `copyFields` copies specified field values from parent card metadata to created children (e.g., `["priority"]`)
- `updateParentSum` transition action sums a field across all `sub_item` children and sets the result on the parent
- `completeParentIfReady` transition action checks if the card's parent has all children in terminal states and transitions the parent if so; posts a progress comment otherwise
- New ticket front matter supports `estimate:` field — value is set on the created card and summed for parent rollup
- New ticket front matter supports `blockedBy:` and `blocks:` lists — dependencies are linked after same-batch slugs resolve
- `dependencyPolicy` controls pickup/merge gating for dependency links; default disabled
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

### Role-Level Fallback

A `WorkflowRole` may declare an ordered `Fallbacks` chain — list of `RoleFallback(Provider, Model?, ProviderParams?)` — that the runtime walks when the role's primary provider throws an exception classified to a `FailureReason` in the role's `FallbackOn` set. Default `FallbackOn` = `[RATE_LIMIT, AGENT_ERROR, INFRASTRUCTURE, TIMEOUT]`: every non-cancellation executor failure category before a valid `AgentResult` exists. An in-band `AgentResult{Outcome=ERROR}` is not an exception and never triggers role fallback; it follows workflow transitions. Cancellation (`OperationCanceledException`) is never a fallback trigger. Operators may set `FallbackOn` to a narrower exception-category subset.

Wired into the **single-agent invocation sites only**: gate check, optional reviewer, simple-step main path (no slots/candidates), and the evaluator role inside `CandidateExecutor`. **NOT wired** into `CandidateExecutor.ExecuteSingleCandidateAsync` — candidates already have per-candidate `Retries` / `RetryOn` and slot-level chain mechanisms, and adding role-level fallback there would overlap.

`AgentRunner.ExecuteWithSessionAndRoleFallbackAsync` walks the chain: attempt 0 uses the configured session if provider matches, attempts 1+ pass `session=null` so the existing `ExecuteWithSessionAsync` falls through to direct execution (sessions are tied to the primary provider's image; a fallback can't reuse them). The init-mirror, resource-pool lease, and provider-mismatch logic inside `ExecuteWithSessionAsync` all run unchanged per attempt. The wrapper returns `(AgentResult, SessionExecMs, ActualProvider, ActualModel)` so callers persist the fallback's identity to `step_result.provider/model` rather than the role's primary — this is what makes a future `v_role_fallback_rate` view possible from data already on disk.

`CandidateExecutor.RunEvaluatorAsync` uses an analogous loop without sessions (the evaluator never had session reuse). The evaluator schema (`AgentSchemas.EvaluatorOutcomeSchema` vs the OpenAI variant) is recomputed per attempt via the new `SchemaForProvider(providerKey)` helper so a Claude → Codex fallback chain swaps the schema correctly. If the loop ends without setting a result (e.g. the final attempt threw a `RateLimitException` not in the catch's allow-list), the wrapper rethrows so the outer rate-limit handler restores the card to the trigger column.

A fallback's `Model` defaults to the role's primary `Model` when omitted, and its `ProviderParams` overlay (rather than replace) the caller's primary params on key collision. Cross-provider fallbacks should pin a model that the fallback's executor accepts (Codex doesn't accept `claude-opus-4-6`); the existing `AuditCrossProviderCandidateModels` warning surfaces this case for candidates, and operators are expected to follow the same guidance for fallbacks.

Validator (`WorkflowConfigValidator.ValidateRoleFallback`) errors on: empty fallback provider; unknown fallback provider (cross-checked against `StartupConfigValidator.KnownAgentExecutors`); `FallbackOn` set without any `Fallbacks` declared. `AGENT_ERROR` is allowed in `FallbackOn` because it means an executor-thrown exception category, not an in-band agent verdict. `WorkflowConfig.GetRequiredProviders` and `GetAllReferencedProviders` both include fallback providers, so `--mode validation`'s Docker-image probe pass tests fallback images even when no role uses the fallback as its primary.

Schema (additions to `WorkflowRole` JSON):
```json
"roles": {
  "gate_checker": {
    "provider": "docker-claude-cli",
    "model": "claude-haiku-4-5-20251001",
    "systemPromptFile": "prompts/gate_checker.md",
    "fallbacks": [
      { "provider": "docker-codex", "model": "gpt-5.4-mini" }
    ],
    "fallbackOn": ["RATE_LIMIT", "AGENT_ERROR", "INFRASTRUCTURE", "TIMEOUT"]
  }
}
```

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

All per-tenant tables carry `tenant_id TEXT NOT NULL` as the leading PK column. Format: `{provider}:{identifier}` (e.g. `github:owner/repo/4`).

### agent_run (run tracking)
| Column | Description |
|--------|-------------|
| tenant_id | Tenant scope (PK with run_id) |
| run_id | TEXT (PK with tenant_id) |
| card_id | Card ID |
| agent_identity | Session display name (`"FamilyName on MachineName"`) at the time the run started; TEXT, display-only |
| state_name | Workflow state that triggered the run |
| started_at_utc | Run start time |
| completed_at_utc | Run end time (nullable) |
| outcome | COMPLETE / NEEDS_INFO / ERROR (nullable) |
| failure_reason | Structured failure category for ERROR outcomes: `RATE_LIMIT`, `AGENT_ERROR`, `INFRASTRUCTURE`, `TIMEOUT` (nullable; NULL for non-error outcomes) |
| estimate | Story point estimate captured from the estimation step (nullable) |
| session_startup_ms | Time (ms) to create and start the Docker container for a session-based run (nullable; NULL = no session) |
| rate_limit_events | INT NOT NULL DEFAULT 0; incremented by `IRunStore.IncrementRateLimitEventsAsync` whenever `AgentRunner` catches a `RateLimitException` (V22) |

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
| cost_usd | NUMERIC(10,6) NULL — total USD cost for this step's LLM call (Claude only; Codex / local-LLM stay null) |
| input_tokens / output_tokens | BIGINT NULL — populated from `usage.input_tokens` / `usage.output_tokens`; local-LLM rows include Qwen token data |
| cache_read_tokens / cache_creation_tokens | BIGINT NULL — Claude-specific cache token counts; null for other providers |
| fast_path_hit | BOOLEAN NULL — deprecated compatibility column; no longer written. Cache decisions use `execution_kind` (`cache_hit` / `full_run`) |
| execution_kind | TEXT NOT NULL DEFAULT 'full_run' — `cache_hit` when deterministic cache reused a prior step_result; `full_run` otherwise. Read by `v_cache_hit_rate` |
| output_summary | TEXT NULL — short narrative captured at step completion; reused as synthetic detail on cache hit |
| input_hash | TEXT NULL — SHA-256 of operator-side input bundle (description prefix, non-aiboard-log comments, prior section hashes, step config, prompts) |
| section_output_hash | TEXT NULL — SHA-256 of the step's managed-section content after the agent ran |
| section_update_json | JSONB NULL — raw `section_update` directive returned by the agent for replay/debugging |
| source_run_id / source_step_result_id | TEXT NULL / UUID NULL — back-pointers from cache-hit rows to the source row whose result was reused |
| structurer_fallback_used | BOOLEAN NULL — true on DockerOpenCode rows where the no-think structurer recovered the result from prose narrative |
| evaluator_prompt_chars | INT NULL — char length of evaluator prompt body on `:evaluator` rows |
| winner_regressed | BOOLEAN NULL — true on selected candidate winners when the same run's gate check returned ERROR/GATE_FAIL; updated by `IRunStore.FlagWinnersRegressedForRunAsync` |

### SQL Views (metrics)
| View | Description |
|------|-------------|
| `v_run_metrics` | One row per completed run; projects `tenant_id`; derived `duration_seconds`, `is_complete`, `is_error`, `is_rate_limited`; `is_rate_limited` uses `failure_reason = 'RATE_LIMIT'` |
| `v_step_duration` | One row per completed step; projects `tenant_id`; `duration_seconds` derived |
| `v_card_metrics` | Per-`(tenant_id, card_id)` aggregates: cycle time, working time, waiting time |
| `v_card_rework` | `(tenant_id, card_id, state_name)` re-entered more than once; `WHERE outcome IS NOT NULL` to exclude in-progress runs |
| `v_provider_role_metrics` | Aggregates per-`(role, provider)` candidate runs: wins, win rate, avg quality score, avg duration, cost/token/cache aggregates, and structurer fallback rate |
| `v_evaluator_reliability` | Per-`(evaluator-role, evaluator-provider)` regression rate. LEFT JOIN of evaluator rows to selected candidate siblings on `(run_id, state_name)`; `regression_rate_percent` = share where the winner has `winner_regressed = true` |
| `v_cache_hit_rate` | Per-`(state, step, role, provider)` deterministic-cache hit rate from `execution_kind in ('full_run','cache_hit')`; low rate on frequently re-run steps indicates input-bundle drift |
| `v_slot_outcomes` | Per-`(state, step, slot)` aggregate of slot wins/failures for slot-order tuning |

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

### card_dependency_wait (observational)
| Column | Description |
|--------|-------------|
| tenant_id | Tenant scope |
| card_id | Blocked card |
| blocker_card_id | Blocking card |
| source | guard source (`polling`, `agent`, `merge`) |
| first_seen_at_utc / last_seen_at_utc | Wait observation timestamps |
| resolved_at_utc | Set when blocker no longer unresolved |
| last_blocker_title / last_blocker_column | Latest blocker display data |

