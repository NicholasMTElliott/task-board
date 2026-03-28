param(
    [ValidateSet("sync", "run-dotnet")]
    [string]$Action = "sync",
    [string]$EnvFile = ".env.local",
    [string]$WorkerDevVarsPath = "worker/.dev.vars",
    [string]$DotnetProjectPath = "lambda/src/TaskBoard.Worker",
    [int]$RunTimeoutSeconds = 0,
    [ValidateSet("one", "wait", "loop", "manual", "agent")]
    [string]$Mode,
    [int]$WaitSeconds = 30,
    [string]$CardId,
    [string]$BoardId,
    [string]$WorkspacePath,
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
    "TRELLO_API_SECRET",
    "TRELLO_WEBHOOK_CALLBACK_URL",
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
if (-not [string]::IsNullOrWhiteSpace($workerDir) -and -not (Test-Path -Path $workerDir)) {
    New-Item -ItemType Directory -Path $workerDir | Out-Null
}

Set-Content -Path $WorkerDevVarsPath -Value ($workerLines -join "`n")
Write-Host "Generated $WorkerDevVarsPath from $EnvFile"

if ($Action -eq "run-dotnet") {
    if ([string]::IsNullOrWhiteSpace($Mode) -and (-not $DotnetArgs -or $DotnetArgs.Length -eq 0)) {
        throw "Mode or DotnetArgs is required for run-dotnet. Example: -Mode one"
    }

    $dotnetArgsToRun = @("run", "--no-launch-profile", "--project", $DotnetProjectPath, "--")

    if (-not [string]::IsNullOrWhiteSpace($Mode)) {
        $dotnetArgsToRun += @("--mode", $Mode)

        if ($Mode -eq "wait") {
            $dotnetArgsToRun += @("--wait-seconds", $WaitSeconds)
        }

        if ($Mode -eq "manual") {
            if ([string]::IsNullOrWhiteSpace($CardId)) {
                throw "-CardId is required for manual mode. Example: -Mode manual -CardId abc123"
            }
            $dotnetArgsToRun += @("--card-id", $CardId)
        }

        if ($Mode -eq "agent") {
            if ([string]::IsNullOrWhiteSpace($CardId)) {
                throw "-CardId is required for agent mode. Example: -Mode agent -CardId abc123 -BoardId xyz -WorkspacePath C:\repo"
            }
            $dotnetArgsToRun += @("--card-id", $CardId)

            $resolvedBoardId = if (-not [string]::IsNullOrWhiteSpace($BoardId)) { $BoardId } else { $env:BoardId }
            if ([string]::IsNullOrWhiteSpace($resolvedBoardId)) {
                throw "-BoardId or BoardId env var is required for agent mode."
            }
            $dotnetArgsToRun += @("--board-id", $resolvedBoardId)

            $resolvedWorkspace = if (-not [string]::IsNullOrWhiteSpace($WorkspacePath)) { $WorkspacePath } else { $env:AgentWorkspacePath }
            if ([string]::IsNullOrWhiteSpace($resolvedWorkspace)) {
                throw "-WorkspacePath or AgentWorkspacePath env var is required for agent mode."
            }
            $dotnetArgsToRun += @("--workspace", $resolvedWorkspace)
        }
    }

    if ($DotnetArgs -and $DotnetArgs.Length -gt 0) {
        $dotnetArgsToRun += $DotnetArgs
    }

    if ($RunTimeoutSeconds -le 0) {
        & dotnet @dotnetArgsToRun
        exit $LASTEXITCODE
    }

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()

    try {
        $process = Start-Process -FilePath "dotnet" -ArgumentList $dotnetArgsToRun -PassThru -NoNewWindow -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

        try {
            $null = $process | Wait-Process -Timeout $RunTimeoutSeconds -ErrorAction Stop
        }
        catch {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue

            $stdout = if (Test-Path -Path $stdoutPath) { Get-Content -Path $stdoutPath -Raw } else { "" }
            $stderr = if (Test-Path -Path $stderrPath) { Get-Content -Path $stderrPath -Raw } else { "" }

            if (-not [string]::IsNullOrWhiteSpace($stdout)) {
                Write-Host $stdout
            }

            if (-not [string]::IsNullOrWhiteSpace($stderr)) {
                Write-Error $stderr
            }

            Write-Error "dotnet run timed out after $RunTimeoutSeconds seconds."
            exit 124
        }

        $stdout = if (Test-Path -Path $stdoutPath) { Get-Content -Path $stdoutPath -Raw } else { "" }
        $stderr = if (Test-Path -Path $stderrPath) { Get-Content -Path $stderrPath -Raw } else { "" }

        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout
        }

        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Error $stderr
        }

        exit $process.ExitCode
    }
    finally {
        if (Test-Path -Path $stdoutPath) {
            Remove-Item -Path $stdoutPath -ErrorAction SilentlyContinue
        }

        if (Test-Path -Path $stderrPath) {
            Remove-Item -Path $stderrPath -ErrorAction SilentlyContinue
        }
    }
}
