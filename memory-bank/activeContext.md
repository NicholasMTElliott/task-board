# Active Context

## Current Focus
Direct board-to-agent flow using GitHub Projects as the task board provider. The system can now take a card ID, run the appropriate agent based on the card's column, and update the board with results.

## Working Flow
```powershell
$env:BOARD_PROVIDER="github"
$env:AGENT_EXECUTOR="stub"  # or "claude-cli" for real agent
$env:WORKFLOW_CONFIG_PATH="c:\Users\Nicho\Documents\task-board\workflow.github.json"
$env:GitHubProjects__Owner="NicholasMTElliott"
$env:GitHubProjects__Repo="NicholasMTElliott/task-board"
$env:GitHubProjects__ProjectNumber="1"

dotnet run --project lambda/src/TaskBoard.Worker -- --mode agent --card-id <ISSUE_NUMBER> --board-id 1 --workspace .
```

## GitHub Project
- URL: https://github.com/users/NicholasMTElliott/projects/1/views/1
- Repo: NicholasMTElliott/task-board
- Auth: `gh` CLI with `project` + `repo` scopes

## Key Decisions Made This Session
1. GitHub Projects v2 as primary board (replacing Trello for active development)
2. `gh` CLI as the GitHub API interface (simpler than raw GraphQL HTTP calls)
3. Column names (not IDs) as workflow state keys for GitHub Projects
4. `ProjectNumber` in options rather than discovering it from issue metadata
5. IN_PROGRESS transition concept for visual feedback on the board
