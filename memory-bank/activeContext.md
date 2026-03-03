# Active Context

## Current Focus
**Webhook runtime implementation.** We confirmed PGMQ on Neon and started implementing a reusable Npgmq-based processor runtime.

## What Has Been Done
- README authored defining architecture, principles, and high-level design
- Memory bank initialized and architecture confirmed
- v1 product scope confirmed:
  - Full pipeline: Backlog → Requirements → Requirements Review → Design → Design Review → Implementation → Code Review → Testing → Done
  - BA, Senior Engineer, QA roles
  - Manual gates: Requirements Review, Design Review, Code Review
  - Single operator, single board
- **Architecture pivot approved for prototype phase:**
  - Cloudflare Worker webhook ingress
  - Neon Postgres queue + idempotency store
  - Lambda Function URL kick/drain
  - Reusable .NET processor core with local CLI + Lambda adapter
- **Scaffolding implemented:**
  - `worker/` TypeScript Cloudflare Worker project with `/webhooks/trello` + `/health`
  - `lambda/src/TaskBoard.Worker/` .NET drain API with `/drain` + `/health`
  - DB SQL scripts for `processed_events`, PGMQ SQL-only install, and fallback `jobs` queue
  - Prototype docs for config and spike execution
- **Queue decision resolved:**
  - PGMQ-on-Neon spike succeeded using SQL-only install script (`0002_pgmq_create.sql`)
  - Fallback jobs table remains contingency only
- **Runtime implementation in progress:**
  - Added `Npgmq` dependency to .NET worker
  - Added Npgsql data source + Npgmq client DI wiring
  - Implemented `EventProcessor` with three execution modes:
    1. Process one message
    2. Process one message or wait for configurable duration
    3. Run continuous processing loop
  - HTTP `/drain` handler now uses shared processor core
  - Idempotency repository now performs real insert-once semantics in Postgres
  - Local execution modes enabled via `--mode one|wait|loop`
- **Local secret workflow standardized:**
  - Added root `.env.example` template and `.gitignore` rules for local secret files
  - Added `scripts/local-secrets.ps1` to load env vars from `/.env.local` and generate `worker/.dev.vars`
  - Updated prototype docs with single-source local secret workflow and run commands
  - Confirmed Neon connection string can be stored as full URL (protocol + credentials + query params)
- **Neon URI compatibility implemented in .NET worker:**
  - `NEON_DATABASE_URL` now accepts either Neon URI format (`postgresql://...`) or Npgsql key/value format
  - Added runtime normalization in `Program.cs` to parse URI credentials/query params into `NpgsqlConnectionStringBuilder`
  - Updated docs to use PowerShell-safe `-DotnetArgs` syntax for `local-secrets.ps1`
- **QueueRepository reflection removed for AOT/trimming safety:**
  - Replaced reflective `NpgmqClient` method invocation with strongly-typed calls (`ReadBatchAsync<string>`, `DeleteAsync`, `ArchiveAsync`)
  - Eliminated runtime generic `MakeGenericMethod` dependency that failed on constrained `ReadBatchAsync<T>`
  - Local run now progresses to DB bootstrap validation and reports missing `pgmq` schema when SQL install has not been applied
- **Flyway migration workflow finalized for local usage:**
  - Added Flyway migration chain under `db/migrations` (`V1` processed events, `V2` pgmq core, `V3` events queue)
  - `scripts/migrate.ps1` now runs Flyway via Docker image (`redgate/flyway`) so no local Flyway install is required
  - Fixed PowerShell JDBC URL concatenation bug in `scripts/migrate.ps1` that previously produced invalid Flyway URL parsing
  - Executed migrations successfully against Neon; `public.flyway_schema_history` now tracks applied versions
- **Worker enqueue path implemented:**
  - Cloudflare Worker now enqueues webhook events into PGMQ on Neon using `pgmq.send(...)` before Lambda kick
  - Queue payload now includes `actionId`, `cardId`, `receivedAtUtc`, and raw webhook payload
  - Worker supports optional `PGMQ_QUEUE_NAME` env var (defaults to `events`)
  - Added `@neondatabase/serverless` dependency to Worker package
  - Updated prototype config docs to reflect Worker + Lambda shared queue name variable
- **Worker kick behavior updated for local/prod parity:**
  - Lambda kick is now optional in Worker flow
  - If `LAMBDA_KICK_URL` is unset/blank, Worker skips kick and logs `kickStatus: "skipped"`
  - If `LAMBDA_KICK_URL` is set, Worker requires `INTERNAL_KICK_SECRET`, attempts kick, and logs success/failure
  - Kick failures are fail-open: Worker still returns 200 after successful enqueue while logging `kickStatus: "failed"`
- **Configurable queue read defaults implemented in .NET worker:**
  - Added `QueueProcessing` options binding with defaults for batch size, visibility timeout, and loop idle delay
  - `/drain` now accepts optional `batchSize` and `visibilityTimeoutSeconds`; invalid/missing values fall back to configured defaults
  - Processor `one`/`loop` paths now consume configured defaults instead of hard-coded constants
  - Added corresponding env/config docs and example env keys

## Next Steps (Priority Order)
1. Run duplicate, concurrency, and idle wake tests and record outcomes
2. Validate local run/test flow using `./scripts/migrate.ps1 -Action migrate` then `./scripts/local-secrets.ps1 -Action run-dotnet -DotnetArgs "--mode","one"`
3. Reassess need for low-frequency sweeper trigger now that local/manual processing flow is first-class

## Active Decisions Under Consideration
- Lambda deployment model (AOT vs. managed runtime) not decided
- IaC toolchain not yet selected (Terraform vs. SAM vs. CDK)
- Whether sweeper trigger is needed beyond kick-only flow

## Known Risks
- Trello webhook delivery is not guaranteed exactly-once; idempotency is critical
- Cross-cloud operation (Cloudflare + Neon + AWS) adds auth/observability complexity
- Lambda + Neon cold start/resume latency may stack after idle
- Kick endpoint failures can delay processing unless sweeper fallback exists
- Local runs can fail if `/.env.local` is missing or not synced before Worker/.NET execution
