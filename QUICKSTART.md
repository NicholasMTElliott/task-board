# AI Board — Quick Start

## Prerequisites

- **gh CLI** — installed and authenticated (`gh auth login` with `project` + `repo` scopes)
- **claude CLI** — installed and authenticated (`claude` must be on PATH)
- **Git** — installed and on PATH

## Setup (one-time, single project)

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
   (the default workflow works out of the box for standard design -> implement -> test pipelines)

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
Default interval is 60 seconds; override with `--poll-interval <SECONDS>`.

## Command-line reference

Run `aiboard` with no arguments or `aiboard --help` to see all available options.

Every setting can be provided via any of the following (highest priority wins):

1. **Command-line args** — `--board-provider github`, `--github-repo owner/repo`, etc.
2. **Environment variables** — `BoardProvider=github`, `GitHubProjects__Repo=owner/repo`, etc.
3. **Config files** — `appsettings.user.json` > `--config` file > `appsettings.json`

### Key options

| Flag | Description | Default |
|------|-------------|---------|
| `--mode <mode>` | `agent` or `polling` | *(required)* |
| `--card-id <id>` | Card/issue number (agent mode only) | *(required for agent)* |
| `--config <path>` | Additional JSON config file to layer in | *(none)* |
| `--prompt-root <path>` | Base directory for prompt files | exe directory |
| `--board-provider <name>` | `stub`, `trello`, `github` | `stub` |
| `--agent-executor <name>` | `stub`, `claude-cli` | `stub` |
| `--workflow-config <path>` | Path to workflow JSON file | `workflow.v1.json` (exe dir) |
| `--board-id <id>` | Board identifier / project number | from config |
| `--workspace <path>` | Agent workspace / repository path | *(required)* |
| `--worktree-base <path>` | Base path for git worktrees | *(auto)* |
| `--poll-interval <secs>` | Polling interval in seconds | `60` |
| `--github-owner <owner>` | GitHub org or user | from config |
| `--github-repo <owner/repo>` | Repository in owner/repo format | from config |
| `--github-project <number>` | GitHub project number | from config |
| `--claude-path <path>` | Path to Claude CLI executable | `claude` |
| `--claude-max-budget <usd>` | Max budget in USD per invocation | `10.00` |
| `--claude-timeout <secs>` | Claude CLI timeout in seconds | `900` |

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

### Prompt resolution rules

The `--prompt-root` option controls where prompt file paths (from the workflow JSON) are resolved:

- **Not provided:** prompts resolve relative to the `aiboard.exe` directory (where
  the default prompts are installed)
- **Relative path** (e.g. `--prompt-root .` or `--prompt-root content/`): resolved
  relative to the current working directory
- **Absolute path** (e.g. `--prompt-root C:\my-prompts`): used as-is

## Files in this archive

| File | Purpose |
|------|---------|
| `aiboard.exe` / `aiboard` | The executable |
| `appsettings.json` | Default configuration (do not edit — override via `appsettings.user.json` or `--config`) |
| `appsettings.user.example.json` | Template — copy to `appsettings.user.json` and fill in your values |
| `workflow.github.example.json` | Template — copy to `workflow.github.json` and customise |
| `workflow.v1.json` | Legacy Trello workflow (ignore unless using Trello) |
| `prompts/` | Default agent prompt files referenced by the workflow config |
| `QUICKSTART.md` | This file |

## Troubleshooting

- **"Workflow config not found"** — set `WorkflowConfigPath` in your config or via `--workflow-config`
- **"gh: command not found"** — install GitHub CLI: https://cli.github.com
- **"claude: command not found"** — install Claude CLI: https://docs.anthropic.com/en/docs/claude-cli
- **Prerequisite validation errors** — the app checks for `gh auth status`, prompt files, and config on startup; read the error messages for what's missing
