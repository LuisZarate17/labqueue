#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the labqueue build and test suite inside the .NET SDK container.

.DESCRIPTION
    Smart App Control is enforcing on this machine and blocks freshly built unsigned
    assemblies from this repo with 0x800711C7, so dotnet build / ef / test cannot run on
    the Windows host. They run inside mcr.microsoft.com/dotnet/sdk:10.0 instead.

    Source is copied in as a tar archive rather than bind-mounted, because Windows bin/obj
    artifacts in a mounted tree break the Linux restore.

    Testcontainers starts Postgres as a *sibling* container on the host daemon, so this
    mounts the Docker socket and sets TESTCONTAINERS_HOST_OVERRIDE. That second part is
    load-bearing: Testcontainers publishes Postgres on a host port, and inside this
    container "localhost" is not the host.

    GitHub Actions needs none of this - ubuntu-latest runs the SDK and Docker directly.

.PARAMETER Unskip
    Strips the Skip argument off the Finding A concurrency test in the *extracted copy
    inside the container*. The working tree is never modified, so the committed state
    cannot accidentally be left un-skipped.

    Only useful while that test is committed skipped, which it no longer is - the exclusion
    constraint landed and it runs with the rest of the suite. Left in place because it is
    the mechanism for the next test committed failing-first, and because it refuses rather
    than silently no-ops when there is no Skip to strip. Reproducing Finding A now means
    reverting the constraint migration, not passing this flag.

.PARAMETER KeepResults
    Copies the container's TestResults directory out to ./TestResults before the container
    is removed, and forces the trx logger on so there is something to copy.

    Without this the .trx dies with the container in the finally block below, and with it
    every log line the API wrote - VSTest captures the app's console output into the trx and
    nowhere else, so nothing Serilog emits reaches this script's stdout. That is how the
    40P01 deadlock behind DECISIONS.md section 7 stayed invisible for as long as it did: the
    test reported "500 InternalServerError x50" and the exception explaining it was deleted
    microseconds later. Use this on any intermittent failure before trying to reason about it.

.EXAMPLE
    ./scripts/dev-test.ps1
    ./scripts/dev-test.ps1 -Task build
    ./scripts/dev-test.ps1 -Task ci        # rehearses the GitHub Actions sequence
    ./scripts/dev-test.ps1 -Filter Fifty_concurrent -Repeat 5
    ./scripts/dev-test.ps1 -KeepResults    # then read TestResults/*.trx for the server-side exception
#>
[CmdletBinding()]
param(
    [ValidateSet('test', 'build', 'restore', 'ci')]
    [string]$Task = 'test',

    [string]$Filter = '',

    [ValidateRange(1, 50)]
    [int]$Repeat = 1,

    [switch]$Unskip,

    [switch]$KeepResults,

    [string]$Configuration = 'Release',

    [string]$Image = 'mcr.microsoft.com/dotnet/sdk:10.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$reproFile  = 'tests/LabQueue.Tests/ConcurrentBookingTests.cs'
$nugetCache = 'labqueue-nuget'
$work       = Join-Path $env:TEMP ("labqueue-devtest-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$tarPath    = Join-Path $work 'src.tar'
$runPath    = Join-Path $work 'run.sh'

# Resolve tar explicitly. Launched from Git Bash, a bare "tar" picks up GNU tar from
# /usr/bin, which reads a "C:\..." path as a remote host spec and fails to resolve it.
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $tar)) { throw "Windows tar not found at $tar" }

New-Item -ItemType Directory -Path $work -Force | Out-Null

# ---------------------------------------------------------------- inner script
$filterArg = ''
if ($Filter -ne '') { $filterArg = "--filter 'FullyQualifiedName~$Filter'" }

$unskipBlock = '# (running the suite as committed - nothing is skipped)'

if ($Unskip) {
    $unskipBlock = @"
if [ ! -f "$reproFile" ]; then
  echo "ERROR: $reproFile not found - nothing to un-skip." >&2
  exit 3
fi
if ! grep -q 'Fact(Skip = "Finding A' "$reproFile"; then
  echo "ERROR: the Finding A skip attribute is not in $reproFile." >&2
  echo "       Refusing to run: a silent no-op here would look like a passing run." >&2
  exit 3
fi
sed -i 's/\[Fact(Skip = "Finding A[^"]*")\]/[Fact]/' "$reproFile"
echo ">>> un-skipped in the container copy - the working tree is untouched"
grep -n -B1 'Fifty_concurrent_bookings' "$reproFile" | head -4
"@
}

# -Task ci already logs a trx. -Task test does not, so -KeepResults has to turn it on or
# there would be nothing in the directory worth copying out.
$resultsArg = ''
if ($KeepResults) { $resultsArg = "--logger 'trx;LogFileName=test-results.trx' --results-directory TestResults" }

switch ($Task) {
    'restore' { $command = "dotnet restore labqueue.slnx" }
    'build'   { $command = "dotnet build labqueue.slnx -c $Configuration --nologo" }
    'test'    { $command = "dotnet test labqueue.slnx -c $Configuration --nologo $filterArg $resultsArg" }

    # The exact sequence .github/workflows/ci.yml runs, as a local rehearsal of the runner.
    'ci' {
        $command = @(
            'dotnet restore labqueue.slnx'
            "dotnet build labqueue.slnx -c $Configuration --no-restore"
            "dotnet test labqueue.slnx -c $Configuration --no-build --logger 'trx;LogFileName=test-results.trx' --results-directory TestResults"
        ) -join ' && '
    }
}

# Single-quoted here-string: nothing below is interpolated by PowerShell, so bash's own
# $ and " survive intact. The @@TOKEN@@ placeholders are substituted afterwards.
$template = @'
set -uo pipefail
mkdir -p /src
tar -xf /tmp/src.tar -C /src
cd /src

@@UNSKIP@@

overall=0
for i in $(seq 1 @@REPEAT@@); do
  if [ @@REPEAT@@ -gt 1 ]; then
    echo ""
    echo "=================== run $i of @@REPEAT@@ ==================="
  fi
  @@COMMAND@@
  rc=$?
  if [ @@REPEAT@@ -gt 1 ]; then echo "--- run $i exit code: $rc ---"; fi
  if [ $rc -ne 0 ]; then overall=$rc; fi
done
exit $overall
'@

$script = $template.
    Replace('@@UNSKIP@@',  $unskipBlock).
    Replace('@@REPEAT@@',  $Repeat.ToString()).
    Replace('@@COMMAND@@', $command)

[IO.File]::WriteAllText($runPath, ($script -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

# ---------------------------------------------------------------- pack source
# Excludes match the archive paths produced by "-C <root> ." (e.g. ./src/Api/bin/...).
Write-Host ">>> packing source (bin/obj/.git excluded)" -ForegroundColor Cyan
& $tar -cf $tarPath -C $repoRoot `
    --exclude='*/bin' --exclude='*/bin/*' `
    --exclude='*/obj' --exclude='*/obj/*' `
    --exclude='./.git' --exclude='./.git/*' `
    --exclude='./TestResults' --exclude='*/TestResults/*' `
    .
if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

$leaked = (& $tar -tf $tarPath | Select-String -Pattern '/(bin|obj)/' | Measure-Object).Count
if ($leaked -gt 0) { throw "$leaked bin/obj entries leaked into the archive - the Linux restore will break" }

docker volume create $nugetCache | Out-Null

# ---------------------------------------------------------------- run
$cid = & docker create `
    --volume /var/run/docker.sock:/var/run/docker.sock `
    --add-host host.docker.internal:host-gateway `
    --env TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
    --env DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    --env DOTNET_NOLOGO=1 `
    --volume "${nugetCache}:/root/.nuget/packages" `
    $Image bash /tmp/run.sh
if ($LASTEXITCODE -ne 0) { throw "docker create failed" }
$cid = $cid.Trim()

try {
    & docker cp $tarPath "${cid}:/tmp/src.tar" | Out-Null
    & docker cp $runPath "${cid}:/tmp/run.sh"  | Out-Null

    Write-Host ">>> $command" -ForegroundColor Cyan
    & docker start -a $cid
    $exitCode = & docker inspect -f '{{.State.ExitCode}}' $cid
}
finally {
    # Before the container goes, not after. This has to run even when the tests failed -
    # especially then, since a failing run is the only one worth reading.
    if ($KeepResults) {
        $destination = Join-Path $repoRoot 'TestResults'
        Remove-Item -Recurse -Force $destination -ErrorAction SilentlyContinue
        & docker cp "${cid}:/src/TestResults" $destination 2>&1 | Out-Null

        if (Test-Path $destination) {
            Write-Host ">>> results copied to $destination" -ForegroundColor Cyan
        }
        else {
            # -Task build and -Task restore produce none, which is not an error.
            Write-Host ">>> no TestResults in the container to copy" -ForegroundColor DarkGray
        }
    }

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
