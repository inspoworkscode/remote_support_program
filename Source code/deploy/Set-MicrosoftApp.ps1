<#
    Puts our Microsoft Entra app on a server, for Tickets by email's "Sign in with Microsoft" (HANDOVER 151).

        powershell -ExecutionPolicy Bypass -File deploy\Set-MicrosoftApp.ps1
        powershell -ExecutionPolicy Bypass -File deploy\Set-MicrosoftApp.ps1 -Server 1.2.3.4     # another server

    One window asks for it (docs/MICROSOFT-365.md, step 4):
        User name = the Application (client) ID     (Entra -> App registrations -> The Remote Support Program -> Overview)
        Password  = the client secret's VALUE       (Certificates & secrets -> the secret you just made; shown ONCE)
    Then the date the secret runs out (Entra shows it beside the secret), and ssh asks for your key's passphrase.

    THE SECRET NEVER APPEARS IN A CHAT, A FILE, OR A COMMAND LINE. It goes down the ssh connection on standard input, is
    read into an exported shell variable on the server, handed to `RemoteSupport.Platform microsoft-app set` through its
    environment (sudo -E) - never as an argument, which the server's process list would show - and stored encrypted with
    the platform's key ring, like the mail password. The server does not need restarting.

    Run it again with a new secret before the old one runs out: every company's Microsoft sign-in stops the day it does.
#>
param([string]$Server = "154.65.110.136", [string]$User = "ubuntu")
$ErrorActionPreference = "Stop"

$app = Get-Credential -Message "Microsoft app: user name = Application (client) ID, password = the client secret's VALUE"
if (-not $app) { throw "Cancelled." }
$id = $app.UserName.Trim()
if ($id -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
    throw "The user name must be the Application (client) ID - a GUID from the app's Overview page - not '$id'."
}
$expires = (Read-Host "The date the secret runs out, as yyyy-mm-dd (Entra shows it beside the secret)").Trim()
if ($expires -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "Give the date as yyyy-mm-dd, e.g. 2028-10-19." }

function Plain($secure) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

# Windows PowerShell pipes lines to ssh ending in CR LF: the CR is stripped on the server before use.
$remote = "read -r RSP_MS_CLIENT_SECRET; RSP_MS_CLIENT_SECRET=`"`${RSP_MS_CLIENT_SECRET%`$'\r'}`"; export RSP_MS_CLIENT_SECRET; " +
          "cd /opt/rsp && sudo -u rsp -E ./RemoteSupport.Platform microsoft-app set --client-id $id --expires $expires 2>/dev/null | grep -v -e '^info:' -e '^warn:' -e '^ *Microsoft' -e 'The query uses'; " +
          "sudo -u rsp ./RemoteSupport.Platform microsoft-app show 2>/dev/null | grep -v -e '^info:' -e '^warn:' -e '^ *Microsoft\.' -e 'The query uses'"

Write-Host "Sending the Microsoft app to $Server ...  (type your key's passphrase now)" -ForegroundColor Cyan
$OutputEncoding = [Text.UTF8Encoding]::new($false)
$out = @((Plain $app.Password)) | ssh "$User@$Server" $remote
$out | ForEach-Object { Write-Host "   $_" }
if ($out -match "Saved: the Microsoft app") {
    Write-Host "Done: Sign in with Microsoft is offered on Tickets by email. Check the redirect URI above is on the app in Entra, exactly." -ForegroundColor Green
} else { Write-Host "Not saved - see above." -ForegroundColor Red; exit 1 }
