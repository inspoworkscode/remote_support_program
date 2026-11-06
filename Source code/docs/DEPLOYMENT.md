# Server deployment

The server is a single self-contained executable plus `appsettings.json`, `wwwroot/` and a `data/`
folder it creates on first run. It needs one open TCP port and, for production, TLS.

## Install as a service

Copy `dist/server/` to the VPS, then from an elevated PowerShell in that folder:

```powershell
.\install-server-service.ps1 -Port 8080
```

This registers `RemoteSupportServer`, sets auto-restart on failure, opens the firewall port, and starts it.

Add technicians (max 5 by default):

```powershell
.\RemoteSupport.Server.exe add-tech <username> <password> "Display Name"
.\RemoteSupport.Server.exe list-techs
.\RemoteSupport.Server.exe set-password <username> <newpassword>
.\RemoteSupport.Server.exe remove-tech <username>
```

## Linux server

The server is portable ASP.NET Core with no Windows-only dependencies, so it runs on Linux as a
systemd service. Build the Linux payload on your Windows dev box:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Linux
```

This produces `dist/server-linux/` containing the native `RemoteSupport.Server` binary, `wwwroot`,
`appsettings.json`, `branding`, a systemd unit and an install script. Copy that folder to the Linux box
and run:

```bash
sudo ./install-server-linux.sh
sudo -u remotesupport /opt/remotesupport/RemoteSupport.Server add-tech clint "StrongPassword" "Clint"
```

The script creates an unprivileged `remotesupport` account, installs to `/opt/remotesupport`, enables the
service, and opens the firewall port. Manage it the usual way:

```bash
sudo systemctl status remotesupport-server
journalctl -u remotesupport-server -f
```

The technician CLI commands (`add-tech`, `list-techs`, `set-password`, `remove-tech`) work identically;
just prefix them with the binary path. TLS still comes from a reverse proxy in front — Caddy or Nginx,
covered below and even simpler to run on Linux.

## TLS (required for `wss://`)

The client and technician apps use the `serverUrl` from `brand.json`. For production use `wss://`.
Two options:

**A. Terminate TLS at the app (simple).** Put a PFX certificate on the box and set in `appsettings.json`:
```json
"RemoteSupport": { "HttpsPort": 8443, "CertificatePath": "cert.pfx", "CertificatePassword": "..." }
```
Set `brand.json` `serverUrl` to `wss://support.yourcompany.co.za:8443` (or 443 if you use that port).

**B. Terminate TLS at a reverse proxy (recommended).** Run IIS, Nginx or Caddy in front on 443,
proxying to `http://localhost:8080` with WebSocket upgrade enabled. Keep the app on plain HTTP behind it.
Example Caddy:
```
support.yourcompany.co.za {
    reverse_proxy localhost:8080
}
```
Caddy handles the certificate automatically. Then `serverUrl` = `wss://support.yourcompany.co.za`.

## Config reference (`appsettings.json`)

| Key | Meaning |
|---|---|
| `Urls` | Listen address/port for plain HTTP (default `http://0.0.0.0:8080`) |
| `RemoteSupport:MaxTechnicians` | Account cap (default 5) |
| `RemoteSupport:DataDirectory` | Where accounts, device keys and chat logs live (default `data`) |
| `RemoteSupport:HttpsPort` / `CertificatePath` / `CertificatePassword` | Optional in-app TLS |

## Data the server keeps (`data/`)

- `technicians.json` — accounts (hashed passwords)
- `devices.json` — per-device keys (anti-spoofing)
- `device-info.json` — last-known device list (for the console)
- `chat.jsonl` — append-only chat log

Back up this folder. Deleting `technicians.json` removes all logins.

## Updating

Stop the service, replace the files, start it again:
```powershell
sc.exe stop RemoteSupportServer ; sc.exe start RemoteSupportServer
```
