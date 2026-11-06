# End-to-end encryption of sessions — the brief

**BUILT IN FULL the same day (all five steps), NOT on hardware — HANDOVER §112 is what was built and proved;
`TEST-3.6.md` section CC (525–534) is what is left to see on a real machine.**

Written 2026-10-07. Roadmap 4.4 ("end-to-end encryption of sessions"), HANDOVER §72's "Left for soon after"
item 4. Clint: *"Can we do this?"* — then three answers, below. The handover section that records what was
built will be §112.

## What it is for

TLS ends at Caddy, so **everything a session carries is plaintext inside our server**: the picture, every key
a technician presses (passwords typed into a customer's machine included), the clipboard, file contents,
sound, voice, scripts and their output. A server that is read — by the hosting company, a stolen backup, a
memory dump, somebody who got in — reads all of it.

End-to-end: the host and each console agree a key the server never sees, and the CONTENT travels sealed with
AES-256-GCM. The server still relays it, still knows who connected to what and when, and still writes the
audit trail — it just cannot read what was on the screen or typed.

## Clint's three answers (2026-10-07)

1. **Encrypt AND pin keys.** Each machine has an identity key; the console remembers it the first time and
   **warns loudly if it changes when nobody expected it** — never a silent connect. Stops anyone reading the
   server, and a taken-over server swapping keys after first contact.
2. **Older apps: allowed, clearly marked.** An old host or console still works (still TLS), with *"Not
   end-to-end encrypted — update the support app"* on the session window. No machine stops working on the day
   this ships.
3. **The browser routes are included**: the guest in a browser (`guestview.js`) and browser support
   (`help.js`, where the BROWSER is the host). WebCrypto does the same ECDH, HKDF, AES-GCM and ECDSA.

## What it does NOT do — say it plainly, never oversell it

- **A taken-over server can still START a session of its own.** It is what authorises technicians, and the
  agent obeys it. Pinning protects a REAL technician's session from being read or hijacked; it does not stop
  the server acting as a technician. TeamViewer has the same shape (its keys also go through its servers).
- **The audit trail stays readable on purpose** — who connected, files moved (NAMES), scripts run (their
  first line, as today), reboots, recording, talking, privacy screen. That is the ticket Clint wants.
- Chat stays on the ticket, as today — it is the customer's record, not session content.
- First contact is trusted (TOFU): a console that has never seen a machine believes the first key it is shown.

## The design

### Where: the connection, not the call sites

`WsConnection` gets an optional **`Seal`** (`Shared/SessionSeal.cs`). With one set and active, `Send(Msg)` of
a CONTENT message and `SendBinary` of a content frame are sealed as they are queued; a sealed frame received
is opened before `OnMessage`/`OnBinary` see it. So the host's thirty-odd send sites, the console's session
code and the recorder are untouched — they see plaintext, as now.

### What is sealed: everything except a CLEAR list

**Safe by default**: a message added next year is sealed unless somebody decides the server must read it.
`SessionSeal.StaysClear(Msg)` is the one list — what the hub ACTS on:

| clear | why the server reads it |
|---|---|
| `PingMsg`, `ErrorMsg`, `SessionEndedMsg`, `ParticipantsMsg`, `EndSessionMsg` | the session's own life |
| `DeclineSessionMsg`, `ConsentGrantedMsg` | the consent gate |
| `MonitorsMsg`, `SysInfoMsg` | replayed to a late joiner (layout and machine info — no content) |
| `PrivacyStateMsg`, `AudioStatusMsg`, `VoiceStateMsg`, `FileActivityMsg`, `RecordingStateMsg`, `VoiceTalkMsg` | written on the ticket and/or passed to the customer's tray app — disclosures |
| `SysActionMsg`, `SysActionResultMsg` | audit; Ctrl+Alt+Del goes to the AGENT |
| `VoiceMicAnswerMsg` | dropped from a technician (only the customer answers) |
| `ChatMsg` | routed to the tray app; on the ticket |
| `ScriptAuditMsg` (new) | the ticket's "Ran PowerShell script: <first line>" — the script itself is sealed |
| `E2eHelloMsg`, `E2eKeyMsg`, `E2eModeMsg`, `E2eReadyMsg` (new) | the handshake |

Sealed: mouse, keys, clipboard, every file message and chunk, scripts and their output, printing, selection,
sound devices, the picture (tiles, H.264), the pointer, the machine's sound, voice.

### Frames

- A sealed MESSAGE is a binary frame **`[7 = BinKind.Sealed][tagLen:u8][tag][sender:u32][counter:u64][ciphertext+tag16]`**,
  `tag` being the message's JSON type name (`"mouse"`). The server needs the tag for the visitor and free
  allow-lists (`VisitorMaySend`, `FreePlan.MaySend`) — it rebuilds an empty message of that type and asks the
  same questions. **The receiver refuses a message whose opened type is not its tag, or is on the clear list**
  — so a visitor cannot dress a script up as a mouse movement, and nothing the hub must see can be smuggled
  past it.
- A sealed BINARY frame keeps its kind with the top bit set: **`[kind | 0x80][sender][counter][ciphertext+tag16]`**,
  so the drop rule (screen frames droppable, file chunks never) and the allow-lists still work on `kind & 0x7F`.
- The visible header (kind, tag) and the session id are the AES-GCM associated data: changing the tag breaks it.
- **Nonce = sender (4 bytes) ‖ counter (8 bytes)**: the host is sender 0; each viewer is given its own number
  by the host. A receiver keeps the last counter per sender and refuses anything not higher (no replays).

### The key

1. The host makes the session key **K** (32 random bytes) and an ephemeral ECDH P-256 key when it connects,
   and says **`E2eReadyMsg`**.
2. Each E2E-capable viewer sends **`E2eHelloMsg { Pub }`** (its own ephemeral P-256 key) when it joins and
   whenever it sees `E2eReadyMsg`.
3. The host answers **`E2eKeyMsg { For, HostPub, SenderId, Wrapped, IdentityPub, Signature }`**: K wrapped with
   AES-GCM under HKDF-SHA256(ECDH(host, viewer)), and the machine's IDENTITY key's ECDSA signature over
   (session, viewer key, host key, sender id).
4. The viewer checks the signature, checks the identity key against its PIN, unwraps K. Only then is its seal
   active.

**The machine's identity key** (ECDSA P-256) lives in `%ProgramData%\RemoteSupport\identity.key`, readable only
by SYSTEM and Administrators, made by the host the first time it runs as SYSTEM. A host running as the person
(the tray app's, for an old agent) cannot read it: still encrypted, marked *"machine identity not checked"*.
Browser support has none either (the customer installed nothing). Uninstalling deletes it, so **a reinstall
is a new identity** — which is exactly when §111 already asks the office to confirm the machine.

**The pin** (`%LOCALAPPDATA%\RemoteSupport\machine-keys.json`, per Windows user): by device id, AND by machine
name + customer, because a reinstall (or an impostor) arrives with a NEW device id and a name the technician
recognises. A different key for either → the session window holds — no picture, no input — and asks:
*"SUE-PC's identity has changed… only continue if you know it was reinstalled."* Continue re-pins; End ends.

### Mode — sealed or clear, and never silently

The hub knows which viewer sockets said `E2eHelloMsg`. It tells the host **`E2eModeMsg { Sealed }`**: sealed
while every viewer can, clear if one cannot (an old console). The host then seals or not. Each viewer's badge
says what ACTUALLY arrived: content opened under a pinned key → *End-to-end encrypted*; any content frame that
arrives clear → *Not end-to-end encrypted*, for the rest of the session. A server forcing clear is VISIBLE.
In sealed mode the host refuses clear content from viewers (it could only have come from the server).

### Badge (session window status bar)

- 🔒 **End-to-end encrypted** — identity checked (green)
- 🔒 **Encrypted — machine identity not checked** (amber): tray-hosted, browser support
- **Not end-to-end encrypted — update the support app** (amber): old host, old console in the session, or clear content seen

## The build, in order

1. `Shared/SessionSeal.cs`, `Shared/MachineIdentity.cs`, the messages, `WsConnection.Seal` — `tools/e2ecore`.
2. Host and console, the hub's allow-lists on sealed frames, the mode — `tools/e2ewire`: a real hub, the real
   seal; **a spy viewer that never gets the key searches everything relayed for known plaintext (a typed
   password, a clipboard marker, a file's bytes) and must find none**; tags lied about, replays, a clear frame
   injected, an old console joining.
3. The pin and the badge in the console.
4. `guestview.js` and `help.js`, against the same C# seal.
5. HANDOVER §112, `TEST-3.6.md`, CLAUDE.md, the roadmap.

Deploy, when built: `Publish-Platform.ps1` (no migration), the console re-downloaded, a FULL ship and client
reinstall for the host. Until a machine is reinstalled its sessions say *Not end-to-end encrypted*.
