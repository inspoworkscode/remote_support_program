#!/usr/bin/env bash
# =====================================================================================
# Run ON the live server, by Enable-OffsiteBackup.ps1 (deploy/backup, 2026-10-08):
#
#   sudo bash enable-offsite.sh <office-ip> <public-key-file> <serve-script>
#
# Makes the account rspcopy, whose one key works only from the office's address and can only
# run rsp-backup-serve (list / get a nightly backup). Lets it read /var/backups/rsp as the group
# rspcopy. Safe to run again: a new key replaces the old one.
# =====================================================================================
set -euo pipefail
office="${1:?office address}"; pub="${2:?public key file}"; serve="${3:?serve script}"
[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
[[ "$office" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || { echo "Not an IPv4 address: $office" >&2; exit 1; }
key="$(tr -d '\r' < "$pub" | head -n1)"
[[ "$key" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+(\ .*)?$ ]] || { echo "Not an ed25519 public key: $pub" >&2; exit 1; }

getent group rspcopy >/dev/null || groupadd --system rspcopy
id -u rspcopy >/dev/null 2>&1 || useradd --system --gid rspcopy --create-home --home-dir /var/lib/rspcopy --shell /bin/sh rspcopy
sed 's/\r$//' "$serve" > /usr/local/sbin/rsp-backup-serve
chmod 755 /usr/local/sbin/rsp-backup-serve

install -d -m 700 -o rspcopy -g rspcopy /var/lib/rspcopy/.ssh
# restrict: no shell, no forwarding, no terminal. command=: whatever is asked, rsp-backup-serve runs.
# from=: only the office's address.
printf 'from="%s",command="/usr/local/sbin/rsp-backup-serve",restrict %s\n' "$office" "$key" > /var/lib/rspcopy/.ssh/authorized_keys
chown rspcopy:rspcopy /var/lib/rspcopy/.ssh/authorized_keys
chmod 600 /var/lib/rspcopy/.ssh/authorized_keys

# The backups, readable by the group (setgid, so new ones join it). Still encrypted; the key stays root's.
mkdir -p /var/backups/rsp
chgrp rspcopy /var/backups/rsp; chmod 2750 /var/backups/rsp
shopt -s nullglob
for f in /var/backups/rsp/*.tar.gz.gpg; do chgrp rspcopy "$f"; chmod 640 "$f"; done

# The nightly script was written by go-live.sh before it knew about rspcopy: bring it up to date the
# same way go-live.sh now writes it, so tonight's backup stays readable too (the next deploy rewrites it).
if [[ -f /usr/local/sbin/rsp-backup ]] && ! grep -q rspcopy /usr/local/sbin/rsp-backup; then
  sed -i 's|^out=/var/backups/rsp; mkdir -p "\$out"; chmod 700 "\$out"$|out=/var/backups/rsp; mkdir -p "$out"\nif getent group rspcopy >/dev/null; then chgrp rspcopy "$out"; chmod 2750 "$out"; else chmod 700 "$out"; fi|' /usr/local/sbin/rsp-backup
  sed -i '/--symmetric --cipher-algo AES256/a if getent group rspcopy >/dev/null; then chgrp rspcopy "$out"/rsp-*.tar.gz.gpg; chmod 640 "$out"/rsp-*.tar.gz.gpg; fi' /usr/local/sbin/rsp-backup
  grep -q 'chmod 2750' /usr/local/sbin/rsp-backup || { echo "   !! could not update /usr/local/sbin/rsp-backup - the next deploy will" >&2; }
fi

# An sshd that names its users would refuse rspcopy without a word.
if sshd -T 2>/dev/null | grep -qiE '^(allowusers|allowgroups) '; then
  echo "   !! sshd has AllowUsers/AllowGroups set - add rspcopy to it, or the office cannot connect." >&2
fi

echo "   rspcopy can see:"
sudo -u rspcopy env SSH_ORIGINAL_COMMAND=list /usr/local/sbin/rsp-backup-serve | tail -3 | sed 's/^/     /'
echo "FINGERPRINT $(ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub | awk '{print $2}')"
