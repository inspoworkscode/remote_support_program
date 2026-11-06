#!/usr/bin/env bash
# =====================================================================================
# Read-only: which database the platform is on, its disks, and whether all is well (2026-10-09).
# Run by Get-DatabaseStatus.ps1 with sudo; safe on the live server at any time - it changes nothing.
# =====================================================================================
set -uo pipefail
SERVICE=remotesupport-platform
PG_DROPIN=/etc/systemd/system/$SERVICE.service.d/postgres.conf
DATA=/opt/rsp/data
q() { cd / && runuser -u rsp -- psql -X -qAt -d rsp -c "$1" 2>/dev/null; }

echo "== $(hostname), $(date) =="
health="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8090/health || true)"
echo "   platform: $(systemctl is-active $SERVICE), /health -> $health, up since $(systemctl show -p ActiveEnterTimestamp --value $SERVICE)"
echo "   it said:  $(journalctl -u $SERVICE -b --no-pager -o cat | grep 'DB: ' | tail -n1 | sed 's/.*\(DB: .*\)\. Client payload.*/\1/' || true)"
if [[ -f $PG_DROPIN ]]; then echo "   database: PostgreSQL (postgres.conf since $(stat -c %y "$PG_DROPIN" | cut -d. -f1))"
else echo "   database: SQLite"; fi

echo; echo "== disks =="
lsblk -i -o NAME,SIZE,FSTYPE,LABEL,MOUNTPOINT | grep -v loop | sed 's/^/   /'
for m in / /srv/rsp-files /var/lib/postgresql /opt/rsp/data /var/backups/rsp; do
  if mountpoint -q "$m"; then printf '   %-22s %-12s %s used of %s\n' "$m" "$(findmnt -n -o SOURCE "$m" | sed 's/\[.*//')" "$(df -h --output=used "$m" | tail -1 | tr -d ' ')" "$(df -h --output=size "$m" | tail -1 | tr -d ' ')"
  else printf '   %-22s (on the system disk)\n' "$m"; fi
done
grep -A1 '^# rsp-switch: ' /etc/fstab | grep -v '^#\|^--' | sed 's/^/   fstab: /' || true

echo; echo "== counts =="
for t in Tenants Technicians Devices Clients Tickets TicketNotes; do
  s="$(sqlite3 -readonly "$DATA/platform.db" "SELECT COUNT(*) FROM \"$t\"" 2>/dev/null || echo -)"
  p="-"; command -v psql >/dev/null && p="$(q "SELECT COUNT(*) FROM \"$t\"" || echo -)"; [[ -n $p ]] || p="-"
  printf '   %-12s SQLite file %6s   PostgreSQL %6s\n' "$t" "$s" "$p"
done
if command -v pg_lsclusters >/dev/null; then
  echo; echo "== PostgreSQL =="; pg_lsclusters | sed 's/^/   /'
  runuser -u postgres -- psql -X -qAt -c "SELECT '   ' || datname || ': ' || pg_size_pretty(pg_database_size(datname)) FROM pg_database WHERE datname LIKE 'rsp%'" 2>/dev/null || true
fi

echo; echo "== backups =="
ls -1t /var/backups/rsp/*.gpg 2>/dev/null | head -n 4 | while read -r f; do echo "   $(basename "$f")  $(du -h "$f" | cut -f1)"; done
echo "   recordings kept once: $(find /var/backups/rsp/recordings -name '*.gpg' 2>/dev/null | wc -l)"
