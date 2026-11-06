#!/usr/bin/env bash
# =====================================================================================
# The Remote Support Program — put the holding page live on a fresh Ubuntu server.
#
#   sudo ./go-live-landing.sh theremotesupportprogram.com clint@anywhereit.co.za
#
# This is the small version of go-live.sh: Caddy, a certificate and one static page.
# The platform itself is NOT installed, so nothing is exposed while 2FA is still being
# built. Running go-live.sh later replaces the Caddy configuration written here and
# turns the same server into the real site — no DNS change, no second certificate.
# =====================================================================================
set -euo pipefail

DOMAIN="${1:-}"
LE_EMAIL="${2:-}"
WEB_ROOT="/var/www/landing"
here="$(cd "$(dirname "$0")" && pwd)"

[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
[[ -n "$DOMAIN" && -n "$LE_EMAIL" ]] || { echo "Usage: sudo ./go-live-landing.sh <domain> <email-for-certificate>" >&2; exit 1; }
[[ -f "$here/index.html" ]] || { echo "index.html must be next to this script." >&2; exit 1; }

echo "== 1/4 Caddy =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq curl ca-certificates gnupg debian-keyring debian-archive-keyring apt-transport-https ufw >/dev/null

if ! command -v caddy >/dev/null 2>&1; then
  curl -fsSL 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
    | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -fsSL 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
    > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -qq
  apt-get install -y -qq caddy >/dev/null
fi

echo "== 2/4 the page =="
mkdir -p "$WEB_ROOT"
cp "$here/index.html" "$WEB_ROOT/index.html"
chown -R caddy:caddy "$WEB_ROOT" 2>/dev/null || true

echo "== 3/4 TLS on 443 =="
cat > /etc/caddy/Caddyfile <<EOF
{
    email $LE_EMAIL
}

$DOMAIN, www.$DOMAIN {
    encode zstd gzip
    root * $WEB_ROOT
    file_server
    # Everything answers with the holding page while there is only one page to serve.
    handle_errors {
        rewrite * /index.html
        file_server
    }
}
EOF
systemctl enable caddy >/dev/null 2>&1 || true
systemctl restart caddy

echo "== 4/4 firewall =="
ufw allow 22/tcp  >/dev/null 2>&1 || true
ufw allow 80/tcp  >/dev/null 2>&1 || true
ufw allow 443/tcp >/dev/null 2>&1 || true
yes | ufw enable  >/dev/null 2>&1 || true

sleep 4
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "https://$DOMAIN/" || echo 000)
echo
echo "   https://$DOMAIN -> HTTP $code"
if [[ "$code" == "200" ]]; then
  echo "   Live."
else
  echo "   Not answering yet. Almost always DNS: the A records for $DOMAIN and www.$DOMAIN"
  echo "   must point at this server before a certificate can be issued."
  echo "   Watch it with: journalctl -u caddy -f"
fi
echo
echo "   To change the page later, edit landing/index.html and run Publish-Landing.ps1 again."
echo "   To turn this server into the full platform: run Publish-Platform.ps1 (no DNS change needed)."
