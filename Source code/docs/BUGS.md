# Known bugs

Faults with what was actually observed, what was ruled out, and what evidence is still needed.
Read `docs/HANDOVER.md` first for the architecture.

Entries are marked closed rather than deleted — the elimination is the expensive part and is worth
keeping even when the fault turns out to be a reinstall away. A bug closed because it stopped
happening says so plainly; do not read it as understood.

---

## 1. Ad-hoc connect to an attended-only install: screen works, input does nothing

**Opened** 2026-09-09. **Closed 2026-09-09 — does not reproduce. Cause never found.**

Retested deliberately: uninstalled unattended, installed attended only, connected ad-hoc, and
mouse and keyboard both worked. The fault is gone, but nothing was changed that explains it — no
code touched the session or input path between the failure and the retest, so **this was closed
on behaviour, not on understanding.**

What most likely differed is the install itself rather than the attended/unattended distinction:
the machine had been installed, uninstalled and reinstalled several times that afternoon, and a
half-removed install (a stale `RemoteSupport.Host.exe`, or a tray app left holding an elevated
token — see §4 of the handover on `Start-Process` and inherited tokens) would produce exactly this
signature, since capture and injection fail independently.

**If it comes back**, the evidence needed is one line, and it now survives a reinstall (bug 3):
`C:\ProgramData\RemoteSupport\host-trace.log`, whose first lines name the account the host runs as
and whether the input thread attached to a desktop. Get that *before* reinstalling anything. The
elimination below still stands and does not need repeating.

### What was seen

On `DESKTOP-L2B85US` with **only the tray app installed** (`Install-Client.bat`, no unattended
agent):

- Technician clicks **Connect (unattended / ad-hoc)** → consent prompt appears on the customer
  machine → customer allows → **the technician sees the screen, but mouse and keyboard do
  nothing**.
- The **customer-initiated** path on the same machine, same install, **works** — request support,
  technician accepts, input is fine.
- Reinstalling with `Install-Client-Unattended.bat` (agent present) **fixes ad-hoc connect.**

So: capture works in every case. Injection fails only for a technician-initiated session that the
**tray app** is hosting.

### Ruled out by reading the code (kept — this was expensive and is still valid)

Both paths converge, so the fault is not a different branch being taken:

- **Console** — one `new SessionForm(...)` site (`DashboardForm.cs:321`), used by both.
- **Server** — `CallAccept` (`TenantHub.cs:315`) and `ConnectDevice` (`:326`) both call the same
  `StartSession`, which picks Agent first and falls back to Client identically (`:334`).
- **Client** — both arrive at `OnStartSession` → `HostSpawner.Start(...)` with the same
  `--consent ask`, since `askConsent` is `_brand.RequireConsent` either way
  (`MainForm.cs:170`).

Also ruled out: the **Control** checkbox in the session toolbar (confirmed ticked; it defaults to
`Checked = true` and `ScreenView.ControlEnabled` to `true`).

### What to collect if it returns

`C:\ProgramData\RemoteSupport\host-trace.log`, before touching the install.
`HostEngine.RunAsync` calls `HostTrace.WriteIdentity` at the top of every session, so the log's
first lines name the account and whether the input thread ever attached to a desktop. A healthy
agent-hosted session reads:

```
running as NT AUTHORITY\SYSTEM  isSystem=True
input desktop at startup: Default
input thread followed the user to the Default desktop
```

The client-hosted failing case should read `running as <machine>\User  isSystem=False`, and what
the input thread does after that is the answer. Working theory — **unverified** — is the
integrity/desktop problem of §4 of the handover: the SYSTEM launch lives entirely in the agent
(`SessionLauncher.LaunchAsSystemInActiveSession`), so a tray-app-hosted session has neither the
SYSTEM token nor a working `DesktopFollower`, and Windows discards injected input silently. That
does **not** explain why the customer-initiated path works on the same install, so the theory is
incomplete and the log should be trusted over it.

> The evidence for this was destroyed once already: see bug 3.

---

## 4. The customer download builds a client that points at a server which is not serving

**Opened** 2026-09-11, found while testing the installer download (stage 0 of the health check
work). **Closed 2026-09-13 — fixed by configuration, and the address is now pinned.**

`Platform:PublicUrl` is `http://192.168.1.115:8090`, and this laptop's Wi-Fi address is now a
**manual** 192.168.1.115 rather than DHCP, so the two cannot drift apart again. That matters more
than it sounds: the address is baked into every downloaded client at download time, so a laptop
that moved networks and picked up a new lease would have silently produced clients that could
never connect, with nothing in the portal to show it. Clint set the static address on 2026-09-11
after exactly that happened — the laptop moved to a new network and came back as .125 while the
configuration still said .115.

Still true, and still deferred: for anything outside the LAN this has to become the real domain
with TLS in front of it (§11 of the handover).

**What it was:**

`TenantService.ToBranding` sets the client's `serverUrl` from `Platform:PublicUrl`
(`TenantService.cs:223`), not from anything per-tenant. `appsettings.json` has:

```
"PublicUrl": "https://theremotesupportprogram.com"
```

so every `brand.json` in every downloaded zip says `wss://theremotesupportprogram.com`. That
domain is not serving yet — the live server is on the LAN at `ws://192.168.1.115:8090`.

Measured: the zip downloaded from `/get/anywhere-it` on a copy of the live database contains
`"serverUrl": "wss://theremotesupportprogram.com"`, and the installer's own reachability check
reported `Could not reach https://theremotesupportprogram.com/health` before installing anyway.

So a customer who follows the download page today installs a client that can never connect. This
is separate from, and was hidden by, the missing-installer problem: nothing installed from that
page had ever been run.

**Fix is Clint's call**, because it depends on what that address should be:

- For LAN testing, set `Platform:PublicUrl` to `http://192.168.1.115:8090`.
- For real use, point the domain at the server and put TLS in front of it (§11 of the handover) —
  which is required before any real customer touches it in any case.

Note the installer only **warns** when the health check fails and continues. That is deliberate
(a machine may be installed before the server is reachable) and should stay, but it means this
fault is silent at install time and only shows up as a device that never appears in the console.

---

## 2. Tray app stays on "support in progress" after the session ends

**Opened** 2026-09-09. **Severity: medium** — the customer is told they are still being watched
when they are not.

> **2026-09-13 — cause found, reproduced deliberately, and fixed. Left OPEN until it is seen
> working on a real machine**, because everything below was proven against a *pretend* tray app
> at the protocol level, not against `RemoteSupport.Client.exe` on DESKTOP-OPHQIDH.
>
> **The cause is the one this entry already suspected**, narrowed to the exact gap: `EndSession`
> sends `SessionEndedMsg` to the device connections that exist **at that moment**. If the tray
> app's socket is down when the session ends, it misses the message — and **nothing ever told it
> again**. `DeviceLink` reconnects on its own and the server used to send a reconnecting device
> nothing at all. That is why it was intermittent: a tray app that happened to be connected when
> the session ended was always told correctly.
>
> **Reproduced on purpose** by `tools/reconnectlocal`, CHECK 2b: start a session, kill the device
> socket, end the session while it is down, bring the socket back. Before the fix that machine is
> never told; after it, it is sent `SessionStateMsg { Active = false }` the instant it reconnects.
> CHECK 2 covers the milder case — a blink mid-session, where it is now told the session is still
> running rather than being left to guess. 18 of 18 checks pass.
>
> **The fix** is `SessionStateMsg`, sent to every device connection as it appears (both hubs) and
> handled in `MainForm.OnSessionState`. See §19 of the handover.
>
> **To close this properly**, after the next ship: on DESKTOP-OPHQIDH, with a session running,
> disconnect its network for a few seconds, end the session from the console while it is off, then
> reconnect it. The tray app must go to "session ended" on its own. Until somebody has watched
> that happen, this is a fix with a good argument behind it rather than a fix that is known to
> work — and the tray app has to be **replaced**, not just `Host.exe`, or the indicator stays
> silent while everything else looks right (§18).

Reported after ending a session from the technician console; the customer's tray app continued to
show support in progress. Not yet reproduced deliberately, and it is not known whether it clears
on its own or persists until restart.

The wiring looks correct on both sides, so something in between is at fault:

- `TenantHub.EndSession` sends `SessionEndedMsg` to the host, every viewer, and **both** the
  Client and Agent device connections (`TenantHub.cs:405-406`).
- `MainForm`'s `OnSessionEnded` calls `HostSpawner.Stop`, clears `_sessionActive` and `_ringing`,
  and calls `UpdateStatus()` (`MainForm.cs:178`).

Worth checking first: whether the tray app's websocket dropped and reconnected around the end of
the session, since nothing re-sends state to a device that reconnects — a reconnecting client
would never learn the session had ended.

---

## 3. Uninstall destroys the diagnostic trace (fixed)

`Uninstall-Client.ps1` removed `%ProgramData%\RemoteSupport` wholesale, taking `host-trace.log`
with it. Uninstall-and-reinstall is the commonest troubleshooting step, so the standard fix
erased the record of the fault being investigated — which is exactly what happened to bug 1 on
2026-09-09.

**Fixed** 2026-09-09: the uninstaller now copies `*.log` to `%ProgramData%\RemoteSupport-logs`
with a timestamp before removing the folder.

Note that the same delete also removes `device.json`, which is what produces a duplicate `Device`
row on reinstall (§15 of the handover). That is still open and is a separate decision — the log
copy deliberately does not change identity behaviour.

---

## 5. The Files tab shows nothing when you open it

**Opened** 2026-09-15. **CLOSED 2026-09-16 on DESKTOP-OPHQIDH.** **Severity was: medium** — it is
the only built-in way to get a file *off* a customer machine, and it looked broken before it had
been asked anything.

> **Closed.** Clint opened the Files tab against that machine and the customer's desktop appeared
> **on its own**, with nothing typed and no Go pressed — which is the entire claim this bug makes.
> He then dragged a file off that listing onto his own desktop and it opened, so the same listing
> is the one drag-out works from.
>
> Both halves are now proven where it counts: `fetchfile --list` closed the sentinel resolution
> underneath (BUGS #6), and the tab asking on first open is confirmed by eye on a real session.

> **The fix.** `SelectTab` asks for the customer's desktop the first time the Files tab is shown,
> once per session — the Sound tab's `_soundAsked` pattern. Their desktop rather than their drives
> is Clint's decision of 2026-09-15: it is where the screenshot or the error log that started the
> call actually is. A machine that cannot answer — one at the logon screen, or running a host too
> old to know the sentinel — **falls back to the drives and says so on the status bar**, so the tab
> can never open on a red error line, which would be this same fault wearing a different hat.
>
> **Proven by `tools/dragoutcheck` CHECK 5**, which runs the real `SessionForm` against a real
> websocket the harness answers as if it were the customer's machine. The assertion is on the
> message that actually left the console, not on a flag the console set itself — a flag would have
> agreed with the bug. The harness answers that first request with the exact error a machine at
> the logon screen gives, so the fallback is exercised rather than reasoned about. It also checks
> the request happens **once per session**, not on every click.
>
> **To close it properly**, open the Files tab on DESKTOP-OPHQIDH after a ship and see the
> customer's desktop listed without typing anything.

Reported as "I cannot copy a file from the client desktop to mine". Two separate things are true,
and only the first is this bug:

**Nothing requests a listing when the tab is opened.** `_pathBox` starts empty and no code sends a
first `FileListRequestMsg`, so the list is blank until something is typed and Go is pressed. The
tab *does* render errors (`ShowFileList` writes `[Error] …`), so a blank list genuinely means
"nobody asked", not a swallowed failure.

Pressing Go with an empty path does work and lists the drives — `FileTransfer.List` treats empty,
`\` and `/` as "the drives". So the fix is probably one request on first open, to the customer's
desktop or to the drives. Which it should be is a judgement call, not an obvious default.

**Measured:** listing `C:\Users\Lenovo\Desktop` through the same wire path returns the folder
correctly, so nothing below the UI is wrong.

```
dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- DESKTOP-OPHQIDH --list "C:\Users\Lenovo\Desktop"
```

Belongs to `docs/NEXT-SESSION-FILES.md`.

---

## 6. `%DESKTOP%` is only resolved for drag-in

**Opened** 2026-09-15. **CLOSED 2026-09-16 on DESKTOP-OPHQIDH.** **Severity was: low on its own,
but it was a trap for the next person.**

> **Closed.** `fetchfile --list` with no path, against the shipped host, answered with
> `C:\Users\Lenovo\Desktop` and its seven items — the `PC Health Check` folder, the diagnostic
> text file, the two screenshots and the rest. Before the fix that same command returned
> `Could not find a part of the path 'C:\Program Files\Anywhere IT\Remote Support\%DESKTOP%'`.
> The sentinel is resolved by the host against the logged-on user, on a machine, over the wire.

> **The fix is `RemoteSupport.Host/HostPaths.Resolve`, called where a path ARRIVES** — in
> `HostEngine`'s message switch, for `FileListRequestMsg`, `FilePullMsg`, `FileSetPullMsg`,
> `FileOfferMsg` and `FileSetOfferMsg` alike — rather than at each use. That distinction is the
> whole point: resolving at the use site is exactly how it came to be wired for one message out of
> five, and below that line nothing in the host has to know the sentinel exists. `FileSetTargets`
> is now `RemotePaths` and has `%DOWNLOADS%` and `%DOCUMENTS%` as well. An unknown sentinel is
> **passed through unchanged and traced**, never quietly treated as a relative path, because that
> is this bug in miniature.
>
> **Proven by `tools/dragoutcheck` CHECK 6**, which asserts the properties rather than comparing
> the answer to the code that produced it: fully qualified, ends in `\Desktop`, exists, and is not
> under `config\systemprofile`. Real paths and the three spellings meaning "the drives" come
> through untouched.
>
> **To close it on hardware**, one command, which drives the same wire path the Files tab uses:
>
> ```bash
> dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- DESKTOP-OPHQIDH --list
> ```
>
> It defaults to `%DESKTOP%`. Before the fix it printed
> `Could not find a part of the path 'C:\Program Files\Anywhere IT\Remote Support\%DESKTOP%'`;
> it should now print the contents of `C:\Users\Lenovo\Desktop`. **Ship first, and end any open
> session** — a harness that joins an existing session talks to the `Host.exe` that was running
> when it started.

`HostEngine` translates the `FileSetTargets.Desktop` sentinel into the logged-on user's desktop for
`FileSetOfferMsg` and **nowhere else**. `FileListRequestMsg` and `FilePullMsg` pass it straight to
`FileTransfer`, which treats it as a **relative path** and resolves it against the host's working
directory:

```
the machine could not list %DESKTOP%:
  Could not find a part of the path 'C:\Program Files\Anywhere IT\Remote Support\%DESKTOP%'
```

That is why it matters: the host runs as SYSTEM, so the technician's end cannot know the customer's
profile path and must not guess it — the sentinel is the *right* idea, it is just only half wired.
Anything that later tries to be helpful by opening the Files tab on the customer's desktop will hit
this.

The fix is one place: resolve the sentinel once, where a path arrives, rather than at each use.

Belongs to `docs/NEXT-SESSION-FILES.md`.

---

## 7. The desktop shortcut on the customer's machine has no logo

**Opened** 2026-09-15 by Clint, from a machine in front of him. **Fixed 2026-09-15 — proven by
`tools/iconcheck`, 19 of 19, with no install; not yet seen on a machine.**
**Severity: low technically, high in what it says** — it is the first thing a customer sees of the
product, on **every machine installed so far**, and it looked unfinished.

### What it was

Two separate things had to both be missing, and they were:

- **`Install-Client.ps1` never set `IconLocation` on the shortcuts** it creates. A `.lnk` draws its
  icon from that path, which the shell reads whenever it repaints — long after the installer and
  the tray app have gone. So the tray app's rendered icon could never have served it, however good
  it looks in the tray.
- **With no `IconLocation`, Windows falls back to the icon embedded in the exe** — and no csproj in
  the solution sets `ApplicationIcon`, so that is the generic .NET one.

### Why the obvious fix is the wrong one

Adding `<ApplicationIcon>` would work for Anywhere IT and be wrong for the product. This is
multi-tenant: **every reseller downloads the same binaries** with their own `logo.jpg` beside
them, and an icon compiled in at build time would put Anywhere IT's logo on a reseller's
customers' desktops. The icon has to be rendered per install, from that install's branding, which
is the rule §10 of the handover already states for the tray.

### The fix

`BrandIcon.WriteIcoFile` renders a real multi-size `.ico` file (16 to 256 px) from the tenant's
logo, and the client exe exposes it as `--write-icon <path>` — handled before the single-instance
mutex and before anything touches WinForms, so it cannot fail silently while the tray app is
running (which during an upgrade is exactly when the installer calls it) and can never become an
elevated child that shows a window (§4). The installer renders it into the install folder and
points both the desktop and Start menu shortcuts at it. **Same renderer as the tray icon**, so the
two cannot drift apart.

### What the harness found, and the wrong conclusion it nearly caused

`tools/iconcheck` failed on its first run: asking for the 256 px frame returned 128. The file was
correct — its directory entry was properly marked `0` and its DIB header said 256×512.
**`System.Drawing` simply will not return a 256 px frame, in either PNG or DIB form, and hands
back the next size down without saying so.** The harness was reading with the wrong reader and
reported a fault the code did not have, which is the one thing a diagnostic must never do (§18,
§19). It reads with `PrivateExtractIcons` now, which is what the shell itself uses.

### Known and deliberate: the icon has a black background

`logo.jpg` is a JPEG and has no alpha channel, so the rendered icon is the logo **including its
background**. The tray icon has always looked the same way for the same reason. It cannot be fixed
in the renderer without keying out a colour, which would mangle any logo that legitimately has a
dark background. **To fix it, supply a transparent `branding/logo.png` or a hand-made
`branding/app.ico`** — the code already prefers both.

### To close it

Reinstall on DESKTOP-OPHQIDH and look at the desktop. If an old shortcut is still showing the
blank icon after an upgrade, that is the shell's icon cache rather than this.

---

## 8. A file transfer over a slow link loses a chunk, and neither end says so

**Opened** 2026-09-17, found by reading while briefing stage 2.3, **not by anyone reporting it.**
**FIXED the same day; proven locally by `tools/filesetcheck` (26 of 26) and then REPRODUCED ON REAL
HARDWARE against an un-updated machine -- see "Seen in the wild" below.**
**Not observed before this**, and the reason is now clear rather than lucky: every transfer proven
until 2026-09-17 (`treecheck` 19 of 19, `filesetcheck`, the hardware run of 2026-09-16) was small
and over a LAN. **The first genuinely large push over a real link hit it within two minutes.**

### What was true

Nothing acknowledges, sequences, re-requests or re-sends a file chunk. `FileSetSender` read the
source and pushed chunks at the socket in a loop; the send queue silently evicted the oldest
queued item once full. A chunk lost that way was gone for good.

**The first version of this entry said that produced "a silently corrupt file reported as a
successful transfer". That was wrong**, and the correction matters because the two paths failed
differently and neither symptom is the one that was written down:

- **A file SET hung.** `_written[index]` never reached the file's size, so the file was never
  closed, `_filesDone` never reached `_filesTotal`, `Chunk()` never returned true and `Done()` was
  never called. The transfer simply stopped, for ever, holding an open handle on a file with a
  zero-filled hole in it. No error, no completion, no release of the lock.
- **A SINGLE file did report false success.** `FileTransfer.SendFileAsync` sent
  `FileDoneMsg { Ok = true }` the moment it finished *reading* the file, regardless of what had
  arrived — so the technician was told a file had come across whole when it had not. That is the
  failure mode worse than no transfer at all, and it was real on that path only.

### The fix, in three parts

**Backpressure, which is the actual repair.** The send callback is now `Func<byte[], bool>` and
`FileSetSender.SendChunkAsync` waits for room rather than continuing — five milliseconds backing
off to two hundred, for up to ninety seconds. Both file-set senders, both single-file senders and
the technician's inline send all go through it. The deadline exists so a dead socket ends the
transfer instead of hanging it, not to enforce throughput.

**A gap check, so a loss that still happens is loud instead of silent.** Chunks travel front to
back down one ordered socket, so a chunk that does not start where the last one ended means one
was lost — there is no legitimate way to arrive out of order. Both receivers now detect that,
delete the partial file, name it in `Skipped` with the byte offsets, and count the file as done so
the set can complete and report failure. **This is what turns the hang into an error.**

**An honest single-file result.** `SendFileAsync` reports `Ok = false` when the link would not
carry the whole file, instead of announcing success it cannot know about.

### Why there is no SHA-256, though this entry first proposed one

Verification was going to be the second half, and on inspection it earns nothing. The bytes travel
over WebSocket on TCP, which already guarantees that what is delivered is what was sent; the only
realistic loss was a whole dropped frame, and the offset gap check detects exactly that, precisely,
at the moment it happens and for no cost. A hash would re-read every file to catch a case that
cannot occur. **If a future transport ever loses or reorders below us, this reasoning is what has
to be revisited** — the check is not defence in depth, it is a check aimed at one mechanism.

### How it was proven

`tools/filesetcheck` grew a LOSSY LINK section, and it tests both directions, because a check that
cannot tell the two apart is not a check:

- A link that **accepts a chunk and then loses it** — the old behaviour exactly. The receiver now
  reports `the link lost part of this file — expected the next piece at 65,536 bytes and it
  arrived at 131,072`, the transfer reports failure, and the half-written file is deleted rather
  than left behind looking whole. Before the fix this printed nothing at all and the transfer hung.
- A link that **refuses a chunk twice and then takes it**. The sender waits, nothing is skipped,
  the transfer succeeds, and the file is identical by SHA-256.

The first run of this check reported a fault that was its own: it used a 1,200-byte file, which is
one chunk, so there was no second chunk to lose.

### SEEN IN THE WILD 2026-09-17, which is better evidence than the harness

Pushing a 71.7 MB `RemoteSupport.Host.exe` to DESKTOP-2N6FVGT over the real link reproduced this
exactly, against the OLD host still running on that machine:

- `pushfile` reported `no FileDoneMsg within 120s`.
- The file on that machine was **exactly the right size** (71,726,805 bytes) and **still locked by
  the receiving process**, minutes later.
- Nothing was reported to either end. No error, no completion.

That is the file-set hang described above, on the single-file path, with the zero-filled hole and
the open handle, on hardware. The full size is the giveaway and the trap: `fs.Position = offset`
extends the file to the final offset even when an earlier chunk never arrived, so a file of exactly
the right length proves nothing at all.

**It also found a sender this fix had missed.** `tools/pushfile` has its own inline chunk loop and
was still throwing away the result of `SendBinary`, so it blasted 1,095 chunks at the socket and
overran the queue. Fixed the same way, through `FileSetSender.SendChunkAsync`. The lesson is that
the unfixed senders were not all in `src/` -- a grep for the API is not the same as a grep for the
PATTERN.

### To close it properly

A real transfer over a genuinely poor link — a phone hotspot with one bar — pushing a folder of a
few hundred MB to DESKTOP-OPHQIDH and pulling it back, compared by hash. Everything above is proven
on one machine with a simulated link, which is where the logic lives, but the thing this bug is
about is a real socket that cannot keep up.

---

## 9. ESET blocks the health check install, releases the file a minute later, and nothing ever tries again

**Opened** 2026-09-28. **Fix BUILT 2026-09-29 (HANDOVER §53) - retries in the agent, retry from the console. Not yet on hardware, and the log below is still not collected.**

This also **answers the question stage 5 of `ROADMAP-PARITY.md` has carried since it was written** —
*"whether ESET objects to an unsigned script running as SYSTEM during a health check run."* It does.

### What was seen

Clint, 2026-09-28, on a customer machine running ESET:

> The problem with the unsigned work is ESET. What I have found is that it blocks host.exe messing
> with the healthchecker. It then comes back after a bit, says the file is safe, but by then the
> scheduled installer has already failed.

That is ESET's normal shape for an unknown binary: hold it, ask LiveGrid, release it when the cloud
answers. The hold is on the order of a minute. **Everything we do to recover is slower than that,
and the one thing a human would reach for does nothing at all.**

### Why it does not recover — three layers, all found by reading the code

**1. The agent tries exactly once.** `HealthCheckKeeper.Want` queues a state, `Apply` acts on it,
the result is reported, and the pass ends (`HealthCheckKeeper.cs:60`, `:84`). The install path runs
PowerShell once (`:100`), re-reads the machine, and if the task is not there returns
`"could not install it (exit N)"` (`:103`). There is no retry anywhere in that file.

**2. The console refuses to re-send the same choice.** `DashboardForm.cs:241`:

```csharp
if (d == null || d.HealthCheckMode == mode) return;
```

So a technician ticks **On for this machine**, ESET blocks it, the menu's top line correctly reports
the failure (§41 discipline — it shows what the MACHINE said, not the wish) — and **clicking On
again is a no-op**, because the stored mode is already On. The only retry available from the console
is **Off then On**, which runs the *uninstall* path first and is exactly the wrong thing to do to a
machine whose files are being held.

**3. The only automatic retry is the agent reconnecting.** `TenantHub.cs:332` sends the wanted state
when an agent registers, and `SetDeviceHealthCheckAsync` (`:1594`) sends it on a change. On a machine
that stays up and connected, neither happens for days.

So: ESET holds for a minute, and our three recovery paths are "a technician toggles it off and on
again", "the machine reboots", and "somebody changes a setting".

### What the architecture change did and did not do

The health check **stopped being installed by `Install-Client.ps1`** on 2026-09-28 (commit
`1bec870`); it is now installed on demand by the agent when the server says so — the customer's tick
in the portal, or the per-machine override on the console's right-click.

That **sharpened this fault rather than causing it.** Before, the install ran during client
installation with a technician standing at the machine, so a failure was in front of somebody who
would re-run the installer. Now it is a background action on a remote machine, and the failure is a
line you see only if you right-click that machine again.

The one thing it made better: there is now a single place where a retry belongs
(`HealthCheckKeeper.Apply`), instead of logic spread through the client installer.

### The fix — BUILT 2026-09-29, as designed below except the delays

Built in `HealthCheckKeeper.ApplyWithRetriesAsync` and `HealthCheckModes.WorthSending`; proved by
`tools/keeperretry` (31 of 31). **The delays are 1, 5 and 15 minutes** - four attempts over about 21
minutes rather than three over six, since a later try costs nothing. Removal is retried too. The
portal's dropdown still cannot retry; the console can. HANDOVER §53 has the detail. The design as
written:

Two parts, deliberately small:

1. **Retry with backoff in `HealthCheckKeeper`** — roughly three attempts at 0, 60 s and 5 minutes
   before reporting failure. That covers ESET's block-and-release window almost exactly and costs
   nothing when nothing is wrong. **The detail that matters:** the loop must re-check `_pending`
   between attempts and abandon if a newer state arrived, or a machine switched off mid-retry gets
   reinstalled anyway.
2. **Relax the console guard** so choosing "On" when the mode is *already* On but
   `HealthCheckInstalled` is false re-sends the instruction. That makes the existing menu item a
   retry exactly when one is needed — **no new control**, which is the standing rule.

Contained to `HealthCheckKeeper.cs` and `DashboardForm.cs`. No protocol change, no migration.
Changes the AGENT, so it needs a full ship, not `-OnlyTechnician`.

### What code signing does and does not fix

Signing (stage 5, in progress — see `CODE-SIGNING-ORDER.md`) removes the **trigger**: ESET stops
treating our binaries as unknown. It does **not** remove the fragility. A newly built signed binary
still has no reputation on first sight, and any product that holds a file for a few seconds would
produce the same permanent-looking failure. **The retry is worth building whether or not the
certificate ever arrives**, and the certificate is two to four weeks away.

A per-machine ESET exclusion for the install folder works as a stopgap on our own machines. It is
not something to build the product around — every reseller's customer would need it.

### What to collect — this has NOT been done

The log already exists. `HealthCheckKeeper.Log` (`:264`) writes every attempt's exit code and full
stdout and stderr to `%ProgramData%\RemoteSupport\healthcheck-agent.log`. On the affected machine:

```powershell
Get-Content "$env:ProgramData\RemoteSupport\healthcheck-agent.log" -Tail 80
```

Two things the code cannot answer and that log plus ESET's own detection entry can:

- **Is ESET flagging the scripts, or genuinely `host.exe`?** The keeper never invokes `host.exe` —
  it runs `powershell.exe` against `Install-HealthCheck.ps1`. If it really is `host.exe` being held,
  that is a **second, separate problem** affecting sessions rather than the health check, and the
  retry above would not touch it.
- **Did the install get far enough to register the task before dying?** That decides whether a
  retry lands on a clean machine or a half-installed one.

Collect both before building the fix. The diagnosis above is from reading code, not from watching it
fail.

## 10. The first copy after a fresh logon never reaches the technician

**Opened** 2026-09-29, found while measuring HANDOVER §54. **Severity: low to medium** — one copy
lost per machine per logon, silently, and it is the copy a technician asks for first. **FIXED 2026-10-06 — HANDOVER §93.** The first copy on an unused clipboard is now reported; see `tools/clipfirst`. Proved against Windows itself in a fresh window station (`clipfirst --station` 9 of 9, elevated, 2026-10-06); not yet seen on a freshly restarted machine (TEST-3.6 check 421).

### What was seen

DESKTOP-2N6FVGT read clipboard sequence **0** for the whole of the morning of 2026-09-29, from the
host and from a SYSTEM script beside it (`tools/hostinfo/stationprobe.ps1`, `clipsasdiag.ps1`):
nobody had copied anything since it started. Every host that morning wrote
`clipboard: no sequence number yet - waiting for a window station with a clipboard on it` and
never `watching for changes`.

### Why a copy is lost, by reading the code

`ClipboardAccess.WatchPump` waits while the sequence is 0 and then takes the first non-zero value
as its **baseline** (`_lastSeq`). On a machine where the counter really was 0 — nothing copied
since logon — the first non-zero value IS the customer's first copy, and it is treated as where
things started rather than as a change. The second copy goes through.

### Why it is not fixed yet

The wait exists for a different zero: `clipmirror` found the first read at process start can come
back 0 when the clipboard is NOT empty. Treating every zero-to-non-zero as a copy would send
whatever was already on the clipboard the moment a technician subscribed — the password copied at
nine this morning, which is exactly what §26's privacy gate exists to prevent. A fix has to tell
those two zeros apart (a zero that lasted seconds is a real one; a zero that cleared in the first
moments is the start-up one) and must not fire for the host's own `--put-files` write, which also
moves the counter off zero. None of that can be proved until a machine is freshly restarted.

### To close it

Restart DESKTOP-2N6FVGT, open a session before anybody copies anything there, confirm the host
trace says `no sequence number yet`, then copy text on that machine: it should arrive, and today it
will not. That is the reproduction a fix has to pass, alongside `clipcheck`'s leak checks.

## 11. Thousands of machines connecting at once stall the whole server for seconds

**Opened** 2026-10-07, found by the first run of `tools/loadtest` (HANDOVER §115). **Severity: high
before scale, invisible today** — it is what happens after EVERY restart or deploy once there are a few
thousand installed machines, because they all reconnect within seconds. **FIXED 2026-10-08 on PostgreSQL — see "What it was" at the end; SQLite still stalls, which is why the platform is moving (HANDOVER §121).**

### What was seen

`loadtest local --companies 100 --machines 50 --sessions 0 --ramp 30` (5,000 machines in 30 s, about
170 a second, on this PC): a machine took a median **5 s** to connect (95% within 12 s), some were
refused outright, and a plain `curl /health` from ANOTHER process — a page that does no work at all —
took **1.4 to 13 s** until the ramp ended, then 1 ms. The server's processor sat at **6–7%**, and its
thread count crept from 47 to 69: the thread pool adding threads at its own slow pace.

### What it is, by measurement

Thread-pool starvation: something on the connect path blocks a worker thread. The same run with
`--env DOTNET_ThreadPool_ForceMinWorkerThreads=200` (512 threads ready, hex): connect median **2 ms**,
`/health` 2 ms throughout, nothing refused or dropped. Ruled out by measurement: SQLite's journal
mode (`--wal`, no change) and logging volume (`Logging__LogLevel__Default=Warning`, no change).

### Not yet known

WHICH call blocks. Leading suspect: Microsoft.Data.Sqlite does its "async" work synchronously, and a
hello makes several database calls (`ByKeyAsync`, `PersistDeviceAsync`, the outsider and branding
lookups). A stack capture during a ramp (`dotnet-stack`) would say for certain. Also not known: whether
the live Linux server behind Caddy behaves the same — `deploy/loadtest` measures that, with and without
`Prepare-LoadServer.ps1 -MoreThreads`.

### To close it

A stopgap is `ThreadPool.SetMinThreads` at start-up; the real fix is not blocking (PostgreSQL's driver
is truly asynchronous). Either way the proof is the ramp above: connect p95 under 1 s and `/health`
never above 100 ms.

### What it was (2026-10-08, PostgreSQL step 3)

**Not the database's journal, not logging, and not mainly SQLite: loading cold company hubs.** `HubRegistry.Get`
loaded a company's hub SYNCHRONOUSLY (about eight queries in the `TenantHub` constructor), and
`ConcurrentDictionary.GetOrAdd` ran that constructor on EVERY thread that asked before the first finished —
after a restart, every company's machines at once. Each query held a thread; the pool grew slowly.

How it was told apart: `loadtest local --warm` connects one machine per company first. Warm, on PostgreSQL:
5,000 machines in 30 s at a 3 ms median, nothing failed. Cold: a 30 s freeze at 1% processor, the thread count
creeping 50 → 95, then a burst. Fixed by loading each hub ONCE and asynchronously (`HubRegistry.GetAsync`, a
`Lazy<Task<…>>` per company; `TenantHub.CreateAsync`); the socket endpoints await it; `Loaded()` for events
that must never load one. Cold, after: **PostgreSQL 3 ms median, 59 ms at 95%, 0 failed, 0 dropped.**

**SQLite still stalls with the fix in** (8.9 s median, 682 dropped): its driver does its "async" work
synchronously, so a second blocker the first one hid remains. That is the measured case for PostgreSQL.

On the way, PostgreSQL's own first run was WORSE (1,847 of 5,000 held): Npgsql's pool and PostgreSQL's
`max_connections` are both 100 by default, so the rush asked for connections PostgreSQL refused ("remaining
connection slots are reserved…"), and every refusal dropped a machine. The platform now caps its pool at 40
unless the connection string says otherwise (`PgPlatformDb.Prepare`); beyond that a request waits its turn.

## 12. Past ~28,000 connections the whole server stops — Caddy runs out of ports to the platform

**Opened** 2026-10-13, found by the xneelo load test (HANDOVER §126). **Severity: critical at scale, invisible
today** — live has a handful of machines against a ceiling of ~28,000 connections (about 27 companies of 10
technicians and 500 machines each). **FIXED 2026-10-13 and PROVEN on the xneelo load server (`39d6498`, HANDOVER §126):
Caddy reaches the platform through `/run/rsp/platform.sock`; the same 30 companies on live's default port range carried
comfortably — 0 connections over TCP, 30,902 through the socket, all 28,232 ports left. NOT YET ON LIVE: it needs a
`Publish-Platform.ps1` deploy, and step 7 must say `platform on /run/rsp/platform.sock, as caddy -> HTTP 200`.**

### What was seen

30 companies (~30,900 connections) on a server set up exactly as live: Caddy's connections to `127.0.0.1:8090` stopped
at **28,234** — the whole of Linux's default `ip_local_port_range` (32768–60999, 28,232 ports) — and everything past it
was answered **502**: 185,000 machine connects in 20 minutes, and **every one of the 300 technicians' sign-ins**,
because an ordinary HTTP request needs a port too. The portal and the consoles were dead for everybody on the server,
not just the machines over the line; Caddy, refusing the machines' retries every 3 s, held the server at 100% processor,
and `/health` took up to 10.5 s.

### What it is

Caddy reaches the platform over TCP on one address and port; each proxied WebSocket holds one connection from one
local port for as long as it lives, and Linux gives one destination only the port range. Proved by `-WidePorts`
(1024–65535 on the load server only): the same 30 companies then carried comfortably (30,903 connections, 40% processor).

### To close it

Kestrel listening on a Unix socket (`ListenUnixSocket`), Caddy `reverse_proxy unix//run/…`: no ports at all. Changes
`go-live.sh` (Caddyfile and the unit's socket directory and permissions) and the platform's listen settings; the
platform must still answer `/health` for the deploy checks. Prove it with the load test at 30 companies on live's
default port range. `-WidePorts` (sysctl) is a stopgap that moves the ceiling to ~62,000, not a fix.

## 13. A message sent straight behind a machine's hello is dropped

**Opened** 2026-10-18, found by `tools/requestedname` (HANDOVER §150). **Severity: low — no real app is known to do it.**

### What was seen

A pretend tray app said hello and sent `RequestCallMsg` at once. On PostgreSQL the request never reached the hub: no ring,
no ticket, nothing in the server's log after *device … online*. On SQLite the same harness passed — the gap is shorter.

### What it is

`Program.cs`'s `/ws/device` reads the hello, then looks the company up (`tenants.ByKeyAsync`), and `RunDeviceAsync` reads the
company's clock (`UseCompanyClockAsync`) before `ws.OnMessage += HandleDeviceMessage` (`TenantHub.cs`, ~line 751). The
receive loop is already running, so anything arriving in between is raised to no handler and lost.

### To close it

Queue messages from the end of the hello until the handler is attached, then hand them over in order. Check first whether
anything real sends immediately after a hello — the free installer's one client connection (§103) answers by `RequestId` and
may already be retrying around it. A harness: hello and a message in the same breath, on PostgreSQL.
