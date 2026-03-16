param(
    [string]$EnvFile = ".env.local",
    [string]$WorkerUrl = "http://127.0.0.1:8787/webhooks/trello"
)

$validActionId = "act_contract_valid_$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"

pwsh -File scripts/simulate-trello-webhook.ps1 `
    -EnvFile $EnvFile `
    -PayloadPath "tests/fixtures/trello/webhook.valid.json" `
    -WorkerUrl $WorkerUrl `
    -ExpectedStatusCode 200 `
    -ActionIdOverride $validActionId

if ($LASTEXITCODE -ne 0) {
    throw "Valid webhook contract test failed."
}

pwsh -File scripts/simulate-trello-webhook.ps1 `
    -EnvFile $EnvFile `
    -PayloadPath "tests/fixtures/trello/webhook.missing-action-id.json" `
    -WorkerUrl $WorkerUrl `
    -ExpectedStatusCode 400

if ($LASTEXITCODE -ne 0) {
    throw "Missing action.id contract test failed."
}

pwsh -File scripts/simulate-trello-webhook.ps1 `
    -EnvFile $EnvFile `
    -PayloadPath "tests/fixtures/trello/webhook.valid.json" `
    -WorkerUrl $WorkerUrl `
    -ExpectedStatusCode 401 `
    -ActionIdOverride "act_contract_unauthorized_$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())" `
    -WebhookSecretOverride "invalid-secret"

if ($LASTEXITCODE -ne 0) {
    throw "Unauthorized contract test failed."
}

Write-Host "Worker contract tests passed."
