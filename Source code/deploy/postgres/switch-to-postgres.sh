#!/usr/bin/env bash
# =====================================================================================
# The Remote Support Program: move the live server onto PostgreSQL and its own disks.
# Step 4 of HANDOVER section 118, built 2026-10-09. Clint runs it from his laptop with
# deploy/postgres/Switch-ToPostgres.ps1, which sends this file over ssh and runs it with sudo:
#
#   sudo bash switch-to-postgres.sh --check [--files-gb 50] [--db-gb 20]   looks; changes NOTHING
#   sudo bash switch-to-postgres.sh --go    [--files-gb 50] [--db-gb 20]   does it
#   bash switch-to-postgres.sh --find-disks [--files-gb N] [--db-gb N]     the disk choice alone (tools/switchcheck)
#
# In order, stopping with a plain message at the first thing that is not right:
#   1. The root partition grows to fill its disk (online, growpart + resize2fs).
#   2. The two blank disks are found BY SIZE AND BLANKNESS - never by name: vdb and vdc can swap across
#      a reboot. Anything ambiguous, or not blank, is refused. Each is formatted ext4 with a label
#      (rsp-files, rsp-db) and mounted by UUID with nofail: rsp-files at /srv/rsp-files, rsp-db at
#      /var/lib/postgresql. A disk that already carries our label (an earlier run) is used, never formatted.
#   3. PostgreSQL 16 is installed AFTER rsp-db is mounted, so its cluster lands on that disk. Role rsp and
#      database rsp; the account rsp signs in over the local socket as itself (peer) - no password exists.
#      Modest tuning for the machine's memory; no TCP at all.
#   -- the platform stops here --
#   4. A fresh backup (kept as before-switch-*.tar.gz.gpg, never pruned).
#   5. /opt/rsp/data and /var/backups/rsp are COPIED onto rsp-files, every file's name, size, mode and
#      owner compared, then bind-mounted back at the same paths - so nothing that uses them changes. The
#      originals are kept in /var/lib/rsp-before-disks until the SQLite fallback is retired.
#   6. copy-to-postgres: every table copied and then checked row by row. It must say OK.
#   7. The platform is pointed at PostgreSQL (the drop-in postgres.conf and appsettings.live.json) and
#      started; /health must answer and its start-up line must say PostgreSQL.
#   8. A few counts side by side, and the nightly backup run once, on PostgreSQL.
# If anything fails after the platform stopped, it is started again ON SQLITE, as it was. The SQLite
# file is never written: copy-to-postgres opens it read-only. The way back is switch-back-to-sqlite.sh.
# =====================================================================================
set -Eeuo pipefail

APP_DIR=/opt/rsp
SERVICE=remotesupport-platform
DROPIN_DIR=/etc/systemd/system/$SERVICE.service.d
PG_DROPIN=$DROPIN_DIR/postgres.conf
DISKS_DROPIN=$DROPIN_DIR/disks.conf
DATA=$APP_DIR/data
BACKUPS=/var/backups/rsp
FILES_MNT=/srv/rsp-files
DB_MNT=/var/lib/postgresql
ASIDE=/var/lib/rsp-before-disks
PG_CONN="Host=/var/run/postgresql;Database=rsp;Username=rsp"
PORT=8090
STAMP="$(date +%Y%m%d-%H%M%S)"
# Only tools/switchcheck sets these: a folder standing in for /dev, so the disk choice can be proved on a PC.
DEV="${RSP_SWITCH_DEV:-/dev}"

say()  { echo; echo "== $* =="; }
note() { echo "   $*"; }
# To stderr: a refusal inside $( ... ) must still be seen, not swallowed into a variable.
die()  { echo >&2; echo "!! STOPPED: $*" >&2; exit 1; }

STATE=looking   # looking -> stopped -> switched; what to undo if it stops part-way

# ---------------------------------------------------------------------------------------------
# The disks, by size and blankness.
# ---------------------------------------------------------------------------------------------
declare -a DISKS=()
declare -A SIZE=() FSTYPE_OF=() PTTYPE_OF=() LABEL_OF=() MOUNTED=() HASKIDS=()

read_disks() {
  local line NAME SIZE_ TYPE FSTYPE PTTYPE LABEL MOUNTPOINT PKNAME
  while IFS= read -r line; do
    NAME='' SIZE_='' TYPE='' FSTYPE='' PTTYPE='' LABEL='' MOUNTPOINT='' PKNAME=''
    # lsblk -P prints KEY="value" pairs, escaping anything odd itself.
    eval "${line/SIZE=/SIZE_=}"
    [[ -n $PKNAME ]] && HASKIDS[$PKNAME]=1
    [[ -n $MOUNTPOINT && -n $PKNAME ]] && MOUNTED[$PKNAME]=1
    [[ $TYPE == disk ]] || continue
    DISKS+=("$NAME"); SIZE[$NAME]=$SIZE_; FSTYPE_OF[$NAME]=$FSTYPE; PTTYPE_OF[$NAME]=$PTTYPE; LABEL_OF[$NAME]=$LABEL
    [[ -n $MOUNTPOINT ]] && MOUNTED[$NAME]=1
  done < <(lsblk -bnpPo NAME,SIZE,TYPE,FSTYPE,PTTYPE,LABEL,MOUNTPOINT,PKNAME)
  (( ${#DISKS[@]} > 0 )) || die "lsblk listed no disks at all"
}

gb() { echo "$(( $1 / 1000000000 )) GB"; }

# A size the panel calls N GB: anything from 97% of N x 10^9 to 103% of N x 2^30 bytes.
size_is() { local bytes=$1 n=$2; (( bytes >= n * 1000000000 * 97 / 100 && bytes <= n * 1073741824 * 103 / 100 )); }

# Why a disk is not blank, or nothing when it is.
not_blank() {
  local d=$1 f="$DEV/${1#/dev/}" why=() rc=0
  [[ -n ${FSTYPE_OF[$d]} ]] && why+=("it has a ${FSTYPE_OF[$d]} filesystem")
  [[ -n ${PTTYPE_OF[$d]} ]] && why+=("it has a partition table")
  [[ -n ${HASKIDS[$d]:-} ]] && why+=("it has partitions")
  [[ -n ${MOUNTED[$d]:-} ]] && why+=("it is mounted")
  blkid -p "$d" >/dev/null 2>&1 || rc=$?
  (( rc == 2 )) || why+=("blkid finds something on it")
  if [[ ! -r $f ]]; then why+=("it cannot be read")
  elif [[ -n "$(head -c 1048576 "$f" | tr -d '\0' | head -c 1)" ]]; then why+=("its first megabyte is not empty"); fi
  (( ${#why[@]} == 0 )) || { local IFS=';'; echo "${why[*]}" | sed 's/;/, /g'; }
}

# pick_disk <label> <gb>: prints "<device> ours" or "<device> blank", or stops.
pick_disk() {
  local label=$1 n=$2 d ours=() blank=() why
  for d in "${DISKS[@]}"; do
    [[ ${LABEL_OF[$d]} == "$label" ]] || continue
    [[ ${FSTYPE_OF[$d]} == ext4 && -z ${HASKIDS[$d]:-} ]] || die "$d is labelled $label but is not a whole-disk ext4 filesystem - look at it by hand"
    ours+=("$d")
  done
  (( ${#ours[@]} <= 1 )) || die "more than one disk is labelled $label: ${ours[*]}"
  if (( ${#ours[@]} == 1 )); then echo "${ours[0]} ours"; return; fi
  for d in "${DISKS[@]}"; do
    size_is "${SIZE[$d]}" "$n" || continue
    why="$(not_blank "$d")"
    if [[ -z $why ]]; then blank+=("$d"); else echo "   ($d is $(gb "${SIZE[$d]}") but not blank - $why - left alone)" >&2; fi
  done
  (( ${#blank[@]} > 0 )) || die "no blank $n GB disk for $label. Is the volume attached in the xneelo panel?"
  (( ${#blank[@]} == 1 )) || die "${#blank[@]} blank $n GB disks (${blank[*]}) - cannot tell which is $label. Detach the extra one."
  echo "${blank[0]} blank"
}

find_disks() {
  (( FILES_GB != DB_GB )) || die "the two disks must differ in size to be told apart (both $FILES_GB GB)"
  read_disks
  local f d
  f="$(pick_disk rsp-files "$FILES_GB")"; d="$(pick_disk rsp-db "$DB_GB")"
  FILES_DEV=${f% *}; FILES_STATE=${f#* }; DB_DEV=${d% *}; DB_STATE=${d#* }
  [[ $FILES_DEV != "$DB_DEV" ]] || die "the same disk ($FILES_DEV) was chosen for both"
}

# ---------------------------------------------------------------------------------------------
# Small pieces.
# ---------------------------------------------------------------------------------------------
health() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$PORT/health" 2>/dev/null || true; }

wait_healthy() {
  local i; for i in $(seq 1 60); do [[ "$(health)" == 200 ]] && return 0; sleep 1; done; return 1
}

# appsettings.live.json with the connection string put in (a value) or taken out ("").
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

as_postgres() { runuser -u postgres -- "$@"; }
psql_q() { as_postgres psql -X -qAt -v ON_ERROR_STOP=1 "$@"; }

# /etc/fstab: one entry per mount point, found by its mount point (field 2), with its note on the line ABOVE -
# fstab only promises comments on lines of their own, and a line it cannot read is a disk lost at the next boot.
fstab_unset() {
  local tmp; tmp="$(mktemp)"
  awk -v m="$1" '!((($0 !~ /^[[:space:]]*#/) && $2 == m) || $0 == "# rsp-switch: " m)' /etc/fstab > "$tmp"
  cat "$tmp" > /etc/fstab; rm -f "$tmp"
}
fstab_set() { # <mount point> <the whole entry>
  [[ -f /etc/fstab.before-rsp-switch ]] || cp /etc/fstab /etc/fstab.before-rsp-switch
  fstab_unset "$1"
  printf '# rsp-switch: %s\n%s\n' "$1" "$2" >> /etc/fstab
  systemctl daemon-reload
}

# Same names, sizes, modes and owners on both sides of a copy.
listing() { (cd "$1" && find . -printf '%p %s %m %u %g\n' | LC_ALL=C sort); }

# move_dir <path> <on-disk copy> <aside name>: copy, compare, then bind-mount the copy at the same path.
move_dir() {
  local src=$1 dst=$2 aside="$ASIDE/$3" n
  if mountpoint -q "$src"; then note "$src is already on rsp-files (an earlier run)"; return; fi
  [[ ! -e $aside ]] || die "$aside already exists - an earlier run stopped part-way here; look at it by hand"
  mkdir -p "$dst"
  cp -a "$src/." "$dst/"
  chown --reference="$src" "$dst"; chmod --reference="$src" "$dst"
  if ! diff <(listing "$src") <(listing "$dst") >/dev/null; then
    die "the copy of $src on rsp-files does not match it (nothing moved; the original is untouched)"
  fi
  n="$(listing "$src" | wc -l)"
  fstab_set "$src" "$dst $src none bind,nofail,x-systemd.requires-mounts-for=$FILES_MNT 0 0"
  mkdir -p "$ASIDE"; chmod 700 "$ASIDE"
  mv "$src" "$aside"
  mkdir "$src"
  if ! mount "$src"; then
    rmdir "$src"; mv "$aside" "$src"; fstab_unset "$src"; systemctl daemon-reload
    die "could not bind-mount $dst at $src (put back as it was)"
  fi
  note "$src: $n files and folders, now on rsp-files ($dst); the original kept in $aside"
}

# ---------------------------------------------------------------------------------------------
# If it stops part-way: the platform back as it was, on SQLite.
# ---------------------------------------------------------------------------------------------
recover() {
  set +e
  case $STATE in
    looking) echo "   Nothing was stopped; the platform is as it was." ;;
    stopped|switched)
      if [[ $STATE == switched ]]; then
        rm -f "$PG_DROPIN"; live_json ""; systemctl daemon-reload
        echo "   The PostgreSQL setting was taken out again."
      fi
      systemctl restart "$SERVICE"
      if wait_healthy; then echo "   The platform is running again ON SQLITE, as before. Nothing was lost."
      else echo "   !! The platform did not come back by itself. Look: journalctl -u $SERVICE -n 50"; fi ;;
  esac
  echo "   The whole log: $LOG"
}

# ---------------------------------------------------------------------------------------------
main() {
  MODE='' FILES_GB=50 DB_GB=20
  while (( $# )); do
    case $1 in
      --check|--go|--find-disks) MODE=${1#--} ;;
      --files-gb) FILES_GB=$2; shift ;;
      --db-gb) DB_GB=$2; shift ;;
      *) die "unknown argument $1" ;;
    esac
    shift
  done
  [[ -n $MODE ]] || die "say --check (look only) or --go"
  [[ $FILES_GB =~ ^[0-9]+$ && $DB_GB =~ ^[0-9]+$ ]] || die "--files-gb and --db-gb are whole numbers"

  if [[ $MODE == find-disks ]]; then
    find_disks
    echo "rsp-files $FILES_DEV $FILES_STATE"
    echo "rsp-db $DB_DEV $DB_STATE"
    return
  fi

  [[ $EUID -eq 0 ]] || die "run with sudo"
  cd /   # PostgreSQL's tools, run as postgres, cannot read the home folder sudo leaves us in
  LOG="/var/log/rsp-switch-$STAMP-$MODE.log"
  exec > >(tee -a "$LOG") 2>&1
  trap 'rc=$?; if (( rc != 0 )); then recover; fi' EXIT
  export DEBIAN_FRONTEND=noninteractive

  echo "The Remote Support Program - switching to PostgreSQL ($MODE), $(date)"
  say "checking - nothing is changed yet"
  . /etc/os-release
  [[ ${VERSION_ID:-} == 24.04 ]] || die "this is written for Ubuntu 24.04 (PostgreSQL 16); this server is ${PRETTY_NAME:-unknown}"
  [[ -x $APP_DIR/RemoteSupport.Platform ]] || die "no platform at $APP_DIR"
  systemctl cat "$SERVICE" >/dev/null 2>&1 || die "no service $SERVICE"
  [[ ! -f $PG_DROPIN ]] || die "this server is ALREADY on PostgreSQL ($PG_DROPIN). The way back is Switch-BackToSqlite.ps1."
  [[ -f $DATA/platform.db ]] || die "no SQLite database at $DATA/platform.db"
  # copy-to-postgres is in the program (its name is in the .dll, as UTF-16): Step A, the deploy, came first.
  LC_ALL=C grep -qaP 'c\x00o\x00p\x00y\x00-\x00t\x00o\x00-\x00p\x00o\x00s\x00t\x00g\x00r\x00e\x00s\x00' "$APP_DIR/RemoteSupport.Platform.dll" \
    || die "the platform on this server has no copy-to-postgres - deploy first (Publish-Platform.ps1), then switch"
  grep -q pg_dump /usr/local/sbin/rsp-backup 2>/dev/null || die "the nightly backup does not know PostgreSQL yet - deploy first (Publish-Platform.ps1)"
  note "platform: $(systemctl is-active "$SERVICE"), on SQLite ($(du -h "$DATA/platform.db" | cut -f1))"
  if dpkg -s postgresql-16 >/dev/null 2>&1; then
    PG_ALREADY=yes; note "PostgreSQL 16 is already installed (an earlier run?)"
  else PG_ALREADY=no; fi
  if mountpoint -q "$DB_MNT" && [[ $PG_ALREADY == no ]] && [[ -n "$(ls -A "$DB_MNT" | grep -v '^lost+found$' || true)" ]]; then
    die "$DB_MNT is mounted and holds files, but PostgreSQL is not installed - look at it by hand"
  fi
  if [[ $PG_ALREADY == no && -d $DB_MNT ]] && ! mountpoint -q "$DB_MNT" && [[ -n "$(ls -A "$DB_MNT")" ]]; then
    die "$DB_MNT already holds files on the system disk - look at it by hand"
  fi

  find_disks
  note "rsp-files: $FILES_DEV, $(gb "${SIZE[$FILES_DEV]}"), $([[ $FILES_STATE == ours ]] && echo "already formatted by an earlier run" || echo "blank - will be formatted")"
  note "rsp-db:    $DB_DEV, $(gb "${SIZE[$DB_DEV]}"), $([[ $DB_STATE == ours ]] && echo "already formatted by an earlier run" || echo "blank - will be formatted")"
  if [[ $PG_ALREADY == yes ]]; then
    [[ "$(findmnt -n -o SOURCE --target "$DB_MNT/16/main" 2>/dev/null)" == "$DB_DEV" ]] \
      || die "PostgreSQL is installed but its data is not on rsp-db ($DB_DEV) - not touching it"
  fi
  # Only what is still on the system disk is moved (a re-run finds it already there - rehearsal, 2026-10-09).
  local need=0 have d moving=()
  for d in "$DATA" "$BACKUPS"; do
    [[ -d $d ]] && ! mountpoint -q "$d" || continue
    need=$(( need + $(du -sb "$d" | cut -f1) )); moving+=("$d $(du -sh "$d" | cut -f1)")
  done
  have=$(( ${SIZE[$FILES_DEV]} * 90 / 100 ))
  (( need < have )) || die "data and backups ($(( need / 1048576 )) MB) will not fit on rsp-files"
  if (( ${#moving[@]} )); then note "to move onto rsp-files: $(IFS=,; echo "${moving[*]}" | sed 's/,/, /g')"
  else note "data and backups are already on rsp-files (an earlier run)"; fi
  local root_src root_disk root_part
  root_src="$(findmnt -n -o SOURCE /)"
  root_disk="$(lsblk -no PKNAME "$root_src")"; root_part="$(cat "/sys/class/block/$(basename "$root_src")/partition")"
  note "root: $root_src, $(df -h --output=size / | tail -1 | tr -d ' ') of a $(lsblk -dno SIZE "/dev/$root_disk" | tr -d ' ') disk"
  MEM_MB=$(( $(awk '/MemTotal/ {print $2}' /proc/meminfo) / 1024 ))
  note "memory: $MEM_MB MB"

  if [[ $MODE == check ]]; then
    echo
    echo "CHECKED: nothing has been changed. With --go this stops the platform for a minute or two."
    return
  fi

  # ======================= 1. the root partition =======================
  say "1/8 the root partition fills its disk"
  command -v growpart >/dev/null || apt-get -o DPkg::Lock::Timeout=600 install -y -qq cloud-guest-utils >/dev/null
  local out
  if out="$(growpart "/dev/$root_disk" "$root_part" 2>&1)"; then
    [[ "$(findmnt -n -o FSTYPE /)" == ext4 ]] && resize2fs "$root_src" >/dev/null 2>&1
  else
    grep -q NOCHANGE <<< "$out" || die "growpart: $out"
  fi
  note "root is now $(df -h --output=size / | tail -1 | tr -d ' ') ($(df -h --output=avail / | tail -1 | tr -d ' ') free)"

  # ======================= 2. the disks =======================
  say "2/8 the two disks"
  local label dev mnt uuid
  local state
  for label in rsp-files rsp-db; do
    if [[ $label == rsp-files ]]; then dev=$FILES_DEV mnt=$FILES_MNT state=$FILES_STATE
    else dev=$DB_DEV mnt=$DB_MNT state=$DB_STATE; fi
    # -F: a whole disk can make mkfs ask "Proceed anyway?", and nobody is at a keyboard to say yes. It is
    # proved blank above (no filesystem, partition or mount, and an empty first megabyte). -m 1: a data disk
    # needs 1% kept for root, not 5%.
    if [[ $state == blank ]]; then mkfs.ext4 -q -F -m 1 -L "$label" "$dev"; note "$dev formatted as $label"; fi
    uuid="$(blkid -s UUID -o value "$dev")"
    [[ -n $uuid ]] || die "$dev has no filesystem UUID after formatting"
    fstab_set "$mnt" "UUID=$uuid $mnt ext4 defaults,nofail 0 2"
    mkdir -p "$mnt"
    mountpoint -q "$mnt" || mount "$mnt"
    [[ "$(findmnt -n -o UUID "$mnt")" == "$uuid" ]] || die "$mnt is not the $label disk after mounting"
    note "$label: $dev mounted at $mnt by UUID $uuid ($(df -h --output=size "$mnt" | tail -1 | tr -d ' '))"
  done

  # ======================= 3. PostgreSQL =======================
  say "3/8 PostgreSQL 16 on rsp-db"
  if [[ $PG_ALREADY == no ]]; then
    apt-get -o DPkg::Lock::Timeout=600 update -qq
    apt-get -o DPkg::Lock::Timeout=600 install -y -qq postgresql-16 >/dev/null
  fi
  [[ "$(findmnt -n -o SOURCE --target "$DB_MNT/16/main")" == "$DB_DEV" ]] || die "PostgreSQL's data is not on rsp-db"
  # Its home, as on any Ubuntu server (mkfs left the disk's top folder root's).
  chown postgres:postgres "$DB_MNT"
  # A quarter of a percent of guesswork: an eighth of memory for PostgreSQL's own cache, half for what the
  # system caches for it; the platform itself needs under a gigabyte (load test, section 121). No TCP at all:
  # the platform, backups and an administrator all use the local socket. max_connections stays 100; the
  # platform takes 40 at most (PgPlatformDb.Prepare).
  cat > /etc/postgresql/16/main/conf.d/rsp.conf <<EOF
# The Remote Support Program (switch-to-postgres.sh, $STAMP), for $MEM_MB MB of memory.
listen_addresses = ''
max_connections = 100
shared_buffers = $(( MEM_MB / 8 ))MB
effective_cache_size = $(( MEM_MB / 2 ))MB
maintenance_work_mem = 256MB
work_mem = 16MB
random_page_cost = 1.1
wal_compression = on
EOF
  systemctl restart postgresql@16-main
  local i; for i in $(seq 1 30); do as_postgres pg_isready -q && break; sleep 1; done
  as_postgres pg_isready -q || die "PostgreSQL did not start (journalctl -u postgresql@16-main)"
  # Its own default is localhost; empty is only ever ours. (Not shared_buffers: 1024MB reads back as 1GB.)
  [[ -z "$(psql_q -c 'SHOW listen_addresses')" ]] \
    || die "PostgreSQL did not read conf.d/rsp.conf (is include_dir = 'conf.d' in /etc/postgresql/16/main/postgresql.conf?)"
  [[ "$(psql_q -c "SELECT 1 FROM pg_roles WHERE rolname = 'rsp'")" == 1 ]] || as_postgres createuser --no-superuser --no-createdb --no-createrole rsp
  # Never dropped: a database called rsp that is already there (a run that stopped part-way, or anything
  # else) is renamed out of the way and kept.
  if [[ "$(psql_q -c "SELECT 1 FROM pg_database WHERE datname = 'rsp'")" == 1 ]]; then
    psql_q -c "ALTER DATABASE rsp RENAME TO rsp_unused_${STAMP//-/_}"
    note "a database called rsp was already there - kept, renamed rsp_unused_${STAMP//-/_}"
  fi
  as_postgres createdb --owner=rsp rsp
  [[ "$(cd / && runuser -u rsp -- psql -X -qAt -d rsp -c 'SELECT current_user')" == rsp ]] \
    || die "the account rsp cannot sign in to its database over the socket"
  note "PostgreSQL $(psql_q -c 'SHOW server_version' | cut -d' ' -f1): database rsp, owned by rsp, peer sign-in over the socket, shared_buffers $(( MEM_MB / 8 ))MB"

  # ======================= 4. the platform stops; a backup =======================
  say "4/8 the platform stops, and a backup is made"
  systemctl stop "$SERVICE"
  STATE=stopped
  note "stopped at $(date +%H:%M:%S)"
  /usr/local/sbin/rsp-backup
  cp "$BACKUPS/rsp-$(date +%Y%m%d).tar.gz.gpg" "$BACKUPS/before-switch-$STAMP.tar.gz.gpg"
  note "kept as $BACKUPS/before-switch-$STAMP.tar.gz.gpg"

  # ======================= 5. data and backups onto rsp-files =======================
  say "5/8 data and backups onto rsp-files"
  move_dir "$DATA" "$FILES_MNT/data" data
  move_dir "$BACKUPS" "$FILES_MNT/backups" backups
  # The platform never starts without its data disk: on an empty /opt/rsp/data it would begin a new key ring.
  cat > "$DISKS_DROPIN" <<EOF
# The Remote Support Program (switch-to-postgres.sh): data/ is on the rsp-files disk; never start without it.
[Unit]
RequiresMountsFor=$DATA
EOF
  systemctl daemon-reload

  # ======================= 6. the copy =======================
  say "6/8 every table copied into PostgreSQL, then checked row by row"
  local copied
  set +e
  # As the platform's own account (peer sign-in), the way every command-line use on this server is run.
  copied="$(cd "$APP_DIR" && sudo -u rsp ./RemoteSupport.Platform copy-to-postgres --from "$DATA/platform.db" --to "$PG_CONN" 2>&1)"
  local rc=$?
  set -e
  echo "$copied" | sed 's/^/   /'
  (( rc == 0 )) && grep -q '^OK: all' <<< "$copied" || die "the copy did not say OK"

  # ======================= 7. switch =======================
  say "7/8 the platform onto PostgreSQL"
  cat > "$PG_DROPIN" <<EOF
# The Remote Support Program runs on PostgreSQL (switch-to-postgres.sh, $STAMP).
# go-live.sh never writes this file; it reads the connection string from it on every deploy.
# The way back (switch-back-to-sqlite.sh) removes it.
[Unit]
After=postgresql.service
Wants=postgresql.service

[Service]
Environment="RSP_ConnectionStrings__Postgres=$PG_CONN"
EOF
  STATE=switched
  live_json "$PG_CONN"
  grep -qF "\"Postgres\": \"$PG_CONN\"" "$APP_DIR/appsettings.live.json" || die "appsettings.live.json did not take the connection string"
  systemctl daemon-reload
  local since; since="$(date '+%Y-%m-%d %H:%M:%S')"
  systemctl start "$SERVICE"
  wait_healthy || die "the platform did not answer /health within a minute on PostgreSQL"
  local started; started="$(journalctl -u "$SERVICE" --since "$since" --no-pager -o cat | grep -m1 'DB: ' || true)"
  grep -q 'DB: PostgreSQL' <<< "$started" || die "the platform started but did not say it is on PostgreSQL: ${started:-no start-up line}"
  note "$started"
  note "back up at $(date +%H:%M:%S)"

  # ======================= 8. checks =======================
  say "8/8 side by side, and tonight's backup"
  local t s p
  for t in Tenants Technicians Devices Clients Tickets TicketNotes; do
    s="$(sqlite3 -readonly "$DATA/platform.db" "SELECT COUNT(*) FROM \"$t\"")"
    p="$(cd / && runuser -u rsp -- psql -X -qAt -d rsp -c "SELECT COUNT(*) FROM \"$t\"")"
    printf '   %-12s SQLite %6s   PostgreSQL %6s%s\n' "$t" "$s" "$p" "$([[ $s == "$p" ]] || echo '   (changed since the copy - machines and people are back)')"
  done
  note "$(/usr/local/sbin/rsp-backup)"

  cat <<EOF

==========================================================================
 DONE: the platform runs on PostgreSQL ($(date +%H:%M)).
--------------------------------------------------------------------------
 Now check by hand: the portal, a ticket, the machines coming back online.
 The way back (only if something is wrong): Switch-BackToSqlite.ps1 -
   anything written from now on exists only in PostgreSQL; it says what.
 Kept until the fallback is retired (about a week):
   $DATA/platform.db                      the SQLite database, never written
   $ASIDE                 data and backups as they were on the system disk
   $BACKUPS/before-switch-$STAMP.tar.gz.gpg
 This log: $LOG
==========================================================================
EOF
}

main "$@"
