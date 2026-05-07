<#
.SYNOPSIS
    Builds the aiboard agent sandbox Docker image.

.DESCRIPTION
    Builds docker/agent-sandbox/Dockerfile with configurable build arguments.
    The resulting image is tagged aiboard-agent-sandbox:<Tag>.

.PARAMETER BaseImage
    Base Docker image to use. Defaults to node:22-slim.

.PARAMETER AgentUid
    UID for the non-root agent user inside the container. Defaults to 1000.

.PARAMETER AgentGid
    GID for the non-root agent group inside the container. Defaults to 1000.

.PARAMETER ClaudeCliVersion
    npm version spec for @anthropic-ai/claude-code. Defaults to latest.

.PARAMETER Tag
    Docker image tag. Defaults to latest.

.PARAMETER NoCache
    When set, passes --no-cache to docker build.

.EXAMPLE
    .\scripts\build-sandbox.ps1

.EXAMPLE
    .\scripts\build-sandbox.ps1 -BaseImage node:20-slim -Tag dev -NoCache
#>
param(
    [string]$BaseImage = "node:22-slim",
    [int]$AgentUid = 1000,
    [int]$AgentGid = 1000,
    # Default mirrors the Dockerfile's ARG CLAUDE_CLI_VERSION pin.
    # Bump in lockstep with docker/agent-sandbox/Dockerfile and
    # CliVersionPolicy.KnownGood["claude"].MaxKnown — see docs/CliVersionTesting.md.
    [string]$ClaudeCliVersion = "2.1.126",
    [string]$Tag = "latest",
    [switch]$NoCache
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$dockerfileDir = Join-Path $repoRoot "docker\agent-sandbox"
$imageName = "aiboard-agent-sandbox:$Tag"

Write-Host "Building $imageName..."
Write-Host "  BASE_IMAGE        = $BaseImage"
Write-Host "  AGENT_UID         = $AgentUid"
Write-Host "  AGENT_GID         = $AgentGid"
Write-Host "  CLAUDE_CLI_VERSION = $ClaudeCliVersion"
Write-Host ""

$buildArgs = @(
    "build",
    "--build-arg", "BASE_IMAGE=$BaseImage",
    "--build-arg", "AGENT_UID=$AgentUid",
    "--build-arg", "AGENT_GID=$AgentGid",
    "--build-arg", "CLAUDE_CLI_VERSION=$ClaudeCliVersion",
    "--tag", $imageName,
    $dockerfileDir
)

if ($NoCache) {
    $buildArgs = @("--no-cache") + $buildArgs
    # Reorder: 'build' must be first
    $buildArgs = @("build", "--no-cache") + ($buildArgs | Select-Object -Skip 2)
}

docker @buildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "docker build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$sizeOutput = docker image inspect $imageName --format "{{.Size}}" 2>&1
if ($LASTEXITCODE -eq 0) {
    $sizeBytes = [long]$sizeOutput
    $sizeMb = [math]::Round($sizeBytes / 1MB, 1)
    Write-Host ""
    Write-Host "Build succeeded: $imageName"
    Write-Host "Image size: ${sizeMb} MB"
} else {
    Write-Host ""
    Write-Host "Build succeeded: $imageName"
}
