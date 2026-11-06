namespace RemoteSupport.Shared;

/// <summary>
/// The one last update by hand (2026-10-16, auto-update stage F, docs/NEXT-SESSION-AUTOUPDATE.md). Every machine installed
/// before auto-update has an agent that cannot take an update (<see cref="DeviceInfo.SelfUpdates"/> false); it needs ONE
/// newer install, and updates itself from then on.
///
/// <para>Not a reinstall: a reinstall makes a new machine id, and the office would have to confirm every machine again
/// (§111) — and it needs somebody at the machine for Windows' prompt. This does what an update does, through the Scripts
/// tool (which runs as SYSTEM on the machine): fetch the release from the company's own server, check the signature of it
/// and of every file it carries — ours, valid — and hand over to the NEW agent's <c>--apply-update</c>, which swaps the
/// files, restarts the service and rolls back if the new agent does not come back. The machine keeps its identity, its
/// settings and its unattended permission.</para>
///
/// <para>The script is shown to the technician before it runs, like anything in the Scripts tool.</para>
/// </summary>
public static class UpdateByHand
{
    /// <summary>The script, for a machine whose company's server is <paramref name="serverHttp"/> (https://…, or a local test).</summary>
    public static string Script(string serverHttp)
    {
        var server = serverHttp.Trim().TrimEnd('/').Replace("'", "''");
        return $$"""
# Brings this machine's support software up to date, so that it updates itself from now on.
# Auto-update's one update by hand (2026-10-16). Run by the Scripts tool, as SYSTEM.
$ErrorActionPreference = 'Stop'
$server = '{{server}}'
$publisher = '{{UpdateTrust.Publisher.Replace("'", "''")}}'
$work = Join-Path $env:ProgramData 'RemoteSupport-Update'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$file = Join-Path $work 'RemoteSupport.Client.exe'

function Ours($path) {
    $s = Get-AuthenticodeSignature -LiteralPath $path
    if ($s.Status -ne 'Valid') { return "$([IO.Path]::GetFileName($path)) is not validly signed ($($s.Status))" }
    $subject = $s.SignerCertificate.Subject
    if ($subject -notmatch "(^|, )O=$([regex]::Escape($publisher))(,|$)" -or $subject -notmatch '(^|, )C=ZA(,|$)') {
        return "$([IO.Path]::GetFileName($path)) is signed by $subject, not by us"
    }
    return ''
}

Write-Output "Downloading the latest release from $server ..."
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
Invoke-WebRequest -Uri "$server/update/client.exe" -OutFile $file -UseBasicParsing
$why = Ours $file
if ($why) { Write-Output "REFUSED: $why. Nothing was changed."; exit 1 }
$version = (Get-Item -LiteralPath $file).VersionInfo.FileVersion
Write-Output "Release $version, signed by us."

$svc = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\RemoteSupportAgent' -ErrorAction SilentlyContinue
if (-not $svc) { Write-Output 'This machine has no background service to update: reinstall the support client on it instead.'; exit 1 }
$dir = Split-Path -Parent ($svc.ImagePath.Trim().Trim('"'))

$files = Join-Path $work "files-$version"
if (Test-Path -LiteralPath $files) { Remove-Item -LiteralPath $files -Recurse -Force }
& $file --unpack $files | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $files 'RemoteSupport.Agent.exe'))) { Write-Output 'The release could not be unpacked. Nothing was changed.'; exit 1 }
Copy-Item -LiteralPath $file -Destination (Join-Path $files 'RemoteSupport.Client.exe') -Force
foreach ($f in @(Get-ChildItem -LiteralPath $files -Filter *.exe) + @(Get-ChildItem -LiteralPath $files -Filter *.ps1) + @(Get-ChildItem -LiteralPath (Join-Path $files 'healthcheck') -Filter *.ps1 -ErrorAction SilentlyContinue)) {
    $why = Ours $f.FullName
    if ($why) { Write-Output "REFUSED: $why. Nothing was changed."; exit 1 }
}
Write-Output "Every file checked. Installing into $dir - this session will drop for a moment while the service restarts."
Start-Process -FilePath (Join-Path $files 'RemoteSupport.Agent.exe') -ArgumentList @('--apply-update', "`"$dir`"", "`"$files`"", $version) -WindowStyle Hidden
Write-Output 'Started. If the new service does not come back within five minutes, the old one is put back by itself.'
""";
    }
}
