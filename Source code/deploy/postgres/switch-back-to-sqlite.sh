#!/usr/bin/env bash
# =====================================================================================
# The Remote Support Program: the way back from PostgreSQL to the SQLite file (step 4 of HANDOVER
# section 118, built 2026-10-09). Clint runs it from his laptop with Switch-BackToSqlite.ps1:
#
#   sudo bash switch-back-to-sqlite.sh --check     says what would be lost; changes NOTHING
#   sudo bash switch-back-to-sqlite.sh --go
#
# The SQLite file was never written after the switch (copy-to-postgres opens it read-only), so the
# platform goes back to EXACTLY what it held at the switch. Anything written since - a ticket, a note,
# a machine's last-seen time - exists only in PostgreSQL: this compares the two, table by table, and
# says which tables changed BEFORE it does anything. Nothing in PostgreSQL is deleted: a backup is made
# first (before-way-back-*.tar.gz.gpg), and the database is renamed, not dropped, so the switch can be
# run again later onto a clean one.
#
# The disks stay as they are - data/ and the backups on rsp-files. Only the database changes.
# =====================================================================================
set -Eeuo pipefail

APP_DIR=/opt/rsp
SERVICE=remotesupport-platform
PG_DROPIN=/etc/systemd/system/$SERVICE.service.d/postgres.conf
DATA=$APP_DIR/data
BACKUPS=/var/backups/rsp
PORT=8090
STAMP="$(date +%Y%m%d-%H%M%S)"
KEPT="rsp_before_way_back_${STAMP//-/_}"

say()  { echo; echo "== $* =="; }
note() { echo "   $*"; }
die()  { echo >&2; echo "!! STOPPED: $*" >&2; exit 1; }
STATE=looking   # looking -> stopped -> renamed -> unswitched

health() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$PORT/health" 2>/dev/null || true; }
wait_healthy() { local i; for i in $(seq 1 60); do [[ "$(health)" == 200 ]] && return 0; sleep 1; done; return 1; }
psql_q() { runuser -u postgres -- psql -X -qAt -v ON_ERROR_STOP=1 "$@"; }

live_json() {
  python3 - "$APP_DIR/appsettings.live.json" "$1" <<'PY'
import json, sys
path, conn = sys.argv[1], sys.argv[2]
try:
    with open(path) as f: d = json.load(f)
except FileNotFoundError:
    d = {}
cs = d.setdefault("ConnectionStrings", {})
if conn: cs["Postgres"] = conn
else: cs.pop("Postgres", None)
if not cs: d.pop("ConnectionStrings")
with open(path, "w") as f: json.dump(d, f)
PY
  chmod 644 "$APP_DIR/appsettings.live.json"
}

# If it stops part-way: back on PostgreSQL, as it was.
recover() {
  set +e
  case $STATE in
    looking) echo "   Nothing was stopped; the platform is as it was, on PostgreSQL." ;;
    stopped|renamed|unswitched)
      [[ $STATE == stopped ]] || psql_q -c "ALTER DATABASE \"$KEPT\" RENAME TO rsp"
      if [[ $STATE == unswitched ]]; then printf '%s' "$DROPIN_TEXT" > "$PG_DROPIN"; live_json "$PG_CONN"; systemctl daemon-reload; fi
      systemctl restart "$SERVICE"
      if wait_healthy; then echo "   The platform is running again ON POSTGRESQL, as before."
      else echo "   !! The platform did not come back by itself. Look: journalctl -u $SERVICE -n 50"; fi ;;
  esac
  echo "   The whole log: $LOG"
}

main() {
  local mode=${1:-}
  [[ $mode == --check || $mode == --go ]] || die "say --check (look only) or --go"
  [[ $EUID -eq 0 ]] || die "run with sudo"
  cd /   # PostgreSQL's tools, run as postgres, cannot read the home folder sudo leaves us in
  LOG="/var/log/rsp-switch-back-$STAMP-${mode#--}.log"
  exec > >(tee -a "$LOG") 2>&1
  trap 'rc=$?; if (( rc != 0 )); then recover; fi' EXIT

  echo "The Remote Support Program - the way back to SQLite (${mode#--}), $(date)"
  say "checking - nothing is changed yet"
  [[ -f $PG_DROPIN ]] || die "this server is not on PostgreSQL (no $PG_DROPIN) - nothing to go back from"
  [[ -f $DATA/platform.db ]] || die "the SQLite file is gone ($DATA/platform.db) - there is nothing to go back to"
  DROPIN_TEXT="$(cat "$PG_DROPIN")"
  PG_CONN="$(grep -o 'RSP_ConnectionStrings__Postgres=[^"]*' "$PG_DROPIN" | head -n1 | cut -d= -f2-)"
  [[ $PG_CONN == *"Database=rsp;"* || $PG_CONN == *"Database=rsp" ]] || die "the connection string does not name the database rsp: $PG_CONN"
  note "platform: $(systemctl is-active "$SERVICE"), on PostgreSQL since $(stat -c %y "$PG_DROPIN" | cut -d. -f1)"
  note "the SQLite file: $(du -h "$DATA/platform.db" | cut -f1), last written $(stat -c %y "$DATA/platform.db" | cut -d. -f1)"

  say "what has changed in PostgreSQL since the switch (lost from the platform if it goes back)"
  local compared rc changed
  set +e
  compared="$(cd "$APP_DIR" && sudo -u rsp ./RemoteSupport.Platform copy-to-postgres --from "$DATA/platform.db" --to "$PG_CONN" --verify-only 2>&1)"
  rc=$?
  set -e
  changed="$(grep -E '^\s+DIFFERENT' <<< "$compared" || true)"
  if grep -q '^OK: all' <<< "$compared"; then note "NOTHING - PostgreSQL still holds exactly what the SQLite file holds."
  elif [[ -n $changed ]]; then echo "$changed" | sed 's/^ */   /'
  elif grep -q 'migration(s) behind' <<< "$compared"; then
    # A deploy since the switch changed the model: the frozen file cannot be compared, but going back still
    # works - the platform brings the file up to date as it starts, as any deploy does.
    note "CANNOT COMPARE: a deploy since the switch changed the database's shape, and the SQLite file is from before it."
    note "Assume EVERYTHING written since the switch is lost from the platform (it stays in PostgreSQL and the backup)."
  else echo "$compared" | tail -5 | sed 's/^/   /'; die "could not compare the two (exit $rc)"; fi

  if [[ $mode == --check ]]; then
    echo
    echo "CHECKED: nothing has been changed. With --go the platform stops for under a minute and comes back on SQLite."
    return
  fi

  say "1/4 a backup of PostgreSQL, kept"
  /usr/local/sbin/rsp-backup
  cp "$BACKUPS/rsp-$(date +%Y%m%d).tar.gz.gpg" "$BACKUPS/before-way-back-$STAMP.tar.gz.gpg"
  note "kept as $BACKUPS/before-way-back-$STAMP.tar.gz.gpg"

  say "2/4 the platform stops; the PostgreSQL database is renamed, never dropped"
  systemctl stop "$SERVICE"
  STATE=stopped
  psql_q -c "ALTER DATABASE rsp RENAME TO \"$KEPT\""
  STATE=renamed
  note "database rsp is now $KEPT"

  say "3/4 the platform back onto the SQLite file"
  rm -f "$PG_DROPIN"
  STATE=unswitched
  live_json ""
  ! grep -q Postgres "$APP_DIR/appsettings.live.json" || die "appsettings.live.json still names PostgreSQL"
  systemctl daemon-reload
  local since; since="$(date '+%Y-%m-%d %H:%M:%S')"
  systemctl start "$SERVICE"
  wait_healthy || die "the platform did not answer /health within a minute on SQLite"
  local started; started="$(journalctl -u "$SERVICE" --since "$since" --no-pager -o cat | grep -m1 'DB: ' || true)"
  grep -q "DB: $DATA/platform.db" <<< "$started" || die "the platform started but not on the SQLite file: ${started:-no start-up line}"
  note "$started"
  STATE=done

  say "4/4 tonight's backup, on SQLite again"
  note "$(/usr/local/sbin/rsp-backup)"

  cat <<EOF

==========================================================================
 DONE: the platform runs on the SQLite file again ($(date +%H:%M)).
--------------------------------------------------------------------------
 What PostgreSQL held is kept: database $KEPT,
 and $BACKUPS/before-way-back-$STAMP.tar.gz.gpg.
 To switch again later: Switch-ToPostgres.ps1 (it copies into a fresh database).
 This log: $LOG
==========================================================================
EOF
}

main "$@"
