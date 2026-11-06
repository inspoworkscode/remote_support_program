# 05 — Building and releasing

For developers. How to get from the code to running programs, and how a change reaches the live
server and customers' computers.

---

## What you need on the build PC

| Tool | Why |
|---|---|
| **Windows 10 or 11** | The customer and technician programs are Windows programs and use Windows-only features |
| **.NET 10 SDK** (`C:\Program Files\dotnet\dotnet.exe`; `winget install --id Microsoft.DotNet.SDK.10 --exact`) | Builds everything. The scripts expect it in that folder |
| **Windows PowerShell 5.1** (built into Windows) | Every build script. Run them as `powershell -ExecutionPolicy Bypass -File <script>` — the plain form is blocked by Windows' script policy |
| **Windows OpenSSH Client** (Settings → Optional features) | Uploading to the live server |
| **The SSH key** for the live server | See document 08 |
| **Windows SDK** (for `signtool.exe`), **Yubico's Smart Card Minidriver** (x64) and the **YubiKey** code-signing token | Only for signed builds |
| Inno Setup 6 (optional) | Only for the old-style setup .exe files (`build.ps1 -Installers`). The normal download no longer uses them |

No other paid tools are needed. Two outside libraries are brought in, both MIT licence and free:
**QRCoder**, for drawing the two-factor QR code, and **MailKit**, for reading and sending email for
tickets by email (since October 2026). Everything else is .NET itself.

## First time: get the code ready

1. Copy the `Source code` folder to the PC, or restore it with its history (see
   `Source code with history\HOW-TO-RESTORE.txt`).
2. Create `branding\brand.json` from `branding\brand.example.json`. For testing against your own
   local platform, set `serverUrl` to `ws://<this PC's address>:8090` and leave `tenantKey` empty
   until you have created a test company (the portal's Downloads page shows the real values).
3. Open `RemoteSupport.sln` in Visual Studio 2022 or use the command line.

## Run the platform locally (for testing)

```bash
dotnet run -c Release --project src/RemoteSupport.Platform
```

It listens on port 8090 and creates its database and keys in its `data` folder on first run.
Use its command line to create a company and a first owner:

```bash
src\RemoteSupport.Platform\bin\Release\net10.0\RemoteSupport.Platform.exe create-tenant "Test IT" testit 7
src\RemoteSupport.Platform\bin\Release\net10.0\RemoteSupport.Platform.exe add-tech testit you@example.com "Password123!" "Your Name" --owner --admin
```

(Run the exe with no arguments to see every command and its exact syntax.)

`tools/workflowdemo` creates a complete demonstration company with clients, staff and tickets on a
throwaway platform, without touching anything real — the quickest way to see the portal working.

## The build scripts

| Command | What it does | When to use it |
|---|---|---|
| `build.ps1` | Builds every program into `dist\` | General build |
| `build.ps1 -Platform -PublicUrl https://theremotesupportprogram.com` | Also builds the Linux server package with the customer and technician downloads inside it | Called for you by `Publish-Platform.ps1` |
| `tools\ship-build.ps1` | A **full ship** to the office test platform: rebuilds the host, agent, tray app and console, refreshes the download, refreshes `demo\technician` | Any change to the customer side (`Host`, `Agent`, `Client`, `Shared`) — then reinstall the client on the test machine |
| `tools\ship-build.ps1 -OnlyTechnician` | Just the console, about 90 seconds, **to the office test platform only** | A change that touched only `RemoteSupport.Technician` (or the portal). **Live technicians get a new console from `Publish-Platform.ps1`** (the console rides inside the server package) and then re-download it — `HANDOVER.md` §110 |
| `deploy\Publish-Platform.ps1 -Server 154.65.110.136` | **Puts the live server up to date.** Builds, uploads, backs up the data, installs, restarts, checks the site answers. The public front stays the holding page | Every live release |
| `deploy\Publish-Platform.ps1 -Server 154.65.110.136 -LandingOnly` | Uploads **only the holding page** (`landing\index.html`) — nothing built, shipped or restarted | A change to the holding page or its legal text |
| `deploy\Publish-Platform.ps1 -Server 154.65.110.136 -RealSite` | As the first, but shows the platform's own website to the public, **opens signups**, and puts our chat bubble on the site (`-SiteChat <company>` chooses whose chat answers it; Anywhere IT by default) | **From 1 November 2026, EVERY live release** — without it the holding page comes back and signups close (§169). Never before 1 November |

Run them like this:

```bash
powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server 154.65.110.136
```

**Important habits, each learned the hard way:**

- `demo\technician` and `demo\client` are ready-to-run copies **that go stale**. Refresh them (the
  ship script does) before testing, or you will test yesterday's program.
- If you are not sure whether a change touched the customer side, do a **full** ship. A stale
  host program looks exactly like a broken feature.
- A **platform restart signs every technician console out** (sign-in tokens live in memory). Warn
  people before updating the live server in working hours.
- **The terms** are in `src/RemoteSupport.Platform/Terms/` (one shared file, one per region, and the
  free version's section). After **any** change to them, move `Terms.Version` on and regenerate the
  holding page's legal text with `RemoteSupport.Platform landing-legal <full path to landing\index.html>`
  — `tools/legalcheck` fails until you do. What a company accepted is kept word for word, so changing
  the files never changes what anybody agreed to.
- **Never use `Publish-Landing.ps1 -Server` on the live server** — it rewrites the web server's whole
  configuration and would cut the platform off. Use `Publish-Platform.ps1 -LandingOnly`.
- **Database changes** are made with Entity Framework migrations, applied automatically when the
  platform starts. **The live server is on PostgreSQL (since 9 October 2026) while local tests default
  to SQLite, so every model change needs TWO migrations, with the same name:**
  `dotnet-ef migrations add <Name> --project src\RemoteSupport.Platform --context PlatformDb -o Data/Migrations`
  and
  `dotnet-ef migrations add <Name> --project src\RemoteSupport.Platform --context PgPlatformDb --output-dir Data/PgMigrations`.
  `tools/pgcheck` fails until both exist. **Never re-make `PgInitial`** — it is frozen. Check a new SQLite
  migration's Designer file says `typeof(PlatformDb)`. Never switch to `EnsureCreated` — it silently
  ignores new columns.
- **Testing on PostgreSQL:** set `RSP_TEST_DB=postgres` (with the git-ignored `tools\rs-postgres.txt`
  naming a local PostgreSQL) and a harness runs on it; `tools\Run-DbHarnesses.ps1 -Postgres` runs them all.
  A new test tool that makes a database uses `UseHarnessDb`, `HarnessDb.MigrateAsync` and `HarnessDb.Use`.
- **Keys are never printed.** `go-live.sh` says where each key is, never the key itself. If a key is
  ever exposed (a log pasted somewhere), `deploy\Rotate-Keys.ps1 -Server <ip>` replaces the owner and
  server keys and puts the old ones back if the platform does not answer.
- **PayFast's account details** go onto the server with `deploy\Set-PayfastKeys.ps1` — never in chat,
  never as a command-line argument. Sandbox and live are separate accounts, changed together.
- **Any new public web page** must be added to `go-live.sh`'s holding list, or it is public the moment it
  deploys; `tools/sitecheck` fails otherwise.

## How a release reaches people

| What changed | What has to happen |
|---|---|
| The website or portal only | `Publish-Platform.ps1`. Done. |
| The technician console | `Publish-Platform.ps1`. An installed console (V2.3 or later) updates itself at its next start, or from **Update ready** in its header; an older one is downloaded again from `/tech/<company>` once |
| The customer side (tray app, agent, host, health check, the installer scripts) | `Publish-Platform.ps1`, signed. Computers that update themselves (V2.3 or later) take it by themselves when the release reaches their ring — see below. A computer installed **before 16 October 2026** needs its **one update by hand** first: connect, Tools → Scripts → *Update this machine* |
| A company's branding | Nothing to release: saving Portal → Branding reaches installed machines by itself (since 7 October) |

### Version numbers and rolling a release out (V2.3)

Every build is stamped with the moment it was made, in UTC — **2026.10.16.1432** is 14:32 on 16 October 2026
(`tools\BuildVersion.ps1`, used by `build.ps1`, `ship-build.ps1` and `Pack-ClientExe.ps1`). It only goes up,
so the newest build is always the newest version. `build.ps1 -Version 2026.10.16.1500` stamps a chosen one,
for testing. **Never pass `-p:Version` alone to a build**: `Directory.Build.props` pins the other version
numbers too, so use all of `VersionArgs` (`HANDOVER.md` §147). A program built any other way says 1.0.0.

After a signed deploy, **Owner → Updates** shows the new release offered to **our own company only**. Our
computers take it within minutes of being idle. **When they have come through it** (each shows *Updated*),
press **Offer it to the pilots**, then **Offer it to everybody**. **Pause** stops it at once; it also pauses
by itself if a computer rolls back, and emails every Full admin and Owner — read what that computer said
before resuming. Each server's release is moved on separately, from the same page.

## Code signing

The certificate is on the YubiKey (issued 14 October 2026, valid to 18 November 2027). The build
signs automatically when the YubiKey is plugged in, using `tools\Signing.ps1`, and says plainly
when it is producing unsigned files. **A PC that signs needs the Windows SDK and Yubico's Smart
Card Minidriver** (x64) — without the driver Windows cannot use the key, which is ECC. Setting up a
new signing PC, or loading the term's second certificate: `tools\Install-SigningCert.ps1`. Rules:

- Use `build.ps1 -RequireSigning` for anything going to customers, so it fails rather than shipping
  unsigned. **`deploy\Publish-Platform.ps1` does this itself since 15 October 2026**: it builds with
  `-RequireSigning`, then checks that every program and script the server hands out is signed by us,
  and uploads nothing otherwise. `-Unsigned` is the deliberate way past it.
- **Never sign test builds.** Sign only what is about to go to real customers.
- The one downloadable .exe is signed **inside first** (the programs packed into it), **outside
  last** — `tools\Pack-ClientExe.ps1` does this.
- The certificate is in the name **The Remote Support Program (Pty) Ltd**, never an IT company's
  name, because every reseller ships the same files.

`docs/CODE-SIGNING.md` and `docs/CODE-SIGNING-ORDER.md` have the full story. **The first signed deploy
went live on 15 October 2026**: all 12 programs and scripts the server hands out, signed and timestamped.

## The pilot guide

`docs/pilot-brochure/brochure.html` is the guide for pilot companies: setting up, what is new, and the
full tour, with screenshots of made-up demonstration companies (never a real customer). It is printed
to `The Remote Support Program - Pilot Guide.pdf` in the same folder with Edge's headless print-to-PDF.
The portal screenshots come from a throwaway platform made by `tools/workflowdemo`; the console and
customer-app windows are the real windows fed made-up data, the way `tools/siteshots` and
`tools/lookprobe` do it.

## Testing

Two kinds of tests exist, and both matter:

1. **The test tools in `tools\`** — about 150 small programs, each proving one feature. Most
   start their own throwaway platform, so they never touch real data. Each one prints how many of
   its checks passed (e.g. `securitycheck 75 of 75`). `HANDOVER.md` §7 explains them, and every
   section of the handover names the tools that prove that feature and the count they should give.
   Run them with, for example:
   `dotnet run -c Release --project tools/securitycheck`
2. **Hardware testing by people.** `docs/TEST-3.6.md` is the numbered checklist (722 checks).
   Staff work through it with the **Test Checklist** program (`tools/testchecklist`), published to
   the office shared drive, and export a daily report. **Never renumber a check** — the number is
   how results are matched up.

**The working rule since day one: measure before changing code.** When something is reported
broken, first prove where it breaks (the logs and test tools are there for that), then fix it, then
show the test that would have caught it fails without the fix.

## Logs worth knowing

| Log | Where | What it tells you |
|---|---|---|
| Console trace | Technician PC: `%LOCALAPPDATA%\TheRemoteSupportProgram\technician-trace.log` | Connections, messages lost, input that was blocked and why |
| Host trace | Customer PC: `C:\ProgramData\RemoteSupport\host-trace.log` | What account the host ran as, which screen it could see, desktop switches |
| Health check | Customer PC: `C:\ProgramData\RemoteSupport\healthcheck-agent.log` | Installing and removing the health check |
| Server | Live server: `sudo journalctl -u remotesupport-platform` | Everything on the server side, including security warnings at start-up |
| Agent state | Run `tools\agentstate.ps1` on a customer PC | Why unattended access is or is not working on that machine |
