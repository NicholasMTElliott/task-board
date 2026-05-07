#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds aiboard-agent-sandbox:dotnet — the Claude sandbox with .NET 10 SDK
    overlaid for task-board self-hosting.

.DESCRIPTION
    Layered on top of aiboard-agent-sandbox:latest (the upstream Claude
    sandbox image). Wraps `docker build` with a base-image-existence check
    so operators get a clear "build the upstream first" error instead of a
    cryptic Docker error.

.PARAMETER DotnetChannel
    .NET install channel string. Default "10.0" (latest 10.0.x). Pin to
    a specific patch (e.g. "10.0.100") for reproducible CI builds.

.PARAMETER Tag
    Image tag suffix. Default "dotnet". Resulting full tag:
    aiboard-agent-sandbox:<Tag>.

.PARAMETER NoCache
    Force `docker build --no-cache`.

.EXAMPLE
    pwsh ./docker/overlays/build-claude-dotnet.ps1
#>
[CmdletBinding()]
param(
    [string]$DotnetChannel = "10.0",
    [string]$Tag = "dotnet",
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$baseImage = "aiboard-agent-sandbox:latest"
$resultTag = "aiboard-agent-sandbox:$Tag"

docker image inspect $baseImage *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Base image '$baseImage' not found. Build it first via aiboard's scripts/build-sandbox.ps1."
    exit 1
}

$buildArgs = @(
    "build",
    "--tag", $resultTag,
    "--file", (Join-Path $here 'Dockerfile.dotnet'),
    "--build-arg", "BASE_IMAGE=$baseImage",
    "--build-arg", "DOTNET_CHANNEL=$DotnetChannel"
)
if ($NoCache) { $buildArgs += "--no-cache" }
$buildArgs += $here

Write-Host ""
Write-Host "Building $resultTag" -ForegroundColor Cyan
Write-Host "  BASE_IMAGE     = $baseImage"
Write-Host "  DOTNET_CHANNEL = $DotnetChannel"
Write-Host ""

& docker @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
docker image ls aiboard-agent-sandbox
