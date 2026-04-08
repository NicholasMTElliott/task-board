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

Agent executor selection: `AGENT_EXECUTOR` env var — `claude-cli`, `codex`, or `stub`. Multi-executor support via `AgentExecutorResolver`.

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
- Migration chain: V1 (processed_events) → V2 (pgmq_core) → V3 (events_queue) → V4 (card_state) → V5 (run_log) → V6 (run_log step_name) → V7 (pgmq_pings_queue) → V8 (card_state_claimed_at) → V9 (agent_run) → V10 (step_result) → V11 (drop_run_log) → V12 (metrics: estimate column, started_at indexes, SQL views)

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
- ✅ Rate limit detection and recovery (Claude CLI stderr + GitHub API 429)
- ✅ Child task generation (cardTypes, CompletionRunner, UpdateFileProcessor)
- ✅ Estimation pipeline (estimator role, calibration-based sizing)
- ✅ Multi-executor support (AgentExecutorResolver: claude-cli, codex, stub)
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

## Open Technical Decisions
- [ ] Webhook/event-driven triggers (currently manual CLI or polling)
- [ ] .NET Lambda deployment model (native AOT vs. managed runtime)
- [ ] IaC toolchain (Terraform vs. SAM vs. CDK)
