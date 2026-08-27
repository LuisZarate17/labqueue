#Requires -Version 5.1
<#
.SYNOPSIS
    Applies EF Core migrations to the local compose database or to Neon.

.DESCRIPTION
    Smart App Control is enforcing on this machine and blocks freshly built unsigned
    assemblies with 0x800711C7, so `dotnet ef` cannot run on the Windows host. It runs
    inside mcr.microsoft.com/dotnet/sdk:10.0 instead, with source piped in as a tar
    archive - never a mounted Windows tree, because Windows bin/obj artifacts break the
    Linux restore. Same mechanism as scripts/dev-test.ps1.

    Migrations are deliberately NOT applied at application startup. Auto-migrating on boot
    would mean a later migration silently applying DDL to the hosted database on the next
    deploy, and the hosted database is not where schema changes get decided.

    The connection string is passed to the container as an environment variable and is
    never written to the console or to disk.

.PARAMETER Target
    local - the docker-compose database, reached at host.docker.internal:5432.
    neon  - the hosted database. Reads LABQUEUE_MIGRATION_CONNECTION from .env, which is
            gitignored. Use Neon's DIRECT (non-pooled) endpoint here: DDL through the
            pooler is a bad trade for no benefit.

.PARAMETER Action
    update - apply all pending migrations (default).
    list   - list migrations and show which are applied.
    script - write an idempotent SQL script to stdout without touching the database.

.EXAMPLE
    ./scripts/db-migrate.ps1 -Target local
    ./scripts/db-migrate.ps1 -Target neon -Action list
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('local', 'neon')]
    [string]$Target,

    [ValidateSet('update', 'list', 'script')]
    [string]$Action = 'update',

    [string]$Image = 'mcr.microsoft.com/dotnet/sdk:10.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$envFile    = Join-Path $repoRoot '.env'
$nugetCache = 'labqueue-nuget'
$toolCache  = 'labqueue-dotnet-tools'
$work       = Join-Path $env:TEMP ("labqueue-migrate-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$tarPath    = Join-Path $work 'src.tar'
$runPath    = Join-Path $work 'run.sh'

# ---------------------------------------------------------------- .env
function Get-DotEnvValue([string]$name) {
    if (-not (Test-Path $envFile)) { return $null }
    foreach ($line in Get-Content $envFile) {
        if ($line -match "^\s*$([regex]::Escape($name))\s*=\s*(.*)$") {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $null
}

# ---------------------------------------------------------------- connection string
if ($Target -eq 'local') {
    $password = Get-DotEnvValue 'POSTGRES_PASSWORD'
    if (-not $password) { $password = 'labqueue_dev' }

    # host.docker.internal, not localhost: inside the SDK container "localhost" is the
    # container itself, and compose publishes Postgres on the host.
    $connectionString = "Host=host.docker.internal;Port=5432;Database=labqueue;Username=labqueue;Password=$password"
    $describe = 'local compose database (host.docker.internal:5432)'
}
else {
    $connectionString = Get-DotEnvValue 'LABQUEUE_MIGRATION_CONNECTION'
    if (-not $connectionString) {
        throw "LABQUEUE_MIGRATION_CONNECTION is not set in $envFile. Copy .env.example to .env and fill it in with Neon's DIRECT (non-pooled) connection string."
    }

    # Echo the host only, never the credentials.
    $hostOnly = if ($connectionString -match 'Host=([^;]+)') { $Matches[1] } else { '(unparsed)' }
    $describe = "Neon ($hostOnly)"
}

# Design-time only. Program.cs refuses to start on a Jwt:Key under 32 bytes, and `dotnet ef`
# builds the host to find the DbContext, so it needs one. This value signs nothing.
$designTimeJwtKey = 'design-time-only-key-not-used-to-sign-anything-0123456789'

switch ($Action) {
    'update' { $efCommand = 'dotnet ef database update --project src/LabQueue.Core --startup-project src/LabQueue.Api' }
    'list'   { $efCommand = 'dotnet ef migrations list --project src/LabQueue.Core --startup-project src/LabQueue.Api' }
    'script' { $efCommand = 'dotnet ef migrations script --idempotent --project src/LabQueue.Core --startup-project src/LabQueue.Api' }
}

New-Item -ItemType Directory -Path $work -Force | Out-Null

$template = @'
set -uo pipefail
mkdir -p /src
tar -xf /tmp/src.tar -C /src
cd /src

dotnet restore src/LabQueue.Api/LabQueue.Api.csproj || exit 1

export PATH="$PATH:/root/.dotnet/tools"
if ! command -v dotnet-ef >/dev/null 2>&1; then
  echo ">>> installing dotnet-ef into the cached tool volume"
  dotnet tool install --global dotnet-ef --version 10.* || exit 1
fi

@@COMMAND@@
'@

$script = $template.Replace('@@COMMAND@@', $efCommand)
[IO.File]::WriteAllText($runPath, ($script -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

# ---------------------------------------------------------------- pack source
Write-Host ">>> packing source (bin/obj/.git excluded)" -ForegroundColor Cyan

$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $tar)) { throw "Windows tar not found at $tar" }

& $tar -cf $tarPath -C $repoRoot `
    --exclude='*/bin' --exclude='*/bin/*' `
    --exclude='*/obj' --exclude='*/obj/*' `
    --exclude='./.git' --exclude='./.git/*' `
    --exclude='./TestResults' --exclude='*/TestResults/*' `
    --exclude='./.env' `
    .
if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

$leaked = (& $tar -tf $tarPath | Select-String -Pattern '/(bin|obj)/' | Measure-Object).Count
if ($leaked -gt 0) { throw "$leaked bin/obj entries leaked into the archive - the Linux restore will break" }

docker volume create $nugetCache | Out-Null
docker volume create $toolCache  | Out-Null

# ---------------------------------------------------------------- run
Write-Host ">>> $Action against $describe" -ForegroundColor Cyan

$cid = & docker create `
    --add-host host.docker.internal:host-gateway `
    --env "ConnectionStrings__LabQueue=$connectionString" `
    --env "Jwt__Key=$designTimeJwtKey" `
    --env DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    --env DOTNET_NOLOGO=1 `
    --volume "${nugetCache}:/root/.nuget/packages" `
    --volume "${toolCache}:/root/.dotnet/tools" `
    $Image bash /tmp/run.sh
if ($LASTEXITCODE -ne 0) { throw "docker create failed" }
$cid = $cid.Trim()

try {
    & docker cp $tarPath "${cid}:/tmp/src.tar" | Out-Null
    & docker cp $runPath "${cid}:/tmp/run.sh"  | Out-Null

    & docker start -a $cid
    $exitCode = & docker inspect -f '{{.State.ExitCode}}' $cid
}
finally {
    & docker rm -f $cid 2>&1 | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

$exitCode = [int]$exitCode
if ($exitCode -eq 0) {
    Write-Host ">>> PASS" -ForegroundColor Green
}
else {
    Write-Host ">>> FAIL (exit $exitCode)" -ForegroundColor Red
}
exit $exitCode
