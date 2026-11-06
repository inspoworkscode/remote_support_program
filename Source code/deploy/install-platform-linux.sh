#!/usr/bin/env bash
# Installs The Remote Support Program platform as a systemd service on Linux.
# Run as root from the folder containing RemoteSupport.Platform, wwwroot, payloads and appsettings.json.
#   sudo ./install-platform-linux.sh
set -euo pipefail
APP_DIR="/opt/rsp"; SERVICE="remotesupport-platform"; USER_NAME="rsp"; PORT="${PORT:-8090}"
here="$(cd "$(dirname "$0")" && pwd)"
[[ -f "$here/RemoteSupport.Platform" ]] || { echo "RemoteSupport.Platform (linux-x64) must be next to this script." >&2; exit 1; }

id -u "$USER_NAME" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"
mkdir -p "$APP_DIR"; cp -r "$here/." "$APP_DIR/"; chmod +x "$APP_DIR/RemoteSupport.Platform"
mkdir -p "$APP_DIR/data"; chown -R "$USER_NAME":"$USER_NAME" "$APP_DIR"
install -m 644 "$here/remotesupport-platform.service" "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload; systemctl enable --now "$SERVICE"
if command -v ufw >/dev/null 2>&1; then ufw allow "$PORT"/tcp || true; fi

echo
echo "Platform running on port $PORT. Put Caddy/Nginx in front on 443 for https://theremotesupportprogram.com"
echo "IMPORTANT: edit /etc/systemd/system/$SERVICE.service and set RSP_Platform__AdminKey, then: systemctl daemon-reload && systemctl restart $SERVICE"
echo "Owner portal: https://theremotesupportprogram.com/owner   Logs: journalctl -u $SERVICE -f"
