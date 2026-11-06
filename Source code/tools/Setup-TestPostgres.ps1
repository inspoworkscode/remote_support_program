# =====================================================================================
# Once, after installing PostgreSQL on this PC: make the account the test harnesses use.
#
#   powershell -ExecutionPolicy Bypass -File tools\Setup-TestPostgres.ps1
#
# A Windows password box asks for PostgreSQL's administrator ("postgres") password - the one you chose
# when installing it. It is used for this one minute and never written down. The script then makes a
# separate account, rsp_test, that may ONLY create and drop its own databases, gives it a long random
# password of its own, and writes that to tools\rs-postgres.txt (ignored by git). Safe to run again.
# =====================================================================================
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$psql = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Directory.Parent.Name -replace '\D', '') } -Descending | Select-Object -First 1
if (-not $psql) { throw "PostgreSQL is not installed (no C:\Program Files\PostgreSQL\...\bin\psql.exe). Install it first." }
Write-Host "Using $($psql.FullName)" -ForegroundColor Cyan

$cred = Get-Credential -UserName "postgres" -Message "PostgreSQL's administrator password (the one you chose when installing it)"
if (-not $cred) { throw "Cancelled." }

# Hex only: nothing in it needs quoting in SQL or in a connection string.
$bytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$testPw = -join ($bytes | ForEach-Object { $_.ToString("x2") })

# The SQL goes in a file: Windows PowerShell mangles quotes inside arguments to programs.
$sqlFile = Join-Path $env:TEMP "rsp-setup-test-postgres.sql"
@'
DO $body$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'rsp_test') THEN
    ALTER ROLE rsp_test LOGIN CREATEDB NOSUPERUSER NOCREATEROLE PASSWORD 'TESTPW';
  ELSE
    CREATE ROLE rsp_test LOGIN CREATEDB NOSUPERUSER NOCREATEROLE PASSWORD 'TESTPW';
  END IF;
END
$body$;
'@.Replace("TESTPW", $testPw) | Set-Content $sqlFile -Encoding ASCII

try {
    $env:PGPASSWORD = $cred.GetNetworkCredential().Password
    & $psql.FullName -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -q -f $sqlFile
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL refused it - most likely the password. Nothing was written." }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item $sqlFile -ErrorAction SilentlyContinue
}

# Proof the new account works, as itself.
$env:PGPASSWORD = $testPw
try { $ok = & $psql.FullName -h localhost -U rsp_test -d postgres -t -A -c "select 1" } finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
if ("$ok".Trim() -ne "1") { throw "The test account was made but could not sign in." }

$file = Join-Path $root "tools\rs-postgres.txt"
"Host=localhost;Port=5432;Username=rsp_test;Password=$testPw;Database=postgres" | Set-Content $file -Encoding ASCII
Write-Host "`nDone. The harnesses' PostgreSQL account is ready (kept in tools\rs-postgres.txt, not in git)." -ForegroundColor Green
