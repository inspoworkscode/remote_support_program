$ErrorActionPreference='Continue'
$exe='C:\Temp\codecbench\bench\codecbench.exe'
Set-Location (Split-Path $exe)
"######## DEFAULT (whatever the product would pick) ########"
& $exe --h264check --out 'C:\Temp\codecbench\out-default' 2>&1 | ForEach-Object { "$_" }
"[exit $LASTEXITCODE]"
