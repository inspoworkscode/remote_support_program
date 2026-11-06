# =====================================================================================
# Run every harness that has a database of its own, on SQLite or on PostgreSQL, one after another,
# and write one table of results (HANDOVER 118, PostgreSQL step 2).
#
#   powershell -ExecutionPolicy Bypass -File tools\Run-DbHarnesses.ps1 -Postgres
#   powershell -ExecutionPolicy Bypass -File tools\Run-DbHarnesses.ps1                (SQLite)
#   ... -Only kbcheck,quotecheck        just those
#
# Each harness's full output goes to tools\dbruns\<sqlite|postgres>\<name>.txt and the table to
# tools\dbruns\<sqlite|postgres>\results.md. One at a time: most of them build the platform, and two
# builds of it at once trip over each other. The demos (servers that run until stopped) and the load
# test are left out.
# =====================================================================================
param([switch]$Postgres, [string[]]$Only, [int]$TimeoutMinutes = 20)
$root = Split-Path $PSScriptRoot -Parent
$mode = if ($Postgres) { "postgres" } else { "sqlite" }
$out = Join-Path $PSScriptRoot "dbruns\$mode"
New-Item -ItemType Directory -Path $out -Force | Out-Null

$skip = @("guestwebdemo", "workflowdemo", "loadtest", "pgcheck")
$all = Get-ChildItem (Join-Path $PSScriptRoot "*\*.csproj") |
    Where-Object { (Get-Content $_.FullName -Raw) -match 'Common\\HarnessDb\.cs' } |
    ForEach-Object { $_.Directory.Name } | Where-Object { $_ -notin $skip } | Sort-Object
if ($Only) { $names = $Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() }; $all = $all | Where-Object { $_ -in $names } }

$env:RSP_TEST_DB = if ($Postgres) { "postgres" } else { "" }
$rows = @()
$i = 0
foreach ($name in $all) {
    $i++
    $log = Join-Path $out "$name.txt"
    $proj = Join-Path $PSScriptRoot "$name\$name.csproj"
    Write-Host ("[{0}/{1}] {2} on {3} ..." -f $i, $all.Count, $name, $mode) -NoNewline
    $started = Get-Date
    $p = Start-Process -FilePath "dotnet" -ArgumentList "run -c Release --project `"$proj`"" -WorkingDirectory $root `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -NoNewWindow -PassThru
    $done = $p.WaitForExit($TimeoutMinutes * 60 * 1000)
    if (-not $done) { try { & taskkill /PID $p.Id /T /F | Out-Null } catch { } }
    $secs = [int]((Get-Date) - $started).TotalSeconds
    $text = (Get-Content $log -Raw -ErrorAction SilentlyContinue) + (Get-Content "$log.err" -Raw -ErrorAction SilentlyContinue)
    # Two ways the harnesses end: "N of M (checks) passed" and "N passed, F failed".
    $passed = $null; $total = $null
    $m1 = [regex]::Matches($text, '(\d+) of (\d+)( checks)? passed') | Select-Object -Last 1
    $m2 = [regex]::Matches($text, '(\d+) passed, (\d+) failed') | Select-Object -Last 1
    if ($m1) { $passed = [int]$m1.Groups[1].Value; $total = [int]$m1.Groups[2].Value }
    elseif ($m2) { $passed = [int]$m2.Groups[1].Value; $total = $passed + [int]$m2.Groups[2].Value }
    $fails = ([regex]::Matches($text, '(?m)^\s*FAIL\s+(.*)$') | ForEach-Object { $_.Groups[1].Value.Trim() }) | Select-Object -First 3
    $result = if (-not $done) { "TIMED OUT" } elseif ($null -ne $total) { "$passed of $total" } else { "no summary (exit $($p.ExitCode))" }
    $ok = $done -and $null -ne $total -and $passed -eq $total -and -not $fails
    Write-Host (" {0} ({1}s)" -f $result, $secs) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    $rows += [pscustomobject]@{ Harness = $name; Result = $result; Ok = $ok; Seconds = $secs; Fails = ($fails -join " | ") }
}

$md = @("# Harnesses on $mode - $(Get-Date -Format 'yyyy-MM-dd HH:mm')", "", "| Harness | Result | | Seconds | First failures |", "|---|---|---|---|---|")
$md += $rows | ForEach-Object { "| $($_.Harness) | $($_.Result) | $(if ($_.Ok) { 'ok' } else { '**FAIL**' }) | $($_.Seconds) | $($_.Fails -replace '\|', '/') |" }
$md += "", "$(@($rows | Where-Object Ok).Count) of $(@($rows).Count) harnesses passed."
$md | Set-Content (Join-Path $out "results.md") -Encoding UTF8
Write-Host "`n$(@($rows | Where-Object Ok).Count) of $(@($rows).Count) harnesses passed. Table: $out\results.md"
