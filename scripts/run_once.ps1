param(
    [Parameter(Mandatory = $true)]
    [string]$CardId
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

# Self-hosting overlays: agents need .NET 10 SDK to build/test this repo,
# which the upstream aiboard sandbox images don't ship. Build once via
# `./docker/overlays/build-all-dotnet.ps1`. To run against stock images
# (e.g. on a non-.NET project), unset these or override per-shell.
$env:DockerAgents__Claude__ImageName = "aiboard-agent-sandbox:dotnet"
$env:DockerAgents__ClaudeQwen__ImageName = "aiboard-agent-sandbox:dotnet"
$env:DockerAgents__Codex__ImageName = "aiboard-codex-sandbox:dotnet"
$env:DockerAgents__OpenCode__ImageName = "aiboard-opencode-sandbox:dotnet"

$workspace = (Resolve-Path "$PSScriptRoot\..").Path

dotnet run --project "$PSScriptRoot\..\lambda\src\TaskBoard.Worker" -- --mode agent --card-id $CardId --board-id 1 --workspace $workspace
