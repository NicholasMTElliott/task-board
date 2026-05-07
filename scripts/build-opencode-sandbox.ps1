<#
.SYNOPSIS
    Builds the aiboard OpenCode sandbox Docker image.

.DESCRIPTION
    Builds docker/opencode-sandbox/Dockerfile with configurable build arguments.
    The resulting image is tagged aiboard-opencode-sandbox:<Tag>.

    The image wraps the OpenCode CLI for use with a local llama.cpp server
    (e.g. Qwen3.6 served by the `local-llm` sibling project on the `llm-net`
    Docker bridge network).

.PARAMETER BaseImage
    Base Docker image to use. Defaults to node:22-slim.

.PARAMETER AgentUid
    UID for the non-root agent user inside the container. Defaults to 1000.

.PARAMETER AgentGid
    GID for the non-root agent group inside the container. Defaults to 1000.

.PARAMETER OpenCodeCliVersion
    npm version spec for opencode-ai. Defaults to latest.

.PARAMETER Tag
    Docker image tag. Defaults to latest.

.PARAMETER NoCache
    When set, passes --no-cache to docker build.

.EXAMPLE
    .\scripts\build-opencode-sandbox.ps1

.EXAMPLE
    .\scripts\build-opencode-sandbox.ps1 -BaseImage node:20-slim -Tag dev -NoCache
#>
param(
    [string]$BaseImage = "node:22-slim",
    [int]$AgentUid = 1000,
    [int]$AgentGid = 1000,
    # Default mirrors the Dockerfile's ARG OPENCODE_CLI_VERSION pin.
    # Bump in lockstep with docker/opencode-sandbox/Dockerfile and
    # CliVersionPolicy.KnownGood["opencode"].MaxKnown — see docs/CliVersionTesting.md.
    [string]$OpenCodeCliVersion = "1.14.26",
    [string]$Tag = "latest",
    [switch]$NoCache
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$dockerfileDir = Join-Path $repoRoot "docker\opencode-sandbox"
$imageName = "aiboard-opencode-sandbox:$Tag"

Write-Host "Building $imageName..."
Write-Host "  BASE_IMAGE           = $BaseImage"
Write-Host "  AGENT_UID            = $AgentUid"
Write-Host "  AGENT_GID            = $AgentGid"
Write-Host "  OPENCODE_CLI_VERSION = $OpenCodeCliVersion"
Write-Host ""

$buildArgs = @("build")
if ($NoCache) {
    $buildArgs += "--no-cache"
}
$buildArgs += @(
    "--build-arg", "BASE_IMAGE=$BaseImage",
    "--build-arg", "AGENT_UID=$AgentUid",
    "--build-arg", "AGENT_GID=$AgentGid",
    "--build-arg", "OPENCODE_CLI_VERSION=$OpenCodeCliVersion",
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
Write-Host "  1. Ensure the local-llm project is running: cd ..\local-llm; docker compose up -d"
Write-Host "  2. Verify the llm-net network exists: docker network ls | Select-String llm-net"
Write-Host "  3. Set AGENT_EXECUTOR=docker-opencode to enable the executor."
