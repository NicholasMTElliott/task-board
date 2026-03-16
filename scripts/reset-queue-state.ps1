param(
    [string]$EnvFile = ".env.local",
    [string]$ActionIdPrefix = "act_",
    [switch]$IncludeAll
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

$queueName = if ($values.ContainsKey("PGMQ_QUEUE_NAME") -and -not [string]::IsNullOrWhiteSpace($values["PGMQ_QUEUE_NAME"])) {
    $values["PGMQ_QUEUE_NAME"]
} else {
    "events"
}

[Environment]::SetEnvironmentVariable("PGMQ_QUEUE_NAME", $queueName, "Process")
[Environment]::SetEnvironmentVariable("RESET_ACTION_ID_PREFIX", $ActionIdPrefix, "Process")
[Environment]::SetEnvironmentVariable("RESET_INCLUDE_ALL", $IncludeAll.IsPresent.ToString().ToLowerInvariant(), "Process")

$json = node worker/scripts/reset-queue-state.mjs
if ($LASTEXITCODE -ne 0) {
    throw "Queue reset script failed."
}

$result = $json | ConvertFrom-Json
Write-Host ($result | ConvertTo-Json -Compress)
