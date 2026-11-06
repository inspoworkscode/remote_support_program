# Implementation plan — folding the health check into the platform

Written 2026-09-11, **all stages complete 2026-09-13**. Kept as the record of what was built and
how each piece was proven; §17 of `docs/HANDOVER.md` describes the result.

| Stage | State |
|---|---|
| 0. Installer in the customer download | done 2026-09-11, installed from the real zip on a real machine |
| 1. Tenant setup and migration | done 2026-09-11 |
| 2. Package the toolkit into the install | done 2026-09-11, task registered on DESKTOP-OPHQIDH for 5 Oct 08:14 |
| 3. Upload, and mail from the server | done 2026-09-13, **a real email sent and received** |
| 4. Surface it in the portal | done 2026-09-13, `/portal/healthchecks` |

Not built, from stage 4: the last-run indicator on the device rows and the client page.

Never yet run: a **scheduled** monthly run firing unattended as SYSTEM. Everything it does has been
exercised by hand. First real one is 5 October 2026.

---

Written 2026-09-11, after reading `docs/HANDOVER.md`, `docs/NEXT-HEALTHCHECK.md`,
`docs/BUGS.md` and the toolkit's own `CLAUDE.md` / `HANDOVER.md`.

**Delivery decision, taken by Clint 2026-09-11: option C.** The endpoint runs the check, does
the maintenance and builds the PDF with Edge exactly as it does today; it then **uploads** the
JSON and the PDF; the **server** stores them, shows them in the portal, and sends the mail using
SMTP settings held once per tenant.

Why C, and not either option the brief set out:

- Against "endpoint mails" (the toolkit's current design): it takes the SMTP password off every
  customer machine, puts reports in the portal, and makes rotating the mail password one portal
  edit rather than a reinstall everywhere.
- Against "server builds the report": the PDF step is headless Edge with roughly 60 lines of
  hard-won workarounds — SYSTEM's TEMP, the sandbox write failure, stage-then-move. The platform
  publishes `linux-x64` and has no Edge. C leaves `New-ClientReport.ps1` untouched and needs no
  PDF engine on the server.

---

## What was measured first, not assumed

| Claim | How it was checked | Result |
|---|---|---|
| The customer download has no installer (§1a) | Listed the live `bin/Release/net8.0/payloads/client` | Three exes and `branding/` only. Confirmed. |
| `/get/{slug}` offers only the portable route | Read `Pages/Get.cshtml` | One button; the steps say "unzip, run RemoteSupport.Client.exe". Confirmed. |
| Shipping the scripts needs a `PayloadService` change | Read `BuildAsync` | **No.** It zips the directory recursively and only swaps `branding/`. Staging in `build.ps1` is enough. |
| The platform can already send mail | `grep` for `SmtpClient`, `MailMessage`, `Smtp` over `RemoteSupport.Platform` | **Nothing.** Server-side mail is net-new. |
| The report needs Edge | Read `New-ClientReport.ps1` | Yes — `msedge.exe --headless --print-to-pdf`. Not present on a Linux host. |
| A device can authenticate an upload without new protocol work | Read `Device` in `PlatformDb.cs` | `DeviceId` and `DeviceKey` already exist. |

---

## Stage 0 — the installer download (prerequisite, blocks everything else)

**Done 2026-09-11.** What was built, and what it turned up, is at the end of this section.

There is nothing to ship the health check inside until this exists.

1. `build.ps1`: stage `deploy-client/`'s scripts into `dist\client`, so they reach
   `payloads\client` and therefore the branded zip. The binaries are already staged there; only
   the `.ps1`, the `.bat` wrappers and the read-me are missing.
2. `Pages/Get.cshtml`: two clearly labelled routes instead of one silent one — **Install
   properly** (recommended: unattended agent plus the monthly health check) and **One-off
   support** (portable, nothing installed). The same zip sits behind both; the difference is
   which file the customer is told to run.
3. Keep the §5 wrapper convention: each `.bat` re-launches *itself* elevated and forwards
   nothing, because arguments through cmd → PowerShell → cmd break on a path with a space, and
   the real path has one.

**The `-Exclude` trap, now measured and not a problem.** `Install-Client.ps1` copies with
`Copy-Item -Recurse -Force -Exclude <leaf names>`, and the worry was that excluding the coming
`healthcheck\` **folder** by name would not work. It does: a test copy with a `healthcheck\`
directory and two files inside it excluded the whole directory cleanly, and the real installer run
from the extracted zip put only the three exes and `branding\` into the install directory. No
explicit file list is needed. Recorded because the opposite belief would have cost an hour.

Not on the table: a signed installer. Code signing is still deferred (§10). An unsigned zip
containing scripts is the honest interim, and `PayloadService`'s own summary already anticipated
it.

### What stage 0 actually changed

- `build.ps1` stages six files from `deploy-client\` into `dist\client`, named explicitly rather
  than copied with `*`, because that folder also holds its own stale copies of the three exes and
  a branding folder which would overwrite the freshly published ones. It throws if one is missing,
  so a download can never silently ship without an installer again.
- `Get.cshtml` offers two routes: **Set up ongoing support** (`Install-Client-Unattended.bat`) and
  **Just this once** (`RemoteSupport.Client.exe`). One zip, two things to double-click — presented
  that way rather than as two buttons pointing at the same file.
- `PayloadService.ClientHasInstaller` checks the payload actually contains
  `Install-Client-Unattended.bat`, and the page falls back to the old portable-only wording when it
  does not. A payload staged by an older `build.ps1` therefore cannot make the page promise a file
  that is not in the customer's zip. Verified both ways.
- **The installer now brands itself from `brand.json`** instead of hardcoding "Anywhere IT". This
  was not in the original plan and became necessary the moment these scripts started shipping from
  `/get/<slug>`: a reseller's customer would otherwise find another IT company's name in
  Program Files, on their desktop shortcut and in their Services list. Install directory, shortcut
  name and description, and the service display name and description all come from the tenant now.
  The company name is sanitised for path use, keeping apostrophes, because a real client is called
  O'Brien Plumbing.
- **Internal identifiers deliberately stay fixed** on every tenant: the service name
  `RemoteSupportAgent`, the `HKLM\...\Run` value `TheRemoteSupportProgram`, and the transient
  scheduled task. Branding those per tenant would strand the service and leave a second Run entry
  on any machine that upgrades, starting the tray app twice at logon. Nobody sees these strings.
- `Uninstall-Client.ps1` works the same names out from the branding next to it, falls back to the
  old fixed path when there is no branding or the folder is not there, and removes both the branded
  shortcut and the legacy `Anywhere IT Support.lnk`.

### How it was proven

Not by reading it. A copy of the source tree was built in a scratch folder (the live server holds
`RemoteSupport.Shared.dll`, and both `-o` and `BaseOutputPath` still collide with it — a source
copy is the way to build without asking Clint to stop anything), run on port 8099 against a **copy**
of the data directory, and the branded zip downloaded from it. The zip contains the six installer
files. The real `Install-Client.ps1` was then run **from the extracted zip**, with only the
administrator check and the final tray-app launch removed, into a scratch install directory:

- only the three exes and `branding\` landed — the installer scripts were correctly excluded;
- the shortcut it tried to create was `Anywhere IT Remote Support.lnk`, so the branding is live;
- pointed at the old payload directory instead, the page correctly hid the install route.

### What it turned up

`Platform:PublicUrl` is `https://theremotesupportprogram.com`, and every downloaded client is
baked with `wss://theremotesupportprogram.com` — which is not serving. **The download has never
produced a working client**, which nobody had noticed because nothing from that page had ever been
installed. Filed as bug 4 in `docs/BUGS.md`; the fix is configuration and is Clint's call.

## Stage 1 — tenant setup in the portal

New `Tenant` fields plus a migration (EF migrations under `Data/Migrations`, applied with
`Database.MigrateAsync()`):

| Field | Purpose |
|---|---|
| `HealthCheckEnabled` | Whether it runs at all |
| `HealthCheckEmailTo` | Where reports go — the IT company's address, per the brief |
| `HealthCheckDayOfMonth` | 0 keeps the toolkit's existing behaviour: spread by install weekday |
| `HealthCheckRunMaintenance` | Maintenance plus check, or the read-only check only |
| `SmtpHost`, `SmtpPort`, `SmtpUser`, `SmtpPasswordProtected`, `SmtpFrom`, `SmtpUseSsl` | The tenant's sending mailbox |

The SMTP password is stored with ASP.NET Data Protection, the same mechanism as workshop device
passwords (§10). That makes `data/keys` more load-bearing than it already was — it is gitignored
and must be backed up privately.

**A gate, not a blocker today:** the portal serves plain HTTP on the LAN (§11). Fine for Clint's
own testing. TLS must land before a reselling tenant types a mail password into it. Recorded here
so it is not forgotten.

## Stage 2 — package the toolkit into the client install

- Bring `C:\PCHealthCheck\src\*.ps1` and `templates/` into this repo under `healthcheck/`,
  tracked and version-stamped. Do **not** tidy the toolkit repo to match this one — its scope
  rules were right for it and are deliberately reversed here.
- `build.ps1` stages `healthcheck/` into `dist\client`.
- `Install-Client.ps1` gains `-HealthCheck`, which calls `Install-HealthCheck.ps1` with values
  read from `branding/brand.json` rather than typed by a technician. Organisation comes from the
  tenant; the per-machine "user or role" defaults to the machine name and stays editable.
- `brand.json` carries only what the endpoint needs: enabled, day of month, maintenance on or
  off, and the upload URL. **No SMTP settings and no mail password reach the endpoint at all** —
  that is the entire point of option C.
- `Uninstall-Client.ps1` removes the scheduled task and the install folder, preserving logs the
  way it now preserves `*.log` (bug 3).

## Stage 3 — upload, and mail from the server

**Transport: an authenticated HTTPS POST, not new WebSocket messages.** `BinKind.FileChunk` is
session-scoped and a monthly run has no session; chunking a multi-megabyte PDF down the device
socket would be new protocol surface for no gain. The device already holds `DeviceId` and
`DeviceKey`, so:

- `POST /api/healthcheck/report` — multipart: the collector JSON, the PDF, and a small summary
  (counts by severity, run duration, whether maintenance ran, and whether a restore point was
  made). Authenticated by device id and key; the tenant is resolved from the device row, so a
  device cannot post into another tenant.
- The server stores the files under the data directory, out of the repo, as uploaded logos
  already are, and writes a `HealthCheckRun` row.
- The server sends the mail: the tenant's SMTP settings, the tenant's branding, PDF attached.
  The subject and body carry across from `Invoke-MonthlyRun.ps1`'s existing wording rather than
  being rewritten — that text has already been through its wording fixes.
- The endpoint seam is small and already exists. `Invoke-MonthlyRun.ps1` takes `-ConfigPath` and
  already has a `-SkipEmail` path that reports "results are on this machine only". The upload
  replaces the mail step in the same place.
- **Failure must be non-fatal and retried.** The toolkit's rule is that a failed send loses
  nothing, because the JSON is written to the machine first. Keep it: on upload failure leave the
  files in place and retry on the next task run, oldest first.

## Stage 4 — surface it in the portal

Last run, severity counts and the next due date on the device row and the client page, both of
which already exist and are grouped by customer (§16). Opening a run shows the stored PDF and the
findings. This is where the toolkit gains most from being absorbed: today it has nowhere to show
anything.

---

## Traps carried across, from both projects

From the toolkit's "Traps", which the brief says are not optional:

- Single-item pipelines return a scalar. `@($things | Sort-Object)[0]`, or `"Clint"` indexes to
  `"C"`. This one bit three times.
- Empty values change type too. `[string]$text = (Get-Content $f -Raw)` before calling `.Trim()`.
- Variable names are case-insensitive: `$allMonths` and `$AllMonths` are one variable. Cost six
  versions, because the error pointed at the command line and the bug was 300 lines inside.
- Switches cannot cross a process boundary. Anything passed to `powershell.exe -File` is a
  `[bool]` with a default.
- Escaping belongs at the call site. Keep `%ORG%` raw and `%ORG_Q%` doubled, or a client reads
  `O''Brien Plumbing` on their own report — confidently wrong, and nothing errors.
- `Set-Content` / `Out-File -Encoding UTF8` writes a BOM. Use `[System.IO.File]::WriteAllText`
  with `UTF8Encoding($false)`.
- Headless Edge cannot write to a locked-down folder and exits 0 having produced nothing.
- Scheduled task `StartBoundary` must be a future date, or the task never fires while looking
  perfectly healthy.
- A Critical that fires on every machine is a wrong check, not a sick estate.

From this repo:

- Scripts run as **SYSTEM**, which has no user profile — watch `$env:APPDATA` and Downloads paths
  (§4).
- Never start a user-facing app from an elevated installer with a plain `Start-Process` (§4).
- `$PSScriptRoot`, not `$MyInvocation.MyCommand.Path`, inside `param()` defaults; parenthesise
  each element of an array of joined paths (§10).
- Keep `.ps1` and console output plain ASCII — 5.1 reads a `.ps1` as ANSI without a BOM.
- Refresh `demo/` from `dist/` before testing, or yesterday's binary gets tested. Twice burned.

## How each stage gets proven

Per §12, and the toolkit's "test the package, not the source tree":

- **Stage 0:** download the branded zip from `/get/{slug}` on the running server, extract it into
  an empty folder, install from there as a stranger would, and show what landed in Program Files.
- **Stage 1:** against a **copy** of the data directory on a spare port
  (`RSP_Platform__DataDirectory` and `RSP_Urls` — note `RSP_Urls`, not `ASPNETCORE_URLS`, since
  `appsettings.json` sets `Urls` and wins otherwise). Never the live database.
- **Stages 2 and 3:** run on this machine first, then DESKTOP-L2B85US. Trigger the scheduled task
  by hand rather than waiting a month.
- **Stage 3 mail:** prove the server can send before wiring the endpoint to it.

Building the whole solution fails while the live server holds `RemoteSupport.Shared.dll`, so
individual projects get built, or Clint is asked to stop the server.

## What is needed from Clint, and when

**Not now.** At stage 1, an SMTP sending mailbox: host, port, username, and a password typed into
the portal's own password field in his browser. It is never pasted into chat. Any local test
script that needs it will take it through `Get-Credential | Export-Clixml`.

Worth following the toolkit's own `EMAIL-SETUP.md` even though the password now lives in one
place instead of on every machine: a dedicated send-only mailbox, and an app password if the
provider supports one.

## Deferred to stage 3, not decided now

The brief and the toolkit differ slightly on audience. `Invoke-MonthlyRun.ps1` supports both a
`Support` recipient (technical, data files attached) and a `Client` recipient (plainly worded,
report only). The brief says reports go to "the preferred email address given by the I.T
company", so **Support is the default and the only one built now**. Sending straight to the end
client is a later tenant option, not part of this work.
