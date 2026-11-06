#!/usr/bin/env bash
# =====================================================================================
# switchcheck - what of deploy/postgres can be proved on this PC (2026-10-09, step 4 of HANDOVER 118).
#
#   bash tools/switchcheck/switchcheck.sh
#
# The switch itself needs a real Ubuntu server (systemd, apt, block devices): that is the rehearsal.
# Here: the scripts are plain ASCII (Windows PowerShell pipes them as ASCII) and parse, in bash and in
# Windows PowerShell 5.1; and above all the DISK CHOICE, which must never trust a device's name. It is
# run with stand-in lsblk and blkid answering for layouts the live server could have - the real one,
# the names swapped by a reboot, a disk that is not blank in each way there is, two disks alike, a run
# that stopped part-way, a volume never attached - and must pick right or refuse, saying why.
# SWITCHCHECK_SCRIPT runs a copy with a fault put back.
# =====================================================================================
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
script="${SWITCHCHECK_SCRIPT:-$repo/deploy/postgres/switch-to-postgres.sh}"
pass=0; fail=0
check() { local what="$1"; shift; if "$@"; then pass=$((pass + 1)); echo "  PASS  $what"; else fail=$((fail + 1)); echo "  FAIL  $what"; fi; }
say() { echo; echo "== $* =="; }
work="$(cygpath -u "${TEMP:-/tmp}")/rs-switchcheck-$$"; mkdir -p "$work"; trap 'rm -rf "$work"' EXIT

echo "switchcheck - deploy/postgres on this PC"
say "the scripts"
cd "$repo"   # so deploy/postgres/* names every script there
files=(deploy/postgres/*.sh deploy/postgres/*.ps1 deploy/rsp-backup.sh)
ascii() { ! LC_ALL=C grep -qP '[^\x00-\x7F]' "$1"; }
no_cr() { ! grep -q $'\r' "$1"; }
for f in "${files[@]}"; do check "$f is plain ASCII" ascii "$repo/$f"; done
for f in deploy/postgres/*.sh deploy/rsp-backup.sh; do
  check "$f has no Windows line endings" no_cr "$repo/$f"
  check "$f parses in bash" bash -n "$repo/$f"
done
for f in deploy/postgres/*.ps1; do
  # The path inside the command text, in single quotes: -Command joins everything after it into one script.
  errs="$(powershell -NoProfile -Command "\$e = \$null; [void][System.Management.Automation.Language.Parser]::ParseFile('$(cygpath -w "$repo/$f")', [ref]\$null, [ref]\$e); \$e.Count" | tr -d '\r')"
  check "$f parses in Windows PowerShell 5.1" test "$errs" = "0"
done

say "the platform has copy-to-postgres: the switch's own test, against real .dll files"
pat="$(grep -o "grep -qaP '[^']*'" "$script" | head -n1 | sed "s/^grep -qaP '//; s/'\$//")"
dll="$repo/src/RemoteSupport.Platform/bin/Release/net10.0/RemoteSupport.Platform.dll"
shared="$repo/src/RemoteSupport.Platform/bin/Release/net10.0/RemoteSupport.Shared.dll"
if [[ -f $dll ]]; then
  check "it finds the command in the platform built from this repository" bash -c "LC_ALL=C grep -qaP '$pat' '$dll'"
  check "...and not in a .dll without it" bash -c "! LC_ALL=C grep -qaP '$pat' '$shared'"
else echo "  (skipped: no Release build of the platform here - run pgcopycheck first)"; fi

# ---------------------------------------------------------------------------------------------
say "choosing the disks"
shims="$work/shims"; mkdir -p "$shims"
cat > "$shims/lsblk" <<'EOF'
#!/usr/bin/env bash
cat "$SWITCHCHECK_FIX/lsblk.txt"
EOF
cat > "$shims/blkid" <<'EOF'
#!/usr/bin/env bash
# blkid -p /dev/X: 0 when the fixture says a signature is there, 2 when nothing is.
[[ -f "$SWITCHCHECK_FIX/sig-${2#/dev/}" ]] && exit 0 || exit 2
EOF
chmod +x "$shims"/*

# A layout: lines of "name size type fstype pttype label mountpoint pkname" ("-" for empty).
layout() {
  local fix="$work/fix-$1"; shift
  rm -rf "$fix"; mkdir -p "$fix/dev"
  local name size type fs pt label mp pk
  for line in "$@"; do
    read -r name size type fs pt label mp pk <<< "$line"
    for v in fs pt label mp pk; do [[ ${!v} == - ]] && printf -v "$v" ''; done
    printf 'NAME="/dev/%s" SIZE="%s" TYPE="%s" FSTYPE="%s" PTTYPE="%s" LABEL="%s" MOUNTPOINT="%s" PKNAME="%s"\n' \
      "$name" "$size" "$type" "$fs" "$pt" "$label" "$mp" "${pk:+/dev/$pk}" >> "$fix/lsblk.txt"
    head -c 1048576 /dev/zero > "$fix/dev/$name"
  done
  echo "$fix"
}
choose() { # <fixture> [args]: ans = the answer alone (stdout), out = answer and notes, code = exit code
  local fix=$1; shift
  ans="$(PATH="$shims:$PATH" SWITCHCHECK_FIX="$fix" RSP_SWITCH_DEV="$fix/dev" bash "$script" --find-disks "$@" 2>"$work/err")"; code=$?
  out="$ans"$'\n'"$(cat "$work/err")"
}
G50=53687091200   # 50 GiB, as a panel's "50 GB" may be
G20=21474836480
SYS=(  "vda $G20 disk - gpt - - -"  "vda1 9663676416 part ext4 - cloudimg-rootfs / vda"  "vda15 111149056 part vfat - UEFI /boot/efi vda"  "loop0 40000000 loop squashfs - - /snap/core -" )

f="$(layout live "${SYS[@]}" "vdb $G50 disk - - - - -" "vdc $G20 disk - - - - -")"
choose "$f"
check "the live layout: rsp-files is vdb (50 GB), rsp-db is vdc (20 GB, not the 20 GB system disk)" \
  test $code -eq 0 -a "$ans" = $'rsp-files /dev/vdb blank\nrsp-db /dev/vdc blank'
check "...and it says it looked at the 20 GB system disk and left it alone" test -n "$(grep 'vda is 21 GB but not blank - it has a partition table, it has partitions, it is mounted' <<< "$out")"

f="$(layout swapped "${SYS[@]}" "vdb $G20 disk - - - - -" "vdc $G50 disk - - - - -")"
choose "$f"
check "names swapped by a reboot: chosen by size, so rsp-files is vdc and rsp-db is vdb" \
  test $code -eq 0 -a "$ans" = $'rsp-files /dev/vdc blank\nrsp-db /dev/vdb blank'

f="$(layout decimal "${SYS[@]}" "vdb 50000000000 disk - - - - -" "vdc 20000000000 disk - - - - -")"
choose "$f"
check "sizes in decimal gigabytes are recognised too" test $code -eq 0 -a "$ans" = $'rsp-files /dev/vdb blank\nrsp-db /dev/vdc blank'

f="$(layout hasfs "${SYS[@]}" "vdb $G50 disk ext4 - - - -" "vdc $G20 disk - - - - -")"
choose "$f"
check "a 50 GB disk with a filesystem on it is refused, and said" \
  test $code -ne 0 -a -n "$(grep 'no blank 50 GB disk for rsp-files' <<< "$out")" -a -n "$(grep 'has a ext4 filesystem' <<< "$out")"

f="$(layout parts "${SYS[@]}" "vdb $G50 disk - gpt - - -" "vdb1 $G20 part - - - - vdb" "vdc $G20 disk - - - - -")"
choose "$f"
check "a 50 GB disk with partitions is refused" test $code -ne 0 -a -n "$(grep 'it has partitions' <<< "$out")"

f="$(layout mounted "${SYS[@]}" "vdb $G50 disk - - - /mnt/x -" "vdc $G20 disk - - - - -")"
choose "$f"
check "a mounted 50 GB disk is refused" test $code -ne 0 -a -n "$(grep 'it is mounted' <<< "$out")"

f="$(layout dirty "${SYS[@]}" "vdb $G50 disk - - - - -" "vdc $G20 disk - - - - -")"
printf 'old data' | dd of="$f/dev/vdc" bs=1 seek=4096 conv=notrunc 2>/dev/null
choose "$f"
check "a 20 GB disk whose first megabyte holds something is refused" \
  test $code -ne 0 -a -n "$(grep 'first megabyte is not empty' <<< "$out")"

f="$(layout sig "${SYS[@]}" "vdb $G50 disk - - - - -" "vdc $G20 disk - - - - -")"
touch "$f/sig-vdb"
choose "$f"
check "a disk lsblk calls empty but blkid finds a signature on is refused" test $code -ne 0 -a -n "$(grep 'blkid finds something' <<< "$out")"

f="$(layout twins "${SYS[@]}" "vdb $G50 disk - - - - -" "vdc $G20 disk - - - - -" "vdd $G20 disk - - - - -")"
choose "$f"
check "two blank 20 GB disks: refused - it cannot tell which is rsp-db" test $code -ne 0 -a -n "$(grep 'cannot tell which is rsp-db' <<< "$out")"

f="$(layout ours "${SYS[@]}" "vdc $G50 disk ext4 - rsp-files - -" "vdb $G20 disk - - - - -")"
choose "$f"
check "an earlier run's rsp-files disk is used again, never formatted (and found by its label, not its name)" \
  test $code -eq 0 -a "$ans" = $'rsp-files /dev/vdc ours\nrsp-db /dev/vdb blank'

f="$(layout bothours "${SYS[@]}" "vdb $G50 disk ext4 - rsp-files /srv/rsp-files -" "vdc $G20 disk ext4 - rsp-db /var/lib/postgresql -")"
choose "$f"
check "both disks already ours and mounted (a re-run): both used as they are" test $code -eq 0 -a "$ans" = $'rsp-files /dev/vdb ours\nrsp-db /dev/vdc ours'

f="$(layout missing "${SYS[@]}" "vdb $G50 disk - - - - -")"
choose "$f"
check "no 20 GB volume attached: refused - the 20 GB SYSTEM disk is never taken for it" \
  test $code -ne 0 -a -n "$(grep 'no blank 20 GB disk for rsp-db' <<< "$out")" -a -n "$(grep 'vda is 21 GB but not blank' <<< "$out")"

f="$(layout live2 "${SYS[@]}" "vdb $G50 disk - - - - -" "vdc $G20 disk - - - - -")"
choose "$f" --files-gb 20 --db-gb 20
check "two disks of the same size asked for: refused" test $code -ne 0 -a -n "$(grep 'must differ in size' <<< "$out")"

f="$(layout rehearsal "${SYS[@]}" "vdb 10737418240 disk - - - - -" "vdc 6442450944 disk - - - - -")"
choose "$f" --files-gb 10 --db-gb 6
check "the rehearsal's small volumes (10 and 6 GB) are found the same way" test $code -eq 0 -a "$ans" = $'rsp-files /dev/vdb blank\nrsp-db /dev/vdc blank'

# ---------------------------------------------------------------------------------------------
say "/etc/fstab: the script's own two functions, pointed at a scratch file"
fst="$work/fstab"
cat > "$fst" <<'EOF'
LABEL=cloudimg-rootfs / ext4 discard,commit=30,errors=remount-ro 0 1
LABEL=UEFI /boot/efi vfat umask=0077 0 1
# UUID=old /srv/rsp-files ext4 defaults 0 2
EOF
sed -n '/^fstab_unset() {/,/^}/p; /^fstab_set() {/,/^}/p' "$script" | sed "s|/etc/fstab|$fst|g" > "$work/fstab-funcs.sh"
systemctl() { :; }
# shellcheck disable=SC1091
. "$work/fstab-funcs.sh"
fstab_set /srv/rsp-files "UUID=aaa /srv/rsp-files ext4 defaults,nofail 0 2"
fstab_set /opt/rsp/data "/srv/rsp-files/data /opt/rsp/data none bind,nofail,x-systemd.requires-mounts-for=/srv/rsp-files 0 0"
fstab_set /srv/rsp-files "UUID=bbb /srv/rsp-files ext4 defaults,nofail 0 2"
entries() { grep -v '^[[:space:]]*#' "$fst" | awk -v m="$1" '$2 == m' ; }
check "one entry for /srv/rsp-files after two runs, the newer one" test "$(entries /srv/rsp-files)" = "UUID=bbb /srv/rsp-files ext4 defaults,nofail 0 2"
check "...its note on the line above it, once" test "$(grep -c '^# rsp-switch: /srv/rsp-files$' "$fst")" = 1 -a "$(grep -A1 '^# rsp-switch: /srv/rsp-files$' "$fst" | tail -n1)" = "UUID=bbb /srv/rsp-files ext4 defaults,nofail 0 2"
check "no entry carries a comment at its end" test -z "$(grep -v '^[[:space:]]*#' "$fst" | grep '#')"
check "the root and EFI lines are untouched" test "$(head -n2 "$fst")" = $'LABEL=cloudimg-rootfs / ext4 discard,commit=30,errors=remount-ro 0 1\nLABEL=UEFI /boot/efi vfat umask=0077 0 1'
check "somebody's commented-out line for the same place is left alone" grep -qx '# UUID=old /srv/rsp-files ext4 defaults 0 2' "$fst"
check "the fstab as it was is kept, once (fstab.before-rsp-switch)" test -f "$fst.before-rsp-switch" -a "$(grep -c rsp-switch "$fst.before-rsp-switch")" = 0
fstab_unset /opt/rsp/data
check "taking the bind mount out removes its entry and its note, nothing else" test -z "$(entries /opt/rsp/data)" -a -z "$(grep 'rsp-switch: /opt/rsp/data' "$fst")" -a -n "$(entries /srv/rsp-files)"
check "every entry still has six fields" test -z "$(grep -v '^[[:space:]]*#' "$fst" | awk 'NF != 6')"

echo
echo "$pass of $((pass + fail)) checks passed."
[[ $fail -eq 0 ]]
