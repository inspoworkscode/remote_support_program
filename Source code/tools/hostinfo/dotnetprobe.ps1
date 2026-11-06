# What runtimes does this machine have, and is there room to copy a bench onto it?
$ErrorActionPreference='SilentlyContinue'
"machine   : $env:COMPUTERNAME"
"running as: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
"session   : $((Get-Process -Id $PID).SessionId)"
""
"== .NET runtimes =="
$dn = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dn) {
    "dotnet at : $($dn.Source)"
    & dotnet --list-runtimes 2>&1 | ForEach-Object { "  $_" }
} else { "  no dotnet on PATH" }
""
"== where the product already lives (its folder is writable by SYSTEM) =="
foreach ($p in @("$env:ProgramFiles\Anywhere IT\Remote Support", "$env:ProgramData\RemoteSupport")) {
    if (Test-Path $p) { "  $p  exists" } else { "  $p  no" }
}
""
"== free space =="
Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object {
    "  $($_.DeviceID) $([math]::Round($_.FreeSpace/1GB,1)) GB free of $([math]::Round($_.Size/1GB,1)) GB"
}
