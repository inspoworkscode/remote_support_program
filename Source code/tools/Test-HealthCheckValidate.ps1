<#
    Does Install-HealthCheck.ps1 refuse the settings it should, and accept the ones it should?

    The toolkit's v2.9 moved its email checks to AFTER the settings file is read - before, they ran
    first and could never fire, so CHANGE-ME was installed as the real password. Our copy moves
    them further still, to after the settings KEPT from an existing install, because a reinstall
    that keeps a Client install's address would otherwise be refused for having none.

    Every case runs the real installer with -ValidateOnly $true, which reads and checks everything
    and then stops. It needs no administrator rights and changes nothing - the last check proves
    that - so this runs from an ordinary PowerShell:

        powershell -ExecutionPolicy Bypass -File tools\Test-HealthCheckValidate.ps1
#>
param(
    # Another copy of the installer to test instead of ours - to prove a case catches what it is for.
    [string]$Installer
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# One variable, not two: PowerShell names are case-insensitive, so $installer IS $Installer.
if (-not $Installer) { $Installer = Join-Path $root 'healthcheck\src\Install-HealthCheck.ps1' }
$work = Join-Path $env:TEMP ("rs-hcvalidate-" + (Get-Date -Format 'HHmmss'))
$pass = 0; $fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "  PASS  $what $(if ($detail) { '- ' + $detail })" }
    else     { $script:fail++; Write-Host "  FAIL  $what $(if ($detail) { '- ' + $detail })" -ForegroundColor Red }
}
function Q([string]$s) { "'" + $s.Replace("'", "''") + "'" }

# -Command rather than -File, because -ValidateOnly is a [bool] and a bool does not survive -File.
# A fresh process each time, as the agent service runs it.
function Validate([string]$installPath, [string]$extra) {
    $cmd = "& $(Q $installer) -Organisation 'Acme Accounting' -UserOrRole 'Sarah' " +
           "-InstallPath $(Q $installPath) -TaskName 'RS VALIDATE TEST - never registered' " +
           "-NoTestRun -ValidateOnly `$true $extra; exit `$LASTEXITCODE"
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $cmd 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Out = $out }
}
function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

$fresh = Join-Path $work 'fresh'
$kept  = Join-Path $work 'kept'
New-Item -ItemType Directory -Path $fresh, $kept -Force | Out-Null

try {
    Write-Host ''
    Write-Host '== exactly what the agent service passes: platform-managed, no mail =='
    $r = Validate $fresh "-PlatformManaged -ExistingSettings Keep -PlatformUrl 'http://127.0.0.1:1'"
    Check 'it passes' ($r.Code -eq 0) "exit $($r.Code)"
    Check 'and says the server sends the report' ($r.Out -match 'the support server, which sends them')

    Write-Host ''
    Write-Host '== an address with no way to send to it =='
    $r = Validate $fresh "-EmailTo 'reports@acme.example'"
    Check 'it is refused' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'for having no mail server' ($r.Out -match 'no mail server is set')

    Write-Host ''
    Write-Host '== a client install with no client address =='
    $r = Validate $fresh "-RecipientType Client -SmtpServer 'mail.acme.example'"
    Check 'it is refused' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'for having no client address' ($r.Out -match 'no client address was given')

    Write-Host ''
    Write-Host '== a mistyped address =='
    $r = Validate $fresh "-EmailTo 'reports@acme' -SmtpServer 'mail.acme.example'"
    Check 'it is refused' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'and the bad address is named' ($r.Out -match 'valid email address: reports@acme')

    # The v2.9 fault itself: nothing on the command line, everything in the file.
    $settings = Join-Path $work 'email-settings.txt'
    Write-Utf8 $settings "EmailTo=support@acme.example`r`nSmtpServer=mail.acme.example`r`nSmtpUser=reports@acme.example`r`nSmtpPassword=CHANGE-ME`r`n"
    Write-Host ''
    Write-Host '== the template password, read from the settings file =='
    $r = Validate $fresh "-SettingsFile $(Q $settings)"
    Check 'it is refused' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'for being CHANGE-ME' ($r.Out -match 'still CHANGE-ME')

    Write-Utf8 $settings "EmailTo=support@acme.example`r`nSmtpServer=mail.acme.example`r`nSmtpUser=reports@acme.example`r`nSmtpPassword=not-a-real-password`r`n"
    Write-Host ''
    Write-Host '== a complete settings file, support install =='
    $r = Validate $fresh "-SettingsFile $(Q $settings)"
    Check 'it passes' ($r.Code -eq 0) "exit $($r.Code)"
    Check 'reports go to the address in the file' ($r.Out -match 'Reports go to  : support@acme\.example')
    Check 'the password is never printed' ($r.Out -notmatch 'not-a-real-password')

    Write-Host ''
    Write-Host '== the same file, client install, no address typed =='
    $r = Validate $fresh "-SettingsFile $(Q $settings) -RecipientType Client"
    Check 'it is refused: the file''s address is ours, never the client''s' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'for having no client address' ($r.Out -match 'no client address was given')

    # The case that decided where the check sits in OUR copy. A Client install is already here,
    # with its address and a stored mail section; the reinstall passes neither, as the agent's
    # does not. Checked before the kept settings load, it was refused for having no address.
    Write-Utf8 (Join-Path $kept 'config.json') (@'
{
  "Organisation": "Acme Accounting", "UserOrRole": "Sarah",
  "ReportsGoTo": "owner@acme.example", "RecipientType": "Client",
  "Schedule": { "DayOfMonth": 7, "TimeOfDay": "06:45" },
  "Email": { "To": "owner@acme.example", "SmtpServer": "mail.acme.example", "Port": 587, "UseSsl": true, "User": "", "PasswordProtected": null },
  "Platform": { "BaseUrl": "http://127.0.0.1:1" }
}
'@)
    Write-Host ''
    Write-Host '== a reinstall that keeps an existing client install =='
    $before = @(Get-ChildItem $kept -Force | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
    $r = Validate $kept "-RecipientType Client -ExistingSettings Keep -PlatformManaged"
    Check 'it passes' ($r.Code -eq 0) "exit $($r.Code)"
    Check 'with the kept address' ($r.Out -match 'Reports go to  : owner@acme\.example')
    Check 'and the kept mail settings' ($r.Out -match 'kept from the existing install \(mail\.acme\.example\)')

    Write-Host ''
    Write-Host '== and nothing was touched =='
    $after = @(Get-ChildItem $kept -Force | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
    Check 'the existing install folder is exactly as it was' ((($before -join ';') -eq ($after -join ';'))) "$($after.Count) file(s)"
    Check 'the fresh folder is still empty' (@(Get-ChildItem $fresh -Force).Count -eq 0)
    Check 'no task was registered' (-not (Get-ScheduledTask -TaskName 'RS VALIDATE TEST - never registered' -ErrorAction SilentlyContinue))
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

# Fewer than this means a case stopped early, and "no failures" would mean "no test".
$expected = 21
Write-Host ''
if ($pass + $fail -lt $expected) {
    Write-Host "INCONCLUSIVE - only $($pass + $fail) of $expected checks ran." -ForegroundColor Red
    exit 1
}
if ($fail -eq 0) { Write-Host "ALL $pass CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "FAILED $fail of $($pass + $fail)" -ForegroundColor Red
exit 1
