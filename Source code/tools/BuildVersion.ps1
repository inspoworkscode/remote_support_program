# The version every app of one build is stamped with (2026-10-16, auto-update stage A) - dot-source it:
#     . (Join-Path $root 'tools\BuildVersion.ps1')
#     $version = New-BuildVersion            # or New-BuildVersion '2026.10.16.1432' to choose one
#     & $dotnet publish ... @(VersionArgs $version)
#
# Shared by build.ps1, tools\ship-build.ps1 and tools\Pack-ClientExe.ps1, so a host, an agent and the tray app
# that packs them can never say different things. The rule, and why it is the moment of the build in UTC, is in
# src\RemoteSupport.Shared\AppVersion.cs: 2026.10.16.1432 is 14:32 UTC on 16 October 2026.

function New-BuildVersion([string]$Chosen) {
    if ($Chosen) {
        $v = $null
        if (-not [System.Version]::TryParse($Chosen, [ref]$v) -or $v.Revision -lt 0) {
            throw "-Version '$Chosen' is not four numbers, like 2026.10.16.1432."
        }
        if ($v.Major -gt 65534 -or $v.Minor -gt 65534 -or $v.Build -gt 65534 -or $v.Revision -gt 65534) {
            throw "-Version '$Chosen': Windows allows each part up to 65534."
        }
        return $v.ToString()
    }
    $now = (Get-Date).ToUniversalTime()
    # The time as one number (14:32 -> 1432, 09:05 -> 905): it fits Windows' 65534 and still only goes up.
    return '{0}.{1}.{2}.{3}' -f $now.Year, $now.Month, $now.Day, ($now.Hour * 100 + $now.Minute)
}

# What dotnet publish needs to stamp it. InformationalVersion would otherwise gain "+<git hash>", and the apps
# report the assembly version anyway; one string everywhere is easier to read in Windows' file properties.
function VersionArgs([string]$Version) {
    return @("-p:Version=$Version", "-p:FileVersion=$Version", "-p:AssemblyVersion=$Version",
             "-p:InformationalVersion=$Version", "-p:IncludeSourceRevisionInInformationalVersion=false")
}

# The version a staged app was stamped with, read from its file properties - so a later step (Pack-ClientExe)
# stamps the tray app the same as the host and agent it packs. Empty when it has none to give.
function Get-StagedVersion([string]$Exe) {
    if (-not (Test-Path $Exe)) { return '' }
    $fv = (Get-Item $Exe).VersionInfo.FileVersion
    $v = $null
    if ($fv -and [System.Version]::TryParse($fv, [ref]$v) -and $v.Revision -ge 0) { return $v.ToString() }
    return ''
}
