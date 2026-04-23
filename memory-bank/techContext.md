# Tech Context

## Technology Stack

### Core
| Layer | Technology | Notes |
|-------|------------|-------|
| Language | C# / .NET 10 | Orchestrator and worker |
| Agent executor (primary) | Claude CLI subprocess | Via `ClaudeAgentExecutor`; `--output-format stream-json` + `--json-schema` |
| Agent executor (secondary) | Codex CLI subprocess | Via `CodexAgentExecutor` (legacy/secondary) |
| Board provider (primary) | GitHub Projects v2 | Via `gh` CLI (GraphQL + REST) |
| Board provider (legacy) | Trello | REST API |
| Git isolation | Git worktrees | `GitWorkspaceManager` |
| Agent sandbox image | `docker/agent-sandbox/Dockerfile` | node:22-slim + Claude CLI (npm) + git + ripgrep + curl; non-root `agent` user; base image / UID/GID configurable via build args |
| Image download | `ImageDownloader` + named HttpClient | Bearer auth via `gh auth token` for GitHub provider |
| Workflow config | `workflow.github.json` | File-based, in repo |
| Example workflow config | `workflow.simple.example.json` | Shared-column multi-phase example (5 phases sharing "Ready" column, disambiguated by Activity field + assignee filters) |

### Edge / Ingestion (Legacy Queue Path)
| Layer | Technology | Notes |
|-------|------------|-------|
| Webhook receiver | Cloudflare Worker (TypeScript) | Cost-first prototype ingress |
| Event queue | PGMQ on Neon Postgres | SQL-only install |
| Compute | AWS Lambda | C#/.NET runtime |

### External APIs
| API | Purpose |
|-----|---------|
| GitHub Projects v2 GraphQL | Read project items, move status, resolve field/option IDs |
| GitHub Issues REST (via `gh`) | Read/update issue body, post/edit comments |
| Trello REST API | Read card state, write sections, post comments, move cards |
| Claude CLI | Agent inference via subprocess |

### Board Provider Selection
| Provider | Implementation | Env Var |
|----------|---------------|---------|
| GitHub Projects | `GitHubProjectsClient` | `BOARD_PROVIDER=github` |
| Trello | `TrelloClient` | `BOARD_PROVIDER=trello` |
| Stub | `StubTaskBoardClient` | `BOARD_PROVIDER=stub` (default) |

Agent executor selection: `AGENT_EXECUTOR` env var — `stub` (test/dev), `docker-claude-cli` (Docker mode with transparent `claude-cli` substitution + fail-fast if Docker unavailable), or unset (auto-detect production mode). Other values trigger a deprecation warning. Multi-executor support via `AgentExecutorResolver`.

## Development Environment
- IDE: Visual Studio Code
- OS: Windows 11
- Shell: PowerShell 7
- Runtime tools: dotnet, node, docker, aws CLI, terraform, git, gh (GitHub CLI)

### Local Execution
- Single secret source: `/.env.local` (gitignored), template: `/.env.example`
- Helper: `scripts/local-secrets.ps1` — loads env vars, generates `worker/.dev.vars`
- Run: `.\scripts\run_once.ps1 -CardId N` or `scripts/local-secrets.ps1 -Action run-dotnet -Mode one`

### GitHub Projects Config
Env vars: `GitHubProjects__Owner`, `GitHubProjects__Repo`, `GitHubProjects__ProjectNumber`.
Requires: `gh` CLI authenticated with `project` + `repo` scopes.

### Migration Tooling
- Flyway via Docker (`redgate/flyway`) for SQL-first schema migrations
- Scripts in `db/migrations`, applied via `scripts/migrate.ps1`
- Migration chain: V1 (processed_events) → V2 (pgmq_core) → V3 (events_queue) → V4 (card_state) → V5 (run_log) → V6 (run_log step_name) → V7 (pgmq_pings_queue) → V8 (card_state_claimed_at) → V9 (agent_run) → V10 (step_result) → V11 (drop_run_log) → V12 (metrics: estimate column, started_at indexes, SQL views) → V13 (session timing: session_startup_ms on agent_run, session_exec_ms on step_result) → V14 (failure_reason: failure_reason TEXT NULL + CHECK constraint on agent_run; v_run_metrics updated to use failure_reason = 'RATE_LIMIT') → V15 (multi-tenant: drop & recreate `agent_run`, `step_result`, `card_state`, `processed_events` with `tenant_id` as leading PK column, all indexes lead with `tenant_id`, no data preserved) → V16 (recreate `v_run_metrics`, `v_step_duration`, `v_card_metrics`, `v_card_rework` projecting `tenant_id`)

## Decided Architecture Items
- ✅ Board abstraction: `ITaskBoardClient` with GitHub Projects and Trello implementations
- ✅ GitHub Projects integration via `gh` CLI + GraphQL
- ✅ Agent executor: Claude CLI subprocess with `--json-schema` structured output
- ✅ Multi-step execution within states (sequential steps with per-step roles/models)
- ✅ Model selection per role (opus for design/review/QA, sonnet for implementation, haiku for gate checks)
- ✅ Gate checks after design, implementation, and test
- ✅ Optional specialist-reviewer steps (gate-triggered, per-state catalogs)
- ✅ Direct CLI mode + polling mode
- ✅ Git worktree isolation
- ✅ Cross-reference resolution between cards
- ✅ System merge (Approved → Done)
- ✅ Merge conflict detection and resolution
- ✅ Agent run tracking (IRunStore with PgRunStore/NullRunStore)
- ✅ Structured failure classification: `FailureReason` enum (`RATE_LIMIT`, `AGENT_ERROR`, `INFRASTRUCTURE`, `TIMEOUT`) persisted to `agent_run.failure_reason`; replaces ILIKE string-matching in `v_run_metrics` and `PgMetricsStore`. All four values are populated by `AgentRunner`: rate-limit in its dedicated catch block, the other three via `AgentRunner.ClassifyFailure(Exception)` (static pattern match: `TimeoutException` → TIMEOUT, `CliInfrastructureException` → INFRASTRUCTURE, everything else → AGENT_ERROR). Executors throw `CliInfrastructureException` for shell-level launch failures (exit 126 permission-denied, 127 command-not-found) and — for the Docker wrapper — for Docker daemon errors (125/126/127/137).
- ✅ Rate limit detection and recovery (Claude CLI stderr + GitHub API 429)
- ✅ Child task generation (cardTypes, CompletionRunner, UpdateFileProcessor)
- ✅ Field-based card-type discrimination + literal field-setting on generated children: `WorkflowConfig.CardTypeField` (optional global discriminator field); `CardTypeDefinition.LabelPrefix` nullable (opt out of labels); `GenerationConfig.SetFields` (Dict<string,string>) merged into `CreateCardRequest.FieldValues` after `CopyFields`, wins on key collision. `UpdateFileProcessor.IsTargetTypeAllowedAsync` checks the discriminator field first then falls back to labels. Validator's "labelPrefix non-empty" error replaced with a discriminator rule (type needs a non-empty `LabelPrefix` OR a global `CardTypeField`). `BoardShapeChecks` adds: `cardTypeField` exists + each `CardTypeDefinition.Name` is an accepted option; every `generationConfig.setFields` key is a real field and single-select values match an option (templated `{{…}}` skipped); label warning skipped when `LabelPrefix` is empty. Labels and fields may be used together.
- ✅ Estimation pipeline (estimator role, calibration-based sizing)
- ✅ Multi-executor support (AgentExecutorResolver: claude-cli, docker-claude-cli, codex, stub)
- ✅ Sleep inhibition during polling (Windows/Mac/Linux)
- ✅ Startup prerequisite validation (PrerequisiteValidator)
- ✅ Metrics reporting (`--mode metrics`): `IMetricsStore` / `PgMetricsStore`, SQL views, Grafana dashboard in docker-compose
- ✅ Connection string config key renamed from `Pgmq:ConnectionString` to `Database:ConnectionString`
- ✅ Estimate persisted to `agent_run.estimate` column (captured during design pipeline estimation step)
- ✅ Story-to-task decomposition (Ready for Tasking → Waiting for Tasks pipeline)
- ✅ Priority propagation from parent to child cards (GenerationConfig.CopyFields)
- ✅ Estimate rollup to parent card (updateParentSum transition action)
- ✅ Best-guess estimates on generated tasks (estimate front matter in new-*.md files)
- ✅ Card type labels (type:story, type:task, type:bug)
- ✅ Event-driven parent completion (completeParentIfReady transition action replaces polled children_complete for stories)
- ✅ GetCardAsync now includes project field metadata (priority, estimate) via gh project item-list
- ✅ Agent sandbox Docker image (`docker/agent-sandbox/Dockerfile`): node:22-slim base, Claude CLI via npm, git, ripgrep, curl, non-root `agent` user (UID 1000, configurable), `/workspace` mount target; build via `scripts/build-sandbox.ps1` or `docker compose --profile build up agent-sandbox`; image tag `aiboard-agent-sandbox:latest`
- ✅ Container reuse for multi-step runs (`IAgentExecutorSession` / `ISessionableAgentExecutor`); `DockerClaudeAgentOptions.ReuseContainer` (default: true); session spans steps + gate checks + specialist reviews; transparent fallback to per-step execution; orphaned container detection at startup via `PrerequisiteValidator`
- ✅ `DockerClaudeAgentExecutor` (`IAgentExecutor`, provider key `docker-claude-cli`): Claude CLI wrapped in `docker run -i --rm`; system prompt dir mounted read-only; exit code classification (Docker 125/126/127/137 vs Claude CLI 0–124); `CLAUDECODE` env var stripped; workspace/credential mounts via `DockerClaudeMountBuilder`; extensible `AdditionalMounts` for operator-supplied static mounts; auto-registered when Docker daemon detected; `AgentOutputParser.ParseStreamOutput` extracted as shared static for reuse by both `ClaudeAgentExecutor` and `DockerClaudeAgentExecutor`
- ✅ `DockerClaudeAgentOptions` DI registration and `AGENT_EXECUTOR=docker-claude-cli` selection: three-mode startup (`stub` / `docker-claude-cli` / auto-detect); docker-claude-cli mode registers executor under both `docker-claude-cli` and `claude-cli` keys for transparent substitution; fail-fast startup error when `docker-claude-cli` requested but Docker unavailable; full config model (`ContainerUser`, `MemoryLimit`, `CpuLimit`, `NetworkMode`, `CredentialPath`, `CredentialMountPoint`, `AdditionalMounts`) bound from `DockerAgents:Claude` appsettings section (legacy `Docker` section still honoured with a deprecation warning)
- ✅ Shared `DockerAgentOptionsBase` + `DockerMountBuilderBase`: Docker-runtime options (network, memory/CPU limits, reuse, additional mounts) and shared worktree/`.git` mount construction factored out of Claude-specific code so future `DockerCodexAgentExecutor` can inherit. Claude-specific fields (`PromptMountPoint`, `MaxBudgetUsd`, `CredentialPath`, `CredentialMountPoint`) and the Claude credential staging mount live on `DockerClaudeAgentOptions` / `DockerClaudeMountBuilder`.
- ✅ Docker workspace and credential mounting: `DockerClaudeMountBuilder` produces 4 bind mounts (worktree RW at `/workspace`, base `.git` RO at `/repo/.git`, `.git` file override for container-internal gitdir path, credentials RO); `DockerMountContext` injects `GIT_OPTIONAL_LOCKS=0` and provides path translation; agents do NO git writes — always-RO `.git` enforces this at mount level; `NormalizeHostPath` handles Windows backslash paths
- ✅ Explicit container stop/kill on `DockerClaudeAgentExecutor` timeout or cancellation: `docker stop -t 30` (30s grace) then `docker rm -f` fallback; best-effort (never throws); `CancellationToken.None` for cleanup commands
- ✅ Image download authentication via `gh auth token` for GitHub user-attachment URLs
- ✅ Orchestrator-owns-git-writes: all git write operations (commit, push) are performed by `AgentRunner.HandleGitBehaviorAsync` on the host after agent/container exit; agents must not run git write commands; Docker enforces via RO `.git` mount; all 7 system prompts enforce via "Git Policy" section; agents may use read-only git commands freely
- ✅ Two-phase graceful shutdown (`ShutdownCoordinator`): first Ctrl+C sets `IsShutdownRequested` flag and cancels `IdleToken` (interrupts idle delays); second Ctrl+C fires hard `CancellationToken` cancel; runner loops check flag before claiming new work; `AgentRunner` inter-step check commits+pushes partial work and restores card on shutdown; applies to polling and queue modes only
- ✅ Shared-column state disambiguation (`WorkflowState.Column` + `WorkflowConfig.ResolveState`): state keys decoupled from board column names; multiple states can share one column, disambiguated by `filters`; `GetEffectiveColumn`/`FindStatesByColumn`/`GetTerminalColumnNames` replace all direct `States.TryGetValue`/`GetTerminalStateNames` call sites (18 sites across 11 files); validator enforces filter presence on actionable shared-column states; `--state` CLI override for agent mode; `workflow.simple.example.json` demonstrates the pattern
- ✅ Multi-tenant DB partitioning (`ITenantIdentifier`): every per-tenant table (`agent_run`, `step_result`, `card_state`, `processed_events`) carries `tenant_id` as the leading PK column; `tenant_id` is also in every index and metrics view; format `{provider}:{identifier}` (e.g. `github:owner/repo/4`, `trello:boardId`, `stub:name`); resolved once per process at DI registration via `TenantIdentifierFactory`, fail-fast on missing required config (Owner/Repo/ProjectNumber for GitHub, BoardId for Trello); injected into `PgRunStore`, `PgMetricsStore`, `CardClaimService` (every INSERT carries it, every SELECT/UPDATE filters on it including run-id-keyed updates); `DockerClaudeAgentExecutor.BuildContainerName` and the `AgentRunner` session container name include `tenant.ShortHash` (8-char SHA256 prefix) so two tenants with overlapping numeric card IDs cannot collide on container names; PGMQ queues are NOT yet tenant-scoped (deferred — queue mode is legacy/secondary)
- ✅ Config-only invocation (`aiboard` with no CLI args): `CliDefinitions.ShouldShowHelp` only triggers on explicit `--help`/`-h`/`-?`; required values (Mode/CardId/BoardId/AgentWorkspacePath) resolve from merged configuration (`appsettings.json` < `.aiboard/appsettings.json` < `--config` < `appsettings.user.json` < env vars < CLI args); `LogMissingConfig` helper reports which config sources were probed (path + exists/missing) when a required value is absent
- ✅ `--mode validation`: read-only workflow-vs-board consistency check. Runs static config checks (`WorkflowConfigValidator` with `validatePolling: true`), prompt-file existence, and live board introspection via `IBoardShapeProbe` (`GitHubProjectShapeProbe` uses `gh project field-list` + `gh label list`; `NullBoardShapeProbe` for stub/Trello returns null → Info). Cross-checks: state effective columns exist on the board (with case-mismatch/near-miss hints); every `moveToColumn`/`setField`/`clearField`/`updateParentSum`/filter field exists; `polling.priorityFieldName` + `priorityOrder` values are real options; `estimation.fieldName` exists and its options cover `estimation.scale`; single-select `setField` values are accepted options (template tokens like `{{estimation}}` skipped); `cardTypes.<k>` labels exist on the repo (Warning, since `AddLabelAsync` creates them on demand). Findings grouped Error/Warning/Info; exit 1 on any Error. New static checks added to `WorkflowConfigValidator`: `gateType`/`gitBehavior` enum sets, `estimation.scale` strictly monotonic + `calibrationSize ∈ scale`, role `model` non-empty, optional-step name uniqueness across the whole config, `cardTypes.labelPrefix` non-empty.
- ✅ Pre-flight config validation (`StartupConfigValidator`): runs after config merge, before tenant resolution, via temporary pre-host logger. **Errors** (exit): unknown `BoardProvider`; `BoardProvider=github` missing any of Owner/Repo/ProjectNumber (all listed at once); `BoardProvider=trello` missing any of ApiKey/ApiToken/BoardId. **Warnings** (continue): `GitHubProjects` populated but provider≠github; `Trello` populated but provider≠trello; both sections populated (names the winner); unrecognized `AgentExecutor` value; **legacy `Docker` config section populated (fires whenever set, regardless of whether the new `DockerAgents:Claude` section is also present — operators should always be nudged to migrate); `CodexCli:MaxBudgetUsd` set (Codex CLI has no budget flag; silently ignored today, warning makes the misconfig visible)**. Chose NOT to flag missing `WorktreeBasePath`/`PollIntervalSeconds`/`ClaudeCli:ExecutablePath`/stub `Stub:TenantName` (all have sensible defaults). `TenantIdentifierFactory.Create` widened to list all missing keys in one exception instead of first-missing.
- ✅ Codex executor robustness parity with Claude: `CliRateLimitDetector` (shared stderr pattern matching) + `CodexCliLlmOptions.RateLimitPatterns` (operator-extensible) + `CodexCliLlmOptions.EnvVarsToRemove` (configurable subprocess env scrub, defaults empty — no hardcoded strip since Codex has no known self-invocation guard analogous to Claude's `CLAUDECODE`). `CodexAgentExecutor.IsRateLimited` throws `RateLimitException(AgentCli)` on non-zero-exit + matching stderr AND on exit-0 + empty-stdout + matching stderr, so rate-limited runs are restored to the trigger column instead of moving to Error.
- ✅ `ProcessRunnerDelegate` test seam: optional delegate on every CLI executor constructor (defaults to `ProcessRunner.RunProcessAsync` method group); `AgentExecutorContractTests` abstract base defines the scenario contract (COMPLETE / NEEDS_INFO / ERROR outcomes, non-zero-exit rate limit, empty-stdout rate limit, non-zero-exit generic error, empty-stdout generic error, TimeoutException propagation, OperationCanceledException propagation, stdin-piping regression guard); per-executor subclasses (Claude / Codex / DockerClaude) supply provider-specific stdout shapes. No production wiring affected — the default delegate is the real `ProcessRunner`.
- ✅ `WorkflowConfigValidator.Audit(config)` — soft non-fatal warnings distinct from the fatal `Validate()` pass. Program.cs invokes it after the workflow config loads and logs each entry at Warning severity. Current audits:
  - **Codex sandbox defaults** — warns when a state or optional step uses a role with `Provider="codex"` but none of `sandbox` / `yolo` / `fullAuto` is set in `providerParams` at the state or step level. The Codex CLI still runs (falling back to `CodexCliLlmOptions.FullAuto`), but the implicit sandbox choice is easy to miss on review. Audit covers main steps, optional steps (inheriting from state or overriding per-step), and legacy single-role states (pre-normalisation).

## Open Technical Decisions
- [ ] Webhook/event-driven triggers (currently manual CLI or polling)
- [ ] .NET Lambda deployment model (native AOT vs. managed runtime)
- [ ] IaC toolchain (Terraform vs. SAM vs. CDK)
