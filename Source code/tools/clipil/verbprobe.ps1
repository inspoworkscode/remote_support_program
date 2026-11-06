# What does the SHELL make of a file list on this clipboard, writer by writer?
#
# Get-Clipboard -Format FileDropList seeing the paths is not the same question as Explorer
# offering Paste. Explorer asks the target folder's drop handler whether it will take what is on
# the clipboard, and the answer to that decides whether Paste is on the menu at all. This asks
# that question for each way of writing the list, so the one that differs can be found.
#
#   .\verbprobe.ps1            (from tools\clipil, after dotnet build -c Release)
param([string]$Exe = "$PSScriptRoot\bin\Release\net10.0-windows\clipil.exe")

$ErrorActionPreference = 'SilentlyContinue'
$src = Join-Path $env:TEMP ("verbprobe-src-" + [guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory $src | Out-Null
'alpha' | Set-Content (Join-Path $src 'alpha.txt')
'beta'  | Set-Content (Join-Path $src 'beta.txt')
$files = @((Join-Path $src 'alpha.txt'), (Join-Path $src 'beta.txt'))
$target = Join-Path $env:TEMP 'verbprobe-target'

function Ask([string]$writer) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory $target | Out-Null
    Start-Sleep -Milliseconds 300
    $sh = New-Object -ComObject Shell.Application
    $self = $sh.NameSpace($target).Self
    $verbsSelf = @($self.Verbs() | ForEach-Object { ($_.Name -replace '&','') })
    $item = $sh.NameSpace((Split-Path $target)).ParseName((Split-Path $target -Leaf))
    $verbsItem = @($item.Verbs() | ForEach-Object { ($_.Name -replace '&','') })
    $n = @(Get-Clipboard -Format FileDropList).Count
    $hasSelf = $verbsSelf -contains 'Paste'
    $hasItem = $verbsItem -contains 'Paste'
    Write-Output ("{0,-42} OLE sees {1} file(s); Paste verb: NameSpace().Self={2}  ParseName={3}" -f $writer, $n, $hasSelf, $hasItem)
    if ($hasSelf -or $hasItem) {
        $v = $self.Verbs() | Where-Object { ($_.Name -replace '&','') -eq 'Paste' } | Select-Object -First 1
        $v.DoIt()
        Start-Sleep -Seconds 3
        $landed = @(Get-ChildItem $target -File).Count
        Write-Output ("{0,-42} after DoIt: {1} file(s) landed in the target" -f '', $landed)
    } else {
        Write-Output ("{0,-42} verbs were: {1}" -f '', ($verbsSelf -join ', '))
    }
}

# A. The host's own write, at this process's integrity.
& $Exe --setfiles $files[0] $files[1] | Out-Null
Ask 'A. ClipboardAccess.SetFiles (the host)'

# B. What PowerShell/.NET does (OLE, CF_HDROP + FileName formats, no drop effect).
Set-Clipboard -Path $files
Ask 'B. Set-Clipboard -Path (OLE)'

# C. What Explorer itself puts there on Ctrl+C: the folder's own data object, with IDLists.
$sh = New-Object -ComObject Shell.Application
$srcFolder = $sh.NameSpace($src)
$srcFolder.ParseName('alpha.txt').InvokeVerb('Copy')
Start-Sleep -Milliseconds 500
Ask 'C. Explorer Copy verb (shell data object)'

Remove-Item $src -Recurse -Force
Remove-Item $target -Recurse -Force
