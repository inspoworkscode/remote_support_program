#!/usr/bin/env bash
# =====================================================================================
# Run ON THE REHEARSAL SERVER by Copy-LiveToRehearsal.ps1 (2026-10-09), with a first line OTP='...'
# put in front of it. Makes a freshly deployed throwaway server a copy of live, on SQLite, as live is
# before the switch:
#
#   1. Refuses the live server (its public address), and a server already switched.
#   2. Blocks every outgoing connection except DNS, time and the web (apt, the TLS certificate) - and
#      PROVES it by trying a real mail server. The copy holds real companies' mail settings: unblocked,
#      this server would read their mailboxes and send their customers auto-replies, notices and
#      reports. This happens BEFORE the platform ever starts on the copy.
#   3. Decrypts /tmp/rsp-for-rehearsal.gpg with the one-time passphrase and puts it in /opt/rsp/data
#      (the deploy's own empty data/ is kept aside), then starts the platform on it.
# Delete this server when the rehearsal is over: it holds live data and live's key ring.
# =====================================================================================
set -Eeuo pipefail
[[ $EUID -eq 0 ]] || { echo "run with sudo" >&2; exit 1; }
[[ -n ${OTP:-} ]] || { echo "no one-time passphrase" >&2; exit 1; }
die() { echo >&2; echo "!! STOPPED: $*" >&2; exit 1; }
say() { echo; echo "== $* =="; }
APP_DIR=/opt/rsp; DATA=$APP_DIR/data; SERVICE=remotesupport-platform; IN=/tmp/rsp-for-rehearsal.gpg
STAMP="$(date +%Y%m%d-%H%M%S)"

say "1/3 is this the rehearsal server?"
url="$(grep -o '"PublicUrl": *"[^"]*"' "$APP_DIR/appsettings.live.json" 2>/dev/null | sed 's/.*"\(https[^"]*\)"/\1/' || true)"
[[ -n $url ]] || die "no platform deployed here (no $APP_DIR/appsettings.live.json) - run Publish-Platform.ps1 to it first"
[[ $url != https://theremotesupportprogram.com && $url != https://www.theremotesupportprogram.com ]] || die "this is the LIVE server ($url)"
[[ ! -f /etc/systemd/system/$SERVICE.service.d/postgres.conf ]] || die "this server is already on PostgreSQL - a rehearsal starts from a fresh server"
[[ -s $IN ]] || die "no $IN - the copy from live did not arrive"
echo "   $url - yes"

say "2/3 outgoing connections blocked, except DNS, time and the web"
ufw default deny outgoing >/dev/null
ufw allow out 53 >/dev/null
ufw allow out 123/udp >/dev/null
ufw allow out 80/tcp >/dev/null
ufw allow out 443/tcp >/dev/null
ufw --force enable >/dev/null
if timeout 8 bash -c '</dev/tcp/smtp.gmail.com/587' 2>/dev/null; then die "a mail server could still be reached on 587 - NOT putting live data here"; fi
if timeout 8 bash -c '</dev/tcp/imap.gmail.com/993' 2>/dev/null; then die "a mailbox could still be reached on 993 - NOT putting live data here"; fi
timeout 8 bash -c '</dev/tcp/github.com/443' 2>/dev/null || die "the web (443) is blocked too - apt and the certificate need it"
echo "   mail (587, 993) cannot be reached; the web (443) can"

say "3/3 live's data in place of this server's"
stage="$(mktemp -d /root/rsp-restore-XXXX)"; trap 'rm -rf "$stage"; rm -f "$IN"' EXIT
gpg --batch --quiet -d --passphrase-fd 3 "$IN" 3<<< "$OTP" | tar xz -C "$stage" || die "could not open the copy (wrong one-time passphrase?)"
[[ ! -f $stage/platform.pgdump ]] || die "this backup is from a server already on PostgreSQL - the rehearsal is of the switch FROM SQLite"
[[ -f $stage/platform.db ]] || die "the backup holds no platform.db"
systemctl stop "$SERVICE"
mv "$DATA" "/root/data-as-deployed-$STAMP"
mkdir "$DATA"
cp -a "$stage/." "$DATA/"
chown -R rsp:rsp "$DATA"; chmod 700 "$DATA"
since="$(date '+%Y-%m-%d %H:%M:%S')"
systemctl start "$SERVICE"
for i in $(seq 1 60); do [[ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8090/health || true)" == 200 ]] && break; sleep 1; done
[[ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8090/health || true)" == 200 ]] || die "the platform did not start on the copy (journalctl -u $SERVICE -n 50)"
echo "   $(journalctl -u "$SERVICE" --since "$since" --no-pager -o cat | grep -m1 'DB: ' || echo 'no start-up line')"
for t in Tenants Technicians Devices Clients Tickets TicketNotes; do
  printf '   %-12s %6s\n' "$t" "$(sqlite3 -readonly "$DATA/platform.db" "SELECT COUNT(*) FROM \"$t\"")"
done
echo
echo "The rehearsal server is a copy of live, on SQLite. Outgoing mail is blocked. Delete the server afterwards."
