# =====================================================================================
# Run one load test from a load box against the test server, and bring the results back.
# Run-LoadStep.ps1 opens one of these per load box; it is rarely run by hand.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Start-LoadRun.ps1 -Box 5.6.7.8 -Companies 10 -Machines 500 -Agents -Techs 10 -SessionsEach 10
#
# With several load boxes, run it once per box AT THE SAME TIME, with -Part 1/2 and -Part 2/2 (or 1/3, 2/3, 3/3):
# each takes its share of the companies. The summary and the 5-second readings land in deploy\loadtest\out\results\.
#
#  -Companies     use only the first N companies of the seed, shared between the boxes (0 = all)
#  -Machines      pretend machines per company
#  -Agents        each machine also connects its agent service, as a real install does (twice the sockets)
#  -Techs         technicians signed in per company (at most what the seed has; default 1)
#  -SessionsEach  remote sessions open at once PER COMPANY (or -Sessions: in total for this box)
#  -Kbps          picture per session, KB/s (default 200, a scrolling screen)
#  -Ramp          seconds to bring everything up, per stage (default 300)
#  -Hold          seconds at full load (default 600)
#  -Storm         then wait for Restart-LoadServer.ps1 and time everybody coming back
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Box,
    [string]$Domain = "loadtest.theremotesupportprogram.com",
    [string]$Part = "1/1",
    [int]$Companies = 0,
    [int]$Machines = 20,
    [switch]$Agents,
    [int]$Techs = 1,
    [int]$Sessions = 0,
    [int]$SessionsEach = -1,
    [int]$Kbps = 200,
    [int]$Ramp = 300,
    [int]$Hold = 600,
    [switch]$Storm,
    [string]$Label = "",
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
$host.UI.RawUI.WindowTitle = "LOAD BOX $Box - part $Part"
if ($Domain -in @("theremotesupportprogram.com", "www.theremotesupportprogram.com")) { throw "-Domain is the LIVE address. The load test runs against loadtest.theremotesupportprogram.com." }
$results = Join-Path $PSScriptRoot "out\results"
New-Item -ItemType Directory -Path $results -Force | Out-Null
$name = "run-$(Get-Date -Format yyyyMMdd-HHmmss)$(if ($Label) { "-$Label" })-part$($Part.Replace('/', 'of'))"

$opts = "--server https://$Domain --seed seed.txt --part $Part --machines $Machines --techs $Techs --kbps $Kbps --ramp $Ramp --hold $Hold --out $name.csv"
if ($SessionsEach -ge 0) { $opts += " --sessions-each $SessionsEach" } else { $opts += " --sessions $Sessions" }
if ($Agents) { $opts += " --agents" }
if ($Storm) { $opts += " --storm" }
if ($Companies -gt 0) { $opts += " --companies $Companies" }

Write-Host "== Running on $Box ==  (type your key's passphrase now; leave this window open until the summary)" -ForegroundColor Cyan
ssh -t "$User@$Box" "cd ~/loadtest && ulimit -n `$(ulimit -Hn) && ./loadtest run $opts 2>&1 | tee $name.txt"

Write-Host ""
Write-Host "############################################################" -ForegroundColor Yellow
Write-Host "##  THE RUN IS OVER. TYPE YOUR PASSPHRASE ONCE MORE, so    ##" -ForegroundColor Yellow
Write-Host "##  the results are copied to your PC.                     ##" -ForegroundColor Yellow
Write-Host "############################################################" -ForegroundColor Yellow
[console]::Beep(880, 300); [console]::Beep(660, 300)
scp "${User}@${Box}:loadtest/$name.*" $results
if ($LASTEXITCODE -eq 0) { Write-Host "`nResults: $results\$name.txt and .csv" -ForegroundColor Green }
