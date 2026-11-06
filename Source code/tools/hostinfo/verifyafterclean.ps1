$ErrorActionPreference='SilentlyContinue'
$dir='C:\Program Files\Anywhere IT\Remote Support'
"live host present : $(Test-Path (Join-Path $dir 'RemoteSupport.Host.exe'))"
"client present    : $(Test-Path (Join-Path $dir 'RemoteSupport.Client.exe'))"
"agent present     : $(Test-Path (Join-Path $dir 'RemoteSupport.Agent.exe'))"
"leftover .old     : $((Get-ChildItem $dir -Filter '*.old-*' -File).Count)"
"C:\Temp\codecbench: $(Test-Path 'C:\Temp\codecbench')"
"C:\Temp\hostupdate: $(Test-Path 'C:\Temp\hostupdate')"
"agent service     : $((Get-Service -Name '*RemoteSupport*' | Select-Object -First 1).Status)"
