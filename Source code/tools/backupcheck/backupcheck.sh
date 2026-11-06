#!/usr/bin/env bash
# =====================================================================================
# backupcheck - deploy/rsp-backup.sh, the server's nightly backup, run on this PC (2026-10-09, step 4
# of HANDOVER §118). There is no Linux here, so it runs in Git Bash against the test PostgreSQL that
# tools/Setup-TestPostgres.ps1 set up, with the server's paths moved into a temp folder by the
# script's RSP_BACKUP_* variables.
#
#   bash tools/backupcheck/backupcheck.sh
#
# Real: pg_dump and pg_restore (PostgreSQL 16 on this PC), gpg, tar, the platform's copy-to-postgres.
# Stand-ins, in a folder put first on PATH: sqlite3 (a plain copy - SQLite's own .backup is what the
# live server already runs every night) and mountpoint (says "not mounted").
#
# What it proves: the SQLite night and the PostgreSQL night each make one encrypted file holding
# data/ and the database and NOT the recordings; each finished recording is kept once, encrypted, and
# never again; an unfinished one (.part) is left; a deleted recording's copy goes after 60 days and
# not before; old nightlies are pruned; a PostgreSQL backup RESTORES into an empty database identical
# to the SQLite original, table by table (copy-to-postgres --verify-only); a backup disk that should be
# mounted and is not stops it; the office copy's gatekeeper lists the new file and nothing else; and the
# command line reads the connection string from appsettings.live.json, as go-live.sh writes it.
# =====================================================================================
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
pass=0; fail=0
check() { local what="$1"; shift; if "$@"; then pass=$((pass + 1)); echo "  PASS  $what"; else fail=$((fail + 1)); echo "  FAIL  $what"; fi; }
say() { echo; echo "== $* =="; }

PGBIN="/c/Program Files/PostgreSQL/16/bin"
[[ -x "$PGBIN/pg_dump.exe" ]] || { echo "No PostgreSQL 16 at $PGBIN"; exit 2; }
[[ -f "$repo/tools/rs-postgres.txt" ]] || { echo "No tools/rs-postgres.txt (run tools\\Setup-TestPostgres.ps1 once)"; exit 2; }
admin="$(tr -d '\r\n' < "$repo/tools/rs-postgres.txt")"
field() { tr ';' '\n' <<< "$admin" | sed -n "s/^$1=//Ip" | head -n1; }
export PGHOST="$(field Host)" PGPORT="$(field Port)" PGUSER="$(field Username)" PGPASSWORD="$(field Password)"
PGPORT="${PGPORT:-5432}"
psql() { "$PGBIN/psql.exe" -X -q -v ON_ERROR_STOP=1 "$@"; }

work="$(cygpath -u "${TEMP:-/tmp}")/rs-backupcheck-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$work"
stamp="$(date +%Y%m%d_%H%M%S)"
dbs=()
# Not $(newdb ...): a subshell's dbs+= would never reach the clean-up.
newdb() { local n="rsp_bkchk_${stamp}_$1"; psql -d postgres -c "CREATE DATABASE \"$n\"" && dbs+=("$n"); }
conn() { echo "Host=$PGHOST;Port=$PGPORT;Username=$PGUSER;Password=$PGPASSWORD;Database=$1"; }
cleanup() {
  for d in "${dbs[@]}"; do psql -d postgres -c "DROP DATABASE IF EXISTS \"$d\" WITH (FORCE)" >/dev/null 2>&1; done
  rm -rf "$work"
}
trap cleanup EXIT

echo "backupcheck - deploy/rsp-backup.sh on this PC"
say "building the platform (for copy-to-postgres and the command line)"
dotnet build -c Release "$repo/src/RemoteSupport.Platform/RemoteSupport.Platform.csproj" -v q --nologo -o "$(cygpath -w "$work/server")" >"$work/build.log" 2>&1 \
  || { tail -20 "$work/build.log"; exit 2; }
exe="$work/server/RemoteSupport.Platform.exe"
echo "  built"

# ---- a data/ folder shaped like the server's ----
opt="$work/opt"; data="$opt/data"; out="$work/backups"; key="$work/backup-key"
mkdir -p "$data/logos" "$data/keys" "$data/recordings/7" "$out"
head -c 48 /dev/urandom | base64 | tr -d '\n' > "$key"
real="$repo/src/RemoteSupport.Platform/bin/Release/net10.0/data/platform.db"
if [[ -f "$real" ]]; then
  cp "$real" "$data/platform.db"
  RSP_Platform__DataDirectory="$(cygpath -w "$data")" "$exe" list-tenants >/dev/null 2>&1   # migrates it, as a deploy does
  echo "  database: a copy of the local test server's ($(( $(stat -c %s "$data/platform.db") / 1024 )) KB), migrated"
else
  RSP_Platform__DataDirectory="$(cygpath -w "$data")" "$exe" seed-loadtest 10 3 "$(cygpath -w "$work/seed.txt")" >/dev/null 2>&1
  echo "  database: the load test's seed (no local test server database on this PC)"
fi
head -c 2000 /dev/urandom > "$data/logos/1.png"
echo '<key id="x"/>' > "$data/keys/key-x.xml"
head -c 300000 /dev/urandom > "$data/recordings/7/20261009-101010-aaaa.mp4"
head -c 1000 /dev/urandom > "$data/recordings/7/20261009-111111-bbbb.mp4.part"

shims="$work/shims"; mkdir -p "$shims"
cat > "$shims/sqlite3" <<'EOF'
#!/usr/bin/env bash
# stand-in: sqlite3 <db> ".backup '<file>'"  ->  a copy
dst="${2#.backup \'}"; dst="${dst%\'}"; cp "$1" "$dst"
EOF
cat > "$shims/mountpoint" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
cat > "$shims/pgdump" <<EOF
#!/usr/bin/env bash
exec "$PGBIN/pg_dump.exe" "\$@"
EOF
chmod +x "$shims"/*
export PATH="$shims:$PATH"

run_backup() { # [extra env assignments...]
  env RSP_BACKUP_APP_DIR="$opt" RSP_BACKUP_KEY="$key" RSP_BACKUP_DIR="$out" RSP_BACKUP_FSTAB="$work/fstab" \
      RSP_BACKUP_PG_DROPIN="$work/postgres.conf" RSP_BACKUP_PG_DUMP=pgdump "$@" \
      bash "${BACKUPCHECK_SCRIPT:-$repo/deploy/rsp-backup.sh}"   # BACKUPCHECK_SCRIPT: a copy with a fault put back
}
open_backup() { # <file> <folder>
  rm -rf "$2"; mkdir -p "$2"
  gpg --batch --quiet --passphrase-file "$key" -d "$1" | tar xz -C "$2"
}
day="$(date +%Y%m%d)"
touch "$work/fstab"

# =================== the SQLite night ===================
say "a night on SQLite"
said="$(run_backup 2>&1)"; code=$?
echo "  said: $said"
check "it finishes and says SQLite" test $code -eq 0 -a -n "$(grep -o SQLite <<< "$said")"
check "one nightly file, rsp-$day.tar.gz.gpg, and nothing half-written left" test -s "$out/rsp-$day.tar.gz.gpg" -a -z "$(ls -A "$out" | grep partial)"
open_backup "$out/rsp-$day.tar.gz.gpg" "$work/open1"
check "it holds the database" test -s "$work/open1/platform.db"
check "...and data/ (a logo and the key ring)" test -s "$work/open1/logos/1.png" -a -s "$work/open1/keys/key-x.xml"
check "...and NO recordings" test ! -e "$work/open1/recordings"
rec="$out/recordings/7/20261009-101010-aaaa.mp4.gpg"
check "the finished recording is kept once, on its own" test -s "$rec"
check "...and opens to exactly the recording" cmp -s <(gpg --batch --quiet --passphrase-file "$key" -d "$rec") "$data/recordings/7/20261009-101010-aaaa.mp4"
check "the unfinished one (.part) is left alone" test -z "$(find "$out/recordings" -name '*bbbb*')"
check "it says 1 new" grep -q "1 new, 0 already kept" <<< "$said"
before="$(stat -c %Y "$rec")"; sleep 1.2
said2="$(run_backup 2>&1)"
check "the next night: 0 new, 1 already kept - never made again" test -n "$(grep -o '0 new, 1 already kept' <<< "$said2")" -a "$(stat -c %Y "$rec")" = "$before"

# deleted recordings: old copy goes, recent copy stays
head -c 1000 /dev/urandom > "$data/recordings/7/20261009-121212-cccc.mp4"
run_backup >/dev/null 2>&1
rm -f "$data/recordings/7/20261009-101010-aaaa.mp4" "$data/recordings/7/20261009-121212-cccc.mp4"
touch -d '70 days ago' "$rec"
# old nightlies and before-update copies
for f in rsp-20200101.tar.gz.gpg before-update-20200101-000000.tar.gz.gpg before-update-20200102-000000.tar.gz.gpg; do echo x > "$out/$f"; done
touch -d '20 days ago' "$out/rsp-20200101.tar.gz.gpg"
touch -d '70 days ago' "$out/before-update-20200101-000000.tar.gz.gpg"
touch -d '30 days ago' "$out/before-update-20200102-000000.tar.gz.gpg"
run_backup >/dev/null 2>&1
check "a deleted recording's copy over 60 days old is removed" test ! -e "$rec"
check "...but a deleted recording's recent copy is kept" test -s "$out/recordings/7/20261009-121212-cccc.mp4.gpg"
check "a nightly over 14 days old is removed" test ! -e "$out/rsp-20200101.tar.gz.gpg"
check "a before-update copy over 60 days old is removed, a 30-day one kept" test ! -e "$out/before-update-20200101-000000.tar.gz.gpg" -a -e "$out/before-update-20200102-000000.tar.gz.gpg"

# =================== the PostgreSQL night ===================
say "a night on PostgreSQL"
newdb live; live="rsp_bkchk_${stamp}_live"
copy_said="$("$exe" copy-to-postgres --from "$(cygpath -w "$data/platform.db")" --to "$(conn "$live")" 2>&1)"
check "the SQLite database copied into a test PostgreSQL" grep -q "^OK: all" <<< "$copy_said"
# The drop-in exactly as switch-to-postgres.sh writes it, with the test database's name.
cat > "$work/postgres.conf" <<EOF
[Unit]
After=postgresql.service
Wants=postgresql.service

[Service]
Environment="RSP_ConnectionStrings__Postgres=Host=/var/run/postgresql;Database=$live;Username=rsp"
EOF
rm -f "$out/rsp-$day.tar.gz.gpg"
said="$(run_backup 2>&1)"; code=$?
echo "  said: $said"
check "it finishes and says PostgreSQL" test $code -eq 0 -a -n "$(grep -o PostgreSQL <<< "$said")"
open_backup "$out/rsp-$day.tar.gz.gpg" "$work/open2"
check "it holds a PostgreSQL dump, and not the frozen SQLite file" test -s "$work/open2/platform.pgdump" -a ! -e "$work/open2/platform.db"
check "...and data/, and no recordings" test -s "$work/open2/logos/1.png" -a ! -e "$work/open2/recordings"
newdb restored; restored="rsp_bkchk_${stamp}_restored"
"$PGBIN/pg_restore.exe" --no-owner -d "$restored" "$(cygpath -w "$work/open2/platform.pgdump")" 2>"$work/restore.log"
check "the dump restores into an empty database without an error" test ! -s "$work/restore.log"
verify="$("$exe" copy-to-postgres --from "$(cygpath -w "$data/platform.db")" --to "$(conn "$restored")" --verify-only 2>&1)"
echo "  $(grep -E '^(OK|FAILED)' <<< "$verify")"
check "...and the restored database is the SQLite original, every table, row by row" grep -q "^OK: all" <<< "$verify"
next_live="$(psql -d "$live" -tA -c "SELECT last_value FROM \"Tickets_Id_seq\"" 2>/dev/null)"
next_restored="$(psql -d "$restored" -tA -c "SELECT last_value FROM \"Tickets_Id_seq\"" 2>/dev/null)"
check "...with its id counters (Tickets: $next_live)" test -n "$next_live" -a "$next_live" = "$next_restored"

# =================== a backup disk that did not mount ===================
say "the backup disk is meant to be mounted and is not"
echo "UUID=0000 $out none bind,nofail 0 0" > "$work/fstab"
rm -f "$out/rsp-$day.tar.gz.gpg"
said="$(run_backup 2>&1)"; code=$?
check "it stops, and says why" test $code -ne 0 -a -n "$(grep -o 'not mounted' <<< "$said")"
check "...and wrote nothing" test ! -e "$out/rsp-$day.tar.gz.gpg"
echo "# UUID=0000 $out none bind,nofail 0 0" > "$work/fstab"
said="$(run_backup 2>&1)"; code=$?
check "a commented-out line in fstab is not taken for a mount" test $code -eq 0
: > "$work/fstab"

# =================== the office copy still sees it ===================
say "the office copy's gatekeeper (deploy/backup/rsp-backup-serve.sh)"
listed="$(SSH_ORIGINAL_COMMAND=list RSP_BACKUP_DIR="$out" bash "$repo/deploy/backup/rsp-backup-serve.sh")"
check "it lists tonight's backup" grep -q "^rsp-$day.tar.gz.gpg " <<< "$listed"
check "...and no recording and nothing half-written" test -z "$(grep -E 'mp4|partial' <<< "$listed")"

# =================== the command line reads appsettings.live.json ===================
say "go-live.sh's two lines: the connection string from the drop-in, into appsettings.live.json"
# The SAME two expressions go-live.sh uses (kept in step by hand - grep go-live.sh for PG_CONN).
PG_CONN="$(grep -o 'RSP_ConnectionStrings__Postgres=[^"]*' "$work/postgres.conf" | head -n1 | cut -d= -f2-)"
check "the connection string is read whole from the drop-in" test "$PG_CONN" = "Host=/var/run/postgresql;Database=$live;Username=rsp"
check "...and go-live.sh still uses exactly that expression" grep -qF "grep -o 'RSP_ConnectionStrings__Postgres=[^\"]*' \"\$PG_DROPIN\" | head -n1 | cut -d= -f2-" "$repo/deploy/go-live.sh"
# This PC has no socket at /var/run/postgresql, so the file gets the test server's address instead.
DB_JSON=", \"ConnectionStrings\": { \"Postgres\": \"$(conn "$live")\" }"
cat > "$work/server/appsettings.live.json" <<EOF
{ "Platform": { "PublicUrl": "https://example.test", "SignupsEnabled": false }$DB_JSON }
EOF
empty="$work/emptydata"; mkdir -p "$empty"
listed="$(RSP_Platform__DataDirectory="$(cygpath -w "$empty")" "$exe" list-tenants 2>&1)"
check "the command line, given only that file, lists the companies in PostgreSQL" test -n "$(grep -v '^\s*$' <<< "$listed")" -a ! -e "$empty/platform.db"
check "...and go-live.sh writes the file in that shape" grep -qF '"ConnectionStrings\": { \"Postgres\": \"$PG_CONN\" }' "$repo/deploy/go-live.sh"

echo
echo "$pass of $((pass + fail)) checks passed."
[[ $fail -eq 0 ]]
