# 07 — Where things stand (28 October 2026, V2.5 — the final version before going live)

## In one paragraph

The **remote control** half of the product is built and **proven on real computers**: screen,
mouse and keyboard, secure screens, files and folders, copy and paste both ways, sound,
reconnecting, restarting, and modern video compression. The **business** half — clients, tickets,
job routes for every category, quotes, photos, roles, invitations, two-factor sign-in, office chat,
website chat, browser support, tickets by email, a knowledge base — is **built and passes its
automatic tests**, but much of it was written in the last four weeks and **has not yet been tried by
staff on real computers**. Between 2 and 6 October a **free version** was added in full (document 10);
on 7 October **sessions became encrypted end to end** (document 11). Between 8 and 15 October
(document 12) the live server **moved to PostgreSQL** and was **load-tested** (18 companies per server),
**PayFast** card payments, the **real website** and **company reviews** were built (all still hidden behind
the holding page), **platform admins and a control dashboard** were added, **code signing went into use**
(every program the server hands out is signed), everything moved to **.NET 10**, and a **pilot-company**
offer was built — plus damage reports, files on comments, going back a step, editing tickets, two
sessions at once with copy and paste straight between them, a sharper picture and handing over a website
chat. On 16 October (document 13) **every program learned to keep itself up to date** — customer computers
silently, in rings, with a rollback — which means that from the next release on, a fix reaches customers
without anybody visiting them (once each computer has had its one update by hand). Since 25 October
there are **three plans** (`HANDOVER.md` §161–§163, `docs/PRICING.md`): the **Free Version** (one computer,
unattended with its password, nothing to pay), **The Basic Version for Remote Workers** (R50 a month, 3 to 20
computers, attended or unattended) and the full version; Free moves up to either, Basic up to full or down
to Free. Between 17 and 27 October (document 14) came **job types**, **latest activity**, sorting and due
dates, a technician asking for **outside help**, **exporting a company's data**, the fixes from an **outside
security review**, per-company numbers in addresses, **removing a client**, and the website made ready to go
public. On 28 October (document 15) Job types was made to show itself to an owner once only, as intended. The live server went up on **1 October 2026** with only Anywhere IT on it; public signup is off and
the public front is a holding page **until 1 November 2026**, when the real website, signups and live
PayFast payments go on together. Internal testing by Anywhere IT staff is under way. **The next job is
proving, not building.**

---

## Stage by stage

The full plan is `docs/ROADMAP-PARITY.md` (technical) and `docs/ROADMAP-PLAIN.md` (plain English,
written 22 September — parts of it are now out of date; this page is newer).

| Stage | What it is | State |
|---|---|---|
| 1 | Core remote control | **Done and proven.** Session recording, privacy screen, remote printing (both ways) and Wake-on-LAN also built (not yet on hardware). **Two sessions at once, with copy on one machine and paste on the other straight between them** (15 October), not on hardware |
| 2 | Picture quality: fast capture, H.264, automatic quality, real pointer | **Done and proven.** **A picture that never stays blurry** (15 October: key frames a minute apart, a still screen sharpened) built, not on hardware. Two loose ends: nobody has watched automatic quality on a genuinely bad line, and the NVIDIA chip on one gaming laptop refuses to compress (it falls back and still works) |
| 3 | Sound | Listening **proven**. Talking both ways **built**, not on hardware |
| 3.5 | Simplicity of the console | **All built.** Copy and paste proven by hand |
| 3.6 | Dashboard, ticketing, roles, photos, light/dark | **All built**, mostly not on hardware. 13 October: **edit a ticket, sort by date, damage reports, files and photos on comments, go back a step** — walked in the browser, not on hardware |
| 3.7 | A route for each job category | **All six built** (Workshop, Call out, Walk in, Remote support, General, Recurring). **Job types** (27 October): each company switches off the categories and steps it does not use, without breaking a route — built in the portal and the console, not on hardware (checks 897–911) |
| 3.8 | Office chat, browser support, website chat, tickets by email and a contact form, a knowledge base | **Built**, not on hardware. A website chat can be **handed over** (15 October). Tickets by email not yet tried against a real mailbox; Microsoft 365 sign-in for it is later (Clint: "leave it for now") |
| 3.9 | Outside help | **All four steps built**, and **a technician asking for it** from a session (22 October). Not on hardware |
| Free | **The free version** | **All built 6 October**, none on hardware. Not public: signup is off |
| 4 | Hosting, security, seats, payment | **Mostly done.** Live server, TLS, two-factor, invitations, backups (and a nightly copy to the office, built — check it is switched on), end-to-end encryption, the reinstall check: done or built. **PostgreSQL on live since 9 October**; load test run 12–13 October (18 companies per server; the port ceiling found there is fixed and on live). **PayFast (4.8) built in five stages** (13–14 October) and proved against PayFast's sandbox; the live server is still on the sandbox account and the pages are hidden. **Platform admins and the control dashboard** (13 October) built. **Updating the programs themselves (4.6) BUILT 16 October** — customer computers and the console, in rings, with rollback; not on hardware (checks 723–739, 737 above all) — **on hardware 19 October the background services updated themselves; the tray app's fault found there is fixed** (§152). **A company's data export (4.9)** built 22 October. **Monitoring**: a check from the office PC, built 27 October, not installed yet. Not done: automatic placement of companies on servers (control stages C–E) |
| 5 | Code signing | **Done.** Sectigo OV certificate issued 14 October 2026 (valid to 18 November 2027), on a YubiKey. **The first signed deploy went live on 15 October** — all 12 programs and scripts the server hands out are signed and timestamped, and `Publish-Platform.ps1` refuses to upload anything unsigned |
| 6 | Mac, Linux, Android, iPhone | **Not started.** Deliberately last. (.NET 10, since 14 October, is what a Mac version would need) |

## How to tell what has been tested

- `docs/TEST-3.6.md` is the numbered checklist of everything not yet proven on real computers —
  933 checks, in lettered sections that match the handover sections. Ticked items have been done.
- Every section of `HANDOVER.md` ends with **"Not on hardware"** or says what was proven where.
- `CLAUDE.md` gives a one-paragraph status for every piece of work.
- `tools/testchecklist/` is the internal-testing program on the office share (testers tick checks and
  log faults; the day's report is folded back into `TEST-3.6.md`).

## The pilot companies — next

Clint agreed a deal with **two pilot IT companies**: five seats free while they are pilots (more on
request since 2026-10-25, invoiced). On 22 October, a week in with nothing used, he asked for a way to
end a pilot — Owner → Pilots → *End the pilot…*, by notice (§153). Each was sent a
one-use link made at **Owner → Pilots** (or `pilot-link "<company>" [email]` on the server); it opens
`/pilot/{token}`, which works even with signups off and behind the holding page. The pilot guide that
goes with it — setting up step by step, with screenshots — is
`docs/pilot-brochure/The Remote Support Program - Pilot Guide.pdf` (made from `brochure.html` in the same
folder). **Before the pilot links are sent:** confirm the deploy containing `PilotCompanies` (§146) is on
the live server (Owner → Pilots exists), and tick **Supervisor** beside any Anywhere I.T login that shows
only Owner and works tickets.

## Before it is sold to the public

In the order agreed:

1. Finish internal testing (`TEST-3.6.md`) and fix what it finds. Every hardware run so far has
   found at least one fault that no automatic test could.
2. The pilots' experience, and their faults fixed.
3. The remaining security items in document 03, section 9: an outside penetration test, and making sure
   the office copy of the backups is running.
4. **PayFast on the live account**: `deploy\Set-PayfastKeys.ps1` with the live merchant details, then
   `Payfast:Sandbox` false (`docs/NEXT-SESSION-PAYFAST.md`). PayFast's Recurring Billing page already
   points at the live server.
5. **Publishing the real website** (`Publish-Platform.ps1 -RealSite`) and switching public signup on —
   Clint's decision, not a developer's. **Decided: 1 November 2026.** Since 27 October `-RealSite` opens
   signups and our chat bubble too, and **every deploy after that day needs it**. Clint has not yet checked
   the website's words (`docs/WEBSITE.md`; `TEST-3.6.md` 924).
6. **Install the site check on the office PC** (`deploy\monitor\Install-SiteWatch.ps1`, document 04), so
   somebody hears when the site is down before a customer does.

**Before the free version is offered to the public:** its hardware checks (`TEST-3.6.md` sections
BR–BX, checks 459–496 — above all 469, a real free install, and 475, the *Being controlled remotely*
bar on a real screen).

---

## Known faults still open

From `docs/BUGS.md` (which records what was tried, so read it before investigating):

| # | Fault | State |
|---|---|---|
| 2 | The customer's tray app may keep saying "support in progress" after a session ends | Cause found and fixed in September; left open until seen fixed on a real machine |
| 7 | The customer's desktop shortcut logo has a black square behind it | The logo file has no transparent background. Fixed by supplying a transparent PNG logo, not by code |
| 8 | A file transfer over a really poor line | Fixed and proven on a simulated bad line; never pushed over a real bad one |
| 9 | ESET antivirus briefly blocks the health check install | Automatic retries built (1, 5, 15 minutes); not seen on a real machine yet. Signed programs (since 15 October) should also help |
| 10 | The **first** copy after the customer logs in is not passed to the technician | **Fixed 6 October** (`HANDOVER.md` §93), not yet seen fixed on a real machine |
| 11 | Thousands of machines connecting at once stalled the server | **Found and fixed** during the PostgreSQL work (§121) — it was never the database |
| 12 | Past ~28,000 connections the whole server stopped: Caddy ran out of ports to the platform | **Fixed** 13 October: Caddy reaches the platform through a Unix socket; **on live since 15 October**. Memory is now the limit (18 companies per server) |
| 13 | A message a machine sends in the same instant as its hello can be lost | Found by a test tool on 18 October. Low: no real program is known to do it. **Not fixed** — `BUGS.md` says how |
| — | A "ding" sound when pasting on the customer's machine | Never explained. Harmless |

Smaller known gaps listed in the handover:

- Photos added later to a multi-machine job always go to machine 1.
- A machine added to a job by mistake cannot be removed (abandon and rebook).
- A workshop device password cannot be changed after the ticket is created.
- The customer is not told when someone types a wrong unattended password.
- Scripts run as the system account, not as the logged-in person.
- *Being controlled remotely* does not show on Windows' secure screens (a UAC prompt, Ctrl+Alt+Del).
- PayFast's sandbox never runs a subscription's scheduled monthly charge, so a renewal has only been
  proved by the test tool and by a charge pressed by hand; the first real one will be on the live account.
- A company with only owner-only logins and technicians has nobody who may close a ticket (a technician
  never closes). That is the deal: the owner ticks Supervisor or Office administrator, and takes a seat.
- The SQLite fallback on the live server was meant to be retired about a week after the PostgreSQL switch
  (§125). Not done yet.

**Assumptions Clint may still change** (each is a small change): free-version signups wait for approval
like any other; an upgraded company starts a 14-day trial; a pilot company is Active at once with no trial.

---

## Dates and renewals to watch

| What | When | What happens if missed |
|---|---|---|
| Domain `theremotesupportprogram.com` (Afrihost) | Yearly — check the date in Afrihost | **Every installed customer app stops working** |
| xneelo Cloud server | Monthly billing | The server can be deleted. In September 2026 an earlier server was lost when its xneelo account was disabled, and the supplier kept no copies |
| xneelo shared hosting (email) | Monthly billing (R99) | Email for the domain stops |
| Code-signing certificate (Sectigo) | **The first certificate expires 18 November 2027.** The two-year term includes a second one at no charge; Sectigo may check the company again before issuing it | New builds cannot be signed, and `Publish-Platform.ps1` will refuse to deploy. Already-signed files keep working (they are timestamped) |
| Let's Encrypt website certificate | Every 90 days | **Automatic** — Caddy renews it. Only a problem if the server is broken |
| Pilot links | 30 days after each is made | The link stops working; make another at Owner → Pilots |
| Microsoft app secret (Tickets by email, *Sign in with Microsoft*), once registered | When it was made to run out — diary it (`docs/MICROSOFT-365.md`) | Every company's Microsoft 365 mailbox stops being read; make a new secret and run `deploy\Set-MicrosoftApp.ps1` |
| .NET 10 | Microsoft supports it until November 2028 | Security updates stop; rebuild on the next long-term version before then |

---

## What a new team should do first

1. Get the keys and accounts from document 08. Without the SSH key, the YubiKey and the password
   manager, the live server cannot be maintained or a release signed.
2. Restore the code on a Windows PC, build it, and run the platform locally (document 05).
3. Run a handful of the test tools (`securitycheck`, `workflowcheck`, `unattendedwire`, `pgcheck`) to see
   them pass — that confirms the build environment is right.
4. Read `HANDOVER.md` §1–14 (the foundations), §112 (end-to-end encryption, before touching anything a
   session sends), §125 (the live server on PostgreSQL), §147–148 and §152 (automatic updates, before
   releasing anything), §158–159 (the security review and the addresses), §166–168 (job types, before
   touching a ticket route), §169 (going public) and §132–146 (the other recent work).
5. Make a test company on a local platform with `tools/workflowdemo` and click around.
6. Only then touch the live server — and take a backup first.
