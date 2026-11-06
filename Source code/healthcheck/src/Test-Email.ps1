<#
.SYNOPSIS
    Tests sending using exactly the settings the monthly run uses.

.DESCRIPTION
    Reads config.json, decrypts the stored password the same way the monthly run
    does, and attempts a send - reporting precisely what fails and where.

    This tests the real path, not an approximation of it.

.EXAMPLE
    .\Test-Email.ps1
    .\Test-Email.ps1 -TryAllPorts
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = "$env:ProgramData\HealthCheck\config.json",

    # Also try 465 with implicit SSL and 25 without, if the configured port fails.
    [bool]$TryAllPorts = $true
)

Write-Host ""
Write-Host "  Email diagnosis" -ForegroundColor Cyan
Write-Host "  ===============" -ForegroundColor Cyan
Write-Host ""

# ---- 1. Settings ------------------------------------------------------------
if (-not (Test-Path $ConfigPath)) {
    Write-Host "  No config.json found at $ConfigPath" -ForegroundColor Red
    Write-Host "  Run option 3 first to install on this machine." -ForegroundColor Yellow
    exit 1
}

try { $cfg = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch {
    Write-Host "  config.json could not be read: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

if (-not $cfg.Email) {
    Write-Host "  No email settings were stored during installation." -ForegroundColor Red
    Write-Host "  email-settings.txt was probably empty or missing when option 3 ran." -ForegroundColor Yellow
    exit 1
}

$e = $cfg.Email
Write-Host "  Server   : $($e.SmtpServer)" -ForegroundColor Green
Write-Host "  Port     : $($e.Port)" -ForegroundColor Green
Write-Host "  SSL      : $($e.UseSsl)" -ForegroundColor Green
Write-Host "  Username : $($e.User)" -ForegroundColor Green
Write-Host "  From     : $($e.From)" -ForegroundColor Green
Write-Host "  To       : $($e.To)" -ForegroundColor Green
Write-Host "  Running as: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)" -ForegroundColor Green
Write-Host ""

# ---- 2. Can the stored password be decrypted? -------------------------------
# The password is encrypted with the machine key. If this is run as a different
# account than installed it, or the machine key has changed, it will not decrypt.
$pw = $null
if ($e.PasswordProtected) {
    try {
        Add-Type -AssemblyName System.Security -ErrorAction Stop
        $bytes = [Convert]::FromBase64String($e.PasswordProtected)
        $pw = [System.Text.Encoding]::UTF8.GetString(
                [System.Security.Cryptography.ProtectedData]::Unprotect(
                    $bytes, $null,
                    [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
    }
    catch {
        Write-Host "  Stored password COULD NOT BE DECRYPTED" -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor DarkGray
        Write-Host "  Reinstalling with option 3 will store it again." -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }
}
else {
    Write-Host "  No password was stored at all." -ForegroundColor Red
    exit 1
}

if (-not $pw) {
    Write-Host "  The decrypted password is empty." -ForegroundColor Red
    exit 1
}

# Show enough to identify it without printing it
$masked = $pw.Substring(0,1) + ('*' * [Math]::Max(0, $pw.Length - 2)) + $pw.Substring($pw.Length-1,1)
Write-Host "  Password decrypted OK  ($($pw.Length) characters, $masked)" -ForegroundColor Green
if ($pw -eq 'CHANGE-ME') {
    Write-Host "  ...but it is still the placeholder from the template." -ForegroundColor Red
    exit 1
}
Write-Host ""

# ---- 3. Can we reach the server? --------------------------------------------
foreach ($port in @([int]$e.Port, 465, 587, 25 | Select-Object -Unique)) {
    if (-not $TryAllPorts -and $port -ne [int]$e.Port) { continue }
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ok  = $tcp.BeginConnect($e.SmtpServer, $port, $null, $null).AsyncWaitHandle.WaitOne(5000, $false)
        if ($ok -and $tcp.Connected) {
            Write-Host ("  Port {0,-5} reachable" -f $port) -ForegroundColor Green
        }
        else {
            Write-Host ("  Port {0,-5} no answer (blocked or closed)" -f $port) -ForegroundColor Yellow
        }
        $tcp.Close()
    }
    catch {
        Write-Host ("  Port {0,-5} could not connect" -f $port) -ForegroundColor Yellow
    }
}
Write-Host ""

# ---- 4. Try to send ---------------------------------------------------------
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$cred = New-Object System.Management.Automation.PSCredential(
            $e.User, (ConvertTo-SecureString $pw -AsPlainText -Force))

$attempts = @(
    @{ Port = [int]$e.Port; Ssl = [bool]$e.UseSsl; Name = "port $($e.Port), SSL $($e.UseSsl) - as configured" }
)
if ($TryAllPorts) {
    $attempts += @{ Port = 587; Ssl = $true;  Name = 'port 587 with SSL' }
    $attempts += @{ Port = 465; Ssl = $true;  Name = 'port 465 with SSL' }
    $attempts += @{ Port = 587; Ssl = $false; Name = 'port 587 without SSL' }
    $attempts += @{ Port = 25;  Ssl = $false; Name = 'port 25 without SSL' }
}

$sent = $null
foreach ($a in $attempts) {
    if ($sent) { break }
    Write-Host "  Trying $($a.Name)..." -ForegroundColor Cyan
    try {
        $args = @{
            To          = @($e.To -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            From        = if ($e.From) { $e.From } else { $e.User }
            Subject     = "Health check email test - $(Get-Date -Format 'HH:mm')"
            Body        = "If you are reading this, the monthly reports will send correctly.`r`n`r`nSettings that worked: $($a.Name)"
            SmtpServer  = $e.SmtpServer
            Port        = $a.Port
            Credential  = $cred
            ErrorAction = 'Stop'
        }
        if ($a.Ssl) { $args['UseSsl'] = $true }
        Send-MailMessage @args
        Write-Host "      SENT" -ForegroundColor Green
        $sent = $a
    }
    catch {
        Write-Host "      failed: $($_.Exception.Message)" -ForegroundColor Yellow
        if ($_.Exception.InnerException) {
            Write-Host "      ($($_.Exception.InnerException.Message))" -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
if ($sent) {
    Write-Host "  Email works using: $($sent.Name)" -ForegroundColor Green
    if ($sent.Port -ne [int]$e.Port -or $sent.Ssl -ne [bool]$e.UseSsl) {
        Write-Host ""
        Write-Host "  Update email-settings.txt to match, then reinstall:" -ForegroundColor Yellow
        Write-Host "      SmtpPort=$($sent.Port)" -ForegroundColor Yellow
        Write-Host "      UseSsl=$($sent.Ssl.ToString().ToLower())" -ForegroundColor Yellow
    }
    Write-Host "  Check the inbox for $($e.To)." -ForegroundColor DarkGray
}
else {
    Write-Host "  Could not send on any port." -ForegroundColor Red
    Write-Host ""
    Write-Host "  The password decrypted correctly, so the tool is sending what it" -ForegroundColor Yellow
    Write-Host "  was given. The mail server is refusing it. Worth checking:" -ForegroundColor Yellow
    Write-Host "    - does the $($e.User) mailbox exist?" -ForegroundColor Yellow
    Write-Host "    - is the password correct?" -ForegroundColor Yellow
    Write-Host "    - does the host require a different port?" -ForegroundColor Yellow
    Write-Host "    - is SMTP sending enabled for that mailbox?" -ForegroundColor Yellow
}
Write-Host ""
