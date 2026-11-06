# Puts the issued Sectigo code-signing certificate onto the YubiKey and proves it signs.
#
#     powershell -ExecutionPolicy Bypass -File tools\Install-SigningCert.ps1 [-CertFile <bundle>] [-CheckOnly]
#
# The certificate is the PUBLIC half; the private key was generated inside the YubiKey (PIV slot 9a)
# and never leaves it. So this script:
#   1. refuses unless the key in slot 9a is the very key the certificate was issued for
#   2. keeps a copy of the bundle and the certificate outside the repository (Desktop\Code signing)
#   3. imports the certificate into slot 9a        - ykman asks for the YubiKey PIN itself
#   4. adds Sectigo's intermediates to CurrentUser\CA, so every signature carries the whole chain
#   5. waits for Windows to list it in CurrentUser\My with its key, which is where build.ps1 looks
#   6. signs a scratch copy of a small exe, timestamped, and checks the signer is us
# Run it in a window you can type in: the PIN prompts are interactive. -CheckOnly does 1 and stops.
# Written 2026-10-15 for the first certificate (valid to 2027-11-18); run it again for the second.
param(
    [string]$CertFile = (Join-Path $env:USERPROFILE 'Downloads\CollectCCC'),
    [string]$Subject = 'The Remote Support Program',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$ykman = 'C:\Program Files\Yubico\YubiKey Manager CLI\ykman.exe'
function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Fail([string]$t) { Write-Host "`n  STOPPED: $t`n" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $ykman)) { Fail "ykman is not installed (winget install --id Yubico.YubiKeyManagerCLI -e)." }
if (-not (Test-Path $CertFile)) { Fail "no certificate file at $CertFile - pass -CertFile <path>." }
& $ykman list | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "no YubiKey detected - plug it in and run this again." }

# ---- 1. the certificate, and the key it belongs to ----
$bundle = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
$bundle.Import($CertFile)
$leaf = $bundle | Where-Object { $_.Subject -like "*$Subject*" } | Select-Object -First 1
if (-not $leaf) { Fail "the file holds no certificate for '$Subject'." }
if (-not ($leaf.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' -and $_.Format($false) -match '1\.3\.6\.1\.5\.5\.7\.3\.3' })) {
    Fail "that certificate is not for code signing."
}
Say "== The certificate ==" Cyan
Say "   $($leaf.Subject)"
Say "   valid $($leaf.NotBefore.ToString('yyyy-MM-dd')) to $($leaf.NotAfter.ToString('yyyy-MM-dd')); thumbprint $($leaf.Thumbprint)"

$pemText = (& $ykman piv keys export 9a -) -join "`n"
if ($LASTEXITCODE -ne 0 -or -not $pemText) { Fail "slot 9a on the YubiKey holds no key." }
$slotSpki = [Convert]::FromBase64String((($pemText -replace '-----[^-]+-----', '') -replace '\s', ''))
$point = $leaf.PublicKey.EncodedKeyValue.RawData          # EC: 04 || X || Y, the tail of the SPKI
$tail = $slotSpki[($slotSpki.Length - $point.Length)..($slotSpki.Length - 1)]
if (Compare-Object $tail $point -SyncWindow 0) {
    Fail "the key in YubiKey slot 9a is NOT the key this certificate was issued for. Nothing was changed."
}
Say "   matches the key in YubiKey slot 9a" Green
if ($CheckOnly) { Say "`n-CheckOnly: nothing was changed." Yellow; exit 0 }

# ---- 2. a copy kept outside the repository ----
$keep = Join-Path $env:USERPROFILE 'Desktop\Code signing'
New-Item -ItemType Directory -Force $keep | Out-Null
$stamp = $leaf.NotBefore.ToString('yyyy-MM-dd')
$leafFile = Join-Path $keep "RSP code signing $stamp.cer"
Copy-Item $CertFile (Join-Path $keep "RSP code signing $stamp (bundle).p7b") -Force
[IO.File]::WriteAllBytes($leafFile, $leaf.RawData)
Say "== Kept a copy in $keep ==" Cyan

# ---- 3. onto the YubiKey ----
Say "== Importing it into YubiKey slot 9a ==" Cyan
$already = $null
try { $already = (& $ykman piv certificates export 9a - 2>$null) -join "`n" } catch { }
$alreadyOurs = $false
if ($already -and $already -match 'BEGIN CERTIFICATE') {
    $b = [Convert]::FromBase64String((($already -replace '-----[^-]+-----', '') -replace '\s', ''))
    $alreadyOurs = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, $b)).Thumbprint -eq $leaf.Thumbprint
}
if ($alreadyOurs) {
    Say "   already there" Green
} else {
    Say "   ykman will ask for the YubiKey PIN." Yellow
    & $ykman piv certificates import 9a $leafFile
    if ($LASTEXITCODE -ne 0) { Fail "ykman could not import the certificate (wrong PIN?). Nothing else was changed." }
    Say "   imported" Green
}

# ---- 4. the intermediates, so the chain is complete on any machine ----
$ca = New-Object System.Security.Cryptography.X509Certificates.X509Store('CA', 'CurrentUser')
$ca.Open('ReadWrite')
foreach ($c in $bundle) {
    if ($c.Thumbprint -eq $leaf.Thumbprint -or $c.Subject -eq $c.Issuer) { continue }   # leaf and self-signed root
    if (-not ($ca.Certificates | Where-Object { $_.Thumbprint -eq $c.Thumbprint })) { $ca.Add($c) }
}
$ca.Close()
Say "== Sectigo intermediates are in CurrentUser\CA ==" Cyan

# ---- 5. Windows lists it, with the key behind it ----
# Windows' own PIV driver does not handle our ECC P-256 key: it sees the card and reports "Missing
# stored keyset" (certutil -scinfo), measured 2026-10-15. Yubico's Smart Card Minidriver (4.6.3.252
# or later, firmware 5.7+) adds ECC; with it installed the certificate is listed.
Say "== Waiting for Windows to list it in CurrentUser\My ==" Cyan
function Listed { Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $leaf.Thumbprint -and $_.HasPrivateKey } }
$mine = $null
for ($i = 0; $i -lt 15 -and -not $mine; $i++) { $mine = Listed; if (-not $mine) { Start-Sleep 1 } }
if (-not $mine) {
    Say "   Not yet. UNPLUG the YubiKey, wait three seconds, plug it back in." Yellow
    for ($i = 0; $i -lt 60 -and -not $mine; $i++) { $mine = Listed; if (-not $mine) { Start-Sleep 1 } }
}
if (-not $mine) {
    Fail ("Windows has not listed the certificate. The certificate IS on the YubiKey; Windows' smart card " +
          "service did not copy it into CurrentUser\My. Our key is ECC, which needs Yubico's Smart Card Minidriver " +
          "(4.6.3.252 or later, from yubico.com) - install it, unplug and replug the YubiKey, and run this again.")
}
Say "   listed, with its key on the YubiKey" Green

# ---- 6. sign something, the way build.ps1 will ----
. (Join-Path $root 'tools\Signing.ps1')
$sign = Initialize-Signing -CertSubject $Subject -RequireSigning
if ($sign.Cert.Thumbprint -ne $leaf.Thumbprint) { Fail "build.ps1 would pick a different certificate ($($sign.Cert.Thumbprint))." }
$test = Join-Path $env:TEMP 'rsp-signing-test.exe'
Copy-Item "$env:WINDIR\System32\whoami.exe" $test -Force
Say "== Signing a test file - Windows will ask for the YubiKey PIN ==" Cyan
& $sign.Tool sign /sha1 $sign.Cert.Thumbprint /fd SHA256 /tr $sign.TimestampUrl /td SHA256 $test
if ($LASTEXITCODE -ne 0) { Fail "signtool could not sign the test file." }
# Read the EMBEDDED signature. Get-AuthenticodeSignature is no use on a copy of a Windows file: the
# file's hash is in Windows' own catalog, so it answers "Microsoft Windows" whatever we embed - which
# is what stopped the first run here (2026-10-15). signtool verify reads only the embedded one.
$verify = (& $sign.Tool verify /pa /v $test) -join "`n"
$verified = $LASTEXITCODE -eq 0
$signer = $null
try { $signer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 ([System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($test)) } catch { }
Remove-Item $test -Force
if (-not $verified) { Fail "the test signature does not verify:`n$verify" }
if (-not $signer -or $signer.Thumbprint -ne $leaf.Thumbprint) { Fail "the embedded signature is not ours." }
if ($verify -notmatch 'The signature is timestamped') { Fail "the test signature has no timestamp." }
$stampedAt = if ($verify -match 'The signature is timestamped:\s*(.+)') { $Matches[1].Trim() } else { '' }

Say ""
Say "  DONE. Signed, verified and timestamped as:" Green
Say "  $($signer.Subject)" Green
Say "  timestamped $stampedAt" Green
Say "  build.ps1 will now sign everything while the YubiKey is plugged in." Green
