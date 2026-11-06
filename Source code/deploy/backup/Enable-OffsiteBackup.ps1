# =====================================================================================
# On Clint's laptop, IN THE OFFICE, after Install-BackupPull.ps1 has run once on the office PC:
# let that PC's key fetch the live server's backups (deploy/backup, 2026-10-08).
#
#   powershell -ExecutionPolicy Bypass -File deploy\backup\Enable-OffsiteBackup.ps1
#
# In the office, because the server learns the office's fixed address from THIS connection and
# lets the key in from that address only. It asks you to confirm the address before using it.
# Asks for your key's passphrase three times.
# =====================================================================================
param(
    [string]$Server = "154.65.110.136",
    [string]$User = "ubuntu",
    [string]$Share = "\\192.168.1.33\anywhereit\Backups\RemoteSupport",
    [string]$OfficeIp
)
$ErrorActionPreference = "Stop"
$target = "$User@$Server"
$pub = Join-Path $Share "rsp-backup-key.pub"
if (-not (Test-Path $pub)) { throw "No $pub - run Install-BackupPull.ps1 on the office PC first." }
if ((Get-Content $pub -Raw) -notmatch '^ssh-ed25519 ') { throw "$pub is not an ed25519 public key." }

if (-not $OfficeIp) {
    Write-Host "== Asking the server which address you are connecting from ==  (type your key's passphrase now)" -ForegroundColor Cyan
    $OfficeIp = ((ssh $target 'echo $SSH_CLIENT') -split ' ')[0].Trim()
    if ($OfficeIp -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw "Could not read the address (got '$OfficeIp')." }
    $yes = Read-Host "The server sees you at $OfficeIp. Is that the office's FIXED address? (y/n)"
    if ($yes -ne 'y') { throw "Not confirmed. Run it again from the office, or give the address: -OfficeIp 1.2.3.4" }
}

Write-Host "== Uploading ==  (passphrase again)" -ForegroundColor Cyan
scp $pub (Join-Path $PSScriptRoot "rsp-backup-serve.sh") (Join-Path $PSScriptRoot "enable-offsite.sh") "${target}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw "upload failed" }

Write-Host "== Setting up the server ==  (passphrase again)" -ForegroundColor Cyan
$out = ssh $target "sed -i 's/\r$//' /tmp/enable-offsite.sh && sudo bash /tmp/enable-offsite.sh $OfficeIp /tmp/rsp-backup-key.pub /tmp/rsp-backup-serve.sh; rc=`$?; rm -f /tmp/enable-offsite.sh /tmp/rsp-backup-serve.sh /tmp/rsp-backup-key.pub; exit `$rc"
$code = $LASTEXITCODE
$out | Where-Object { $_ -notmatch '^FINGERPRINT ' } | ForEach-Object { Write-Host $_ }
if ($code -ne 0) { throw "the server-side step failed - see above" }
$fp = ($out | Where-Object { $_ -match '^FINGERPRINT ' }) -replace '^FINGERPRINT ', ''
if (-not $fp) { throw "the server did not report its fingerprint" }
Set-Content (Join-Path $Share "server-fingerprint.txt") $fp -Encoding ASCII
Write-Host "`nDone. The server lets that key in from $OfficeIp only, to list and fetch backups." -ForegroundColor Green
Write-Host "Last step, on the office PC:  powershell -ExecutionPolicy Bypass -File Install-BackupPull.ps1 -Finish"
