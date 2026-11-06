# =====================================================================================
# What the live server is and what is on it, in one read-only report.
#
#   powershell -ExecutionPolicy Bypass -File deploy\Get-ServerReport.ps1
#
# Connects once (type your key's passphrase), runs deploy\server-report.sh there, shows the
# report and saves it as deploy\reports\server-report-<date>.txt. It changes nothing on the
# server and prints no secret - keys and passphrases are only reported as "set" or "not set".
# =====================================================================================
param([string]$Server = "154.65.110.136", [string]$User = "ubuntu")
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "reports"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$file = Join-Path $out "server-report-$(Get-Date -Format yyyyMMdd-HHmm).txt"
Write-Host "Reading $Server ...  (type your key's passphrase now)" -ForegroundColor Cyan
# The script goes in on standard input; tr strips the Windows line endings PowerShell adds.
$report = Get-Content (Join-Path $PSScriptRoot "server-report.sh") | ssh "$User@$Server" "tr -d '\r' | bash -s"
if ($LASTEXITCODE -ne 0 -and -not $report) { throw "Could not read the server (could not connect, or the passphrase was refused)." }
$report | Set-Content $file -Encoding UTF8
$report | ForEach-Object { Write-Host $_ }
Write-Host "`nSaved: $file" -ForegroundColor Green
