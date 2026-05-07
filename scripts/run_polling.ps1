param(
    [int]$PollInterval = 60
)

$ErrorActionPreference = "Stop"

# Board provider
$env:BoardProvider = "github"
$env:AgentExecutor = "docker-claude-cli"
$env:WorkflowConfigPath = "$PSScriptRoot\..\workflow.github.json"

# GitHub Projects config
$env:GitHubProjects__Owner = "NicholasMTElliott"
$env:GitHubProjects__Repo = "NicholasMTElliott/task-board"
$env:GitHubProjects__ProjectNumber = "1"

$workspace = (Resolve-Path "$PSScriptRoot\..").Path

dotnet run --project "$PSScriptRoot\..\lambda\src\TaskBoard.Worker" -- --mode polling --board-id 1 --workspace $workspace --poll-interval $PollInterval
