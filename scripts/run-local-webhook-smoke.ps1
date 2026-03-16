param(
    [string]$EnvFile = ".env.local",
    [string]$WorkerUrl = "http://127.0.0.1:8787/webhooks/trello",
    [string]$PayloadPath = "tests/fixtures/trello/webhook.valid.json",
    [int]$DotnetRunTimeoutSeconds = 30,
    [switch]$SkipMigrate,
    [switch]$SkipReset,
    [switch]$SkipDotnetDrain
)

$actionId = "act_local_smoke_$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
$shellExe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { "pwsh" } else { "powershell" }

if (-not $SkipMigrate) {
    & $shellExe -File scripts/migrate.ps1 -Action migrate -EnvFile $EnvFile
    if ($LASTEXITCODE -ne 0) {
        throw "Migration failed."
    }
}

if (-not $SkipReset) {
    & $shellExe -File scripts/reset-queue-state.ps1 -EnvFile $EnvFile
    if ($LASTEXITCODE -ne 0) {
        throw "Queue reset failed."
    }
}

& $shellExe -File scripts/local-secrets.ps1 -Action sync -EnvFile $EnvFile
if ($LASTEXITCODE -ne 0) {
    throw "Secret sync failed."
}

& $shellExe -File scripts/simulate-trello-webhook.ps1 `
    -EnvFile $EnvFile `
    -PayloadPath $PayloadPath `
    -WorkerUrl $WorkerUrl `
    -ExpectedStatusCode 200 `
    -ActionIdOverride $actionId

if ($LASTEXITCODE -ne 0) {
    throw "Webhook simulation failed."
}

& $shellExe -File scripts/verify-queue-state.ps1 -EnvFile $EnvFile -ActionId $actionId -RequireQueueFound
if ($LASTEXITCODE -ne 0) {
    throw "Queue verification failed after enqueue."
}

if (-not $SkipDotnetDrain) {
    & $shellExe -File scripts/local-secrets.ps1 -Action sync -EnvFile $EnvFile
    if ($LASTEXITCODE -ne 0) {
        throw "Secret sync failed before dotnet drain."
    }

    $maxAttempts = 10
    $processedFound = $false

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        & $shellExe -File scripts/local-secrets.ps1 `
            -Action run-dotnet `
            -EnvFile $EnvFile `
            -RunTimeoutSeconds $DotnetRunTimeoutSeconds `
            -Mode one

        if ($LASTEXITCODE -ne 0) {
            throw "Dotnet drain failed."
        }

        $verificationJson = & $shellExe -File scripts/verify-queue-state.ps1 -EnvFile $EnvFile -ActionId $actionId
        if ($LASTEXITCODE -ne 0) {
            throw "Queue verification failed while checking processed state."
        }

        $verification = $verificationJson | ConvertFrom-Json
        if ($verification.processedFound) {
            $processedFound = $true
            break
        }

        Start-Sleep -Milliseconds 300
    }

    if (-not $processedFound) {
        throw "Processed events verification failed after $maxAttempts drain attempts."
    }
}

Write-Host "Local webhook smoke test completed for actionId '$actionId'"
