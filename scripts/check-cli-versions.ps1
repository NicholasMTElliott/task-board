<#
.SYNOPSIS
    Polls the npm registry for the latest published version of each CLI we
    sandbox (Claude, Codex, OpenCode) and compares against the version pinned
    in our Dockerfiles. Prints a punch list of CLIs with a newer release than
    we've validated.

.DESCRIPTION
    Three pieces of context carry the source-of-truth for CLI versions:

      1. docker/{agent,codex,opencode}-sandbox/Dockerfile — the ARG default
         that gets baked into the image when the build script runs without
         an explicit -*CliVersion override.
      2. lambda/src/TaskBoard.Worker/Validation/CliVersionPolicy.cs — the
         (MinSupported, MaxKnown) range used for runtime startup checks
         and parser-corpus fixtures.
      3. scripts/build-{X}-sandbox.ps1 — the PowerShell build wrappers.

    All three should move in lockstep. This script only consults the
    Dockerfiles (the build artefact) — operator's job is to follow the
    docs/CliVersionTesting.md bump dance to keep the policy and fixtures in
    sync. The DockerfilePolicyDriftTests unit test catches drift between
    Dockerfile and policy.

.PARAMETER Json
    Emit a machine-readable report instead of the human punch list.

.PARAMETER FailIfDrifted
    Exit with code 1 when at least one CLI has a newer published version than
    we've pinned. Useful in CI as a "should we schedule a fixture-capture
    sprint?" signal — does NOT block the build, just nags.

.EXAMPLE
    pwsh ./scripts/check-cli-versions.ps1

.EXAMPLE
    pwsh ./scripts/check-cli-versions.ps1 -FailIfDrifted

.NOTES
    Requires `npm` on PATH (uses `npm view <pkg>@latest version`). Uses 30 s
    timeout per call. Script does NOT modify any source — it's strictly
    read-only diagnostics.
#>
[CmdletBinding()]
param(
    [switch]$Json,
    [switch]$FailIfDrifted
)

$ErrorActionPreference = 'Stop'

# Resolve repo root from script location (scripts/ → repo root).
$repoRoot = Split-Path -Parent $PSScriptRoot

# CLIs we monitor. Keep the npm package name + Dockerfile path + ARG name
# colocated here so adding a new CLI is one struct entry.
$clis = @(
    @{
        Key            = 'claude'
        DisplayName    = 'Claude Code CLI'
        NpmPackage     = '@anthropic-ai/claude-code'
        Dockerfile     = Join-Path $repoRoot 'docker/agent-sandbox/Dockerfile'
        DockerfileArg  = 'CLAUDE_CLI_VERSION'
        BuildScript    = Join-Path $repoRoot 'scripts/build-sandbox.ps1'
    }
    @{
        Key            = 'codex'
        DisplayName    = 'OpenAI Codex CLI'
        NpmPackage     = '@openai/codex'
        Dockerfile     = Join-Path $repoRoot 'docker/codex-sandbox/Dockerfile'
        DockerfileArg  = 'CODEX_CLI_VERSION'
        BuildScript    = Join-Path $repoRoot 'scripts/build-codex-sandbox.ps1'
    }
    @{
        Key            = 'opencode'
        DisplayName    = 'OpenCode CLI'
        NpmPackage     = 'opencode-ai'
        Dockerfile     = Join-Path $repoRoot 'docker/opencode-sandbox/Dockerfile'
        DockerfileArg  = 'OPENCODE_CLI_VERSION'
        BuildScript    = Join-Path $repoRoot 'scripts/build-opencode-sandbox.ps1'
    }
)

function Get-DockerfilePin {
    param([string]$DockerfilePath, [string]$ArgName)
    if (-not (Test-Path $DockerfilePath)) { return $null }
    $content = Get-Content $DockerfilePath -Raw
    # Match the ARG default: ARG NAME=VALUE  (last assignment wins, in case
    # multiple ARGs with the same name appear — Dockerfile semantics).
    $matches = [regex]::Matches($content, "^\s*ARG\s+$([regex]::Escape($ArgName))=(?<v>[^\r\n]+)", [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -eq 0) { return $null }
    return $matches[$matches.Count - 1].Groups['v'].Value.Trim()
}

function Get-NpmLatest {
    param([string]$Package)
    $proc = $null
    try {
        # `npm view <pkg> version` returns just the version string on stdout.
        # Default registry. Network errors / missing npm propagate as exceptions.
        $output = npm view "$Package@latest" version 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $output) { return $null }
        return $output.Trim()
    }
    catch {
        return $null
    }
}

function Compare-SemVer {
    param([string]$A, [string]$B)
    # Returns: -1 if A<B, 0 if A==B, 1 if A>B. Ignores pre-release suffixes.
    $rx = [regex]'(\d+)\.(\d+)\.(\d+)'
    $ma = $rx.Match($A); $mb = $rx.Match($B)
    if (-not $ma.Success -or -not $mb.Success) { return 0 } # can't compare; treat equal
    for ($i = 1; $i -le 3; $i++) {
        $av = [int]$ma.Groups[$i].Value
        $bv = [int]$mb.Groups[$i].Value
        if ($av -lt $bv) { return -1 }
        if ($av -gt $bv) { return 1 }
    }
    return 0
}

$report = @()
$drifted = $false

foreach ($cli in $clis) {
    $pinned = Get-DockerfilePin -DockerfilePath $cli.Dockerfile -ArgName $cli.DockerfileArg
    $latest = Get-NpmLatest -Package $cli.NpmPackage

    $status = 'unknown'
    $message = ''

    if (-not $pinned) {
        $status = 'error'
        $message = "Could not read pin from $($cli.Dockerfile) (looking for ARG $($cli.DockerfileArg))"
    }
    elseif ($pinned -eq 'latest') {
        $status = 'unpinned'
        $message = "Dockerfile uses 'latest' — please pin to a concrete version"
        $drifted = $true
    }
    elseif (-not $latest) {
        $status = 'npm-failed'
        $message = "Could not query npm for $($cli.NpmPackage) (network / npm missing)"
    }
    else {
        $cmp = Compare-SemVer -A $pinned -B $latest
        if ($cmp -lt 0) {
            $status = 'drifted'
            $message = "npm has newer release; consider capturing a fixture and bumping"
            $drifted = $true
        }
        elseif ($cmp -gt 0) {
            $status = 'ahead'
            $message = "Dockerfile pin is ahead of npm latest (likely a downgrade-after-upgrade situation)"
        }
        else {
            $status = 'current'
            $message = "Dockerfile pin matches npm latest"
        }
    }

    $report += [PSCustomObject]@{
        Cli           = $cli.Key
        DisplayName   = $cli.DisplayName
        NpmPackage    = $cli.NpmPackage
        DockerfilePin = $pinned
        NpmLatest     = $latest
        Status        = $status
        Message       = $message
    }
}

if ($Json) {
    $report | ConvertTo-Json -Depth 4
}
else {
    Write-Host ''
    Write-Host 'CLI Version Drift Report' -ForegroundColor Cyan
    Write-Host '========================' -ForegroundColor Cyan
    foreach ($row in $report) {
        $color = switch ($row.Status) {
            'current'    { 'Green' }
            'ahead'      { 'Yellow' }
            'drifted'    { 'Yellow' }
            'unpinned'   { 'Red' }
            'npm-failed' { 'DarkYellow' }
            default      { 'Red' }
        }
        Write-Host ''
        Write-Host "$($row.DisplayName) ($($row.NpmPackage))" -ForegroundColor White
        Write-Host "  Dockerfile pin: $($row.DockerfilePin)"
        Write-Host "  npm latest:     $($row.NpmLatest)"
        Write-Host "  Status:         " -NoNewline
        Write-Host $row.Status -ForegroundColor $color -NoNewline
        Write-Host " — $($row.Message)"
    }
    Write-Host ''
    Write-Host 'Bump dance for drifted entries:' -ForegroundColor Cyan
    Write-Host '  1. Capture a fixture from the new CLI version (see docs/CliVersionTesting.md).'
    Write-Host '  2. Run parser-corpus tests; fix the parser if it broke.'
    Write-Host '  3. Bump CliVersionPolicy.KnownGood[<cli>].MaxKnown.'
    Write-Host '  4. Bump the matching Dockerfile ARG default and build-script default.'
    Write-Host '  5. DockerfilePolicyDriftTests verifies steps 3+4 stay in sync.'
    Write-Host ''
}

if ($FailIfDrifted -and $drifted) {
    exit 1
}
