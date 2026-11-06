<#
    Installs the Anywhere IT Remote Support client on ANOTHER machine on the office network,
    without touching that machine by hand.

    Run this from THIS laptop, in a normal (non-elevated) PowerShell:

        powershell -ExecutionPolicy Bypass -File .\Install-ClientRemotely.ps1 -Computer OFFICE-PC2 -Unattended

    You will be prompted for an administrator username and password FOR THE TARGET MACHINE.
    The prompt is Windows' own; the password is never typed into a chat, a file or a log.

    Requirements on the target machine:
      * on the same network as this laptop
      * PowerShell Remoting enabled  (on the target, from an elevated prompt: Enable-PSRemoting -Force)
      * Smart App Control OFF, or the unsigned app will be blocked (see docs/CODE-SIGNING.md)
#>
param(
    [Parameter(Mandatory = $true)][string]$Computer,
    [switch]$Unattended,
    [string]$SourceDir
)
$ErrorActionPreference = "Stop"

# $MyInvocation.MyCommand.Path is null inside a param() default, so resolve the package here instead.
if (-not $SourceDir) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $SourceDir = Join-Path (Split-Path -Parent $scriptDir) 'deploy-client'
}
if (-not (Test-Path (Join-Path $SourceDir 'RemoteSupport.Client.exe'))) {
    throw "Client package not found at $SourceDir. Run build.ps1 first, or pass -SourceDir."
}

Write-Host "== Target: $Computer ==" -ForegroundColor Cyan
# Plenty of Windows machines block ping while still being perfectly reachable, so a failed
# ping is a warning, not a reason to stop. WinRM below is the check that actually matters.
if (-not (Test-Connection -ComputerName $Computer -Count 2 -Quiet)) {
    Write-Warning "$Computer does not answer ping (often just its firewall). Continuing."
}
Write-Host "== Checking PowerShell Remoting ==" -ForegroundColor Cyan
try { Test-WSMan -ComputerName $Computer -ErrorAction Stop | Out-Null; Write-Host "   WinRM is listening." -ForegroundColor Green }
catch {
    Write-Host ""
    Write-Warning "PowerShell Remoting is not enabled on $Computer, so it cannot be installed remotely."
    Write-Warning "On $Computer, from an ELEVATED PowerShell, run:    Enable-PSRemoting -Force"
    Write-Warning "Then run this script again. Or copy the deploy-client folder across and run Install-Client.ps1 there."
    throw "WinRM unavailable on $Computer."
}

$cred = Get-Credential -Message "Administrator account on $Computer"

Write-Host "== Opening a remote session ==" -ForegroundColor Cyan
try { $session = New-PSSession -ComputerName $Computer -Credential $cred -ErrorAction Stop }
catch {
    Write-Host ""
    Write-Warning "Could not open a remote session. On $Computer, from an ELEVATED PowerShell, run:"
    Write-Warning "    Enable-PSRemoting -Force"
    Write-Warning "If the machines are in a workgroup rather than a domain, also run on THIS laptop:"
    Write-Warning "    Set-Item WSMan:\localhost\Client\TrustedHosts -Value '$Computer' -Force"
    throw
}

try {
    $stagingPath = Invoke-Command -Session $session -ScriptBlock {
        $p = Join-Path $env:ProgramData 'RemoteSupportProgram-Install'
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        $p
    }

    Write-Host "== Copying the client package across ==" -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $stagingPath -ToSession $session -Recurse -Force

    Write-Host "== Installing ==" -ForegroundColor Cyan
    $result = Invoke-Command -Session $session -ArgumentList $stagingPath, $Unattended.IsPresent -ScriptBlock {
        param($dir, $unattended)
        $script = Join-Path $dir 'Install-Client.ps1'
        $args = @('-ExecutionPolicy','Bypass','-File', $script)
        if ($unattended) { $args += '-Unattended' }
        & powershell.exe @args 2>&1 | Out-String
    }
    Write-Host $result

    $ok = Invoke-Command -Session $session -ScriptBlock {
        [bool](Get-Process -Name 'RemoteSupport.Client' -ErrorAction SilentlyContinue)
    }
    if ($ok) { Write-Host "`n$Computer is installed and the client is running." -ForegroundColor Green }
    else { Write-Warning "Installed, but the client is not running on $Computer. Check Smart App Control on that machine." }

    Invoke-Command -Session $session -ArgumentList $stagingPath -ScriptBlock { param($d) Remove-Item $d -Recurse -Force }
}
finally { if ($session) { Remove-PSSession $session } }
