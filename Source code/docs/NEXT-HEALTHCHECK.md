# Fold the health check toolkit into the platform — **DONE 2026-09-13**

Written 2026-09-11, finished 2026-09-13. Kept for the reasoning, not as a to-do list — the same
way `docs/NEXT-TICKETING.md` was kept.

**What was built and how each piece was proven is in `docs/PLAN-HEALTHCHECK.md`. The state of the
result is §17 of `docs/HANDOVER.md`.** Read those; this file is the brief that was worked to.

The decision the brief asked to be put first was taken: **the endpoint builds the report and
uploads it, and the server sends the mail.** Neither of the two options the brief set out was
chosen unchanged — the server does not build the report, because the PDF step needs the Edge that
is on the endpoint and not on a Linux host, and that step was the most expensive part of the
toolkit to get right.

One item in the brief is deliberately **not** built: the last-run indicator on the device rows and
the client page. `/portal/healthchecks` covers the need for now.

Read `docs/HANDOVER.md` first for the architecture, the decisions already made, and the working
agreements. This file is the brief for the health check work only.

---

## Clint's words

> Incorporating the healthcheck toolkit from a previous project into the remote support program.
> The idea is that the healthcheck will be installed as part of the client installer, that will run
> monthly on a client's machine, do the maintenance and mail the report to the preferred email
> address given by the I.T company. This would need to be part of the initial tenant setup.

---

## Where the toolkit is

**`C:\PCHealthCheck`** — a separate, finished project on this machine. Read its own
`CLAUDE.md` and `HANDOVER.md` before touching anything; both are good and were written for
exactly this situation.

Sample output of a real run is on the desktop: `C:\Users\Clint\Desktop\PC Health Check\`.

**Status there:** working end to end and tested, *not yet used on a real client machine*.

### What it is

PowerShell 5.1, no server, no database. A scheduled task running as SYSTEM, monthly:

1. Creates a system restore point — **and skips maintenance entirely if it can't**
2. Cleans temp files, TRIMs or defragments drives, runs `sfc /scannow`
3. Runs a **read-only** health check
4. Builds a branded HTML report and converts it to PDF using the copy of Edge already on the machine
5. Emails it

| Script | Job |
|---|---|
| `src/Get-ClientHealthCheck.ps1` | Read-only inspection. Never changes anything. |
| `src/Invoke-ClientMaintenance.ps1` | The only script that modifies a machine. |
| `src/New-ClientReport.ps1` | JSON → branded HTML → PDF. |
| `src/Invoke-MonthlyRun.ps1` | Orchestrates the above, then emails. |
| `src/Install-HealthCheck.ps1` | Creates the scheduled task and writes settings. |

### The Findings contract — the most important thing in that project

The collector emits raw data **and** a `Findings` array: Category, Severity
(Critical/Warning/Info/OK), Title, Detail, Recommendation — all already written in plain client
language. **Severity is decided in PowerShell and must stay that way**, so the same machine
produces the same severity every month.

`Recommendation` text is rendered into the client's PDF under "What we suggest". It is addressed
to the client, never to the technician: *"worth planning a replacement"*, never *"worth quoting a
replacement"*.

---

## What to build

**1. Ship it with the client installer.** `deploy-client/Install-Client.ps1` should lay the health
check down alongside the tray app and agent, and register the monthly scheduled task. Today
`Install-HealthCheck.ps1` is run by hand with `-Organisation`, `-EmailTo`, SMTP settings and so on;
those values have to come from the tenant instead of from a technician's typing.

**1a. But the customer download has no installer at all — this blocks item 1.** Found 2026-09-11
while deploying the new build to the test machine.

`/get/{slug}` offers one route, and it is the *portable attended* one: "Download and unzip, then run
RemoteSupport.Client.exe". `PayloadService.BuildAsync` zips whatever is in `payloads/client`, which
`build.ps1` stages from `dist/client` — three executables and branding. **The installer scripts are
not in it.** `deploy-client/` has them (`Install-Client.ps1`, the `.bat` wrappers, the uninstaller)
but that folder only exists on Clint's laptop and is not served by anything.

So today a customer who follows the download page gets a tray app they run by hand: no Program
Files install, no unattended agent, no startup registration — and nowhere for a scheduled task to
live. A health check that ships "as part of the client installer" has no installer to ship with.

The fix is small and was already anticipated — `PayloadService`'s own summary says *"Phase 3
replaces the zip with a signed installer that fetches the same branding at install time"*:

- stage `deploy-client/`'s scripts into `payloads/client` in `build.ps1`, so the branded zip
  contains them and `BuildAsync` needs no change (it already swaps `branding/` per tenant);
- give the download page two clear routes — **install properly** (recommended, with the agent and
  the health check) and **one-off support** (portable, nothing installed), rather than silently
  offering only the second;
- keep the wrapper convention in §5 of the handover: each `.bat` re-launches *itself* elevated and
  passes nothing through, because arguments through cmd → PowerShell → cmd break on a path with a
  space, and the real path has one. **New wrapper files must be added to the installer's
  `Copy-Item -Exclude` list** or they end up installed into Program Files.

Code signing is still deferred (§10), so "signed installer" is not on the table yet — an unsigned
zip with scripts is the honest interim.

**2. Make it part of tenant setup.** The IT company sets, once, in the portal:

- whether the health check runs at all
- the address reports go to (their own, per the brief — "the preferred email address given by the
  I.T company")
- what day of the month, and whether maintenance runs or only the read-only check

`Tenant` already carries `SupportEmail`, branding and colours; this needs its own fields and a
migration. Follow §15/§16 of the handover for how migrations are done here.

**3. Decide how the report gets out.** See "The decision worth making first" below — this is not a
detail, and it changes what gets built.

**4. Surface it in the portal.** A machine's last run, its findings, and when the next one is due
belong on the device row and the client page, both of which now exist and are grouped by customer.
This is where the toolkit gains most from being absorbed: it currently has nowhere to show
anything.

---

## The decision worth making first

**In the standalone toolkit, every client machine stores an SMTP password.** DPAPI-encrypted with
the machine key, so not plain text, but an administrator on that machine can recover it. Its own
`docs/EMAIL-SETUP.md` is candid about this and tells you to use a dedicated send-only mailbox.

**The Remote Support Program has something that project deliberately did not: a server.** The
endpoint already holds an authenticated WebSocket to it. So the obvious design is:

> the machine runs the check and **uploads the result**; the **server** builds the report and sends
> the mail, using SMTP settings held once per tenant.

That removes the mail password from every customer machine, makes the reports visible in the portal
without a mailbox round trip, and means changing the SMTP password is one portal edit instead of a
reinstall everywhere. The cost is new protocol messages, server-side report generation (the current
PDF step shells out to Edge — check that is acceptable on a Linux server, see §11 of the handover),
and a store for the JSON and PDFs.

**Do not just port the mail-from-the-endpoint design across because it works.** Put the choice to
Clint early, with the trade-off, before writing code.

---

## Constraints and warnings

- **PowerShell 5.1 on the endpoint.** No ternary, no `??`, no `-Parallel`. It ships to whatever
  Windows the client has.
- **The health check never changes a machine.** Maintenance is the only script that does, and it
  **never runs without a restore point**. Windows Server has no System Restore, so maintenance
  always skips there — by design, not a bug.
- **Never cry wolf.** A false Critical on a healthy machine destroys trust in the whole report. The
  toolkit's handover lists real false alarms already caught (ESET, blank-password).
- **Scripts run as SYSTEM** under the scheduled task, and so does anything our agent runs (§4 of
  the handover). SYSTEM has no user profile — watch for `$env:APPDATA` and Downloads paths.
- **`samples/` in the toolkit is stale.** Those four JSON files predate its v2.7 wording fixes and
  still contain the old vendor-voiced recommendations. Fine for exercising the report builder,
  misleading for anything about finding text.
- **Read `C:\PCHealthCheck\HANDOVER.md`'s "Traps" section before editing its PowerShell.** Single
  item pipelines returning scalars, case-insensitive variable names, switches across a process
  boundary, BOMs from `Set-Content -Encoding UTF8`. Each cost real time there and will recur.
- **Multi-tenancy is a reversal for that project, and a deliberate one.** Its `CLAUDE.md` says
  multi-tenancy, a web UI, a database and ticketing are all out of scope — because *that* project
  had to stay small to ship. Absorbing it into this platform is the opposite decision, made on
  purpose. Do not treat its scope rules as binding here, and do not "tidy" that repo to match.
- **Branding.** The report is already branded; the platform has per-tenant logo and colours
  (`BrandPalette`, `Tenant.LogoFile`). The report should use the tenant's, not Anywhere IT's
  hardcoded.
- **Two audiences, never confused.** The IT company reads the JSON and the technical detail; the
  end client reads the report and is not technical. South African English.

---

## How to work

Everything in §12 of the handover applies. The ones that matter most here:

- **Measure before changing code.** That project's own hardest-won lesson is the same as this
  one's: *"when a fix changes nothing, the diagnosis is wrong — not incomplete."*
- **Test any script locally before it goes near a customer machine.** Both projects say this.
- **Give Clint one file or one command to run**, not a list of steps, in its own fenced block.
- **Never take Clint's passwords in chat** — `Get-Credential | Export-Clixml`. This will come up
  immediately, because SMTP setup needs a password. Solve it without ever seeing it.
- **Show, don't describe.** A screenshot of the report or the portal page beats three paragraphs.
- Commit before large or risky edits.

---

## Worth knowing before you start

- The live server usually runs on port 8090 from Clint's own terminal. Restarting it needs him to
  stop it, and building the whole solution fails while it holds `RemoteSupport.Shared.dll` — build
  individual projects instead.
- To see portal work against real data without touching his server: copy
  `src/RemoteSupport.Platform/bin/Release/net8.0/data` to a scratch folder, seed a throwaway admin
  into the **copy**, and run the new build on a spare port with
  `RSP_Platform__DataDirectory` and `RSP_Urls`. Note `RSP_Urls`, not `ASPNETCORE_URLS` —
  `appsettings.json` sets `Urls` and wins otherwise. This was used throughout the ticketing work
  and is the reason nothing was ever tested against the live database.
- `.ps1` files will not run from a plain double-click here: the execution policy blocks them. Use
  `powershell -ExecutionPolicy Bypass -File ...`.
- Keep `.ps1` and console output **plain ASCII**. Windows PowerShell 5.1 reads a `.ps1` as ANSI
  unless it has a BOM, so an em dash becomes three bytes and the parser reports an unterminated
  string two lines later. That already happened once.
