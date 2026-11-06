# =====================================================================================
# Put the load tester on a fresh xneelo server that will PLAY the pretend machines.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Prepare-LoadBox.ps1 -Box 5.6.7.8
#
# Builds tools\loadtest for Linux (one file, nothing to install), copies it and the seed file to
# ~/loadtest on the box, and lets the box open far more connections than Ubuntu allows by default
# (one machine talking to one address is otherwise held to about 28,000).
# Run Prepare-LoadServer.ps1 first: it makes the seed file this copies.
# =====================================================================================
param(
    [string]$Box,
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
# No -Box: every saved load box, one after the other.
if (-not $Box) {
    . (Join-Path $PSScriptRoot "Servers.ps1")
    $s = Get-LoadServers
    foreach ($b in @($s.Box1, $s.Box2, $s.Box3) | Where-Object { $_ }) {
        powershell -ExecutionPolicy Bypass -File $PSCommandPath -Box $b -User $User
        if ($LASTEXITCODE -ne 0) { throw "setting up $b failed - see above" }
    }
    exit 0
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = Join-Path $PSScriptRoot "out"
$seed = Join-Path $out "seed.txt"
if (-not (Test-Path $seed)) { throw "No $seed - run Prepare-LoadServer.ps1 first." }
$bin = Join-Path $out "linux"

Write-Host "== Building the load tester for Linux ==" -ForegroundColor Cyan
dotnet publish (Join-Path $root "tools\loadtest\loadtest.csproj") -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $bin -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "the build failed" }

Write-Host "== Copying it to $Box ==  (type your key's passphrase now)" -ForegroundColor Cyan
scp (Join-Path $bin "loadtest") $seed "${User}@${Box}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw "could not copy to $Box" }

Write-Host "== Setting it up ==  (passphrase again)" -ForegroundColor Cyan
$remote = @"
set -e
mkdir -p ~/loadtest
mv /tmp/loadtest /tmp/seed.txt ~/loadtest/
chmod +x ~/loadtest/loadtest
sudo sysctl -q -w net.ipv4.ip_local_port_range='1024 65535' net.core.somaxconn=65535 net.ipv4.tcp_tw_reuse=1
echo '   ready. Processors and memory (MB):'
nproc
free -m | grep Mem:
"@
ssh "$User@$Box" $remote.Replace("`r`n", "`n")
if ($LASTEXITCODE -ne 0) { throw "the set-up failed - see above" }
Write-Host "`nLoad box $Box is ready." -ForegroundColor Green
