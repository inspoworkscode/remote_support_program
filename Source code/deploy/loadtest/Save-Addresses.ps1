# =====================================================================================
# Remember the load-test servers' addresses, so no other command needs one typed in.
# Asks for each one (a third load box is optional - press Enter for none); saved in deploy\loadtest\out\servers.json.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Save-Addresses.ps1
# =====================================================================================
$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Path $out -Force | Out-Null
function Ask($what, [switch]$Optional) {
    while ($true) {
        $ip = (Read-Host "The address of $what (four numbers with dots$(if ($Optional) { '; Enter if there is none' }))").Trim()
        if ($Optional -and $ip -eq "") { return "" }
        if ($ip -match '^\d{1,3}(\.\d{1,3}){3}$') { return $ip }
        Write-Host "  That does not look like an address such as 154.65.110.20 - try again." -ForegroundColor Yellow
    }
}
$s = [ordered]@{
    Server = Ask "rsp-loadtest"
    Box1   = Ask "rsp-load-1"
    Box2   = Ask "rsp-load-2"
    Box3   = Ask "rsp-load-3" -Optional
}
if ("154.65.110.136" -in @($s.Server, $s.Box1, $s.Box2, $s.Box3)) { Write-Host "None of these can be the LIVE server's address. Nothing saved." -ForegroundColor Red; exit 1 }
$s | ConvertTo-Json | Set-Content (Join-Path $out "servers.json") -Encoding UTF8
Write-Host "`nSaved:" -ForegroundColor Green
Write-Host "  rsp-loadtest  $($s.Server)"
Write-Host "  rsp-load-1    $($s.Box1)"
Write-Host "  rsp-load-2    $($s.Box2)"
if ($s.Box3) { Write-Host "  rsp-load-3    $($s.Box3)" }
