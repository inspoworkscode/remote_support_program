# NOTE: this function is NOT called Kill. "Kill" is a built-in ALIAS for Stop-Process, and in
# PowerShell an alias beats a function in name resolution -- so a function called Kill is never
# called at all, and every invocation went to Stop-Process with a folder path. It failed loudly,
# which was lucky: a quieter version of that mistake deletes the wrong thing.
#
# Removes the rollback copies of Host.exe and the staging folders left behind by the 2.2/2.3/2.4
# deployments.
#
# The gate comes first and is not optional: the ONLY reason those .old copies exist is to go back
# to if the live one is wrong, so it verifies the live binary is the known-good build before
# deleting the way back. If the hash does not match, nothing is touched.
$ErrorActionPreference='Continue'
$dir  = 'C:\Program Files\Anywhere IT\Remote Support'
$live = Join-Path $dir 'RemoteSupport.Host.exe'
$good = '53778725F155FC525CAC3451DF3AA242BD30ADFCDC4436E70185EC9182935688'

if (-not (Test-Path $live)) { "no live host at $live - refusing to delete anything"; exit 1 }
$h = (Get-FileHash $live -Algorithm SHA256).Hash
"live host  : $h"
if ($h -ne $good) {
    "expected   : $good"
    "MISMATCH - the live host is not the build that was proven. Nothing deleted."
    exit 2
}
"VERIFIED   : the live host is the proven build, so the rollback copies can go."
""

$freed = 0L
function Remove-Staged($path, $what) {
    if (-not (Test-Path $path)) { "  (already gone) $what"; return }
    $size = 0L
    try {
        $item = Get-Item $path -Force
        if ($item.PSIsContainer) { $size = (Get-ChildItem $path -Recurse -Force -File | Measure-Object Length -Sum).Sum }
        else { $size = $item.Length }
    } catch { }
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $path) { "  STILL THERE  $what - in use by a running process" }
    else { "  removed      $what  ($([math]::Round($size/1MB,1)) MB)"; $script:freed += $size }
}

"rollback copies of the host:"
$olds = @(Get-ChildItem $dir -Filter 'RemoteSupport.Host.old-*.exe' -File -ErrorAction SilentlyContinue)
if ($olds.Count -eq 0) { "  (none)" }
foreach ($o in $olds) { Remove-Staged $o.FullName $o.Name }

""
"staging folders:"
Remove-Staged 'C:\Temp\codecbench' 'C:\Temp\codecbench (the portable bench and its zip)'
Remove-Staged 'C:\Temp\hostupdate' 'C:\Temp\hostupdate (the downloaded client payload)'

""
"freed      : $([math]::Round($freed/1MB,1)) MB"
"C: free    : $([math]::Round((Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'").FreeSpace/1GB,1)) GB"
