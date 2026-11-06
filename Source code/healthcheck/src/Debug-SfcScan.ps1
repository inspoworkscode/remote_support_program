<#
.SYNOPSIS
    Works out why sfc /verifyonly returns output the health check cannot read.

.DESCRIPTION
    Runs sfc /verifyonly two different ways and shows exactly what each one
    produces, instead of guessing.

    Method 1 is what Get-ClientHealthCheck.ps1 currently does: Start-Process
    with the output redirected to a file. On the test machine this finished in
    about one minute and returned something none of the expected phrases
    matched, which is what this exists to explain.

    Method 2 runs sfc without redirecting anything, capturing its output the
    way PowerShell normally would. sfc's own error message mentions needing
    "a console session", so redirection is the prime suspect.

    NOTHING IS REPAIRED. /verifyonly only looks. This is safe to run.

    Allow up to twenty minutes for both. Run it from an ELEVATED PowerShell.
#>

[CmdletBinding()]
param(
    # Where the raw output is kept, so it can be looked at afterwards.
    [string]$OutputPath = "$env:USERPROFILE\Desktop"
)

$ToolkitVersion = '2.11'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ""
Write-Host "  =====================================================" -ForegroundColor Cyan
Write-Host "    SFC DIAGNOSTIC                      Toolkit: v$ToolkitVersion" -ForegroundColor Cyan
Write-Host "  =====================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Running as administrator : $isAdmin"
Write-Host "  Windows                  : $((Get-CimInstance Win32_OperatingSystem).Caption)"
Write-Host "  System locale            : $((Get-Culture).Name)"
Write-Host ""

if (-not $isAdmin) {
    Write-Host "  STOP. This must be run from an elevated PowerShell." -ForegroundColor Red
    Write-Host "  Right-click PowerShell and choose 'Run as administrator'." -ForegroundColor Red
    Write-Host ""
    return
}

Write-Host "  Nothing will be repaired. /verifyonly only inspects." -ForegroundColor DarkGray
Write-Host "  Allow up to twenty minutes for both methods." -ForegroundColor DarkGray
Write-Host ""

# The phrases the health check looks for. Reported per method so it is obvious
# which one matched, rather than inferring it from the final result.
$patterns = [ordered]@{
    'did not find any integrity violations'  = 'Clean'
    'found integrity violations'             = 'Damaged'
    'could not perform the requested operation' = 'Refused'
    'repair pending'                         = 'RebootRequired'
    'requires reboot'                        = 'RebootRequired'
    'could not start the repair service'     = 'ServiceProblem (not currently matched)'
    'You must be an administrator'           = 'NotElevated'
}

function Show-Result {
    param([string]$Method, [string]$Text, [double]$Mins, $ExitCode)

    Write-Host ""
    Write-Host "  --- $Method ---" -ForegroundColor Yellow
    Write-Host "  Took        : $([math]::Round($Mins,1)) minute(s)"
    Write-Host "  Exit code   : $ExitCode"
    Write-Host "  Output chars: $($Text.Length)"
    Write-Host ""

    $matched = $false
    foreach ($p in $patterns.Keys) {
        if ($Text -match [regex]::Escape($p)) {
            Write-Host "  MATCHED: '$p'  ->  $($patterns[$p])" -ForegroundColor Green
            $matched = $true
        }
    }
    if (-not $matched) {
        Write-Host "  NO KNOWN PHRASE MATCHED - this is the problem." -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "  Raw output:" -ForegroundColor DarkGray
    if ($Text.Trim()) {
        foreach ($line in ($Text -split "`r?`n")) {
            if ($line.Trim()) { Write-Host "    $($line.Trim())" -ForegroundColor Gray }
        }
    }
    else {
        Write-Host "    (nothing at all)" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------
# Method 1 - Start-Process with redirection. What the collector does today.
# ---------------------------------------------------------------------------
Write-Host "  Method 1 of 2: redirected to a file (what the health check does)." -ForegroundColor Cyan
Write-Host "  Working..." -ForegroundColor DarkGray

$o1 = Join-Path $env:TEMP "sfcdiag1-$PID.txt"
$e1 = Join-Path $env:TEMP "sfcdiag1-$PID.err"
$start = Get-Date

$p1 = Start-Process -FilePath "$env:SystemRoot\System32\sfc.exe" `
                    -ArgumentList '/verifyonly' -PassThru -NoNewWindow `
                    -RedirectStandardOutput $o1 -RedirectStandardError $e1
$p1.WaitForExit()

$mins1 = ((Get-Date) - $start).TotalMinutes

# [string] is not decoration. Get-Content on an empty file returns nothing, and
# -replace against that yields an empty ARRAY, not an empty string - so the
# later .Trim() fails with "does not contain a method named 'Trim'". Same family
# as the @() trap: PowerShell's types shift underneath you when a value is empty.
[string]$text1 = ''
if (Test-Path $o1) { [string]$text1 = (Get-Content $o1 -Raw -ErrorAction SilentlyContinue) -replace "`0", '' }
[string]$err1 = ''
if (Test-Path $e1) { [string]$err1 = (Get-Content $e1 -Raw -ErrorAction SilentlyContinue) -replace "`0", '' }

Show-Result -Method 'METHOD 1: Start-Process with redirection' -Text $text1 -Mins $mins1 -ExitCode $p1.ExitCode
if ($err1.Trim()) {
    Write-Host ""
    Write-Host "  Standard error:" -ForegroundColor DarkGray
    Write-Host "    $($err1.Trim())" -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# Method 2 - called directly, no redirection. sfc keeps its console.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  Method 2 of 2: called directly, no redirection." -ForegroundColor Cyan
Write-Host "  sfc will print its own progress below. This is the slow one." -ForegroundColor DarkGray
Write-Host ""

$start2 = Get-Date
# sfc emits UTF-16; the nulls are stripped after joining.
$raw2 = & "$env:SystemRoot\System32\sfc.exe" /verifyonly 2>&1
$exit2 = $LASTEXITCODE
$mins2 = ((Get-Date) - $start2).TotalMinutes
[string]$text2 = (($raw2 | Out-String) -replace "`0", '')

Show-Result -Method 'METHOD 2: direct call, no redirection' -Text $text2 -Mins $mins2 -ExitCode $exit2

# ---------------------------------------------------------------------------
# Keep both, so they can be looked at later or sent on.
# ---------------------------------------------------------------------------
$report = @"
SFC DIAGNOSTIC - $(Get-Date -Format 'yyyy-MM-dd HH:mm')
Machine : $env:COMPUTERNAME
Windows : $((Get-CimInstance Win32_OperatingSystem).Caption)
Locale  : $((Get-Culture).Name)
Admin   : $isAdmin

=== METHOD 1: Start-Process with redirection ===
Minutes  : $([math]::Round($mins1,1))
ExitCode : $($p1.ExitCode)
Output:
$text1
StdErr:
$err1

=== METHOD 2: direct call, no redirection ===
Minutes  : $([math]::Round($mins2,1))
ExitCode : $exit2
Output:
$text2
"@

$savePath = Join-Path $OutputPath 'sfc-diagnostic.txt'
try {
    # No BOM. Written with WriteAllText, as everything in this project is.
    [System.IO.File]::WriteAllText($savePath, $report, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host "  Saved to: $savePath" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "  Could not save the report: $($_.Exception.Message)" -ForegroundColor Yellow
}

Remove-Item $o1, $e1 -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "  =====================================================" -ForegroundColor Cyan
Write-Host "    WHAT THIS TELLS US" -ForegroundColor Cyan
Write-Host "  =====================================================" -ForegroundColor Cyan
Write-Host ""
if ($mins2 -gt $mins1 * 2 -and $text2 -match 'integrity violations') {
    Write-Host "  Redirection is the problem. Method 2 ran properly and" -ForegroundColor Green
    Write-Host "  gave a real answer; method 1 did not." -ForegroundColor Green
}
elseif ($text1 -match 'integrity violations' -and $text2 -match 'integrity violations') {
    Write-Host "  Both worked. The earlier run failed for another reason -" -ForegroundColor Yellow
    Write-Host "  compare the timings above." -ForegroundColor Yellow
}
else {
    Write-Host "  Neither method produced a phrase we recognise. The raw" -ForegroundColor Yellow
    Write-Host "  output above is what we need - send the saved file." -ForegroundColor Yellow
}
Write-Host ""
