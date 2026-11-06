<#
    Puts a PayFast account on the live server, and checks PayFast's API accepts it.

        powershell -ExecutionPolicy Bypass -File deploy\Set-PayfastKeys.ps1            # the SANDBOX account
        powershell -ExecutionPolicy Bypass -File deploy\Set-PayfastKeys.ps1 -Live      # the LIVE account, at go-live only

    Two windows ask for it (docs/NEXT-SESSION-PAYFAST.md):
      1. User name = the MERCHANT ID, password = the MERCHANT KEY   (PayFast -> Settings)
      2. The PASSPHRASE                                            (the user name is ignored)
    Then ssh asks for your key's passphrase, as every deploy does.

    THE SECRETS NEVER APPEAR IN A CHAT, A FILE, OR A COMMAND LINE. They go down the ssh connection on standard input,
    are read into two exported shell variables on the server, handed to `RemoteSupport.Platform payfast set` through
    its environment (sudo -E) — never as arguments, which the server's process list would show — and stored
    encrypted with the platform's key ring, like the mail password. The server does not need restarting.

    Sandbox and live are separate accounts with separate keys. The live one goes on only when Clint says go-live, and
    PayFast's Recurring Billing IP whitelist must hold this server's address (154.65.110.136), or every API call is
    refused with "Request origin could not be verified".
#>
param([string]$Server = "154.65.110.136", [string]$User = "ubuntu", [switch]$Live)
$ErrorActionPreference = "Stop"
$which = if ($Live) { "LIVE" } else { "SANDBOX" }

if ($Live) {
    Write-Host "This puts the LIVE PayFast account on the server: real cards, real money." -ForegroundColor Yellow
    if ((Read-Host "Type LIVE to go on") -cne "LIVE") { throw "Stopped - nothing was changed." }
}

$account = Get-Credential -Message "PayFast $($which): user name = merchant ID, password = merchant key"
if (-not $account) { throw "Cancelled." }
if ($account.UserName -notmatch '^\d{5,12}$') { throw "The merchant ID is the number on PayFast's Settings page, not '$($account.UserName)'." }
$phrase = Get-Credential -UserName "passphrase" -Message "PayFast $($which): the passphrase (the user name is ignored)"
if (-not $phrase) { throw "Cancelled." }

function Plain($secure) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

$mode = if ($Live) { "--live" } else { "--sandbox" }
# Windows PowerShell pipes lines to ssh ending in CR LF: the CR is stripped on the server before use.
$remote = "read -r RSP_PAYFAST_KEY; read -r RSP_PAYFAST_PASSPHRASE; " +
          "RSP_PAYFAST_KEY=`"`${RSP_PAYFAST_KEY%`$'\r'}`"; RSP_PAYFAST_PASSPHRASE=`"`${RSP_PAYFAST_PASSPHRASE%`$'\r'}`"; " +
          "export RSP_PAYFAST_KEY RSP_PAYFAST_PASSPHRASE; " +
          "cd /opt/rsp && sudo -u rsp -E ./RemoteSupport.Platform payfast set --merchant-id $($account.UserName) $mode 2>/dev/null | grep -v -e '^info:' -e '^warn:' -e '^ *Microsoft' -e 'The query uses'"

Write-Host "Sending the $which account to $Server ...  (type your key's passphrase now)" -ForegroundColor Cyan
$OutputEncoding = [Text.UTF8Encoding]::new($false)
$out = @((Plain $account.Password), (Plain $phrase.Password)) | ssh "$User@$Server" $remote
$out | ForEach-Object { Write-Host "   $_" }
if ($out -match "Saved: the $which account") {
    if ($out -match "PayFast's API accepts it") { Write-Host "Done: the $which account is on the server and PayFast accepts it." -ForegroundColor Green }
    else { Write-Host "Saved, but PayFast's API refused it - check the merchant ID, key and passphrase (and, for LIVE, the IP whitelist)." -ForegroundColor Yellow; exit 1 }
} else { Write-Host "Not saved - see above." -ForegroundColor Red; exit 1 }
