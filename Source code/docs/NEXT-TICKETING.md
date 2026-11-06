# Ticketing system — DONE 2026-09-09

> **This work is complete.** All three items below were built: the dashboard, the client page, and
> devices grouped by organisation. See §16 of `docs/HANDOVER.md` for what the portal now does.
> This file is kept for the *reasoning* — why each screen exists and what was wrong with the old
> one — not as a to-do list. The current brief is `docs/NEXT-HEALTHCHECK.md`.
>
> Two things from this brief were **not** done and are still open:
> - The orphan-ticket backfill (`tools/backfill-clients`) has not been run against live data.
>   Tickets #12 and #13 are still filed under no client. Dry run first; it needs the server stopped.
> - "A way to start a session" from the client page. The portal has no session-start plumbing at
>   all — that lives in the technician console over `/ws/tech` — so it needs protocol work, not a
>   button. Left out rather than faked.

---

# Original brief — make the ticketing system usable

Written 2026-09-09. Read `docs/HANDOVER.md` first for the architecture, the decisions already
made, and the working agreements. This file is the brief for the ticketing work only.

---

## Clint's words

> The ticketing system is not user friendly at all. It needs a dashboard, needs to list clients,
> and all machines in that organisation, clearly stating open tickets etc.

---

## What exists today

The data model is fine. The screens on top of it are not.

**Entities** (`src/RemoteSupport.Platform/Data/PlatformDb.cs`) — everything needed is already
there and already related:

- `Client` — the IT company's customer. Name, contact, phone, email, address, notes, archived.
- `Ticket` — `Number` (per-tenant sequential), subject, description, `Status`
  (Open/InProgress/OnHold/Resolved/Closed), `Priority`, `Source`, `Category`
  (RemoteSupport/WalkIn/Workshop/CallOut), `ClientId`, `MachineName`, `AssignedTo`, call-out
  address and `AppointmentUtc`, workshop fields, timestamps.
- `TicketNote` — history, with `IsSystem` for automatic entries (call accepted, script run,
  session ended).
- `Device` — machine name, user, OS, CPU, cores, RAM, disk, last seen, **and `ClientId`**.

So "all machines in that organisation" is `Device.ClientId == client.Id`, and "their open tickets"
is `Ticket.ClientId == client.Id && Status is Open/InProgress/OnHold`. **The link already exists;
nothing surfaces it.**

**Portal pages** (`src/RemoteSupport.Platform/Pages/Portal/`):

| Page | What it is now | Problem |
|---|---|---|
| `Index.cshtml` (`/portal`) | An **onboarding** page: "1. Brand it, 2. Add technicians, 3. Share your link", plus tenant stats (seats used, devices seen, sessions, plan). | This is the dashboard, and it tells you nothing about the work in front of you. No ticket counts, nothing needing attention, no today's appointments. |
| `Tickets.cshtml` | A flat table with filter (open/closed/all), search, date range. Columns: #, Type, Subject, Client, Machine, Assigned to, Status, Opened, Time. | Flat. No grouping, no per-client rollup, no sense of priority or age. Fine as a list; useless as a way to run a day. |
| `Clients.cshtml` | Table of name / contact / phone / ticket counts, plus an "add client" form. The only drill-down is a **History** button that runs a text search of the tickets list on the client's name. | A client is not a page. You cannot see their machines. The history link is a string search, not a relationship — it will break on similar names. |
| `Devices.cshtml` | Flat table: Machine, Client, OS, Processor, RAM, Disk, User, Last seen. | Not grouped by client. No link to that machine's tickets. |
| `Ticket.cshtml` | Single ticket detail with notes. | Reasonable; leave alone unless the redesign needs it. |
| `NewTicket.cshtml` | New ticket form with client type-ahead. | Works. Do not break the type-ahead — it exists to stop duplicate clients. |

**Technician console** (`src/RemoteSupport.Technician/TicketsForm.cs`) — a separate WinForms view
over the same data via the `/ws/tech` protocol (`TicketListRequestMsg`, `TicketOpenMsg`,
`TicketCreateMsg`, `TicketUpdateMsg`, `TicketNoteAddMsg`, `ClientSearchMsg`). List columns are
just #, Subject, Machine, Status, Priority. It has never been verified by eye (§8 of the handover).

---

## What to build

Clint named three things. Take them as the requirement.

**1. A real dashboard at `/portal`.** Replace the onboarding cards with what someone actually
needs when they sit down:

- Open tickets, broken down by status and by priority; how many are unassigned; how many are
  assigned to *me*.
- Anything overdue or ageing — oldest open ticket, tickets untouched for N days.
- Today's and this week's **call-out appointments** (`AppointmentUtc`) — these are the only
  time-critical items in the system.
- Workshop jobs currently in.
- Devices currently online, and any that have not been seen for a long time.
- Keep the onboarding cards, but move them somewhere sensible or show them only while the tenant
  is still setting up (no technicians, no branding, no devices).

**2. A client page.** `/portal/clients/{id}` — one page per customer, showing:

- Their details, editable.
- **Their machines**, from `Device.ClientId`: machine name, user, OS, spec summary, last seen,
  online/offline, and a way to start a session or open that machine's ticket history.
- **Their tickets**, open ones first and clearly separated from closed, with number, subject,
  category, status, priority, assigned to, age.
- The counts at the top: open / in progress / on hold / resolved this month.

The clients list should then link to this page rather than to a text search.

**3. Machines grouped by organisation.** Either fold this into the client page (above) and make
`/portal/devices` group by client with an "unassigned" bucket at the top, or make the devices page
client-first. Unassigned machines matter — they are the ones nobody has linked to a customer yet,
and they should be easy to find and assign.

Throughout: **open tickets must be obvious**. Counts and status pills at every level — dashboard,
client list, client page, device row.

---

## Constraints and warnings

- **Do not change the data model unless you must.** The relationships are already right. If a
  migration is genuinely needed, use EF migrations (`Data/Migrations`, `Database.MigrateAsync()`)
  and remember SQLite stores no timezone — every `DateTime` needs the UTC value converter in
  `PlatformDb`.
- Keep the ticket categories and their extra fields exactly as decided (§9 of the handover). Walk
  In, Workshop, Call Out, Remote Support are settled.
- Keep the new-ticket client type-ahead working — it exists to stop duplicate client records.
- Anyone can reassign a ticket and the whole history follows it. Do not add ownership rules.
- Roles are `tenant`, `billing`, `owner`. Reception and accounts sign in for billing and can see
  ticket history, so the client and ticket pages must be reachable by `billing`. Razor folder and
  page policies combine with **AND** — check `Pages/Portal/PortalPage.cs` and the policy setup
  before assuming a page is visible to a role.
- The portal styling lives in `wwwroot/site.css` with existing `card`, `stat`, `stats`, `grid2`,
  `grid3`, `pill`, `pill-warn`, `pill-muted`, `btn`, `btn-sm`, `btn-accent`, `alert` classes.
  Reuse them; the portal should stay visually consistent with the marketing pages.
- The technician console's `TicketsForm` reads the same data over the WebSocket protocol. If you
  add fields the console should show, they need protocol messages too — and the console has to be
  rebuilt and re-staged to `demo/technician`, which is easy to forget.

## How to work

- The server is usually already running on port 8090. Restarting it requires stopping Clint's
  instance, which also locks `RemoteSupport.Shared.dll` and breaks a full solution build — build
  individual projects instead, or ask him to stop it.
- There is test data: clients including "Roger Rabbit", devices `DESKTOP-L2B85US`, `BILL-PC`,
  `INPUT-PC`, `SPEC-PC`, `TICKET-PC`, and existing tickets.
- Show Clint the result rather than describing it. A screenshot of the new dashboard beats three
  paragraphs. He wants things proven, not asserted.

---

## One more thing the client page has to cope with

A machine can appear **twice**. `DeviceId` comes from `C:\ProgramData\RemoteSupport\device.json`,
which the uninstaller deletes, so an uninstall/reinstall creates a second `Device` row with the
same machine name and no `ClientId`. DESKTOP-L2B85US is in that state right now — one row online
and linked, one offline and stale.

So the client page and the devices list should:

- show stale/offline duplicates without pretending they are separate machines,
- make it easy to link an unassigned device to a client, and
- ideally offer to merge or retire the old row.

The underlying fix (preserve `device.json` across reinstall, or match on machine name) is out of
scope for the UI work but is recorded in §15 of the handover.
