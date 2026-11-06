# Do the installed binaries carry the "downloaded from the internet" mark?
#
# Windows attaches a Zone.Identifier alternate data stream to anything that came from a browser,
# and propagates it to files extracted from a marked zip. Launching such a file shows the
# "Open File - Security Warning / The publisher could not be verified" dialog - at every logon,
# for the tray app - and nothing starts until somebody clicks Run.
#
# Pure ASCII (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'
$dir = 'C:\Program Files\Anywhere IT\Remote Support'

Write-Output "=== Zone.Identifier on the installed files ==="
if (-not (Test-Path $dir)) { Write-Output "not installed at $dir"; exit 0 }

$marked = 0; $clean = 0
Get-ChildItem $dir -Recurse -Include *.exe,*.dll,*.ps1,*.bat -ErrorAction SilentlyContinue | ForEach-Object {
    $z = Get-Content -Path $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($z) {
        $marked++
        $zone = ($z | Where-Object { $_ -match '^ZoneId=' }) -replace 'ZoneId=',''
        Write-Output ("  MARKED  ZoneId={0}  {1}" -f $zone, $_.Name)
    }
    else { $clean++ }
}
Write-Output ""
Write-Output ("marked: {0}   clean: {1}" -f $marked, $clean)
Write-Output "(ZoneId=3 means 'from the internet'. That is what raises the security warning.)"

Write-Output ""
Write-Output "=== how the tray app is started at logon ==="
$run = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
if ($run) { $run.PSObject.Properties | Where-Object { $_.Value -match 'RemoteSupport' } | ForEach-Object { Write-Output ("  HKLM Run: {0} = {1}" -f $_.Name, $_.Value) } }
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match 'Remote|Support' } | ForEach-Object {
    Write-Output ("  Scheduled task: {0}  state={1}" -f $_.TaskName, $_.State)
}
exit 0
