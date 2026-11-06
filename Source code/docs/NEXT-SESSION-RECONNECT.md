# Next piece of work — surviving a dropped socket, and the system-action buttons

> **Built 2026-09-13. See §19 of `docs/HANDOVER.md` for what was built and what was measured.**
> This file is kept for the reasoning and for the measurement in "What was measured", which is
> still the best short account of why this was a server change before it was a client one.
>
> **Clint's answers to the three open questions**, taken before any code was written:
>
> - **Grace period: 90 seconds.** Long enough for a Wi-Fi roam or a VPN re-dial, short enough that
>   a host does not linger unnoticed on a customer's machine.
> - **The customer is told.** The tray app says the technician's connection has been lost, and goes
>   back to "in progress" when they return. Same reasoning as the listening indicator: the person
>   at the machine is the only one who cannot otherwise tell.
> - **A failed reconnect leaves the window up**, greyed, with a Reconnect button that starts a
>   fresh session on the same machine. The chat and transfer history survive.
> - **Reboot holds for five minutes**, and says immediately when a machine has no unattended agent
>   rather than waiting for something that cannot happen.
>
> **What is still not proven** is the real-hardware half — `tools/reconnectcheck`, a real
> Ctrl+Alt+Del, a real reboot, and the customer-side banner seen on the machine. §19 lists them.

Written 2026-09-13. Read `docs/HANDOVER.md` first for the architecture, the decisions already made
and the working agreements. This file is the brief for this work only. It is stage **1.4** and
**1.5** of `docs/ROADMAP-PARITY.md`.

The two previous briefs are finished: `docs/NEXT-SESSION-IO.md` (clipboard, files and folders, drag
in — proven on real hardware) and `docs/NEXT-SESSION-AUDIO.md` (streaming and the Sound tab —
shipped and confirmed over a live session). Read them only for their reasoning.

---

## The work, in one line

A session must survive the technician's socket dropping, and the console must be able to reboot,
lock and Ctrl+Alt+Del the machine.

## Why this, and why now

It is stage 1.4 in the roadmap, but it is next for three reasons rather than one:

- **It blocks 1.5.** `SysActionMsg` is already handled in the host (`HostEngine.cs:121`, with
  `reboot|lock|ctrl_alt_del`) and **nothing in the entire solution sends it**. The buttons are
  nearly free. But a reboot button that permanently kills the session window is worse than no
  reboot button, so reconnect comes first and they ship together.
- **It is probably `docs/BUGS.md` #2** — the tray app stuck on "support in progress" after a session
  ended. That bug's leading hypothesis is that nothing re-sends state to a device that reconnects.
  `DeviceLink` *does* auto-reconnect on the customer side, so this is the same missing capability
  seen from the other end: reconnect without resync.
- **Every brief since has carried it as a constraint.** `NEXT-SESSION-AUDIO.md` says in plain terms:
  "do not build an audio path that assumes recovery." Everything built from here inherits that
  limitation until it is fixed.

---

## What was measured on 2026-09-13, before this was written

Do not re-derive any of this. It was read out of the code, and the third item changes the shape of
the work completely.

**1. `SessionLink` has no retry at all.** It connects once (`SessionLink.RunAsync`). When the
receive loop ends it fires `OnConnectedChanged(false)`, clears `_ws` and returns. There is no loop,
no backoff, no second attempt.

**2. The console already claims otherwise.** `SessionForm.cs:97` sets the pill to
**"○ reconnecting"** on exactly that event, and `DashboardForm.cs:197` says "○ reconnecting…" too.
The UI announces a recovery that no code performs. Whatever else this work does, that dishonesty
must stop — either it reconnects, or the pill says "disconnected".

**3. The server destroys the session the moment the last viewer's socket closes.** This is the one
that matters. `TenantHub.RunSessionViewerAsync:538-540`:

```csharp
lock (_lock) { s.Viewers.Remove(ws); … empty = s.Viewers.Count == 0; }
if (empty) EndSession(sessionId, "All technicians left");
```

and `EndSession` (`:419`) removes the session from `_sessions`, unregisters it from the
`HubRegistry`, sends `SessionEndedMsg` to the customer's tray app, **and closes the host's socket**
(`s.Host?.CloseAsync("ended")`).

**So a client-side retry loop on its own cannot work.** By the time it dials back in, the session is
gone from the registry, the host process has been told to stop, and `RunSessionViewerAsync:505`
answers `"Session not found"`. A dropped Wi-Fi packet and a technician clicking "Leave / End" are
currently the *same event* as far as the server is concerned. **This is a server-side change first
and a client-side change second.**

**4. A reconnecting viewer gets the replay it needs — and nothing else.** `:507-512` replays
`LastMonitors` and `LastSysInfo` to every viewer that attaches. That is what makes joining late
work, and it is what a reconnect depends on: `MonitorsMsg` is the **only** thing that triggers the
console's first `SubscribeMsg`. What it does *not* replay is any in-session state the technician had
set — which monitor was selected, the quality setting, and whether **Listen** was on.

**5. Audio listening is host-side state.** `AudioListenMsg { On }` is sent by the console
(`SessionForm.cs:1013`) and the host starts capturing. After a reconnect the host is still
capturing and still telling the customer someone is listening, while the console has forgotten it
asked. That is a **disclosure** feature, so getting it wrong is worse than a cosmetic bug: see §18
of the handover on why the indicator exists at all.

---

## What to build

### Part 1 — the session outlives the socket (1.4)

**1a. A grace period on the server.** When the last viewer leaves, do not end the session
immediately. Hold it for a short window — **60 seconds is the suggestion, not a decision** — and end
it only if nobody has come back. A viewer that reconnects inside the window rejoins the session it
left, with the host still running.

The distinction that must be preserved: **"Leave / End" is deliberate and must still end the session
at once.** An explicit `EndSessionMsg` is not a dropped socket. If those two stay indistinguishable,
every technician who closes a window leaves a host running on a customer machine for a minute, which
is a disclosure problem, not just an untidy one.

Mirror it in `RemoteSupport.Server` if the change touches shared behaviour — fixes have needed
applying to both before (handover §2).

**1b. A retry loop in `SessionLink`.** Backoff, a cap, and a give-up that reports honestly. The
existing `DeviceLink` is the model — it is already an auto-reconnecting connection and its comment
at `DeviceLink.cs:70` records a trap worth copying: a misbehaving subscriber must never be able to
stop the reconnect loop.

**The trap that will bite here is already documented and has already cost an hour:** attach every
`OnMessage` handler **before** calling `RunAsync`. The replayed `MonitorsMsg` arrives within
milliseconds of the socket opening, and a handler registered after the pump has started misses it
for good — leaving a viewer that reconnected to a permanently black screen, which looks exactly like
a reconnect that failed.

**1c. Re-establish what the technician had set.** After a successful reconnect: re-subscribe to the
monitor that was selected, force a full frame, re-send the quality setting, and re-send
`AudioListenMsg` if Listen was on — or turn the button off in the UI. Decide which and make it
visible either way; silently stopping audio while the customer's banner still says someone is
listening is the worst of the options.

**1d. Resync a device that reconnects, and close BUGS #2.** The customer's tray app reconnects on
its own but is told nothing about the current state when it does. Send it the session state on
reconnect. Then reproduce #2 deliberately — drop the tray app's socket mid-session, let it come
back, end the session — and see whether the stale "support in progress" is what has been reported.
**If it turns out to be a different fault, say so and leave the bug open** rather than closing it
because the area was touched.

### Part 2 — the buttons (1.5)

Toolbar buttons in the session window for **reboot**, **lock** and **Ctrl+Alt+Del**. The host end
exists; this is console UI plus a `SysActionMsg`.

- **Confirm before reboot.** It is irreversible from the technician's side and the person at the
  machine may be mid-sentence in a document.
- **Reboot needs the reconnect story from part 1**, and more of it: the machine goes away for a
  minute or two, so the grace period is the wrong tool. Decide what the window does — most likely
  hold, show "machine restarting", and wait for the agent to bring a host back. An unattended
  machine will; an attended-only install will not, because nobody is there to accept. **Say which
  in the UI rather than letting it hang.**
- **Record the action on the ticket.** `ScriptRunMsg` already logs to the ticket for exactly this
  reason (`TenantHub.cs:524-530`) — a reboot deserves the same audit line.
- Ctrl+Alt+Del is the one that needs SYSTEM and the Winlogon desktop, which is already how the host
  runs (§4). It should work; prove it rather than assuming it.

---

## How to prove it

The rule from §7 and §12 of the handover applies: **every feature on this list gets a harness that
proves it before it is called done**, and a transfer must not mark its own homework.

The harness this needs does not exist yet. Something like `tools/reconnectcheck`: start a session,
confirm tiles are arriving, **kill the viewer socket without an `EndSessionMsg`**, and then assert
that the session still exists on the server, the host is still alive, the viewer gets back in, and
tiles resume. Counting frames before and after is the measurement; "it looked fine" is not.

Prove the buttons the way the others were proved — `tools/runscript` can ask the machine what it
thinks happened, and `viewprobe` screenshots the toolbar so a button that half-covers something else
is seen before it ships. That is the only reason the Listen button's overlap was caught.

**The ship trap, now in `ship-build.ps1`'s closing advice and worth repeating:** a harness that joins
an *existing* session talks to the `Host.exe` that was running when that session started. Straight
after shipping a build that is the old binary, and the check reports the new feature missing —
correctly, about a host that has never heard of it. **End any open session first.**

---

## Open questions for Clint — ask, don't assume

- **How long should the grace period be?** 60 seconds is a guess. Too short and a Wi-Fi blip still
  loses the session; too long and a host lingers on a customer machine after the technician has gone.
- **Should the customer be told during the grace window?** The tray app currently believes support is
  in progress, and for 60 seconds it would be right. Doing nothing is defensible. Given that the
  listening indicator was deliberately built (§18), it is worth a decision rather than a default.
- **What should a failed reconnect do?** Close the session window, or leave it up with a Reconnect
  button. The second is friendlier; the first is honest about a session that is gone.

---

## Constraints carried over

- **The host runs as SYSTEM** in the interactive session via `SessionLauncher` (§4).
- **Never `await` inside a `lock`** in the hub (CS1996) — take the list under the lock, send outside.
- **Attach every `WsConnection.OnMessage` handler before `RunAsync`** (§10). Central to this work.
- **A new session has no host for a second or two**, and the hub relays with `s.Host?.Send(m)`, a
  silent no-op. Wait for evidence of the host before sending.
- **A feature with no visible feedback will be reported as broken**, because it is indistinguishable
  from broken. Drag and drop worked for a fortnight and was reported as failing the first time
  anyone used it. Reconnect is exactly this shape: silent recovery looks like nothing happening.
- WinForms traps are in §10. F11 is handled locally by `ScreenView` and must not be forwarded.

## The machines

- Server: this laptop, static **192.168.1.115:8090**, run from `src/RemoteSupport.Platform` in
  Clint's own terminal. Building the whole solution fails while it holds `RemoteSupport.Shared.dll`
  — build individual projects, or ask him to stop it.
- **DESKTOP-OPHQIDH** — the main test machine, user `Lenovo`. Client + unattended agent + the
  monthly health check, and on the current build.
- **DESKTOP-L2B85US** — ticketing client "Roger Rabbit", client + unattended agent.
- `tools/ship-build.ps1` puts a new build on the server's download and into `demo\technician`.
  `deploy-client-payload.ps1` alone is **not** enough — it refreshes the installer scripts but not
  the exes.
