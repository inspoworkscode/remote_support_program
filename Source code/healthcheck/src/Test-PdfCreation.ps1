<#
.SYNOPSIS
    Works out why PDF creation is failing on this machine.

.DESCRIPTION
    Tries every step of the PDF process separately and reports which one breaks.
    Read-only apart from writing a small test file to the temp folder.

.EXAMPLE
    .\Test-PdfCreation.ps1
#>

[CmdletBinding()]
param()

Write-Host ""
Write-Host "  PDF creation test" -ForegroundColor Cyan
Write-Host "  =================" -ForegroundColor Cyan
Write-Host ""

function Say {
    param([string]$Label, [string]$Result, [string]$Colour = 'Green', [string]$Detail = '')
    Write-Host ("  {0,-30} {1}" -f $Label, $Result) -ForegroundColor $Colour
    if ($Detail) { Write-Host "      $Detail" -ForegroundColor DarkGray }
}

# ---- 1. Is Edge there? -----------------------------------------------------
$candidates = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
)
$edge = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $edge) {
    Say 'Microsoft Edge' 'NOT FOUND' 'Red'
    Write-Host ""
    Write-Host "  Edge is not installed on this machine, so PDFs cannot be made." -ForegroundColor Yellow
    Write-Host "  Reports will still be produced as web pages." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
Say 'Microsoft Edge' 'found' 'Green' $edge

try {
    $ver = (Get-Item $edge).VersionInfo.ProductVersion
    Say 'Edge version' $ver 'Green'
} catch { }

# ---- 2. Who are we? --------------------------------------------------------
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
Say 'Running as' $me 'Green'
Say 'TEMP folder' $env:TEMP 'Green'

# ---- 3. Can we write to temp? ----------------------------------------------
$profileDir = Join-Path $env:TEMP 'edge-pdf-profile'
try {
    if (-not (Test-Path $profileDir)) { New-Item -Path $profileDir -ItemType Directory -Force | Out-Null }
    $probe = Join-Path $profileDir 'probe.txt'
    'test' | Out-File $probe -Force
    Remove-Item $probe -Force
    Say 'Write to profile folder' 'ok' 'Green' $profileDir
}
catch {
    Say 'Write to profile folder' 'FAILED' 'Red' $_.Exception.Message
}

# ---- 4. Make a tiny page and try to convert it ------------------------------
$testHtml = Join-Path $env:TEMP 'pdf-test.html'
$testPdf  = Join-Path $env:TEMP 'pdf-test.pdf'
Remove-Item $testPdf -Force -ErrorAction SilentlyContinue

@'
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Test</title></head>
<body style="font-family:sans-serif"><h1>PDF test</h1><p>If you can read this, it worked.</p></body></html>
'@ | Out-File $testHtml -Encoding UTF8 -Force

Say 'Test page written' 'ok' 'Green' $testHtml

$errFile  = Join-Path $env:TEMP 'pdf-test.err'
$fileUrl  = 'file:///' + ($testHtml -replace '\\', '/')

$attempts = @(
    @{ Name = 'headless (classic)'; Args = @('--headless') },
    @{ Name = 'headless=new';       Args = @('--headless=new') },
    @{ Name = 'headless=old';       Args = @('--headless=old') }
)

$worked = $null

foreach ($a in $attempts) {
    Remove-Item $testPdf, $errFile -Force -ErrorAction SilentlyContinue

    $args = @($a.Args) + @(
        '--disable-gpu'
        '--disable-extensions'
        '--no-first-run'
        '--no-default-browser-check'
        '--no-pdf-header-footer','--print-to-pdf-no-header'
        "--user-data-dir=`"$profileDir`""
        "--print-to-pdf=`"$testPdf`""
        "`"$fileUrl`""
    )

    try {
        $p = Start-Process -FilePath $edge -ArgumentList $args -PassThru -WindowStyle Hidden `
                           -RedirectStandardError $errFile
        $null = $p.WaitForExit(60000)
        if (-not $p.HasExited) { try { $p.Kill() } catch { } }
    }
    catch {
        Say $a.Name 'would not start' 'Red' $_.Exception.Message
        continue
    }

    for ($i = 0; $i -lt 10; $i++) {
        if (Test-Path $testPdf) { break }
        Start-Sleep -Milliseconds 500
    }

    if (Test-Path $testPdf) {
        $size = [math]::Round((Get-Item $testPdf).Length / 1KB, 1)
        Say $a.Name "WORKED ($size KB)" 'Green'
        if (-not $worked) { $worked = $a.Name }
    }
    else {
        $detail = ''
        if (Test-Path $errFile) {
            $e = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
            if ($e) { $detail = ($e -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim() }
        }
        Say $a.Name 'no PDF produced' 'Yellow' $detail
    }
}

Remove-Item $testHtml, $testPdf, $errFile -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($worked) {
    Write-Host "  PDF creation works on this machine using: $worked" -ForegroundColor Green
    Write-Host "  Send this output to Clint so the tool can be set to use it." -ForegroundColor DarkGray
}
else {
    Write-Host "  Edge could not produce a PDF in any mode on this machine." -ForegroundColor Yellow
    Write-Host "  Reports will still be produced as web pages, which can be" -ForegroundColor Yellow
    Write-Host "  printed to PDF manually with Ctrl+P." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Send this whole output to Clint." -ForegroundColor DarkGray
}
Write-Host ""
