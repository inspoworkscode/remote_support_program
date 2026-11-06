<#
.SYNOPSIS
    Finds out why the report step cannot make a PDF, using the real report path.

.DESCRIPTION
    Test-PdfCreation.ps1 proved Edge works. This goes further: it takes an
    actual report file and walks through exactly what the report script does,
    printing every value along the way.

    Run it AFTER a report has been produced.

.EXAMPLE
    .\Debug-ReportPdf.ps1
    .\Debug-ReportPdf.ps1 -ReportPath "C:\ProgramData\HealthCheck\Reports\x.html"
#>

[CmdletBinding()]
param(
    [string]$ReportPath,
    [string]$SearchIn = "$env:ProgramData\HealthCheck\Reports"
)

Write-Host ""
Write-Host "  Report PDF diagnosis" -ForegroundColor Cyan
Write-Host "  ====================" -ForegroundColor Cyan
Write-Host ""

# ---- Find a report to work with --------------------------------------------
if (-not $ReportPath) {
    $found = Get-ChildItem -Path $SearchIn -Filter '*.html' -File -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) {
        Write-Host "  No report found in $SearchIn" -ForegroundColor Red
        Write-Host "  Run a health check first, then run this again." -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }
    $ReportPath = $found.FullName
}

Write-Host "  Report file" -ForegroundColor Green
Write-Host "      $ReportPath" -ForegroundColor DarkGray
Write-Host "      exists: $(Test-Path $ReportPath)" -ForegroundColor DarkGray
if (Test-Path $ReportPath) {
    $sizeKB = [math]::Round((Get-Item $ReportPath).Length / 1KB, 1)
    Write-Host "      size: $sizeKB KB" -ForegroundColor DarkGray
}
Write-Host ""

# ---- Build exactly what the report script builds ----------------------------
$abs     = (Resolve-Path -LiteralPath $ReportPath).ProviderPath
$pdfPath = [System.IO.Path]::ChangeExtension($abs, 'pdf')
$fileUrl = 'file:///' + ($abs -replace '\\', '/')

Write-Host "  Absolute path" -ForegroundColor Green
Write-Host "      $abs" -ForegroundColor DarkGray
Write-Host "  PDF will be written to" -ForegroundColor Green
Write-Host "      $pdfPath" -ForegroundColor DarkGray
Write-Host "  URL given to Edge" -ForegroundColor Green
Write-Host "      $fileUrl" -ForegroundColor DarkGray
Write-Host ""

# ---- Can we write there? ---------------------------------------------------
try {
    $probe = Join-Path (Split-Path -Parent $abs) 'write-probe.tmp'
    'test' | Out-File $probe -Force
    Remove-Item $probe -Force
    Write-Host "  Can write to that folder      yes" -ForegroundColor Green
}
catch {
    Write-Host "  Can write to that folder      NO" -ForegroundColor Red
    Write-Host "      $($_.Exception.Message)" -ForegroundColor DarkGray
}

# ---- Is a PDF already there, and is it locked? -----------------------------
if (Test-Path $pdfPath) {
    Write-Host "  A PDF already exists there" -ForegroundColor Yellow
    try {
        $fs = [System.IO.File]::Open($pdfPath, 'Open', 'ReadWrite', 'None')
        $fs.Close()
        Write-Host "      not locked - can be overwritten" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "      LOCKED - something has it open (a PDF viewer?)" -ForegroundColor Red
        Write-Host "      This alone would stop Edge writing it." -ForegroundColor Red
    }
}
Write-Host ""

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $edge) {
    Write-Host "  Edge not found." -ForegroundColor Red
    exit 1
}

$profileDir = Join-Path $env:TEMP 'edge-pdf-profile'
if (-not (Test-Path $profileDir)) { New-Item -Path $profileDir -ItemType Directory -Force | Out-Null }

# ---- Attempt 1: exactly what the report script does -------------------------
Write-Host "  ATTEMPT 1 - as the report script does it" -ForegroundColor Cyan

Remove-Item $pdfPath -Force -ErrorAction SilentlyContinue
$errFile = Join-Path $env:TEMP 'debug-edge.err'
$outFile = Join-Path $env:TEMP 'debug-edge.out'

$edgeArgs = @(
    '--headless'
    '--disable-gpu'
    '--disable-extensions'
    '--no-first-run'
    '--no-default-browser-check'
    '--no-pdf-header-footer','--print-to-pdf-no-header'
    "--user-data-dir=`"$profileDir`""
    "--print-to-pdf=`"$pdfPath`""
    "`"$fileUrl`""
)

Write-Host "      command line:" -ForegroundColor DarkGray
Write-Host "      msedge.exe $($edgeArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ""

$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    $p = Start-Process -FilePath $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden `
                       -RedirectStandardError $errFile -RedirectStandardOutput $outFile
    $null = $p.WaitForExit(120000)
    $sw.Stop()
    Write-Host "      Edge exited with code $($p.ExitCode) after $([math]::Round($sw.Elapsed.TotalSeconds,1)) seconds" -ForegroundColor DarkGray
}
catch {
    $sw.Stop()
    Write-Host "      Edge would not start: $($_.Exception.Message)" -ForegroundColor Red
}

Start-Sleep -Seconds 2

if (Test-Path $pdfPath) {
    $kb = [math]::Round((Get-Item $pdfPath).Length / 1KB, 1)
    Write-Host "      RESULT: PDF created, $kb KB" -ForegroundColor Green
}
else {
    Write-Host "      RESULT: no PDF" -ForegroundColor Red
}

foreach ($f in @(@{P=$errFile;N='error output'}, @{P=$outFile;N='standard output'})) {
    if (Test-Path $f.P) {
        $c = (Get-Content $f.P -Raw -ErrorAction SilentlyContinue)
        if ($c -and $c.Trim()) {
            Write-Host "      Edge $($f.N):" -ForegroundColor Yellow
            ($c -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 8) |
                ForEach-Object { Write-Host "        $($_.Trim())" -ForegroundColor DarkGray }
        }
    }
}
Write-Host ""

# ---- Attempt 2: no quotes in the arguments ---------------------------------
# Start-Process quoting is a common cause of this. Passing the arguments
# unquoted behaves differently and often works where quoted does not.
Write-Host "  ATTEMPT 2 - same, but without quotes around the arguments" -ForegroundColor Cyan
Remove-Item $pdfPath -Force -ErrorAction SilentlyContinue

$plainArgs = @(
    '--headless'
    '--disable-gpu'
    '--no-first-run'
    '--no-pdf-header-footer','--print-to-pdf-no-header'
    "--user-data-dir=$profileDir"
    "--print-to-pdf=$pdfPath"
    $fileUrl
)

try {
    $p2 = Start-Process -FilePath $edge -ArgumentList $plainArgs -PassThru -WindowStyle Hidden -Wait
    Write-Host "      Edge exited with code $($p2.ExitCode)" -ForegroundColor DarkGray
}
catch { Write-Host "      failed: $($_.Exception.Message)" -ForegroundColor Red }

Start-Sleep -Seconds 2
if (Test-Path $pdfPath) {
    $kb = [math]::Round((Get-Item $pdfPath).Length / 1KB, 1)
    Write-Host "      RESULT: PDF created, $kb KB" -ForegroundColor Green
}
else { Write-Host "      RESULT: no PDF" -ForegroundColor Red }
Write-Host ""

# ---- Attempt 3: call Edge directly, not via Start-Process -------------------
Write-Host "  ATTEMPT 3 - calling Edge directly" -ForegroundColor Cyan
Remove-Item $pdfPath -Force -ErrorAction SilentlyContinue

try {
    & $edge --headless --disable-gpu --no-first-run --no-pdf-header-footer --print-to-pdf-no-header `
            "--user-data-dir=$profileDir" "--print-to-pdf=$pdfPath" $fileUrl 2>&1 | Out-Null
}
catch { Write-Host "      failed: $($_.Exception.Message)" -ForegroundColor Red }

Start-Sleep -Seconds 3
if (Test-Path $pdfPath) {
    $kb = [math]::Round((Get-Item $pdfPath).Length / 1KB, 1)
    Write-Host "      RESULT: PDF created, $kb KB" -ForegroundColor Green
}
else { Write-Host "      RESULT: no PDF" -ForegroundColor Red }
Write-Host ""

# ---- Attempt 4: copy the report somewhere simple first ---------------------
# Long paths and spaces in folder names both break this.
Write-Host "  ATTEMPT 4 - from a short path with no spaces" -ForegroundColor Cyan

$simpleDir  = 'C:\pdftest'
if (-not (Test-Path $simpleDir)) { New-Item -Path $simpleDir -ItemType Directory -Force | Out-Null }
$simpleHtml = Join-Path $simpleDir 'r.html'
$simplePdf  = Join-Path $simpleDir 'r.pdf'
Copy-Item $abs $simpleHtml -Force
Remove-Item $simplePdf -Force -ErrorAction SilentlyContinue

$simpleUrl = 'file:///' + ($simpleHtml -replace '\\', '/')
try {
    $p4 = Start-Process -FilePath $edge -ArgumentList @(
        '--headless','--disable-gpu','--no-first-run','--no-pdf-header-footer','--print-to-pdf-no-header',
        "--user-data-dir=$profileDir","--print-to-pdf=$simplePdf",$simpleUrl
    ) -PassThru -WindowStyle Hidden -Wait
    Write-Host "      Edge exited with code $($p4.ExitCode)" -ForegroundColor DarkGray
}
catch { Write-Host "      failed: $($_.Exception.Message)" -ForegroundColor Red }

Start-Sleep -Seconds 2
if (Test-Path $simplePdf) {
    $kb = [math]::Round((Get-Item $simplePdf).Length / 1KB, 1)
    Write-Host "      RESULT: PDF created, $kb KB" -ForegroundColor Green
    Write-Host "      So the report itself is fine - the problem is the path or folder." -ForegroundColor Yellow
}
else { Write-Host "      RESULT: no PDF" -ForegroundColor Red }

Remove-Item $simpleDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $errFile, $outFile -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "  Send this whole output to Clint." -ForegroundColor Cyan
Write-Host ""
