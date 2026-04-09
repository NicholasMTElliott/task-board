<#
.SYNOPSIS
    Smoke-tests the aiboard agent sandbox Docker image against all acceptance criteria.

.DESCRIPTION
    Runs a series of docker run commands against a built aiboard-agent-sandbox image
    and reports pass/fail for each acceptance criterion. Build the image first with
    scripts/build-sandbox.ps1.

.PARAMETER Tag
    Docker image tag to test. Defaults to latest.

.EXAMPLE
    .\scripts\test-sandbox.ps1

.EXAMPLE
    .\scripts\test-sandbox.ps1 -Tag dev
#>
param(
    [string]$Tag = "latest"
)

$ErrorActionPreference = "Stop"

$imageName = "aiboard-agent-sandbox:$Tag"
$passed = 0
$failed = 0

function Test-Assertion {
    param(
        [string]$Name,
        [scriptblock]$Block
    )

    try {
        $result = & $Block
        if ($result) {
            Write-Host "  [PASS] $Name"
            $script:passed++
        } else {
            Write-Host "  [FAIL] $Name"
            $script:failed++
        }
    } catch {
        Write-Host "  [FAIL] $Name — $_"
        $script:failed++
    }
}

function Invoke-Container {
    param([string[]]$Args)
    $output = docker run --rm $imageName bash -c ($Args -join " ") 2>&1
    return $output
}

Write-Host ""
Write-Host "=== Agent Sandbox Smoke Tests ==="
Write-Host "Image: $imageName"
Write-Host ""

# --- AC1: Dockerfile builds and image exists ---
Write-Host "[AC1] Image exists"
Test-Assertion "Image '$imageName' is present in local registry" {
    $inspect = docker image inspect $imageName 2>&1
    $LASTEXITCODE -eq 0
}

# --- AC2: Claude CLI installed and functional ---
Write-Host ""
Write-Host "[AC2] Claude CLI"
Test-Assertion "claude --version exits 0" {
    $out = docker run --rm $imageName claude --version 2>&1
    $LASTEXITCODE -eq 0
}
Test-Assertion "claude --version output contains version string" {
    $out = docker run --rm $imageName claude --version 2>&1
    "$out" -match "\d+\.\d+\.\d+"
}

# --- AC3: Git installed and supports worktree operations ---
Write-Host ""
Write-Host "[AC3] Git worktree support"
Test-Assertion "git --version exits 0" {
    docker run --rm $imageName git --version 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}
Test-Assertion "git worktree list exits 0 in a fresh repo" {
    $script = "cd /tmp && git init testrepo && cd testrepo && git worktree list"
    docker run --rm $imageName bash -c $script 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}

# --- AC4: Non-root user ---
Write-Host ""
Write-Host "[AC4] Non-root user"
Test-Assertion "whoami returns 'agent'" {
    $out = docker run --rm $imageName whoami 2>&1
    "$out".Trim() -eq "agent"
}
Test-Assertion "id -u does not return 0 (not root)" {
    $out = docker run --rm $imageName id -u 2>&1
    [int]"$out".Trim() -ne 0
}

# --- AC5: Configurable base image ---
Write-Host ""
Write-Host "[AC5] Configurable base image"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$dockerfileDir = Join-Path $repoRoot "docker\agent-sandbox"
Test-Assertion "Dockerfile contains 'ARG BASE_IMAGE'" {
    $content = Get-Content (Join-Path $dockerfileDir "Dockerfile") -Raw
    $content -match "ARG BASE_IMAGE"
}
Test-Assertion "Dockerfile uses ARG in FROM instruction" {
    $content = Get-Content (Join-Path $dockerfileDir "Dockerfile") -Raw
    $content -match "FROM \`${BASE_IMAGE}"
}

# --- AC6: Build script exists ---
Write-Host ""
Write-Host "[AC6] Build script"
Test-Assertion "scripts/build-sandbox.ps1 exists" {
    Test-Path (Join-Path $repoRoot "scripts\build-sandbox.ps1")
}
Test-Assertion "docker-compose.yml contains agent-sandbox service" {
    $content = Get-Content (Join-Path $repoRoot "docker-compose.yml") -Raw
    $content -match "agent-sandbox"
}

# --- AC7: Image size documented (report only) ---
Write-Host ""
Write-Host "[AC7] Image size"
Test-Assertion "docker image inspect returns size info" {
    $sizeOutput = docker image inspect $imageName --format "{{.Size}}" 2>&1
    $LASTEXITCODE -eq 0 -and "$sizeOutput".Trim() -match "^\d+$"
}
$sizeOutput = docker image inspect $imageName --format "{{.Size}}" 2>&1
if ($LASTEXITCODE -eq 0) {
    $sizeMb = [math]::Round([long]"$sizeOutput".Trim() / 1MB, 1)
    Write-Host "         Image size: ${sizeMb} MB"
}

# --- Additional checks ---
Write-Host ""
Write-Host "[Extra] Tool availability"
Test-Assertion "ripgrep (rg) is on PATH" {
    docker run --rm $imageName rg --version 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}
Test-Assertion "curl is on PATH" {
    docker run --rm $imageName curl --version 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}
Test-Assertion "npm is on PATH" {
    docker run --rm $imageName npm --version 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}
Test-Assertion "node is on PATH" {
    docker run --rm $imageName node --version 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}

Write-Host ""
Write-Host "[Extra] Agent user configuration"
Test-Assertion "Agent home directory exists" {
    $out = docker run --rm $imageName bash -c "test -d /home/agent && echo yes" 2>&1
    "$out".Trim() -eq "yes"
}
Test-Assertion "/workspace exists and is writable by agent" {
    $out = docker run --rm $imageName bash -c "test -w /workspace && echo yes" 2>&1
    "$out".Trim() -eq "yes"
}
Test-Assertion "WORKDIR is /workspace" {
    $out = docker run --rm $imageName pwd 2>&1
    "$out".Trim() -eq "/workspace"
}

Write-Host ""
Write-Host "[Extra] CLAUDECODE env var is not set (required for headless operation)"
Test-Assertion "CLAUDECODE env var is absent" {
    $out = docker run --rm $imageName bash -c "printenv CLAUDECODE 2>/dev/null; echo exit:\$?" 2>&1
    "$out" -match "exit:0" -eq $false -or "$out".Trim() -eq "exit:0"
    # More direct check: env | grep CLAUDECODE should be empty
    $check = docker run --rm $imageName bash -c "env | grep -c CLAUDECODE || true" 2>&1
    [int]"$check".Trim() -eq 0
}

Write-Host ""
Write-Host "================================="
Write-Host "Results: $passed passed, $failed failed"

if ($failed -gt 0) {
    Write-Host ""
    Write-Error "$failed test(s) failed."
    exit 1
} else {
    Write-Host ""
    Write-Host "All tests passed."
}
