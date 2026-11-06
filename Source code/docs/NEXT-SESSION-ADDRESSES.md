# Per-company numbers in addresses, not database ids

**Brief written 2026-10-23, after the outside security review (HANDOVER §158, finding F8). Build it BEFORE go-live on 1 November.**
**BUILT THE SAME DAY — HANDOVER §159** is what it turned into, what building it found and how it is proved; it wins over this brief.
Read `HANDOVER.md` §158 first, then §82 (one sign-in at a time, the console's Tickets hand-off), §80 and §96 (the knowledge base),
§125 (PostgreSQL — **every model change is TWO migrations**) and §134 onwards only if you touch anything near billing (you should not).

## The one-paragraph version

Every ticket, client, knowledge base article and photo has a database id from ONE sequence shared by every company on the server.
The portal puts those ids in its addresses (`/portal/tickets/129`) and in its pages, so a company that makes a ticket on Monday and
another on Friday can read, from the gap, how many tickets the whole platform made that week — and the same for clients and photos.
**Nobody can reach another company's data this way** (every page asks the company first; the tester scanned 1–140 and found only
their own), so this is a business-information leak, not a breach. The fix: **everything a portal page shows names things by a number
that counts within the company**, and the old id addresses keep working for that company's own people.

## Clint's words

> One thing he also mentioned was that tickets are counted by all tenants. If one company creates a ticket, and another company
> creates a ticket, it seems they become sequential. Is that correct?

> would fixing it after going live not be more riskier?

Agreed: yes. Old id links are in bookmarks, browser history, the console's hand-off and notifications. Today only Anywhere IT and the
two pilots hold any; after 1 November paying companies will. So: now, with the old addresses still working.

## What is already true (do not re-derive)

- **Tickets already have a per-company number**: `Ticket.Number`, given by `TicketService.CreateAsync` (highest in the company + 1,
  under `NumberLock`), with a **unique index on (TenantId, Number)** (`PlatformDb.cs`, `HasIndex(t => new { t.TenantId, t.Number })`).
  It is what people see (#2) and what client emails use (`[Ticket #12]`, matched by company AND number in `TicketIntake`).
  **No migration is needed for tickets.**
- **Clients and knowledge base articles have NO per-company number.** They need one — a column, a unique index on (TenantId, Number),
  a backfill numbering each company's existing rows in id order, and new rows numbered as tickets are. **Two migrations, the same
  name in each** — `--context PlatformDb` AND `--context PgPlatformDb --output-dir Data/PgMigrations` — or `tools/pgcheck` fails;
  check each Designer says the right context (§123's trap). Never remake `PgInitial`.
- Routes today: `/portal/tickets/{id:int}`, `/portal/clients/{id:int}`, `/portal/kb/{id:int}`, `/portal/kb/edit/{id:int?}`,
  `/portal/photo/{id}`, `/portal/kb/image/{id:long}`, `/portal/recurring/{which}`. Links to them are built in about 20 page files
  (`Index`, `Tickets`, `Ticket`, `_Timeline`, `Client(s)`, `Knowledge*`, `_Knowledge`, `Devices`, `Workload`, `WebChat`, `MailIntake`,
  `NewTicket`, `_RequestQuote`, `RecurringEdit`), in `Program.cs` (a redirect after a photo post), and in **`wwwroot/chat.js`** (the
  billing pop-up and its notification open `/portal/tickets/' + a.id`) — grep `/portal/tickets/`, `/portal/clients/`, `/portal/kb/`.
- **The console opens portal pages through the hand-off** (`TicketsForm.cs`: `RequestPortalLink($"/portal/tickets/{id}")`, `?tab=photos`,
  `?open=quote-requested`, `/portal/kb/{id}`), and **`PortalHandoffService` only lets through addresses matching its allow-list**
  (digits only, spelled out whole). Consoles in the field send the OLD shapes and update themselves only on the next release (§148).

## Decided (Claude's recommendation; Clint asked for the brief — confirm only if something below turns out wrong)

1. **New address shapes that cannot be mistaken for the old**: `/portal/tickets/2` already means database id 2, so the number needs
   its own shape. Use **`/portal/t/{number}`**, **`/portal/c/{number}`** (client) and **`/portal/a/{number}`** (article),
   **`/portal/a/{number}/edit`**, with the same query strings as today (`?tab=photos`, `?open=quote-requested`, `?version=N`, …).
   Pick other shapes only if these collide with something; keep them short and the same pattern for all three.
2. **The old id addresses keep working — for that company's own people only**: `/portal/tickets/{id}` (and clients, kb) looks the
   id up WITHIN the signed-in company and **redirects** (302, query kept) to the new address; another company's id, or none, is the
   same 404 as a missing number — never a different answer that says "it exists elsewhere". Keep the old routes for good (cheap).
3. **Everything a portal page shows uses the number**, not only links: hidden form fields (the ticket page posts `id`), `asp-route`
   values, links written by `chat.js` (the server should send the number, or the finished link), the hand-off from the console.
   Handlers look the thing up by (company, number) — **one helper per kind**, e.g. `TicketService.ByNumberAsync(tenantId, number)`,
   and never trust a number without the company in the same query.
4. **Photos and article pictures**: their addresses are in the page source too (`/portal/photo/261`). Address a ticket photo by
   ticket number and its place on the ticket (e.g. `/portal/t/2/photo/3`), or by a random token stored on the row — the session's
   choice, whichever is less disruptive; the same for `/portal/kb/image/{id}`. The old photo addresses keep working for the company's
   own people, as above.
5. **The console**: change `TicketsForm` to send the new shapes (it has `Ticket.Number`), AND keep the old shapes in
   `PortalHandoffService`'s allow-list so a not-yet-updated console still lands (through the redirect). Add the new shapes to the
   allow-list spelled out whole, digits only, like the existing ones.
6. **Out of scope, on purpose — say so in the handover**: ids in the portal's background calls (`/api/portal/…` JSON) and in the
   console↔server protocol (`Shared/Protocol.cs`). Reading those takes developer tools or taking the console apart, and gives only a
   rough platform-wide count. Closing them too would mean ids that are never sequential at all — a much bigger change, not this one.
   Recurring tasks (`/portal/recurring/{which}`): check what `which` is; if it is a database id, give it the same treatment.

## How it must be proved (the project's rule: measure, and show the check fails without the fix)

A new harness, **`tools/addresscheck`**, on a throwaway platform with **two companies** (use `UseHarnessDb` / `HarnessDb.MigrateAsync`
/ `HarnessDb.Use` — never `UseSqlite` — and `tools/Common/TestImages.cs` for any photo). At least:
- company A's ticket #1, client #1 and article #1 open at the new addresses; company B's #1 are B's own, not A's;
- A's old id addresses redirect to the new ones, query kept; **B opening A's old id or A's number is a 404**, the same page as a
  number that does not exist;
- **every page a company opens contains no database id in a portal address or a hidden field** — render the main pages (dashboard,
  ticket list, a ticket in each layout, client page, knowledge base, an article) and assert no `/portal/tickets/{n}`,
  `/portal/clients/{n}`, `/portal/kb/{n}`, `/portal/photo/{n}` and no `name="id" value="{db id}"` remain (make A's numbers and ids
  differ by seeding B first, or the check proves nothing);
- a form posted from the new ticket page acts on the right ticket, and a forged number from another company is refused;
- the console hand-off: an OLD-shape link and a new-shape link both land on the ticket;
- the new numbers: a new client and a new article each get the company's next number; the backfill numbers existing rows 1, 2, 3 in
  id order per company. **Then take each guard out and show the harness fails** (the company in the lookup; the redirect's company
  check), and write the numbers down.
Then re-run everything that opens these pages — at least `ticketwindowprobe`, `kbcheck`, `ticketeditcheck`, `ticketstepcheck`,
`quotecheck`, `photogate`, `clientupdatecheck`, `mailintakecheck`, `handoffcheck`, `devicelink` — and finally the whole set:
`powershell -ExecutionPolicy Bypass -File tools\Run-DbHarnesses.ps1` (about 1½–2 hours; SQLite) and, because of the migration,
`tools\Run-DbHarnesses.ps1 -Postgres` for the harnesses that touch clients and articles. **Harnesses that read a page that may be a
404 must read the status, not `GetStringAsync`** (§158). Walk it in the built-in browser on a throwaway platform before finishing.

## When it is done

- HANDOVER **§159** (what it is, what building it found, how it is proved, deploy), a CLAUDE.md bullet in the same style as the
  others, and `TEST-3.6.md` **section DR, checks from 827** (never renumber). Update §158's F8 line to point at §159.
- Deploy: a **signed `deploy\Publish-Platform.ps1`** (YubiKey in) — the migration runs on start — and the console updates itself.
  Nothing to ship to customer machines.
- Commit with the files named (never `git add -A` — other sessions share this checkout).
