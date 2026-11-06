#!/usr/bin/env bash
# The load-test server's vital signs every 5 s (deploy/loadtest): the platform, Caddy and PostgreSQL's
# processor and memory, the whole machine (steal included: a shared cloud's neighbours), the network,
# connections on 443 and Caddy's connections to the platform against the ports Linux allows it, PostgreSQL's
# connections against the platform's pool of 40 (HANDOVER section 121), lock waits, the longest query,
# commits and slow statements, and the database disk. Installed by Prepare-LoadServer.ps1 as
# /usr/local/sbin/rsp-loadstats; watched by Watch-LoadServer.ps1. Read-only.
#
# For tools/loadstatscheck alone (never set on a server): RSP_PROC, RSP_ETC, RSP_PGLOG, RSP_INTERVAL and
# RSP_SAMPLES (stop after that many readings; 0 = until stopped).
set -u
P=${RSP_PROC:-/proc}
ETC=${RSP_ETC:-/etc}
PGLOG=${RSP_PGLOG:-/var/log/postgresql/postgresql-16-main.log}
INTERVAL=${RSP_INTERVAL:-5}
SAMPLES=${RSP_SAMPLES:-0}

cores=$(grep -c '^cpu[0-9]' "$P/stat")
iface=$(ip route | awk '/^default/ {for (i = 1; i < NF; i++) if ($i == "dev") {print $(i + 1); exit}}')
pid_of() { systemctl show -p MainPID --value "$1" 2>/dev/null || echo 0; }
# utime + stime; the fields counted from after the name in brackets, which may itself hold spaces.
ticks() { [[ -r $P/$1/stat ]] && sed 's/.*) //' "$P/$1/stat" | awk '{print $12 + $13}' || echo 0; }
# PostgreSQL is many processes that come and go; adding up the living ones lost a finished one's time and read
# NEGATIVE on xneelo (2026-10-12). Its systemd slice counts every process's time, finished ones included.
CG=${RSP_CGROUP:-/sys/fs/cgroup}
HZ=${RSP_HZ:-$(getconf CLK_TCK)}
pg_ticks() {
  local f=$CG/system.slice/system-postgresql.slice/cpu.stat t=0 x
  if [[ -r $f ]]; then awk -v hz="$HZ" '$1 == "usage_usec" {printf "%.0f", $2 * hz / 1000000}' "$f"; return; fi
  for x in $(pgrep -u postgres 2>/dev/null); do t=$((t + $(ticks "$x"))); done; echo $t
}
rss() { awk '/^VmRSS/ {printf "%.0f", $2 / 1024}' "$P/$1/status" 2>/dev/null || echo 0; }
threads() { awk '/^Threads/ {print $2}' "$P/$1/status" 2>/dev/null || echo 0; }
# All of user..steal, the idle part (idle + iowait), and steal alone.
cpu_line() { awk '/^cpu / {print $2+$3+$4+$5+$6+$7+$8+$9, $5+$6, $9; exit}' "$P/stat"; }
meminfo() { awk '/^(MemAvailable|SwapTotal|SwapFree):/ {v[$1] = $2} END {printf "%.0f %.0f", v["MemAvailable:"] / 1024, (v["SwapTotal:"] - v["SwapFree:"]) / 1024}' "$P/meminfo"; }
# Large counters run into the name ("ens3:123456789"), so the colon is made a space first.
net() { sed 's/:/ /' "$P/net/dev" | awk -v i="$iface" '$1 == i {print $2, $10}'; }
conns() { ss -Htn state "$1" "$2" 2>/dev/null | wc -l; }
# Caddy's connections through the platform's Unix socket (2026-10-13, BUGS #12): they use no ports at all. Each
# accepted one names the socket as its local address.
sock_conns() { ss -Hxn state connected 2>/dev/null | grep -c '/run/rsp/platform.sock'; }
as_pg() { runuser -u postgres -- psql -XAt -F, -d rsp -c "$1" 2>/dev/null; }
PG_SQL="SELECT count(*) FILTER (WHERE state = 'active'), count(*) FILTER (WHERE state LIKE 'idle%'), count(*) FILTER (WHERE wait_event_type = 'Lock'), greatest(0, coalesce(max(extract(epoch FROM now() - query_start) * 1000) FILTER (WHERE state = 'active'), 0))::bigint, (SELECT xact_commit + xact_rollback FROM pg_stat_database WHERE datname = 'rsp') FROM pg_stat_activity WHERE usename = 'rsp' AND pid <> pg_backend_pid()"
# grep -c says 0 itself when nothing matches (and fails), so no "|| echo 0" after it - that would say 0 twice.
slow_count() { if [[ -r $PGLOG ]]; then grep -c 'duration:' "$PGLOG"; else echo 0; fi; }
# The database's disk: the device under PostgreSQL's data, if it has one of its own (rsp-db).
dbdev=$(findmnt -no SOURCE /var/lib/postgresql 2>/dev/null | head -n1); dbdev=${dbdev##*/}
disk() { [[ -n $dbdev ]] && awk -v d="$dbdev" '$3 == d {print $6, $10, $13}' "$P/diskstats"; }

# ---- what this server is, once ----
read lo hi < "$P/sys/net/ipv4/ip_local_port_range"
ports=$((hi - lo + 1))
mem_total=$(awk '/^MemTotal:/ {printf "%.0f", $2 / 1024}' "$P/meminfo")
if [[ -f $ETC/systemd/system/remotesupport-platform.service.d/postgres.conf ]]; then
  db=PostgreSQL
  pool=$(grep -o 'Maximum Pool Size=[0-9]*' "$ETC/systemd/system/remotesupport-platform.service.d/postgres.conf" | cut -d= -f2)
  db="PostgreSQL (max_connections $(as_pg 'SHOW max_connections'), the platform's pool ${pool:-40})"
else
  db='SQLite - NOT like live'
fi
echo "# $(date '+%F %T') - $cores cores, $mem_total MB memory, network $iface; database: $db; ports Caddy may use to reach the platform: $lo-$hi ($ports)"
echo "# cpu columns are % of the WHOLE machine (100 = all $cores cores busy); upstream = Caddy's connections to the platform over TCP, ports_left = $ports minus those, socket_conns = through its Unix socket (no ports)"
echo "time,platform_cpu_pct,platform_mem_mb,platform_threads,caddy_cpu_pct,caddy_mem_mb,pg_cpu_pct,machine_cpu_pct,steal_pct,mem_free_mb,swap_used_mb,net_in_mbs,net_out_mbs,conns_443,upstream,upstream_tw,ports_left,socket_conns,pg_active,pg_idle,pg_lock_waits,pg_longest_ms,pg_commits_s,pg_slow,db_read_mbs,db_write_mbs,db_busy_pct"

reading() {
  p=$(pid_of remotesupport-platform); c=$(pid_of caddy)
  r_pt=$(ticks "$p"); r_ct=$(ticks "$c"); r_gt=$(pg_ticks)
  read r_tt r_it r_st <<<"$(cpu_line)"
  read r_rx r_tx <<<"$(net)"
  IFS=, read r_act r_idle r_lock r_long r_xact <<<"$(as_pg "$PG_SQL")"
  r_slow=$(slow_count)
  read r_dr r_dw r_dio <<<"$(disk)"
}
pct() { awk -v a="$1" -v s="$2" 'BEGIN {printf "%.0f", a / s * 100}'; }
rate() { awk -v b="$1" -v s="$INTERVAL" -v u="$2" 'BEGIN {printf "%.1f", b / s / u}'; }

reading
pt=$r_pt; ct=$r_ct; gt=$r_gt; tt=$r_tt; it=$r_it; st=$r_st; rx=$r_rx; tx=$r_tx; xact=${r_xact:-0}; slow=$r_slow
dr=${r_dr:-}; dw=${r_dw:-}; dio=${r_dio:-}
n=0
while sleep "$INTERVAL"; do
  reading
  span=$((r_tt - tt)); [[ $span -le 0 ]] && span=1
  mcpu=$(awk -v i=$((r_it - it)) -v s=$span 'BEGIN {printf "%.0f", (1 - i / s) * 100}')
  read free swap <<<"$(meminfo)"
  up=$(conns established '( dport = :8090 )'); tw=$(conns time-wait '( dport = :8090 )')
  if [[ -n ${r_act:-} ]]; then
    pg="$r_act,$r_idle,$r_lock,$r_long,$(awk -v x=$(( ${r_xact:-0} - xact )) -v s="$INTERVAL" 'BEGIN {printf "%.0f", x / s}'),$((r_slow - slow))"
  else
    pg="-,-,-,-,-,$((r_slow - slow))"
  fi
  if [[ -n ${r_dr:-} && -n $dr ]]; then
    dk="$(rate $((r_dr - dr)) 2048),$(rate $((r_dw - dw)) 2048),$(awk -v m=$((r_dio - dio)) -v s="$INTERVAL" 'BEGIN {printf "%.0f", m / (s * 1000) * 100}')"
  else
    dk="-,-,-"
  fi
  echo "$(date +%T),$(pct $((r_pt - pt)) $span),$(rss "$p"),$(threads "$p"),$(pct $((r_ct - ct)) $span),$(rss "$c"),$(pct $((r_gt - gt)) $span),$mcpu,$(pct $((r_st - st)) $span),$free,$swap,$(rate $((r_rx - rx)) 1048576),$(rate $((r_tx - tx)) 1048576),$(conns established '( sport = :443 )'),$up,$tw,$((ports - up - tw)),$(sock_conns),$pg,$dk"
  pt=$r_pt; ct=$r_ct; gt=$r_gt; tt=$r_tt; it=$r_it; st=$r_st; rx=$r_rx; tx=$r_tx; xact=${r_xact:-0}; slow=$r_slow
  dr=${r_dr:-}; dw=${r_dw:-}; dio=${r_dio:-}
  n=$((n + 1)); [[ $SAMPLES -gt 0 && $n -ge $SAMPLES ]] && break
done
