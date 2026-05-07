#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds aiboard-opencode-sandbox:dotnet — the OpenCode sandbox with
    .NET 10 SDK overlaid for task-board self-hosting.

.DESCRIPTION
    Layered on top of aiboard-opencode-sandbox:latest. See
    docker/overlays/Dockerfile.dotnet for the install layer.

.PARAMETER DotnetChannel
    .NET install channel string. Default "10.0".

.PARAMETER Tag
    Image tag suffix. Default "dotnet". Resulting full tag:
    aiboard-opencode-sandbox:<Tag>.

.PARAMETER NoCache
    Force `docker build --no-cache`.

.EXAMPLE
    pwsh ./docker/overlays/build-opencode-dotnet.ps1
#>
[CmdletBinding()]
param(
    [string]$DotnetChannel = "10.0",
    [string]$Tag = "dotnet",
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$baseImage = "aiboard-opencode-sandbox:latest"
$resultTag = "aiboard-opencode-sandbox:$Tag"

docker image inspect $baseImage *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Base image '$baseImage' not found. Build it first via aiboard's scripts/build-opencode-sandbox.ps1."
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
docker image ls aiboard-opencode-sandbox
