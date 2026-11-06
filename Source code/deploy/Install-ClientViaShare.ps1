<#
    Installs the Anywhere IT Remote Support client on another machine WITHOUT PowerShell Remoting.

    Uses the admin file share (TCP 445) to copy the package, then WMI over DCOM (TCP 135) to run the
    installer there. Use this when Enable-PSRemoting has not been run on the target.

    From THIS laptop, in a normal PowerShell:

        powershell -ExecutionPolicy Bypass -File .\Install-ClientViaShare.ps1 -Computer 192.168.1.113 -Unattended

    Windows prompts for an administrator account on the TARGET machine. The password goes straight to
    Windows; it is never placed on a command line, written to a file, or logged.
#>
param(
    [Parameter(Mandatory = $true)][string]$Computer,
    [switch]$Unattended,
    [string]$SourceDir
)
$ErrorActionPreference = "Stop"

# $MyInvocation.MyCommand.Path is null inside a param() default, so resolve the package here instead.
if (-not $SourceDir) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $SourceDir = Join-Path (Split-Path -Parent $scriptDir) 'deploy-client'
}
if (-not (Test-Path (Join-Path $SourceDir 'RemoteSupport.Client.exe'))) {
    throw "Client package not found at $SourceDir. Run build.ps1 first, or pass -SourceDir."
}
Write-Host "Package: $SourceDir" -ForegroundColor DarkGray

Write-Host "== Target: $Computer ==" -ForegroundColor Cyan
foreach ($port in 445, 135) {
    $t = Test-NetConnection -ComputerName $Computer -Port $port -WarningAction SilentlyContinue
    if (-not $t.TcpTestSucceeded) {
        throw "TCP $port is closed on $Computer. File sharing (445) and WMI (135) must both be reachable. Check that machine's firewall."
    }
    Write-Host "   TCP $port open." -ForegroundColor Green
}

$cred = Get-Credential -Message "Administrator account on $Computer"

# A username with no domain/computer part makes New-PSDrive throw "Index was outside the bounds of
# the array", so normalise it and connect with the Windows networking API instead.
$user = $cred.UserName
if ($user -notmatch '[\\@]') { $user = "$Computer\$user" }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WinNet {
    [StructLayout(LayoutKind.Sequential)]
    public struct NETRESOURCE {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpProvider;
    }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetAddConnection2(ref NETRESOURCE nr, string password, string username, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetCancelConnection2(string name, int flags, bool force);
}
"@ -ErrorAction SilentlyContinue

$share = "\\$Computer\C$"
$stagingUnc = "$share\ProgramData\RemoteSupportProgram-Install"
$stagingLocal = "C:\ProgramData\RemoteSupportProgram-Install"

Write-Host "== Connecting to $share ==" -ForegroundColor Cyan
[WinNet]::WNetCancelConnection2($share, 0, $true) | Out-Null   # drop any stale session first
$nr = New-Object WinNet+NETRESOURCE
$nr.dwType = 1                      # RESOURCETYPE_DISK
$nr.lpRemoteName = $share
$rc = [WinNet]::WNetAddConnection2([ref]$nr, $cred.GetNetworkCredential().Password, $user, 0)
if ($rc -ne 0) {
    $hint = switch ($rc) {
        5    { "Access denied - that account is not an administrator on $Computer." }
        53   { "Network path not found - check the IP and that file sharing is on." }
        1326 { "Wrong username or password." }
        1219 { "Conflicting credentials already cached for $Computer. Run: net use $share /delete" }
        default { "Windows error $rc." }
    }
    throw "Could not connect to $share. $hint"
}
Write-Host "   Connected as $user." -ForegroundColor Green

try {
    Write-Host "== Copying the client package (this is ~170 MB, allow a minute) ==" -ForegroundColor Cyan
    if (Test-Path $stagingUnc) { Remove-Item $stagingUnc -Recurse -Force }
    New-Item -ItemType Directory -Path $stagingUnc -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $stagingUnc -Recurse -Force
    $copied = (Get-ChildItem $stagingUnc -Recurse -File | Measure-Object).Count
    Write-Host "   $copied files copied." -ForegroundColor Green
}
catch { [WinNet]::WNetCancelConnection2($share, 0, $true) | Out-Null; throw }

Write-Host "== Running the installer on $Computer ==" -ForegroundColor Cyan
$session = $null
try {
    $session = New-CimSession -ComputerName $Computer -Credential $cred -SessionOption (New-CimSessionOption -Protocol Dcom) -ErrorAction Stop
    $log = "$stagingLocal\install.log"
    $args = if ($Unattended) { '-Unattended' } else { '' }
    $cmd = "powershell.exe -ExecutionPolicy Bypass -NoProfile -File `"$stagingLocal\Install-Client.ps1`" $args *> `"$log`""
    $r = Invoke-CimMethod -CimSession $session -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
    if ($r.ReturnValue -ne 0) { throw "Could not start the installer remotely (WMI code $($r.ReturnValue))." }
    Write-Host "   Installer started (pid $($r.ProcessId)). Waiting for it to finish…"

    $deadline = (Get-Date).AddMinutes(5)
    do {
        Start-Sleep -Seconds 4
        $running = Get-CimInstance -CimSession $session -ClassName Win32_Process -Filter "ProcessId=$($r.ProcessId)" -ErrorAction SilentlyContinue
    } while ($running -and (Get-Date) -lt $deadline)

    $logUnc = "$stagingUnc\install.log"
    if (Test-Path $logUnc) { Write-Host "`n---- installer output ----" -ForegroundColor Cyan; Get-Content $logUnc | Write-Host }
    else { Write-Warning "No installer log was produced." }

    $client = Get-CimInstance -CimSession $session -ClassName Win32_Process -Filter "Name='RemoteSupport.Client.exe'" -ErrorAction SilentlyContinue
    if ($client) { Write-Host "`n$Computer is installed and the client is running." -ForegroundColor Green }
    else { Write-Warning "Installed, but the client is not running on $Computer. If Windows blocked it, check Smart App Control there." }
}
finally {
    if ($session) { Remove-CimSession $session -ErrorAction SilentlyContinue }
    if (Test-Path $stagingUnc) { Remove-Item $stagingUnc -Recurse -Force -ErrorAction SilentlyContinue }
    [WinNet]::WNetCancelConnection2($share, 0, $true) | Out-Null
}
