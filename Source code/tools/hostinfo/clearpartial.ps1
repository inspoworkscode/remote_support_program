# The old host is still holding the half-written file open. Nothing can replace it until the
# process that owns the handle goes away, which happens when the session ends.
$ErrorActionPreference='SilentlyContinue'
$p='C:\Temp\codecbench\RemoteSupport.Host.exe'
Remove-Item $p -Force
if (Test-Path $p) { "still locked - it will clear when this session ends" } else { "removed" }
