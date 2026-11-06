#!/usr/bin/env bash
# =====================================================================================
# The ONLY thing the office's backup key can do on the server (deploy/backup, 2026-10-08).
# Installed as /usr/local/sbin/rsp-backup-serve and forced on the key by authorized_keys
# (command=..., restrict, from= the office), so whatever the far end asks for, this runs instead:
#
#   list           every nightly backup: name, size in bytes, SHA-256
#   get NAME       that one backup's bytes, if NAME is exactly a nightly backup's name
#
# Anything else is refused. It runs as rspcopy, which can read the backups (still AES-256
# encrypted) and nothing of the platform's; the passphrase in /etc/rsp-backup-key is root's alone.
# =====================================================================================
set -euo pipefail
dir="${RSP_BACKUP_DIR:-/var/backups/rsp}"
ask="${SSH_ORIGINAL_COMMAND:-}"

case "$ask" in
  list)
    shopt -s nullglob
    cd "$dir"
    for f in rsp-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9].tar.gz.gpg; do
      printf '%s %s %s\n' "$f" "$(stat -c %s "$f")" "$(sha256sum "$f" | cut -d' ' -f1)"
    done
    ;;
  "get "*)
    name="${ask#get }"
    # Exactly a nightly backup's name: nothing with a slash, a dot-dot or anything else in it.
    if [[ ! "$name" =~ ^rsp-[0-9]{8}\.tar\.gz\.gpg$ ]]; then echo "refused: $name is not a backup's name" >&2; exit 2; fi
    if [[ ! -f "$dir/$name" ]]; then echo "no such backup: $name" >&2; exit 3; fi
    cat "$dir/$name"
    ;;
  *)
    echo "refused: this key may only 'list' or 'get <backup>'" >&2
    exit 2
    ;;
esac
