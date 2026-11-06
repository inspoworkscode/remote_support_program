# Files orphaned tickets under the client that owns their machine.
#
#   .\backfill-clients.ps1            # show what would change, write nothing
#   .\backfill-clients.ps1 -Apply     # make the changes, after backing the database up
#
# Stop the platform server first. SQLite will not let two processes write at once, and a
# half-applied backfill is worse than none.

param(
    [switch]$Apply,
    [string]$Db = ""
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (Get-Process -Name 'RemoteSupport.Platform' -ErrorAction SilentlyContinue) {
    Write-Host "The platform server is running." -ForegroundColor Yellow
    Write-Host "Stop it first:  Stop-Process -Name RemoteSupport.Platform -Force" -ForegroundColor Yellow
    if ($Apply) { throw "Refusing to write to the database while the server has it open." }
    Write-Host "Continuing anyway - a dry run only reads." -ForegroundColor DarkGray
    Write-Host ""
}

$toolArgs = @()
if ($Db)    { $toolArgs += @('--db', $Db) }
if ($Apply) { $toolArgs += '--apply' }

dotnet run -c Release --project (Join-Path $here 'backfill-clients.csproj') -- @toolArgs
