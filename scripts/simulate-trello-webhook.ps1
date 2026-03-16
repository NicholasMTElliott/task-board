param(
    [string]$EnvFile = ".env.local",
    [string]$PayloadPath = "tests/fixtures/trello/webhook.valid.json",
    [string]$WorkerUrl = "http://127.0.0.1:8787/webhooks/trello",
    [int]$ExpectedStatusCode = 200,
    [string]$ActionIdOverride,
    [string]$WebhookSecretOverride
)

if (-not (Test-Path -Path $EnvFile)) {
    throw "Missing $EnvFile. Copy .env.example to .env.local and fill values."
}

if (-not (Test-Path -Path $PayloadPath)) {
    throw "Payload file '$PayloadPath' not found."
}

$values = @{}

Get-Content -Path $EnvFile | ForEach-Object {
    $line = $_.Trim()

    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
        return
    }

    $split = $line -split "=", 2
    if ($split.Length -ne 2) {
        return
    }

    $key = $split[0].Trim()
    $value = $split[1].Trim()

    if ((($value.StartsWith('"')) -and ($value.EndsWith('"'))) -or (($value.StartsWith("'")) -and ($value.EndsWith("'")))) {
        $value = $value.Substring(1, $value.Length - 2)
    }

    $values[$key] = $value
}

if (-not $values.ContainsKey("TRELLO_API_SECRET") -or [string]::IsNullOrWhiteSpace($values["TRELLO_API_SECRET"])) {
    throw "TRELLO_API_SECRET is required in $EnvFile."
}

$payload = Get-Content -Path $PayloadPath -Raw

if (-not [string]::IsNullOrWhiteSpace($ActionIdOverride)) {
    $payloadObject = $payload | ConvertFrom-Json
    if ($null -eq $payloadObject.action) {
        $payloadObject | Add-Member -NotePropertyName "action" -NotePropertyValue ([pscustomobject]@{})
    }

    $payloadObject.action.id = $ActionIdOverride
    $payload = $payloadObject | ConvertTo-Json -Depth 20
}

$webhookSecret = if (-not [string]::IsNullOrWhiteSpace($WebhookSecretOverride)) {
    $WebhookSecretOverride
} else {
    $values["TRELLO_API_SECRET"]
}

if ($PSVersionTable.PSVersion.Major -ge 7) {
    $response = Invoke-WebRequest `
        -Method Post `
        -Uri $WorkerUrl `
        -Headers @{ "x-trello-webhook-secret" = $webhookSecret } `
        -ContentType "application/json" `
        -Body $payload `
        -SkipHttpErrorCheck
}
else {
    try {
        $response = Invoke-WebRequest `
            -Method Post `
            -Uri $WorkerUrl `
            -Headers @{ "x-trello-webhook-secret" = $webhookSecret } `
            -ContentType "application/json" `
            -Body $payload `
            -UseBasicParsing
    }
    catch {
        $statusCode = $null
        $content = ""

        if ($_.Exception.Response -ne $null) {
            $statusCode = [int]$_.Exception.Response.StatusCode
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream -ne $null) {
                $reader = New-Object System.IO.StreamReader($stream)
                $content = $reader.ReadToEnd()
                $reader.Dispose()
                $stream.Dispose()
            }
        }

        $response = [pscustomobject]@{
            StatusCode = $statusCode
            Content = $content
        }
    }
}

if ($response.StatusCode -ne $ExpectedStatusCode) {
    throw "Expected status $ExpectedStatusCode but got $($response.StatusCode). Body: $($response.Content)"
}

Write-Host "Webhook simulation succeeded with status $($response.StatusCode)"
Write-Host $response.Content
