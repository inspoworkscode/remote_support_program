#!/usr/bin/env bash
# =====================================================================================
# Run ON THE LIVE SERVER by Copy-LiveToRehearsal.ps1 (2026-10-09), with a first line OTP='...' put in
# front of it by that script. Changes nothing on the live server except one file in /tmp:
#
#   The newest nightly backup is decrypted with the live passphrase and encrypted again with the
#   one-time passphrase OTP, into /tmp/rsp-for-rehearsal.gpg, which is removed by itself in 20 minutes.
#
# So the live backup passphrase never leaves this server, and what travels through the laptop is
# useless without the one-time passphrase, which the laptop only ever holds in memory.
# =====================================================================================
set -Eeuo pipefail
[[ $EUID -eq 0 ]] || { echo "run with sudo" >&2; exit 1; }
[[ -n ${OTP:-} ]] || { echo "no one-time passphrase" >&2; exit 1; }
src="$(ls -1t /var/backups/rsp/rsp-[0-9]*.tar.gz.gpg 2>/dev/null | head -n1 || true)"
[[ -n $src ]] || { echo "no nightly backup in /var/backups/rsp" >&2; exit 1; }
out=/tmp/rsp-for-rehearsal.gpg
rm -f "$out"
gpg --batch --quiet -d --passphrase-file /etc/rsp-backup-key "$src" \
  | gpg --batch --yes --symmetric --cipher-algo AES256 --passphrase-fd 3 -o "$out" 3<<< "$OTP"
chown "${SUDO_USER:-ubuntu}" "$out"; chmod 600 "$out"
systemd-run --quiet --on-active=20min /bin/rm -f "$out" || true
echo "   $(basename "$src") ($(stat -c %y "$src" | cut -d. -f1), $(( $(stat -c %s "$src") / 1024 )) KB) - ready to copy for 20 minutes"
