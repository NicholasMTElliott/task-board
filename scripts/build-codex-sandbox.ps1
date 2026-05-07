<#
.SYNOPSIS
    Builds the aiboard Codex sandbox Docker image.

.DESCRIPTION
    Builds docker/codex-sandbox/Dockerfile with configurable build arguments.
    The resulting image is tagged aiboard-codex-sandbox:<Tag>.

    The image wraps the OpenAI Codex CLI for sandboxed agent execution.
    Codex runs with --yolo by default inside the container because the
    container itself provides filesystem isolation — see
    DockerCodexAgentExecutor for details.

.PARAMETER BaseImage
    Base Docker image to use. Defaults to node:22-slim.

.PARAMETER AgentUid
    UID for the non-root agent user inside the container. Defaults to 1000.

.PARAMETER AgentGid
    GID for the non-root agent group inside the container. Defaults to 1000.

.PARAMETER CodexCliVersion
    npm version spec for @openai/codex. Defaults to latest.

.PARAMETER Tag
    Docker image tag. Defaults to latest.

.PARAMETER NoCache
    When set, passes --no-cache to docker build.

.EXAMPLE
    .\scripts\build-codex-sandbox.ps1

.EXAMPLE
    .\scripts\build-codex-sandbox.ps1 -CodexCliVersion 0.125.0 -Tag dev -NoCache
#>
param(
    [string]$BaseImage = "node:22-slim",
    [int]$AgentUid = 1000,
    [int]$AgentGid = 1000,
    # Default mirrors the Dockerfile's ARG CODEX_CLI_VERSION pin.
    # Bump in lockstep with docker/codex-sandbox/Dockerfile and
    # CliVersionPolicy.KnownGood["codex"].MaxKnown — see docs/CliVersionTesting.md.
    [string]$CodexCliVersion = "0.128.0",
    [string]$Tag = "latest",
    [switch]$NoCache
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$dockerfileDir = Join-Path $repoRoot "docker\codex-sandbox"
$imageName = "aiboard-codex-sandbox:$Tag"

Write-Host "Building $imageName..."
Write-Host "  BASE_IMAGE         = $BaseImage"
Write-Host "  AGENT_UID          = $AgentUid"
Write-Host "  AGENT_GID          = $AgentGid"
Write-Host "  CODEX_CLI_VERSION  = $CodexCliVersion"
Write-Host ""

$buildArgs = @("build")
if ($NoCache) {
    $buildArgs += "--no-cache"
}
$buildArgs += @(
    "--build-arg", "BASE_IMAGE=$BaseImage",
    "--build-arg", "AGENT_UID=$AgentUid",
    "--build-arg", "AGENT_GID=$AgentGid",
    "--build-arg", "CODEX_CLI_VERSION=$CodexCliVersion",
    "--tag", $imageName,
    $dockerfileDir
)

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

Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Authenticate Codex on the host: codex login"
Write-Host "     (this populates ~/.codex/auth.json which the container mounts)"
Write-Host "  2. Use 'docker-codex' as a role provider in your workflow JSON."
