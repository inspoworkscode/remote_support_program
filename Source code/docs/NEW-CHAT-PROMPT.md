# Starting the new chat

Rewritten 2026-09-15, for the file and clipboard work (roadmap 1.3, widened).

Open a new Claude Code session with the working directory set to
`C:\Users\Clint\Desktop\Remote Support`, then paste the message below as the first thing you send.
`CLAUDE.md` loads by itself and points at the rest.

---

## Paste this

> Read `docs/HANDOVER.md` and then `docs/NEXT-SESSION-FILES.md` before doing anything. Between them
> they carry the architecture, the decisions already made, the traps that each cost a day, and the
> brief for this piece of work. Don't re-derive any of it, and don't read the whole codebase to
> rebuild context — the measurements in the brief were made deliberately so you don't have to.
>
> The job is one sentence: **text, files and folders move between my machine and the customer's in
> both directions, by drag and drop and by copy and paste, and it is obvious when it worked.**
>
> **Read "What already works" in the brief before you plan anything, and believe it.** The wire is
> already proven on real hardware — `tools/treecheck` 19 of 19 for trees both ways, hashed by a
> separate process on the customer's machine; `tools/clipcheck` 7 of 7 for text; `tools/filesetcheck`
> 20 checks on the engine including five hostile paths. So **every gap left is in the console's UI
> or in one unresolved path on the host.** Do not spend a morning in `FileSetTransfer`.
>
> The three gaps, all measured on 2026-09-15:
>
> 1. **Drag OUT does not exist.** Dragging in works. Out needs `DoDragDrop` with a `CF_HDROP`, which
>    hands the far end *paths, not bytes* — so the files must be fetched to a temp folder before the
>    drop completes. **That ordering problem is already solved for paste** (bytes first, then point
>    the clipboard at where they landed). Reuse that path rather than inventing a second one.
> 2. **The Files tab shows nothing when I open it** — `docs/BUGS.md` #5. Nothing sends a first
>    `FileListRequestMsg`, so it is blank until I type a path. That is what "I can't copy a file off
>    the client" turned out to mean. It is not a broken transfer.
> 3. **`%DESKTOP%` is only resolved for drag-in** — `docs/BUGS.md` #6. List and pull treat it as a
>    *relative path* against the host's working directory. Fix it in one place, where a path
>    arrives, not at each use.
>
> **There are three questions for me in that brief. Ask them before writing code**, not after: what
> the Files tab should show when it opens, whether drag-out lands where I drop it or always in
> Downloads, and how much a technician should be able to take without the customer seeing anything.
> That last one is a product decision like the listening indicator was — don't assume a default.
>
> A harness for drag OUT does not exist and is the hard one, because a real drag is a Windows shell
> interaction rather than a message. Think about how you would prove it before you build it —
> asserting on the temp folder and the `CF_HDROP` we hand the shell is probably enough, and is
> certainly better than a manual test nobody repeats.
>
> One lesson from the last session that is worth carrying in: **three separate faults were each
> reported as a success by the layer underneath.** The host said a Ctrl+Alt+Del was carried out, the
> agent said the same, and a tool showed an empty folder where the host had returned an error it
> was not printing. Every one was telling the truth about the wrong question. When something "does
> nothing", go to the layer that would *notice* — a desktop that should change, a row that should
> appear, an error field nobody reads — not the layer that reports.
>
> Some things I care about, which are in the handover but worth saying twice: measure before
> changing code and prove where something breaks rather than guessing; don't let a feature mark its
> own homework; test any script locally before it goes near a customer machine; give me one file or
> one command to run, in its own code block, not a list of steps; and never ask me for a password in
> chat. Show me the result rather than describing it — a screenshot of the real window beats a
> paragraph about it.
>
> My test machine is DESKTOP-OPHQIDH, user `Lenovo`, on the current build. The server runs from
> `src/RemoteSupport.Platform` on port 8090 out of my own terminal, **not elevated** — building the
> whole solution fails while it holds `RemoteSupport.Shared.dll`, so build individual projects or
> ask me to stop it. `tools/ship-build.ps1` is what puts a new build where it can be tested;
> `deploy-client-payload.ps1` alone is not enough, and I have to exit the technician console from
> the **tray** first because closing its window only hides it. End any open session before running a
> harness after a ship, or it will talk to the old `Host.exe` and report the new feature missing.

---

## What is already done — don't redo it

- **The file and clipboard wire, both ways.** `tools/treecheck` 19 of 19, `tools/clipcheck` 7 of 7,
  `tools/filesetcheck` 20 checks, and a dragged folder arriving by SHA-256 in 280 ms. HANDOVER §8.
  **Only drag OUT and the UI gaps above are left.**
- **Reconnect and the system-action buttons**, proven on a machine. `tools/reconnectcheck` 10 of 10;
  Ctrl+Alt+Del working once `SoftwareSASGeneration` is set, which the installer now does; restart
  holding for five minutes and re-attaching; Task Manager on the System menu. HANDOVER §19 and §21.
- **Audio.** Streaming with a Listen button, a level meter, a disclosure indicator on the customer's
  tray app, and the Sound tab that says *why* a machine is silent. HANDOVER §18.
- **The monthly health check** (§17) and **the trial-expiry warnings** (§20) — portal banner and
  email, 18 of 18 by `tools/trialnoticecheck`.
- **The ticketing portal**, device identity across a reinstall, and the customer-side overlays.
  §16, §15, §8.

## Still open, and named so they aren't lost

- **The platform mailbox is not configured.** Trial notices send nothing until it is set at
  `/owner/mail`. The portal banner works regardless.
- **The Opus question.** The bandwidth is measured (§18) and does not settle it: against a busy
  screen audio is 3% of the traffic, against an idle one it is all of it. It turns on the customer's
  uplink. **Clint's call, under the "nothing is bought in" rule.**
- **Stage 2 — the codec — is untouched**, and it is what a prospect who has used AnyDesk will judge
  the product on. GDI BitBlt and JPEG tiles, against DeskRT and H.264.
- **TLS and going live.** `docs/GO-LIVE.md` is the runbook; the domain currently serves a holding
  page while 2FA (4.4) is finished.
- **`docs/BUGS.md` #1 and #2.** #1 does not reproduce and was closed on behaviour, not on
  understanding. #2 is fixed and reproduced deliberately but is **left open** until the real tray
  app is seen clearing itself on a machine.
- **Security debt on the old test machine** (§6): firewall off, `LocalAccountTokenFilterPolicy`
  set, and `~/.rs-target-cred.xml` on this laptop to delete.

## Why this works

The handover carries the architecture and the traps, so the new session starts where this one ended
instead of reading the whole codebase. The brief leads with **what is already proven**, which is the
thing most likely to be wasted — the wire is sound, and a session that starts by "checking" it
spends a morning re-proving `treecheck`. It names the three gaps with the measurement behind each,
so nobody debugs the transfer engine to fix a tab that never asked a question. And the prompt forces
the three decisions to the surface before code goes down the wrong path, which is the pattern that
has worked every time here.
