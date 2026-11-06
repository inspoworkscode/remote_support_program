#!/usr/bin/env bash
# =====================================================================================
# The Remote Support Program: the nightly encrypted backup. go-live.sh installs this as
# /usr/local/sbin/rsp-backup and runs it at 02:30 (/etc/cron.d/rsp-backup), and before every deploy.
#
# One file a night, rsp-YYYYMMDD.tar.gz.gpg in /var/backups/rsp, AES-256 with the passphrase in
# /etc/rsp-backup-key, kept 14 days (before-update-* copies 60). It holds data/ and the database:
#   - PostgreSQL when the platform uses it (the drop-in postgres.conf exists - step 4 of HANDOVER
#     section 118): pg_dump, as platform.pgdump;
#   - otherwise SQLite, copied with its own backup command so a write in progress cannot tear it.
# Recordings are NOT in it (2026-10-09): a recording never changes, so each is encrypted ONCE, on
# its own, into /var/backups/rsp/recordings/, and the nightly file stays a sensible size for the
# office copy (deploy/backup). A recording's copy goes once the recording is deleted and the copy
# is over 60 days old.
#
# Restore:
#   gpg --batch --passphrase-file /etc/rsp-backup-key -d rsp-DATE.tar.gz.gpg | tar xz -C <folder>
#   SQLite:      <folder>/platform.db
#   PostgreSQL:  sudo -u postgres pg_restore --clean --if-exists -d rsp <folder>/platform.pgdump
#   A recording: gpg --batch --passphrase-file /etc/rsp-backup-key -d recordings/<company>/<name>.mp4.gpg > <name>.mp4
#
# The RSP_BACKUP_* variables exist only so tools/backupcheck can run this on a PC; the server sets none.
# =====================================================================================
set -euo pipefail
APP_DIR="${RSP_BACKUP_APP_DIR:-/opt/rsp}"
KEY="${RSP_BACKUP_KEY:-/etc/rsp-backup-key}"
out="${RSP_BACKUP_DIR:-/var/backups/rsp}"
FSTAB="${RSP_BACKUP_FSTAB:-/etc/fstab}"
PG_DROPIN="${RSP_BACKUP_PG_DROPIN:-/etc/systemd/system/remotesupport-platform.service.d/postgres.conf}"
PG_DUMP="${RSP_BACKUP_PG_DUMP:-runuser -u postgres -- pg_dump}"
data="$APP_DIR/data"
day="$(date +%Y%m%d)"

# On a disk of its own (step 4): if that disk did not mount, writing here would quietly fill the system
# disk underneath it instead - so stop and say so.
if grep -qsE "^[^#]*[[:space:]]$out[[:space:]]" "$FSTAB" && ! mountpoint -q "$out"; then
  echo "!! $out is not mounted (its disk is missing?) - no backup made." >&2; exit 1
fi

if [[ -f "$PG_DROPIN" ]]; then
  engine=PostgreSQL
  db="$(grep -o 'Database=[^;"]*' "$PG_DROPIN" | head -n1 | cut -d= -f2)"; db="${db:-rsp}"
else
  engine=SQLite
  # Never let sqlite3 CREATE the database: made by root, the platform could not write to it.
  [[ -f "$data/platform.db" ]] || { echo "no database yet"; exit 0; }
fi

mkdir -p "$out"
# The office's nightly copy (deploy/backup, 2026-10-08) reads them as the group rspcopy, which holds nobody
# else; the files stay encrypted and the passphrase stays root's. Without that group: root alone.
if getent group rspcopy >/dev/null 2>&1; then chgrp rspcopy "$out"; chmod 2750 "$out"; else chmod 700 "$out"; fi

stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
if [[ $engine == PostgreSQL ]]; then
  $PG_DUMP --format=custom --dbname="$db" > "$stage/platform.pgdump"
  dbfile=platform.pgdump
else
  sqlite3 "$data/platform.db" ".backup '$stage/platform.db'"
  dbfile=platform.db
fi
# data/ without the live database (above), without the recordings (below), and without the companies' exports —
# a zip of what is already here, kept a week (CompanyExportService). On PostgreSQL the SQLite
# file left in data/ is the frozen way back; the backup made just before the switch holds it.
tar -C "$data" --exclude=./platform.db --exclude=./platform.db-wal --exclude=./platform.db-shm \
    --exclude=./platform.db-journal --exclude=./recordings --exclude=./exports -cf "$stage/data.tar" .
tar -C "$stage" -rf "$stage/data.tar" "$dbfile"
# Under a name the office copy never lists, then renamed: nobody ever fetches half a backup.
gzip -c "$stage/data.tar" | gpg --batch --yes --symmetric --cipher-algo AES256 --passphrase-file "$KEY" -o "$out/.rsp-$day.partial"
mv -f "$out/.rsp-$day.partial" "$out/rsp-$day.tar.gz.gpg"
if getent group rspcopy >/dev/null 2>&1; then chgrp rspcopy "$out"/rsp-*.tar.gz.gpg; chmod 640 "$out"/rsp-*.tar.gz.gpg; fi
find "$out" -maxdepth 1 -name 'rsp-*.tar.gz.gpg' -mtime +14 -delete
find "$out" -maxdepth 1 -name 'before-update-*.tar.gz.gpg' -mtime +60 -delete

# Each finished recording, once (an upload in progress is still *.part and is left for tomorrow).
rec="$data/recordings"; rout="$out/recordings"; new=0; kept=0
if [[ -d "$rec" ]]; then
  mkdir -p "$rout"; chmod 700 "$rout"
  while IFS= read -r -d '' f; do
    dst="$rout/${f#"$rec"/}.gpg"
    if [[ -s "$dst" ]]; then kept=$((kept + 1)); continue; fi
    mkdir -p "$(dirname "$dst")"
    gpg --batch --yes --symmetric --cipher-algo AES256 --passphrase-file "$KEY" -o "$dst.partial" "$f"
    mv -f "$dst.partial" "$dst"
    new=$((new + 1))
  done < <(find "$rec" -type f -name '*.mp4' -print0)
fi
if [[ -d "$rout" ]]; then
  while IFS= read -r -d '' g; do
    rel="${g#"$rout"/}"
    [[ -f "$rec/${rel%.gpg}" ]] || rm -f "$g"
  done < <(find "$rout" -type f -name '*.mp4.gpg' -mtime +60 -print0)
fi

echo "rsp-$day.tar.gz.gpg ($engine, $(( $(stat -c %s "$out/rsp-$day.tar.gz.gpg") / 1024 )) KB); recordings: $new new, $kept already kept"
