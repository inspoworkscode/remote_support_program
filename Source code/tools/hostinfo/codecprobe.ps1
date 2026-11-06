# What can this machine encode, and with what?
#
# Stage 2.2 needs an H.264 encoder on the CUSTOMER's machine -- the one whose screen is captured.
# The technician's end only needs a decoder. This says which of them a machine has.
#
# It reads the Media Foundation transform registry rather than calling MFTEnumEx, so it needs no
# runtime installed and no binary copied anywhere. The categories are subkeys named by GUID
# without braces, and those GUIDs are the reason this script exists: the first version of our own
# interop had the encoder one wrong, MFTEnumEx answered an unknown category with "success, zero
# results", and the conclusion was a machine with no encoder. Reading them here is checkable.
#
# IMPORTANT, and learned the hard way on the machine this was written on: a REGISTERED encoder is
# not a WORKING encoder. A driver registers its transforms whether or not the silicon behind them
# exists. Intel Quick Sync is registered on an Alder Lake-N laptop that appears to have no encode
# engine at all -- it activates, configures, and then never asks for a frame. So treat this as
# "what to expect", and `codecbench --h264check` as the thing that settles it.

$ErrorActionPreference = 'SilentlyContinue'
$base = 'HKLM:\SOFTWARE\Classes\MediaFoundation\Transforms'

function Show-Category($label, $guid) {
    Write-Output ""
    Write-Output "== $label =="
    $key = Join-Path $base "Categories\$guid"
    if (-not (Test-Path $key)) { Write-Output "  the category itself is not in the registry - $guid"; return }
    $names = @()
    foreach ($child in Get-ChildItem $key) {
        $n = (Get-ItemProperty (Join-Path $base $child.PSChildName)).'(default)'
        if ($n) { $names += $n } else { $names += "(unnamed $($child.PSChildName))" }
    }
    if ($names.Count -eq 0) { Write-Output "  (none)" }
    else { $names | Sort-Object -Unique | ForEach-Object { Write-Output "  $_" } }
}

Write-Output "machine    : $env:COMPUTERNAME"
Write-Output "running as : $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Write-Output "OS         : $((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version.Build)"
Write-Output "CPU        : $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name)"
foreach ($g in Get-CimInstance Win32_VideoController) {
    Write-Output "GPU        : $($g.Name)  driver $($g.DriverVersion) ($($g.DriverDate))"
}

Show-Category 'video encoders (what stage 2.2 needs on THIS machine)' 'f79eac7d-e545-4387-bdee-d647d7bde42a'
Show-Category 'video decoders (the technician end needs one of these)' 'd6c02d4b-6833-45b4-971a-05a4b04bab91'
Show-Category 'video processors (the BGRA to NV12 conversion)' '302ea3fc-aa5f-47f9-9f7a-c2188bb16302'

Write-Output ""
$enc = Join-Path $base 'Categories\f79eac7d-e545-4387-bdee-d647d7bde42a'
$h264 = @()
if (Test-Path $enc) {
    foreach ($child in Get-ChildItem $enc) {
        $n = (Get-ItemProperty (Join-Path $base $child.PSChildName)).'(default)'
        if ($n -match 'H\.?264|AVC') { $h264 += $n }
    }
}
if ($h264.Count -gt 0) {
    Write-Output "VERDICT: $($h264.Count) H.264 encoder(s) registered - worth running codecbench --h264check here."
    $h264 | Sort-Object -Unique | ForEach-Object { Write-Output "         $_" }
} else {
    Write-Output "VERDICT: no H.264 encoder registered. This machine would fall back to JPEG tiles."
}
