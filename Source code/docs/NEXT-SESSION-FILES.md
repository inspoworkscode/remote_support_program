# Moving things between the two machines — FINISHED 2026-09-15

> **Done.** §22 of `docs/HANDOVER.md` is what was built and how it was proven; `tools/dragoutcheck`
> is 39 of 39 on this laptop, and the short list of what one run against a real machine would close
> is at the end of that section. Clint answered the three questions below on 2026-09-15: the Files
> tab opens on the customer's **desktop**, a drag-out lands **where it is dropped**, and a file
> taken off a machine is **logged, not announced** — a line in the tray app's chat log and on the
> ticket, with no banner and no balloon. Kept for the measurements and the reasoning, not as a
> to-do list.

Written 2026-09-15. Read `docs/HANDOVER.md` first for the architecture, the decisions already made
and the working agreements. This file is the brief for this work only. It is roadmap stage **1.3**
widened: testing on 2026-09-15 showed that drag-out is not the only thing missing, and that the
three ways of moving a file are really one job.

**The goal, in one line:** text, files and folders move between the technician and the customer in
both directions, by drag and drop and by copy and paste, and it is obvious when it worked.

---

## What already works — do not rebuild any of it

All of this is proven on real hardware. `docs/NEXT-SESSION-IO.md` carries the reasoning and §8 of
the handover carries the results.

| | State |
|---|---|
| Clipboard **text**, both ways | Proven, `tools/clipcheck`, 7 of 7 — including that a *separate process* on the customer machine sees the text, which is the only way to tell a shared clipboard from a private one |
| **Files and folders** on the wire, both ways | Proven, `tools/treecheck`, 19 of 19 — a tree pushed, hashed by a separate PowerShell process on that machine, pulled back and compared |
| The whole-tree engine | Proven locally, `tools/filesetcheck`, 20 checks including empty folders, zero-byte files, non-ASCII names, collision renaming and **five hostile paths** |
| **Drag IN** (technician → customer desktop) | Proven end to end, 280 ms for three files, all identical by SHA-256 |
| Pulling one file off a machine | Proven — `tools/fetchfile` did it on 2026-09-15 to recover the screenshot that started this |

**So the wire is not the problem.** Every gap below is in the console's UI or in one unresolved
path on the host. That is worth knowing before a day is spent in `FileSetTransfer`.

---

## What is missing, measured

### 1. Drag OUT is not built at all (the original 1.3)

Dragging *into* the remote view works. Dragging *out of* it does not exist. It was deliberately
left because it is materially harder, and the reason is an ordering problem rather than a WinForms
one:

`DoDragDrop` with a `CF_HDROP` hands the far end **paths**, not bytes. So the files have to be
fetched to a temp folder on the technician's machine **before the drop completes**, or Explorer
gets paths to files that do not exist yet.

**That exact ordering problem is already solved for paste** — the bytes move first as a file set,
and only then is the clipboard pointed at where they landed. Reuse that path rather than inventing
a second one. See §8 of the handover, "A file on the clipboard is a list of PATHS, not content".

### 2. The Files tab shows nothing when you open it — `docs/BUGS.md` #5

`_pathBox` starts empty and **nothing sends a first `FileListRequestMsg`**, so the list is blank
until a path is typed and Go is pressed. This is what "I cannot copy a file from the client
desktop" turned out to mean. The tab *does* render errors (`[Error] …`), so a blank list genuinely
means nobody asked.

Pressing Go with an empty path works and lists the drives: `FileTransfer.List` treats empty, `\`
and `/` as "the drives". So the fix is one request on first open — **to the customer's desktop, or
to the drives, and which one is a judgement call rather than an obvious default.** See the
questions below.

### 3. `%DESKTOP%` is only resolved for drag-in — `docs/BUGS.md` #6

`HostEngine` translates the sentinel for `FileSetOfferMsg` and nowhere else. `FileListRequestMsg`
and `FilePullMsg` pass it through to `FileTransfer`, which treats it as a **relative path** and
resolves it against the host's working directory:

```
Could not find a part of the path 'C:\Program Files\Anywhere IT\Remote Support\%DESKTOP%'
```

The sentinel is the right idea — the host runs as SYSTEM, so the technician's end cannot know the
customer's profile path and must not guess it — it is just half wired. **Resolve it once, where a
path arrives, rather than at each use.** If item 2 is fixed by opening the tab on the customer's
desktop, this must be fixed first or the tab opens on an error.

### 4. Pasting INTO the remote machine is not on the Clipboard tab as a verb

Worth checking before building: the Clipboard tab is deliberately manual (automatic sync would
copy whatever the customer had — very often a password — onto the technician's machine unasked).
Confirm what the two buttons actually do today for **files** as opposed to text, in both
directions, and whether a technician can tell which direction a button acts in without guessing.

---

## What to build

1. **The Files tab opens on something.** One request when the tab is first shown, once per session
   rather than on every click — the Sound tab's `_soundAsked` is the pattern.
2. **Resolve `%DESKTOP%` in one place on the host**, covering list, pull and file-set pull.
3. **Drag out**, reusing the fetch-then-point-at-it ordering that paste already uses.
4. **Make each direction legible.** Drag-in was reported as broken for a fortnight while working
   perfectly, because the only feedback was a label on a hidden tab. Every transfer now reports on
   the status bar with a progress bar — keep that, and make sure drag-out and the Files tab use it
   too.

---

## Constraints carried over — these have each cost real time

- **Validate a received path BEFORE rewriting it.** The collision-rename step turned `..\evil.txt`
  into a legal-looking name that then passed every check below it, and an absolute
  `C:\Windows\Temp\x` went the same way because `Directory.Exists("C:")` is true. Four hostile
  paths were accepted until `filesetcheck` caught it. **The receiving side runs as SYSTEM**, so
  this is full machine rights. One bad entry condemns the whole set.
- **Writing the clipboard needs a real window handle.** `OpenClipboard(NULL)` reads fine, but
  `EmptyClipboard` then sets the owner to NULL and `SetClipboardData` **fails** — documented,
  silent, and indistinguishable from a paste that never arrived. The host keeps a message-only
  window alive for exactly this.
- **The clipboard is per-window-station, not per-desktop**, so `DesktopFollower` does not come into
  it. The host is on WinSta0, measured, so it is the user's real clipboard.
- **A feature with no visible feedback will be reported as broken**, because it is indistinguishable
  from broken.
- **Attach every `WsConnection.OnMessage` handler before `RunAsync`.**
- **A new session has no host for a second or two**, and the hub relays with `s.Host?.Send(m)`,
  a silent no-op.
- Non-ASCII names: compare them **base64-encoded** across the wire. PowerShell 5.1 reads a BOM-less
  file as ANSI and writes stdout in the OEM code page, which turns `café` into `cafÃ©` in either
  direction. `tools/treecheck` does this, and it is why it can tell "the file did not arrive" apart
  from "the file arrived and I cannot read its name".

---

## Questions for Clint — ask before building, not after

- **What should the Files tab show when it opens?** The customer's desktop is the most useful
  answer for support work and is what was being looked for when this came up; their drives is the
  more neutral one and cannot fail. Or their Downloads, which is where things we send them land.
- **Should drag-out land where it is dropped, or always in Downloads?** Dropping onto a folder in
  Explorer implies the first; the second is predictable and never surprises. Drag-in chose "where
  the gesture pointed", so the symmetric answer is the first.
- **How much should a technician be able to take without the customer seeing anything?** Today a
  file can be pulled with no indication on the customer's screen at all. Sound has a banner and a
  disclosure argument behind it (§18); files do not, and the two are not obviously different in
  kind. This is a product decision, not a technical one.

---

## How to prove it

The rule from §7 and §12 of the handover applies: **every item gets a harness, and a transfer must
not mark its own homework.** Most of what is needed already exists:

- `tools/filesetcheck` — the engine, locally, no machine needed.
- `tools/treecheck` — trees both ways over a real session, hashed by a separate process on the
  customer machine.
- `tools/clipcheck` — text both ways, verified by a separate process.
- `tools/fetchfile` — lists a folder and pulls one file; use it to prove the `%DESKTOP%` fix
  without opening the console at all.
- `tools/toolbarprobe` — drives the real `SessionForm` with no server, and already exercises the
  viewing modes. The Files tab's first-open behaviour can be checked there.

**What does not exist yet is a drag-OUT harness**, and it is the hard one: a real drag is a
Windows shell interaction, not a message. Options worth thinking about before starting — driving
`DoDragDrop` against a drop target created in the harness, or asserting on the temp folder and the
`CF_HDROP` the console produces rather than on Explorer's behaviour. Asserting on what we hand the
shell is probably enough, and is certainly better than a manual test nobody repeats.

**The ship trap, worth repeating:** a harness that joins an *existing* session talks to the
`Host.exe` that was running when that session started. Straight after a ship that is the old
binary. **End any open session first.** And after a ship that changes the agent or the tray app,
those need replacing too — a host-only update leaves the other halves silent (§18, §21).

---

## The machines

- Server: this laptop, static **192.168.1.115:8090**, run from `src/RemoteSupport.Platform` in a
  terminal Clint keeps open. **Not elevated** — a server started from an elevated terminal cannot
  be stopped by `ship-build.ps1` from a normal one (§21).
- **DESKTOP-OPHQIDH** — the main test machine, user `Lenovo`. Client + unattended agent + the
  monthly health check, on the current build.
- `tools/ship-build.ps1` puts a new build where it can be tested. Exit the technician console from
  the **tray** first; closing its window is not enough.
