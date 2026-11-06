$ErrorActionPreference='SilentlyContinue'
$root='C:\Temp\codecbench'
"bench exe   : $(if (Test-Path "$root\bench\codecbench.exe") {'present'} else {'MISSING'})"
"runner      : $(if (Test-Path "$root\run-unattended.ps1") {'present'} else {'MISSING'})"
"files in bench: $((Get-ChildItem "$root\bench" -File).Count)"
"logged-on   : $((Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User } | Select-Object -First 1))"
"results yet : $(if (Test-Path "$root\results.txt") {'yes'} else {'not run'})"
