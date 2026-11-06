# Previous piece of work — clipboard, drag and drop, and the customer's sound

**Finished 2026-09-13**, except the audio half, which Clint reversed into streaming — see
`docs/NEXT-SESSION-AUDIO.md`. Kept for the measurements and the reasoning, not as a to-do list.

Written 2026-09-13. Read `docs/HANDOVER.md` first for the architecture, the decisions already
made, and the working agreements. This file is the brief for this work only.

---

## Clint's words

> Copy and paste both ways, drag and drop both ways, and being able to hear the client's sound.
> My sound not working is a common issue that we encounter and need to be able to resolve or test
> it remotely.

---

## What is already there, and what is not

Measured on 2026-09-13, not assumed. Do not re-derive it.

| | State |
|---|---|
| **Clipboard** | `ClipboardMsg { Text }` exists in `Protocol.cs:354` and is **referenced nowhere else**. Declared and never wired: nothing sends it, the hub does not route it, no console or host code handles it. Text only — no image or file-list clipboard. |
| **File transfer** | Real and implemented: `FileOfferMsg`, `FileAcceptMsg`, `FileDoneMsg`, `FileListRequestMsg`, `FileListMsg`, `FilePullMsg`, plus `BinKind.FileChunk`. Lives in `RemoteSupport.Host/FileTransfer.cs`, `HostEngine.cs` and `Technician/SessionForm.cs`. **But §8 of the handover still lists file transfer in both directions as "not yet verified by eye".** |
| **Drag and drop** | Nothing. No `AllowDrop`, no `DragEnter`, no `DragDrop` anywhere in the solution. |
| **Audio** | Nothing at all. No capture, no codec, no protocol, no UI. |

Two structural facts that shape the work:

- **The hub relays binary frames without inspecting them.** `TenantHub.cs:480` fans host binary out
  to every viewer; `:525` sends viewer binary to the host. Byte 0 is the kind
  (`BinKind.Tiles = 1`, `FileChunk = 2`). **A new `BinKind` for audio needs no server change at
  all** — only the host and the console.
- **`ClipboardMsg` is a JSON `Msg`, and JSON messages *are* routed explicitly.** That is exactly
  where session chat was broken on 2026-09-09: `RouteChat` fanned out to `/ws/tech` dashboards and
  the customer's `/ws/device`, but not to `/ws/session` **viewer** sockets, which is where
  `SessionForm` listens, so inbound chat never arrived. Clipboard will hit the same trap unless it
  is routed to viewers. See §8 of the handover.

---

---

## Answered on 2026-09-13 — read this before the sections below

The two things this brief said to settle before writing any audio code have both been settled.
The sections that follow are left as they were written, so the reasoning is still readable, but
where they disagree with this section, this section is what was measured.

**Clint chose: diagnose and test tone first.** Not live streaming, not the five-second sample.
The host reports what Windows thinks and can play a test tone on the customer's machine. That is
the question he actually gets on the phone.

**SYSTEM can see and drive the user's audio. The design does not have to change.** Measured with
`tools/audioprobe` against DESKTOP-OPHQIDH, running as a child of `Host.exe` — so the script's own
token *is* the host's token:

- identity came back `NT AUTHORITY\SYSTEM`, session 1, parent `RemoteSupport.Host`
- it saw the endpoint, its 75% volume and its mute state
- it saw the **per-application mixer**, including `firefox (pid 5304, session 1)` — a process in
  the interactive user's session, which was the doubt worth having
- with `--tone`, rendering succeeded: a SYSTEM process in session 1 **is** allowed to play audio

The Edge-as-SYSTEM problem does not repeat here. No impersonation, no helper in the user's
session, no architecture change.

The first run also found a real fault on the first try, which is the argument for the feature:
**DESKTOP-OPHQIDH's speakers are muted.** On this laptop the default output turned out to be the
HDMI television rather than the speakers. Those are the two most common "my sound stopped
working" calls, and the probe named both without anyone looking at the machine.

**File transfer is proven** (`tools/filecheck`, 10 of 10 checks, SHA-256 identical both ways,
saved to the logged-on user's `Downloads` and not SYSTEM's). Step 1 of the order below is done.

**The clipboard warning below is overstated.** The chat trap does not apply. `RunSessionHostAsync`
and `RunSessionViewerAsync` are a **generic passthrough** — host to every viewer, viewer to host,
with no per-message cases. Chat needed `RouteChat` because chat must also reach the customer's
tray app on the `/ws/device` connection, which is a different problem. Clipboard between the
technician and the host needs **no server change at all**: a host-side handler and console UI.
`ClipboardMsg` is already registered as a `JsonDerivedType`, so it serialises today.

**Clipboard is built** (2026-09-13) and measured: the host is on `WinSta0`, so it is the user's
real clipboard, and the clipboard is per-**window-station**, not per-desktop — `DesktopFollower`
is irrelevant to it. It is **not yet proven against a machine running the new client**: DESKTOP-
OPHQIDH is still on an older build, which ignores the new message (the session survives — that
was confirmed). `tools/clipcheck` proves it in one command as soon as the new client is on there.

**Files and folders are built too** (2026-09-13), at Clint's request, in one go rather than after
the text clipboard: copy and paste and drag-in now move whole trees in both directions.
`tools/filesetcheck` proves the engine on one machine in two seconds and caught four real
path-traversal holes the day it was written — see §8 and §10 of the handover. Dragging **out** of
the session window is deliberately not claimed, exactly as warned below.

## Proven on the wire, 2026-09-13

Clint deployed the new client to DESKTOP-OPHQIDH and both harnesses were run against it.

- **`tools/clipcheck` — 7 of 7.** Text both ways, confirmed by a separate process on the machine
  reading the same clipboard, and the host confirmed on `WinSta0`.
- **`tools/treecheck` — 19 of 19.** Files and folders both ways. Pushed a tree, had a separate
  PowerShell process hash everything that landed, pulled it back and compared with the originals,
  and checked the `CF_HDROP` file list. Empty folder, zero-byte file and `café ümläut.txt` all
  survived. It landed in `C:\Users\Lenovo\Downloads`, not SYSTEM's profile.

The first live run found a real product bug that had nothing to do with file transfer: PowerShell
5.1's encoding in both directions mangled non-ASCII in **all** script output, so the Scripts tab
had been showing nonsense for any accented name. Fixed in `ScriptRunner`; see §10.

**Drag and drop IN is proven, 2026-09-13.** A folder dragged onto the remote view arrived on the
customer's desktop, three files, SHA-256 identical, in 280 ms — verified from the customer's own
disk by a separate process, not from the sender's log. It was reported as broken only because it
was invisible; transfers now show on the status bar with a progress bar. Dragging **out** remains
unclaimed.

What is left:

1. **The audio diagnostic UI** (the measurement is done; what remains is a panel in the console
   and a `SysActionMsg`-style request the host answers).
2. **Dragging out** of the session window, if it is wanted, once someone has proven it is
   possible. Dragging **in** works.
3. A **redeploy** at some point to carry the `ScriptRunner` encoding fix to the customer machines;
   nothing depends on it urgently, and the harnesses no longer do.

---

## The decision worth making first

**"Hear the client's sound" and "diagnose why their sound is broken" are not the same feature, and
the cheaper one may be the more useful one.**

*Streaming the audio* means capturing what the machine is playing (WASAPI loopback), encoding it,
shipping it down the session and playing it at the technician's end. It is the most work by a wide
margin, and it answers "what does it sound like at their end".

*Testing the audio* means the host reporting what Windows thinks: which output device is default,
whether it is muted, the volume level, whether the device is disabled or absent, whether the
driver is in error, which app has it open — and being able to **play a test tone on the customer's
machine** and ask them whether they heard it. That is a script and a few WASAPI/Core Audio calls,
and it answers "why is there no sound", which is the question Clint actually gets on the phone.

A third of the way in between: stream a short **sample** on demand — five seconds, captured when
the technician presses a button — rather than a live stream. Much less work than continuous audio,
and enough to tell silence from distortion.

**Put this to Clint before writing any audio code.** Ask which of the three he wants first, and be
honest that live streaming is the expensive one.

### A warning that is not hypothetical

**The host runs as SYSTEM** (§4 of the handover). This work has just been bitten by exactly that:
Edge refuses to run as `NT AUTHORITY\SYSTEM` and produced no PDF and no error — see §17 and
`ReportPdfService`. Audio endpoints are per-session and per-user, and a SYSTEM process may not see
the interactive user's default playback device at all.

**Measure this before designing anything.** The tooling already exists: `tools/hostinfo` runs a
PowerShell script on the remote machine *through the product's own script execution*, so the script
runs as a child of `Host.exe` with the host's token. Write a script that enumerates audio endpoints
and reports the default device, and run it through `hostinfo` against DESKTOP-OPHQIDH. If SYSTEM
cannot see the user's audio, that changes the whole design — and finding out on day one costs an
hour, while finding out at the end costs the feature.

---

## What to build, in the order that de-risks it

**1. Verify file transfer actually works.** It is implemented but unverified by eye (§8), and drag
and drop is a new front end on top of it. Proving the plumbing first means a drag-and-drop bug is a
drag-and-drop bug. `tools/` is the place for a harness if one helps.

**2. Clipboard, text both ways.** Small, self-contained, and immediately useful — passwords, error
messages, licence keys. Route `ClipboardMsg` to `/ws/session` viewers as well as the device, or it
will fail exactly the way chat did. Decide deliberately whether it syncs automatically on focus
change or only when the technician presses a button; automatic clipboard sync surprises people and
can leak whatever the customer copied.

> **Revisited 2026-09-16.** The button-only answer was chosen and shipped, and in use it read as a
> fault rather than as a decision: "copy here, paste there" is what everyone arriving from
> TeamViewer or AnyDesk expects, and its absence gets reported as "I can't copy and paste".
>
> Narrowed rather than reversed. Automatic sync is **one direction only** — the technician's
> clipboard reaches the customer, never the other way, so nothing a customer copies lands on a
> technician's machine unasked. It fires **on window focus**, not on every copy, so a password
> copied for another customer while the session sits in the background is not shipped anywhere.
> It is **on by default, with a visible toggle** in the Clipboard tab.
>
> The original concern was leaking what the customer copied. The direction that actually needed
> guarding turned out to be the other one.

**3. Drag and drop, both directions.** Onto the session view to send a file to the customer, and
out of it to receive. WinForms drag-out of a control is materially harder than drag-in; do not
promise both until drag-out is proven.

**4. Audio** — whatever shape Clint chose above, after the SYSTEM question has been answered.

---

## Constraints carried over

- **Nothing is bought in.** No third-party remote engine. A codec library is a different question
  from a remote-control engine, but raise it rather than assuming.
- **The host runs as SYSTEM**, in the interactive session, via `SessionLauncher`. Read §4 before
  touching anything that talks to the desktop, and remember the Edge lesson before assuming a
  user-level API works there.
- **Capture and input are per-thread-desktop** and fail silently when wrong. Audio is not, but
  clipboard **is** — the clipboard belongs to a window station and a desktop, and a SYSTEM process
  on the wrong one will read or write the wrong clipboard, silently.
- **`SessionLink` does not reconnect.** If the viewer socket drops, the session window is dead —
  do not build anything that assumes it recovers.
- **Never `await` inside a `lock`** in the hub (CS1996): take the list under the lock, send outside.
- WinForms traps are in §10. F11 is handled locally by `ScreenView` and must not be forwarded.

## How to work

§12 of the handover, and everything this last session confirmed the value of:

- **Measure before changing code.** Three real bugs in the health check work were found by running
  things rather than reading them, and two of my own diagnoses were wrong until measured.
- **Build the diagnostic before the fix when a failure is silent** — and make sure the diagnostic
  output is not thrown away. `| Out-Null` on a child script hid the cause of a PDF failure for two
  sessions.
- **Test any script locally before it goes near a customer machine.**
- **Give Clint one file or one command to run**, in its own fenced block, not a list of steps.
- **Never ask Clint for a password in chat.**
- **Show, don't describe.** A screenshot or a real log beats three paragraphs.
- Commit before large or risky edits; say plainly when a theory turns out to be wrong.

## The machines

- Server: this laptop, static **192.168.1.115:8090**, run from `src/RemoteSupport.Platform` in
  Clint's own terminal. Building the whole solution fails while it holds `RemoteSupport.Shared.dll`
  — build individual projects, or copy the source tree elsewhere and build that (§17 notes how).
- **DESKTOP-L2B85US** — ticketing client "Roger Rabbit", client + unattended agent.
- **DESKTOP-OPHQIDH** — also linked to Roger Rabbit, client + unattended agent + the monthly health
  check. User is `Lenovo`. This machine had the standalone health check toolkit on it before.
- Both reachable on the LAN. Security debt on DESKTOP-L2B85US is listed in §6 and still stands.
