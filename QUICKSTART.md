# AI Board — Quick Start

## Install

Download the latest release for your platform from the [Releases page](https://github.com/NicholasMTElliott/task-board/releases/latest).

Each release ships **two flavors per platform** — pick whichever suits your machine:

| Asset name | When to pick it | Size | Requires |
|---|---|---|---|
| `aiboard-{platform}.zip` / `.tar.gz` | **Default — pick this if you're not sure.** Self-contained single-file binary; runs without any .NET install. | ~33 MB compressed | Nothing |
| `aiboard-{platform}-fdd.zip` / `.tar.gz` | Smaller download, faster cold start. For users who already have .NET 10. | ~5–10 MB compressed | [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on `PATH` |

`{platform}` is one of `win-x64`, `linux-x64`, `osx-x64`, or `osx-arm64`.

Extract anywhere; the resulting directory is the **install directory** (referenced as such throughout these docs). Add it to `PATH` if you want to run `aiboard` without a full path.

## Prerequisites

- **Docker** — installed and running (for local PostgreSQL via `docker compose`)
- **gh CLI** — installed and authenticated (`gh auth login` with `project` + `repo` scopes)
- **claude CLI** — installed and authenticated (`claude` must be on PATH)
- **Git** — installed and on PATH

## Database Setup (one-time)

Start the local PostgreSQL database and run migrations:

```
docker compose up -d
```

This launches Postgres on `localhost:5432` and automatically runs Flyway migrations.
The default connection string in `appsettings.json` points to this local instance — no extra config needed.

To check migration status: `docker compose run --rm migrate info`
To reset the database: `docker compose down -v && docker compose up -d`

## Setup

aiboard config layers from two places: the **install directory** (next to `aiboard.exe` — for personal secrets that span every project) and the **project's own `.aiboard/` folder** (for per-project board IDs and workflow shape, committed alongside the project's code). Project-local files **override** the install-directory ones.

### Per-project setup (do this in every project that uses aiboard)

In the project repo's root, create `.aiboard/appsettings.json`:

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

Then copy `workflow.github.example.json` (from the aiboard install directory) into `.aiboard/workflow.github.json` and customise as needed. The default workflow works out of the box for standard design → implement → test pipelines.

These files travel with the project. When `aiboard` runs from anywhere inside the repo, it picks them up automatically.

### Install-directory setup (one-time, machine-local)

If you have personal credentials that apply to every project (Anthropic API key, Trello token, default executor preferences), put them in `appsettings.user.json` next to `aiboard.exe` — gitignored, never committed:

```json
{
  "ClaudeCli": { "ExecutablePath": "claude" }
}
```

> **Don't edit `appsettings.json` next to `aiboard.exe`.** Per-project `.aiboard/` files override it silently. Edits there will appear to do nothing once a project ships its own `.aiboard/appsettings.json`. The shipped `appsettings.json` is the floor — leave it as-is.

## Run a single card

```
aiboard --mode agent --card-id <CARD_ID> --workspace <PATH_TO_YOUR_REPO>
```

- `--card-id` — the GitHub issue number (visible in the card URL or via `gh project item-list`)
- `--workspace` — absolute path to the git repo the agents will work in
- `--board-id` — optional; auto-derived from `GitHubProjects:ProjectNumber` in your config

## Run in polling mode (auto-pickup)

```
aiboard --mode polling --workspace <PATH_TO_YOUR_REPO>
```

Polls the board and picks up the highest-priority card in any "Ready for" column.
Default interval is 120 seconds (with adaptive backoff); override with `--poll-interval <SECONDS>`.

## Run in queue mode (webhook-driven)

```
aiboard --mode queue --workspace <PATH_TO_YOUR_REPO> --neon-connection "postgresql://user:pass@host/db?sslmode=require"
```

Polls a Postgres PGMQ queue for webhook "ping" notifications instead of the board directly.
When a ping arrives, fetches the board once and processes **all** eligible cards concurrently.
Falls back to a safety-net board poll every 10 minutes if no pings arrive.

Requires a Neon PostgreSQL database with PGMQ migrations applied — see `docs/Deployment.md` for setup.

| Flag | Description | Default |
|------|-------------|---------|
| `--neon-connection <url>` | Neon PostgreSQL connection string | *(required)* |
| `--ping-queue <name>` | PGMQ queue name | `pings` |
| `--max-concurrent-agents <n>` | Max parallel agent runs (0 = unlimited) | `0` |
| `--stale-claim-minutes <n>` | Timeout for abandoned card claims | `30` |

## Command-line reference

Run `aiboard` with no arguments or `aiboard --help` to see all available options.

Every setting can be provided via any of the following (highest priority wins):

1. **Command-line args** — `--board-provider github`, `--github-repo owner/repo`, etc.
2. **Environment variables** — `BoardProvider=github`, `GitHubProjects__Repo=owner/repo`, etc.
3. **Config files**, highest precedence first — env vars > `--config <path>` file > `appsettings.user.json` (next to exe, gitignored) > `./.aiboard/appsettings.json` (project-local — **edit this for per-project board/workflow config**) > `appsettings.json` (next to exe, shipped defaults — don't edit)

### Key options

| Flag | Description | Default |
|------|-------------|---------|
| `--mode <mode>` | `agent`, `polling`, or `queue` | *(required)* |
| `--card-id <id>` | Card/issue number (agent mode only) | *(required for agent)* |
| `--config <path>` | Additional JSON config file to layer in | *(none)* |
| `--prompt-root <path>` | Base directory for prompt files | exe directory |
| `--board-provider <name>` | `stub`, `trello`, `github` | `stub` |
| `--agent-executor <name>` | `stub`, `claude-cli` | `stub` |
| `--workflow-config <path>` | Path to workflow JSON file | `workflow.v1.json` (exe dir) |
| `--board-id <id>` | Board identifier / project number | from config |
| `--workspace <path>` | Agent workspace / repository path | *(required)* |
| `--worktree-base <path>` | Base path for git worktrees | *(auto)* |
| `--poll-interval <secs>` | Base polling interval in seconds (adaptive backoff) | `120` |
| `--github-owner <owner>` | GitHub org or user | from config |
| `--github-repo <owner/repo>` | Repository in owner/repo format | from config |
| `--github-project <number>` | GitHub project number | from config |
| `--claude-path <path>` | Path to Claude CLI executable | `claude` |
| `--claude-max-budget <usd>` | Max budget in USD per invocation | `10.00` |
| `--claude-timeout <secs>` | Claude CLI hard wall-clock timeout in seconds | `7200` |

## Running multiple projects

A single `aiboard` installation can manage multiple projects. The key is separating what
differs per project (repo, board, possibly workflow and prompts) from what stays the same.

### Scenario 1: Different workflows and different prompts per project

Each project has its own workflow JSON and its own prompt files. Create a per-project
config file and point it at the project's content directory:

```
projects/
  project-a/
    config.json          # board + workflow + prompt config for project A
    workflow.json
    prompts/
      senior_engineer.md
      ...
  project-b/
    config.json
    workflow.json
    prompts/
      ...
```

**project-a/config.json:**
```json
{
  "BoardProvider": "github",
  "AgentExecutor": "claude-cli",
  "WorkflowConfigPath": "C:/projects/project-a/workflow.json",
  "GitHubProjects": {
    "Owner": "your-username",
    "Repo": "your-username/repo-a",
    "ProjectNumber": "1"
  }
}
```

Run each project with `--config` and `--prompt-root`:

```
aiboard --config C:/projects/project-a/config.json ^
        --prompt-root C:/projects/project-a ^
        --mode polling --workspace C:/repos/repo-a

aiboard --config C:/projects/project-b/config.json ^
        --prompt-root C:/projects/project-b ^
        --mode polling --workspace C:/repos/repo-b
```

### Scenario 2: Different workflows, same prompts

Projects share the same prompt files (e.g. the default prompts shipped with aiboard)
but each has its own workflow configuration.

```
projects/
  project-a/
    config.json
    workflow.json        # project-specific workflow
  project-b/
    config.json
    workflow.json
shared/
  prompts/               # shared prompt files
    senior_engineer.md
    ...
```

Point `--prompt-root` at the shared prompts directory (or omit it to use the default
prompts shipped alongside `aiboard.exe`):

```
aiboard --config C:/projects/project-a/config.json ^
        --prompt-root C:/shared ^
        --mode polling --workspace C:/repos/repo-a

aiboard --config C:/projects/project-b/config.json ^
        --prompt-root C:/shared ^
        --mode polling --workspace C:/repos/repo-b
```

Or, if the default prompts in the aiboard directory are fine, omit `--prompt-root` entirely:

```
aiboard --config C:/projects/project-a/config.json ^
        --mode polling --workspace C:/repos/repo-a
```

### Scenario 3: Same workflows and same prompts

Projects share everything except the repo/board identity. Use the default workflow
and prompts from the aiboard directory, and just override the project-specific values:

**Option A — per-project config file:**

```json
{
  "GitHubProjects": {
    "Owner": "your-username",
    "Repo": "your-username/repo-a",
    "ProjectNumber": "1"
  }
}
```

```
aiboard --config project-a.json --mode polling --workspace C:/repos/repo-a
aiboard --config project-b.json --mode polling --workspace C:/repos/repo-b
```

**Option B — purely command-line (no config files needed):**

```
aiboard --mode polling --workspace C:/repos/repo-a ^
        --board-provider github --agent-executor claude-cli ^
        --github-owner your-username ^
        --github-repo your-username/repo-a ^
        --github-project 1

aiboard --mode polling --workspace C:/repos/repo-b ^
        --board-provider github --agent-executor claude-cli ^
        --github-owner your-username ^
        --github-repo your-username/repo-b ^
        --github-project 2
```

### Path resolution rules

All path options (`--config`, `--prompt-root`, `--workflow-config`, `--workspace`,
`--worktree-base`) follow the same resolution rules, regardless of whether the value
comes from the command line, an environment variable, or a config file:

| Path form | Resolved relative to | Examples |
|-----------|---------------------|----------|
| Starts with `.` | Current working directory | `./workflow.json`, `../content`, `.` |
| Absolute path | Used as-is (normalized) | `C:\configs\workflow.json`, `/opt/content` |
| Bare path | Executable directory | `workflow.json`, `content/prompts` |

This means:
- `--workflow-config workflow.github.json` resolves to `{exe-dir}/workflow.github.json`
- `--workflow-config ./workflow.github.json` resolves to `{CWD}/workflow.github.json`
- `--workflow-config C:\configs\workflow.json` is used as-is
- `--prompt-root .` means "use the current working directory for prompt files"
- `--prompt-root prompts/custom` means `{exe-dir}/prompts/custom`

Both forward slashes and backslashes work on all platforms.

## Files in this archive

| File | Purpose |
|------|---------|
| `aiboard.exe` / `aiboard` | The executable |
| `appsettings.json` | Shipped defaults — **do not edit**. Override per-project via `./.aiboard/appsettings.json` in your project repo (preferred), or per-machine via `appsettings.user.json` next to `aiboard.exe`. |
| `appsettings.user.example.json` | Template for `appsettings.user.json` — machine-local secrets that span every project (e.g., personal API keys). |
| `workflow.github.example.json` | Template for the project-local workflow file — copy into the project's `./.aiboard/workflow.github.json` and customise. |
| `workflow.v1.json` | Legacy Trello workflow (ignore unless using Trello) |
| `docker-compose.yml` | Local PostgreSQL + Flyway migrations |
| `db/migrations/` | SQL migration files applied by Flyway |
| `prompts/` | Default agent prompt files referenced by the workflow config |
| `docs/` | Deployment guide, architecture docs |
| `QUICKSTART.md` | This file |

## Troubleshooting

- **"Workflow config not found"** — set `WorkflowConfigPath` in your config or via `--workflow-config`
- **"gh: command not found"** — install GitHub CLI: https://cli.github.com
- **"claude: command not found"** — install Claude CLI: https://docs.anthropic.com/en/docs/claude-cli
- **Prerequisite validation errors** — the app checks for `gh auth status`, prompt files, and config on startup; read the error messages for what's missing
