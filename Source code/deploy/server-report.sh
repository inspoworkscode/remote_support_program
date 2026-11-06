#!/usr/bin/env bash
# =====================================================================================
# A read-only picture of the live server (deploy/Get-ServerReport.ps1 runs this over SSH).
# It CHANGES NOTHING and prints no secret: the owner key, the backup passphrase and any
# connection string are only ever reported as "set" or "not set".
# =====================================================================================
h() { echo; echo "== $1 =="; }
s() { sudo -n "$@" 2>/dev/null; }

h "the machine"
echo "name        : $(hostname)"
echo "system      : $(. /etc/os-release && echo "$PRETTY_NAME"), kernel $(uname -r)"
echo "up          : $(uptime -p)"
echo "processors  : $(nproc) x $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs)"
free -h | awk '/^Mem:/ {print "memory      : " $2 " total, " $3 " used, " $7 " available"} /^Swap:/ {print "swap        : " $2 " total, " $3 " used"}'

h "disks (every disk the server can see, attached volumes included)"
lsblk -i -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINTS -e 7 2>/dev/null || lsblk -i
h "how full"
df -h -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null

h "the platform"
echo "service     : $(systemctl is-active remotesupport-platform) (since $(systemctl show -p ActiveEnterTimestamp --value remotesupport-platform))"
echo "program     : /opt/rsp, built $(date -r /opt/rsp/RemoteSupport.Platform '+%Y-%m-%d %H:%M' 2>/dev/null)"
pid=$(systemctl show -p MainPID --value remotesupport-platform)
[[ "$pid" -gt 0 ]] && echo "using       : $(ps -o rss= -p "$pid" | awk '{printf "%.0f MB memory", $1/1024}'), $(ls /proc/"$pid"/task 2>/dev/null | wc -l) threads"
if s grep -qs 'ConnectionStrings__Postgres' /etc/systemd/system/remotesupport-platform.service.d/*.conf; then echo "database    : PostgreSQL (a connection string is set)"; else echo "database    : SQLite (/opt/rsp/data/platform.db)"; fi
echo "public URL  : $(grep -ho 'PublicUrl=[^ ]*' /etc/systemd/system/remotesupport-platform.service.d/override.conf 2>/dev/null | cut -d= -f2)"
echo "owner key   : $(s test -s /etc/rsp-owner-key && echo set || echo NOT set)"
echo "connections : $(ss -Htn state established '( sport = :443 )' | wc -l) open on 443 right now"

h "what is in data/ (sizes)"
s du -sh /opt/rsp/data 2>/dev/null | sed 's/^/total       : /'
s du -sh /opt/rsp/data/* 2>/dev/null | sort -h | sed 's/^/  /'
s test -f /opt/rsp/data/platform.db && echo "  companies in the database: $(s sqlite3 /opt/rsp/data/platform.db 'select count(*) from Tenants;' 2>/dev/null), machines: $(s sqlite3 /opt/rsp/data/platform.db 'select count(*) from Devices;' 2>/dev/null), tickets: $(s sqlite3 /opt/rsp/data/platform.db 'select count(*) from Tickets;' 2>/dev/null)"

h "backups"
echo "nightly     : $(cat /etc/cron.d/rsp-backup 2>/dev/null || echo 'NO cron entry')"
echo "passphrase  : $(s test -s /etc/rsp-backup-key && echo set || echo NOT set)"
if s test -d /var/backups/rsp; then
  echo "kept        : $(s ls /var/backups/rsp | wc -l) files, $(s du -sh /var/backups/rsp | cut -f1) in all"
  s ls -lh --time-style='+%Y-%m-%d %H:%M' /var/backups/rsp | tail -n +2 | tail -5 | awk '{print "  " $6 " " $7 "  " $5 "  " $8}'
else echo "kept        : NO /var/backups/rsp"; fi
echo "office copy : $(id -u rspcopy >/dev/null 2>&1 && echo 'set up (account rspcopy exists)' || echo 'not set up yet')"

h "other services"
echo "Caddy (TLS) : $(systemctl is-active caddy), site $(grep -m1 -oE '^[a-z0-9.-]+\.[a-z]+' /etc/caddy/Caddyfile 2>/dev/null)"
# Caddy holds ~100 KB per connection and gives little back (HANDOVER 126). Every deploy restarts it, so it should
# stay about the same between deploys while the machines do; climbing on its own means it is growing.
cpid=$(systemctl show -p MainPID --value caddy)
[[ "$cpid" -gt 0 ]] && echo "Caddy using : $(ps -o rss= -p "$cpid" | awk '{printf "%.0f MB memory", $1/1024}'), since $(systemctl show -p ActiveEnterTimestamp --value caddy) (fresh at every deploy)"
echo "PostgreSQL  : $(command -v psql >/dev/null && echo "installed, $(systemctl is-active postgresql 2>/dev/null)" || echo 'not installed')"
echo "Chrome (PDF): $([[ -x /usr/bin/google-chrome ]] && echo installed || echo 'not installed')"
echo "fail2ban    : $(systemctl is-active fail2ban 2>/dev/null)"
echo "auto updates: $(systemctl is-active unattended-upgrades 2>/dev/null)"
echo "reboot due  : $([[ -f /var/run/reboot-required ]] && echo 'YES - updates are waiting for a restart' || echo no)"

h "firewall"
s ufw status | sed 's/^/  /'
