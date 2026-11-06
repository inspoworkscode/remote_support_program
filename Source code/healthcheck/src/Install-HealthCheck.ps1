<#
.SYNOPSIS
    Installs the health check onto a client machine and schedules it monthly.

.DESCRIPTION
    Copies the collector to a permanent location on the machine and creates a
    Windows Scheduled Task that runs it automatically once a month, as SYSTEM,
    whether or not anyone is logged in.

    After this runs once, the machine looks after itself. Nobody needs to visit
    again, and the technician never has to remember to run anything.

    Results build up in the install folder, one file per month, ready to be
    pulled down through your existing remote support tools.

.NOTES
    Requires: PowerShell 5.1, administrator rights
    Version:  1.0

.EXAMPLE
    .\Install-HealthCheck.ps1 -Organisation "Riverside Guest House" -UserOrRole "Reception"

.EXAMPLE
    .\Install-HealthCheck.ps1 -Organisation "Acme Accounting" -UserOrRole "Sarah" -DayOfMonth 3 -TimeOfDay "07:30"
#>

[CmdletBinding()]
param(
    # The organisation this machine belongs to. Reports are produced per organisation.
    [Parameter(Mandatory = $true)]
    [string]$Organisation,

    # Who uses this PC, or what it is for: "Reception", "Sarah", "Accounts PC".
    # Shown to the client instead of the machine name.
    [Alias('ClientName')]
    [string]$UserOrRole,

    # Where reports are emailed after each monthly run.
    #   Your own address  - for clients on a support contract
    #   The client's own  - for once-off customers who read it themselves
    # Leave blank to keep reports on the machine only.
    [string]$EmailTo,

    # 'Support' sends a technical email with the data files attached.
    # 'Client' sends a plainly worded email with just the report.
    [ValidateSet('Support','Client')]
    [string]$RecipientType = 'Support',

    # Shown at the end of a client-facing email.
    #
    # Blank, for the same reason as in New-ClientReport.ps1: this installer runs on every tenant's
    # customer machines, and a default here is another IT company's name on a reseller's report.
    # Install-Client.ps1 passes the tenant's details from brand.json; a standalone install brands
    # itself by passing them.
    [string]$CompanyName = '',

    # What the monthly report says it is from. The platform fills these from the tenant's own
    # portal settings. Blank is meaningful and is preserved: no tagline means the report shows
    # no tagline.
    [string]$CompanyTag   = '',
    [string]$CompanyEmail = '',
    [string]$CompanyPhone = '',

    # Full path to the tenant's own logo, copied into the install folder so reports built on this
    # machine carry it. Left empty, the toolkit's own logo is used and a standalone install is
    # unchanged. The name it lands under is worked out from this, keeping the real extension.
    [string]$BrandLogoPath = '',

    # This install is managed by The Remote Support Program: the machine holds no mail settings
    # because the server sends the report. Only changes what is printed - the standalone toolkit's
    # "no email configured" warning is accurate there and alarming here, where it is the design.
    [switch]$PlatformManaged,

    # Where the monthly run uploads its results, e.g. http://192.168.1.115:8090. The device
    # identity it authenticates with is read at run time from the remote support client's own
    # device.json, so nothing secret is written here.
    [string]$PlatformUrl = '',

    # Hex colours for the report, e.g. '#00B0F0'. Anything else is ignored by the report builder
    # rather than written into its stylesheet.
    [string]$AccentColor  = '#00b0f0',
    [string]$PrimaryColor = '#7a2e8e',
    [string]$SmtpServer,
    [int]$SmtpPort = 587,
    [string]$SmtpFrom,
    [string]$SmtpUser,
    [string]$SmtpPassword,
    [switch]$NoSsl,

    # Where the tool lives on the client machine.
    [string]$InstallPath = "$env:ProgramData\HealthCheck",

    # Which day of the month the run happens.
    # Leave at 0 and it is chosen from the day of the week this is installed:
    # Monday installs report on the 1st, Tuesday the 2nd, and so on to Friday
    # the 5th. That spreads the monthly emails across five days by itself,
    # instead of every client machine mailing at once on the 1st.
    [ValidateRange(0,28)]
    [int]$DayOfMonth = 0,

    # What time it runs. 24 hour format.
    [string]$TimeOfDay = "08:00",

    # Name of the scheduled task as it appears in Windows.
    [string]$TaskName = "Monthly Maintenance and Health Check",

    # A key=value file holding the shared email settings, so a technician only
    # ever types the organisation and the role.
    [string]$SettingsFile,

    # Skip the immediate test run.
    [switch]$NoTestRun,

    # Leave scheduled tasks from earlier versions alone. They run a second health check every
    # month against the same folder, so removing them is the default; this is the escape hatch
    # for a machine where one was set up deliberately.
    [switch]$KeepOtherTasks,

    # What to do when the health check is already installed on this machine.
    #
    #   Ask       - ask, and keep them if there is nobody to ask (an unattended or remote install)
    #   Keep      - keep what is already configured here
    #   Overwrite - replace it with this installer's values, which is what it always used to do
    #
    # The scripts are replaced either way; that is what a reinstall is for. This is only about
    # the settings configured on the machine afterwards.
    [ValidateSet('Ask','Keep','Overwrite')]
    [string]$ExistingSettings = 'Ask',

    # Read the settings, check them, report what WOULD be installed, then stop.
    # Changes nothing and needs no administrator rights. Use it to check an
    # email-settings.txt before installing. A [bool] rather than a [switch] so
    # it survives being passed through powershell.exe -File.
    [bool]$ValidateOnly = $false
)


# Version of this toolkit. Printed at run time so a stale copy cannot hide.
$ToolkitVersion = '2.11'

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Text) Write-Host "  $Text" -ForegroundColor Cyan }
function Write-Good { param([string]$Text) Write-Host "  $Text" -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host "  $Text" -ForegroundColor Red }

Write-Host ""
Write-Host "  =====================================================" -ForegroundColor Cyan
Write-Host "    INSTALL MONTHLY HEALTH CHECK" -ForegroundColor Cyan
Write-Host "  =====================================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# Checks before we touch anything
# ---------------------------------------------------------------------------

# The email checks that used to sit here have moved to just after the settings
# file is read. Here they could never work: the menu does not pass the password
# or the support address on the command line, so both were still empty at this
# point and every check passed. The file was read fifty lines later, loaded
# CHANGE-ME as the real password, and the install went ahead - then confirmed
# "Email password stored and verified". See "Email settings are checked after
# they are read" in docs/DECISIONS.md.

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $ValidateOnly) {
    Write-Bad "This must be run as administrator."
    Write-Host ""
    exit 1
}

# ---------------------------------------------------------------------------
# Locate everything that needs installing
# ---------------------------------------------------------------------------

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-File {
    param([string]$Name)
    foreach ($p in @(
        (Join-Path $here $Name),
        (Join-Path $here "src\$Name"),
        (Join-Path (Split-Path -Parent $here) "src\$Name"),
        (Join-Path (Split-Path -Parent $here) $Name)
    )) { if (Test-Path $p) { return $p } }
    return $null
}

$needed = @(
    'Get-ClientHealthCheck.ps1',
    'Invoke-ClientMaintenance.ps1',
    'New-ClientReport.ps1',
    'Invoke-MonthlyRun.ps1',
    # Beside the run, because the run removes its own schedule when the server says this machine
    # is no longer meant to have it (2026-09-28).
    'Uninstall-HealthCheck.ps1'
)

$found   = @{}
$missing = @()
foreach ($n in $needed) {
    $f = Find-File $n
    if ($f) { $found[$n] = $f } else { $missing += $n }
}

if ($missing.Count -gt 0) {
    Write-Bad "These files are missing and must be alongside the installer:"
    foreach ($m in $missing) { Write-Host "    $m" }
    Write-Host ""
    exit 1
}

# ---------------------------------------------------------------------------
# Shared email settings, so only the organisation and role are ever typed
# ---------------------------------------------------------------------------

if (-not $SettingsFile) {
    foreach ($p in @(
        (Join-Path $here 'email-settings.txt'),
        (Join-Path (Split-Path -Parent $here) 'email-settings.txt')
    )) { if (Test-Path $p) { $SettingsFile = $p; break } }
}

$settingsRead = $false
if ($SettingsFile -and (Test-Path $SettingsFile)) {
    $settingsRead = $true
    foreach ($line in (Get-Content $SettingsFile -ErrorAction SilentlyContinue)) {
        if ($line -match '^\s*#') { continue }
        if ($line -notmatch '=')   { continue }
        $k, $v = $line -split '=', 2
        $k = $k.Trim(); $v = $v.Trim()
        if (-not $v) { continue }
        switch ($k) {
            # The file's address is OUR address, and is only ever used for a
            # support install. A client install takes its address from the
            # question asked at install time and nowhere else - otherwise a
            # client install with no address given quietly sent the client's
            # report to us, worded for the client. See "The report recipient is
            # chosen at install" in docs/DECISIONS.md.
            'EmailTo'      { if (-not $EmailTo -and $RecipientType -eq 'Support') { $EmailTo = $v } }
            'SmtpServer'   { if (-not $SmtpServer)   { $SmtpServer   = $v } }
            'SmtpPort'     { if ($SmtpPort -eq 587)  { $SmtpPort     = [int]$v } }
            'SmtpUser'     { if (-not $SmtpUser)     { $SmtpUser     = $v } }
            'SmtpFrom'     { if (-not $SmtpFrom)     { $SmtpFrom     = $v } }
            'SmtpPassword' { if (-not $SmtpPassword) { $SmtpPassword = $v } }
            'UseSsl'       { if ($v -match 'false|no|0') { $NoSsl = $true } }
            'DayOfMonth'   { if ($DayOfMonth -eq 0 -and $v -notmatch '^auto$') { $DayOfMonth = [int]$v } }
            'TimeOfDay'    { if ($TimeOfDay -eq '08:00') { $TimeOfDay = $v } }
        }
    }
    Write-Good "Email settings read from $(Split-Path -Leaf $SettingsFile)"
}

# ---------------------------------------------------------------------------
# An install that is already on this machine
# ---------------------------------------------------------------------------
# Re-running the client installer re-runs this, and it used to rebuild config.json from its
# parameters alone. The scheduled task was never duplicated - one fixed name, registered with
# /F - but everything configured on the machine after the first install was silently lost: the
# organisation went back to the machine name, the schedule moved to whatever weekday it happened
# to be, and the whole Email section went with it, including an SMTP password that exists nowhere
# else because it is encrypted to this machine.
#
# So the scripts are always replaced, because that is what a reinstall is for, and the settings
# are kept unless somebody actually says otherwise.

$configPath = Join-Path $InstallPath 'config.json'
$existing = $null
if (Test-Path $configPath) {
    try { $existing = Get-Content $configPath -Raw -ErrorAction Stop | ConvertFrom-Json }
    catch {
        Write-Bad "config.json is present but could not be read ($($_.Exception.Message))."
        Write-Bad "Treating this as a fresh install - the old settings cannot be preserved."
        $existing = $null
    }
}

$keepSettings = $false
if ($existing) {
    $mode = $ExistingSettings
    if ($mode -eq 'Ask') {
        # Keeping is the answer when there is nobody to ask. A remote or silent install has no
        # console - Read-Host throws there rather than hanging - and an installer that stops for
        # a question on an unattended run is worse than one that makes the safe choice.
        $mode = 'Keep'
        $canAsk = $false
        try { $canAsk = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected } catch { }
        # A settings check asks nothing; it reports what an unattended reinstall would do.
        if ($ValidateOnly) { $canAsk = $false }

        if ($canAsk) {
            Write-Host ""
            Write-Host "  The health check is already installed on this machine." -ForegroundColor Yellow
            if ($existing.InstalledOn)   { Write-Step "Installed on : $($existing.InstalledOn)" }
            if ($existing.Organisation)  { Write-Step "Organisation : $($existing.Organisation)" }
            if ($existing.Schedule)      { Write-Step "Runs         : day $($existing.Schedule.DayOfMonth) at $($existing.Schedule.TimeOfDay)" }
            if ($existing.ReportsGoTo)   { Write-Step "Reports to   : $($existing.ReportsGoTo)" }
            if ($existing.Email)         { Write-Step "Email        : configured ($($existing.Email.SmtpServer))" }
            Write-Host ""
            Write-Host "  The scripts are updated either way, and the branding is always refreshed." -ForegroundColor Gray
            try {
                $answer = Read-Host "  Keep these settings? [Y] keep / [n] replace with this installer's"
                if ($answer -and $answer.Trim() -match '^(n|no)$') { $mode = 'Overwrite' }
            }
            catch { }   # no console after all; keep, as above
        }
        else {
            Write-Step "Already installed here; keeping the existing settings (nothing to ask on an unattended run)."
        }
    }
    $keepSettings = ($mode -eq 'Keep')
}

if ($keepSettings) {
    # Only what a person or the platform configured after the install. Branding is deliberately
    # NOT in this list: it is the tenant's current identity, and a reinstall is the one chance a
    # machine gets to catch up with a rebrand.
    if ($existing.Organisation)  { $Organisation  = [string]$existing.Organisation }
    if ($existing.UserOrRole)    { $UserOrRole    = [string]$existing.UserOrRole }
    if ($existing.ReportsGoTo)   { $EmailTo       = [string]$existing.ReportsGoTo }
    if ($existing.RecipientType) { $RecipientType = [string]$existing.RecipientType }
    if ($existing.Schedule) {
        if ($null -ne $existing.Schedule.DayOfMonth) { $DayOfMonth = [int]$existing.Schedule.DayOfMonth }
        if ($existing.Schedule.TimeOfDay)            { $TimeOfDay  = [string]$existing.Schedule.TimeOfDay }
    }
    Write-Good "Keeping the settings already on this machine."
}
elseif ($existing) {
    Write-Step "Replacing the existing settings with this installer's."
}

# ---------------------------------------------------------------------------
# Check the email settings - AFTER they are read, BEFORE anything is installed
#
# These used to run at the very top of the script, before the settings file had
# been read, so they only ever saw what was typed on the command line. The menu
# types neither the password nor the support address, so every check passed
# and CHANGE-ME was installed as the real password.
#
# Here they also come after the settings KEPT from an existing install, which the toolkit's v2.9
# does not have. Placed where the toolkit put them - straight after the settings file - they would
# have repeated its mistake in a new place: a reinstall of a Client install that passes no address
# refused for "no client address", then kept one; and a kept Email section, carried over whole
# below, is a mail server whether or not one was typed this time.
# ---------------------------------------------------------------------------

$problems = New-Object System.Collections.ArrayList

if ($EmailTo) { $EmailTo = $EmailTo.Trim() }

# The same condition that carries the Email section across further down. When it holds, the mail
# server, login and password are the ones already stored and proven on this machine.
$emailCarried = [bool]($keepSettings -and $existing.Email -and -not $SmtpServer)

# A client install exists to send the client their report. Without an address
# it would install, run every month, and send nothing to anybody.
if ($RecipientType -eq 'Client' -and -not $EmailTo) {
    [void]$problems.Add("This is set to email the client, but no client address was given.")
}

if ($EmailTo) {
    # A typo here sends one client's report to a stranger.
    foreach ($addr in ($EmailTo -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if ($addr -notmatch '^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$') {
            [void]$problems.Add("This does not look like a valid email address: $addr")
        }
    }

    if (-not $emailCarried) {
        # An address with no way to send to it is never what anybody meant.
        if (-not $SmtpServer) {
            [void]$problems.Add("Reports are meant to go to $EmailTo, but no mail server is set in email-settings.txt (SmtpServer=).")
        }
        if ($SmtpUser -and -not $SmtpPassword) {
            [void]$problems.Add("A mail username is set but the password is empty (SmtpPassword=).")
        }
        if ($SmtpPassword -eq 'CHANGE-ME') {
            [void]$problems.Add("The mail password in email-settings.txt is still CHANGE-ME - the placeholder from the template.")
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host ""
    foreach ($p in $problems) { Write-Bad $p }
    Write-Host ""
    Write-Host "  Nothing has been installed." -ForegroundColor Yellow
    if ($settingsRead) {
        Write-Host "  Settings were read from: $SettingsFile" -ForegroundColor DarkGray
    }
    elseif ($SettingsFile) {
        Write-Host "  No settings file was found at: $SettingsFile" -ForegroundColor DarkGray
    }
    Write-Host "  Fix the above, then run the installer again. Without this, the monthly" -ForegroundColor Yellow
    Write-Host "  report would fail to send and nobody would notice for a month." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

if ($ValidateOnly) {
    # Report what WOULD happen, without touching anything. The password is
    # never printed - only whether one is present and how long it is.
    Write-Host ""
    Write-Good "Settings check passed. Nothing has been installed."
    Write-Host ""
    Write-Host "    Organisation   : $Organisation"
    Write-Host "    User or role   : $UserOrRole"
    Write-Host "    Recipient type : $RecipientType"
    if ($EmailTo -and $emailCarried) {
        Write-Host "    Reports go to  : $EmailTo"
        Write-Host "    Mail settings  : kept from the existing install ($($existing.Email.SmtpServer))"
    }
    elseif ($EmailTo) {
        Write-Host "    Reports go to  : $EmailTo"
        Write-Host "    Mail server    : $SmtpServer : $SmtpPort  (SSL: $(-not $NoSsl))"
        Write-Host "    Mail user      : $SmtpUser"
        $pwNote = if ($SmtpPassword) { "present, $($SmtpPassword.Length) characters" } else { 'none' }
        Write-Host "    Mail password  : $pwNote"
    }
    elseif ($PlatformManaged) {
        Write-Host "    Reports go to  : the support server, which sends them"
    }
    else {
        Write-Host "    Reports go to  : nowhere - kept on the machine only" -ForegroundColor Yellow
    }
    if ($settingsRead)     { Write-Host "    Settings file  : $SettingsFile" }
    elseif ($SettingsFile) { Write-Host "    Settings file  : NOT FOUND at $SettingsFile" -ForegroundColor Yellow }
    elseif (-not $PlatformManaged) { Write-Host "    Settings file  : none found" -ForegroundColor Yellow }
    Write-Host ""
    exit 0
}

# ---------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Which day of the month?
#
# Spreading the runs across the first five days keeps fifty-odd machines from
# all mailing within the same hour, which is what trips a shared mail server's
# hourly send limit.
#
# Resolved here rather than just before the schedule is created, because config.json is written
# in between and has to record the day that was actually chosen - otherwise a reinstall that
# means to keep the schedule reads back a 0 and picks a new day from today's weekday.
# ---------------------------------------------------------------------------

$dayChosenAutomatically = $false
if ($DayOfMonth -eq 0) {
    $installedOn = (Get-Date).DayOfWeek
    $DayOfMonth = switch ($installedOn) {
        'Monday'    { 1 }
        'Tuesday'   { 2 }
        'Wednesday' { 3 }
        'Thursday'  { 4 }
        'Friday'    { 5 }
        'Saturday'  { 5 }   # nearest working day back
        'Sunday'    { 1 }   # nearest working day forward
        default     { 1 }
    }
    $dayChosenAutomatically = $true
}

Write-Step "Organisation: $Organisation"
Write-Step "User or role: $UserOrRole"
Write-Step "Machine  : $env:COMPUTERNAME"
Write-Step "Installing to: $InstallPath"
Write-Host ""

if (-not (Test-Path $InstallPath)) {
    New-Item -Path $InstallPath -ItemType Directory -Force | Out-Null
}

foreach ($n in $needed) {
    $dest = Join-Path $InstallPath $n
    Copy-Item -Path $found[$n] -Destination $dest -Force
    Unblock-File -LiteralPath $dest -ErrorAction SilentlyContinue
}
$target = Join-Path $InstallPath 'Invoke-MonthlyRun.ps1'

# The logo travels with the install so reports built on this machine are branded.
# A tenant logo supplied by the platform wins; otherwise the toolkit's own is used, so a
# standalone install behaves exactly as before.
$logo = $null
if ($BrandLogoPath -and (Test-Path $BrandLogoPath)) {
    $logo = $BrandLogoPath
    # Keep the real extension: the platform ships JPGs as often as PNGs, and the report builder
    # picks the media type from the file name. Calling a JPG "logo.png" produced a data URI
    # declaring the wrong type.
    $BrandLogoName = 'logo' + ([System.IO.Path]::GetExtension($BrandLogoPath)).ToLower()
}
elseif ($PlatformManaged) {
    # No fallback here, ever. This install belongs to a tenant, and the only logo that may appear
    # on their customer's report is their own. A tenant who has not uploaded one is an ordinary
    # case - the report falls back to their name as text - whereas reaching for whatever logo.png
    # happens to be lying beside this script would put one IT company's mark on another's report.
    # That is exactly what used to happen, because build.ps1 staged the toolkit's own logo into
    # every tenant's client payload.
    $BrandLogoName = ''
    # Clear any logo a previous install left behind, or the report builder's own search would
    # find it sitting beside the scripts and brand the report with it anyway.
    Get-ChildItem $InstallPath -Filter 'logo.*' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "   No tenant logo supplied; reports will carry the company name as text." -ForegroundColor Yellow
}
else {
    $logo = Find-File 'logo.png'
    if (-not $logo) {
        foreach ($p in @((Join-Path $here 'templates\logo.png'),
                         (Join-Path (Split-Path -Parent $here) 'templates\logo.png'))) {
            if (Test-Path $p) { $logo = $p; break }
        }
    }
    $BrandLogoName = 'logo.png'
}
if ($logo) { Copy-Item -Path $logo -Destination (Join-Path $InstallPath $BrandLogoName) -Force }

# ---------------------------------------------------------------------------
# Settings file. Secrets are encrypted with the Windows machine key so they are
# not sitting in plain text, and are kept off the command line where Task
# Scheduler would display them.
#
# Use a dedicated, send-only mailbox for this. An administrator on this machine
# could still recover the password, so it must not be an account you care about.
# ---------------------------------------------------------------------------

function Protect-Secret {
    param([string]$Plain)
    if (-not $Plain) { return $null }
    Add-Type -AssemblyName System.Security
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Plain)
    $prot  = [System.Security.Cryptography.ProtectedData]::Protect(
                $bytes, $null,
                [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Convert]::ToBase64String($prot)
}

$config = [ordered]@{
    Organisation = $Organisation
    UserOrRole   = $UserOrRole
    # The date of the FIRST install, kept across reinstalls, because "installed on" answering
    # "today" on a machine that has been running this for a year is not worth recording.
    InstalledOn   = if ($keepSettings -and $existing.InstalledOn) { [string]$existing.InstalledOn }
                    else { (Get-Date).ToString('yyyy-MM-dd') }
    UpdatedOn     = (Get-Date).ToString('yyyy-MM-dd')
    ReportsGoTo   = $EmailTo
    RecipientType = $RecipientType
    CompanyName   = $CompanyName
    # Recorded so a reinstall can put the machine back on the same day and time. Without this
    # the day was only ever known to the scheduled task, and a reinstall picked a new one from
    # whatever weekday it happened to be - quietly moving a client's reporting date.
    Schedule      = [ordered]@{ DayOfMonth = $DayOfMonth; TimeOfDay = $TimeOfDay }
}

# Who the report says it is from. On the platform these come from the tenant, so a reseller's
# client sees their own IT company on the report rather than Anywhere IT's hardcoded details.
# Written even when blank, so the monthly run can tell "not configured" from "deliberately empty"
# - an IT company with no tagline gets no tagline, not somebody else's.
if ($PlatformUrl) {
    # No credentials here on purpose. The monthly run reads the machine's identity from the
    # remote support client's device.json at run time, so config.json holds nothing worth
    # stealing and a reinstall that reissues that identity keeps working.
    $config['Platform'] = [ordered]@{ BaseUrl = $PlatformUrl.TrimEnd('/') }
}

$config['Branding'] = [ordered]@{
    CompanyName  = $CompanyName
    CompanyTag   = $CompanyTag
    CompanyEmail = $CompanyEmail
    CompanyPhone = $CompanyPhone
    LogoFile     = $BrandLogoName
    AccentColor  = $AccentColor
    PrimaryColor = $PrimaryColor
}

# Carried over whole when nothing new was supplied. The stored password is encrypted to this
# machine and there is no plaintext copy anywhere to rebuild it from, so anything short of the
# intact section loses it - and losing it is silent until the month's report fails to send.
if ($keepSettings -and $existing.Email -and -not $SmtpServer) {
    $config['Email'] = $existing.Email
}
elseif ($EmailTo -and $SmtpServer) {
    $config['Email'] = [ordered]@{
        To                = $EmailTo
        From              = if ($SmtpFrom) { $SmtpFrom } else { $SmtpUser }
        SmtpServer        = $SmtpServer
        Port              = $SmtpPort
        UseSsl            = (-not $NoSsl)
        User              = $SmtpUser
        PasswordProtected = Protect-Secret $SmtpPassword
    }
}

# $configPath was resolved at the top, where the existing install is read.
$utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 4), $utf8NoBom)

# Prove the password can be read back. If encryption succeeded but decryption
# fails, the monthly send would connect with no credentials and the server would
# answer "authentication required" - which looks like a wrong password.
if ($config.Email -and $config.Email.PasswordProtected) {
    try {
        Add-Type -AssemblyName System.Security -ErrorAction Stop
        $check = [System.Text.Encoding]::UTF8.GetString(
                    [System.Security.Cryptography.ProtectedData]::Unprotect(
                        [Convert]::FromBase64String($config.Email.PasswordProtected), $null,
                        [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
        # A kept password has nothing typed this time to compare with - $SmtpPassword is empty -
        # so for that one, decrypting at all is the proof. Comparing it with the empty string
        # refused every reinstall of a machine that had a mail password, after config.json was
        # written and before the task was.
        if ($emailCarried) {
            if (-not $check) { throw 'the stored password decrypted to nothing' }
            Write-Good "Email password kept from the existing install, and still readable ($($check.Length) characters)."
        }
        else {
            if ($check -ne $SmtpPassword) { throw 'the password did not survive the round trip' }
            Write-Good "Email password stored and verified ($($check.Length) characters)."
        }
    }
    catch {
        Write-Bad "The email password could not be stored safely on this machine."
        Write-Host "  $($_.Exception.Message)"
        Write-Host "  The monthly report would fail to send. Nothing else has been changed."
        Write-Host ""
        exit 1
    }
}

Write-Good "Files installed."
if ($config.Email) {
    Write-Good "Reports will be emailed to: $EmailTo"
    if ($RecipientType -eq 'Client') {
        Write-Host "  Written for the client to read themselves." -ForegroundColor DarkGray
    }
    else {
        Write-Host "  Technical email, with the data files attached." -ForegroundColor DarkGray
    }
}
elseif ($PlatformManaged) {
    # Not a misconfiguration. On the platform the machine deliberately holds no mail settings -
    # the server sends the report, so no customer machine stores a mail password. Saying "no
    # email configured" here reads as a fault and sent Clint looking for one.
    Write-Good "No mail password is stored on this machine, by design."
    Write-Host "  Reports are uploaded, and $companyOwns support server sends them." -ForegroundColor DarkGray
}
else               { Write-Host "  No email configured - reports stay on this machine." -ForegroundColor Yellow }

# Lock the folder down - it runs as SYSTEM and should not be user-writable
try {
    icacls $InstallPath /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)RX" | Out-Null

    # The Reports folder must be writable by more than SYSTEM, or the finished
    # PDF cannot be moved into place. Edge itself works in a temp folder.
    $reportsDir = Join-Path $InstallPath 'Reports'
    if (-not (Test-Path $reportsDir)) { New-Item -Path $reportsDir -ItemType Directory -Force | Out-Null }
    icacls $reportsDir /grant "Users:(OI)(CI)M" | Out-Null

    Write-Good "Folder permissions set."
}
catch {
    Write-Host "  Could not set folder permissions - continuing anyway." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Schedule it
# ---------------------------------------------------------------------------

# A tenant who has not filled in a company name is a legitimate case - the branding defaults are
# blank on purpose, so that no reseller is ever told somebody else's name - but every sentence
# that leans on the name needs a form that still reads when it is absent. Without this the
# installer announced that "  's support server sends the report instead."
$companyOwns = if ($CompanyName -and $CompanyName.Trim()) { "$($CompanyName.Trim())'s" } else { 'the' }

# The day itself was resolved before config.json was written; see the note there.
$ordinal = switch ($DayOfMonth) {
    1 { '1st' } 2 { '2nd' } 3 { '3rd' } 21 { '21st' } 22 { '22nd' } 23 { '23rd' }
    default { "$($DayOfMonth)th" }
}

Write-Host ""
Write-Step "Creating the monthly schedule..."

$action = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{0}\" -WorkPath \"{1}\"' -f $target, $InstallPath

# schtasks is used rather than the newer cmdlets because it behaves identically
# on every version of Windows this will land on.
$args = @(
    '/Create',
    '/TN', "`"$TaskName`"",
    '/TR', "`"$action`"",
    '/SC', 'MONTHLY',
    '/D',  "$DayOfMonth",
    '/ST', $TimeOfDay,
    '/RU', 'SYSTEM',
    '/RL', 'HIGHEST',
    '/F'
)

# Built with the New-ScheduledTask commands where possible, because a task
# created by schtasks.exe often cannot be modified afterwards - which is why
# every setting was being refused.
$scheduled = $false

if (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue) {
    try {
        $taskAction = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -WorkPath "{1}"' -f $target, $InstallPath)

        # Monthly triggers need a date-based schedule; this builds one that
        # fires on the chosen day of every month.
        $taskTrigger = New-ScheduledTaskTrigger -Daily -At $TimeOfDay
        $taskTrigger.RandomDelay = 'PT30M'

        $taskSettings = New-ScheduledTaskSettingsSet `
            -StartWhenAvailable `
            -DontStopIfGoingOnBatteries `
            -AllowStartIfOnBatteries `
            -ExecutionTimeLimit (New-TimeSpan -Hours 4) `
            -MultipleInstances IgnoreNew

        $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

        Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $taskTrigger `
            -Settings $taskSettings -Principal $taskPrincipal -Force -ErrorAction Stop | Out-Null

        # Swap the daily trigger for a monthly one. The XML route is the only
        # reliable way to express "day N of every month" in PowerShell 5.1.
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $xml  = Export-ScheduledTask -TaskName $TaskName
        # The StartBoundary must be the NEXT real occurrence, not today. A start
        # time already in the past leaves Windows reporting no next run time at
        # all, and the task simply never fires.
        $now       = Get-Date
        $parts     = $TimeOfDay -split ':'
        $candidate = Get-Date -Year $now.Year -Month $now.Month -Day $DayOfMonth `
                              -Hour ([int]$parts[0]) -Minute ([int]$parts[1]) -Second 0
        if ($candidate -le $now) { $candidate = $candidate.AddMonths(1) }
        $startBoundary = $candidate.ToString('yyyy-MM-ddTHH:mm:ss')

        $monthlyTrigger = @"
      <CalendarTrigger>
        <StartBoundary>$startBoundary</StartBoundary>
        <Enabled>true</Enabled>
        <RandomDelay>PT30M</RandomDelay>
        <ScheduleByMonth>
          <DaysOfMonth><Day>$DayOfMonth</Day></DaysOfMonth>
          <Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months>
        </ScheduleByMonth>
      </CalendarTrigger>
"@
        $newXml = [regex]::Replace($xml, '<Triggers>.*?</Triggers>', "<Triggers>`n$monthlyTrigger    </Triggers>", 'Singleline')
        Register-ScheduledTask -TaskName $TaskName -Xml $newXml -User 'SYSTEM' -Force -ErrorAction Stop | Out-Null

        # A task Windows will not schedule is worth catching now, not in a month.
        $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
        if (-not $info -or -not $info.NextRunTime) {
            throw 'The task was created but Windows reports no next run time.'
        }

        $scheduled = $true
        Write-Good "Scheduled for the $ordinal of every month at $TimeOfDay."
        Write-Good "First run: $($info.NextRunTime.ToString('dddd d MMMM yyyy, HH:mm'))"
        # Explains why that time is not the one on the line above. The trigger carries a 30
        # minute random delay so fifty machines do not all wake, defragment and mail at once,
        # and a first run reading 07:08 against a 06:45 schedule otherwise looks like a fault.
        Write-Host "  Start times are spread over the following 30 minutes, so machines do not all begin together." -ForegroundColor DarkGray
        Write-Good "Catches up automatically if the machine was off that day."
        Write-Good "Will not run more than four hours, and runs on battery too."
    }
    catch {
        Write-Host "  Could not use the modern scheduler: $($_.Exception.Message)" -ForegroundColor DarkGray
        Write-Host "  Falling back to the older method..." -ForegroundColor DarkGray
    }
}

if (-not $scheduled) {
    $result = & schtasks.exe @args 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Bad "Could not create the scheduled task."
        Write-Host "  $result"
        Write-Host ""
        exit 1
    }
    Write-Good "Scheduled for the $ordinal of every month at $TimeOfDay."
    Write-Host "  Note: will not catch up if the machine is off that day." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Tasks an earlier version left behind
# ---------------------------------------------------------------------------
# Registering our task replaces a task OF THE SAME NAME. One registered under any other name is
# invisible to that, and goes on firing forever.
#
# DESKTOP-OPHQIDH had exactly this: ours, plus a "Monthly Health Check" from the standalone
# toolkit that predates this repository, still set for the 1st of every month, still running a
# full collection as SYSTEM and writing into this same folder under the organisation "Test" -
# which then sits there waiting to be picked up by a report that was never meant to include it.
#
# Matched on what a task RUNS rather than what it is called. A name catches an unrelated Lenovo
# task with the word "Maintenance" in it and misses anything somebody renamed; an action pointing
# at one of our scripts, or into our install folder, is ours whoever named it.

if (-not $KeepOtherTasks) {
    # Scoped to THIS install folder, and that scoping is the whole safety of it.
    #
    # The first version matched any task whose action named one of our scripts, wherever it lived.
    # Run against a different InstallPath - which is exactly what the test harness does, and what
    # an install to a custom folder does - that matched the machine's REAL task, pointing at
    # C:\ProgramData\HealthCheck, and deleted it. It did precisely that to DESKTOP-OPHQIDH.
    #
    # A task is a leftover of ours only if it runs something out of the folder we are installing
    # into. Anything outside it belongs to another install and is none of our business.
    $strays = @(Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object { $_.TaskPath -notlike "\Microsoft\*" -and $_.TaskName -ne $TaskName } |
        Where-Object {
            $_.Actions | Where-Object { "$($_.Execute) $($_.Arguments)" -like "*$InstallPath*" }
        })

    foreach ($s in $strays) {
        Write-Host ""
        Write-Host "  Found an older health check task: $($s.TaskPath)$($s.TaskName)" -ForegroundColor Yellow
        foreach ($a in $s.Actions) { Write-Host "    it ran: $($a.Execute) $($a.Arguments)" -ForegroundColor DarkGray }
        try {
            Unregister-ScheduledTask -TaskName $s.TaskName -TaskPath $s.TaskPath -Confirm:$false -ErrorAction Stop
            Write-Good "Removed it - it would have run a second health check every month."
        }
        catch {
            Write-Bad "Could not remove it: $($_.Exception.Message)"
            Write-Host "  Remove it by hand, or this machine runs two health checks a month." -ForegroundColor Yellow
        }
    }
}

if ($dayChosenAutomatically) {
    Write-Host "  (chosen because today is $((Get-Date).DayOfWeek) - this spreads the" -ForegroundColor DarkGray
    Write-Host "   monthly emails across the first five days of the month)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# Prove it works now, rather than finding out next month
# ---------------------------------------------------------------------------

if (-not $NoTestRun) {
    Write-Host ""
    Write-Step "Running it once now to confirm it works..."
    Write-Host ""

    # Quick mode for the test run: proves the whole chain works without making
    # the technician wait ten minutes for the system file check.
    # -Command not -File. With -File, powershell.exe binds positionally and
    # passes $true as the literal string "$true".
    $runCmd = '& "{0}" -WorkPath "{1}" -Quick $true' -f $target, $InstallPath
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $runCmd

    $produced = Get-ChildItem -Path $InstallPath -Filter '*.json' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($produced) {
        Write-Good "Test run produced: $($produced.Name)"
    }
    else {
        Write-Bad "Test run did not produce a results file. Investigate before leaving this machine."
    }
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "  =====================================================" -ForegroundColor Green
Write-Host "    INSTALLED" -ForegroundColor Green
Write-Host "  =====================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  On the $ordinal of every month at $TimeOfDay this machine will,"
Write-Host "  on its own and with nobody logged in:"
Write-Host ""
Write-Host "    1. Clean up, optimise its drives and check Windows files"
Write-Host "    2. Run the health check"
Write-Host "    3. Build a PDF report"
if ($config.Email)          { Write-Host "    4. Email the report to $EmailTo" }
elseif ($PlatformManaged)   { Write-Host "    4. Keep the report here for $companyOwns support server to collect" }
else                        { Write-Host "    4. Keep the report on this machine - no email is configured" }
Write-Host ""
Write-Host "  Results collect here:"
Write-Host "    $InstallPath"
Write-Host ""
# The toolkit's v2.11 fixed these lines for its own kit, pointing at START HERE.bat options 4 and
# 5. That menu is not part of this product and is never on a customer machine, so the lines here
# say what is true of THIS install: where the results go and how it is stopped. A message about
# where results are is part of the product - the toolkit's technician followed a wrong one exactly.
if ($PlatformManaged) {
    Write-Host "  Each month's results are also uploaded to $companyOwns support server."
    Write-Host ""
    Write-Host "  To remove it, switch it off for this machine or its client in the console or portal."
}
else {
    Write-Host "  To build a report from them at any time, run New-ClientReport.ps1 with"
    Write-Host "  -InputPath `"$InstallPath`"."
    Write-Host ""
    Write-Host "  To remove it later, delete the scheduled task '$TaskName'."
}
Write-Host ""

# Said outright. Without it the exit code is whatever the last native command inside this script
# happened to return, and the agent service (HealthCheckKeeper) reads that code as the verdict.
exit 0
