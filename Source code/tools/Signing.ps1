# Code signing (stage 5), shared by build.ps1 and tools\Pack-ClientExe.ps1 - dot-source it:
#     . (Join-Path $root 'tools\Signing.ps1')
#     $sign = Initialize-Signing -CertSubject '...' -TimestampUrl '...' [-RequireSigning] [-SkipSigning]
#     Invoke-Signing $sign 'what this is' @(files)
#
# Moved out of build.ps1 on 2026-09-30, unchanged in behaviour, when the one downloadable .exe made
# a second script need it: that .exe carries signed files INSIDE it, so they must be signed before
# they are packed and the outer .exe after (docs/CODE-SIGNING.md, "One .exe for every company").
#
# Deliberately automatic and self-disabling: with no certificate installed nothing is signed, the
# output says so plainly, and nothing has to change on the day the certificate arrives.

function Find-SignTool {
    # signtool.exe ships with the Windows SDK under a version-numbered folder; take the newest x64.
    foreach ($r in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (-not (Test-Path $r)) { continue }
        $found = Get-ChildItem $r -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object { $_.Directory.Parent.Name } -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

function Get-SigningCert([string]$subject) {
    # -CodeSigningCert filters on the Code Signing EKU. A certificate without it would fail later,
    # inside signtool, which is a much more confusing place to discover the problem. The key lives
    # on the hardware token; what is in the store is the public half and a handle to the key, so
    # this finds it either way and the token prompts for its PIN.
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
        $c = Get-ChildItem $store -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -like "*$subject*" -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
        if ($c) { return $c }
    }
    return $null
}

function Initialize-Signing {
    param(
        [string]$CertSubject = "The Remote Support Program",
        # Always timestamp, or every signature stops verifying the day the certificate expires, on
        # machines that were installed years earlier.
        [string]$TimestampUrl = "http://timestamp.sectigo.com",
        [switch]$RequireSigning,
        [switch]$SkipSigning,
        [switch]$Quiet
    )
    $s = [pscustomobject]@{ On = $false; Tool = $null; Cert = $null; TimestampUrl = $TimestampUrl }
    if ($SkipSigning) {
        if (-not $Quiet) { Write-Host "== Code signing skipped (-SkipSigning) ==" -ForegroundColor Yellow }
        return $s
    }
    $s.Cert = Get-SigningCert $CertSubject
    $s.Tool = Find-SignTool
    if ($s.Cert -and $s.Tool) {
        $s.On = $true
        if (-not $Quiet) {
            Write-Host "== Code signing enabled ==" -ForegroundColor Green
            Write-Host "   $($s.Cert.Subject)"
            Write-Host "   expires $($s.Cert.NotAfter.ToString('yyyy-MM-dd')); thumbprint $($s.Cert.Thumbprint)"
        }
        return $s
    }
    $why = if (-not $s.Cert) {
        "no valid code-signing certificate matching '$CertSubject' in CurrentUser\My or LocalMachine\My"
    } else {
        "signtool.exe was not found - install the Windows SDK"
    }
    if ($RequireSigning) { throw "-RequireSigning was given, but signing is not possible: $why." }
    if (-not $Quiet) {
        Write-Warning "Not signing: $why."
        Write-Warning "The output will be UNSIGNED. Smart App Control will block it on a clean Windows 11; see docs/CODE-SIGNING.md."
    }
    return $s
}

function Invoke-Signing($sign, [string]$label, [string[]]$paths) {
    if (-not $sign -or -not $sign.On) { return }
    $files = @($paths | Where-Object { $_ -and (Test-Path $_) })
    if (-not $files) { return }
    Write-Host "== Signing $label ($($files.Count) files) ==" -ForegroundColor Cyan

    # /sha1 picks the exact certificate found above rather than letting signtool guess, which
    # matters the moment there is more than one in the store. /tr timestamps: without it every
    # signature stops verifying the day the certificate expires, including on machines installed
    # years earlier.
    $exes = @($files | Where-Object { $_ -notlike '*.ps1' })
    if ($exes) {
        & $sign.Tool sign /sha1 $sign.Cert.Thumbprint /fd SHA256 /tr $sign.TimestampUrl /td SHA256 @exes
        if ($LASTEXITCODE -ne 0) { throw "signtool failed while signing $label." }
        & $sign.Tool verify /pa /q @exes
        if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $label." }
    }

    # A .ps1 carries its signature as a comment block appended to the file, so signtool cannot do
    # it. These are the scripts that install the client and the ones the monthly health check runs
    # as SYSTEM - the two places an execution policy or an AV product is most likely to object.
    foreach ($ps in @($files | Where-Object { $_ -like '*.ps1' })) {
        $r = Set-AuthenticodeSignature -FilePath $ps -Certificate $sign.Cert `
            -TimestampServer $sign.TimestampUrl -HashAlgorithm SHA256
        if ($r.Status -ne 'Valid') { throw "signing failed on ${ps}: $($r.StatusMessage)" }
    }

    Write-Host "   Signed and verified." -ForegroundColor Green
}
