param(
    [string]$EnvFile = ".env.local",
    [Parameter(Mandatory = $true)]
    [string]$ActionId,
    [switch]$RequireQueueFound,
    [switch]$RequireProcessedFound
)

if (-not (Test-Path -Path $EnvFile)) {
    throw "Missing $EnvFile. Copy .env.example to .env.local and fill values."
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

if (-not $values.ContainsKey("NEON_DATABASE_URL") -or [string]::IsNullOrWhiteSpace($values["NEON_DATABASE_URL"])) {
    throw "NEON_DATABASE_URL is required in $EnvFile."
}

[Environment]::SetEnvironmentVariable("NEON_DATABASE_URL", $values["NEON_DATABASE_URL"], "Process")
[Environment]::SetEnvironmentVariable("ACTION_ID", $ActionId, "Process")

$queueName = if ($values.ContainsKey("PGMQ_QUEUE_NAME") -and -not [string]::IsNullOrWhiteSpace($values["PGMQ_QUEUE_NAME"])) {
    $values["PGMQ_QUEUE_NAME"]
} else {
    "events"
}

[Environment]::SetEnvironmentVariable("PGMQ_QUEUE_NAME", $queueName, "Process")

$json = node worker/scripts/verify-queue-state.mjs
if ($LASTEXITCODE -ne 0) {
    throw "Queue verification script failed."
}

$result = $json | ConvertFrom-Json

if ($RequireQueueFound -and -not $result.queueFound) {
    throw "Expected queue row for actionId '$ActionId' but none was found."
}

if ($RequireProcessedFound -and -not $result.processedFound) {
    throw "Expected processed_events row for actionId '$ActionId' but none was found."
}

Write-Host ($result | ConvertTo-Json -Compress)
