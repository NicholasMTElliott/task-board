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

--------------------------------------------------------------------
Optional: Run agents inside a Docker sandbox (off by default)
--------------------------------------------------------------------
Isolates each agent invocation in a container instead of invoking the
host Claude CLI directly. Requires Docker daemon running and the
sandbox image built.

  1. Build the sandbox image (one-time; requires repo sources):
       .\scripts\build-sandbox.ps1
     Verify:
       docker images aiboard-agent-sandbox
     Expect tag: aiboard-agent-sandbox:latest

  2. Enable the Docker executor by setting:
       AGENT_EXECUTOR=docker
     At startup the worker probes `docker info`. If Docker is not
     reachable, the worker logs a warning and keeps using claude-cli.

  3. Optional tuning in appsettings.json (section "Docker"):
       ImageName        (default aiboard-agent-sandbox:latest)
       ReuseContainer   (default true -- one container per run via
                         docker exec; false = per-step docker run)
       NetworkMode      (default "host"; use "none" to isolate)
       MemoryLimit      (e.g. "4g"; unset = no limit)
       CpuLimit         (e.g. "2.0"; unset = no limit)
       CredentialPath   (auto-detects ~/.claude)
       TimeoutSeconds   (default 900)
       MaxBudgetUsd     (default 10.00)

  4. Run a card as usual:
       aiboard --mode agent --card-id 3 --board-id 1 --workspace .

  5. Verify:
     - While running:  docker ps --filter name=aiboard-
         Session mode:  one container "aiboard-{cardId}".
         Per-step mode: short-lived "aiboard-run-{cardId}-{suffix}".
     - After running:  docker ps -a --filter name=aiboard-   (empty)
     - Metrics:        aiboard --mode metrics --card-id 3
         Session runs populate agent_run.session_startup_ms and
         step_result.session_exec_ms. If both are NULL after a run
         with AGENT_EXECUTOR=docker, the session did NOT activate --
         usually the image is missing (rebuild it) or the daemon is
         down.

  6. Disable: unset AGENT_EXECUTOR or set it to claude-cli.

Note for Linux/Mac users:
  After unzipping, you may need to make the binary executable:
    chmod +x aiboard

Prompt files and example configs are bundled in the same directory as the
executable. WORKFLOW_CONFIG_PATH should point to your customized copy of
workflow.github.json. Prompt paths in the workflow config are resolved
relative to the directory containing the workflow config file.
