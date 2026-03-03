param(
    [ValidateSet("sync", "run-dotnet")]
    [string]$Action = "sync",
    [string]$EnvFile = ".env.local",
    [string]$WorkerDevVarsPath = "worker/.dev.vars",
    [string]$DotnetProjectPath = "lambda/src/TaskBoard.Worker",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
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
    [Environment]::SetEnvironmentVariable($key, $value, "Process")
}

$workerKeys = @(
    "TRELLO_WEBHOOK_SECRET",
    "NEON_DATABASE_URL",
    "LAMBDA_KICK_URL",
    "INTERNAL_KICK_SECRET"
)

$workerLines = @()
foreach ($key in $workerKeys) {
    if ($values.ContainsKey($key)) {
        $workerLines += "$key=$($values[$key])"
    }
}

$workerDir = Split-Path -Parent $WorkerDevVarsPath
if (-not (Test-Path -Path $workerDir)) {
    New-Item -ItemType Directory -Path $workerDir | Out-Null
}

Set-Content -Path $WorkerDevVarsPath -Value ($workerLines -join "`n")
Write-Host "Generated $WorkerDevVarsPath from $EnvFile"

if ($Action -eq "run-dotnet") {
    & dotnet run --project $DotnetProjectPath -- @DotnetArgs
    exit $LASTEXITCODE
}
