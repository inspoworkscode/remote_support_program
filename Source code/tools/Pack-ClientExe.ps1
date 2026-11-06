<#
.SYNOPSIS
    Makes the ONE downloadable .exe (2026-09-30): the customer app with the host, the agent service,
    the install scripts and the health check toolkit packed inside it.

.DESCRIPTION
    Clint: "It would be much better if every download was an exe. it would be way more professional."

    Every company's customers download this same file - /download/<slug> serves it as
    <slug>-support.exe - and it learns whose it is from the download's address or its name, then
    fetches that company's branding from the server (RemoteSupport.Client.Launcher). It is the same
    file for everybody because a file that differed per company could not be code-signed
    (docs/CODE-SIGNING.md, "One .exe for every company").

    In this order, and the order is the point:
      1. SIGN what goes inside - the host, the agent and every script - if a certificate is installed
      2. pack them into one zip (everything the download folder holds except the app itself and branding\)
      3. publish the customer app with that zip and the server's address embedded in it
      4. SIGN the result
      5. put it in the download folder as RemoteSupport.Client.exe, with client-exe.packed beside it,
         which is what tells the platform the .exe can be offered on its own

    build.ps1 and tools\ship-build.ps1 both run this once the download folder is staged.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Pack-ClientExe.ps1 -PayloadDir .\dist\client
#>
param(
    # A folder shaped like the old download: RemoteSupport.Host.exe, RemoteSupport.Agent.exe,
    # Install-Client.ps1 and the rest, and healthcheck\.
    [Parameter(Mandatory)] [string]$PayloadDir,
    # The server a copy asks when the download did not record where it came from. Defaults to the
    # platform's PublicUrl in src\RemoteSupport.Platform\appsettings.json.
    [string]$Server,
    [switch]$RequireSigning,
    [switch]$SkipSigning,
    # The version to stamp the customer app with (2026-10-16). build.ps1 passes its own; left out, it is read from the
    # staged RemoteSupport.Host.exe, so the app is always stamped the same as the host and agent packed inside it.
    [string]$Version
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
. (Join-Path $PSScriptRoot 'Signing.ps1')
. (Join-Path $PSScriptRoot 'BuildVersion.ps1')

$PayloadDir = (Resolve-Path $PayloadDir).Path
foreach ($need in 'RemoteSupport.Host.exe', 'RemoteSupport.Agent.exe', 'Install-Client.ps1', 'Uninstall-Client.ps1', 'healthcheck') {
    if (-not (Test-Path (Join-Path $PayloadDir $need))) {
        throw "$need is not in $PayloadDir. Stage the download folder first (build.ps1 or tools\ship-build.ps1)."
    }
}

if (-not $Server) {
    try {
        $m = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $root 'src\RemoteSupport.Platform\appsettings.json')), '"PublicUrl"\s*:\s*"([^"]*)"')
        if ($m.Success) { $Server = $m.Groups[1].Value }
    } catch { }
}
if (-not $Server) { throw "No -Server given and no PublicUrl in the platform's appsettings.json." }

$sign = Initialize-Signing -RequireSigning:$RequireSigning -SkipSigning:$SkipSigning -Quiet
Write-Host "== The one .exe: packing $PayloadDir ==" -ForegroundColor Cyan
Write-Host "   server for copies that cannot tell: $Server" -ForegroundColor DarkGray
Write-Host ("   signing: " + $(if ($sign.On) { "ON - $($sign.Cert.Subject)" } else { "off (no certificate) - the .exe will be UNSIGNED" })) -ForegroundColor DarkGray

# ---- 1. sign the inside, skipping anything already carrying a valid signature -----------------
$inside = @(
    (Join-Path $PayloadDir 'RemoteSupport.Host.exe'),
    (Join-Path $PayloadDir 'RemoteSupport.Agent.exe')
) + @(Get-ChildItem $PayloadDir -Recurse -Filter *.ps1 | ForEach-Object { $_.FullName })
$unsigned = @($inside | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
Invoke-Signing $sign "what goes inside the .exe" $unsigned

# ---- 2. one zip of everything but the app itself and branding\ --------------------------------
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("rs-pack-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$zip = Join-Path $work 'client-payload.zip'
$defaults = Join-Path $work 'client-defaults.json'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$skip = @('RemoteSupport.Client.exe', 'client-exe.packed')
$fs = [System.IO.File]::Open($zip, 'CreateNew')
$archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($f in Get-ChildItem $PayloadDir -Recurse -File) {
        $rel = $f.FullName.Substring($PayloadDir.Length).TrimStart('\').Replace('\', '/')
        if ($skip -contains $rel -or $rel -like 'branding/*' -or $rel -like '*.zip') { continue }
        # Fastest: the exes are compressed single-file bundles already; the scripts are small.
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Fastest) | Out-Null
        $count++
    }
}
finally { $archive.Dispose(); $fs.Dispose() }
[System.IO.File]::WriteAllText($defaults, (@{ server = $Server } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("   packed {0} files, {1:N0} MB" -f $count, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green

# ---- 3. the customer app, with them inside ------------------------------------------------------
$out = Join-Path $work 'out'
if (-not $Version) { $Version = Get-StagedVersion (Join-Path $PayloadDir 'RemoteSupport.Host.exe') }
$versionArgs = @()
if ($Version) { $versionArgs = VersionArgs (New-BuildVersion $Version); Write-Host "   version $Version" -ForegroundColor DarkGray }
& $dotnet publish (Join-Path $root 'src\RemoteSupport.Client\RemoteSupport.Client.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none "-p:ClientPayload=$zip" "-p:ClientDefaults=$defaults" -o $out --nologo -v quiet @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Publishing the customer app with its payload failed." }
$exe = Join-Path $out 'RemoteSupport.Client.exe'

# ---- 4. sign the outside, last -----------------------------------------------------------------
Invoke-Signing $sign "the one .exe" @($exe)

# ---- 5. into the download folder ----------------------------------------------------------------
Copy-Item $exe (Join-Path $PayloadDir 'RemoteSupport.Client.exe') -Force
$stamp = "Packed $(Get-Date -Format 'yyyy-MM-dd HH:mm') for $Server; signed: $($sign.On)"
[System.IO.File]::WriteAllText((Join-Path $PayloadDir 'client-exe.packed'), $stamp)
$size = (Get-Item (Join-Path $PayloadDir 'RemoteSupport.Client.exe')).Length / 1MB
Write-Host ("   RemoteSupport.Client.exe {0:N0} MB - the download, served as <company>-support.exe" -f $size) -ForegroundColor Green
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
