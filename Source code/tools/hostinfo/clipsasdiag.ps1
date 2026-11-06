# Ctrl+Alt+Del and copy/paste, as they happened on THIS machine. Read-only.
#
# Written 2026-09-29 for Clint's report from internal testing: Ctrl+Alt+Del and copy and paste
# "do not seem to be working" on the gaming laptop, and on the test machine a second copy of
# different folders pasted the FIRST folders again. Everything below is evidence the machine
# already holds; nothing is changed and nothing is sent.
#
#   Ctrl+Alt+Del  - the SoftwareSASGeneration policy (HANDOVER 21), the agent service, and the
#                   agent's own record of every SendSAS it made.
#   the clipboard - every clipboard decision in the host's trace, newest last, and who holds the
#                   clipboard right now.
#
# Pure ASCII: PowerShell 5.1 reads a BOM-less file as ANSI (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'

Write-Output "=== machine ==="
Write-Output ("name       : " + $env:COMPUTERNAME)
$os = Get-CimInstance Win32_OperatingSystem
Write-Output ("windows    : " + $os.Caption + " " + $os.Version)
Write-Output ("running as : " + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
$exp = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
if ($exp) { $o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner; Write-Output ("logged on  : " + $o.Domain + "\" + $o.User + "  (explorer session " + $exp.SessionId + ")") }
else { Write-Output "logged on  : NOBODY - no explorer.exe" }

Write-Output ""
Write-Output "=== Ctrl+Alt+Del ==="
$pol = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'SoftwareSASGeneration'
if ($pol -and $null -ne $pol.SoftwareSASGeneration) {
    $v = $pol.SoftwareSASGeneration
    $meaning = switch ($v) { 0 { 'NONE - SendSAS does nothing, for everybody' } 1 { 'services - correct' } 2 { 'ease of access apps' } 3 { 'services and ease of access apps' } default { 'unknown' } }
    Write-Output ("SoftwareSASGeneration : " + $v + " (" + $meaning + ")")
} else {
    Write-Output "SoftwareSASGeneration : NOT SET - means 0 = none. SendSAS returns success and does nothing."
}
# A domain or local GPO can hold the same value in the policy store and win over the key above.
$gp = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name 'SoftwareSASGeneration'
if ($gp -and $null -ne $gp.SoftwareSASGeneration) { Write-Output ("  GPO copy also set    : " + $gp.SoftwareSASGeneration) }

$svc = Get-CimInstance Win32_Service -Filter "Name = 'RemoteSupportAgent'"
if ($svc) { Write-Output ("agent service         : " + $svc.State + ", start " + $svc.StartMode + ", as " + $svc.StartName + ", pid " + $svc.ProcessId) }
else { Write-Output "agent service         : NOT INSTALLED" }

Write-Output ""
Write-Output "the agent's own record, last 3 days (Application log, source RemoteSupportAgent):"
$since = (Get-Date).AddDays(-3)
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteSupportAgent'; StartTime = $since } -MaxEvents 400
$sas = $ev | Where-Object { $_.Message -match 'Ctrl\+Alt\+Del|SendSAS|system action|Launched host|SYSTEM launch failed' } | Sort-Object TimeCreated | Select-Object -Last 25
if (-not $sas) { Write-Output "  (nothing about Ctrl+Alt+Del or host launches)" }
foreach ($e in $sas) {
    $m = ($e.Message -split "`r?`n")[0]
    if ($m.Length -gt 160) { $m = $m.Substring(0,160) }
    Write-Output ("  " + $e.TimeCreated.ToString('MM-dd HH:mm:ss') + "  " + $m)
}

Write-Output ""
Write-Output "=== host trace: clipboard and system actions ==="
$log = Join-Path $env:ProgramData 'RemoteSupport\host-trace.log'
if (-not (Test-Path $log)) { Write-Output "NO host trace at $log" }
else {
    $fi = Get-Item $log
    Write-Output ("{0}  ({1:N0} bytes, written {2})" -f $log, $fi.Length, $fi.LastWriteTime)
    Get-Content $log |
        Where-Object { $_ -match 'host started|=== |clipboard:|sys action|fileset|consent|subscri|participants' -and $_ -notmatch 'sending .*/.* files' } |
        Select-Object -Last 160 |
        ForEach-Object { Write-Output ("  " + $_) }
}

Write-Output ""
Write-Output "=== the clipboard right now ==="
Add-Type -Namespace Clip -Name Native -MemberDefinition @'
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
'@
$owner = [Clip.Native]::GetClipboardOwner()
$opid = 0
if ($owner -ne [IntPtr]::Zero) { [void][Clip.Native]::GetWindowThreadProcessId($owner, [ref]$opid) }
$pname = if ($opid) { (Get-Process -Id $opid).ProcessName } else { '(none - owner gone or never set)' }
Write-Output ("sequence : " + [Clip.Native]::GetClipboardSequenceNumber())
Write-Output ("owner    : pid " + $opid + " " + $pname)

Write-Output ""
Write-Output "=== paste staging on this machine (what the technician has sent) ==="
Get-ChildItem 'C:\Users' -Directory | ForEach-Object {
    $p = Join-Path $_.FullName 'AppData\Local\Temp\RemoteSupport'
    if (Test-Path $p) {
        Get-ChildItem $p -Directory | ForEach-Object {
            $n = @(Get-ChildItem $_.FullName -Recurse -File).Count
            Write-Output ("  {0}  {1} file(s), newest {2}" -f $_.FullName, $n, ((Get-ChildItem $_.FullName -Recurse | Sort-Object LastWriteTime | Select-Object -Last 1).LastWriteTime))
        }
    }
}
