# Tech Context

## Technology Stack

### Edge / Ingestion
| Layer | Technology | Notes |
|-------|------------|-------|
| Webhook receiver | Cloudflare Worker (TypeScript) | Cost-first prototype ingress |
| Event queue | PGMQ on Neon Postgres | Confirmed via SQL-only install spike |
| Event queue (fallback) | Postgres `jobs` table | Contingency only |
| Event trigger | Lambda Function URL kick | Worker invokes `/drain` after enqueue |
| Event queue (future) | AWS SQS FIFO | Migration target for higher reliability |

### Compute
| Layer | Technology | Notes |
|-------|------------|-------|
| Orchestrator runtime | AWS Lambda | C#/.NET runtime |
| Language | C# | .NET 8+ |

### Storage
| Layer | Technology | Notes |
|-------|------------|-------|
| Metadata + queue + config | Neon Postgres (prototype) | Free-tier-first validation target |

### Workflow Config
| Layer | Technology | Notes |
|-------|------------|-------|
| GitHub Projects workflow | `workflow.github.json` (file in repo) | Active config; column names as state keys |
| Trello workflow (legacy) | `workflow.v1.json` (file in repo) | Trello list IDs as state keys |

Selection via `WORKFLOW_CONFIG_PATH` env var. Defaults to `workflow.v1.json` in output directory.

### AI
| Layer | Technology | Notes |
|-------|------------|-------|
| Agent executor (primary) | Claude CLI subprocess | Via `ClaudeAgentExecutor`; uses `--json-schema` for structured output |
| LLM provider (legacy) | OpenAI / Anthropic HTTP | Via `ILlmClient` implementations; used by legacy Orchestrator path |

Agent executor selection via `AGENT_EXECUTOR` env var: `claude-cli` or `stub`.

### External APIs
| API | Purpose |
|-----|---------|
| GitHub Projects v2 GraphQL | Read project items, move status, resolve field/option IDs |
| GitHub Issues REST (via `gh`) | Read/update issue body, post/edit comments |
| Trello REST API | Read card state, write description sections, post/update comments, move cards |
| Claude CLI | Agent inference via subprocess (structured output with `--json-schema`) |

### Board Provider
| Provider | Implementation | Selection |
|----------|---------------|-----------|
| GitHub Projects | `GitHubProjectsClient` (via `gh` CLI) | `BOARD_PROVIDER=github` |
| Trello | `TrelloClient` (HTTP) | `BOARD_PROVIDER=trello` |
| Stub | `StubTaskBoardClient` | `BOARD_PROVIDER=stub` (default) |

GitHub Projects requires: `gh` CLI authenticated with `project` + `repo` scopes.
Config via env vars: `GitHubProjects__Owner`, `GitHubProjects__Repo`, `GitHubProjects__ProjectNumber`.

## Key Technical Constraints
- Worker must return 200 to Trello within ~3 seconds; it should enqueue then return immediately
- Worker and Lambda share a secret for kick endpoint authorization (`x-internal-kick-secret`)
- PGMQ SQL-only install script must be applied in each target Neon environment
- .NET runtime should use Npgmq library for queue operations
- Kick model is at-least-once; idempotency by `actionId` remains mandatory
- Optional low-frequency sweeper may be needed if kick-only delivery proves insufficient
- OpenAI responses must be parsed and validated against the Agent Contract JSON schema before being applied
- Idempotency enforced via unique constraint on `processed_events.action_id` — checked before any agent invocation

## Development Environment
- IDE: Visual Studio Code
- OS: Windows 11
- Shell: PowerShell 7
- Runtime tools available locally: dotnet, node, docker, aws CLI, terraform, git, gh (GitHub CLI)

### Local Secret Loading Convention
- Single local source of truth: `/.env.local` (gitignored)
- Template file committed: `/.env.example`
- Sync/load helper: `scripts/local-secrets.ps1`
  - `-Action sync` loads env vars from `/.env.local` into process and generates `worker/.dev.vars`
  - `-Action run-dotnet -Mode one|wait|loop` is preferred for local processor execution
  - Optional guard: `-RunTimeoutSeconds <n>` terminates long-running local runs and exits with code `124`
- `NEON_DATABASE_URL` supports both:
  - Neon URI format (preferred for copy/paste from Neon): `postgresql://...?...`
  - Npgsql key/value format

### Migration Tooling Convention
- Flyway (Docker image `redgate/flyway`) is the lightweight migration runner for SQL-first schema changes.
- Migration scripts live in `db/migrations` and are applied in version order.
- `scripts/migrate.ps1` reads `/.env.local` (`NEON_DATABASE_URL`) and executes Flyway actions (`migrate`, `info`, `validate`) through Docker.
- `scripts/migrate.ps1` includes URI→JDBC conversion for Neon and explicitly concatenates query params to avoid PowerShell interpolation issues.

## Infrastructure-as-Code
- IaC tool: **TBD** (Terraform or AWS SAM/CDK — to be decided)

## Decided Architecture Items
- ✅ **Board abstraction:** `ITaskBoardClient` with GitHub Projects and Trello implementations
- ✅ **GitHub Projects integration:** Via `gh` CLI + GraphQL, validated end-to-end
- ✅ **Agent executor:** Claude CLI subprocess with `--json-schema` structured output
- ✅ **IN_PROGRESS transitions:** Cards move to "X-ing" column before agent starts work
- ✅ **Direct CLI mode:** `--mode agent --card-id N` bypasses queues/databases entirely
- ✅ **Workflow config:** file-based, per-provider (`workflow.github.json`, `workflow.v1.json`)
- ✅ **Git worktree isolation:** Agents work in isolated worktrees, main repo untouched
- ✅ **Queue backend:** PGMQ on Neon (confirmed, used in legacy queue path)
- ✅ **Re-trigger rule:** card moved back to "Ready for" state to re-trigger

## Open Technical Decisions
- [ ] Webhook/event-driven triggers (currently manual CLI invocation only)
- [ ] .NET Lambda deployment model (native AOT vs. standard managed runtime)
- [ ] IaC toolchain (Terraform vs. SAM vs. CDK)
- [ ] Model selection per role (currently claude-sonnet-4-6 for all)
- [ ] Max retry and dead-letter policy for queue backend
