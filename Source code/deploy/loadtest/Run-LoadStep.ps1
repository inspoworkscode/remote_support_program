# =====================================================================================
# Run one step of the load test: N companies at Clint's profile (2026-10-12). Opens its windows by itself:
#   SERVER      - watches the test server (close it once every load box has finished)
#   LOAD BOX 1, 2 (and 3 if saved) - each plays its share of the companies, ends with a summary and a VERDICT
# Each window asks for your key's passphrase. Leave them all alone until every LOAD BOX window says
# "Results:". Results land in deploy\loadtest\out\results\, named with the step.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Run-LoadStep.ps1 -Companies 10
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Run-LoadStep.ps1 -Companies 10 -Storm
#
# The profile - one busy IT company at the most the seat limit allows (HANDOVER section 116), confirmed by
# Clint on 2026-10-12: 10 technicians all signed in; 500 machines, each with its tray app AND its agent
# (1,000 sockets); 10 remote sessions at once at 200 KB/s (a scrolling screen). So each company is about
# 1,030 connections and 2 MB/s of picture in AND 2 MB/s out.
#
#  -Storm  after the hold, the windows say READY FOR THE RESTART: then run Restart-LoadServer.ps1 once, and
#          they time every machine, console and session coming back, as after every deploy.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 10000)][int]$Companies,
    [switch]$Storm,
    [int]$Hold = 600
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Servers.ps1")
$s = Get-LoadServers

# ---- the profile: change it only with Clint ----
$Machines = 500; $Techs = 10; $SessionsEach = 10; $Kbps = 200

$boxes = @($s.Box1, $s.Box2, $s.Box3) | Where-Object { $_ }
$n = $boxes.Count
# One load box reaches one address through about 64,000 ports of its own (Prepare-LoadBox widens the range);
# past ~60,000 sockets it is the BOX that runs out, which says nothing about the server.
$perBox = [math]::Ceiling($Companies / $n) * ($Machines * 2 + $SessionsEach * 2 + $Techs)
if ($perBox -gt 60000) {
    throw "$Companies companies is about $perBox connections for each of $n load boxes - more than one box can open (about 60,000). Add a load box (Save-Addresses.ps1 asks for a third) first."
}
$seed = Join-Path $PSScriptRoot "out\seed.txt"
if (Test-Path $seed) {
    $seeded = (Get-Content $seed | Where-Object { $_ -match '^load-' }).Count
    if ($Companies -gt $seeded) { throw "The seed holds $seeded companies; this step needs $Companies. Prepare-LoadServer.ps1 -Companies $Companies on a fresh server." }
}

$label = "c$Companies" + $(if ($Storm) { "-storm" } else { "" })
$each = "-Companies $Companies -Machines $Machines -Agents -Techs $Techs -SessionsEach $SessionsEach -Kbps $Kbps -Hold $Hold -Label $label"
if ($Storm) { $each += " -Storm" }

function Open($script, $extra) {
    $file = Join-Path $PSScriptRoot $script
    Start-Process powershell -ArgumentList "-NoExit -ExecutionPolicy Bypass -File `"$file`" $extra"
}
Open "Watch-LoadServer.ps1" "-Label $label"
Start-Sleep -Seconds 2
for ($i = 1; $i -le $n; $i++) { Open "Start-LoadRun.ps1" "-Box $($boxes[$i - 1]) -Part $i/$n $each" }

$conns = $Companies * ($Machines * 2 + $SessionsEach * 2 + $Techs)
$mbs = $Companies * $SessionsEach * $Kbps / 1024
Write-Host "Step: $Companies companies - about $conns connections, $($Companies * $SessionsEach) sessions, $([math]::Round($mbs, 1)) MB/s of picture each way." -ForegroundColor Green
Write-Host "$($n + 1) new windows opened. Type your key's passphrase in EACH of them."
if ($Storm) {
    Write-Host "About 25 minutes in, the LOAD BOX windows say READY FOR THE RESTART. Then run:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File deploy\loadtest\Restart-LoadServer.ps1" -ForegroundColor Yellow
    Write-Host "and leave them until every LOAD BOX window says 'Results:' (about 5 minutes more)."
} else {
    Write-Host "It takes about 25 minutes. Wait until every LOAD BOX window says 'Results:'."
}
