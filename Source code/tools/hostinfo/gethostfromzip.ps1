# Pull the freshly built client payload over HTTP and take just RemoteSupport.Host.exe out of it.
#
# Deliberately NOT through the session's own file transfer: a 71 MB push overran the send queue and
# lost a chunk (BUGS #8), and this is the path a real install uses anyway. Nothing is swapped here
# and nothing is restarted -- this only stages a verified copy.
$ErrorActionPreference='Stop'
$url='http://192.168.1.115:8090/downloads/anywhere-it/client.zip'
$dir='C:\Temp\hostupdate'
$zip=Join-Path $dir 'client.zip'
$out=Join-Path $dir 'RemoteSupport.Host.exe'
$want='53778725F155FC525CAC3451DF3AA242BD30ADFCDC4436E70185EC9182935688'

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Remove-Item $zip,$out -Force -ErrorAction SilentlyContinue
$ProgressPreference='SilentlyContinue'
$sw=[Diagnostics.Stopwatch]::StartNew()
(New-Object Net.WebClient).DownloadFile($url,$zip)
"downloaded : $((Get-Item $zip).Length) bytes in $([int]$sw.Elapsed.TotalSeconds)s"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$za=[IO.Compression.ZipFile]::OpenRead($zip)
try {
    $e=$za.Entries | Where-Object { $_.Name -eq 'RemoteSupport.Host.exe' } | Select-Object -First 1
    if (-not $e) { "no RemoteSupport.Host.exe inside the zip"; exit 1 }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($e,$out,$true)
} finally { $za.Dispose() }

$h=(Get-FileHash $out -Algorithm SHA256).Hash
"extracted  : $((Get-Item $out).Length) bytes"
"sha256     : $h"
if ($h -ne $want) { "DIFFERENT from the build on the dev machine - not safe to install"; exit 2 }
"VERDICT    : MATCHES the build. Staged at $out, nothing swapped yet."
