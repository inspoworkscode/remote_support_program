#!/usr/bin/env bash
# =====================================================================================
# The Remote Support Program — put the platform live on a fresh Ubuntu server.
#
#   sudo ./go-live.sh theremotesupportprogram.com clint@anywhereit.co.za
#
# Run it from the folder this script came in (the contents of dist/platform-linux).
# It is safe to run again: data/, the database and the owner key are preserved.
#
# What it does, in order:
#   1. Installs Caddy (TLS, automatic certificate) and Chrome (health-check PDFs).
#   2. Installs the platform to /opt/rsp as the systemd service remotesupport-platform,
#      bound to 127.0.0.1:8090 and the socket /run/rsp/platform.sock, so nothing but the proxy can reach it.
#   3. Generates the owner key once and keeps it across re-runs.
#   4. Points Caddy at it on 443 for the domain and www.
#   5. Firewalls everything except 22, 80 and 443.
#   6. Hardens the server (2026-10-01, HANDOVER §72): security updates install themselves,
#      repeated SSH failures are banned, SSH passwords are switched off IF a key is already in
#      place (never otherwise — that would lock you out), the program cannot rewrite itself,
#      only it can read its data and key ring, and a nightly AES-256 encrypted backup is made.
# =====================================================================================
set -euo pipefail

DOMAIN="${1:-}"
LE_EMAIL="${2:-}"
# Whose website chat answers on OUR site once the real site is shown (2026-10-27, Services/SiteChat.cs): a company's
# short code, anywhereit. Optional; Publish-Platform.ps1 passes its -SiteChat. Unused while the holding page is in front.
SITE_CHAT="${3:-}"
APP_DIR="/opt/rsp"
SERVICE="remotesupport-platform"
USER_NAME="rsp"
PORT=8090
# Caddy reaches the platform through this socket, never the port (2026-10-13, BUGS #12): over TCP each proxied
# WebSocket held one of Linux's ~28,000 local ports, and at ~27 companies every request was answered 502. The port
# stays for the checks run on the server itself (this script, the switch, the status report, the backup).
SOCK=/run/rsp/platform.sock
OVERRIDE_DIR="/etc/systemd/system/$SERVICE.service.d"
# PostgreSQL (2026-10-09, step 4 of HANDOVER §118). This drop-in is written by switch-to-postgres.sh and
# removed by its way back - NEVER by this script, so a deploy cannot undo the switch. Its being there is
# THE sign that the platform runs on PostgreSQL; the connection string in it is carried into
# appsettings.live.json below for the command line, which never sees systemd's environment.
PG_DROPIN="$OVERRIDE_DIR/postgres.conf"
here="$(cd "$(dirname "$0")" && pwd)"

[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
[[ -n "$DOMAIN" ]] || { echo "Usage: sudo ./go-live.sh <domain> <email-for-certificate>" >&2; exit 1; }
[[ -n "$LE_EMAIL" ]] || { echo "Usage: sudo ./go-live.sh <domain> <email-for-certificate>" >&2; exit 1; }
[[ -z "$SITE_CHAT" || "$SITE_CHAT" =~ ^[a-z0-9-]{1,40}$ ]] || { echo "The site chat's company code '$SITE_CHAT' is not a short code (a-z, 0-9, -)." >&2; exit 1; }
[[ -f "$here/RemoteSupport.Platform" ]] || { echo "RemoteSupport.Platform (linux-x64) must be next to this script." >&2; exit 1; }
[[ -f "$here/rsp-backup.sh" ]] || { echo "rsp-backup.sh must be next to this script (build.ps1 -Platform puts it there)." >&2; exit 1; }
PG_CONN=""
if [[ -f "$PG_DROPIN" ]]; then
  PG_CONN="$(grep -o 'RSP_ConnectionStrings__Postgres=[^"]*' "$PG_DROPIN" | head -n1 | cut -d= -f2-)"
  [[ -n "$PG_CONN" ]] || { echo "$PG_DROPIN exists but holds no connection string - fix or remove it first." >&2; exit 1; }
fi

echo "== 1/7 packages =="
export DEBIAN_FRONTEND=noninteractive
# Caddy's apt repository is NOT used (2026-10-01). On the day we went live, Cloudsmith began signing
# it with a key that expired in 2024 (caddyserver/dist#140), and one apt source that fails its
# signature breaks EVERY apt-get update on the machine — security updates included. An earlier run
# of this script may have added it, so it is taken out first, before anything asks apt for updates.
rm -f /etc/apt/sources.list.d/caddy-stable.list /usr/share/keyrings/caddy-stable-archive-keyring.gpg
apt-get update -qq
apt-get install -y -qq curl ca-certificates gnupg apt-transport-https ufw \
  sqlite3 unattended-upgrades fail2ban >/dev/null

# Caddy from its own GitHub release instead: the very same .deb (service, user, config) that the
# repository carries, checked against the SHA-512 sums Caddy publishes beside it, and refused if it
# does not match. Pinned, so every server gets what was tested; raise the number to upgrade — a
# re-run installs it when the running Caddy is a different version.
CADDY_VERSION="2.11.4"
if ! caddy version 2>/dev/null | grep -q "^v$CADDY_VERSION "; then
  rel="https://github.com/caddyserver/caddy/releases/download/v$CADDY_VERSION"
  deb="caddy_${CADDY_VERSION}_linux_amd64.deb"
  tmp="$(mktemp -d)"
  curl -fsSL -o "$tmp/$deb" "$rel/$deb"
  curl -fsSL -o "$tmp/sums.txt" "$rel/caddy_${CADDY_VERSION}_checksums.txt"
  want="$(awk -v f="$deb" '$2 == f {print $1}' "$tmp/sums.txt")"
  got="$(sha512sum "$tmp/$deb" | awk '{print $1}')"
  if [[ -z "$want" || "$want" != "$got" ]]; then
    echo "Caddy $CADDY_VERSION does not match its published checksum — NOT installing it." >&2; exit 1
  fi
  apt-get install -y -qq "$tmp/$deb" >/dev/null
  rm -rf "$tmp"
  echo "   Caddy $(caddy version | cut -d' ' -f1) installed from GitHub (checksum verified)"
fi

# Chrome, not Chromium: on current Ubuntu the chromium package is a snap, and a snap started by a
# system service with no home directory is a fight we do not need. The PDF engine only ever runs
# headless with --no-sandbox into a temp profile. Health-check reports still send as HTML if this
# step fails, so it is not fatal.
if [[ ! -x /usr/bin/google-chrome ]]; then
  curl -fsSL https://dl.google.com/linux/linux_signing_key.pub \
    | gpg --dearmor -o /usr/share/keyrings/google-chrome-keyring.gpg || true
  echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome-keyring.gpg] http://dl.google.com/linux/chrome/deb/ stable main" \
    > /etc/apt/sources.list.d/google-chrome.list
  apt-get update -qq || true
  apt-get install -y -qq google-chrome-stable >/dev/null 2>&1 \
    || echo "   (Chrome would not install — reports will be sent as HTML instead of PDF)"
fi

echo "== 2/7 application =="
id -u "$USER_NAME" >/dev/null 2>&1 \
  || useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"

# An update never touches data/ (the database, logos, photos, recordings, the key ring): the package
# carries none, and these two lines make sure of it (2026-10-01, once real companies' data was on
# here). First, if a package ever DID carry a data folder — a build that swept one up — it is
# dropped here rather than copied over the live one. Second, a backup is taken BEFORE anything
# changes, so an update that goes wrong — a migration included — can always be undone.
if [[ -d "$here/data" ]]; then echo "   The package carried a data/ folder — IGNORED, the live data is kept."; rm -rf "$here/data"; fi

# The holding page (2026-10-03). Publish-Platform.ps1 packs it as holding/index.html unless it was
# given -RealSite; it is taken out of the package here so it never lands among the program's files.
HOLDING=no
if [[ -f "$here/holding/index.html" ]]; then
  mkdir -p /var/www/landing
  install -m 644 "$here/holding/index.html" /var/www/landing/index.html
  HOLDING=yes
fi
rm -rf "$here/holding" "$APP_DIR/holding"
if [[ -x /usr/local/sbin/rsp-backup ]] && [[ -f "$APP_DIR/data/platform.db" || -n "$PG_CONN" ]]; then
  /usr/local/sbin/rsp-backup \
    && cp "/var/backups/rsp/rsp-$(date +%Y%m%d).tar.gz.gpg" "/var/backups/rsp/before-update-$(date +%Y%m%d-%H%M%S).tar.gz.gpg" \
    && echo "   Backed up before updating: /var/backups/rsp/before-update-$(date +%Y%m%d)-….tar.gz.gpg" \
    || { echo "   !! The backup before the update FAILED — stopping, nothing has been changed." >&2; exit 1; }
fi

systemctl stop "$SERVICE" 2>/dev/null || true
mkdir -p "$APP_DIR"
cp -r "$here/." "$APP_DIR/"
chmod +x "$APP_DIR/RemoteSupport.Platform"
mkdir -p "$APP_DIR/data"
# The program belongs to root and the service may only READ it; the service owns data/ alone
# (database, logos, photos, recordings, and the key ring that decrypts every stored password and
# authenticator secret). A fault in the program can then not rewrite the program, and no other
# account on the machine can read the data.
chown -R root:root "$APP_DIR"
chmod -R go-w "$APP_DIR"
chown -R "$USER_NAME":"$USER_NAME" "$APP_DIR/data"
chmod 700 "$APP_DIR/data"
install -m 644 "$here/remotesupport-platform.service" "/etc/systemd/system/$SERVICE.service"

echo "== 3/7 owner key and public address =="
# Generated once. A re-deploy must not change it, or the owner portal password changes under you.
KEY_FILE="/etc/rsp-owner-key"
if [[ ! -f "$KEY_FILE" ]]; then
  head -c 32 /dev/urandom | base64 | tr -d '=+/' | cut -c1-40 > "$KEY_FILE"
  chmod 600 "$KEY_FILE"
fi
ADMIN_KEY="$(cat "$KEY_FILE")"
# The server key (2026-10-13, HANDOVER §133): how the control dashboard on ANOTHER server reaches this one
# (/api/control). Made once, like the owner key; pasted into that dashboard's Servers page when this server is added.
SERVER_KEY_FILE="/etc/rsp-server-key"
if [[ ! -f "$SERVER_KEY_FILE" ]]; then
  head -c 32 /dev/urandom | od -An -tx1 | tr -dc '0-9a-f' > "$SERVER_KEY_FILE"
  chmod 600 "$SERVER_KEY_FILE"
fi
SERVER_KEY="$(cat "$SERVER_KEY_FILE")"
# What the dashboard calls this server ("za1", "za2"): RSP_SERVER_NAME=za2 before go-live. Unset: its domain.
SERVER_NAME="${RSP_SERVER_NAME:-}"
# Signups and our site's chat bubble follow the holding page (2026-10-27): with it in front (the default) signups stay
# closed and there is no bubble; with -RealSite (no holding page in the package) signups open and the bubble is the
# -SiteChat company's. Written on EVERY deploy, so the next deploy without -RealSite closes them again with the page.
if [[ "$HOLDING" == "yes" ]]; then
  SIGNUPS=false; CHAT_SLUG=""
  echo "   Signups closed; no chat bubble on the site (holding page in front)."
else
  SIGNUPS=true; CHAT_SLUG="$SITE_CHAT"
  echo "   Signups OPEN (-RealSite)."
  if [[ -n "$CHAT_SLUG" ]]; then echo "   Chat bubble on the site, answered by: $CHAT_SLUG"; else echo "   No chat bubble on the site (no -SiteChat given)."; fi
fi

mkdir -p "$OVERRIDE_DIR"
cat > "$OVERRIDE_DIR/override.conf" <<EOF
[Service]
# Everything here wins over the values shipped in the unit file and appsettings.json.
Environment=RSP_Platform__AdminKey=$ADMIN_KEY
Environment=RSP_Platform__ServerKey=$SERVER_KEY
$([[ -n "$SERVER_NAME" ]] && echo "Environment=RSP_Platform__ServerName=$SERVER_NAME")
Environment=RSP_Platform__PublicUrl=https://$DOMAIN
# Signups open only with the real site (-RealSite); until then tenants are created from the CLI or Owner pages.
Environment=RSP_Platform__SignupsEnabled=$SIGNUPS
$([[ -n "$CHAT_SLUG" ]] && echo "Environment=RSP_Platform__SiteChatSlug=$CHAT_SLUG")
# Only Caddy may talk to it: through the socket (group rsp, which caddy joins below), and the port for local checks.
Environment=RSP_Urls=http://127.0.0.1:$PORT;http://unix:$SOCK
Environment=RSP_Platform__PdfEngine=/usr/bin/google-chrome
EOF

# The same address for the command line, which never sees systemd's environment: an `invite`
# typed on this server must mail a link to https://$DOMAIN, not the office LAN address that
# appsettings.json ships with. Nothing secret goes in this file - nor in the PostgreSQL connection
# string, which has no password: the account rsp signs in over the local socket as itself (peer).
if [[ -n "$PG_CONN" ]]; then
  DB_JSON=", \"ConnectionStrings\": { \"Postgres\": \"$PG_CONN\" }"
  echo "   Database: PostgreSQL ($PG_DROPIN)"
else
  DB_JSON=""
  echo "   Database: SQLite ($APP_DIR/data/platform.db)"
fi
cat > "$APP_DIR/appsettings.live.json" <<EOF
{ "Platform": { "PublicUrl": "https://$DOMAIN", "SignupsEnabled": $SIGNUPS }$DB_JSON }
EOF
chmod 644 "$APP_DIR/appsettings.live.json"

systemctl daemon-reload
# Enabled but NOT started yet (2026-10-26). Caddy restarts in step 4, and starting the platform here cut every machine
# and console off twice, two seconds apart: once when the platform went down, again when Caddy did. Kept down until
# Caddy is back, a deploy cuts them off once - one reconnect rush, not two. Should anything before that start fail,
# the trap starts it anyway: a failed step must never leave the platform down.
systemctl enable "$SERVICE"
trap 'systemctl start "$SERVICE" || true' EXIT

echo "== 4/7 TLS on 443 =="
# With the holding page, only the platform's public sales pages are swapped for it — every page in
# Services/SitePages.cs (tools/sitecheck fails if one is missing below), and sign-up. Everything else (sign-in, portal, owner, /get, /download, /help,
# /tech, web chat, the API and the apps' WebSockets) still reaches the platform. Caddy's path
# matching ignores case, as ASP.NET's does, so /Pricing cannot slip past.
if [[ "$HOLDING" == "yes" ]]; then
  echo "   The front of the site is the holding page (publish with -RealSite to show the real one)."
  # A browser an admin has given the preview (/owner/preview, 2026-10-13) carries rsp_preview and is let through to the
  # real pages. Caddy only sees that the cookie is there; the platform checks it is one it made before signup opens.
  SITE_BODY="    @holding {
        path / /index /index.html /features /features/ /pricing /pricing/ /support /support/ /signup /signup/ /remote-support /remote-support/ /ticketing /ticketing/ /health-checks /health-checks/ /knowledge-base /knowledge-base/ /website-chat /website-chat/ /security /security/ /about /about/ /privacy /privacy/ /remote-workers /remote-workers/
        not header Cookie *rsp_preview=*
    }
    handle @holding {
        # Caddy serves this page itself, so it gets none of the platform's headers unless they are written
        # here (the pentest's F3, 2026-10-23). The same five as the platform's pages; only frame-ancestors in
        # the policy, because the page has an inline script.
        header {
            Strict-Transport-Security \"max-age=31536000\"
            X-Content-Type-Options \"nosniff\"
            X-Frame-Options \"DENY\"
            Referrer-Policy \"strict-origin-when-cross-origin\"
            Content-Security-Policy \"frame-ancestors 'none'\"
        }
        root * /var/www/landing
        rewrite * /index.html
        file_server
    }
    handle {
        reverse_proxy unix/$SOCK
    }"
else
  echo "   The front of the site is the platform's own home page."
  SITE_BODY="    reverse_proxy unix/$SOCK"
fi
# Written beside the live one and checked first: a configuration Caddy refuses must never replace
# one that works.
cat > /etc/caddy/Caddyfile.new <<EOF
{
    email $LE_EMAIL
}

$DOMAIN, www.$DOMAIN {
    encode zstd gzip
    # reverse_proxy upgrades WebSockets on its own — that is the whole remote-control protocol.
$SITE_BODY
}
EOF
caddy validate --config /etc/caddy/Caddyfile.new --adapter caddyfile >/dev/null \
  || { echo "   !! Caddy refused the new configuration (/etc/caddy/Caddyfile.new); the old one is still in place." >&2; exit 1; }
mv /etc/caddy/Caddyfile.new /etc/caddy/Caddyfile
# Caddy may open the platform's socket (owner rsp, group rsp, 0660): a member of the group. Its supplementary groups
# are read when it starts, which is the restart below.
usermod -aG "$USER_NAME" caddy
systemctl enable caddy >/dev/null 2>&1 || true
# Every deploy restarts Caddy, so whatever memory it grew holding connections starts fresh each time (the load
# test's restart grew it 1.8 GB and it kept it - but that restart was the platform alone).
systemctl restart caddy
# The platform last, once Caddy is up (see the end of step 3).
systemctl start "$SERVICE"
trap - EXIT

echo "== 5/7 firewall =="
ufw allow 22/tcp    >/dev/null 2>&1 || true
ufw allow 80/tcp    >/dev/null 2>&1 || true
ufw allow 443/tcp   >/dev/null 2>&1 || true
ufw delete allow "$PORT"/tcp >/dev/null 2>&1 || true
yes | ufw enable    >/dev/null 2>&1 || true

echo "== 6/7 hardening the server =="
# Security updates install themselves, every day, without anybody remembering to.
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
systemctl enable --now unattended-upgrades >/dev/null 2>&1 || true

# Somebody guessing SSH passwords is banned after a few tries (Ubuntu's fail2ban guards sshd by default).
systemctl enable --now fail2ban >/dev/null 2>&1 || true

# SSH by key only — but ONLY if a key is already there. Switching passwords off on a server whose
# only way in is a password is how you lock yourself out of it, so without a key this says so and
# changes nothing. Put your key in first (ssh-copy-id), then run this script again.
HAVE_KEY=no
for f in /root/.ssh/authorized_keys /home/*/.ssh/authorized_keys; do [[ -s "$f" ]] && HAVE_KEY=yes; done
if [[ "$HAVE_KEY" == yes ]]; then
  cat > /etc/ssh/sshd_config.d/50-rsp-hardening.conf <<'EOF'
# The Remote Support Program (go-live.sh): keys only.
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
EOF
  if sshd -t 2>/dev/null; then systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
    echo "   SSH: passwords switched off — keys only."
  else rm -f /etc/ssh/sshd_config.d/50-rsp-hardening.conf; echo "   SSH: the hardening did not validate, so it was left out."; fi
else
  echo "   SSH: !! NO SSH KEY on this server, so passwords were LEFT ON. Add your key (ssh-copy-id) and run this again."
fi

# A nightly backup of data/ and the database, encrypted with AES-256, kept for 14 days in
# /var/backups/rsp; recordings once each beside it. rsp-backup.sh (shipped beside this script) says
# how, and how to restore. The passphrase is made once; WITHOUT IT THE BACKUPS CANNOT BE OPENED, so
# copy it somewhere safe off this server — and copy the backups off it too (deploy/backup).
BACKUP_KEY="/etc/rsp-backup-key"
if [[ ! -f "$BACKUP_KEY" ]]; then
  head -c 48 /dev/urandom | base64 | tr -d '\n' > "$BACKUP_KEY"
  chmod 600 "$BACKUP_KEY"
fi
install -m 700 -o root -g root "$here/rsp-backup.sh" /usr/local/sbin/rsp-backup
echo "30 2 * * * root /usr/local/sbin/rsp-backup" > /etc/cron.d/rsp-backup
backup_said="$(/usr/local/sbin/rsp-backup 2>&1)" && echo "   Backup: tonight's made now as a test — $backup_said" \
  || echo "   Backup: !! the test backup FAILED — $backup_said"

echo "== 7/7 checking =="
sleep 4
local_ok=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/" || echo 000)
echo "   platform on 127.0.0.1:$PORT -> HTTP $local_ok"
# Asked AS caddy, from where Caddy stands: the socket existing is not enough if caddy may not open it.
sock_ok=$(runuser -u caddy -- curl -s -o /dev/null -w '%{http_code}' --unix-socket "$SOCK" "http://localhost/health" || echo 000)
echo "   platform on $SOCK, as caddy -> HTTP $sock_ok"
if [[ "$sock_ok" != "200" ]]; then
  echo "   !! Caddy cannot reach the platform through its socket - every page will answer 502."
  echo "   !! Look at: ls -l $SOCK; id caddy; journalctl -u $SERVICE | grep -i socket"
fi
public_ok=$(curl -s -o /dev/null -w '%{http_code}' --max-time 25 "https://$DOMAIN/" || echo 000)
echo "   https://$DOMAIN        -> HTTP $public_ok"
if [[ "$public_ok" != "200" ]]; then
  echo "   If that is not 200, the certificate is probably still being issued, or DNS for"
  echo "   $DOMAIN does not point at this server yet. Watch it with: journalctl -u caddy -f"
fi

cat <<EOF

==========================================================================
 Live at https://$DOMAIN
--------------------------------------------------------------------------
 Owner portal : https://$DOMAIN/owner     key: sudo cat $KEY_FILE
                (never printed here: deploy logs get pasted - 2026-10-15. New one: deploy\\Rotate-Keys.ps1)
                The first sign-in with the key sets up your authenticator app;
                from then on it needs the key AND a code. Lost phone:
                sudo -u $USER_NAME $APP_DIR/RemoteSupport.Platform reset-owner-2fa
 Server key   : sudo cat $SERVER_KEY_FILE
                Only needed to watch this server from ANOTHER server's dashboard:
                its Servers page → Add a server → this address and this key.

 Database     : $([[ -n "$PG_CONN" ]] && echo "PostgreSQL ($PG_DROPIN)" || echo "SQLite ($APP_DIR/data/platform.db)")
 Backups      : nightly, AES-256, in /var/backups/rsp (14 days kept; recordings once each)
                Passphrase: $BACKUP_KEY — COPY IT OFF THIS SERVER NOW.
                Without it the backups cannot be opened. Copy the backups off too.

 Create your own tenant, then sign in to its portal at https://$DOMAIN/login :

   cd $APP_DIR
   sudo -u $USER_NAME ./RemoteSupport.Platform create-tenant "Anywhere IT" anywhereit 7
   read -s -p "Password: " PW && echo && sudo -u $USER_NAME ./RemoteSupport.Platform add-tech anywhereit clint@anywhereit.co.za "\$PW" "Clint" --owner --admin; unset PW

 (No 'approve' for your own company: that makes it a 14-day trial. create-tenant is Active.)

 Customer download link : https://$DOMAIN/get/anywhereit
 Logs                   : journalctl -u $SERVICE -f
 Back up                : $APP_DIR/data  (database, uploaded logos and the key ring) — done nightly, above
==========================================================================
EOF
