# Roadmap — closing the gap on TeamViewer and AnyDesk

Written 2026-09-13. **Loaded at the start of every session** via `CLAUDE.md`. This is the running
order for the product as a whole; `docs/HANDOVER.md` is the state of the build and always wins on
matters of fact. When a stage item is finished, mark it here **and** record the result in the
handover — this file says what to do next, the handover says what is true.

---

## The position, in one paragraph

As a remote-control engine we are roughly 70% of AnyDesk's feature surface and behind on transport
and codec. As a *support business platform* — ticketing, client records, machine inventory,
automated monthly health checks, reseller branding — we have things neither product offers without
buying a second one. The gap that has to close is the first column; the reason anyone buys is the
second. Nothing in this roadmap touches the second column, because it is already ahead.

## Why this order

Clint set it: **core remote control, then codec, then audio streaming. Then hosting, digital
signatures, and the other platforms.**

That order is right for a reason worth writing down. Stages 1–3 are all *ours* — Windows, C#, our
own protocol, no external dependency, no money, no waiting on anyone. Stages 4–6 need a host to be
paid for, a certificate authority to issue an EV certificate, and entire new codebases in other
languages. Doing the cheap, fully-controlled work first means the product is worth shipping by the
time the slow work starts, and the slow work blocks nothing in front of it.

**One exception, and it is not negotiable.** TLS (stage 4) is the only thing standing between this
and a real customer. If a paying customer appears before stage 3 is done, TLS jumps the queue and
nothing else does. Everything else in stage 4 can wait its turn.

---

## Status at a glance

Updated 2026-09-29 (3.6.8 done; the 3.7 row caught up with §44–§49). Updated 2026-09-18 (stage 3.5 added). Written 2026-09-17 against HANDOVER §8, §18, §23, §24 and §25 and against the code, rather than
against this file's own memory.

| Stage | What it is | State |
|---|---|---|
| 1 | Core remote control parity | **DONE, and proven on hardware 2026-09-16.** Five harnesses pass against DESKTOP-OPHQIDH (reconnect 7/7, clipboard 7/7, files 19/19, audio, fetchfile); the Files tab opens on the customer's desktop unprompted, a real drag out lands and opens, and the tray app *and* the ticket both carry the disclosure line. BUGS #5 and #6 closed. Nothing outstanding. See §22. |
| 2 | Codec and transport quality | **2.1 DONE 2026-09-16. 2.2 FIXED AND WORKING 2026-09-17 -- it encodes: scrolling 2785 to 198 KB/s, video 2619 to 186 KB/s, CPU 12.7% to 1.0%, at 1.0/255 against the screen. Two swapped interop constants told the encoder the stream had ended. PROVEN OVER A REAL SESSION on DESKTOP-2N6FVGT the same day: codeccheck 8 of 8, first picture in 585 ms, and the decoded screen looked at rather than counted. 2.3 (adaptive quality) is DONE the same day, floor and controller, though nobody has yet watched it back off on a genuinely bad link. 2.4 (the real cursor) is DONE and PROVEN ON HARDWARE the same day: codecbench --cursor 31 of 31, codeccheck 11 of 11 against DESKTOP-2N6FVGT with that machine's own arrow decoded here and looked at. STAGE 2 IS COMPLETE.** `tools/capturecheck` 11 of 11 against DESKTOP-OPHQIDH as SYSTEM, including the UAC prompt captured and legible. Desktop Duplication replaced GDI BitBlt: idle went from 30% of a core to nothing, a scrolling document from 71.6 to 21.3 ms a frame. 2.1 could not move bandwidth and said so; 2.2 is what moved it. See HANDOVER §24 and §25, `docs/NEXT-SESSION-CODEC.md` and `docs/NEXT-SESSION-ADAPTIVE.md`. |
| 3 | Audio | **largely done, out of order and deliberately** — 3.1 and 3.2 built and proven on real hardware; 3.3 and the Opus question remain |
| 3.5 | Simplicity: the console gets out of the way | **IN PROGRESS. 3.5.1 IS FINISHED 2026-09-21 AND PROVEN ON HARDWARE: files now paste on the customer's machine.** Explorer will not paste from a file list owned by a process above it, and the host is SYSTEM; the host now hands that one write to a short-lived copy of itself running as the person at the machine. `userpaste` 4 of 4. **Copying FROM the customer is DONE AND PROVEN the same day (§40)**: a real Ctrl+C there leaves a delay-rendered promise that does not cross to another account, so the host reads it through a helper running as the person; and the same work found a LEAK -- the clipboard kept crossing after the technician had gone, because a subscription was never cleared when a viewer disconnected. Both fixed. `usercopy` 3 of 3, `clipcheck` 16 of 16 with both leak tests. Needs a PLATFORM RESTART as well as a full ship. **HANDOVER §39 supersedes §27**, which is kept for the six real faults it fixed and for the lesson: ask the question Explorer asks, from where Explorer stands. The "ding" is still unexplained. 3.5.4 IS DONE AND CONFIRMED ON THE REAL CONSOLE (the side panel is gone, every tool is its own window, toolbarprobe 37 of 37 — HANDOVER §28). 3.5.5 IS DONE AND CONFIRMED ON HARDWARE the same day (the customer chat popup is movable and stays where it is put, chatprobe 23 of 23 - HANDOVER 29); it needed a FULL ship, because it changes the client. 3.5.6 and 3.5.7 are DONE the same day (the tray menu is Request Support, Chat with a Technician and Exit; a mandatory description, read first on the alert — callprobe 25 of 25, HANDOVER 30) and need a FULL ship, not yet on hardware. 3.5.3 was built inside 3.5.1 and made true 2026-09-29 (HANDOVER §56). 3.5.2, dragging files off the picture, is BUILT 2026-09-29 and not on hardware (HANDOVER §58) — every item of 3.5 is now built. **CONFIRMED BY HAND ON HARDWARE 2026-09-21: "copy and paste is now working both ways". 3.5.1 IS CLOSED.** |
| 3.6 | The dashboard and ticketing system earn their keep | **BUILT 2026-09-20, all seven items, NOT on hardware.** Contacts; the console home as Attended/Unattended tabs under the customer who owns each machine; technicians signing in to the portal at last, with the console handing them a one-use link; roles enforced in the service and not in markup; three workshop photos in and three out; and every remote session getting its own ticket, with jobs made of sessions. Six harnesses: contactcheck 34, dashtabs 47, handoffcheck 42, ticketscope 26, photogate 36, ticketpersession 28. HANDOVER §32–§36. **3.6.9 (a light or dark each person owns) is DONE too — themecheck 40 of 40, HANDOVER §38. 3.6.8 (roles as tick boxes) is DONE 2026-09-29 — rolecheck 72 of 72, HANDOVER §55. STAGE 3.6 IS BUILT IN FULL.** |
| 3.7 | A workflow per category | **ALL FIVE BUILT 2026-09-24 to 2026-09-28, NOT on hardware; 3.7.1 (delivery, several machines at once) BUILT 2026-09-30.** Workshop, Call out, Walk in, Remote support and General each have their own route, written by Clint into `docs/NEXT-SESSION-WORKFLOWS.md`; the console's own ticket window is back; several people can be on one ticket. HANDOVER §44–§49. |
| 3.8 | The office talks to itself; a customer rings from a browser; a website chat bubble | **ALL THREE BUILT 2026-09-30, NOT on hardware.** Office chat (one to one and Everyone, portal and console), support in the browser (a public link that rings the technicians; the customer shares their screen, view only), and website chat (a bubble on the company's own site that rings everybody signed in; first reply takes it; a message becomes a ticket when nobody is there). HANDOVER §65, §66, §70. **3.8.4, tickets by email and a contact form, added and being built 2026-10-05** (HANDOVER §79); **Microsoft 365 sign-in for it built 2026-10-19** (§151). **3.8.5, a knowledge base, added and being built the same day** (HANDOVER §80). |
| 3.9 | Somebody from outside the company, let in by the customer with a code: a guest (no account; a guest console or a browser) or another company on the platform; the local technicians watch and can end it | **Added 2026-10-06. ALL FOUR STEPS BUILT the same day, NOT on hardware** (the switches and the code, §87; another company, §89; a guest with the guest console, §90; a guest in a browser, §91; files on the customer's tick, §92). Built in full. Clint's four answers recorded in the stage. **2026-10-22: a TECHNICIAN can ask for it too** — Tools → Get outside help in a session (3.9.3, §155), NOT on hardware. |
| 4 | Hosting, reach, production security, seats and money | **not started.** The domain resolves again, but to xneelo shared hosting serving a holding page — there is no platform server. The previous one was lost outright; see `docs/HOSTING-DNS.md`. 4.6 auto-update, 4.7 self-service seats and 4.8 PayFast all added 2026-09-16 - the commercial model now lives in `docs/PRICING.md`. |
| 5 | Code signing | build wired 2026-09-16; waiting on the certificate — still blocks half of 4.6 |
| 6 | macOS, Linux, Android, iOS | not started |

**Not a stage, done anyway (2026-09-20): unattended access is a switch the customer holds.**
It used to mean "the agent service is connected", decided once at install time by whoever held
the mouse and unchangeable afterwards without another visit. It is now two facts — an agent is
connected AND the person at the machine has allowed it — with the permission stored and enforced
on the server, a checkbox in the tray app, and the technician's Access column flipping live.
Every install now installs the service (it does nothing without permission); `-Unattended` means
"and tick the box for them". `unattendedcheck` 32 of 32. **See HANDOVER §31**, including the two
traps: a customer's own call must still be allowed to use the agent, and only the CLIENT
connection may change the permission. **CONFIRMED ON HARDWARE 2026-09-20** — Clint, after the
platform restart §33 asked for: *"the unattended switch works."* The greyed-out tray item was a hub
race whose fast path lost every single time, and `tools/unattendedwire` now covers the half no
stand-in server could. Still unwatched by eye: the console's Access column flipping live.

**Not a stage, done anyway (2026-10-02): an unattended password** — TeamViewer's and AnyDesk's
"unattended password", in our shape. Set in the tray app at the machine; with unattended access on, a
technician-started connection needs it, the owner's included, and nobody can join a session opened
with it without typing it. For somebody reaching their own machine from the office. `unattendedpw`
33 of 33. **See HANDOVER §77.** Not on hardware.

**Not a stage, done anyway (2026-09-20): Devices is everybody's, and the console can link one.**
Saying which customer a machine belongs to used to be the owner's job alone — `/portal/devices`
was admin-only and there was no console route at all. That is the wrong person: until a machine is
linked its tickets never reach a client page, and whoever notices the stray is a technician
looking at the list. `Permissions.CanLinkDevices` answers it as a question rather than a role
comparison (the 3.6.8 shape), the page policy widened to every role, and the hub learned
`DeviceLinkMsg` so a right-click on the console's machine list can do it. `tools/devicelink`
**19 of 19, and proved to drop to 15 with the old policy put back.** See HANDOVER §34, including
the harness weakness it found — a refused page redirects to the sign-in form and arrives as a
cheerful 200, so the first version passed against the very fault it existed to catch.

**Not a stage, done anyway (2026-09-16): per-tenant appearance.** An IT company chooses Light or
Dark for the console and the client app separately, previews both while editing their branding, and
gets it baked into their installers. On dark the signature colour is the tenant's own accent. It sits
alongside the reseller-branding column the roadmap already says is ahead, and it is what surfaced
4.6. See `docs/BRANDING.md`.

**Stage 3 ran before stage 2 on purpose.** Clint reversed the audio order on 2026-09-13 and the
reasoning is recorded in `docs/NEXT-SESSION-AUDIO.md`. It is not a slip in the plan.

---

## Stage 1 — core remote control parity

Everything here is Windows C# in code we already own. No new dependencies, nothing to buy. This is
the stage that makes a demo stop having visible holes in it.

**1.1 Prove the file-set wire path. — DONE 2026-09-13.** `tools/treecheck`, 19 of 19 against
DESKTOP-OPHQIDH: pushed a tree, had a **separate PowerShell process on that machine** hash
everything that landed, pulled it back, compared with the originals, and checked the `CF_HDROP`
list on the customer's clipboard. It landed in `C:\Users\Lenovo\Downloads`, not SYSTEM's profile.
HANDOVER §8.

**1.2 Prove the clipboard against a machine running the new client. — DONE 2026-09-13.**
`tools/clipcheck`, 7 of 7. The point of it stands: it verifies a *separate process* on the customer
machine sees the text, which is the only way to tell a shared clipboard from a private one.

**Drag IN — DONE 2026-09-13**, and worth reading before 1.3. A folder dragged from the technician's
desktop arrived on the customer's desktop with all three files identical by SHA-256, in 280 ms. It
was reported as "not working" only because it was **invisible** — the only feedback was a label on
a hidden tab. Transfers now show on the session window's status bar with a progress bar that stays
up for twenty seconds. A feature with no visible feedback is indistinguishable from a broken one.

**1.3 Drag out — BUILT 2026-09-15, proven by `tools/dragoutcheck` (39 of 39) on this laptop, and
CONFIRMED ON A REAL MACHINE 2026-09-16** — a file dragged from the Files tab onto Clint's own
desktop arrived and opened. That is the part no harness can perform, because the drop is Windows
handing a `CF_HDROP` to Explorer. **See §22 of the handover.** The brief was
`docs/NEXT-SESSION-FILES.md` and it covered more than this item: the Files tab listed nothing when
opened, and `%DESKTOP%` was resolved only for drag-in. Moving things between the two machines is
one job, so it was briefed and built as one.

The ordering was the whole difficulty, and it was never a WinForms one. `DoDragDrop` hands the
drop target a `CF_HDROP` — **paths, not bytes** — so the files have to be on the technician's
machine before the drop completes, or Explorer copies from paths to nothing. That is the problem
paste already solved, so drag-out reuses it: fetch the set into a staging folder under `%TEMP%`,
then hand the shell those paths. A drag released before the fetch finishes puts them on the
clipboard instead, because a technician who has waited must not be told to do it again.

The drag starts in the **Files tab's list**, not on the picture of the remote desktop: the console
knows what is selected there, whereas grabbing an icon on the remote screen would need the host to
hit-test its own desktop and report what is under the cursor. That is a separate piece of work and
is not needed for the job this was for — getting a log, a screenshot or a backup off a machine.

**1.4 Auto-reconnect — DONE 2026-09-13. See §19 of the handover.** The account below is what it
looked like beforehand, and the part worth keeping is that it turned out to be a SERVER change
before a client one: the hub ended the session the instant the last viewer socket closed, so the
"client-side retry loop" described here could never have worked on its own. A dropped packet and
"Leave / End" were the same event. The server holds a session for 90 s now, and a deliberate end
still ends it at once. **Proven by `tools/reconnectlocal`, 18 of 18 with no customer machine, and by
`tools/reconnectcheck`, 10 of 10 against DESKTOP-OPHQIDH.**

*What follows is the original entry, kept because the trap it names is still live.* `SessionLink`
did not reconnect: it connected once, and when the receive loop ends it fires `OnConnectedChanged(false)`
and returns. There is no retry anywhere. Meanwhile `SessionForm.cs:97` sets the pill to
**"○ reconnecting"** at that exact moment — the UI states that something is happening which no code
performs. The session id already survives on the server, so this is a client-side retry loop plus a
re-subscribe, but note that `MonitorsMsg` is the **only** thing that triggers the console's first
`SubscribeMsg`, and the hub replays it to each new viewer, so a reconnected viewer must attach its
handlers before the pump starts or it will reconnect to a permanently black screen. That trap has
already cost an hour once.

Three things converge here, which is why it is next rather than fifth: it **blocks 1.5** (a reboot
button that kills the session window is worse than no button); it is very likely **BUGS #2**, whose
leading hypothesis is that nothing re-sends state to a device that reconnects — `DeviceLink` *does*
auto-reconnect on the customer side, so that is the same missing capability seen from the other
end; and every brief since has carried "SessionLink does not reconnect" as a **standing constraint**
on what may be built.

**1.5 Wire the system actions — DONE, and PROVEN ON A MACHINE 2026-09-15.** A System menu on the
session toolbar carries Ctrl+Alt+Del, Open Task Manager, Lock and Restart, and
`tools/sysactioncheck` is the harness this item asked for.

It was nothing like as cheap as this predicted, and the whole story is in §21. In short:
Ctrl+Alt+Del took three attempts (synthesised keystrokes are discarded; `SendSAS` needs a SERVICE,
not merely SYSTEM, so it moved to the agent; and it is gated on the `SoftwareSASGeneration` policy,
which defaults to OFF and makes the call succeed while doing nothing). The restart hold also had to
stop reconnecting five seconds after the reboot, while the machine was still up and going down.

**1.6 Session recording. — BUILT 2026-09-29, not on hardware; HANDOVER §61.** A Record button (Tools menu, red marker on the status bar), recorded on the console as the brief below says, uploaded to the session's ticket, the customer told in the tray app and on the ticket. `recordcheck` 11 of 11, `sessionextras` 26 of 26. The original brief: Both competitors have it, and for us it is also an audit trail worth
having on a multi-tenant product. The tile stream is already a sequence of timestamped JPEGs —
record the frames as received on the *console* side, not the host side, so it costs the customer
machine nothing and captures exactly what the technician saw.

**1.7 Privacy screen (blank the remote display). — BUILT 2026-09-29, not on hardware; HANDOVER §60.** Black windows excluded from capture, refusing rather than half-working; proved against both capture paths with a control that captures black (`privacyprobe` 14 of 14). The original brief: Standard for anyone working on a machine in an
open office. Needs care: if it fails silently the customer is looking at their own desktop while
believing they are not, which is worse than not having the feature. Prove it with a screenshot
taken on the customer machine, not by eye.

**1.8 Remote printing and Wake-on-LAN.** *Taken up 2026-10-06 by Clint's decision.* **Wake-on-LAN is BUILT (HANDOVER §94), not on hardware**: right-click an offline machine, Wake up; another of the same customer's machines on that network sends the wake-up. **Remote printing is BUILT too (HANDOVER §95), both directions, not on hardware**: Tools → Print on my printer (their prints come out on the technician's), and Print a file on their printer. **Roadmap 1.8 is now built in full.** The original note: Both real gaps, both low frequency for a one-man IT
business. Explicitly deferred to the end of this stage, and droppable if stage 2 starts looking
more valuable — which it probably will.

**Done when:** a session against a refreshed client does files, folders, clipboard and drag in both
directions by checksum (**done, except drag out**); a dropped socket recovers on its own
(**1.4, next**); and the console can reboot, lock and Ctrl+Alt+Del the machine (**1.5**).

---

## Stage 2 — codec and transport quality

This is the stage a prospect who has used AnyDesk will judge us on, and the one that is currently
weakest. It is also the only stage where the honest answer today is "ours looks worse".

**What we have:** GDI `BitBlt` to 128 px tiles, FNV hash change detection, JPEG per changed tile
(`ScreenCapture.cs`), quality a manual slider and FPS pinned at 12 (`SessionForm.cs:103`). On a LAN
this is fine. Over ADSL or LTE, on video, on scrolling text, it will not be.

**What they have:** AnyDesk's DeskRT and TeamViewer's codec are purpose-built for desktop content
with hardware encode, and both adapt to the link continuously.

**2.1 DXGI Desktop Duplication instead of GDI — DONE and PROVEN ON HARDWARE 2026-09-16.** Built as
described below, including the fallback. `tools/capturecheck` is 11 of 11 against DESKTOP-OPHQIDH
with the agent hosting as SYSTEM: duplication runs as SYSTEM, the picture is right, it survives the
Winlogon desktop, and **the UAC prompt itself is captured and legible** — which answers the
protected-content half too. The one thing still unobserved is the GDI fallback actually firing in
the field; duplication has never been refused on either machine. §24 of the handover has the
detail, including the forced-full bug that only looking at the picture found.

Measured with `tools/codecbench`, both paths against the same scene at 1920x1200: idle **64.2 ms a
poll and 30% of a core became 0.3 ms and nothing**; a scrolling document 71.6 → 21.3 ms a frame with
a ceiling of 14.9 → 45.1 fps; video 89.1 → 24.1 ms with a ceiling of 12.0 → 22.4 fps; a full frame
on subscribe 107 → 28 ms.

**The finding that reorders the rest of the stage: bandwidth did not move, and could not have.** A
scrolling document costs **2.8 MB/s** on either path — the tiles and the JPEG are the same. Speed
was the capture; bandwidth is the codec. **2.2 is now the whole of what is left in this stage**, and
the 12 fps cap is no longer what is holding the picture back.

One trap, recorded because it cost a ten-times bandwidth regression that nearly shipped: a dirty
rectangle means Windows **redrew** those pixels, not that they came out different. Trusting them
sent 150 tiles a frame where 24 had changed. They narrow the search; the survivors still get
compared.

*The original entry:* GDI BitBlt is slow, burns CPU, and cannot capture
some fullscreen and protected content — which is why a black screen has been reported more than
once. Desktop Duplication hands us the dirty rectangles Windows has already computed, so change
detection stops being our problem. Keep the GDI path as a fallback: Duplication is unavailable in
some session and driver states, and losing capture entirely is a worse bug than a slow one.

**2.2 H.264 with hardware encode. — BUILT 2026-09-16. DIAGNOSED AND FIXED 2026-09-17. It encodes.**
A whole 1920x1200 screen in **10.9 KB**, decoded and matching the screen at **1.0 of 255**. Against
the JPEG tile path on the same screen at 12 fps: scrolling **2785 → 198 KB/s**, video
**2619 → 186 KB/s**, CPU **12.7% → 1.0%**, a full frame on subscribe **321 → 122 KB**. The number to
beat was 2817 KB/s.

Two faults, one hiding the other. `MFT_MESSAGE_NOTIFY_START_OF_STREAM` and `..._END_OF_STREAM` were
**swapped** in our interop, so starting the stream told the encoder it had ENDED — `S_OK`, then
silence, on every encoder, which is the whole of `events 0`. With the pump alive, the first
`ProcessOutput` returned `MF_E_TRANSFORM_STREAM_CHANGE` — the normal way a hardware encoder settles
its output type — and `Encode` declared that flag and never read it, so the first frame was dropped
and every one after it refused. Both fixed, both proven by `codecbench --h264check`.

**PROVEN OVER A REAL SESSION the same day**: `tools/codeccheck` against DESKTOP-2N6FVGT running the
rebuilt host, **8 of 8**. The host chose H.264 via Quick Sync at 819 kbit/s, the first picture
arrived in 585 ms, frames followed a change on screen (8 in 3 s, the stream is live rather than
stuck on a key frame), and **the decoded picture was looked at** — that machine's desktop, legible
down to the window text and the taskbar clock.

Two things it does NOT prove. The 20-second A/B in that run was against an **idle** screen, so both
codecs read ~0 KB/s and the harness says so rather than claiming a win; the 14x stands on the bench,
not on that session. And the **NVIDIA activation failure on that Optimus machine is untouched and
still real**.

**Not a stage item, built 2026-09-17: giving the screen back.** The host now releases its Desktop
Duplication — and the encoder, which is built on the duplication's D3D device and must go with it —
after three seconds with nobody subscribed. Duplication is *exclusive per output*, so an idle
session was locking every other program on the machine out of a screen nobody was looking at,
including our own diagnostics: that is what made the codec impossible to test remotely, and it is
how the behaviour was noticed. Proven in the host's own trace, 3.016 s after acquiring it.

*The diagnosis that got here (superseded, kept for the reasoning):* Parked that morning
for want of hardware and answered the same afternoon on a Kaby Lake machine with a working Intel
HD 630 encode engine and an NVIDIA GTX 1050. Quick Sync activates, unlocks, takes the device and
both media types, reports streaming started — and raises **`events 0`**, the identical signature to
the Alder Lake-N laptop. **The missing-silicon theory is dead**: the same code fails the same way on
silicon that has the engine, which is our bug reproducing rather than two unlucky machines.

The diagnostics also narrow it. `last GetEvent: no event queued` is a well-formed
`MF_E_NO_EVENTS_AVAILABLE`, so the event-generator QI and the vtable slot — the first two suspects
in the old list — are unlikely. The leading candidate is now that the shared Direct3D device is
never marked multithread-protected. **Separately, the NVIDIA encoder would not activate at all
(`0x8000FFFF`) on that Optimus machine**, because duplication runs on the Intel adapter and the
NVIDIA transform will not take an Intel device. That is a real and commercially relevant second
problem — gaming laptops are exactly what customers call about — and it is not the pump.

`docs/NEXT-SESSION-CODEC.md` has the evidence, the eliminated suspects and the order to try the
rest. §25 of the handover is the earlier record and its conclusion is now superseded. `src/RemoteSupport.Media` holds hand-rolled Media
Foundation interop, the encoder, the decoder and the GPU colour conversion; `SubscribeMsg.Codecs`
negotiates it and `BinKind.Video` carries it; **the server was not changed at all**, as predicted.
The frame goes from Desktop Duplication to the encoder without leaving the graphics card.

What IS proven: everything the encoder stands on, measured on this laptop at **0.5 of 255** against
the screen (`codecbench --mfpipe`), and backward compatibility with a pre-2.2 host over a real
session (`codeccheck` against DESKTOP-OPHQIDH). The YUV matrix is measured rather than assumed, and
the first attempt at measuring it was wrong in a way worth reading about.

What is NOT: the encoder, the decoder and the picture end to end, and above all **the asynchronous
pump** — the part that talks to Quick Sync, NVENC and AMD VCE. This laptop lists Intel Quick Sync
Video H.264 Encoder MFT, and it activates, unlocks, takes the device and both media types, reports
streaming started, and then never asks for a frame; the Microsoft AVC DX12 Encoder refuses the
output type with `E_FAIL`. Both are consistent with an Alder Lake-N GPU that has no H.264 encode
engine while its driver registers the transform anyway — but an encoder that never asks for input
looks the same whether the silicon is missing or our event loop is wrong, and **nothing measurable
here tells those apart.** 2.8 MB/s is still the number to beat and it has not yet been measured
against.

One thing this stage nearly shipped is worth reading in §25: the video encoder category GUID was
wrong, `MFTEnumEx` answers an unknown category with `S_OK` and zero results, and the result was a
fully documented conclusion that this machine had no encoder at all. The category GUIDs are
readable out of the registry and now say so in the code.

*The original entry:* 2.8 MB/s is
the number to beat. Two things 2.1 left ready: the D3D11 device is created with `BGRA_SUPPORT` so an
encoder can share it, and the duplicated frame is already a GPU texture — getting it to the encoder
without ever bringing it to the CPU is the prize, and the readback in the current code exists only
because JPEG-per-tile demands one. Media Foundation reaches Intel Quick Sync, NVENC and AMD VCE.
Add a third `BinKind` for encoded video — the hub relays binary frames without inspecting them
(`TenantHub.cs:480`), so **this needs no server change at all**, only host and console. Negotiate
it: fall back to the JPEG tile stream when the encoder is unavailable or the far end is an older
build.

**2.3 Adaptive quality. — DONE 2026-09-17, with one gap named below.** Both halves: the floor (the
send queue no longer destroying picture under load) and the controller (`LinkController`, which
chooses quality and frame rate from the queue depth and the refusal count). `codecbench --adapt` is
13 of 13 against scripted link conditions — falls in 2.1 s, climbs back in 15.4 s, holds dead still
for 32 s in the band between its two marks, and its two ladders diverge as intended: a moving
picture gives up frame rate first, a still one gives up quality first. On DESKTOP-2N6FVGT it proved
the property that mattered most by doing **nothing** — one trace line for a whole session, no
hunting on a healthy link, with `codeccheck` 8 of 8 beside it.

**The gap, and it is the important one: nobody has watched it back off on a genuinely bad link.**
Everything proven is scripted adversity or a healthy LAN. The thresholds (512 KB to back off, 64 KB
to recover) are chosen from the old tile stream's cost, not measured against the depth at which
latency becomes visible to a technician. Until a session over a phone hotspot on one bar has been
watched, the back-off is a controller with a good argument rather than one known to help.

*The brief, kept:*
`docs/NEXT-SESSION-ADAPTIVE.md` is the brief. Measure round-trip and queue depth and move quality
and frame rate on their own. Keep the manual slider as an override; a technician on a bad link often
*wants* it ugly.

**Why it moved.** Two things that were not visible when the order was set. The tile path is **not**
interim — 2.2 negotiates it as the permanent fallback for every machine without a hardware encoder,
which is exactly the class of machine that parked 2.2. And with 2.2 blocked, quality and frame rate
are the only remaining lever on the 2817 KB/s this stage exists to fix; both are already on the wire
in `QualityMsg` and already obeyed by the host, so it needs no protocol change and no server change.

**It also has a real fault to repair underneath it**, found while briefing it and not yet filed as a
bug because nothing has observed it in the field: `WsConnection`'s send channel is bounded at 1024
with `DropOldest`, and the capture paths record what the far end has at **capture** time, not at
**delivered** time. So a screen frame evicted under load is marked as delivered and never resent,
and the stale rectangle persists until that region changes again on its own. It is a mechanism that
certainly produces the "stale picture" symptom; whether it caused any report we have had is unknown
and should not be assumed.

**2.4 Cursor — DONE 2026-09-17, and PROVEN ON HARDWARE the same day.** The technician sees the
customer's **real pointer**, not a yellow ring: the arrow, the I-beam, the hand, the busy spinner, at
the right size, with the hot spot on the right pixel. `codecbench --cursor` is 31 of 31, and
`codeccheck` against DESKTOP-2N6FVGT is **11 of 11** — 30 pointer updates, the shape decoded, and
**the arrow looked at**: `docs/evidence/2026-09-17-cursor-from-DESKTOP-2N6FVGT.png` is that
machine's own pointer, captured there and decoded here.

**The hardware run found a fault no harness could have.** The first attempt reported zero pointer
updates. The loop was working perfectly — the trace showed it polling and sending the shape 0.2 s
after the host started — but **nobody was at that machine, so the pointer never moved**, and the
"only send when something changed" rule therefore sent exactly ONE message, before the viewer had
finished attaching. It was lost and nothing sent another.

That is not a test artefact. The shape travels once per session, so **a technician whose socket
dropped and reconnected (stage 1.4) would have lost the pointer for the rest of the session**, and a
viewer joining a session in progress would never have received the shape at all. Fixed twice over: a
`SubscribeMsg` makes the pointer re-introduce itself, clearing the sent-shapes cache so the bitmap
goes again, and a one-second heartbeat sits underneath as the backstop — about 80 bytes a second on
a completely idle machine.

**The lesson is about the change, not the cursor.** A send path was optimised from "always" to "on
change" without asking what happens to a receiver that arrives BETWEEN changes. And the cursor path
traced nothing at all, so "the poll failed", "the monitor lookup missed" and "the shape would not
render" were indistinguishable from the console — all three look like a yellow ring. It now traces
one line at each of those points.

**It does not use `GetFramePointerShape`, as this entry suggested.** That only exists on the Desktop
Duplication path, and duplication is refused in exactly the session and driver states the GDI
fallback exists for — so a cursor built on it would leave the machines already having a bad day
looking at a ring. `GetCursorInfo` + `GetIconInfo` works on both paths and needs no new DXGI interop.
The ring is kept, and is what shows until a shape arrives.

**The wire is one bitmap per shape.** `BinKind.Cursor` carries a PNG keyed by the cursor's Windows
handle, which is stable for the life of a shape; `CursorMsg` then carries position, visibility and
that id, so a moving pointer costs a few dozen bytes. Visibility matters: a full-screen video hides
the pointer, and drawing one anyway would show the technician something not on the customer's screen.

**The cursor loop is independent of the frame rate, and 2.3 is why that now matters.** Duplication
sends nothing at all for a mouse move, and with the controller in place the capture loop may be
pacing down to a few frames a second on a bad link — a pointer that keeps moving is most of what
makes a slow picture feel alive rather than broken. It polls at 40 ms against the picture's 80 ms.

*Two things the harness caught that reading the code did not.* `Bitmap.FromHbitmap` **discards the
alpha channel**, so every colour cursor came back fully opaque — a solid 48x48 rectangle — and the
only one that survived was the one with no colour bitmap. And there is a **third** cursor format
this entry did not anticipate: the I-beam has a colour bitmap with no alpha and a mask that is 100%
white, meaning "XOR onto the screen", which the obvious rules render as completely transparent. It
is approximated by inverting the colour, which is right over the light documents an I-beam lives in
and wrong over a black background — stated in the code as a known cost.

**Scroll smoothness is not a separate item any more.** 2.1 took a scrolling document from 71.6 to
21.3 ms a frame, 2.2 took it from 2785 to 198 KB/s, and 2.3 chooses the frame rate from the link.
There is nothing left in this item that those three did not already do.

*The original entry:* Cheaper than when this was written — 2.1 has landed, and the
original note said these were cheap wins once it did: send the real cursor shape rather than drawing
a fake one, and stop letting a scrolling window look like a slideshow.
`IDXGIOutputDuplication::GetFramePointerShape` hands over the real cursor bitmap, and the frame info
struct already carries `PointerPosition` and `PointerVisible` — both are declared in `Dxgi.cs` and
currently read and ignored. A duplicated frame does not contain the cursor, which is why the console
draws its own yellow ring; sending the real shape is what replaces it.

**Done when:** a side-by-side against AnyDesk on the same link is not embarrassing. Measure it —
frames per second and bytes per second under three conditions (idle desktop, scrolling a document,
playing video), before and after. This stage is exactly the kind of work that feels better without
being better, and a number is the only defence against that.

**`tools/codecbench` is that measurement and it exists now** — it drives the real capture path
against a scene it paints itself, so runs weeks apart compare. It earned its keep on the first day:
it caught the dirty-rectangle regression above, and it caught itself measuring the wrong screen,
which is the failure mode this "done when" was written to guard against.

---

## Stage 3 — audio

> **Reversed on 2026-09-13, after this was written. Streaming is now first, the diagnostic second.**
> Clint's reason is better than the reason behind the original order: a test tone needs a human in
> the room to say whether they heard it, and **unattended sessions have nobody there** — which is
> exactly when these machines get worked on. Streaming also subsumes much of the diagnosis, is the
> actual parity item, and opens the home-viewing market where audio is most of the point.
> `docs/NEXT-SESSION-AUDIO.md` is the brief. The text below is left as written, because 3.1 is
> still wanted — just second.
>
> **3.2 was built on 2026-09-13 and is what stage 3 now means.** Raw 24 kHz mono PCM over
> `BinKind.Audio`, a Listen button and a level meter, proven end to end on one machine by
> `tools/audiolocal` and awaiting a shipped host on the test machine for the wire half. See
> HANDOVER §18. **Opus was deliberately not adopted** — that is Clint's call once bandwidth
> alongside screen tiles has been measured, and it is a "nothing is bought in" question.
> **3.1 was built on 2026-09-13**: the Sound tab, with findings in plain English rather than a
> device dump. The remaining audio item is 3.3, the microphone, plus the Opus decision.

**The diagnostic half is already decided and already half-built.** Clint chose *diagnose and test
tone first*, not live streaming, and `tools/audioprobe` proves the approach works: running as a
child of `Host.exe` — so with the host's own SYSTEM token — it saw the endpoints, the volumes, the
mute states, the per-application mixer including a process in the interactive user's session, and
with `--tone` it played a 2-second tone on the customer's speakers. On its first run it found a real
fault: DESKTOP-OPHQIDH's speakers were muted and the default output was an HDMI television. Those
are the two most common "my sound stopped working" calls, named without anyone looking at the
machine.

**3.1 Productise the probe.** Move it out of `tools/` and into the session window as an Audio tab:
what Windows thinks the default device is, per role, with volume, mute, per-app mixer and device
problem codes — plus a Play Test Tone button. This is a **differentiator**, not parity: neither
competitor tells you *why* there is no sound, they stream it and leave you guessing.

**3.2 Then stream it.** Loopback capture through WASAPI on the host, Opus over a new `BinKind`,
playback on the console. Same no-server-change property as 2.2. Streaming answers "does it sound
right", which is a different and rarer question than "why is there nothing" — which is why it is
second.

> **3.3 BUILT 2026-09-30, both ways, in the app AND the browser — NOT on hardware.** Clint's answers:
> the customer allows their microphone every session, never in an unattended session; an open
> microphone with a Mute button; recordings stay picture only. `docs/NEXT-SESSION-VOICE.md` and
> HANDOVER §67. Opus is still not adopted — voice is raw 16 kHz PCM, 32 KB/s each way.

**3.3 Microphone the other way, if it earns its place.** Talking to the customer through the tool
rather than the phone. Genuinely useful, genuinely more work, and the phone already works. Decide
after 3.2 ships, not now.

**Done when:** a technician can tell a customer why their sound is broken without asking them to
click anything, and can hear the machine when they need to.

---

## Stage 3.5 — simplicity: the console gets out of the way

Added 2026-09-18, on Clint's instruction, and **it runs before stage 4**. Nothing here is parity
work: TeamViewer and AnyDesk both have all of it. It is the stage that makes the product feel like
the thing it claims to be. *The whole idea of this program is to be as simple as possible to use* —
and three of the items below exist only because a feature was given a tab instead of a gesture.

The wire is not the problem, as it was not the problem in 1.3. Text, files and folders already move
both ways and are proven on hardware (§8, §22). **Every item here is in the console's UI, the tray
app, or one missing watcher on the host.** Do not reopen `FileSetTransfer`.

**Decided by Clint, 2026-09-18, before any of it was built** — three questions that change what gets
written, answered up front rather than discovered halfway:

- **The clipboard mirrors only while the session is live and being looked at.** Customer to
  technician while the technician is actually viewing the screen; technician to customer while the
  session window is in front, which is the rule `SyncMyClipboard` already enforces. Nothing copied
  before the session or after it moves anywhere. This keeps most of what §8 was protecting — the
  password a customer copied at nine this morning never crosses — while still feeling like one
  clipboard for the length of the call. **§8's "no automatic sync" is narrowed by this, not
  overturned: read them together.**
- **A copied file set fetches silently under a threshold (start at 50 MB) and offers above it.**
  Under it, paste just works. Over it the status bar says what is waiting and how big, and nothing
  moves until it is asked for. The alternative that has no threshold at all is a proper COM
  delayed-rendering data object (`CFSTR_FILEDESCRIPTOR` / `CFSTR_FILECONTENTS`), which is how
  TeamViewer does it and is days of interop; it is the right answer eventually and it is not the
  answer now.
- **Drag off the picture reads the customer's foreground Explorer selection, and only that.** A
  drag begun anywhere else says so plainly. The failure this is built to avoid is a gesture that
  silently does nothing, not a gesture that has limits.

**3.5.0 What "works" means here: eighteen cells, not three features.** Clint, 2026-09-18: *all of
this must work for text, files and folders.* So the unit of done for 3.5.1 and 3.5.2 is a cell in
this table, and a gesture that handles files but shrugs at a folder is not done. Folders are listed
separately from files on purpose — they are the same engine (`FileSetTransfer`, and `filesetcheck`
already covers empty folders, zero-byte files, non-ASCII names and collision renaming) but they are
not the same *test*, and a tree that arrives with its empty directories missing has failed in a way
a single file never can.

| Gesture | Payload | To the customer | To the technician |
|---|---|---|---|
| `Ctrl+C` / `Ctrl+V` | text | **BUILT 2026-09-18** — mirrors while this window is in front | **BUILT 2026-09-18** — mirrors while a technician is watching the screen |
| `Ctrl+C` / `Ctrl+V` | files | **BUILT** — sent on the copy, or offered when over 50 MB | **BUILT** — fetched on the copy, or offered when over 50 MB |
| `Ctrl+C` / `Ctrl+V` | folders | **BUILT**, same engine, and asserted separately from files | **BUILT**, same engine, and asserted separately from files |
| right-click Copy / Paste | text, files, folders | **BUILT by the row above** — right-click Copy *is* the clipboard; there was never a second mechanism to build, only the same one to perform through a different gesture, and nobody has yet performed it on a real machine | same |
| drag and drop | text | **BUILT 2026-09-29 (3.5.2)** — goes onto their clipboard, *Ctrl+V to place it*; not on hardware | **not a real gesture** — a text selection on the remote screen cannot be known without hit-testing the customer's foreground app. Covered by copy and paste, and say so rather than pretending |
| drag and drop | files | **DONE**, proven on hardware, lands where the gesture pointed | **BUILT 2026-09-29 (3.5.2)** — off the picture as well: the selection in their front Explorer window or on their desktop; not on hardware |
| drag and drop | folders | **DONE**, proven on hardware | as files — `dragselcheck` reads a selected folder back |

**The one cell that cannot be seamless, and the honest answer for it.** Text *dropped* onto the
remote picture has nowhere to land: placing it needs a drop target on the customer's desktop, and
the process that would have to perform the `DoDragDrop` over there is the tray app, driven by a
synthesised mouse move it does not control. That is fragile in a way none of the rest of this is.
**So a dropped text selection goes onto the customer's clipboard and the status bar says
`on their clipboard — Ctrl+V to place it`**, which is one keystroke the technician is already
pressing and which is already forwarded as remote input. One keystroke beats a gesture that works
four times in five. If it ever needs to be truly seamless, that is its own item and its own day.

**3.5.1 The clipboard stops being a place. — BUILT 2026-09-18, proven locally by `tools/clipmirror` (13 of 13); and PROVEN ON HARDWARE the same day — `clipcheck` 15 of 15 against DESKTOP-OPHQIDH, leak test included. See HANDOVER §26.** Copy on either machine, paste on the other, for text,
files and folders, by `Ctrl+C` / `Ctrl+V` and by right-click Copy / Paste, with nothing to press
first. Half of this exists: the console already runs an `AddClipboardFormatListener` and pushes the
technician's **text** to the customer automatically (`SessionForm.SyncMyClipboard`), and a set of
files arriving from a paste is already fetched and put on the technician's clipboard
(`ClipboardMsg` with `Files`). What is missing is the other half — **the host never watches the
customer's clipboard.** It only answers a pull, which is why "Get theirs" is a button. The work is
a clipboard-format listener on the message-only window `ClipboardAccess` already keeps alive (§8 —
it exists because `SetClipboardData` fails silently without a real owner window), an echo guard on
both sides so a mirrored copy is not mirrored back, and a size rule for files. Then **the Clipboard
tab is deleted**, not demoted.

**3.5.2 Files stop being a place. — BUILT 2026-09-29, not on hardware; see HANDOVER §58.** A drag off the picture takes the selection in their front Explorer window or desktop (read by a helper running as the person); with nothing selected their drag is left alone and the reason shown; dropped text goes onto their clipboard. `dragselcheck` 11 of 11, `dragoffcheck` 21 of 21 (15 with the cancel moved ahead of the answer). The original brief follows. Drag in already lands where the gesture pointed. For files and
folders — not for text, which it refuses outright today, and 3.5.0 says what happens to a dropped
text selection instead. Drag out is
built but starts **in the Files tab's list**, which is the compromise 1.3 recorded and the reason
the tab still exists. Dragging off the picture of the remote desktop needs the console to know what
the customer has selected, which the host cannot hit-test for itself — but the tray app runs as the
user and *can* ask the shell (`IShellWindows` / `IFolderView::GetSelection`) what is selected in the
foreground Explorer window. That covers the case this is for — getting a log, a screenshot or a
backup off a machine — and degrades to a plain message when the drag did not start in a folder
window. **The Files tab then becomes a fallback under Tools, not a destination.**

**3.5.3 Everything that moves is on the ticket. — BUILT inside 3.5.1 (2026-09-18) and MADE TRUE 2026-09-29; see HANDOVER §56.** Nothing had ever checked the lines reached a ticket, and most did not: the host only reported its clipboard minute when another copy arrived, and the server forgot the ticket before the host's last line came. `tools/clipledger` 20 of 20 (18 before the fix). New host on DESKTOP-OPHQIDH, not yet through a session. The original brief follows. One line per movement: direction, what, how much,
when. `FileActivityMsg` already does this for transfers and is already the customer's disclosure
line in the tray app (§22, "logged, not announced"). Extend it to cover clipboard movements, and
make sure a *seamless* copy is still a *recorded* copy — automatic must not mean invisible.

**3.5.4 THE SIDE PANEL GOES. Tools is a dropdown in the TOP toolbar. — DONE 2026-09-18.
`toolbarprobe` 37 of 37, `dragoutcheck` 43 of 43; see HANDOVER §28. CONFIRMED on the real console
the same day — Clint, after running `demo\technician`: "all seems to be working well."**
Corrected by Clint on
2026-09-18 after a first attempt got it wrong, and the correction is the whole item: *"the whole
idea is to get rid of the side bar on the right. listen should be added to the tools bar so that
all tabs sit at the top instead of anything being on the side."*

So the target is **no right-hand panel at all**. The session window is the picture, a toolbar
across the top, and the status bar. `Tools` sits on that toolbar next to `System`, and holds
**Chat, Listen, Files, Scripts and Info** — Listen moves off the toolbar and onto the menu with
the rest, so there is one place for everything rather than two.

Choosing a tool opens it as its **own movable window**, the way Chat does in 3.5.5. That is what
makes the side panel unnecessary rather than merely smaller: a panel that is always there costs
the picture its width whether or not anybody is using it, and pop-out and full screen inherit that
cost. A window costs nothing until it is opened and can be put on a second monitor.

**The first attempt, `bfb7c75`, was a half step**: the five tab buttons became one `Tools`
dropdown and the bar went from two rows to one, but the panel stayed and the tools were still
pages inside it. The menu plumbing was kept; the panel and the pages went.

**What is built now.** `Tools ▾` on the top toolbar beside `System ▾`, holding Chat, Files,
Scripts, Sound and Info, with **Listen** under a separator — off the toolbar, as asked. Each opens
a movable, resizable `ToolWindow` owned by the session window. No right-hand panel, and
`ToggleSidePanel`, `LayoutTabs`, `_sideIndex`, `_sideCard`, `_sideToggle` and `SidePanelWidth` are
gone with it. `toolbarprobe` measures the point rather than asserting it: **1338 px of a 1338 px
canvas** where the panel used to take 400, in every viewing mode. The pop-out no longer has to
carry the panel with it, because an owned tool window is the pop-out's sibling rather than
something buried under it.

Three decisions are in HANDOVER §28 and are the ones to read before changing any of it: every page
is built and parented at construction (a lazily-built chat log drops what arrives before it is
opened, silently); closing a tool hides it rather than destroying what is in it; and a tool window
never takes the focus by itself, because the technician may be typing on the remote machine.

**Still to prove on hardware:** whether a chat window that comes up without stealing focus is
actually noticed. If it is not, the answer is a mark on the `Tools` button — not a window that
grabs the keyboard.

**3.5.5 Every tool is a window, not a tab — starting with Chat. — DONE AND CONFIRMED ON HARDWARE
2026-09-18. `chatprobe` 23 of 23; see HANDOVER §29. Clint, after a full ship and a client
reinstall: *"it works."* It needed the full ship, because it changes the client.** The technician's half came with 3.5.4. This was the customer's, and it had the real
fault: `ChatPopup` was pinned to the bottom right and **re-pinned there on every message**, so it
could sit on the desktop icon the customer had just been asked to double-click and there was
nothing they could do about it — the window was borderless with nothing to drag it by, and even a
movable one would have been dragged back by the next sentence.

It is now dragged by the company name across its top (that window's title bar, and it says so
with a move cursor) and by its own background, with the drag handed to Windows so snapping and
the screen edges come free. The log and the reply box are deliberately **not** drag handles,
because selecting text out of the log has to keep working. Positions are remembered between runs
for both sides in `%LOCALAPPDATA%\RemoteSupport\windows.json`, via the new
`RemoteSupport.UI.WindowPlacement`; a remembered position on a screen that no longer exists is
refused rather than restored, because a window nobody can see presents as the feature being
broken.

`chatprobe` also found a 7 px overlap of the reply box on the Send button — a one-line `TextBox`
takes the height its font needs and ignores the height it is docked into, so the top strip of
Send typed instead of sending. Invisible in a screenshot. Fixed by asking the control for its
height rather than hard-coding one.

**3.5.6 The customer's tray menu. — DONE 2026-09-18; see HANDOVER §30.** `Request Support`,
`Chat with a Technician`, a separator, and `Exit`. Chat opens the chat window itself with the
caret in the reply box. `Open` is the one that went — the double-click on the icon does it and is
what people try first.

**This item asked for exactly two items and that part was wrong.** Exit was removed and Clint put
it back the same day: the app runs on somebody else's computer, and one that watches for support
requests and cannot be closed from its own icon is the kind of thing people uninstall rather than
trust. Nothing in the product needs it gone — the installer stops the process itself and the HKLM
`Run` key restarts the app at logon — so quitting costs the customer nothing they cannot undo by
logging in again.

**3.5.7 A request must say what is wrong. — DONE 2026-09-18. `callprobe` 25 of 25; see HANDOVER
§30.** The description is required: at least three characters, refused inside the dialog rather
than in a message box on top of it, and the complaint clears as soon as they type. On the
technician's side the reason is now the **largest text at the top** of the incoming-call alert,
above the caller's name. The empty branch stays, reworded, because an old client app can still
produce one.

Building it turned up three faults of one shape — **a size in pixels measured against a font in
points** — two of which were invisible on the developer's own screen: `AutoEllipsis` and wrapping
are exclusive in WinForms; re-fitting text from inside `Resize` leaves the dock unapplied, so a
label kept the 100 px it is born with while its siblings measured 428; and the alert had been
clipping its own text at 150% display scaling all along. Every label measures itself from its own
font now. **The rule for the next window in this codebase: do not write a control height as a
number.**

The original brief, kept because it is the reasoning: `RequestDialog` asked "What do you need
help with? **(optional)**" and the incoming-call popup consequently had a "No description given."
branch. Make the description mandatory, and make it the thing a technician reads first on the ring — a call with no reason is a call answered blind, and whoever answers has
to ask the question the customer has already been asked once.

**How this gets proved.** The rule from §7 and §12 stands and most of the harness exists.
**3.5.0's table is the checklist**: every cell that is not "not a real gesture" gets an assertion,
and the folder rows are separate assertions from the file rows even though they share an engine —
an empty directory that quietly did not arrive is the failure that only a folder can have, and
`filesetcheck` catches it precisely because it looks for it.

`tools/clipcheck` grows the reverse direction, which is the missing half: a *separate process* on
the customer machine puts text, then a `CF_HDROP` naming a file, then one naming a folder tree, on
their clipboard — and the technician's clipboard is read here. Reading it here is the only way to
tell a mirrored clipboard from a remembered one, the same reason the original 7 of 7 used a
separate process on the far end. Non-ASCII names go across **base64-encoded**, for the reason
`treecheck` already knows: PowerShell 5.1 turns `café` into `cafÃ©` in either direction and would
otherwise make "the name is wrong" and "the file never came" look identical.

`tools/dragoutcheck` gains the Explorer-selection path and a folder case. `tools/toolbarprobe`
drives the real `SessionForm` with no server and is where the Tools menu, pop-out, full screen, and
the text-drop-onto-the-picture path get checked. The tray menu and the mandatory description are
two-minute manual checks and should say so rather than pretending to a harness.

---

## Stage 3.6 — the dashboard and the ticketing system earn their keep

**BUILT 2026-09-20, ALL SEVEN ITEMS. NOT ON HARDWARE.** Six harnesses: `contactcheck` 34 of 34,
`dashtabs` 47 of 47, `handoffcheck` 42 of 42, `ticketscope` 26 of 26, `photogate` 36 of 36 and
`ticketpersession` 28 of 28. Sections 32 to 36 of the handover are what each one turned into and
what building it found. **3.6.8 (roles as tick boxes) and 3.6.9 (a theme each person owns)** are the two Clint added while
it was being built; 3.6.9 was done the same day and 3.6.8 on 2026-09-29 (HANDOVER §55).

*Added 2026-09-20, from Clint's description and seven decisions he made the same day. The whole
brief, including the traps and the harnesses, is `docs/NEXT-SESSION-DASHBOARD.md` — read that
rather than this summary before building any of it.*

**3.6.1 Contacts.** A client may be one person or a company with many. Contacts belong to a client,
carry their own details, and a machine links to either a contact or a client.

**3.6.2 The console home becomes two tabs.** Attended and Unattended, a green or red dot for
online, the owner's name in the first column (contact, else client, else the logged-on user), no
Status or Access column, no grid lines, and a client with several machines on one expandable row.

**3.6.3 The portal learns who a person is.** Technicians cannot sign in to it today at all, and
every portal page resolves a company rather than a person. This blocks 3.6.4.

**3.6.4 Tickets move to the browser, with roles.** The console's Tickets button opens the portal
signed in as that technician. A technician sees their own tickets and the unassigned pool, and
nothing of a colleague's — enforced on the server. The owner (`TenantRole.Admin`) sees and edits
everything; the office administrator (`TenantRole.Billing`) sees everything, opens tickets, assigns
and reassigns them, comments, and may not change a status. All three roles may open a ticket; in
practice the office administrator opens most and hands them out. Owner and office administrator get
a view of every technician's load.

**3.6.5 Workshop photos.** Three before photos to book a workshop job in, three after photos before
it may be closed. Gated on the server, uploaded from both the console dialog and the web form.

**3.6.6 One session, one ticket.** Every remote session gets its own ticket and number, however
many times the same machine is worked on in a day — reusing only within 30 minutes of the last one.
A technician starting an unattended session is asked why first, and that answer becomes the
ticket's description, exactly as a customer's own reason does since 3.5.7.

**3.6.8 A person holds ROLES, not a role. — DONE 2026-09-29, not on hardware. See HANDOVER §55.** Four boxes (Owner, Supervisor, Technician, Office administrator), the union of what they allow, one seat per person if Technician or Supervisor is ticked. `rolecheck` 72 of 72. Migration `RolesAsTickBoxes`; platform restart only. The original brief follows. *Added 2026-09-20.* `TenantRole` is one number on a
person, so a one-man business must call itself the owner and live with seeing everything, and
"technician and office administrator, but not owner" cannot be said at all. Owner, Technician and
Office administrator become three tick boxes, and what somebody may do is the union of what they
tick. Needs a migration: the stored values are 0, 1 and 2 today and flags cannot have a zero.

**Sequencing.** This does NOT block 3.6.4, and 3.6.4 must not be written against the single enum
either. Every page asks a QUESTION — may this person see all tickets, change a status, assign one —
rather than comparing a role, and the storage behind those questions changes here without any page
being rewritten. Seat counting (4.7) depends on this: one person holding three roles is one seat.

**3.6.9 Nobody is locked into somebody else's light or dark. — DONE 2026-09-20, not on hardware.**
`themecheck` 40 of 40: both palettes define every token and every pair that carries text passes WCAG
AA, the tightest being light warn at 5.0:1. One setting on the PERSON (`Technician.Appearance`),
applied to the portal and the console together; the company's `TechnicianTheme` is now the fallback
rather than the last word, and `ClientTheme` is untouched by design. **See HANDOVER §38 for the half
that was nearly missed** — a dozen rules lifted surfaces with a wash of white, which is invisible on
a light ground, and the sticky header was hard-coded to the dark ground. The original brief follows.

*Added 2026-09-20.* The portal has
no light mode at all — `site.css` hardcodes a dark palette and `color-scheme: dark`, so this is
"build the light palette, then add the switch", not just the switch. In the console the owner picks
`TechnicianTheme` for every technician; it becomes the technician's own choice, remembered against
their own account so it follows them to any machine. The customer's app is deliberately NOT
included: `ClientTheme` is how the IT company presents itself to its customers, which is branding
rather than preference.

**3.6.7 A ticket can be part of another ticket.** *Added 2026-09-20.* The 30-minute window is a
guess about one sitting, and real work carries over a lunch break and a night. One parent, many
children, one level deep: the parent lists its children and totals their time, so a three-day job
is one billable thing made of the sessions that did it, and every session still keeps its own
number.

---

## Stage 3.7 — a workflow per category

*Added 2026-09-20. **Nothing built, and deliberately unfinished**: the four workflows are Clint's to
write. The brief, the template he is writing them into, and the traps already known are in
`docs/NEXT-SESSION-WORKFLOWS.md` — start there, not here.*

Clint, after opening a workshop ticket: *"I'm going to create workflows for each category, then give
those to you, so that each ticket in each category follows certain routes and automatically assigns
accordingly."*

Each of **General, Remote support, Walk in, Workshop and Call out** gets a defined route — the stages a job
moves through, who may move it between them, what must be true before it can leave each stage, and
who it lands with to begin with. It generalises something the code already does in exactly one
place: Workshop's three-photos-in-three-out (3.6.5) is a workflow rule in everything but name, and
`TicketService.UpdateAsync` is already the single gate every status change passes through.

**Where a technician works one, reversed 2026-09-20.** Tickets moved to the browser in 3.6.3
because the console window was cramped; once the New ticket dialog was rebuilt (§37) Clint looked
again and wants a technician's tickets back in their console, with the browser kept as a second way
in and mainly for the owner and the office administrator. Cheap, because the hub already carries
every ticket message and has scoped and role-checked them since 3.6.4. **`TicketsForm` is rebuilt
WHEN the workflows are written, not before** - they decide what it has to hold.

**The condition on all of it: a workflow is defined ONCE, on the server, and both surfaces render
from that definition.** Two copies of the stage rules will drift, and the symptom is a ticket a
technician cannot move and nobody can explain.

**The fork to settle first: built in, or configured per reseller.** A configurable engine is a large
build and puts a rules screen in front of every IT company before they can open a ticket, which cuts
against the standing rule that this product be as simple as possible. Four built-in workflows cost a
fraction and cannot be misconfigured. **Recommendation: built in**, with per-tenant knobs only where
companies genuinely differ.

**The trap that decides the size of the job:** `TicketStatus` is ONE enum shared by all four
categories. If a category needs stages beyond Open / In progress / On hold / Resolved / Closed, the
data model changes; if they map onto the five that exist, this is rules only.

**3.7.1 Delivery, and several machines at once (2026-09-30). BUILT the same day, NOT on hardware — HANDOVER §63 and §64.** Two additions to the Workshop route,
in Clint's words and with his answers in `docs/NEXT-SESSION-WORKFLOWS.md` ("Fourth round").

---

## Stage 3.8 — the office talks to itself, and a customer can ring from a browser

*Added 2026-09-30, from one message of Clint's. **BOTH BUILT THE SAME DAY, NOT on hardware** — HANDOVER §65 (office chat) and §66 (support in the browser).*

**3.8.1 Office chat.** *"Everybody at the office is able to chat with each other so that when you
chat and do either the @ or select the person it pops up a chat window in the browser or something
similar where internal chatting can take place."* His answers: **one to one, and one group,
Everyone**; **@mentions in ticket comments stay ticket comments** (they do not open a chat); and
**the browser shows a notification** when a message arrives while the tab is in the background. In
the portal and the console alike, through the hub that already carries every ticket message.

**3.8.2 Support in the browser.** *"Potential clients to be able to get remote support via a
browser. So they click on a link, put in their contact details, and request a support call online
and it rings through to the technicians the same way the app works. This would also be a good way
for them to test the I.T company's services before downloading the actual app."* **What a browser
cannot do, and Clint accepted it:** a web page can SHOW the customer's screen, with their
permission, but no browser lets anyone move their mouse or type on their machine — that boundary is
the browser's, not ours. So it is **see and guide**: their screen, chat, and a clear way to download
the real app for full control. His answers: **worth building**; each IT company can **switch the
link off and set its own hours** for it (support hours can be longer than office hours); and **a
browser caller becomes a Contact and a ticket**, like a tray app call. It is not the cure for the
app being blocked — code signing (stage 5) is.

**3.8.3 Website chat.** *Added and BUILT 2026-09-30, NOT on hardware — HANDOVER §70.* *"A chat windows
for a tenants website. This would allow them to install it on their own website and when one chats in
it is goes through to every logged in technician including supervisors and office administrators.
this would be a great way for someone to answer questions immediately and guide them to the support
page."* His answers: **the first person to reply takes it** (the others see who is answering, and may
still join in); **a visitor gives their name and an email or phone** first, and becomes a Contact;
**when nobody is signed in, or outside the chat's hours, they leave a message, which becomes a General
ticket**; and **build it now** — it goes live on a real website the day the platform is hosted (stage
4), because until then the server is only on the office network.

**3.8.4 Tickets by email and by a contact form.** *Added 2026-10-05.* *"The ability to open general
tickets via a website contact form or email address. For example, someone sends an email to
support@anywhereit.co.za, it automatically creates a ticket using the email address, subject and
body. It would be great if the system could check the email address against the client database,
and automatically link to the client if the email address exists. The same would work from the
contact form. The system should then auto reply to the email address given with the ticket reference
number and a short message saying a ticket has been opened."* His answers, the same day:
1. **It must work for any mail host**, *"explaining the possible issues they may face"*, and **a
   company can make a form for its website from the portal**. Anywhere IT's own mail is cPanel.
   Two ways in, built now: **the platform checks the company's mailbox** (server, username,
   password — cPanel, xneelo, most hosting, Gmail with an app password, Zoho) and **forwarding**
   (support@ forwards to one mailbox on our domain, which the platform checks and sorts by the
   address it was sent to). **Microsoft 365 sign-in is LATER, by Clint's decision** — forwarding
   covers 365 customers meanwhile, once their admin allows forwarding outside the company. **BUILT 2026-10-19
   (HANDOVER §151): Sign in with Microsoft** — one multi-tenant Entra app of ours, delegated IMAP and SMTP only,
   the refresh token kept encrypted; a refused sign-in stops and says Sign in again. Clint's one-time steps (register
   the app, the secret, publisher verification): `docs/MICROSOFT-365.md`. Forwarding stays as the fallback.
2. **An unknown sender: the ticket opens with no client.** Never a client made up from an email,
   or every spam message becomes a client.
3. **The client is known but the person is not** (their address's domain belongs to a client):
   the ticket stays unlinked and **says so, suggesting the person be added as a contact first**, so
   that the ticket is linked correctly — one button that adds them and links it.

Also agreed: a reply to the auto-reply goes onto the SAME ticket (its number is in the subject),
and never opens another; automatic mail (out-of-office, bounces, newsletters, the mail server's
spam) never becomes a ticket and is never answered; a limit per sender per hour; free-mail domains
(Gmail, Outlook, …) never match a client by domain.

**3.8.5 A knowledge base.** *Added 2026-10-05.* *"There should be an option on all user's dashboards
to click on their dashboard and access and edit/create in the knowledgebase. There should also be an
option once a ticket is completed with the option to commit to the knowledgebase that then opens it
as an editable item, so that it can be worded correctly. Knowledgebase options should be able to be
linked to clients. For example a client may have a ticket to create a new user on their network. Once
the technician completes the job, he can commit the ticket to the knowledgebase, then edit with step
by step instructions, so that any other technician then knows how to add that user to that client.
The knowledgebase would have a General section and clients section."* His answers, the same day —
*"go with your recommendations"*:
1. **When the ticket offers it:** from the moment the work is recorded as done (a Work done entry,
   or the technician's decision on how the job ends), and for ever after, closed included — a
   technician never closes anything, so a button that waited for Closed would never be seen by the
   person who did the work. **Offered to anybody on the ticket, and supervisors and the owner.**
2. **Found without looking for it:** a ticket for a client with articles says *"Knowledge base for
   this client (3)"*; so does the console when you connect to that client's machine.
3. **One client per article, or General.** Widened later if several clients share a set-up.
4. **Pictures:** paste or drop screenshots straight in, made smaller as workshop photos are.
5. **Everybody may read, write and edit; every save is kept** with who and when, so a damaged
   article can be put back. **Only a supervisor or the owner may delete.**
6. **Never a password.** The editor says so, and a save that looks like one is warned about.
Also: **staff only** — nothing here is shown to customers (a customer-facing help page is a later
question); **edited in the browser** — the console's buttons sign you straight in to it.

---

## Stage 3.9 — somebody from outside the company, let in by the customer

*Added 2026-10-06. **Step 1 (the switches and the customer's code, HANDOVER §87), step 2 (another company connects with a code, HANDOVER §89) step 3 (a guest, with the guest console from /guest, HANDOVER §90), step 4 (a guest in a browser, /guest/web, HANDOVER §91) and files on the customer's tick (HANDOVER §92) BUILT the same day, NOT on hardware.** **3.9 is built in full.*** Clint was asked: *"a client has our product on, but now needs support
from say an accounting firm that wants to remote in to support a client with something else."* His
suggestion: *"have the client generate a code, with the other company using a generic technician
installer that links to that code."* Then: *"We need to also take into consideration, the possibility
of cross tenant support. Two signed up tenants may compliment each other in the work they do, so cross
tenant option would be good using the same principle. This would still need to be a multiple
connection option, so that the local tech can watch what is going on and terminate the connection if
necessary."*

**The principle, for both kinds of outsider.** The CUSTOMER makes a code in the tray app — *Let
someone else connect…*, saying who it is for — shown only after a warning (*"Only give this to
someone you contacted yourself. Nobody legitimate will phone you asking for it."*). Nine digits, ten
minutes, used once; five wrong codes lock the machine as the unattended password does (§77). The
outsider enters it; **the customer is still asked** — the normal consent prompt, naming who is
asking — and nothing happens until they press Allow. **Never unattended.** The machine's own company
is told the moment it starts — a pop-up in the console (the billing pop-up's mechanism, §86) — and
**its technicians can join the session to watch it and end it**; the session is written on a ticket
under that client. The scam this invites ("read me the code on your screen") is why the local
company's eye and its End button are part of the design, not an extra.

**3.9.1 A guest** — anybody, with no account (the accounting firm). Connects with a **guest console**
— the technician console with no company and no sign-in, opening on *Enter the code you were given*,
one .exe for everybody from a public page — **or from a browser page, installing nothing**. Clint:
*both options should be available*. The console first (it reuses the viewer we have); the browser
viewer second (H.264 through WebCodecs, the reverse of 3.8.2, plus keyboard and mouse from the page).

**3.9.2 Another company on the platform** (cross tenant) — two signed-up companies whose work
complements each other. The other company's technician uses **their own console, signed in as
themselves**, with *Connect with a code*: so who is on the machine is a named person at a known
company, not a guest. They see that one session and nothing else of the machine's company — no
machine list, no clients, no tickets. Their own company gets a record of it too.

**Clint's answers (2026-10-06):**
1. **A switch, OFF by default, for each company — and changeable per machine** (as the health check:
   follow the company, or On or Off for this machine, §50).
2. **What an outsider may do: see the screen and control it.** No scripts, restart, unattended access
   or Tools. **File transfer only if the customer ticks it** for that session.
3. **Both the downloaded guest console and a browser page.**
4. **Free.**

**What it touches.** A guest-code row (company, machine, who it is for, expiry, used); the hub
admitting a viewer that is not one of the company's technicians — today `/ws/session` refuses any
viewer whose company is not the machine's (`who.TenantId != hub.TenantId` → 401), and that line is
the one to open, narrowly, for a session the code made; a restricted kind of viewer whose every
message the hub checks against (2); the consent prompt naming the outsider; the local console's
pop-up, Join (watch) and End. The host is unchanged — to it, an outsider is one more viewer.

**The visiting company's record, settled 2026-10-06 (§89):** a line on its Sessions page, no ticket — one step to change.

**Open, to settle when it is built:** two companies on DIFFERENT region servers (§84) cannot share a
session through one hub — the first version is same-server only, and says so; and whether the
visiting company's record is a ticket in their portal or only a line in their Sessions page.

**3.9.3 A technician asks for outside help** — *added and BUILT 2026-10-22 (HANDOVER §155), NOT on hardware.* Clint:
*"What would be nice is for a technician to also be able to ask for outside help. In the console for example under tools
when connected to a machine, there is a get outside help option. This generates a code, plus links to the browser and guest
account download, that they can then copy or share via their whatsapp or email. Sometimes it happens that I tech needs the
outside assistance rather than the client."* He took every recommendation, and on who allows it: *"the customer should be
told"* — the technician lets them in; the person at the machine is told, never asked. The code is for that session only; the
outsider leaves without ending it, and goes when nobody of the company is left in it. A company switch (on by default) for
supervisors and the owner; the machine's own outside-help switch is the customer's and is not asked. Not on the free version.

---

## Stage 4 — hosting, reach and production security

The first stage that costs money and involves someone else's timetable. Nothing here is hard; all of
it is blocking.

**4.1 TLS.** The platform serves plain HTTP and `ws://` on the LAN. A reverse proxy with a real
certificate is **required before any real customer touches it**. This is the one item allowed to
jump the queue.

**4.2 Replace `owner-demo-key`.** Still the development value.

**4.3 Reach beyond the LAN.** Today the server is `ws://192.168.1.115:8090`. TeamViewer and AnyDesk
are global relay networks with NAT traversal and P2P. We do not need to match that and should not
try — a hosted relay that customer machines dial *out* to is enough, and outbound WebSocket over 443
gets through nearly every firewall without anyone configuring anything. That is already the shape of
the protocol. What it needs is a host, a DNS name and capacity planning. Direct P2P is a later
optimisation for bandwidth cost, not a requirement.

**4.4 Account security.** No 2FA, no SSO, no device allow/block lists. On a multi-tenant product
holding SYSTEM-level access to every customer machine in the country, 2FA stops being optional the
moment there is a second paying tenant. **2FA is BUILT (2026-10-01, HANDOVER §72)** — every sign-in
and the owner portal — with sign-out-everywhere, lockout, real addresses behind the proxy and no
plaintext. **Built 2026-10-07 (HANDOVER §111), not on hardware: a reinstalled machine waits for the
office** — matched by hardware alone, it keeps its record but unattended access is held back until a
supervisor or the owner confirms it is the same machine (the customer's tick is no proof: an impostor
holds the tray app too) — **and passwords at 600,000 rounds**, old hashes re-made at the next sign-in
(§72 items 1 and 2). **End-to-end encryption of sessions is BUILT too (2026-10-07, HANDOVER §112,
`docs/NEXT-SESSION-E2E.md`), not on hardware**: the console and the machine agree a key, AES-256-GCM,
the server relays only what it cannot read — what TeamViewer and AnyDesk advertise — with each machine's
identity pinned by the console, older apps allowed but marked, and the browser routes included. Still
open: SSO and device allow/block lists.

**4.5 The rest of the production checklist.** Monitoring, backups (nightly encrypted ON the server
since 2026-10-01; an off-box copy still to do), password reset, and the Data
Protection key ring — `SetApplicationName("RemoteSupportPlatform")` must never change, and
`data/keys` must be backed up somewhere private or every stored workshop password is lost.

**4.6 Auto-update.** *Added 2026-09-16, and it is bigger than it looks.* **The settings half is BUILT 2026-10-07
(HANDOVER §113), not on hardware: the hub sends a machine the company's current look when it connects and the moment
the Branding page is saved; the agent keeps it beside the installed branding and the tray app restarts into it when
nobody is using it. The binary half still waits for stage 5.** **Stage 5 is done (2026-10-15), and the binary half is
BRIEFED 2026-10-16 in `docs/NEXT-SESSION-AUTOUPDATE.md` — silent on customer machines, the rollout ours alone, only
files signed by us and newer than what is installed, rollback if the new agent does not connect.** **ALL OF IT IS BUILT
2026-10-16 (HANDOVER §147–§148), not on hardware: version numbers, the verifier, rings and Pause on `/owner/updates`, the agent
updating its machine with rollback, the console updating itself, and the one update by hand for machines installed before it.
`TEST-3.6.md` section DE.** Before this, a machine ran
whatever version was installed on it, for ever. There is no way to ship a fix to an endpoint we do
not manually revisit, which means every bug found on a customer machine is permanent until someone
drives out to it. That is already the position; it becomes untenable the moment a reseller has a few
hundred endpoints.

It surfaced while adding the per-tenant theme: a tenant who changes their branding or appearance
sees it only on *new* downloads, because `Branding.Load()` reads `brand.json` off disk and nothing
ever pushes a change to an installed machine. The theme made that visible, but it is not a theme
problem — the same is true of the logo, the colours, and every line of code in `Host.exe`.

Two halves, and they are worth separating:

- **Settings refresh** — the easy half. The tenant's branding is already sent over a connection the
  machine opens anyway; the host could take an updated `brand.json` on connect and write it beside
  itself. That closes the "I changed my logo and nothing happened" support call, and needs no
  installer, no signing, and no privilege.
- **Binary update** — the hard half. Replacing a running `Host.exe` that lives as a LocalSystem
  service, without breaking a live session, and without the update channel itself becoming the
  attack surface that hands SYSTEM to whoever compromises it. This one must not ship before **code
  signing (stage 5)** — an unsigned auto-updater pulling executables onto every customer machine is
  precisely the supply chain we would be warning customers about.

Do the settings half early; it is small and removes a real support burden. Hold the binary half
until stage 5 is done.

**4.7 Self-service seats, and the price that follows from them.** *Added 2026-09-16.* **BUILT 2026-10-05 (HANDOVER §83), not on hardware: seats are a price agreed by the owner on the People page, every role takes one, regions stored; payment is still 4.8.** The
commercial model is now seven seats — one owner, five technicians, one office administrator — at
R500, with extra seats at R300 each, and three other currencies. `docs/PRICING.md` is the
authority. Nothing in the product implements it: `Tenant.Seats` is a single integer defaulting to
5, there is no office-administrator role, and `/pricing` still advertises the old model.

What this item is: an owner signs up, chooses how many seats beyond the seven they need, and sees
the monthly total change as they do — before they pay, and again later in the portal when the team
grows. The arithmetic is trivial; the work is in the seat model underneath it, because seats stop
being a count and become three kinds with different rights.

**The office-administrator seat is the part to be careful with.** It is reception and accounts —
customers, jobs and invoicing — and it must not be able to start or join a session. The existing
`--admin` flag means tenant-administrative rights, which is the opposite, and reusing it would hand
the receptionist SYSTEM-level reach into every customer machine. A fourth role, with session access
denied explicitly rather than by omission.

The regional pricing already exists on the landing page (four regions, chosen or guessed from
locale). The portal needs the same, and the tenant's region has to be stored, because it decides
what they are billed in for as long as they are a customer.

**Answer the open questions in `PRICING.md` before building this, not during** — whether an extra
seat can be any role, whether a seat that cannot reach a machine costs the same as one that can,
and what happens to a mid-month addition at renewal.

**4.8 Taking the money.** *Added 2026-09-16.* **PayFast for South Africa**; Clint has the account
details from 2026-09-17. Onboarding and payment automated end to end, so a South African IT company
can sign up, pay, and have its branded installers without anyone at this end doing anything.

Sequencing that matters: this depends on 4.7 (there is no amount to charge until seats compute a
price) and on 4.4 (a portal that takes money and holds SYSTEM-level access to customer machines
needs 2FA first, not after). The other regions need a processor that handles their currency; that
decision is not urgent while only South Africa is open, and should not delay PayFast.

**4.9 A company takes its data with it.** *Added 2026-10-22 — Clint: "the ability for companies to
export their clients, tickets and knowledgebase. We should do this in the next chat."* **BUILT the same day (HANDOVER §154),
NOT on hardware**: Portal → Export (owner and supervisors), a zip of Excel workbooks and the files made in the background,
`clients.xlsx` in Import clients' own columns; a suspended or closed company exported by us from Owner → All companies, its owners
emailed a link; recordings on a tick. `exportcheck` 92 on both databases. What was asked, kept for the record: An
owner (and probably a supervisor) downloads the company's **clients** (with contacts), **tickets**
(with their story, notes and attachments) and **knowledge base** (articles, folders, pictures). Section
11 of the terms already promises it — *"Ask us before then for an export of your data"* within the 60
days after an agreement ends — and today that export is done by hand on the server, if at all. Things
to settle when it is built: the format (a zip of CSV/xlsx plus the files, so another program can import
it — the clients file should be the same columns `Clients → Import clients` reads, §52); whether a
Closed or Suspended company may still download it in its 60 days; and that it is a long-running job
for a big company, not one request.

**Done when:** a customer outside this LAN can be supported over the public internet, on TLS, with
2FA on the portal, and the server can be restored from a backup.

---

## Stage 5 — code signing

Deferred for a long time, and a sales problem rather than a polish one: unsigned installers mean
SmartScreen warnings and antivirus friction on every single customer install, which is a poor first
impression for a product a reseller is putting their own name on. `uiAccess="true"` — the cleaner
answer to driving elevated windows than holding SYSTEM on every machine, handover §4 — also needs a
signature, though **not an EV one**: see below.

**Wired 2026-09-16, unproven.** The publisher is **The Remote Support Program**, the registered
business, not Anywhere IT — one certificate covers every tenant, because every tenant ships identical
binaries and their own name reaches their customers through `brand.json`. `build.ps1` signs the four
executables, the two setups and the seven PowerShell scripts automatically once a certificate is
installed, and warns and continues until then; `-RequireSigning` refuses to produce unsigned output.
`docs/CODE-SIGNING.md` is the detail.

**Revised 2026-09-21 — buy OV, not EV.** This entry used to say EV, on two grounds that
have both gone. Microsoft's Trusted Root Program update of **March 2024** removed EV's instant
SmartScreen reputation: EV and OV now earn it identically, through download volume. And the claim
that EV "unlocks `uiAccess`" was never right — Microsoft's own UAC policy documentation requires a
signature chaining to the **Trusted Root Certification Authorities** store plus installation under
`%ProgramFiles%`, naming no validation tier, and `{autopf}` in both `.iss` files already satisfies
the second half. Nothing EV-only — kernel-mode drivers, WHQL, Hardware Dev Center — applies to this
product. OV is $129/yr against EV at $349/yr for identical behaviour on every machine we ship to.

One-year terms were chosen on 2026-09-21 because the rules above have moved twice in three years,
and **that was reversed on 2026-09-23: two years, by Clint's decision**, so the identity check is not
repeated annually. The volatility argument still stands; it is simply outweighed by not redoing
documents, a phone listing and a callback every year.

**The entity is registered with CIPC** (confirmed 2026-09-21), so nothing blocks the application —
and the CA validates the business, never the software, so an unfinished product is no reason to wait.

**`CODE-SIGNING-ORDER.md` is the step-by-step**, written 2026-09-23 and **revised 2026-09-28 once
LeaderSSL answered**: a Sectigo OV certificate, two-year term, at R9,942.42 excl. VAT, on a
**YubiKey 5 FIPS 140-3 we supply ourselves** (LeaderSSL bundles a token only with a one-year term).
Three corrections came out of that reply and all three matter. **The token does NOT have to be in
hand to order** — payment secures the price, and the device is needed only for the CSR and
attestation, which is what made a 30 September promotional price reachable. **It must be FIPS
140-3, not 140-2** — Yubico's 140-2 certification went to the Historical List in September 2026,
and because **the model names are identical across both generations and Yubico still sells both**,
the obvious store link ships the superseded key. And **a multi-year purchase is a term, not a
certificate**: since February 2026 a code-signing certificate may be valid for at most **459 days**,
so two years means a re-issue at about fifteen months. That last one weakens the reason two years
was chosen and does not reverse it — R9,942.42 against R11,714.52 for two one-year terms over the
same period. A standard YubiKey 5 will be refused; it has to be the FIPS line, and the key must be
generated **inside** the token in PIV slot 9a with a hardware attestation, never made on a PC and
imported. `CODE-SIGNING.md` keeps the reasoning, the corrected pricing and the finding that **there
is no South African certificate authority** — no local root is in Microsoft's programme, so every
certificate sold here is a foreign one behind a local shopfront, and the one CA that was South
African, Thawte, has been a DigiCert brand since 2017.

**ORDERED AND PAID 2026-10-02.** The certificate is bought in the name of **The Remote Support
Program (Pty) Ltd** (CIPC 2026/737434/07), and the YubiKey 5 NFC FIPS 140-3 is bought and being
couriered to Knysna. **The build needs no change for the longer legal name** — `Get-SigningCert`
matches on `*The Remote Support Program*`, so do not tighten that wildcard. What is left: tell
LeaderSSL when the token lands, follow their `ykman` commands to generate the key in PIV slot 9a and
produce the CSR and attestation, sit through Sectigo's OV validation, and install the issued
certificate into the same slot.

**THE KEY ARRIVED 2026-10-08 and the build PC is ready.** The YubiKey 5 NFC FIPS (140-3) reached
Knysna — **GTIN 5060408466162 and Article 101178 on the box both match Yubico's published values**,
and it says FIPS 140-3 on the packaging, so it is the right product and the right generation. The
Windows SDK and `ykman` are installed, and **`Find-SignTool` was run for real**: it picks up
`Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`, the binary runs, and the default timestamp
server is already Sectigo's. `Initialize-Signing` reports `On: False` for one reason only —
**the certificate**. Nothing in the build changes on the day it arrives.

**Answered 2026-09-28, and it is a bug rather than a footnote.** This entry used to ask whether ESET
objects to an unsigned script running as SYSTEM during a health check run. **It does** — Clint saw it
on a customer machine: ESET holds the file, the install fails, ESET releases it a minute later having
decided it is safe, and by then nothing is listening. **`BUGS.md` §9** has the diagnosis: three
separate layers of no-retry, including a console guard that makes the obvious human retry a no-op.
The fix (retry with backoff in `HealthCheckKeeper`, and let the console re-send a choice that failed)
is **worth building whether or not the certificate ever arrives**, because signing removes the
trigger and not the fragility — a freshly built signed binary still has no reputation on first sight.
**Built 2026-09-29** (HANDOVER §53): the agent tries again after 1, 5 and 15 minutes, and choosing
the same thing again in the console tries at once. Not yet watched against a real ESET hold.

**Every download an .exe, not a zip (Clint, 2026-09-30).** *"Will these still be zip files once the
digital signing is in place. It would be much better if every download was an exe. it would be way
more professional."* Signing alone does not change it: the download is a zip because each company's
branding (`brand.json`, the logo) is put in at download time, and a signed .exe cannot be changed
after it is signed without breaking the signature. The way out is **one signed .exe for everybody
that learns which company it belongs to without being edited**: from its own file name
(`/downloads/{slug}/…exe` served as e.g. *Anywhere IT Support.exe* with the company's code in it),
with the download's own record as a fallback (Windows writes the address a file came from beside
it, in its Zone.Identifier), and asking for the company's code only if both are lost — then fetching
its branding from the server (`/api/branding` already exists). One .exe, two choices inside it —
*run it once* or *install it so we can help when you are away* — replaces the two files and the
Unblock step. **BUILT 2026-09-30 (HANDOVER §71), not on hardware**: `tools/Pack-ClientExe.ps1`, served at
`/download/{slug}` as `{slug}-support.exe`. Unsigned it still runs, and still warns, until the certificate.

---

## Stage 6 — macOS, Linux, Android and iOS

The largest item and the last, because it is four new codebases and it is worth nothing until the
Windows product is good.

Sequence when we get there: **macOS host first** (the most-asked-for by IT companies), then an
**Android/iOS viewer** (a technician taking a call away from their desk — that is a client, not a
host, and much less work than it sounds), then **Linux host**, then **phone-as-host** for supporting
a customer's handset, which is where a large slice of AnyDesk's actual usage sits.

The protocol is JSON over WebSocket plus binary frames and is not Windows-specific. Capture, input
injection and file paths are. Plan on the shared protocol library being portable and on nothing in
`RemoteSupport.Host` being portable at all.

---

## Rules that apply to every stage

Not new — these are the working agreements from `CLAUDE.md` and handover §7 and §12, repeated here
because a roadmap is exactly where people start guessing instead of measuring.

- **Measure before changing code.** `tools/` exists because "the screen is black" turned out to be a
  rendering bug, and guessing would have sent the fix to the wrong file.
- **Every feature on this list gets a harness that proves it before it is called done.** That is the
  pattern that has already caught a truncated stream, four path-traversal holes, and a clipboard
  that looked shared but was not.
- **Give Clint one file or one command to run**, in its own fenced block.
- Refresh `demo/technician` and `demo/client` from `dist/` before testing, or you are testing
  yesterday's binary. That has wasted time twice.
- Commit before large or risky edits.
