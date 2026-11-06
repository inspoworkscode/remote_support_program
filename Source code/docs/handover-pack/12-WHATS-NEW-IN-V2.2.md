# 12 — What is new in V2.2

**V2.1** of this pack was written on **7 October 2026**. **V2.2** was written on **15 October 2026**. This
page lists what was added in those eight days, so somebody who read V2.1 does not need to read everything
again. Documents 00–09 have been updated to match; documents 10 and 11 still list what V2 and V2.1 added.
(Three items from 8 and 9 October — five seats, several owners, emailing the client from a ticket — were
already added to the end of document 11; they are listed here too.)

Unless a row says otherwise, everything below was **built and passed its automatic test tools**, but has
**not yet been tried on real customer computers**. The hardware checklist is `docs/TEST-3.6.md` (the
lettered section is named in each row), and the numbered section of `docs/HANDOVER.md` is the full
engineering record.

---

## The biggest changes

| What | In short | Record |
|---|---|---|
| **The live server runs on PostgreSQL** | Since 9 October 2026, 11:56 UTC. Rehearsed first on a copy of live (all twelve steps), then switched: down 11 seconds, all 2,538 rows identical. The database and the data each have a disk of their own. Every test tool passes on both databases; **every model change now needs two migrations** (document 05). The way back to SQLite exists and is tested | §118–§121, §125 |
| **Load-tested on xneelo** | 18 companies per server (each 10 technicians, 500 machines with agents, 10 sessions) — about 18,500 connections on a 4-processor, 8 GB server. The first ceiling found (Caddy running out of ports) was fixed — Caddy now reaches the platform through a socket, **on live since 15 October** — so memory is the limit now | §126, BUGS #12 |
| **Code signing is in use** | The Sectigo certificate was issued on 14 October; the **first signed deploy went live on 15 October**: all 12 programs and scripts the server hands out are signed and timestamped, and `Publish-Platform.ps1` refuses to upload anything unsigned | `CODE-SIGNING.md` |
| **Card payments through PayFast** | A full-version signup in South Africa ends at PayFast (R0 now, the package after 14 days); PayFast's notice switches the company on. The amount follows the number of people; receipts; reminders and suspension when a payment does not come (14 days' grace); cancelling. Proved against PayFast's sandbox; **the live server is still on the sandbox and the pages are hidden** | §134–§137, CQ–CT |
| **Platform admins and a control dashboard** | Named people for `/owner` at four levels, with a log; a card per server showing how full it is, emailed when it crosses 70% or 85%; companies on other servers changed through their own server. Suspending a company now takes effect at once | §132–§133, CO–CP |
| **Pilot companies** | Five seats free for as long as they are customers, extra seats invoiced; a one-use link opens `/pilot/…`, Active at once with no trial and no card. The guide that goes with it: `docs/pilot-brochure/The Remote Support Program - Pilot Guide.pdf` | §146, DC |
| **The owner-only login oversees** | The free owner login runs the company and comments, but no longer works tickets (it could, through the portal, before) | §146, DC |
| **Everything on .NET 10** | .NET 8 leaves Microsoft's support on 10 November 2026. **Proven on hardware**: every test tool, and the new programs on Windows 10 (checks 678–683) | §141, CX |

---

## Tickets

| What | In short | Record |
|---|---|---|
| Damage reports | *Not repairable → Damage report needed*: a note, the report attached, time and amount; emailed to the client (never the time or amount) | §128, CL |
| Files and photos on any comment | Uploaded as picked; a comment can be files alone; they go with the comment if it is emailed | §128, CL |
| Go back a step, at every stage | With a reason; the undone step stays in the story crossed through; warns about invoices, payments and emails | §128, §131, CL |
| Comment boxes grow | Up to 15 lines, in the portal and the console | §128, CL |
| Edit a ticket | Subject, description, appointment, category (a new category restarts its route) | §130, CN |
| Sort tickets by date | Portal and console; the dashboard's Unassigned count fixed | §130, CN |
| Job types in colour; the portal menu as tabs | | §124, CJ |
| Linking a ticket to a bigger job no longer closes it | The job waits for its parts, and says which | §122, CH |
| Part of / not part of a job; unlink a machine from its customer | | §122, CH |
| Emailing the client from a ticket (9 October) | The original fault and that one comment, from the company's support address; their reply comes back onto the ticket | §123, CI |

## Remote control and the customer's side

| What | In short | Record |
|---|---|---|
| A picture that never stays blurry | Key frames a minute apart; a still screen sharpened for two seconds after a change | §142, CY |
| Two sessions at once | One per monitor, two at most | §143, CZ |
| Copy on one machine, paste on the other — straight between them | Files go from machine A to machine B through the server, sealed, never through the technician's PC | §144, DA |
| Off means offline | The server notices a machine that has gone silent within about 45 seconds | §122, CH |
| Reconnecting asks nothing | Within 30 minutes of the end of the last session, the same ticket carries on | §122, CH |
| Asking for help: subject, description, a picture of the screen | And the app remembers the customer's name, and says when the picture shows only the desktop | §122, §129, CH, CM |
| A new machine's two hellos at once | A first install's tray app and agent arriving together no longer lose one's details | §127 |

## Chat, the company and the website

| What | In short | Record |
|---|---|---|
| Hand over a website chat | To anybody signed in now; the visitor is told; the dings follow it | §145, DB |
| Reviews from companies | Asked once a week in; published only after an admin approves and the company ticks yes | §139, CV |
| The real website | About a dozen pages, light and dark, screenshots of the real windows with made-up companies, a cookie line. **Hidden behind the holding page** until Clint says so | §138, §140, CU, CW |
| Five seats, ten at most (8 October) | R500 for five seats in any mix of roles; R300 per extra seat; ten at most; one owner-only login free | §116, CF |
| Several owners (8 October) | Only an owner makes or removes another; always at least one | §117, CG |

## Security and running it

| What | In short | Record |
|---|---|---|
| The owner key opens nothing alone | Two old addresses that took the key without a code were removed | 15 October, `securitycheck` 78 |
| Keys are never printed, and were rotated | `go-live.sh` says where a key is, never the key; `deploy\Rotate-Keys.ps1` replaced the owner and server keys on 15 October after a log was pasted into a chat | document 03, 8b |
| Backups to the office | An office PC fetches each night's encrypted backup through an account that can only list and fetch. Built and tested; **check it is set up** | §120 |

---

## New test tools

`pgcheck`, `pgcopycheck`, `switchcheck`, `backupcheck` and `Run-DbHarnesses.ps1 -Postgres` (the database
move); `loadtest`, `loadstatscheck` and `unixsockcheck` (the load test and the socket); `silentoffline`,
`reasonwire`, `requestwire`, `firsthello`; `clientupdatecheck`, `ticketstepcheck`, `ticketeditcheck`;
`admincheck`, `controlcheck`; `payfastcheck` (it talks to PayFast's real sandbox) and `signuppaycheck`;
`sitecheck`, `siteshots`, `reviewcheck`; `sharpwire`, `keyframewire`, `codecbench --sharp`;
`sessionlimitwire`, `twosessions`, `bridgewire` (two real hosts); `pilotcheck`. Most that make a
database run on **both** SQLite and PostgreSQL.

## Putting V2.2 live

The platform side goes out with `deploy\Publish-Platform.ps1` (its database changes apply by themselves;
it now refuses to upload unless every program is signed, so plug the YubiKey in). Then every technician
re-downloads the console. The sharper picture, two sessions and copying between machines, the request
window and the remembered name change the customer's programs too, so those need a **reinstall on
customer computers**. Before the pilot links go out, check the deploy holding `PilotCompanies` is on the
live server (Owner → Pilots) — see document 07.
