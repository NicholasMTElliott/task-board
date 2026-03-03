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
| Workflow definition (POC) | `workflow.v1.json` (file in repo) | Loaded at Lambda startup; DB migration is a future step |

### AI
| Layer | Technology | Notes |
|-------|------------|-------|
| LLM provider | OpenAI | Model TBD per role (e.g., gpt-4.1) |

### External APIs
| API | Purpose |
|-----|---------|
| Trello REST API | Read card state, write description sections, post/update comments, move cards |
| OpenAI Chat Completions | Agent inference |

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
- Runtime tools available locally: dotnet, node, docker, aws CLI, terraform, git

### Local Secret Loading Convention
- Single local source of truth: `/.env.local` (gitignored)
- Template file committed: `/.env.example`
- Sync/load helper: `scripts/local-secrets.ps1`
  - `-Action sync` loads env vars from `/.env.local` into process and generates `worker/.dev.vars`
  - `-Action run-dotnet -DotnetArgs "--mode","one"` pattern is preferred in Windows PowerShell to avoid `--` argument-binding issues
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

## Decided Architecture Items (POC)
- ✅ **Prototype ingress:** Cloudflare Worker + Lambda Function URL kick
- ✅ **Queue backend:** PGMQ on Neon (SQL-only install confirmed)
- ✅ **Idempotency key:** Trello `actionId` (unique constraint on `processed_events` table)
- ✅ **Workflow config:** file-based `workflow.v1.json`
- ✅ **Re-trigger rule:** card moved back to prior agent state only
- ✅ **Agent output:** schema validation mandatory before any write applied
- ✅ **Processor runtime shape:** reusable core with one/wait/loop execution modes

## Open Technical Decisions
- [ ] .NET Lambda deployment model (native AOT vs. standard managed runtime)
- [ ] Whether low-frequency sweeper is required in addition to kick-only invocation
- [ ] IaC toolchain (Terraform vs. SAM vs. CDK)
- [ ] OpenAI model per role (all gpt-4.1, or mix with gpt-4o-mini for cheaper roles)
- [ ] Structured output strategy (JSON mode vs. response_format schema)
- [ ] Trello webhook auth model finalization in Worker
- [ ] Max retry and dead-letter policy for selected queue backend
