<#
.SYNOPSIS
    End-to-end smoke test for the OpenCode Docker executor.

.DESCRIPTION
    Runs the OpenCode sandbox container against the local llama.cpp server and
    verifies the round-trip: container starts, reaches llama-server, produces
    an Agent Contract JSON response, and exits cleanly.

    This is NOT a unit test — it requires:
      - Docker daemon running
      - `llm-net` Docker network (owned by the local-llm compose project)
      - aiboard-opencode-sandbox image built (scripts/build-opencode-sandbox.ps1)
      - llama-server reachable on the llm-net network

    On failure, prints what was missing and a suggested fix.

.PARAMETER Image
    Docker image tag to test. Defaults to aiboard-opencode-sandbox:latest.

.PARAMETER ModelName
    Model alias to request from llama-server. Defaults to qwen3.6-35b-a3b.

.PARAMETER ProviderBaseUrl
    Base URL of the Anthropic-compatible API. Defaults to http://llama-server:8080.

.PARAMETER TimeoutSeconds
    How long to wait for the container to complete. Defaults to 300 (first
    request on a cold prefix cache can take 1-2 minutes).
#>
param(
    [string]$Image = "aiboard-opencode-sandbox:latest",
    [string]$ModelName = "qwen3.6-35b-a3b",
    [string]$ProviderBaseUrl = "http://llama-server:8080",
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

function Fail($msg, $suggestion = $null) {
    Write-Host ""
    Write-Host "SMOKE TEST FAILED: $msg" -ForegroundColor Red
    if ($suggestion) {
        Write-Host "  Suggested fix: $suggestion" -ForegroundColor Yellow
    }
    exit 1
}

Write-Host "OpenCode sandbox smoke test"
Write-Host "  image          = $Image"
Write-Host "  model          = $ModelName"
Write-Host "  providerBaseUrl= $ProviderBaseUrl"
Write-Host "  timeoutSec     = $TimeoutSeconds"
Write-Host ""

# 1. Docker available?
$null = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Docker daemon is not running or not reachable." `
         "Start Docker Desktop (or the dockerd daemon) and try again."
}

# 2. Image present?
$null = docker image inspect $Image 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Image '$Image' not found locally." `
         "Run scripts/build-opencode-sandbox.ps1 first."
}

# 3. llm-net network present?
$null = docker network inspect llm-net 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Docker network 'llm-net' does not exist." `
         "Start the local-llm compose project: cd ../local-llm && docker compose up -d"
}

# 4. Run a minimal one-shot against the image.
# The prompt asks for a stock Agent Contract JSON response; we don't care
# whether the model actually listed files correctly, only that the round-trip
# produced parseable JSON.
$prompt = @"
List the files in your current working directory. Respond with a JSON object
matching the Agent Contract. Example:
{"outcome":"COMPLETE","detail":"<markdown summary of what you saw>"}
"@

$containerName = "aiboard-oc-smoke-$(Get-Random -Maximum 99999999 -Minimum 10000000)"

Write-Host "Running container $containerName (up to ${TimeoutSeconds}s)..."

$dockerArgs = @(
    "run", "--rm", "-i",
    "--name", $containerName,
    "--network", "llm-net",
    "-e", "OPENCODE_PROVIDER_BASE_URL=$ProviderBaseUrl",
    "-e", "OPENCODE_AUTH_TOKEN=local",
    "-e", "OPENCODE_MODEL_NAME=$ModelName",
    $Image,
    "opencode", "run"
)

# Capture output. Use Start-Process for timeout control because `docker run` itself has no --timeout.
$stdout = ""
$stderr = ""
$exitCode = -1

try {
    $tmpOut = [System.IO.Path]::GetTempFileName()
    $tmpErr = [System.IO.Path]::GetTempFileName()

    $proc = Start-Process -FilePath "docker" `
        -ArgumentList $dockerArgs `
        -RedirectStandardInput (New-TemporaryFile | ForEach-Object {
            Set-Content -Path $_.FullName -Value $prompt -NoNewline
            $_.FullName
        }) `
        -RedirectStandardOutput $tmpOut `
        -RedirectStandardError $tmpErr `
        -NoNewWindow -PassThru

    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Host "Container did not exit within ${TimeoutSeconds}s — stopping." -ForegroundColor Yellow
        docker stop -t 30 $containerName 2>&1 | Out-Null
        docker rm -f $containerName 2>&1 | Out-Null
        Fail "Container timed out." `
             "Possibly a cold prefix cache or llama-server is unresponsive. Check 'docker logs llama-server' for the state of the upstream server."
    }

    $exitCode = $proc.ExitCode
    $stdout = Get-Content -Path $tmpOut -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content -Path $tmpErr -Raw -ErrorAction SilentlyContinue

    Remove-Item -Path $tmpOut -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $tmpErr -Force -ErrorAction SilentlyContinue
} catch {
    Fail "Failed to run docker: $_"
}

Write-Host ""
Write-Host "--- container stderr ---"
Write-Host $stderr
Write-Host "--- container stdout ---"
Write-Host $stdout
Write-Host "--- end ---"
Write-Host ""

if ($exitCode -ne 0) {
    Fail "Container exited with code $exitCode." `
         "See stderr above. Common causes: llama-server unreachable, OpenCode config not templated correctly (check entrypoint.sh), or the model alias doesn't match local-llm's configuration."
}

# 5. Parse stdout for Agent Contract JSON.
# Same strategies the executor uses: fenced ```json block, whole document, or
# trailing balanced { ... }.
$foundJson = $null

# Strategy 1: fenced
$fencedMatch = [regex]::Match($stdout, "```(?:json)?\s*\n(\{[\s\S]*?\})\s*\n```")
if ($fencedMatch.Success) {
    $foundJson = $fencedMatch.Groups[1].Value
}

# Strategy 2: whole document
if (-not $foundJson) {
    $trimmed = $stdout.Trim()
    if ($trimmed.StartsWith("{") -and $trimmed.EndsWith("}")) {
        try { $null = $trimmed | ConvertFrom-Json; $foundJson = $trimmed } catch { }
    }
}

# Strategy 3: trailing brace scan (naive but good enough for smoke)
if (-not $foundJson) {
    $lastClose = $stdout.LastIndexOf("}")
    if ($lastClose -ge 0) {
        for ($i = $lastClose - 1; $i -ge 0; $i--) {
            if ($stdout[$i] -eq "{") {
                $candidate = $stdout.Substring($i, $lastClose - $i + 1)
                try { $null = $candidate | ConvertFrom-Json; $foundJson = $candidate; break } catch { }
            }
        }
    }
}

if (-not $foundJson) {
    Fail "Container produced no parseable Agent Contract JSON." `
         "Check the raw stdout above. The model may need a clearer prompt or the OpenCode config may not be wired correctly."
}

try {
    $parsed = $foundJson | ConvertFrom-Json
} catch {
    Fail "Extracted candidate JSON is not valid: $_"
}

if (-not $parsed.outcome) {
    Fail "Extracted JSON has no 'outcome' field." `
         "Raw JSON: $foundJson"
}

if ($parsed.outcome -notin @("COMPLETE", "NEEDS_INFO", "ERROR")) {
    Fail "'outcome' value '$($parsed.outcome)' is not one of COMPLETE/NEEDS_INFO/ERROR."
}

Write-Host "SMOKE TEST PASSED" -ForegroundColor Green
Write-Host "  outcome = $($parsed.outcome)"
if ($parsed.detail) {
    $detailPreview = if ($parsed.detail.Length -gt 200) {
        $parsed.detail.Substring(0, 200) + "..."
    } else { $parsed.detail }
    Write-Host "  detail  = $detailPreview"
}
