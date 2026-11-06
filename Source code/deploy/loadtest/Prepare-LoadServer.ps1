# =====================================================================================
# Turn a FRESH xneelo server into the load-test copy of the platform, in one command.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Prepare-LoadServer.ps1
#
# In order, as the live server has it (2026-10-12):
#   1. the platform, by Publish-Platform.ps1 + go-live.sh (Caddy and TLS included) - on SQLite, as every install starts;
#   2. the switch to PostgreSQL on its own rsp-files / rsp-db volumes, by the SAME Switch-ToPostgres.ps1 that moved
#      live (it shows what it found and asks you to type SWITCH);
#   3. two-factor OFF for the pretend technicians, slow-query logging on, the pretend companies (seed-loadtest) -
#      REFUSED unless the platform is on PostgreSQL - and the seed file back to deploy\loadtest\out\seed.txt.
# Run again on the same server and it skips what is already done.
#
#  -Server       the TEST server's address (default: the one Save-Addresses.ps1 kept). Refused if it is live.
#  -Domain       default loadtest.theremotesupportprogram.com - its DNS record must point at -Server
#  -Companies    pretend companies (default 200)
#  -People       people in each, all technicians (default 10 - the most a company may have, HANDOVER section 116)
#  -FilesGB, -DbGB   the two volumes' sizes as the xneelo panel shows them (default 10 and 20)
#  -MoreThreads  start the platform with 512 worker threads ready (DOTNET_ThreadPool_ForceMinWorkerThreads),
#                to compare against a run without - HANDOVER section 115. Run again without it to switch it off.
#  -WidePorts    let Caddy open up to about 64,000 connections to the platform instead of Linux's default 28,000
#                (net.ipv4.ip_local_port_range 1024-65535) - the LOAD SERVER only, to see past that ceiling.
#                Run again without it to put the default back.
#  -SkipInstall  the platform is already on it: only the switch (if needed), the settings, the seed and the stats script
# =====================================================================================
param(
    [string]$Server,
    [string]$Domain = "loadtest.theremotesupportprogram.com",
    [int]$Companies = 200,
    [int]$People = 10,
    [int]$FilesGB = 10,
    [int]$DbGB = 20,
    [switch]$MoreThreads,
    [switch]$WidePorts,
    [switch]$SkipInstall,
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Servers.ps1")
if (-not $Server) { $Server = (Get-LoadServers).Server }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$target = "$User@$Server"

# ---- never the live server ----
$live = "theremotesupportprogram.com"
if ($Domain -in @($live, "www.$live")) { throw "-Domain is the LIVE address. Use loadtest.$live." }
$liveIps = @("154.65.110.136")
try { $liveIps += [System.Net.Dns]::GetHostAddresses($live) | ForEach-Object { $_.IPAddressToString } } catch { }
if ($Server -in $liveIps) { throw "$Server is the LIVE server. The load test must run on a separate, throwaway server." }
if ($People -gt 10) { throw "-People ${People}: a company may have ten people at most (HANDOVER section 116)." }

# ---- the DNS record must already point here, or Caddy cannot get a certificate ----
try { $points = [System.Net.Dns]::GetHostAddresses($Domain) | ForEach-Object { $_.IPAddressToString } } catch { $points = @() }
if ($Server -notin $points) {
    throw "$Domain does not point at $Server yet (it gives: $($points -join ', ')). Add the A record in Cloudflare (DNS only, grey cloud), wait a minute, and run this again."
}

if (-not $SkipInstall) {
    Write-Host "== 1 of 3: installing the platform on the test server (the same way as live) ==" -ForegroundColor Cyan
    powershell -ExecutionPolicy Bypass -File (Join-Path $root "deploy\Publish-Platform.ps1") -Server $Server -Domain $Domain -User $User
    if ($LASTEXITCODE -ne 0) { throw "the install failed - see above" }
}

# ---- PostgreSQL, as live: the drop-in postgres.conf is THE sign a server is on it (HANDOVER section 125) ----
Write-Host "== 2 of 3: is it on PostgreSQL yet? ==  (type your key's passphrase now)" -ForegroundColor Cyan
$onPg = ssh $target "test -f /etc/systemd/system/remotesupport-platform.service.d/postgres.conf && echo yes || echo no"
if ($LASTEXITCODE -ne 0) { throw "could not reach $target" }
if ("$onPg".Trim() -ne "yes") {
    Write-Host "   Not yet - switching it, exactly as live was switched. Type SWITCH when it asks." -ForegroundColor Yellow
    powershell -ExecutionPolicy Bypass -File (Join-Path $root "deploy\postgres\Switch-ToPostgres.ps1") -Server $Server -User $User -FilesGB $FilesGB -DbGB $DbGB
    if ($LASTEXITCODE -ne 0) { throw "the switch to PostgreSQL did not finish - see above. Nothing was seeded." }
} else {
    Write-Host "   Yes - already on PostgreSQL." -ForegroundColor Green
}

$threads = if ($MoreThreads) { "Environment=DOTNET_ThreadPool_ForceMinWorkerThreads=200" } else { "# (no extra threads)" }
$ports = if ($WidePorts) { "1024 65535" } else { "32768 60999" }
# The stats script travels as a FILE: Windows PowerShell mangles double quotes inside an argument to ssh,
# which is also why nothing below uses one.
Write-Host "== 3 of 3: uploading the stats script ==  (passphrase again)" -ForegroundColor Cyan
scp (Join-Path $PSScriptRoot "rsp-loadstats.sh") "${target}:/tmp/rsp-loadstats.sh"
if ($LASTEXITCODE -ne 0) { throw "could not reach $target" }
Write-Host "== Load-test settings and the pretend companies ==  (passphrase again)" -ForegroundColor Cyan
$remote = @"
set -e
if ! test -f /etc/systemd/system/remotesupport-platform.service.d/postgres.conf; then
  echo 'REFUSED: the platform is not on PostgreSQL (no postgres.conf drop-in). Nothing seeded.'; exit 1
fi
sudo tee /etc/systemd/system/remotesupport-platform.service.d/loadtest.conf >/dev/null <<'EOF'
[Service]
# LOAD TEST SERVER ONLY (deploy/loadtest): the pretend technicians sign in with a password alone.
Environment=RSP_Security__RequireTwoFactor=false
$threads
EOF
# LOAD TEST SERVER ONLY: the ports Caddy may use to reach the platform on 127.0.0.1:8090 (Linux's default is 32768-60999).
echo 'net.ipv4.ip_local_port_range = $ports' | sudo tee /etc/sysctl.d/90-rsp-loadtest.conf >/dev/null
sudo sysctl -q -p /etc/sysctl.d/90-rsp-loadtest.conf
# LOAD TEST SERVER ONLY: any statement slower than 250 ms goes to PostgreSQL's log; rsp-loadstats counts them.
sudo -u postgres psql -qX -c 'ALTER SYSTEM SET log_min_duration_statement = 250' -c 'SELECT pg_reload_conf()' >/dev/null
sudo systemctl daemon-reload
since=`$(date +%s)
sudo systemctl restart remotesupport-platform
for i in `$(seq 60); do curl -sf -o /dev/null http://127.0.0.1:8090/health && break; sleep 1; done
started=`$(sudo journalctl -u remotesupport-platform --since @`$since --no-pager -o cat | grep -o 'DB: [A-Za-z]*' | head -n1)
echo '   the platform says:' `${started:-nothing}
case `$started in *PostgreSQL*) ;; *) echo 'REFUSED: the platform did not say DB: PostgreSQL at start-up. Nothing seeded.'; exit 1;; esac
cd /opt/rsp
if sudo test -f /opt/rsp/data/loadtest-seed.txt; then
  echo '   already seeded - keeping the seed that is there'
else
  sudo -u rsp ./RemoteSupport.Platform seed-loadtest $Companies $People /opt/rsp/data/loadtest-seed.txt | tee /tmp/seed-output.txt
  grep -q 'Database: PostgreSQL' /tmp/seed-output.txt || { echo 'REFUSED: the seed did not go into PostgreSQL.'; exit 1; }
fi
sudo install -m 644 /opt/rsp/data/loadtest-seed.txt /tmp/loadtest-seed.txt
sed -i 's/\r`$//' /tmp/rsp-loadstats.sh
sudo install -m 755 /tmp/rsp-loadstats.sh /usr/local/sbin/rsp-loadstats
rm -f /tmp/rsp-loadstats.sh /tmp/seed-output.txt
echo '   threads setting: $threads'
echo '   ports Caddy may use to reach the platform:' `$(cat /proc/sys/net/ipv4/ip_local_port_range)
"@
ssh $target $remote.Replace("`r`n", "`n")
if ($LASTEXITCODE -ne 0) { throw "the server-side step failed - see above" }

Write-Host "== Bringing the seed file back ==  (passphrase again)" -ForegroundColor Cyan
scp "${target}:/tmp/loadtest-seed.txt" (Join-Path $out "seed.txt")
if ($LASTEXITCODE -ne 0) { throw "could not copy the seed file back" }
Write-Host "`nThe test server is ready, on PostgreSQL: https://$Domain  (seed: $out\seed.txt)" -ForegroundColor Green
