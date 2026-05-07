#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds all three task-board self-hosting overlays in one go.

.DESCRIPTION
    Convenience wrapper for build-claude-dotnet.ps1, build-codex-dotnet.ps1,
    and build-opencode-dotnet.ps1. Aborts on the first failure so the
    operator sees which base image needs rebuilding.

.PARAMETER DotnetChannel
    Forwarded to all three builds.

.PARAMETER Tag
    Forwarded to all three builds.

.PARAMETER NoCache
    Forwarded to all three builds.

.PARAMETER OnlyClaude / OnlyCodex / OnlyOpenCode
    Build just the named family. Useful when only one upstream image was
    rebuilt — skips the others.
#>
[CmdletBinding()]
param(
    [string]$DotnetChannel = "10.0",
    [string]$Tag = "dotnet",
    [switch]$NoCache,
    [switch]$OnlyClaude,
    [switch]$OnlyCodex,
    [switch]$OnlyOpenCode
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$selectAll = -not ($OnlyClaude -or $OnlyCodex -or $OnlyOpenCode)
$targets = @()
if ($selectAll -or $OnlyClaude)   { $targets += "build-claude-dotnet.ps1" }
if ($selectAll -or $OnlyCodex)    { $targets += "build-codex-dotnet.ps1" }
if ($selectAll -or $OnlyOpenCode) { $targets += "build-opencode-dotnet.ps1" }

foreach ($script in $targets) {
    $path = Join-Path $here $script
    Write-Host "==> $script" -ForegroundColor Yellow
    $forwarded = @("-DotnetChannel", $DotnetChannel, "-Tag", $Tag)
    if ($NoCache) { $forwarded += "-NoCache" }
    & $path @forwarded
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$script failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

Write-Host ""
Write-Host "All overlays built." -ForegroundColor Green
