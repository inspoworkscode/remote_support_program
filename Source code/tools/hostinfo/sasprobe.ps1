# Which SendSAS argument actually produces Ctrl+Alt+Del from the host's context?
#
# Run through the product's own script execution, so this script is a CHILD OF Host.exe and its
# token IS the host's token: NT AUTHORITY\SYSTEM, in the interactive session. That is the whole
# point - SendSAS behaves differently depending on the account and the session, so testing it
# anywhere else answers a different question.
#
# It observes the result rather than asking anybody to look: when the secure attention sequence
# succeeds, the INPUT DESKTOP becomes "Winlogon". So the desktop name before and after is a pass
# or a fail, not an opinion.
#
# NOTE: a success puts the Ctrl+Alt+Del screen on this machine. Press Escape to dismiss it.
#
# Pure ASCII: PowerShell 5.1 reads a BOM-less file as ANSI (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'Stop'

Add-Type -Namespace Sas -Name Native -MemberDefinition @'
    [DllImport("sas.dll", SetLastError = true)]
    public static extern void SendSAS(bool AsUser);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, System.Text.StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);
'@

function Get-InputDesktopName {
    # READ_CONTROL (0x00020000) is enough to read the name and needs no special rights.
    $h = [Sas.Native]::OpenInputDesktop(0, $false, 0x00020000)
    if ($h -eq [IntPtr]::Zero) { return "(could not open the input desktop - error " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ")" }
    try {
        $sb = New-Object System.Text.StringBuilder 256
        $needed = 0
        if ([Sas.Native]::GetUserObjectInformationW($h, 2, $sb, 256, [ref]$needed)) { return $sb.ToString() }
        return "(name unavailable)"
    }
    finally { [void][Sas.Native]::CloseDesktop($h) }
}

Write-Output "=== context ==="
Write-Output ("user       : " + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
Write-Output ("session id : " + (Get-Process -Id $PID).SessionId)
Write-Output ("is SYSTEM  : " + ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'))

$pol = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'SoftwareSASGeneration' -ErrorAction SilentlyContinue
Write-Output ("SoftwareSASGeneration policy : " + $(if ($pol) { $pol.SoftwareSASGeneration } else { "not set - means 0 = NONE, which disables SendSAS for everybody, services included" }))

Write-Output ""
Write-Output "=== SendSAS(false) - documented as the SERVICES session ==="
Write-Output ("input desktop before : " + (Get-InputDesktopName))
try { [Sas.Native]::SendSAS($false); Write-Output "call returned without error" }
catch { Write-Output ("THREW: " + $_.Exception.Message) }
Start-Sleep -Seconds 2
$after1 = Get-InputDesktopName
Write-Output ("input desktop after  : " + $after1)
Write-Output ("RESULT: " + $(if ($after1 -like 'Winlogon*') { "WORKED - the secure desktop is up" } else { "did nothing" }))

if ($after1 -like 'Winlogon*') {
    Write-Output ""
    Write-Output "Stopping here: the secure desktop is already showing. Press Escape on that machine."
    exit 0
}

Write-Output ""
Write-Output "=== SendSAS(true) - documented as the LOGGED-ON USER's session ==="
try { [Sas.Native]::SendSAS($true); Write-Output "call returned without error" }
catch { Write-Output ("THREW: " + $_.Exception.Message) }
Start-Sleep -Seconds 2
$after2 = Get-InputDesktopName
Write-Output ("input desktop after  : " + $after2)
Write-Output ("RESULT: " + $(if ($after2 -like 'Winlogon*') { "WORKED - the secure desktop is up. Press Escape on that machine." } else { "did nothing either" }))

exit 0
