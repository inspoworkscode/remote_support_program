#!/usr/bin/env bash
# Installs the Remote Support server as a systemd service on Linux (Debian/Ubuntu/RHEL).
# Run as root from the folder containing RemoteSupport.Server, appsettings.json and wwwroot.
#   sudo ./install-server-linux.sh
set -euo pipefail

APP_DIR="/opt/remotesupport"
SERVICE="remotesupport-server"
USER_NAME="remotesupport"
PORT="${PORT:-8080}"

here="$(cd "$(dirname "$0")" && pwd)"
if [[ ! -f "$here/RemoteSupport.Server" ]]; then
  echo "RemoteSupport.Server (the linux-x64 binary) must be next to this script." >&2
  exit 1
fi

echo "== Creating service account '$USER_NAME' =="
id -u "$USER_NAME" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"

echo "== Installing files to $APP_DIR =="
mkdir -p "$APP_DIR"
cp -r "$here/." "$APP_DIR/"
chmod +x "$APP_DIR/RemoteSupport.Server"
mkdir -p "$APP_DIR/data"
chown -R "$USER_NAME":"$USER_NAME" "$APP_DIR"

echo "== Installing systemd unit =="
install -m 644 "$here/remotesupport-server.service" "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload
systemctl enable --now "$SERVICE"

# open the firewall if a supported tool is present
if command -v ufw >/dev/null 2>&1; then ufw allow "$PORT"/tcp || true
elif command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --permanent --add-port="$PORT"/tcp && firewall-cmd --reload || true
fi

echo
echo "Service '$SERVICE' is running. Add your first technician:"
echo "  sudo -u $USER_NAME $APP_DIR/RemoteSupport.Server add-tech <username> <password> \"Display Name\""
echo "Health check: curl http://localhost:$PORT/health"
echo "Logs: journalctl -u $SERVICE -f"
