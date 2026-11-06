# =====================================================================================
# The way back: a server on PostgreSQL returns to its SQLite file - step 4 of HANDOVER section 118.
#
#   powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-BackToSqlite.ps1 -Server 154.65.110.136
#
# Two connections, each asking for your key's passphrase:
#   1. LOOK: switch-back-to-sqlite.sh --check compares PostgreSQL with the SQLite file and says which
#      tables have changed since the switch - what the platform would lose by going back. Nothing changes.
#   2. You type BACK. Then --go: a backup of PostgreSQL, the platform stopped, the database renamed (never
#      dropped), the PostgreSQL setting taken out, and the platform started on the SQLite file.
# Everything printed is saved in deploy\reports\switch-back-<server>-<time>.log as well.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
$script = Join-Path $PSScriptRoot "switch-back-to-sqlite.sh"
if (-not (Test-Path $script)) { throw "switch-back-to-sqlite.sh is missing beside this script." }
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) { throw "ssh is not on PATH (Settings > Optional features > OpenSSH Client)." }
$target = "$User@$Server"
$reports = Join-Path (Split-Path $PSScriptRoot -Parent) "reports"
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$log = Join-Path $reports ("switch-back-{0}-{1}.log" -f $Server, (Get-Date -Format "yyyyMMdd-HHmmss"))

function Invoke-Back([string]$Mode) {
    $remote = "tr -d '\r' > /tmp/rsp-switch-back.sh && sudo bash /tmp/rsp-switch-back.sh $Mode; rc=`$?; rm -f /tmp/rsp-switch-back.sh; exit `$rc"
    $ErrorActionPreference = "Continue"
    Get-Content $script | ssh $target $remote | Tee-Object -FilePath $log -Append | Out-Host
    return $LASTEXITCODE
}

Write-Host "== 1 of 2: looking - nothing is changed ==  (type your key's passphrase now)" -ForegroundColor Cyan
$rc = Invoke-Back "--check"
if ($rc -ne 0) {
    Write-Host "`nThe check stopped - the reason is above. Nothing was changed. Saved: $log" -ForegroundColor Red
    exit 1
}
Write-Host ""
$answer = Read-Host "Type BACK to put $Server back on its SQLite file now (anything else stops here)"
if ($answer -cne "BACK") { Write-Host "Nothing was changed."; exit 1 }

Write-Host "== 2 of 2: going back ==  (type your key's passphrase again now)" -ForegroundColor Cyan
$rc = Invoke-Back "--go"
if ($rc -ne 0) {
    Write-Host "`nIt stopped - the reason is above; the platform was put back on PostgreSQL. Saved: $log" -ForegroundColor Red
    exit 1
}
Write-Host "`nBack on SQLite. Saved: $log" -ForegroundColor Green
