# =====================================================================================
# Move a server onto PostgreSQL and its own disks - step 4 of HANDOVER section 118 (2026-10-09).
#
#   powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-ToPostgres.ps1 -Server 154.65.110.136
#
# Two connections, each asking for your key's passphrase:
#   1. LOOK: switch-to-postgres.sh --check finds the disks and says what it will do. Nothing changes.
#   2. You type SWITCH. Then --go: the platform stops for a minute or two and comes back on PostgreSQL.
#      If anything fails it is started again on SQLite, as it was.
# Everything printed is saved in deploy\reports\switch-<server>-<time>.log as well.
#
#  -FilesGB, -DbGB   the sizes of the two volumes as the xneelo panel shows them (live: 50 and 20)
#  -User             SSH user with sudo (default ubuntu)
# Before this: a normal Publish-Platform.ps1 (Step A), so the server has copy-to-postgres.
# The way back: Switch-BackToSqlite.ps1.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = "ubuntu",
    [int]$FilesGB = 50,
    [int]$DbGB = 20
)
$ErrorActionPreference = "Stop"
$script = Join-Path $PSScriptRoot "switch-to-postgres.sh"
if (-not (Test-Path $script)) { throw "switch-to-postgres.sh is missing beside this script." }
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) { throw "ssh is not on PATH (Settings > Optional features > OpenSSH Client)." }
$target = "$User@$Server"
$reports = Join-Path (Split-Path $PSScriptRoot -Parent) "reports"
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$log = Join-Path $reports ("switch-{0}-{1}.log" -f $Server, (Get-Date -Format "yyyyMMdd-HHmmss"))

# The script goes over on standard input and is written to a file first, so nothing it runs can read the
# rest of it as input. No double quotes in the remote command: Windows PowerShell mangles them for ssh.
function Invoke-Switch([string]$Mode) {
    $remote = "tr -d '\r' > /tmp/rsp-switch.sh && sudo bash /tmp/rsp-switch.sh $Mode --files-gb $FilesGB --db-gb $DbGB; rc=`$?; rm -f /tmp/rsp-switch.sh; exit `$rc"
    $ErrorActionPreference = "Continue"
    Get-Content $script | ssh $target $remote | Tee-Object -FilePath $log -Append | Out-Host
    return $LASTEXITCODE
}

Write-Host "== 1 of 2: looking - nothing is changed ==  (type your key's passphrase now)" -ForegroundColor Cyan
$rc = Invoke-Switch "--check"
if ($rc -ne 0) {
    Write-Host "`nThe check stopped - the reason is above. Nothing was changed. Saved: $log" -ForegroundColor Red
    exit 1
}
Write-Host ""
$answer = Read-Host "Type SWITCH to stop the platform on $Server and move it to PostgreSQL now (anything else stops here)"
if ($answer -cne "SWITCH") { Write-Host "Not switched. Nothing was changed."; exit 1 }

Write-Host "== 2 of 2: switching ==  (type your key's passphrase again now)" -ForegroundColor Cyan
$rc = Invoke-Switch "--go"
if ($rc -ne 0) {
    Write-Host "`nThe switch stopped - the reason is above; the platform was put back on SQLite. Saved: $log" -ForegroundColor Red
    exit 1
}
Write-Host "`nSwitched to PostgreSQL. Now check the portal, a ticket and the machines by hand. Saved: $log" -ForegroundColor Green
