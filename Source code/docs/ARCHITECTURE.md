# Architecture

## Overview

```
   Customer machine                     Your server (VPS)                Technician machine
 ┌──────────────────┐              ┌──────────────────────┐          ┌────────────────────┐
 │ Client (tray app)│◀──ws/device─▶│                      │◀─ws/tech─▶│ Technician console │
 │ Agent (service)  │◀──ws/device─▶│   RemoteSupport       │          │                    │
 │                  │              │   .Server (hub)       │          │  Session viewer    │
 │ Host.exe ────────┼──ws/session─▶│   calls · chat ·      │◀ws/session┤  (screen+input+    │
 │ (spawned per     │   (host)     │   presence · relay    │  (viewer) │   files+scripts)   │
 │  session)        │              │                      │          │                    │
 └──────────────────┘              └──────────────────────┘          └────────────────────┘
```

Everything is WebSockets over one HTTP(S) port. The server is a message hub and a **relay**: it never
stores screen data, it just forwards frames from the session **host** to every **viewer**.

## The three socket endpoints

- **`/ws/device`** — the Client app and the Agent service each hold one. They say `hello` (role =
  Client or Agent), then send `request_call` / `cancel_call` / `chat`, and receive `start_session`,
  `call_update`, `chat`, `session_ended`.
- **`/ws/tech`** — one per signed-in technician (needs a login token). Receives `state`, `ring`,
  `call_update`, `device_update`, `session_ready`, `tech_presence`, `chat`. Sends `accept_call`,
  `connect_device`, `end_session`, `chat`.
- **`/ws/session`** — the live session. Opened as `role=host` (by Host.exe, with a one-time host token)
  or `role=viewer` (by each technician). The server copies every message and binary frame from the host
  to all viewers and vice-versa, so multiple technicians share one session automatically.

## Call flow

1. Client clicks **Request Support** → `request_call` on `/ws/device`.
2. Server creates a ringing `CallInfo` and pushes `ring` to **every** technician.
3. First technician to `accept_call` wins. The server creates a `Session`, mints a host token, and
   sends `start_session` to the device.
4. On the device, the Client app (attended) or the Agent (unattended) launches **Host.exe** with that
   token. Host.exe connects to `/ws/session?role=host`.
5. The accepting technician (and any other who joins) opens `/ws/session?role=viewer`. Screen tiles and
   input now flow through the relay. Additional technicians joining the same session just add another
   viewer — no extra setup.

## Attended vs unattended: why Host.exe is separate

A Windows **service runs in session 0** and cannot see or control the interactive desktop. So the Agent
service does not capture anything itself — when a session starts it uses `WTSQueryUserToken` +
`CreateProcessAsUser` to spawn **Host.exe inside the active console session** (`SessionLauncher.cs`).
The Client tray app already runs in the user's session, so it spawns Host.exe directly. Either way the
capture/control code is the same binary, kept out of the UI process.

## Screen pipeline

`ScreenCapture.cs` grabs each monitor with GDI `BitBlt`, splits it into 128-px tiles, hashes each tile
and JPEG-encodes only the ones that changed. Tiles are sent as compact binary frames
(`BinKind.Tiles`). The technician's `ScreenView` control paints tiles onto a per-monitor canvas and
scales it to fit. This is deliberately dependency-free and swappable for a DXGI/H.264 pipeline later
without touching the protocol.

## Input, files, scripts

- **Input** — `InputInjector.cs` uses `SendInput` with absolute virtual-desktop coordinates, so
  multi-monitor mapping is correct.
- **Files** — `FileTransfer.cs` streams 64 KB binary chunks (`BinKind.FileChunk`). Push (tech→client),
  pull (client→tech) and a remote file/drive browser are all supported.
- **Scripts** — `ScriptRunner.cs` writes the script to a temp file, runs PowerShell or cmd, and streams
  stdout/stderr back live with the final exit code.

## Where AI plugs in later

The server hub already sees all chat and can see all sessions. Natural insertion points:
- A hook in `Hub.RouteChat` to draft suggested replies or triage the request before a human accepts.
- A "suggest a script" action in the technician console that calls an LLM and drops the result into the
  script box (which already streams output).
- Session summaries generated from the chat + script transcript stored under the server `data/` folder.

## Security notes (v1)

- Technician passwords are PBKDF2-SHA256 (120k iterations, per-user salt).
- Devices are pinned to a per-machine key on first connect to stop ID spoofing.
- Session host tokens are single-use random 24-byte values.
- **Put TLS in front of the server** (`wss://`) — see DEPLOYMENT.md. Without it, traffic is unencrypted.

## Consent

Before any screen is captured, `Host.exe` shows a branded **"Allow a remote support connection?"** dialog
on the client machine (`ConsentPrompt.cs`). Because the host always runs in the interactive user session,
this covers both attended and unattended launches. The dialog names the technician, lists what access is
being granted, and **auto-declines after a timeout** so a machine left alone never grants access. On deny,
the host sends `decline_session` and the technician sees the reason.

Behaviour is set in `brand.json`:
- `requireConsent` (default `true`) — ask for attended sessions.
- `unattendedRequireConsent` (default `false`) — ask for unattended/agent sessions too. Leave off for
  true unattended access; turn on if a person must approve every connection.
- `consentTimeoutSeconds` (default `60`) — how long the prompt waits before auto-declining.
