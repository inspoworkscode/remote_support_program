# Auto-update — every app on every machine keeps itself current

Asked 2026-10-16, the day after code signing went live. Clint's words:

> *"One of the most important things now that the digital key is in place is auto updates. This needs to
> be in place for all features, health check, technician console, client app etc."*

This is roadmap **4.6, the binary half**. The settings half (branding reaching installed machines) was built
2026-10-07, HANDOVER §113. The binary half was held back on purpose until code signing worked, because an
unsigned updater pulling programs onto every customer machine is the supply-chain attack we would warn
customers about. Signing works since 2026-10-15 (`docs/CODE-SIGNING.md`, "SIGNING PROVEN"), so the hold is
over.

**Why it matters:** today a machine runs whatever was installed on it, for ever. Every fix in this project
marked "client reinstall" — and there are dozens — reaches a customer only when somebody reinstalls by hand.
With a few hundred machines per company that is not a support model.

## The decisions

Clint, 2026-10-16: *"Go with your recommendations (silent on customer machines, rollout controlled by us
only)."* Do not re-ask these two.

1. **Customer machines update silently.** No prompt, no "update available" for the person at the machine;
   they did not choose this software and cannot judge an update. The machine's history (console, portal)
   records every update; the person sees nothing except, at most, the tray icon going away and back.
2. **The rollout is ours alone.** Platform admins decide what version goes out and when. A company (reseller)
   cannot hold its machines back, because then a security fix would never reach them. Companies see their
   machines' versions; they do not choose them.

My recommendations on everything else are below, marked **(recommended)**. They are the plan unless Clint says
otherwise; *Still to answer* at the end lists the ones worth a second look.

## What gets updated, and by what

| What | Where it lives | Updated by |
|---|---|---|
| Host (`RemoteSupport.Host.exe`) | `Program Files\<Company>\Remote Support` | the agent service |
| Agent service itself (`RemoteSupport.Agent.exe`) | same | the agent, through a separate updater process (below) |
| Tray app (`RemoteSupport.Client.exe`) | same | the agent; the tray app restarts into it when idle |
| `Install-Client.ps1`, `Uninstall-Client.ps1` | same (kept since §106) | the agent |
| Health check toolkit | `…\Remote Support\healthcheck`, installed from there | the agent replaces the folder; **`HealthCheckKeeper` already spots an installed toolkit older than the scripts in the install folder and reinstalls it, keeping the mail settings** (§50, §51), so the health check follows with no new code |
| Free-version machines | agent only, no tray app (§103) | the same agent path |
| Technician console | `%LOCALAPPDATA%\Programs\RemoteSupport\Console\{slug}` (`ConsoleDownload`, §74) | itself, at start — per user, no administrator |
| The platform (server) | xneelo | **not part of this.** `Publish-Platform.ps1` as now |

**Never touched by an update:** anything that is not a shipped file. That covers `branding\` (the installed
`brand.json` is never rewritten; §113), `branding\live\`, `config.json`, `device.json`, the machine identity
store (E2E keys, §112), the health check's kept mail settings and `windows.json`. An update replaces **only the
files named in the release's manifest** and never deletes anything else.

## The one rule that makes it safe: only our signed files install

The server is the obvious thing to attack: whoever controls it could tell every machine "here is an update".
So **the server is not trusted to say what is safe; the signature is.** The agent installs a file only if:

1. **its Authenticode signature is valid** (`WinVerifyTrust`: chain to a trusted root, timestamped, not
   revoked), **and**
2. **the signer is The Remote Support Program (Pty) Ltd.** It checks the subject's organisation, *not* one
   certificate's thumbprint, because the second certificate (free from Sectigo, from November 2027) must keep
   working. And **not** "signed by anyone": any company can buy a certificate. **and**
3. **it is newer than what is installed**, judged by the version inside the signed file. A compromised server
   could otherwise push an OLD, genuinely signed build that has a known fault. The only way back to an older
   version is the machine's own rollback (below), never the server.

Every file is checked, **including each `.ps1`** (signed since 2026-10-15; `Publish-Platform.ps1` already
refuses to upload an unsigned one). The hash the hub sends is for spotting a broken download; it does not
protect against a compromised server, and the code must not treat it as if it did.

**There is no test mode in the verifier.** A switch that relaxes the check is a backdoor. Harnesses prove the
positive case with real signed files from `dist\` (signed by the YubiKey at build time), and the negative
cases with unsigned files, files signed by a self-made "Someone Else" certificate, a real signed file with one
byte changed, and a real signed file that is older.

## How a release reaches a machine

1. **A release is a set of files and a version.** `build.ps1` stamps a real version (below), and the server's
   payload (`dist\platform-linux\payloads\client`) becomes the release when `Publish-Platform.ps1` deploys it.
   A manifest lists each file, its size, its SHA-256 and its version.
2. **The hub offers it** with `UpdateOfferMsg` (version, manifest), **only to an agent that says
   `DeviceCan.Update`**, as with every capability since §76. Older agents are never offered one.
3. **Only changed files are downloaded.** The agent compares the manifest with what it has and fetches only
   files whose hash differs, over HTTPS from its own server. Sizes today (2026-10-15 build): Agent 38 MB,
   Host 52 MB, and the tray app **132 MB**, because the installed tray app is the downloadable .exe itself,
   with the host, agent and scripts packed inside it (`ClientPayload`, §71). The packed copy is never needed
   on an installed machine, so **stage D should install a plain tray app** (without the payload) and keep the
   packed one for downloads only. That brings a full release to roughly 130 MB per machine instead of 220 MB.
   Self-contained .NET apps change in every build, so expect every release to touch all three.
4. **It stages and checks everything first**, into `…\Remote Support\update\<version>\`, applying the three
   rules to every file. One failure means nothing is installed, and the hub is told why.
5. **It waits until the machine is idle (recommended):** no session open for 10 minutes and no health check
   running. A machine that is never idle (rare) is updated in the first quiet moment overnight.
6. **It swaps the files.** Windows lets you *rename* a running .exe but not overwrite it, so each old file is
   renamed aside (`*.old`) and the new one put in its place. Nothing has to stop first except the agent,
   which must restart to run its new self.
7. **A separate updater does the agent's own restart.** A copy of the new agent, started from the staging
   folder with `--apply-update`, outlives the service stop. It stops the service, completes the swap, starts
   the service and keeps watch.
8. **The tray app restarts into the new version** the way branding already does: when idle (visible and not
   minimised means in use, §113), with `--tray --after <pid>`.
9. **It rolls itself back if needed.** The new agent must connect to the hub within **5 minutes**
   (recommended). If it does not, the watching updater puts every `*.old` file back, restarts the service and
   reports *"update to X rolled back"* once the old agent is connected. **A bad update must never leave a
   machine we cannot reach**: that machine would need a visit, which is what this whole stage exists to end.
10. **It reports the result.** The hub writes each step to the machine's history (offered, downloaded,
    installed, rolled back and why), and the new version arrives with the next hello as always.

## Rolling out in stages (ours alone)

- **Three rings (recommended):** **Anywhere IT's own machines**, then **the pilot companies**
  (`Tenant.Pilot`, §146), then **everyone**.
- A **Full admin or Owner** moves a release to the next ring on `/owner` (`PlatformAdmins` rules, logged by
  name). **There is a Pause button** that stops new offers at once; machines already part-way finish or roll
  back on their own.
- **Pause is automatic** (recommended) when more than **2%** of the machines offered a release in a ring
  roll back, and the Full admins and Owners are emailed, as `ControlSweep` already does for capacity.
- **Offers are spread out:** the hub offers a release to at most N machines a minute per server, and each
  agent adds a random wait. Five hundred machines asking for 130 MB in the same minute would flatten the
  server, and its link is the cell's limit (§126).
- **Several servers:** each server offers what it holds. The control dashboard (§133) shows per server how
  many machines are on each version and lets a release be moved forward on each, through `ControlServers` as
  with every other change.

## The technician console

- **At start (recommended):** the console compares its version with the server's (`StateMsg` carries it).
  If the server's is newer, it downloads `technician.exe`, applies the same three rules, runs its own set-up
  over the installed copy (which `ConsoleDownload` already does when a newer download is run) and relaunches,
  the way Sign out already does (§69). The technician sees *"Updating…"* for a few seconds.
- **If it has been left running:** a quiet *"A new version is ready — restart"* in the header when no session
  is open. Never in the middle of a session.
- The console follows the same rings as customer machines, for the same reason.

## Version numbers — the first thing to build

**Every app reports `1.0.0` today** (`Directory.Build.props`), so nothing can tell one build from another.
`HelloMsg.Version` is already sent (from `DeviceLink`'s assembly) but never changes. `build.ps1` must stamp
each build with a version that always goes up: **`2026.10.16.N`** (recommended: the date, then the build
number that day). The console's machine list, the portal's Devices page and the control dashboard show each
machine's version.

## The last manual reinstall

Machines installed before the updater exists cannot update themselves into one. **Every machine in the field
needs one more reinstall** to reach a build that has the updater. After that, no machine needs another one.

- The console and the portal list machines **below the first updater version**, so nobody has to guess which
  ones still need it.
- **Unattended machines can be done remotely** with the Scripts tool (it runs as SYSTEM): run the signed
  installer silently. The one-command way to do that belongs in the stage F work.
- The sooner this ships, the fewer machines need that last visit: every reinstall done before then is one
  more done twice.

## Stages

- **A. Real version numbers**, stamped by `build.ps1`, shown everywhere. Small, and it can ship before the
  rest. **BUILT 2026-10-16 (HANDOVER §147), not on hardware**: `Shared/AppVersion.cs`, `tools\BuildVersion.ps1`,
  `Device.AgentVersion`/`ClientVersion` (migration `AppVersions`), shown in the console, the portal and `/owner`;
  `versioncheck` 27 on both databases. The version is the build's moment in UTC, `2026.10.16.1432` (not a counter). **It must be in the build that goes out for the last reinstall**, or the field cannot be told apart.
- **B. The verifier** — `Shared/UpdateTrust.cs`: the three rules, `WinVerifyTrust` called by hand (no
  package), and its harness with the negative cases above. Nothing else is built on it until it is proved.
- **C. The server side:** the release and its manifest, `DeviceCan.Update`, `UpdateOfferMsg`, rings, pause,
  spreading the offers, per-machine history, and the `/owner` controls and dashboard counts.
- **D. The agent:** download, stage, check, wait for idle, swap, `--apply-update`, rollback, report. The
  tray app's restart, and the health check following through `HealthCheckKeeper`.
- **E. The console's own update.**
- **F. The last reinstall:** the "below updater version" list, and the remote way to do it on unattended
  machines.

**B to F ARE BUILT, 2026-10-16 (HANDOVER §148), none of it on hardware** — Clint: *"lets finish all the stages before i
deploy."* Three things came out differently from the plan above, each for a reason in §148: **the release is ONE file**,
the packed customer app (smaller than the three apart, and what is signed); **the last reinstall is not a reinstall** but
*Scripts → Update this machine*, which keeps the machine's identity (a reinstall would make the office confirm every
machine again); and **the console rolls itself back** if a new copy does not reach its sign-in window. Harnesses:
`updatetrust` 8, `updatewire` 33 (both databases), `updateapply` 36, `consoleupdate` 14, `updatebyhand` 9, `versioncheck`
28. The hardware test is `TEST-3.6.md` section DE, 728–739 — **737, the rollback, is the one that matters.**

**Proving it** needs real releases, so the hardware test is: build version N, install it on DESKTOP-OPHQIDH
and DESKTOP-2N6FVGT, build N+1, deploy, and watch both update with nobody touching them. Then build a
**deliberately broken N+2** (an agent that never connects) and watch both roll back to N+1 by themselves.
**Do not mark this stage done until that rollback has been seen on a real machine.** Each stage adds its
checks to `TEST-3.6.md` as it is built.

## Traps known in advance

- **`sc failure` restarts the agent** (`Install-Client.ps1`), so the updater's stop must be a real
  `sc stop`, and the swap must finish before Windows' own restart-on-failure fights it.
- **The install folder is per company** (`Program Files\<Company>\Remote Support`), but the files are the
  same for everyone (one signed .exe, `CODE-SIGNING.md`). The agent updates where it is running, so it never
  works the folder out.
- **ESET and other antivirus hold a new file** while they ask their cloud (BUGS 9, §53). Staging must retry
  the way `HealthCheckKeeper` does (1, 5, 15 minutes) rather than fail on the first try.
- **E2E (§112):** new and old builds must still seal to each other. Rings mean a console and a host of
  different builds meet as a matter of routine now — `SubscribeMsg.Codecs` was written for exactly this day.
- **Never `Get-AuthenticodeSignature` on a copy of a Windows file** (`CODE-SIGNING.md`): it answers
  "Microsoft" from the catalog. The verifier checks the file's own embedded signature.

## Still to answer (each has a recommendation above; it is the plan unless Clint changes it)

1. **When:** whenever the machine is idle (recommended), or only at night? Night alone misses every machine
   that is switched off at night, which is most office PCs.
2. **Rings:** moved forward by hand (recommended), or automatically after a day each?
3. **Auto-pause** at 2% rollbacks per ring — right figure?
4. **Too-old apps:** once updating works, should the hub refuse a console or agent older than some version?
   Recommended: not yet; only when a security fix needs it, and then per release.
5. **Where downloads come from:** our own server first (recommended). If one release × every machine turns
   out too much for a server's link, the files can be served from anywhere — Cloudflare already carries the
   domain's DNS — because the signature, not the server, is what is trusted.
