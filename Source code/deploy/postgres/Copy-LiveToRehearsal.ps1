# =====================================================================================
# Make the rehearsal server a copy of the live one - for rehearsing the PostgreSQL switch (2026-10-09).
#
#   powershell -ExecutionPolicy Bypass -File deploy\postgres\Copy-LiveToRehearsal.ps1 -Rehearsal 1.2.3.4
#
# Asks for your key's passphrase four times:
#   1. on LIVE: the newest nightly backup is encrypted again with a one-time passphrase made up here
#      (the live backup passphrase never leaves the live server; nothing else there changes);
#   2. that file is copied down to this PC, and 3. up to the rehearsal server;
#   4. on the REHEARSAL server: outgoing mail is blocked and proved blocked, then the copy is put in
#      place of its own data and the platform started on it - SQLite, as live is today.
# The one-time passphrase exists only in this window's memory. The file on this PC is deleted at the end.
#
#  -Rehearsal  the rehearsal server's address. Refused if it is the live server.
#  -Live       default 154.65.110.136
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Rehearsal,
    [string]$Live = "154.65.110.136",
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
$liveIps = @("154.65.110.136", $Live)
try { $liveIps += [System.Net.Dns]::GetHostAddresses("theremotesupportprogram.com") | ForEach-Object { $_.IPAddressToString } } catch { }
if ($Rehearsal -in $liveIps) { throw "$Rehearsal is the LIVE server. The rehearsal must be a separate, throwaway server." }
foreach ($f in @("rehearsal-export.sh", "rehearsal-restore.sh")) {
    if (-not (Test-Path (Join-Path $PSScriptRoot $f))) { throw "$f is missing beside this script." }
}

# Letters and digits only, so it can sit in single quotes on the far side.
$bytes = New-Object byte[] 32
(New-Object System.Security.Cryptography.RNGCryptoServiceProvider).GetBytes($bytes)
$otp = ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', '')
$local = Join-Path $env:TEMP "rsp-for-rehearsal.gpg"

# A script with OTP='...' in front of it, written to a file only its owner can read, run, and removed.
function Invoke-WithOtp([string]$Target, [string]$Script) {
    $remote = "umask 077; tr -d '\r' > /tmp/rsp-step.sh && sudo bash /tmp/rsp-step.sh; rc=`$?; rm -f /tmp/rsp-step.sh; exit `$rc"
    $ErrorActionPreference = "Continue"
    @("OTP='$otp'") + (Get-Content (Join-Path $PSScriptRoot $Script)) | ssh $Target $remote | Out-Host
    return $LASTEXITCODE
}

try {
    Write-Host "== 1 of 4: on LIVE, the newest backup made ready ==  (type your key's passphrase now)" -ForegroundColor Cyan
    if ((Invoke-WithOtp "$User@$Live" "rehearsal-export.sh") -ne 0) { throw "the live server could not prepare the copy - see above" }

    Write-Host "== 2 of 4: copying it down to this PC ==  (passphrase again)" -ForegroundColor Cyan
    if (Test-Path $local) { Remove-Item $local -Force }
    scp "${User}@${Live}:/tmp/rsp-for-rehearsal.gpg" $local
    if ($LASTEXITCODE -ne 0) { throw "could not copy it from the live server" }

    Write-Host "== 3 of 4: copying it up to the rehearsal server ==  (passphrase again)" -ForegroundColor Cyan
    scp $local "${User}@${Rehearsal}:/tmp/rsp-for-rehearsal.gpg"
    if ($LASTEXITCODE -ne 0) { throw "could not copy it to $Rehearsal" }

    Write-Host "== 4 of 4: on the REHEARSAL server, mail blocked and the copy put in place ==  (passphrase again)" -ForegroundColor Cyan
    if ((Invoke-WithOtp "$User@$Rehearsal" "rehearsal-restore.sh") -ne 0) { throw "the rehearsal server stopped - see above" }
    Write-Host "`nThe rehearsal server is a copy of live: https://rehearsal.theremotesupportprogram.com" -ForegroundColor Green
}
finally {
    if (Test-Path $local) { Remove-Item $local -Force }
    $otp = $null
}
