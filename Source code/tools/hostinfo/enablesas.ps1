# Enables software Ctrl+Alt+Del for the agent SERVICE on this machine, and says what it changed.
#
# SendSAS is gated on the SoftwareSASGeneration machine policy, which defaults to 0 = none:
# disabled for everybody, services included. The call then RETURNS SUCCESSFULLY AND DOES NOTHING,
# which is why this took two attempts to find - the host reported "carried out" and the agent
# reported "sent Ctrl+Alt+Del", and both were telling the truth about a call that had no effect.
#
# 1 = services only. It does not let ordinary applications generate the sequence; only a service
# running as LocalSystem, which is the agent. This is what Install-Client.ps1 now does at install
# time; this script is for machines installed before that.
#
# Pure ASCII (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'Stop'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'

Write-Output ("running as : " + [Security.Principal.WindowsIdentity]::GetCurrent().Name)

$before = (Get-ItemProperty -Path $key -Name 'SoftwareSASGeneration' -ErrorAction SilentlyContinue).SoftwareSASGeneration
Write-Output ("SoftwareSASGeneration before : " + $(if ($null -eq $before) { "not set (means 0 - none)" } else { $before }))

if ($null -ne $before -and $before -ne 0) {
    Write-Output "Already allows software SAS. Nothing changed."
    exit 0
}

if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
New-ItemProperty -Path $key -Name 'SoftwareSASGeneration' -Value 1 -PropertyType DWord -Force | Out-Null

$after = (Get-ItemProperty -Path $key -Name 'SoftwareSASGeneration').SoftwareSASGeneration
Write-Output ("SoftwareSASGeneration after  : " + $after + "   (1 = services only)")
Write-Output ""
Write-Output "The policy is read when SendSAS is called, so no restart of anything is needed."
exit 0
