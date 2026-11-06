# 13 — What is new in V2.3

**V2.2** of this pack was written on **15 October 2026**. **V2.3** was written on **16 October 2026**. One thing
was added in that day, and it is a big one: **every program now keeps itself up to date.** Documents 00–09
have been updated to match; documents 10–12 still list what V2, V2.1 and V2.2 added.

Everything below was **built and passed its automatic test tools**, but has **not yet been tried on real
customer computers**. The hardware checklist is `docs/TEST-3.6.md`, sections **DD** and **DE** (checks
723–739), and `docs/HANDOVER.md` **§147–§148** is the full engineering record. The plan it was built from,
with Clint's decisions, is `docs/NEXT-SESSION-AUTOUPDATE.md`.

---

## Why it matters

Before this, a customer's computer ran whatever version was installed on it, for ever. Every fix to the
programs on customers' computers reached them only when somebody reinstalled by hand — which, with a few
hundred machines per IT company, is not a way to run a business. Updating was held back until code signing
worked (15 October 2026), because a program that installs other programs on customers' computers must be
able to prove they are ours.

**Clint's two decisions:** customer computers update **silently** (nobody at the machine is asked), and
**the rollout is ours alone** — an IT company cannot hold its machines back, or a security fix would never
reach them.

---

## What was added

| What | In short | Record |
|---|---|---|
| **Real version numbers** | Every program used to say 1.0.0. Now each build is stamped with the moment it was made, in UTC: **2026.10.16.1432** is 14:32 on 16 October 2026. Shown in the console's machine list and title, on the portal's Devices page (amber when older than what the server hands out), and on the control dashboard. Every computer installed before 16 October shows **1.0 (old)** | §147, DD |
| **Only our signed, newer files are installed** | Each computer checks every file itself with Windows' own signature check: signed by **The Remote Support Program (Pty) Ltd** (South Africa), not revoked, and **newer** than what it has — so even somebody who took over our server could not push an old or foreign program. There is no "test mode" that relaxes this | §148 B |
| **Releases go out in rings, and can be paused** | A new release goes first to **our own company's** computers, then — when a Full admin or Owner says so on **Owner → Updates** — to **the pilots**, then **everybody**. A few machines a minute, so the server is never flooded. **If a computer has to roll back, the release pauses itself** and every Full admin and Owner is emailed | §148 C |
| **Computers update themselves** | The background service downloads the release from the IT company's own server, checks every file, waits until **nobody has been connected for 10 minutes**, and installs it. If the new version has not connected back within **5 minutes**, the old one is put back by itself, and that version is never tried on that computer again. The tray app restarts into the new version when nobody is using it; the monthly health check follows by itself | §148 D |
| **The console updates itself** | Before its sign-in window, an installed console fetches a newer version and restarts in it (*"Updating the console…"* for a few seconds). While open, an **Update ready** button appears in its header. If a new console cannot reach its sign-in window, the old one is put back | §148 E |
| **The one update by hand** | Computers installed before 16 October cannot update themselves yet. **Once each**: connect from the console, then **Tools → Scripts → Update this machine**, and Run. It keeps the computer's identity and settings (a reinstall would make the office confirm it again). The portal's Devices page counts them and marks each one *update by hand once* | §148 F |

---

## New test tools

`versioncheck` (version numbers), `updatetrust` (the signature check, with real files — including a
certificate made with our name, which is refused), `updatewire` (rings, Pause, the automatic pause, never
offering a refused version again — on both databases), `updateapply` (the file swap and the rollback on a
computer), `consoleupdate` (two real consoles, one updating the other), `updatebyhand` (the script, run
for real against files it must refuse). On 16 October **all 82 database test tools passed**, and the
console's window checks (`dashtabs`, `toolbarprobe`, `fitprobe`, `lookprobe`).

---

## Putting V2.3 live

1. **A signed deploy:** `deploy\Publish-Platform.ps1` with the YubiKey plugged in. Two database changes
   (`AppVersions`, `AutoUpdate`) apply by themselves. **Owner → Updates** then shows the release offered to
   our own company only.
2. **Every technician re-downloads the console once.** The old consoles cannot update themselves.
3. **Every customer computer gets its one update by hand** (Tools → Scripts → Update this machine).
4. **From the next deploy on**, our own computers update themselves within minutes; watch them come
   through it on Owner → Updates, **then** offer it to the pilots, then everybody.

The check that matters most is **737 in `TEST-3.6.md`: a deliberate rollback on a real computer**. Until it
has been seen working, treat automatic updating as unproven.

## Known gaps

- **A restart of the computer during the five minutes after an install** leaves nobody to roll back; if
  the new version cannot connect, someone has to reinstall it.
- The console's own rollback has no automatic test.
- **Owner → Updates** shows the history of the machines on the server you are looking at only.
