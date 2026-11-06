# A push whose --dir lost its backslashes lands in a folder literally named "Temphostupdate"
# relative to the host's current directory. Find it, move the exe to C:\Temp\hostupdate, and
# verify it, so the push does not have to be repeated. Run through tools/runscript.
$ErrorActionPreference='SilentlyContinue'
$dest='C:\Temp\hostupdate'
$want='9AEF97857582F51E0B1DA1F82F4A824D9BE07C82BECB89BFEE838154E459BFB0'
$candidates=@(
  'C:\Program Files\Anywhere IT\Remote Support\Temphostupdate\RemoteSupport.Host.exe',
  'C:\Windows\System32\Temphostupdate\RemoteSupport.Host.exe',
  'C:\Temphostupdate\RemoteSupport.Host.exe',
  (Join-Path $dest 'RemoteSupport.Host.exe')
)
$found=$candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $found) {
  "not in any of the usual places; searching two levels down from C:\ and Program Files..."
  $found = Get-ChildItem 'C:\','C:\Program Files','C:\Windows' -Directory -Filter 'Temphostupdate' -Depth 2 |
           ForEach-Object { Join-Path $_.FullName 'RemoteSupport.Host.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $found) { "could not find the pushed host anywhere"; exit 1 }
"found  : $found"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$target=Join-Path $dest 'RemoteSupport.Host.exe'
if ($found -ne $target) { Move-Item $found $target -Force; Remove-Item (Split-Path $found) -Force -Recurse; "moved  : $target" }
"size   : $((Get-Item $target).Length)"
$h=(Get-FileHash $target -Algorithm SHA256).Hash
"sha256 : $h"
if ($h -eq $want) { "MATCHES the build on the dev machine" } else { "DIFFERENT - do not install this"; exit 2 }
