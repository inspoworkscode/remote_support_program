$ErrorActionPreference='Continue'
$exe='C:\Temp\codecbench\bench\codecbench.exe'
if (-not (Test-Path $exe)) { "no bench at $exe"; exit 1 }
Set-Location (Split-Path $exe)
& $exe --mfts 2>&1 | ForEach-Object { "$_" }
"[exit $LASTEXITCODE]"
