# Deployment Guide

This guide walks through deploying the AI Kanban Agent Orchestrator from scratch. By the end you will have:

1. A **Neon PostgreSQL** database with PGMQ message queues
2. A **Cloudflare Worker** receiving webhooks from your board provider
3. A **local .NET agent** processing cards from your kanban board

The system supports two board providers: **GitHub Projects** and **Trello**. Pick the one you use and skip the other.

---

## Prerequisites

| Tool | Purpose | Install |
|------|---------|---------|
| .NET 10 SDK | Build and run the orchestrator | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| `gh` CLI | GitHub API access (GitHub provider only) | `winget install GitHub.cli` |
| `claude` CLI | AI agent execution | [claude.ai/code](https://claude.ai/code) |
| Node.js 18+ | Cloudflare Worker tooling | [nodejs.org](https://nodejs.org/) |
| `wrangler` CLI | Deploy Cloudflare Workers | `npm install -g wrangler` |
| Docker | Run Flyway database migrations | [docker.com](https://www.docker.com/) |
| PowerShell 7 | Run helper scripts | Pre-installed on Windows; `brew install powershell` on macOS |

---

## Step 1: Set Up Neon PostgreSQL

### 1.1 Create a Neon project

1. Sign up at [neon.tech](https://neon.tech/) (free tier: 0.5 GB storage, 100 compute-hours/month).
2. Create a new project. Note the **connection string** from the dashboard — it looks like:
   ```
   postgresql://neondb_owner:abc123@ep-cool-name-12345.us-east-2.aws.neon.tech/neondb?sslmode=require
   ```
3. Save this as `NEON_DATABASE_URL` — you will need it for both the worker and the local agent.

### 1.2 Create the local secrets file

Copy the example and fill in your Neon connection string:

```powershell
cp .env.example .env.local
```

Edit `.env.local` and set:
```
NEON_DATABASE_URL=postgresql://neondb_owner:abc123@ep-cool-name-12345.us-east-2.aws.neon.tech/neondb?sslmode=require
```

### 1.3 Run database migrations

Migrations install the PGMQ message queue extension and create all required tables.

```powershell
# Requires Docker running
./scripts/migrate.ps1 -Action migrate
```

Verify migrations applied:
```powershell
./scripts/migrate.ps1 -Action info
```

You should see V1 through V8 all marked as "Success".

**What gets created:**
- `pgmq.q_events` / `pgmq.q_pings` — message queues
- `card_state` — distributed card locking
- `run_log` — agent execution history
- `processed_events` — webhook idempotency

---

## Step 2: Deploy the Cloudflare Worker

The worker receives webhooks from your board provider and enqueues lightweight "ping" notifications to the Postgres queue.

### 2.1 Install dependencies

```bash
cd worker
npm install
```

### 2.2 Configure secrets

Secrets are stored securely in Cloudflare and never committed to git.

```bash
# Required: Neon connection string
wrangler secret put NEON_DATABASE_URL
# Paste your postgresql://... connection string

# Provider-specific (set one or both):
wrangler secret put TRELLO_API_SECRET      # For Trello webhooks
wrangler secret put GITHUB_WEBHOOK_SECRET  # For GitHub webhooks
```

### 2.3 Configure non-secret variables

Edit `worker/wrangler.toml`:

```toml
[vars]
TRELLO_WEBHOOK_CALLBACK_URL = "https://your-worker.your-subdomain.workers.dev/webhooks/trello"
```

The `TRELLO_WEBHOOK_CALLBACK_URL` must match the exact URL you register with Trello (including the path). If you are only using GitHub, you can leave this empty.

### 2.4 Deploy

```bash
cd worker
npm run deploy
```

Note the deployed URL (e.g., `https://task-board.your-subdomain.workers.dev`). You will need it to register webhooks.

### 2.5 Verify deployment

```bash
curl https://task-board.your-subdomain.workers.dev/health
# Expected: {"ok":true,"service":"task-board-webhook-worker"}
```

---

## Step 3: Configure Your Board Provider

Choose your provider and follow the corresponding guide:

- [GitHub Projects setup](#github-projects-setup)
- [Trello setup](#trello-setup)

---

## GitHub Projects Setup

### 3.1 Authenticate the GitHub CLI

```bash
gh auth login
```

When prompted, select scopes that include **repo** and **project** access.

Verify:
```bash
gh auth status
```

### 3.2 Create a GitHub Project

1. Go to your GitHub profile or organization and create a new **Project** (Projects v2).
2. Note the **project number** from the URL (e.g., `https://github.com/users/you/projects/1` → project number is `1`).
3. Add a **Status** field (single-select) with options matching your workflow config columns:
   - Backlog, Ready for Design, Designing, Design Questions, Designed, Ready for Implementation, Implementing, Implementation Questions, Ready for Test, Testing, Tested, Approved, Merging, Done, Error

### 3.3 Register the GitHub Webhook

1. Go to your **repository** Settings → Webhooks → Add webhook (or organization-level for org-owned projects).
2. Configure:
   - **Payload URL:** `https://task-board.your-subdomain.workers.dev/webhooks/github`
   - **Content type:** `application/json`
   - **Secret:** Generate a strong secret (use `openssl rand -base64 32`). This must match what you set as `GITHUB_WEBHOOK_SECRET` in Step 2.2.
   - **Events:** Select "Let me select individual events" and check **Projects v2 items**.
3. Click "Add webhook". GitHub sends a `ping` event — verify it returns 200 in the "Recent Deliveries" tab.

### 3.4 Configure the local agent

Edit `.env.local`:
```
BoardProvider=github
AgentExecutor=claude-cli
WorkflowConfigPath=./workflow.github.json

GitHubProjects__Owner=your-github-username
GitHubProjects__Repo=your-username/your-repo
GitHubProjects__ProjectNumber=1
```

Or create `appsettings.user.json` (gitignored):
```json
{
  "BoardProvider": "github",
  "AgentExecutor": "claude-cli",
  "WorkflowConfigPath": "./workflow.github.json",
  "GitHubProjects": {
    "Owner": "your-github-username",
    "Repo": "your-username/your-repo",
    "ProjectNumber": "1"
  }
}
```

---

## Trello Setup

### 3.1 Get Trello API credentials

1. Go to [trello.com/app-key](https://trello.com/app-key) and note your **API Key**.
2. Click "Token" on that page to generate an **API Token** with read/write access.
3. Generate a **webhook secret** for HMAC verification (use `openssl rand -base64 32`).

### 3.2 Register the Trello webhook

Trello webhooks are registered via API call. Replace the placeholders:

```bash
curl -X POST "https://api.trello.com/1/tokens/{YOUR_API_TOKEN}/webhooks" \
  -H "Content-Type: application/json" \
  -d '{
    "key": "{YOUR_API_KEY}",
    "callbackURL": "https://task-board.your-subdomain.workers.dev/webhooks/trello",
    "idModel": "{YOUR_BOARD_ID}",
    "description": "AI Kanban Agent Orchestrator"
  }'
```

- `{YOUR_BOARD_ID}` — Find this in the Trello board URL: `https://trello.com/b/{BOARD_ID}/board-name`.
- `callbackURL` must match `TRELLO_WEBHOOK_CALLBACK_URL` in `wrangler.toml` exactly.

Trello will send a HEAD request to verify the URL exists. The worker responds with 200.

### 3.3 Configure the local agent

Edit `.env.local`:
```
BoardProvider=trello
AgentExecutor=claude-cli
WorkflowConfigPath=./workflow.v1.json

Trello__ApiKey=your-api-key
Trello__ApiToken=your-api-token
```

---

## Step 4: Run the Local Agent

There are three execution modes. Choose based on your setup.

### Option A: Queue mode (recommended with webhooks)

Polls the Postgres queue for webhook pings, then fetches the board and processes all eligible cards concurrently.

```powershell
dotnet run --project lambda/src/TaskBoard.Worker -- \
  --mode queue \
  --board-id 1 \
  --workspace . \
  --neon-connection "postgresql://..." \
  --max-concurrent-agents 2
```

Or using the helper script with `.env.local`:
```powershell
$env:Pgmq__ConnectionString = "postgresql://..."
.\scripts\run_polling.ps1  # Update script to use --mode queue
```

### Option B: Polling mode (no webhooks needed)

Polls the board directly on a timer. Simpler but uses more API quota.

```powershell
.\scripts\run_polling.ps1
```

Or manually:
```powershell
dotnet run --project lambda/src/TaskBoard.Worker -- \
  --mode polling \
  --board-id 1 \
  --workspace . \
  --poll-interval 120
```

### Option C: Single card (debugging/testing)

Process one specific card:

```powershell
.\scripts\run_once.ps1 -CardId 3
```

Or manually:
```powershell
dotnet run --project lambda/src/TaskBoard.Worker -- \
  --mode agent \
  --card-id 3 \
  --board-id 1 \
  --workspace .
```

---

## Step 5: Verify End-to-End

1. Create an issue in your repository and add it to the project board.
2. Move it to **Ready for Design** (or the first agent-triggerable column).
3. If using **queue mode**: the webhook fires, the worker enqueues a ping, and the local agent picks it up within seconds.
4. If using **polling mode**: the agent picks it up within the poll interval (default 120s).
5. Watch the agent logs — you should see the card move to **Designing**, then to **Designed** when complete.

---

## Configuration Reference

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `BoardProvider` | Yes | `stub` | Board provider: `github`, `trello`, or `stub` |
| `AgentExecutor` | Yes | `stub` | Agent executor: `claude-cli` or `stub` |
| `WorkflowConfigPath` | Yes | — | Path to workflow JSON (e.g., `./workflow.github.json`) |
| `GitHubProjects__Owner` | GitHub | — | GitHub username or org |
| `GitHubProjects__Repo` | GitHub | — | Repository in `owner/repo` format |
| `GitHubProjects__ProjectNumber` | GitHub | — | Project number |
| `Trello__ApiKey` | Trello | — | Trello API key |
| `Trello__ApiToken` | Trello | — | Trello API token |
| `Pgmq__ConnectionString` | Queue mode | — | Neon PostgreSQL connection string |
| `Pgmq__MaxConcurrentAgents` | No | `0` | Max parallel agent runs (0 = unlimited) |
| `Pgmq__StaleClaimMinutes` | No | `30` | Timeout for abandoned card claims |
| `PollIntervalSeconds` | No | `120` | Base polling interval (polling mode) |

### CLI Arguments

All environment variables can be overridden with CLI arguments. Run `--help` for the full list:

```powershell
dotnet run --project lambda/src/TaskBoard.Worker -- --help
```

### Cloudflare Worker Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `NEON_DATABASE_URL` | Yes | Neon PostgreSQL connection string |
| `TRELLO_API_SECRET` | Trello | Trello webhook HMAC secret |
| `GITHUB_WEBHOOK_SECRET` | GitHub | GitHub webhook HMAC secret |

---

## Troubleshooting

**"gh exited with code 1" / rate limit errors**
- The agent includes adaptive backoff and retry. If persistent, check `gh api rate_limit`.
- Consider switching from polling mode to queue mode to reduce API calls.

**"No eligible cards found"**
- Verify the card is in an agent-triggerable column (e.g., "Ready for Design").
- Check that the Status field values in your GitHub Project match the state names in `workflow.github.json`.
- Run with `--board-provider stub` first to verify the orchestrator works without a real board.

**Webhook not arriving**
- Check the Cloudflare Worker logs: `wrangler tail` (streams live logs).
- Verify the webhook secret matches between your provider and `wrangler secret`.
- For GitHub: check Recent Deliveries in the webhook settings for error details.
- For Trello: run `./scripts/simulate-trello-webhook.ps1` against the local dev server.

**Database connection failures**
- Verify `NEON_DATABASE_URL` is correct and the database is active (Neon free tier auto-suspends after inactivity).
- Run `./scripts/migrate.ps1 -Action info` to test connectivity.
- Check that migrations V1-V8 are all applied.

**Agent process hangs or times out**
- Default agent timeout is 1800s (30 min). Check `ClaudeCli__TimeoutSeconds`.
- Ensure `claude` CLI is installed and authenticated: `claude --version`.
