$ErrorActionPreference='Stop'
$p='C:\Temp\codecbench\RemoteSupport.Host.exe'
if (-not (Test-Path $p)) { "not there yet"; exit 1 }
"size   : $((Get-Item $p).Length)"
$sw=[Diagnostics.Stopwatch]::StartNew()
$h=(Get-FileHash $p -Algorithm SHA256).Hash
"sha256 : $h  (hashed in $([int]$sw.Elapsed.TotalSeconds)s)"
if ($h -eq '2254A0EEA9AB4BD62C9453105CA102AB5FEBD5FE707F9755014D4C44E409D710') { "MATCHES the build on the dev machine" } else { "DIFFERENT - do not install this" }
