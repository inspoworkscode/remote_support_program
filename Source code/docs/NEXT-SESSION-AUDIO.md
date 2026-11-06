# Hearing the customer's machine

> **Status, 2026-09-13: steps 1 to 3 are done and working on real hardware.** The measurements
> below were made and are recorded in HANDOVER §18 — including the one that came out the other way
> round, which is that a **muted endpoint does not silence loopback at all**. Streaming was shipped
> to DESKTOP-OPHQIDH and confirmed working over a live session.
>
> **Step 5 (the Sound tab) was built on 2026-09-13 — see HANDOVER 18.**
>
> **What remains: the Opus question for Clint (bandwidth IS now measured — HANDOVER 18: ~47 KB/s
> of audio against 1,513 KB/s of screen with a video playing, and 0.3 KB/s with the screen idle),
> step 5 (fold `audioprobe`'s diagnostic in as the second panel) and step 6 (the microphone).**
> The brief below is kept because its reasoning still governs all three. On Opus, the number now
> exists and it does not settle the question on its own: against a busy screen audio is 3% of the
> traffic, against an idle one it is all of it. It turns on the customer's uplink, not the screen.

Written 2026-09-13. Read `docs/HANDOVER.md` first for the architecture, the decisions already
made, and the working agreements. `docs/NEXT-SESSION-IO.md` is the previous brief and is finished;
it is worth reading only for the audio measurements recorded in it.

---

## Clint's words

> I would rather build this as streaming that a technician can hear for themselves rather than
> client feedback, as we could be working on this problem during an unattended session where the
> client is not even there to test for us. It also brings us closer to features that AnyDesk and
> TeamViewer have and also opens up more avenues of revenue for people who simply want the program
> so that they have remote viewing on their home computers.

---

## This reverses an earlier decision, deliberately

On 2026-09-13 Clint was asked to choose between *diagnose and test tone*, *a five-second sample*,
and *live streaming*, and chose the diagnostic. He has since reversed it, and the reasoning is
better than the reasoning that produced the first answer:

- **A test tone needs a human in the room.** "Play it and ask whether they heard it" has no answer
  during an unattended session at eleven at night, which is exactly when these machines get worked
  on. The diagnostic was chosen because it answers "why is there no sound"; it quietly assumed
  somebody was there to close the loop.
- **Streaming is a superset for diagnosis.** Hearing crackle, or silence where music should be,
  answers what the tone test would have answered and more.
- **It is the parity item.** AnyDesk and TeamViewer both stream. `docs/ROADMAP-PARITY.md` stage 3
  has been updated to match this order.
- **It opens a different market.** Remote viewing of a home machine is a real use case where audio
  is most of the point, and it is a cheaper sell than support seats.

**Do not re-litigate this.** Live streaming is the expensive option and was described as such when
the choice was made; Clint has chosen it with his eyes open and for sound reasons.

**Keep the diagnostic.** `tools/audioprobe` already exists, already works, and already found a real
fault on its first run. It is cheap to fold into the same Audio tab as a secondary panel, and it
answers a question streaming cannot: *why* there is no sound. Neither competitor tells you that.
It is a differentiator, not a detour — but it is now second, not first.

---

## What is already measured, and what that buys

Measured on 2026-09-13 against DESKTOP-OPHQIDH, running as a child of `Host.exe` so the script's
own token *is* the host's token. Do not re-derive any of it.

| | Result |
|---|---|
| Identity | `NT AUTHORITY\SYSTEM`, **session 1**, parent `RemoteSupport.Host` |
| Can SYSTEM see the endpoints? | **Yes** — all render endpoints, states, volumes, mute |
| Can SYSTEM see the per-app mixer? | **Yes**, including `firefox (pid 5304, session 1)` — a process in the interactive user's session |
| Can SYSTEM *render* audio? | **Yes** — a 2-second tone played without error |
| Is the clipboard-style window-station trap in play? | **No.** Audio endpoints are per-session, and the host is in session 1 |

So the Edge-as-SYSTEM problem does not repeat, and the host does not need impersonation or a
helper process in the user's session. **That is the expensive risk already retired.**

**The hub needs no change at all.** `TenantHub` relays binary frames without inspecting them —
host to every viewer, viewer to host. `BinKind.Tiles = 1` and `FileChunk = 2` already share that
path, and whole-tree file transfer was added without touching the server. A new `BinKind` for
audio is a host and console change only. This is proven, not assumed.

---

## The measurements to make on day one

Rendering is not capturing. These are cheap, and finding out at the end costs the feature.

1. **Can a SYSTEM process in session 1 open WASAPI loopback capture?**
   `IAudioClient::Initialize` with `AUDCLNT_STREAMFLAGS_LOOPBACK` on the default *render* endpoint.
   A different permission and a different code path from rendering. Extend `tools/audioprobe` with
   a `--listen <seconds>` that captures and reports bytes, peak and RMS, and run it through
   `tools/runscript` the way every other probe is run.

2. **Does loopback still deliver audio when the endpoint is muted?** This matters enormously and
   the answer is not obvious. DESKTOP-OPHQIDH's speakers *are* muted, so this will be hit on the
   first real test. If muting silences the loopback stream, then "I can hear nothing" is ambiguous
   and the mute state **must** be shown next to the listen button, or technicians will chase
   ghosts. Measure it: capture, then unmute, capture again, compare RMS.

3. **What arrives when nothing is playing?** WASAPI loopback commonly delivers *no packets at all*
   rather than silence. A pipeline that waits for packets will simply stall and look broken. Decide
   whether to synthesise silence, send nothing and let the console hold, or send a keepalive.

4. **What is the device's mix format?** Almost always 48 kHz float32 stereo. Everything downstream
   depends on it, and it can change under you when the user switches device.

---

## Design, with recommendations

### Capture, on the host

A dedicated thread, like capture, cursor and input (§4). Not `Task.Run`: these threads carry state
that async continuations lose. Audio is **not** per-desktop, so no `DesktopFollower` — but do
handle the default device changing mid-session (`IMMNotificationClient`, or poll and re-open).

### Format on the wire — start with raw PCM

Bandwidth, for the decision:

| Format | Rate |
|---|---|
| 48 kHz stereo float32 (what the device gives) | ~384 KB/s |
| 48 kHz stereo 16-bit | ~192 KB/s |
| 24 kHz **mono** 16-bit | ~48 KB/s |
| Opus at 32 kbit/s | ~4 KB/s |

**Recommendation: 24 kHz mono 16-bit PCM first.** It needs no library at all, it is perfectly good
enough to tell silence from music from crackle, and it proves the whole pipeline — capture,
transport, buffering, playback — before a codec is introduced. On a LAN it is nothing. Over the
internet, 48 KB/s alongside screen tiles is the point at which a codec starts to earn its place.

**Then Opus, if it earns it.** That is a decision to put to Clint rather than to assume, because
of the "nothing is bought in" rule. A codec is arguably a different question from a remote-control
engine — we are not buying a remote engine, we are avoiding writing a psychoacoustic codec — but
it is his call. The realistic options are native `libopus` (a DLL to ship and sign) or
**Concentus**, a pure C# port (no native dependency, slower, MIT). Raise it; do not just add it.

### Transport

A new `BinKind.Audio = 3`, framed the way the others are:
`[3][seq:u32][sampleRate:u32][channels:u8][format:u8][pcm…]`. Carrying the format in the frame
means a mid-session device change does not need a separate message and cannot arrive out of order
with the data it describes.

Watch the interaction with screen tiles: they share one socket and one send channel. Audio frames
are small and frequent, tiles are large and bursty. A 400 ms tile burst must not stall audio into a
gap the listener hears. Measure this before tuning it.

### Playback, on the console

`waveOut*` from `winmm` by P/Invoke needs no dependency and is easily good enough; WASAPI shared
mode is the better long-term answer. Either way the real work is the same:

- **A jitter buffer**, ~200 ms. Too small and every hiccup clicks; too big and it feels laggy.
- **Clock drift.** The customer's capture clock and the technician's playback clock are not the
  same. Over a long session the buffer drifts to empty or to full. The cheap fix is to drop or
  duplicate a packet when the buffer crosses a threshold; the proper fix is resampling. Start
  cheap, and *measure the drift over a twenty-minute session* before deciding it needs more.

### UI

- A **speaker toggle in the session toolbar**, not buried in a tab.
- A **level meter**, so a technician can see audio arriving even through headphones they are not
  wearing. This is the single most useful signal for "is it working".
- **The endpoint's mute and volume shown next to it**, for the reason in measurement 2 above.
- Status and errors on the **status bar**, which is where transfers now report. The
  drag-and-drop lesson from 2026-09-13 applies directly: a feature with no visible feedback is
  indistinguishable from a broken one, and will be reported as broken.

---

## Order of work, in the order that de-risks it

1. **Measure loopback as SYSTEM**, and the muted-endpoint question. Extend `tools/audioprobe`.
   Nothing below is worth designing until this is known.
2. **Raw PCM end to end**, LAN only, no codec, no polish. Host captures, console plays.
3. **Prove it with a harness, not by ear.** The host plays a known 1 kHz tone (`audioprobe --tone`
   already does this) while the console records the received stream; the harness then checks the
   captured audio for a 1 kHz peak — a Goertzel filter is a dozen lines and needs no FFT library.
   That turns "I think I heard something" into a pass or a fail, and it will still work at two in
   the morning with the speakers muted at this end. This is the `treecheck` pattern applied to
   sound, and it is what makes the feature provable.
4. **Then bandwidth**: measure it alongside screen streaming, and only then decide about Opus.
5. **Then the UI**, including folding `audioprobe`'s diagnostic in as the second panel.
6. **Microphone the other way**, only if it earns its place. The phone already works.

---

## Open questions for Clint

- **Opus, or stay with raw PCM?** Ask once the bandwidth is measured, not before, and frame it as
  the "nothing is bought in" question it is.
- ~~**Should the customer be told when a technician is listening?**~~ **Decided 2026-09-13: yes.** Built, shipped and confirmed working on real hardware — see HANDOVER 18. Original reasoning: An unattended session that can
  hear the room's music can also hear a speakerphone call. The tray app already exists and already
  shows session state; an indicator is cheap. This is worth deciding deliberately rather than
  arriving at by default, and it is the kind of thing a reseller's customers will ask about.
- **Does the home-viewing market change anything structurally?** Clint raised it as a revenue
  reason. If someone is watching their own home machine, audio is most of the point and support
  ticketing is none of it. That may be a different SKU rather than a different product, but it is
  worth a decision before the pricing page is written.

---

## Constraints carried over

- **The host runs as SYSTEM**, in the interactive session, via `SessionLauncher` (§4). Audio is
  per-session and the host is in session 1, which is why this works — but remember the Edge lesson
  before assuming any other user-level API works there.
- **`SessionLink` does not reconnect.** If the viewer socket drops the session window is dead; do
  not build an audio path that assumes recovery.
- **Never `await` inside a `lock`** in the hub (CS1996).
- **Attach every `WsConnection.OnMessage` handler before `RunAsync`** — one-shot messages arrive in
  the first milliseconds and a late handler misses them for good (§10).
- **A new session has no host for a second or two**, and the hub relays with `s.Host?.Send(m)`,
  which is a silent no-op. Wait for evidence of the host before sending.
- WinForms traps are in §10. F11 is handled locally by `ScreenView` and must not be forwarded.

## How to work

§12 of the handover, and everything the last two sessions confirmed:

- **Measure before changing code.** Every real bug in this work was found by running something,
  not by reading it — including four path-traversal holes that a two-second local test caught, and
  a device list that was one pixel high.
- **Build the diagnostic before the fix when a failure is silent**, and never throw the evidence
  away. `catch {}` and `| Out-Null` have each cost more than a day here.
- **A feature with no visible feedback will be reported as broken**, because it is
  indistinguishable from broken. Drag and drop worked perfectly for a fortnight of nobody trying it
  and was reported as failing the first time it was used, because it finished in 280 ms with its
  only feedback on a hidden tab.
- **Do not let a transfer mark its own homework.** Verify from the other machine's disk, through a
  separate process, the way `treecheck` and `clipcheck` do.
- **Test any script locally before it goes near a customer machine.**
- **Give Clint one file or one command to run**, in its own fenced block.
- **Never ask Clint for a password in chat.**
- Commit before large or risky edits; say plainly when a theory turns out to be wrong.

## The machines

- Server: this laptop, static **192.168.1.115:8090**, run from `src/RemoteSupport.Platform` in
  Clint's own terminal.
- **DESKTOP-OPHQIDH** — the test machine. User is `Lenovo`. Client + unattended agent + the monthly
  health check. **Its speakers are muted and its default output has been an HDMI television**,
  which makes it an unusually good audio test subject and an unusually confusing one.
- **DESKTOP-L2B85US** — ticketing client "Roger Rabbit", client + unattended agent.
- `tools/ship-build.ps1` puts a new build on the server's download and into `demo\technician`;
  `deploy-client-payload.ps1` alone is **not** enough, because it refreshes the installer scripts
  but not the exes.
