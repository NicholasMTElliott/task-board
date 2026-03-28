param(
    [Parameter(Mandatory = $true)]
    [string]$CardId
)

$ErrorActionPreference = "Stop"

# Board provider
$env:BoardProvider = "github"
$env:AgentExecutor = "claude-cli"
$env:WorkflowConfigPath = "$PSScriptRoot\..\workflow.github.json"

# GitHub Projects config
$env:GitHubProjects__Owner = "NicholasMTElliott"
$env:GitHubProjects__Repo = "NicholasMTElliott/task-board"
$env:GitHubProjects__ProjectNumber = "1"

$workspace = (Resolve-Path "$PSScriptRoot\..").Path

dotnet run --project "$PSScriptRoot\..\lambda\src\TaskBoard.Worker" -- --mode agent --card-id $CardId --board-id 1 --workspace $workspace
