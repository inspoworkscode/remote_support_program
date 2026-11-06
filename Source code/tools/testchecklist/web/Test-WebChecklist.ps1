<#
    Tests the browser version of the checklist against the exe, in headless Edge (Chrome's engine).
    Nothing here touches the share.

        powershell -ExecutionPolicy Bypass -File .\tools\testchecklist\web\Test-WebChecklist.ps1

    1. The page, seeded with the real docs\TEST-3.6.md and with lines the EXE wrote, runs its
       self-test in the browser's own scratch storage: reading, saving, a fault with a picture,
       a status change, the report.
    2. What the page made of checks.md is compared line for line with what the exe made of it.
    3. The lines the page wrote are handed to the exe, whose report must contain them.
    4. Screenshots of the page, into the output folder.
#>
param([string]$Out = (Join-Path $env:TEMP 'tc-webtest'))
$ErrorActionPreference = 'Stop'

$web = $PSScriptRoot
$tool = Split-Path $web
$repo = (Resolve-Path (Join-Path $tool '..\..')).Path
$page = Join-Path $web 'TestChecklist.html'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Needs Edge or Chrome.' }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out | Out-Null
$checks = [IO.File]::ReadAllText((Join-Path $repo 'docs\TEST-3.6.md'))
$png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII='
$pass = 0; $fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "PASS  $name" } else { $script:fail++; Write-Host "FAIL  $name  $detail" -ForegroundColor Red }
}

# Lines exactly as the exe writes them: seven decimal places, then one cut off half way.
$exeLines = @(
    '{"type":"result","id":"exe-7digits","at":"2026-09-29T13:50:45.6880272Z","by":"Ryan","machine":"DESK-A","check":"1","status":"works","note":"from the exe"}',
    '{"type":"item","id":"exeitem0000000000000000000000001","at":"2026-09-29T13:51:00.1234567Z","by":"Ryan","machine":"DESK-A","kind":"fault","seq":1,"title":"Exe fault","details":"x","area":"Console","level":"Serious","check":"2"}',
    '{"type":"result","id":"half","at":"2026-09-29T13:52:00Z","by":"Ryan","machine":"DESK-A","check":"3","sta'
) -join "`n"

. (Join-Path $web 'Cdp.ps1')
$port = 8791
$server = Start-StaticServer $Out $port

function PageWith($test, [string]$name) {
    $json = ($test | ConvertTo-Json -Compress -Depth 6).Replace('</', '<\/')
    $html = [IO.File]::ReadAllText($page).Replace('<!--TEST-->', "<script>window.CHECKLIST_TEST = $json;</script>")
    [IO.File]::WriteAllText((Join-Path $Out $name), $html, (New-Object Text.UTF8Encoding $false))
    return "http://localhost:$port/$name"
}

$nextPort = 9340
function Open-Page([string]$url) {
    $script:nextPort++
    return Start-Browser $edge $url (Join-Path $Out "profile-$script:nextPort") $script:nextPort
}

try {
    # ---- 1. the self-test in the page -----------------------------------------------------------
    $url = PageWith @{ mode = 'selftest'; me = 'WebTest'; device = 'test'; checks = $checks;
                       files = @{ 'Ryan on DESK-A.jsonl' = $exeLines }; png = $png } 'selftest.html'
    $b = Open-Page $url
    try {
        $title = Wait-Title $b 'SELFTEST' 90
        $self = Invoke-Js $b "document.getElementById('selftest')?.textContent || ''"
        $written = Invoke-Js $b "document.getElementById('written')?.textContent || ''"
        $webParsed = Invoke-Js $b "document.getElementById('parsed')?.textContent || ''"
        $report = Invoke-Js $b "document.getElementById('report')?.textContent || ''"
    } finally { Stop-Browser $b }
    if (-not $self) { Check 'the page ran its self-test' $false "title '$title'"; throw 'stop' }
    [IO.File]::WriteAllText((Join-Path $Out 'web-report.md'), $report, (New-Object Text.UTF8Encoding $false))
    foreach ($line in $self -split "`n") {
        if ($line -match '^(PASS|FAIL)  (.*)$') { Check ("page: " + $Matches[2]) ($Matches[1] -eq 'PASS') }
    }

    # ---- 2. checks.md read the same way by both ------------------------------------------------
    $exe = Join-Path $tool 'bin\Release\net10.0-windows\TestChecklist.exe'
    & dotnet build (Join-Path $tool 'testchecklist.csproj') -c Release --nologo -v q | Out-Null
    $share = Join-Path $Out 'share'
    New-Item -ItemType Directory -Path (Join-Path $share 'data') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $share 'checks.md'), $checks, (New-Object Text.UTF8Encoding $false))
    [IO.File]::WriteAllText((Join-Path $share 'data\WebTest on browser-test.jsonl'), $written, (New-Object Text.UTF8Encoding $false))
    Start-Process $exe -ArgumentList '--root', "`"$share`"", '--me', 'ExeTest', '--snapshot', "`"$Out\exe`"" -Wait
    $exeParsed = [IO.File]::ReadAllText((Join-Path $Out 'exe\parsed.txt')).Replace("`r", '').TrimEnd()
    $a = $exeParsed -split "`n"; $bb = $webParsed.Replace("`r", '').TrimEnd() -split "`n"
    $diff = @(); for ($i = 0; $i -lt [Math]::Max($a.Count, $bb.Count); $i++) { if ($a[$i] -ne $bb[$i]) { $diff += "line $($i+1)`n  exe: $($a[$i])`n  web: $($bb[$i])" } }
    Check "checks.md read identically by both ($($a.Count) lines)" ($diff.Count -eq 0) (($diff | Select-Object -First 3) -join "`n")

    # ---- 3. the exe reads what the page wrote ----------------------------------------------------
    $exeReport = [IO.File]::ReadAllText((Join-Path $Out 'exe\report-all.md'))
    Check 'the exe sees the page''s results' ($exeReport.Contains('from the browser') -and $exeReport.Contains('line one'))
    Check 'the exe sees the page''s fault, and its closing' ($exeReport -match 'F2 . Web fault with a picture' -and $exeReport -match 'status . Closed')
    Check 'the exe credits the page''s writer' ($exeReport -match 'Raised by WebTest on browser-test')

    # ---- 4. pictures of it -----------------------------------------------------------------------
    $shots = @(
        @{ n = 'web-1-check.png';  s = @{ section = 'A'; check = '2' } },
        @{ n = 'web-2-faults.png'; s = @{ tab = 'fault'; item = 'F1' } },
        @{ n = 'web-3-dialog.png'; s = @{ section = 'I'; dialog = 'item' } },
        @{ n = 'web-4-export.png'; s = @{ dialog = 'export' } }
    )
    foreach ($sh in $shots) {
        $u = PageWith @{ mode = 'demo'; me = 'Clint'; device = 'demo'; checks = $checks; select = $sh.s; png = $png;
                         files = @{ 'Ryan on DESK-A.jsonl' = $exeLines; 'WebTest on browser-test.jsonl' = $written } } ('demo-' + $sh.n + '.html')
        $b = Open-Page $u
        try { [void](Wait-Title $b 'DEMO READY' 60); Start-Sleep -Milliseconds 600; Save-Screenshot $b (Join-Path $Out $sh.n) } finally { Stop-Browser $b }
    }
    Check 'screenshots taken' ((Get-ChildItem $Out -Filter 'web-*.png').Count -eq $shots.Count)
}
catch { if ("$_" -ne 'stop') { Check 'the test ran to the end' $false "$_" } }
finally { Stop-StaticServer $server }

Write-Host ''
Write-Host "$pass passed, $fail failed. Output: $Out"
if ($fail -gt 0) { exit 1 }
