# Remote Support

A self-hosted, fully branded remote-support suite for Windows. Built from scratch in C# / .NET 10.
No third-party remote-control engine — you own every line and run it on your own server.

## What it does

- **Client** requests a support call with one click. It rings **all** signed-in technicians until one accepts.
- **Chat** the client can start reaches every technician; the whole team sees the conversation.
- **Multiple technicians** can work the **same session** at once (shared view + control).
- **Attended, ad-hoc and unattended** access (unattended runs as a background Windows service).
- **Multi-monitor** screen sharing, **file transfer** (both directions, plus a remote file browser),
  and **script execution** (PowerShell / batch) with live streamed output.
- **Client consent** — a branded approval prompt appears on the client machine before any screen is
  shared; it auto-declines if unanswered. Configurable per attended/unattended.
- **Missed-call proof** — an always-on-top alert appears in the middle of every signed-in technician's
  screen when a call comes in, even with the console minimised and the sound off.
- Up to **5 technician accounts** (configurable), PBKDF2-hashed passwords.
- Lightweight core designed so an **AI assistant** can be added later (chat triage, script suggestions).

## Components

| Project | Output | Runs on |
|---|---|---|
| `RemoteSupport.Server` | ASP.NET Core web service | Your VPS / server |
| `RemoteSupport.Client` | Tray app (attended) | Customer machines |
| `RemoteSupport.Agent` | Windows service (unattended) | Customer machines |
| `RemoteSupport.Host` | Capture/control helper (spawned per session) | Customer machines |
| `RemoteSupport.Technician` | Technician console | Your technicians |
| `RemoteSupport.Shared` | Protocol + shared library | — |
| `RemoteSupport.Platform` | Multi-tenant SaaS server for theremotesupportprogram.com (see [docs/PLATFORM.md](docs/PLATFORM.md)) | Your platform host |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how these fit together and
[docs/BRANDING.md](docs/BRANDING.md) to apply your company identity.

## Quick start

### 1. Brand it
Edit [branding/brand.json](branding/brand.json): set `companyName`, `productName`, colours,
`serverUrl` (e.g. `wss://support.yourcompany.co.za`), and drop a `logo.png` in `branding/`.

### 2. Build everything
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Installers
```
Output lands in `dist/`:
- `dist/server/` — copy to your VPS
- `dist/installers/` — the two branded setup .exe files (client + technician)

(Requires the .NET 10 SDK and, for `-Installers`, Inno Setup 6.)

### 3. Run the server (on your VPS)
```powershell
# in the copied dist\server folder, elevated
.\install-server-service.ps1 -Port 8080
.\RemoteSupport.Server.exe add-tech clint "StrongPassword" "Clint"
```
Put HTTPS in front of it (see docs/DEPLOYMENT.md) so clients can use `wss://`.

### 4. Install the apps
- Run the **Technician** setup on your machines, sign in with the account you created.
- Run the **Client** setup on a customer machine (tick *unattended* to install the background service).

## Testing status

The control plane (login, ringing, accept, multi-tech sessions, chat, end) and the real capture host
(monitor enumeration, live screen tiles, script execution with exit codes) are covered by the automated
checks that were run during the build — all passing.

## License

Your own code — license as you wish. No AGPL or third-party remote-engine obligations.
