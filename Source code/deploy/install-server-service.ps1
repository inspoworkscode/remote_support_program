# Installs the Remote Support server as a Windows service on your VPS.
# Run from an elevated PowerShell in the folder containing RemoteSupport.Server.exe.
param(
    [int]$Port = 8080,
    [string]$ServiceName = "RemoteSupportServer"
)
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "RemoteSupport.Server.exe"
if (-not (Test-Path $exe)) { throw "RemoteSupport.Server.exe not found next to this script." }

Write-Host "Installing service '$ServiceName' -> $exe run" -ForegroundColor Cyan
sc.exe create $ServiceName binPath= "`"$exe`" run" start= auto DisplayName= "Remote Support Server" | Out-Null
sc.exe description $ServiceName "Coordination server for Remote Support (calls, chat, sessions, relay)." | Out-Null
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# open the firewall port
New-NetFirewallRule -DisplayName "Remote Support ($Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -ErrorAction SilentlyContinue | Out-Null

sc.exe start $ServiceName | Out-Null
Write-Host "Service started. Add your first technician:" -ForegroundColor Green
Write-Host "  .\RemoteSupport.Server.exe add-tech <username> <password> `"Display Name`""
Write-Host "Health check: http://localhost:$Port/health"
