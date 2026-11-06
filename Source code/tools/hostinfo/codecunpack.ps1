# Verify the bench arrived intact, then unpack it. Nothing is run here.
$ErrorActionPreference='Stop'
$zip='C:\Temp\codecbench\codecbench-portable.zip'
$dest='C:\Temp\codecbench\bench'
$want='0DFA5E4ADBE1F4E05677B26661E20D9F44DB8C3AB898FE478D7E43F7DC7906C8'

if (-not (Test-Path $zip)) { "the zip is not there: $zip"; exit 1 }
$fi = Get-Item $zip
"size on this machine : $($fi.Length) bytes"
$got = (Get-FileHash $zip -Algorithm SHA256).Hash
"sha256               : $got"
if ($got -eq $want) { "INTEGRITY            : MATCHES the file that was sent" }
else { "INTEGRITY            : DIFFERENT - the transfer corrupted it"; exit 2 }

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $dest -Force
$exe = Join-Path $dest 'codecbench.exe'
if (Test-Path $exe) { "unpacked             : $exe" } else { "unpack FAILED - no codecbench.exe"; exit 3 }
"files                : $((Get-ChildItem $dest -File).Count)"
