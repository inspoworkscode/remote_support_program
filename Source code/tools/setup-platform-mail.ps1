<#
    Configures the mailbox THE PRODUCT writes from, and proves it works.

        powershell -ExecutionPolicy Bypass -File .\tools\setup-platform-mail.ps1

    Add -Show to print the current settings and change nothing.

    WHAT THIS IS FOR.

    Trial warnings go to a reseller's admins seven days out, one day out, and on the day their
    trial lapses - which is the day every one of their customer machines stops connecting. That
    mail has to come from the PRODUCT, not from a tenant's own mailbox: a tenant's SMTP is for
    writing to THEIR customers (the monthly health check), and a company still on trial has almost
    certainly not configured it. See PlatformSettings and HANDOVER section 20.

    THE PASSWORD IS NEVER TYPED INTO A CHAT, WRITTEN TO A FILE, OR PASSED AS AN ARGUMENT.

    It is read straight into a SecureString, converted in memory only, handed to the platform on
    the child process's own environment block, and wiped. An argument would end up in shell history
    and in the process list; a file would end up in a backup. The platform then encrypts it with
    Data Protection, exactly as a tenant's mail password is encrypted - which ties it to data/keys
    and to SetApplicationName, so read section 10 of the handover before moving either.

    The running server does NOT need restarting afterwards: PlatformMailService reads the settings
    from the database each time it sends, so the next trial pass picks them up.
#>
param(
    [switch]$Show,
    [string]$SmtpHost,
    [int]$Port = 587,
    [string]$User,
    [string]$From,
    [string]$FromName = 'The Remote Support Program',
    [string]$CopyTo = 'admin@theremotesupportprogram.com',
    [switch]$NoSsl
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\RemoteSupport.Platform\bin\Release\net10.0\RemoteSupport.Platform.exe'
if (-not (Test-Path $exe)) {
    throw "No platform build at $exe. Build it first, or run tools\ship-build.ps1."
}

# Runs the platform CLI, optionally with the password on the child's environment only.
function Invoke-Platform {
    param([string[]]$CliArgs, [string]$Password)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    # A quoted Arguments STRING, not ArgumentList: that property is .NET Core only and this runs
    # under Windows PowerShell 5.1, where it is null and the call fails with "you cannot call a
    # method on a null-valued expression". Quoting every argument matters here because one of them
    # is "The Remote Support Program".
    $psi.Arguments = ($CliArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $psi.WorkingDirectory = Split-Path -Parent $exe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($Password) { $psi.EnvironmentVariables['RSP_SMTP_PASSWORD'] = $Password }
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
    $p.WaitForExit(60000)
    # Drop the copy we put on the child's environment as soon as it has been handed over.
    if ($Password) { $psi.EnvironmentVariables['RSP_SMTP_PASSWORD'] = '' }
    [pscustomobject]@{ Code = $p.ExitCode; Output = $out }
}

Write-Host ''
Write-Host '== Current platform mailbox ==' -ForegroundColor Cyan
(Invoke-Platform -CliArgs @('platform-mail','show')).Output -split "`n" |
    Where-Object { $_ -notmatch 'Microsoft\.EntityFrameworkCore|The query uses' -and $_.Trim() } |
    ForEach-Object { Write-Host ('   ' + $_.Trim()) -ForegroundColor Gray }

if ($Show) { Write-Host ''; Write-Host 'Nothing was changed.' -ForegroundColor Gray; exit 0 }

Write-Host ''
Write-Host '== The address the product writes from ==' -ForegroundColor Cyan
Write-Host '   This is what a reseller sees in the From line of a trial warning.' -ForegroundColor DarkGray
if (-not $From)     { $From = Read-Host '   From address' }
if (-not $From)     { throw 'A from address is required.' }
if (-not $User)     { $User = Read-Host ("   SMTP username [{0}]" -f $From); if (-not $User) { $User = $From } }
if (-not $SmtpHost) {
    # A sensible guess from the address domain, which the provider usually matches.
    $guess = 'mail.' + ($From -replace '.*@','')
    $SmtpHost = Read-Host ("   SMTP host [{0}]" -f $guess); if (-not $SmtpHost) { $SmtpHost = $guess }
}

Write-Host ''
Write-Host ("   host    : {0}:{1}  ssl={2}" -f $SmtpHost, $Port, (-not $NoSsl)) -ForegroundColor Gray
Write-Host ("   user    : {0}" -f $User) -ForegroundColor Gray
Write-Host ("   from    : {0} ({1})" -f $From, $FromName) -ForegroundColor Gray
Write-Host ("   copy to : {0}" -f $CopyTo) -ForegroundColor Gray

Write-Host ''
Write-Host '== Password ==' -ForegroundColor Cyan
Write-Host '   Typed here, not into a chat. It is never echoed, never written to a file and never' -ForegroundColor DarkGray
Write-Host '   passed as an argument - it goes on the child process environment and is then wiped.' -ForegroundColor DarkGray
$secure = Read-Host '   Mailbox password' -AsSecureString
if (-not $secure -or $secure.Length -eq 0) { throw 'No password entered; nothing was changed.' }

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)

    $setArgs = @('platform-mail','set','--host',$SmtpHost,'--port',"$Port",'--user',$User,
                 '--from',$From,'--from-name',$FromName,'--copy-to',$CopyTo)
    $setArgs += $(if ($NoSsl) { '--nossl' } else { '--ssl' })

    Write-Host ''
    Write-Host '== Saving ==' -ForegroundColor Cyan
    $r = Invoke-Platform -CliArgs $setArgs -Password $plain
    if ($r.Code -ne 0) { Write-Host $r.Output; throw 'The platform CLI refused the settings.' }
    Write-Host '   Saved and encrypted.' -ForegroundColor Green
}
finally {
    # Wipe both copies rather than waiting for the garbage collector.
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    if ($plain) { $plain = $null }
    Remove-Variable plain -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '== Proving it ==' -ForegroundColor Cyan
Write-Host '   Mail that has never been tried is mail that does not work, and the first time these' -ForegroundColor DarkGray
Write-Host '   settings matter is the day a customer is lapsing.' -ForegroundColor DarkGray
$testTo = Read-Host ("   Send a test to [{0}]" -f $CopyTo)
if (-not $testTo) { $testTo = $CopyTo }

$t = Invoke-Platform -CliArgs @('platform-mail','test',$testTo)
$line = ($t.Output -split "`n" | Where-Object { $_ -match 'Sent to|Could not send' } | Select-Object -First 1)
if ($t.Code -eq 0) {
    Write-Host ('   ' + $line.Trim()) -ForegroundColor Green
    Write-Host ''
    Write-Host '   Check that it arrived. If it did, trial warnings will reach your resellers.' -ForegroundColor Gray
}
else {
    Write-Host ('   ' + $(if ($line) { $line.Trim() } else { 'the test failed' })) -ForegroundColor Red
    Write-Host ''
    Write-Host '   The settings are saved; only the send failed. Common causes, in order:' -ForegroundColor Gray
    Write-Host '     - the password is wrong, or the mailbox needs an app password rather than the login one' -ForegroundColor Gray
    Write-Host '     - the provider wants port 465 with implicit SSL instead of 587' -ForegroundColor Gray
    Write-Host '     - the From address is not one this mailbox is allowed to send as' -ForegroundColor Gray
    Write-Host '   Re-run this script to try different settings.' -ForegroundColor Gray
}

Write-Host ''
Write-Host 'The running server needs no restart - it reads these each time it sends.' -ForegroundColor Cyan
Write-Host 'To see what the trial pass would do right now:' -ForegroundColor Gray
Write-Host '   .\src\RemoteSupport.Platform\bin\Release\net10.0\RemoteSupport.Platform.exe trial-notices' -ForegroundColor White
Write-Host ''
