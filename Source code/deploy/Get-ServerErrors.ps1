# =====================================================================================
# Get-ServerErrors.ps1 - the platform's recent errors from the live server, into one file on the Desktop
# (2026-10-16: a pilot link answered HTTP 500 and the cause is only in the server's log).
#
#   powershell -ExecutionPolicy Bypass -File deploy\Get-ServerErrors.ps1
#   ... -Server 154.65.110.136 -Hours 48
#
# Reads only: journalctl for the platform's service, the lines around every error and the requests just before
# it. Link codes (/pilot/..., /account/setup?...) and anything that looks like a key are blanked before the file
# is written, so the file can be handed to Claude as it is. Nothing on the server is changed.
# =====================================================================================
param([string]$Server = "154.65.110.136", [string]$User = "ubuntu", [int]$Hours = 24)

if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw "ssh is not on PATH. Install the Windows OpenSSH Client (Settings > Optional features)."
}

# Sent as base64: PowerShell 5.1 mangles double quotes in an ssh argument (Rotate-Keys.ps1 found it).
$script = @"
journalctl -u remotesupport-platform --since '-$Hours hours' --no-pager -o short-iso \
  | grep -a -i -E -B12 -A45 'fail: |crit: |unhandled exception|exception: ' \
  | sed -E -e 's#(/pilot/)[0-9A-Za-z]{8,}#\1<token>#g' \
           -e 's#([?&](token|t|code|key)=)[^ &"]+#\1<hidden>#gi' \
           -e 's#[0-9A-Fa-f]{40,}#<long-hex>#g' \
  | tail -n 400
echo
echo '--- service status ---'
systemctl status remotesupport-platform --no-pager | head -n 12
"@
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script.Replace("`r`n", "`n")))
$out = Join-Path ([Environment]::GetFolderPath("Desktop")) "server-errors.txt"
Write-Host "Reading the last $Hours hours of errors from $Server ..."
ssh "$User@$Server" "echo $b64 | base64 -d | sudo bash" | Out-File -FilePath $out -Encoding utf8
if ($LASTEXITCODE -ne 0) { Write-Host "ssh did not finish (exit $LASTEXITCODE)." -ForegroundColor Red }
$n = (Get-Content $out | Measure-Object -Line).Lines
Write-Host "Written: $out ($n lines). Tell Claude it is there." -ForegroundColor Green
