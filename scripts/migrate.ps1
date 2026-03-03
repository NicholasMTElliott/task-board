param(
    [ValidateSet("migrate", "info", "validate")]
    [string]$Action = "migrate",
    [string]$EnvFile = ".env.local",
    [string]$MigrationsPath = "db/migrations",
    [string]$FlywayImage = "redgate/flyway:latest"
)

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI not found on PATH. Install Docker Desktop and retry."
}

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

if (-not (Test-Path -Path $MigrationsPath)) {
    throw "Migrations path '$MigrationsPath' not found."
}

$connectionValue = $values["NEON_DATABASE_URL"]

if (-not ($connectionValue.StartsWith("postgres://", [System.StringComparison]::OrdinalIgnoreCase) -or $connectionValue.StartsWith("postgresql://", [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "scripts/migrate.ps1 expects NEON_DATABASE_URL in URI format (postgresql://...)."
}

$uri = [Uri]$connectionValue

$username = ""
$password = ""

if (-not [string]::IsNullOrWhiteSpace($uri.UserInfo)) {
    $parts = $uri.UserInfo -split ":", 2
    $username = [Uri]::UnescapeDataString($parts[0])
    if ($parts.Length -eq 2) {
        $password = [Uri]::UnescapeDataString($parts[1])
    }
}

$port = if ($uri.IsDefaultPort) { 5432 } else { $uri.Port }
$database = $uri.AbsolutePath.TrimStart('/')

$query = $uri.Query
if (-not [string]::IsNullOrWhiteSpace($query)) {
    $query = $query.TrimStart('?')
}

$jdbcUrl = "jdbc:postgresql://$($uri.Host):$port/$database"
if (-not [string]::IsNullOrWhiteSpace($query)) {
    $jdbcUrl = $jdbcUrl + "?" + $query
}

$migrationsAbsolutePath = (Resolve-Path -Path $MigrationsPath).Path

$dockerArgs = @(
    "run",
    "--rm",
    "-v",
    "${migrationsAbsolutePath}:/flyway/sql",
    $FlywayImage,
    "-url=$jdbcUrl",
    "-user=$username",
    "-password=$password",
    "-locations=filesystem:/flyway/sql",
    "-connectRetries=3",
    $Action
)

Write-Host "Running: docker flyway $Action"
& docker @dockerArgs
exit $LASTEXITCODE
