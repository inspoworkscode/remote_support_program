# Replaces RemoteSupport.Host.exe with the staged, hash-verified copy.
#
# Host.exe is RUNNING right now -- it is the process this script is a child of -- so it cannot be
# overwritten. Windows does allow a running image to be RENAMED, which is the whole trick: the old
# one is renamed out of the way, the new one takes its name, and the next session spawns the new
# one. The running process keeps working under its new name until the session ends.
#
# The agent is not touched. It is what gives us access to this machine, and replacing it while
# using it is how you lock yourself out.
$ErrorActionPreference='Stop'
$dir='C:\Program Files\Anywhere IT\Remote Support'
$staged='C:\Temp\hostupdate\RemoteSupport.Host.exe'
$live=Join-Path $dir 'RemoteSupport.Host.exe'
$new=Join-Path $dir 'RemoteSupport.Host.new.exe'
$want='9AEF97857582F51E0B1DA1F82F4A824D9BE07C82BECB89BFEE838154E459BFB0'

if (-not (Test-Path $staged)) { "nothing staged at $staged"; exit 1 }
if (-not (Test-Path $live))   { "no live host at $live - this is not an install folder"; exit 1 }
"live before : $((Get-FileHash $live -Algorithm SHA256).Hash.Substring(0,16))...  $((Get-Item $live).Length) bytes"

Copy-Item $staged $new -Force
if ((Get-FileHash $new -Algorithm SHA256).Hash -ne $want) { Remove-Item $new -Force; "the copy into the install folder did not verify"; exit 2 }

$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$old=Join-Path $dir "RemoteSupport.Host.old-$stamp.exe"
Rename-Item $live $old
try { Rename-Item $new 'RemoteSupport.Host.exe' }
catch {
    # Put it back. A machine with no host still answers, it just cannot host a session, and that
    # is a far worse place to leave someone than where we started.
    Rename-Item $old 'RemoteSupport.Host.exe'
    "the swap failed and was rolled back: $($_.Exception.Message)"
    exit 3
}
"live after  : $((Get-FileHash $live -Algorithm SHA256).Hash.Substring(0,16))...  $((Get-Item $live).Length) bytes"
"previous    : $old  (kept, delete once this is known good)"
"NOTE        : the running host is still the OLD one. The next session spawns the new one."
