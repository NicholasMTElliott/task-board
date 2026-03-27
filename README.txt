AI Board - AI Kanban Agent Orchestrator
========================================

Prerequisites (must be installed and on PATH):
  - git (any recent version)
  - gh (GitHub CLI, authenticated: gh auth login --scopes project,repo)
  - claude (Claude Code CLI, authenticated: claude login)

Quick Start:
  1. Copy workflow.github.example.json to workflow.github.json
  2. Edit workflow.github.json to match your board's column names
  3. Set environment variables (see .env.example for the full list):
       BOARD_PROVIDER=github
       AGENT_EXECUTOR=claude-cli
       WORKFLOW_CONFIG_PATH=/path/to/workflow.github.json
       GitHubProjects__Owner=YourGitHubUser
       GitHubProjects__Repo=YourUser/your-repo
       GitHubProjects__ProjectNumber=1
  4. cd /path/to/your/project/repo
  5. aiboard --mode polling --board-id 1 --workspace . --poll-interval 60

To run a single card:
  aiboard --mode agent --card-id 3 --board-id 1 --workspace .

Stop polling with Ctrl+C (graceful shutdown).

Note for Linux/Mac users:
  After unzipping, you may need to make the binary executable:
    chmod +x aiboard

Prompt files and example configs are bundled in the same directory as the
executable. WORKFLOW_CONFIG_PATH should point to your customized copy of
workflow.github.json. Prompt paths in the workflow config are resolved
relative to the directory containing the workflow config file.
