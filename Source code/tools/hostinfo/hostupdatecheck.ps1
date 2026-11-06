# Is the host staged at C:\Temp\hostupdate the one built on the dev machine? Run through
# tools/runscript after tools/pushfile has put it there and before swaphost.ps1 renames it in.
# Update $want from the sha256 of the published exe every time a new host is staged.
$ErrorActionPreference='Stop'
$p='C:\Temp\hostupdate\RemoteSupport.Host.exe'
$want='76590A1B89159CC79A3C34217D6041DB64FCC46D6A9760393A58A514356FECDE'
if (-not (Test-Path $p)) { "not there yet"; exit 1 }
"size   : $((Get-Item $p).Length)"
$sw=[Diagnostics.Stopwatch]::StartNew()
$h=(Get-FileHash $p -Algorithm SHA256).Hash
"sha256 : $h  (hashed in $([int]$sw.Elapsed.TotalSeconds)s)"
if ($h -eq $want) { "MATCHES the build on the dev machine" } else { "DIFFERENT - do not install this"; exit 2 }
