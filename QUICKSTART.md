# AI Board — Quick Start

## Prerequisites

- **gh CLI** — installed and authenticated (`gh auth login` with `project` + `repo` scopes)
- **claude CLI** — installed and authenticated (`claude` must be on PATH)
- **Git** — installed and on PATH

## Setup (one-time)

1. Copy `appsettings.user.example.json` to `appsettings.user.json` (same folder as `aiboard.exe`)
2. Edit `appsettings.user.json` with your values:

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

3. Copy `workflow.github.example.json` to `workflow.github.json` and customise if needed
   (the default workflow works out of the box for standard design → implement → test pipelines)

## Run a single card

```
aiboard --mode agent --card-id <CARD_ID> --workspace <PATH_TO_YOUR_REPO>
```

- `--card-id` — the GitHub Projects item ID (visible in the card URL or via `gh project item-list`)
- `--workspace` — absolute path to the git repo the agents will work in
- `--board-id` — optional; auto-derived from `GitHubProjects:ProjectNumber` in your config

## Run in polling mode (auto-pickup)

```
aiboard --mode polling --workspace <PATH_TO_YOUR_REPO>
```

Polls the board and picks up the highest-priority card in any "Ready for" column.
Default interval is 60 seconds; override with `--poll-interval <SECONDS>`.

## Configuration reference

All settings can be provided in three ways (highest priority wins):

1. **Command-line args** — `--mode`, `--card-id`, `--board-id`, `--workspace`, `--poll-interval`, `--worktree-base`
2. **Environment variables** — `BoardProvider`, `AgentExecutor`, `GitHubProjects__Owner`, etc.
3. **Config files** — `appsettings.user.json` > `appsettings.json` (defaults)

See `.env.example` for the full list of environment variable names.

## Files in this archive

| File | Purpose |
|------|---------|
| `aiboard.exe` / `aiboard` | The executable |
| `appsettings.json` | Default configuration (do not edit — override via `appsettings.user.json`) |
| `appsettings.user.example.json` | Template — copy to `appsettings.user.json` and fill in your values |
| `workflow.github.example.json` | Template — copy to `workflow.github.json` and customise |
| `workflow.v1.json` | Legacy Trello workflow (ignore unless using Trello) |
| `prompts/` | Agent prompt files referenced by the workflow config |
| `.env.example` | Environment variable reference (for users who prefer env vars over JSON) |

## Troubleshooting

- **"Workflow config not found"** — set `WorkflowConfigPath` in `appsettings.user.json` or copy the example workflow file
- **"gh: command not found"** — install GitHub CLI: https://cli.github.com
- **"claude: command not found"** — install Claude CLI: https://docs.anthropic.com/en/docs/claude-cli
- **Prerequisite validation errors** — the app checks for `gh auth status`, prompt files, and config on startup; read the error messages for what's missing
