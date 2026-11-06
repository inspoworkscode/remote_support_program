# Clears the "downloaded from the internet" mark from the installed client on THIS machine.
#
# Without it, Windows shows "Open File - Security Warning / The publisher could not be verified"
# every time the tray app starts at logon, naming our own exe - so it reads as our software being
# broken or unsafe, and nothing starts until somebody clicks Run.
#
# Install-Client.ps1 now does this at install time. This is for machines installed before that.
#
# Pure ASCII (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'
$dir = 'C:\Program Files\Anywhere IT\Remote Support'
if (-not (Test-Path $dir)) { Write-Output "not installed at $dir"; exit 0 }

$before = 0
Get-ChildItem $dir -Recurse -File | ForEach-Object {
    if (Get-Content -Path $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue) { $before++ }
}
Write-Output ("files carrying the mark before: " + $before)

Get-ChildItem $dir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
$hc = 'C:\ProgramData\HealthCheck'
if (Test-Path $hc) { Get-ChildItem $hc -Recurse -File | Unblock-File -ErrorAction SilentlyContinue }

$after = 0
Get-ChildItem $dir -Recurse -File | ForEach-Object {
    if (Get-Content -Path $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue) { $after++ }
}
Write-Output ("files carrying the mark after : " + $after)
Write-Output ""
if ($after -eq 0) { Write-Output "Cleared. The security warning will not appear at the next logon." }
else { Write-Output "Some files are still marked - check permissions." }
exit 0
