#!/usr/bin/env bash
# =====================================================================================
# loadstatscheck - what of deploy/loadtest can be proved on this PC (2026-10-12, step 5 of HANDOVER 118).
#
#   bash tools/loadstatscheck/loadstatscheck.sh
#
# The server watch (rsp-loadstats.sh) reads /proc, systemd, ss and PostgreSQL, none of which this PC has:
# it is run against a pretend /proc with two readings 5 s apart and stand-in commands, and every column
# must come out as worked by hand - processor including steal, memory and swap, the network (a counter
# run into its interface's name included), connections and the ports left, PostgreSQL's, the database
# disk. Then the same with no PostgreSQL (it must say SQLite and not fall over), and with two faults put
# back, which must change the answer. And the load-test scripts: plain ASCII, no Windows line endings,
# parse in bash and Windows PowerShell 5.1, and no double quote in what goes to ssh as an argument
# (Windows PowerShell mangles them).
# =====================================================================================
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
pass=0; fail=0
check() { local what="$1"; shift; if "$@"; then pass=$((pass + 1)); echo "  PASS  $what"; else fail=$((fail + 1)); echo "  FAIL  $what"; fi; }
say() { echo; echo "== $* =="; }
work="$(cygpath -u "${TEMP:-/tmp}")/rs-loadstatscheck-$$"; mkdir -p "$work"; trap 'rm -rf "$work"' EXIT

echo "loadstatscheck - deploy/loadtest on this PC"
say "the scripts"
cd "$repo"
ascii() { ! LC_ALL=C grep -qP '[^\x00-\x7F]' "$1"; }
no_cr() { ! grep -q $'\r' "$1"; }
for f in deploy/loadtest/*.ps1 deploy/loadtest/*.sh; do check "$f is plain ASCII" ascii "$repo/$f"; done
for f in deploy/loadtest/*.sh; do
  check "$f has no Windows line endings" no_cr "$repo/$f"
  check "$f parses in bash" bash -n "$repo/$f"
done
for f in deploy/loadtest/*.ps1; do
  errs="$(powershell -NoProfile -Command "\$e = \$null; [void][System.Management.Automation.Language.Parser]::ParseFile('$(cygpath -w "$repo/$f")', [ref]\$null, [ref]\$e); \$e.Count" | tr -d '\r')"
  check "$f parses in Windows PowerShell 5.1" test "$errs" = "0"
done
# What each script sends as ONE ssh argument is its $remote here-string: no double quote may be in it.
remote_block() { awk '/^\$remote = @"/ {on = 1; next} /^"@/ {on = 0} on' "$1"; }
no_dq() { ! remote_block "$1" | grep -q '"'; }
for f in deploy/loadtest/Prepare-LoadServer.ps1 deploy/loadtest/Prepare-LoadBox.ps1; do
  check "$f has a remote block at all" test -n "$(remote_block "$repo/$f")"
  check "$f sends ssh no double quote" no_dq "$repo/$f"
done
check "Prepare-LoadServer refuses to seed off PostgreSQL" grep -q "REFUSED: the platform is not on PostgreSQL" deploy/loadtest/Prepare-LoadServer.ps1
check "Prepare-LoadServer checks the seed went into PostgreSQL" grep -q "grep -q 'Database: PostgreSQL'" deploy/loadtest/Prepare-LoadServer.ps1
for f in deploy/loadtest/Prepare-LoadServer.ps1 deploy/loadtest/Restart-LoadServer.ps1 deploy/loadtest/Save-Addresses.ps1; do
  check "$f refuses the live address" grep -q "154.65.110.136" "$f"
done

say "the server watch, against a pretend server"
bin="$work/bin"; mkdir -p "$bin"
# Stand-ins. sleep is the clock: each "5 seconds" brings the next reading into the pretend /proc.
cat > "$bin/sleep" <<'EOF'
#!/usr/bin/env bash
n=$(cat "$W/tick" 2>/dev/null || echo 0); n=$((n + 1)); echo $n > "$W/tick"
[[ -d $W/s$n ]] && cp -r "$W/s$n/." "$W/proc/"
exit 0
EOF
cat > "$bin/systemctl" <<'EOF'
#!/usr/bin/env bash
case "${@: -1}" in remotesupport-platform) echo 111;; caddy) echo 222;; *) echo 0;; esac
EOF
cat > "$bin/ip" <<'EOF'
#!/usr/bin/env bash
echo 'default via 10.0.0.1 dev ens3 proto dhcp src 10.0.0.5 metric 100'
echo '10.0.0.0/24 dev ens3 proto kernel scope link src 10.0.0.5'
EOF
cat > "$bin/ss" <<'EOF'
#!/usr/bin/env bash
case "$*" in *-Hxn*) printf 'u_str 0 0 /run/rsp/platform.sock 11 * 12\nu_str 0 0 /run/rsp/platform.sock 13 * 14\nu_str 0 0 /run/rsp/platform.sock 15 * 16\nu_str 0 0 /run/rsp/platform.sock 17 * 18\nu_str 0 0 /run/systemd/journal/stdout 19 * 20\n';; *established*443*) printf 'a\nb\nc\n';; *established*8090*) printf 'a\nb\n';; *time-wait*8090*) printf 'a\n';; esac
EOF
printf '#!/usr/bin/env bash\ncat "$W/proc/pgrep.txt"\n' > "$bin/pgrep"
printf '#!/usr/bin/env bash\necho /dev/vdc\n' > "$bin/findmnt"
cat > "$bin/runuser" <<'EOF'
#!/usr/bin/env bash
while [[ $# -gt 0 && $1 != -- ]]; do shift; done; shift; exec "$@"
EOF
cat > "$bin/psql" <<'EOF'
#!/usr/bin/env bash
[[ -n ${NO_PSQL:-} ]] && exit 2
case "$*" in *'SHOW max_connections'*) echo 100;; *) cat "$W/proc/psql.txt";; esac
EOF
chmod +x "$bin"/*

# Two readings, worked by hand: over 1,400 jiffies the platform used 400 (29%), Caddy 140 (10%), PostgreSQL's
# two processes 112 (8%), the machine was idle 600 (57% busy) and stolen 200 (14%); 50 MB in and 25 MB out in 5 s;
# 2,500 ms of disk time (50%), 10 MB read and 50 MB written; 500 transactions (100 a second); 3 slow statements.
reading() { # dir platform-utime platform-stime caddy pg333 pg444 cpu-line net-line disk-line psql slow
  local d="$work/$1"; mkdir -p "$d/111" "$d/222" "$d/333" "$d/444" "$d/net" "$d/sys/net/ipv4"
  echo "111 (Remote Support) S 1 111 111 0 -1 4194560 100 0 0 0 $2 $3 0 0 20 0 30" > "$d/111/stat"
  echo "222 (caddy) S 1 222 222 0 -1 4194560 100 0 0 0 $4 $4 0 0 20 0 9" > "$d/222/stat"
  echo "333 (postgres) S 1 333 333 0 -1 4194560 100 0 0 0 $5 0 0 0 20 0 1" > "$d/333/stat"
  echo "444 (postgres) S 1 444 444 0 -1 4194560 100 0 0 0 $6 5 0 0 20 0 1" > "$d/444/stat"
  printf 'Name:\tRemoteSupport\nVmRSS:\t 2048000 kB\nThreads:\t123\n' > "$d/111/status"
  printf 'Name:\tcaddy\nVmRSS:\t  512000 kB\nThreads:\t9\n' > "$d/222/status"
  printf '%s\ncpu0 1 0 1 1 0 0 0 0 0 0\ncpu1 1 0 1 1 0 0 0 0 0 0\n' "$7" > "$d/stat"
  printf 'MemTotal:        8000000 kB\nMemAvailable:    4096000 kB\nSwapTotal:       1024000 kB\nSwapFree:         972800 kB\n' > "$d/meminfo"
  printf 'Inter-|   Receive\n face |bytes\n    lo: 500 5 0 0 0 0 0 0 500 5 0 0 0 0 0 0\n%s\n' "$8" > "$d/net/dev"
  printf '  252      16 vdb 1 0 8 0 1 0 8 0 0 1 0\n%s\n' "$9" > "$d/diskstats"
  printf '32768\t60999\n' > "$d/sys/net/ipv4/ip_local_port_range"
  echo "${10}" > "$d/psql.txt"
  : > "$d/pg.log"; for ((i = 0; i < ${11}; i++)); do echo 'LOG:  duration: 312.5 ms  statement: SELECT 1' >> "$d/pg.log"; done
}
reading s0 100 50 10 0 0 'cpu  1000 0 500 8000 100 0 0 0 0 0' '  ens3: 1000000 10 0 0 0 0 0 0 2000000 20 0 0 0 0 0 0' \
  ' 252      32 vdc 100 0 2048 0 50 0 4096 0 0 500 0' '7,33,1,250,1000' 2
reading s1 400 150 80 70 47 'cpu  1400 0 700 8600 100 0 0 200 0 0' '  ens3:53428800 10 0 0 0 0 0 0 28214400 20 0 0 0 0 0 0' \
  ' 252      32 vdc 300 0 22528 0 150 0 106496 0 0 3000 0' '8,32,0,1200,1500' 5
# PostgreSQL's processes come and go: process 444 has ENDED by the second reading, taking its time with it - the sum of
# the living read negative on xneelo. Its systemd slice's total (cpu.stat) keeps every process's time: 0.1 s, then
# 1.22 s = 112 jiffies at 100 Hz.
printf '333\n444\n' > "$work/s0/pgrep.txt"; printf '333\n' > "$work/s1/pgrep.txt"
for s in s0:100000 s1:1220000; do
  mkdir -p "$work/${s%%:*}/cg/system.slice/system-postgresql.slice"
  printf 'usage_usec %s\nuser_usec 1\nsystem_usec 1\n' "${s##*:}" > "$work/${s%%:*}/cg/system.slice/system-postgresql.slice/cpu.stat"
done
mkdir -p "$work/etc/systemd/system/remotesupport-platform.service.d"
echo 'Environment="RSP_ConnectionStrings__Postgres=Host=/var/run/postgresql;Database=rsp;Username=rsp"' \
  > "$work/etc/systemd/system/remotesupport-platform.service.d/postgres.conf"

watch() { # script [etc] -> its output, one reading
  rm -rf "$work/proc"; cp -r "$work/s0" "$work/proc"; echo 0 > "$work/tick"
  W="$work" PATH="$bin:$PATH" RSP_PROC="$work/proc" RSP_ETC="${2:-$work/etc}" RSP_PGLOG="$work/proc/pg.log" RSP_CGROUP="$work/proc/cg" RSP_HZ=100 \
    RSP_INTERVAL=5 RSP_SAMPLES=1 bash "$1" 2>&1
}
want="29,2000,123,10,500,8,57,14,4000,50,10.0,5.0,3,2,1,28229,4,8,32,0,1200,100,3,2.0,10.0,50"
out="$(watch "$repo/deploy/loadtest/rsp-loadstats.sh")"
echo "$out" | sed 's/^/        /'
row="$(echo "$out" | grep -E '^[0-9]{2}:[0-9]{2}:[0-9]{2},' | cut -d, -f2-)"
check "one reading, every column as worked by hand" test "$row" = "$want"
check "the header names every column (27)" test "$(echo "$out" | grep '^time,' | tr ',' '\n' | wc -l)" = "27"
check "it says it is on PostgreSQL, with the cap and the pool" grep -q "database: PostgreSQL (max_connections 100, the platform's pool 40)" <<< "$out"
check "it says the ports Caddy may use" grep -q 'ports Caddy may use to reach the platform: 32768-60999 (28232)' <<< "$out"
check "it found the network by the default route (ens3)" grep -q 'network ens3;' <<< "$out"
check "nothing written to stderr" bash -c "! grep -qiE 'error|syntax|unbound|command not found' <<< \"\$1\"" _ "$out"

say "the server watch on a server NOT on PostgreSQL"
mkdir -p "$work/etc-sqlite"
out2="$(NO_PSQL=1 watch "$repo/deploy/loadtest/rsp-loadstats.sh" "$work/etc-sqlite")"
check "it says SQLite and NOT like live" grep -q 'database: SQLite - NOT like live' <<< "$out2"
row2="$(echo "$out2" | grep -E '^[0-9]{2}:[0-9]{2}:[0-9]{2},' | cut -d, -f2-)"
check "PostgreSQL's columns are '-', the rest still read" test "$row2" = "29,2000,123,10,500,8,57,14,4000,50,10.0,5.0,3,2,1,28229,4,-,-,-,-,-,3,2.0,10.0,50"

say "faults put back - each must change the answer"
fault() { # what sed-expression
  sed "$2" "$repo/deploy/loadtest/rsp-loadstats.sh" > "$work/faulty.sh"
  check "the edit took ($1)" bash -c "! cmp -s '$work/faulty.sh' '$repo/deploy/loadtest/rsp-loadstats.sh'"
  local r; r="$(watch "$work/faulty.sh" | grep -E '^[0-9]{2}:[0-9]{2}:[0-9]{2},' | cut -d, -f2-)"
  check "caught: $1" test "$r" != "$want"
}
fault "the old network reading, which misses a counter run into its name" \
  "s/^net() .*/net() { awk -v i=\"\$iface:\" '\$1 == i {print \$2, \$10}' \"\$P\/net\/dev\"; }/"
fault "steal left out of the machine's total" 's/\$2+\$3+\$4+\$5+\$6+\$7+\$8+\$9, \$5+\$6, \$9/$2+$3+$4+$5+$6+$7+$8, $5+$6, $9/'
fault "PostgreSQL's processor as the sum of its living processes (read negative on xneelo)" 's/^  if \[\[ -r \$f \]\]; then awk.*$/  :/'

echo
echo "loadstatscheck: $pass passed, $fail failed"
[[ $fail -eq 0 ]]
