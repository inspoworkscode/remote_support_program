<#
    Stores your PayFast SANDBOX account on this PC, for tools\payfastcheck to test against PayFast's real sandbox.

        powershell -ExecutionPolicy Bypass -File .\tools\Save-PayfastSandbox.ps1

    Two windows ask for it:
      1. User name = the sandbox MERCHANT ID, password = the sandbox MERCHANT KEY
         (sandbox.payfast.co.za -> Settings / Account Information).
      2. The sandbox SALT PASSPHRASE you set there.

    Nothing is typed into a chat, and nothing is written in plain text: Export-Clixml encrypts the secrets to this
    Windows user on this PC (DPAPI). The file is tools\payfast-sandbox.xml, which git ignores. Sandbox only — the
    live account is set on the server with deploy\Set-PayfastKeys.ps1 and never stored here.
#>
$ErrorActionPreference = 'Stop'
$file = Join-Path $PSScriptRoot 'payfast-sandbox.xml'

$account = Get-Credential -Message 'PayFast SANDBOX: user name = merchant ID, password = merchant key'
if (-not $account) { throw 'Cancelled.' }
if ($account.UserName -notmatch '^\d{5,12}$') { throw "The merchant ID is the number on the sandbox's Settings page, not '$($account.UserName)'." }
$phrase = Get-Credential -UserName 'passphrase' -Message 'PayFast SANDBOX: the Salt Passphrase (the user name is ignored)'
if (-not $phrase) { throw 'Cancelled.' }

[pscustomobject]@{
    MerchantId = $account.UserName
    MerchantKey = $account.Password
    Passphrase = $phrase.Password
    SavedOn = (Get-Date).ToString('yyyy-MM-dd HH:mm')
} | Export-Clixml -Path $file

Write-Host "Saved, encrypted to you on this PC: $file" -ForegroundColor Green
Write-Host 'tools\payfastcheck will now also test against PayFast''s real sandbox.' -ForegroundColor Gray
