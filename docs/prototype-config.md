# Prototype Configuration

This prototype uses Cloudflare Worker ingestion, Neon Postgres queue storage, and Lambda Function URL kick/drain.

## Shared Environment Variables

| Variable | Used By | Purpose |
|----------|---------|---------|
| `TRELLO_WEBHOOK_SECRET` | Worker | Validates webhook authenticity header.
| `NEON_DATABASE_URL` | Worker, Lambda | Postgres connection string for queue/idempotency tables.
| `LAMBDA_KICK_URL` | Worker | Optional function URL for `POST /drain` kick. If omitted/blank, Worker skips kick.
| `INTERNAL_KICK_SECRET` | Worker, Lambda | Required only when `LAMBDA_KICK_URL` is configured.
| `PGMQ_QUEUE_NAME` | Worker, Lambda | Queue name for enqueue/read (default `events`).
| `QueueProcessing__DefaultBatchSize` | Lambda | Default claim batch size when `batchSize` is not provided to `/drain`.
| `QueueProcessing__DefaultVisibilityTimeoutSeconds` | Lambda | Default PGMQ visibility timeout used for reads.
| `QueueProcessing__LoopIdleDelaySeconds` | Lambda | Delay between loop iterations when no messages are claimed.

## Local Development Secrets (Single Source)

Use one gitignored file at repo root as the local source of truth:

- `/.env.local`

Quick start:

1. Copy template:

```bash
copy .env.example .env.local
```

2. Fill values in `.env.local`.
3. Sync local secrets:

```bash
pwsh -File scripts/local-secrets.ps1 -Action sync
```

This sync does two things:
- Exports variables into the current process for local .NET runs.
- Generates `worker/.dev.vars` for Wrangler local Worker execution.

Run local .NET processor with env loaded from `.env.local`:

```bash
./scripts/local-secrets.ps1 -Action run-dotnet -DotnetArgs "--mode","one"
./scripts/local-secrets.ps1 -Action run-dotnet -DotnetArgs "--mode","wait","--wait-seconds","30"
./scripts/local-secrets.ps1 -Action run-dotnet -DotnetArgs "--mode","loop"
```

`NEON_DATABASE_URL` supports both Npgsql key/value format and Neon URI format. A direct Neon copy/paste URI is valid:

```text
postgresql://<user>:<password>@<host>/<database>?sslmode=require&channel_binding=require
```

## Database Migrations (Flyway)

This repo now supports lightweight SQL-first migrations via Flyway using scripts in `db/migrations`.

Flyway runs via Docker (`redgate/flyway` image), so no local Flyway install is required.

Run migrations from local env:

```bash
./scripts/migrate.ps1 -Action migrate
```

Inspect migration status:

```bash
./scripts/migrate.ps1 -Action info
./scripts/migrate.ps1 -Action validate
```

Notes:
- `scripts/migrate.ps1` reads `NEON_DATABASE_URL` from `/.env.local` and converts Neon URI format to Flyway JDBC args.
- Prerequisite: Docker Desktop / Docker daemon running locally.
- Primary PGMQ path is tracked in Flyway migrations.
- `db/sql/0003_jobs_fallback.sql` remains manual contingency SQL and is intentionally not in the Flyway chain.

## Worker Setup

Use Wrangler secrets for sensitive values:

```bash
wrangler secret put TRELLO_WEBHOOK_SECRET
wrangler secret put NEON_DATABASE_URL
wrangler secret put INTERNAL_KICK_SECRET
```

Set non-secret vars in `worker/wrangler.toml`:

- `LAMBDA_KICK_URL` (optional for local-only queueing flow)
- `PGMQ_QUEUE_NAME` (optional, defaults to `events`)

Kick behavior:
- If `LAMBDA_KICK_URL` is blank or unset, Worker enqueues and returns success without attempting kick.
- If `LAMBDA_KICK_URL` is set, Worker attempts kick and logs failures, but still returns success to Trello (fail-open).

For local development, `worker/.dev.vars` is generated from `/.env.local` by `scripts/local-secrets.ps1`.

## Lambda Setup

Set environment variables on the Lambda function:

- `INTERNAL_KICK_SECRET`
- `NEON_DATABASE_URL`
- `PGMQ_QUEUE_NAME` (optional, defaults to `events`)
- `QueueProcessing__DefaultBatchSize` (optional, defaults to `10`)
- `QueueProcessing__DefaultVisibilityTimeoutSeconds` (optional, defaults to `30`)
- `QueueProcessing__LoopIdleDelaySeconds` (optional, defaults to `1`)

`POST /drain` accepts optional query params:

- `batchSize`
- `visibilityTimeoutSeconds`

If omitted (or invalid), Lambda uses `QueueProcessing` defaults from configuration.

For local development, use `scripts/local-secrets.ps1` so Lambda-compatible env vars come from `/.env.local`.

## Local Processor Execution

The .NET processor supports local execution modes:

```bash
dotnet run --project lambda/src/TaskBoard.Worker -- --mode one
dotnet run --project lambda/src/TaskBoard.Worker -- --mode wait --wait-seconds 30
dotnet run --project lambda/src/TaskBoard.Worker -- --mode loop
```

- `one` pulls and processes up to one message.
- `wait` polls up to the wait window for one message.
- `loop` runs continuously until stopped.

## Security Notes

- Use different secrets for Trello webhook validation and internal worker-to-lambda calls.
- Rotate both secrets if exposure is suspected.

## Troubleshooting

If local run fails with `schema "pgmq" does not exist`, bootstrap Neon for this environment before running the worker:

1. Run `./scripts/migrate.ps1 -Action migrate`.
2. If Docker is not running, start Docker and rerun migrations.