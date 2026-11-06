<#  Removes the remote support client from this machine.
    Run from an ELEVATED PowerShell:  powershell -ExecutionPolicy Bypass -File .\Uninstall-Client.ps1  #>
param([string]$InstallDir)
$ErrorActionPreference = "SilentlyContinue"

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }

# The installer names the folder and the shortcut after the tenant's own company, so the
# uninstaller has to work the same names out rather than assume any one of them. It reads the
# branding sitting next to it; if that is missing these fallbacks must match the ones in
# Install-Client.ps1 exactly, or the uninstaller looks in a folder the installer never used and
# reports success having removed nothing. They are deliberately unbranded: the publisher is The
# Remote Support Program, but no customer should see the publisher's name where their own IT
# company's belongs. Installs predating all of this are found by the legacy paths further down.
$companyName = 'Remote Support'
$productName = 'Remote Support'
$brandFile = Join-Path $here 'branding\brand.json'
if (Test-Path $brandFile) {
    try {
        $brand = Get-Content $brandFile -Raw | ConvertFrom-Json
        if ($brand.companyName) { $companyName = [string]$brand.companyName }
        if ($brand.productName) { $productName = [string]$brand.productName }
        elseif ($brand.companyName) { $productName = "$companyName Remote Support" }
    } catch { }
}

function Get-SafeName([string]$text) {
    foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $text = $text.Replace($c, ' ') }
    return ($text -replace '\s+', ' ').Trim()
}

if (-not $InstallDir) {
    $InstallDir = Join-Path (Join-Path $env:ProgramFiles (Get-SafeName $companyName)) 'Remote Support'
    # A machine installed before the branding change, or with a since-renamed tenant, is still at
    # the old fixed path. Removing nothing and reporting success is the worst outcome here.
    if (-not (Test-Path $InstallDir)) {
        $legacyDir = Join-Path $env:ProgramFiles 'Anywhere IT\Remote Support'
        if (Test-Path $legacyDir) { $InstallDir = $legacyDir }
    }
}

sc.exe stop RemoteSupportAgent | Out-Null
sc.exe delete RemoteSupportAgent | Out-Null
Get-Process -Name 'RemoteSupport.Client','RemoteSupport.Host','RemoteSupport.Agent' | Stop-Process -Force
Start-Sleep -Milliseconds 800

# Both the current value and the one used before the business was registered as The Remote
# Support Program. A missed Run entry starts a tray app whose files have just been deleted.
Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TheRemoteSupportProgram'
Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AnywhereITRemoteSupport'

# Both names: the branded one this install created, and 'Anywhere IT Support.lnk' from installs
# that predate the branding change. Leaving a dead shortcut on the desktop is what a customer
# notices about an uninstall.
$shortcutDirs = @(
    (Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs'),
    ([Environment]::GetFolderPath('CommonDesktopDirectory'))
)
$shortcutNames = @(((Get-SafeName $productName) + '.lnk'), 'Anywhere IT Support.lnk') | Select-Object -Unique
foreach ($dir in $shortcutDirs) {
    foreach ($name in $shortcutNames) { Remove-Item (Join-Path $dir $name) -Force }
}

Remove-Item $InstallDir -Recurse -Force

# Keep the traces. Uninstall-and-reinstall is the commonest troubleshooting step, so deleting
# this folder wholesale destroyed host-trace.log - the only record of whatever was being chased.
# It cost us the evidence for the ad-hoc input bug on 2026-09-09 (docs/BUGS.md).
$rsData = "$env:ProgramData\RemoteSupport"
$keep = "$env:ProgramData\RemoteSupport-logs"
$logs = @(Get-ChildItem $rsData -Filter '*.log' -File -ErrorAction SilentlyContinue)
if ($logs.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $keep | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($l in $logs) {
        Copy-Item $l.FullName (Join-Path $keep ("{0}-{1}{2}" -f $l.BaseName, $stamp, $l.Extension)) -Force
    }
    Write-Host "Kept $($logs.Count) log file(s) in $keep" -ForegroundColor Yellow
}
Remove-Item $rsData -Recurse -Force

Write-Host "Removed." -ForegroundColor Green
