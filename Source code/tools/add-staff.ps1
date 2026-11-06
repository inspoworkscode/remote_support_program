# =====================================================================================
# add-staff.ps1 - the four test staff accounts for internal testing (2026-09-28).
#
#   JasonTest    Supervisor   (a supervisor can do everything a technician and an office
#   ChantelTest  Supervisor    administrator can, so one role covers what was asked; a person
#                              holding several roles at once is roadmap 3.6.8, not built)
#   RyanTest     Technician
#   AnthonyTest  Technician
#
# Raises Anywhere IT to 7 seats first (Clint's choice): the four need a seat each, and 3 of 5 were
# in use. Asks for ONE password in a Windows prompt and gives it to all four - it is never typed
# into chat, and never written to disk by this script.
#
# Run from the repository folder:
#   powershell -ExecutionPolicy Bypass -File tools\add-staff.ps1
# =====================================================================================

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\RemoteSupport.Platform\bin\Release\net10.0\RemoteSupport.Platform.exe'
if (-not (Test-Path $exe)) {
    Write-Host "The platform has not been built. Run: dotnet build -c Release src\RemoteSupport.Platform" -ForegroundColor Red
    exit 1
}
$slug = 'anywhere-it'

$cred = Get-Credential -UserName 'staff' -Message 'One password for all four test staff accounts (at least 8 characters)'
if ($null -eq $cred) { Write-Host 'Cancelled.'; exit 1 }
$pw = $cred.GetNetworkCredential().Password
if ($pw.Length -lt 8) { Write-Host 'The password must be at least 8 characters.' -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host 'Seats:' -ForegroundColor Cyan
& $exe set-brand $slug seats 7

$people = @(
    @{ Email = 'jasontest@anywhereit.co.za';   Name = 'JasonTest';   Role = '--admin' },
    @{ Email = 'chanteltest@anywhereit.co.za'; Name = 'ChantelTest'; Role = '--admin' },
    @{ Email = 'ryantest@anywhereit.co.za';    Name = 'RyanTest';    Role = '' },
    @{ Email = 'anthonytest@anywhereit.co.za'; Name = 'AnthonyTest'; Role = '' }
)

Write-Host ''
Write-Host 'Accounts:' -ForegroundColor Cyan
$failed = 0
foreach ($p in $people) {
    $cliArgs = @('add-tech', $slug, $p.Email, $pw, $p.Name)
    if ($p.Role) { $cliArgs += $p.Role }
    & $exe @cliArgs
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
$pw = $null

Write-Host ''
Write-Host 'The team now:' -ForegroundColor Cyan
& $exe list-techs $slug

Write-Host ''
if ($failed -eq 0) { Write-Host 'All four added.' -ForegroundColor Green }
else { Write-Host "$failed could not be added - the reason is printed above." -ForegroundColor Yellow }
