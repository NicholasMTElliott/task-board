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
- Migration chain: V1 (processed_events) → V2 (pgmq_core) → V3 (events_queue) → V4 (card_state) → V5 (run_log) → V6 (run_log step_name) → V7 (pgmq_pings_queue) → V8 (card_state_claimed_at) → V9 (agent_run) → V10 (step_result) → V11 (drop_run_log) → V12 (metrics: estimate column, started_at indexes, SQL views) → V13 (session timing: session_startup_ms on agent_run, session_exec_ms on step_result) → V14 (failure_reason: failure_reason TEXT NULL + CHECK constraint on agent_run; v_run_metrics updated to use failure_reason = 'RATE_LIMIT')

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
- ✅ Structured failure classification: `FailureReason` enum (`RATE_LIMIT`, `AGENT_ERROR`, `INFRASTRUCTURE`, `TIMEOUT`) persisted to `agent_run.failure_reason`; replaces ILIKE string-matching in `v_run_metrics` and `PgMetricsStore`
- ✅ Rate limit detection and recovery (Claude CLI stderr + GitHub API 429)
- ✅ Child task generation (cardTypes, CompletionRunner, UpdateFileProcessor)
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
- ✅ Container reuse for multi-step runs (`IAgentExecutorSession` / `ISessionableAgentExecutor`); `DockerAgentOptions.ReuseContainer` (default: true); session spans steps + gate checks + specialist reviews; transparent fallback to per-step execution; orphaned container detection at startup via `PrerequisiteValidator`
- ✅ `DockerAgentExecutor` (`IAgentExecutor`, provider key `docker-claude-cli`): Claude CLI wrapped in `docker run -i --rm`; system prompt dir mounted read-only; exit code classification (Docker 125/126/127/137 vs Claude CLI 0–124); `CLAUDECODE` env var stripped; workspace/credential mounts via `DockerMountBuilder`; extensible `AdditionalMounts` for operator-supplied static mounts; auto-registered when Docker daemon detected; `AgentOutputParser.ParseStreamOutput` extracted as shared static for reuse by both `ClaudeAgentExecutor` and `DockerAgentExecutor`
- ✅ `DockerAgentOptions` DI registration and `AGENT_EXECUTOR=docker-claude-cli` selection: three-mode startup (`stub` / `docker-claude-cli` / auto-detect); docker-claude-cli mode registers executor under both `docker-claude-cli` and `claude-cli` keys for transparent substitution; fail-fast startup error when `docker-claude-cli` requested but Docker unavailable; full config model (`ContainerUser`, `MemoryLimit`, `CpuLimit`, `NetworkMode`, `CredentialPath`, `CredentialMountPoint`, `AdditionalMounts`) bound from `Docker` appsettings section
- ✅ Docker workspace and credential mounting: `DockerMountBuilder` produces 4 bind mounts (worktree RW at `/workspace`, base `.git` RO at `/repo/.git`, `.git` file override for container-internal gitdir path, credentials RO); `DockerMountContext` injects `GIT_OPTIONAL_LOCKS=0` and provides path translation; agents do NO git writes — always-RO `.git` enforces this at mount level; `NormalizeHostPath` handles Windows backslash paths
- ✅ Explicit container stop/kill on `DockerAgentExecutor` timeout or cancellation: `docker stop -t 30` (30s grace) then `docker rm -f` fallback; best-effort (never throws); `CancellationToken.None` for cleanup commands
- ✅ Image download authentication via `gh auth token` for GitHub user-attachment URLs

## Open Technical Decisions
- [ ] Webhook/event-driven triggers (currently manual CLI or polling)
- [ ] .NET Lambda deployment model (native AOT vs. managed runtime)
- [ ] IaC toolchain (Terraform vs. SAM vs. CDK)
