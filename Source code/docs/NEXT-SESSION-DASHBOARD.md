# The dashboard and the ticketing system

**Brief written 2026-09-20 from Clint's description and seven decisions he made the same day.**
Nothing here is built yet. Read `HANDOVER.md` §30 before adding any control to any window, and this
file before touching `TenantHub.AttachSessionToTicketAsync` or `TicketService.ActiveForDeviceAsync`
— both are deliberately wrong for what is wanted now, and the reason they look right is explained
below.

## The one-paragraph version

The console's home stops being a split screen full of columns nobody reads, and becomes two tabs —
Attended and Unattended — of machines listed under the person who owns them, with a green or red
dot for online. Clients grow *contacts*, and a machine hangs off either. Tickets leave the console
for the browser, where a technician can finally sign in as themselves; a technician sees only their
own work, the owner sees everything, and the office administrator can ask for an update but cannot
move a ticket's status. Workshop jobs demand three photos to book in and three to close. And every
remote session gets its own ticket number, because that is what "time and tracking" actually means.

## The decisions, so nobody re-opens them

| Question | Clint's answer, 2026-09-20 |
|---|---|
| Where does the device dashboard live? | **The console.** Its home screen is rebuilt. Tickets go to the browser. |
| Is supervisor a new role? | **No.** `TenantRole.Admin` is the owner/supervisor; `TenantRole.Billing` is the office administrator. No new role, no migration. |
| May one person hold several roles? | **Yes — eventually, and plan for it now.** Owner, Technician and Office administrator become tick boxes (3.6.8), because a one-man business is all three and "technician and office administrator but not owner" cannot be said today. **Stage D must therefore never compare a role.** It asks a question — may this person see all tickets, change a status, assign one — and 3.6.8 changes the answer's storage without touching a page. |
| Does a repeat support request open a new ticket? | **Yes, outside 30 minutes.** Inside 30 minutes of the device's last ticket it reuses, which catches a dropped connection and a double-click and nothing else. |
| Who gets a new ticket? | **An automatic ticket goes to whoever caused it** — the technician who accepted the call or started the session. It is never unassigned. |
| Who may open a ticket by hand? | **All three roles.** In most cases it is the office administrator who opens a ticket and then assigns it to the right technician. |
| May a hand-opened ticket be saved with nobody on it? | **Yes**, and it goes into a pool **every role can see**, including technicians, who may claim from it. This is the one hole in "a technician sees only their own", and it is deliberate: work nobody has been given must not be invisible to the people who could do it. |
| May the office administrator reassign? | **Yes, at any time.** Assignment is theirs; status is not. They control who does the job, the technician controls where it has got to. |
| What about work that carries over past 30 minutes? | **A ticket may say it is part of another one.** One parent, many children. The parent shows every ticket under it and totals their time, so a three-day job is one readable, billable thing made of the sessions that did it — and each session keeps its own number. |
| Where is a workshop job booked in? | **Both** the console dialog and the web form. One upload endpoint, two callers. |
| What does a machine link to? | **Either** a client or a contact. Contact where it is one person's PC, client where it is a server or a shared machine. |
| What does a technician see? | **Their own tickets only.** Enforced on the server, not offered as a filter. |

## Stage A — contacts

`Client` today carries one inline contact (`ContactName`, `Phone`, `Email`, `PlatformDb.cs:203`)
and `Device.ClientId` is the only link there is. `Ticket.ContactName` is free text.

- New `Contact`: `Id, TenantId, ClientId, Name, Phone, Email, Notes, Archived, CreatedUtc`.
- `Device.ContactId` (nullable) alongside the existing `Device.ClientId`. A device may carry a
  contact, a client, or neither — never a contact belonging to a different client.
- `Ticket.ContactId` (nullable). `Ticket.ContactName` **stays** and keeps being written: a ticket is
  the record of a job, and the person's name at the time is part of it. Same argument as
  `HealthCheckRun.ClientId`, which is copied rather than joined.
- Migration turns each client's existing inline contact into that client's first `Contact` where
  the name is non-empty. Do not drop the inline columns in the same migration — one migration, one
  job, and the portal still reads them.
- Portal: create a contact and link it to a client; link a machine to either.

## Stage B — the console's home screen

`DashboardForm.BuildUi` (`src/RemoteSupport.Technician/DashboardForm.cs:139`) builds one flat
`ListView` with eight columns, two of which restate what a tab would say.

- Two tabs: **Attended** and **Unattended**. Unattended = `UnattendedEnabled` ticked (the switch
  from §31, not "the agent happens to be connected"). Attended = everything else.
- First column is the owner: contact name, else client name, else `Device.UserName`. A machine
  nobody has linked still says who is sitting at it.
- Machine second. A green/red dot replaces the `Status` column; the `Access` column goes.
- A client with more than one machine collapses into one expandable row. **`ListView` will not do
  this** — its groups collapse but it has no two-level tree. Either an owner-drawn tree-list or a
  `TreeView` with columns; decide by building the smaller of the two, not by arguing about them.
- `GridLines = false`. The vertical rules are what make dragging a column edge feel like it is
  about to do something else.
- §30's rule applies to every new control here: **do not write a control height as a number —
  measure it from its font.**

## Stage C — the portal learns who a person is

**Technicians cannot sign in to the portal at all today.** `Login.cshtml.cs:29` rejects them, and
`PortalPage` (`Pages/Portal/PortalPage.cs`) resolves a *tenant* from the cookie and nothing else —
every portal page is company-wide by construction. This is the prerequisite for everything in
stage D, and it is the part most likely to be underestimated.

- Let technicians sign in. Add a `technicianId` claim and a role claim carrying `TenantRole`.
- `PortalPage` exposes the signed-in person, not just the tenant.
- A hand-off token so the console's Tickets button opens a browser already signed in as the
  technician who is signed into the console: single-use, short-lived, issued over the existing
  authenticated websocket, redeemed at a portal URL. It must not be a bearer token that lands in a
  browser history as a reusable login.

## Stage D — tickets, in the browser, with roles that mean something

`TicketService.ListAsync` (`Services/TicketService.cs:98`) takes `me` and a `filter`, and "mine" is
a filter the technician *chooses*. That is not a permission boundary.

**Ask a question, never compare a role.** Because of 3.6.8 a person will hold a SET of roles, so
`MyRole == TenantRole.Admin` is a comparison with a short life. Pages and services ask
`CanSeeAllTickets`, `CanChangeStatus`, `CanAssign`, `CanOpenTicket` — four questions whose answers
move from a single enum to a set of tick boxes without a page being rewritten. `TicketService.VisibleTo`
already takes the role rather than reading it, which is the same idea and is why it will survive.

- Server-side scope, applied before any filter: `Technician` → `AssignedTo == me || AssignedTo == ""`;
  `Admin` → all; `Billing` → all. **The empty-string half is not an oversight** — an unassigned
  ticket is in the pool and every role can see the pool, or work nobody has been handed becomes
  work nobody knows about. A technician still cannot see a ticket that is assigned to a colleague,
  and claiming from the pool is an assignment they are allowed to make *to themselves only*.
- **Creating a ticket is not restricted by role.** Technician, Admin and Billing may all open one;
  in practice the office administrator opens most of them. An assignee is optional at creation.
- The office administrator may open tickets, **assign and reassign them at any time**, and add
  notes. They may not change status or priority. Enforce every line of that in
  `TicketService.UpdateAsync`, not by hiding the dropdown — a hidden control is not a rule.
- A per-technician view for Admin and Billing: every technician, their open tickets, their load,
  and the unassigned pool alongside them so it is obvious when something is sitting in it.
- The console's `TicketsForm` stops being the ticket UI. Whether it is deleted or left as a
  read-only fallback is a decision for when the browser side actually works.

## Stage E — workshop photos

Nothing in the schema stores a file against a ticket.

- `TicketPhoto`: `Id, TenantId, TicketId, Kind (Before|After), FileName, UploadedBy, UploadedUtc`.
  Files under the tenant's own folder, the way `data/healthchecks` already works.
- One HTTP upload endpoint, called by both the console's `NewTicketDialog` and the web form.
- **Three before photos to create a Workshop ticket. Three after photos before its status may
  become Resolved or Closed.** Both gates on the server; the client-side check is a courtesy.
- Only `TicketCategory.Workshop`. Every other category is unaffected.

## Stage F — one session, one ticket

This is the sharpest break with the code as it stands, and both of the functions it breaks were
written on purpose.

`AttachSessionToTicketAsync` (`Hub/TenantHub.cs:912`) calls `ActiveForDeviceAsync` and *reuses* the
device's newest open ticket. `OpenTicketForCallAsync` (`Hub/TenantHub.cs:939`) finds an open ticket
and adds a note saying "Customer requested support again" rather than opening a second one. Both
were right when a ticket meant "this machine has an open problem". They are wrong now that a ticket
means "this was a piece of work somebody did".

- Every session gets its own ticket and its own number. A hundred sessions to one machine in a day
  is a hundred tickets.
- The only exception is the 30-minute window above, measured from the device's last ticket's
  `CreatedUtc`.
- `ActiveForDeviceAsync` stops being the thing that decides. Leave it if something else reads it;
  do not let it keep deciding.
- **A technician starting an unattended session is asked why first**, and the answer becomes the
  ticket's `Description` — exactly as a customer's own reason already does since 3.5.7
  (`callprobe`, HANDOVER §30). No reason, no session. The prompt is in the console, the ticket is
  made on the server, so the reason has to travel *with* the session-start message rather than
  arrive after it.
- The ticket queue must group by client and machine, or a hundred tickets a day is unreadable and
  the whole change looks like a mistake.

**Linking tickets.** *Clint, 2026-09-20, while stage A was going in.* The 30-minute window is a
guess about one sitting; real work carries over a lunch break and a night. So a ticket may say it
is **part of** another one:

- `Ticket.ParentTicketId`, nullable. One parent, many children, **one level** — a child may not
  itself be a parent. Two levels would mean a tree, and a tree means a technician deciding where in
  it something belongs, which is a question nobody wants at the end of a job.
- The parent page lists its children, and totals their session time. That total is the job's time,
  and it is the number that gets billed.
- A child keeps its own number, its own description and its own time. That is the whole point: the
  session record is not destroyed by being filed under something.
- Setting the parent is one control on the ticket — "part of #…" — offered with the same machine's
  recent tickets already in the list, because that is what it almost always is.
- Closing a parent does **not** close its children, and a parent with an open child cannot be
  closed. Otherwise "the job is finished" and "somebody is still working on it" can both be true.

## What to prove, and how

The project's rule is measure first. Each stage gets a harness in `tools/`, the way `callprobe`,
`unattendedcheck` and `toolbarprobe` did:

- **`contactcheck`** — a machine linked to a contact, to a client, to neither; the first column says
  the right thing in all three; the migration turns an existing inline contact into a Contact
  exactly once.
- **`dashtabs`** — a device with the unattended switch off never appears on the Unattended tab and
  does appear on Attended; the dot follows online state; one client with three machines collapses
  to one row and expands to three.
- **`ticketscope`** — a technician cannot read a *colleague's* ticket **through the API**, not
  merely through the page; a technician CAN see an unassigned one and claim it; a technician
  cannot claim one to somebody else; Billing can open, assign, reassign and note, and cannot
  change status or priority; Admin can do all of it.
- **`photogate`** — a Workshop ticket cannot be created with two photos and can with three; cannot
  be closed with two after photos and can with three; a Remote Support ticket is unaffected.
- **`ticketpersession`** — two sessions 31 minutes apart produce two ticket numbers; two sessions
  two minutes apart produce one; an unattended session with no reason given never starts; a child
  ticket cannot be given a child of its own; a parent with an open child cannot be closed; and the
  parent's time total is the sum of its children's sessions and its own.

## Ship notes

Stage B changes the CONSOLE only → `ship-build.ps1 -OnlyTechnician`. Stages A, C, D, E and F change
the SERVER, and A and E need migrations. Nothing here changes the CLIENT, so no customer reinstall
is needed — check that again before shipping, because 3.5.5 got it the other way round and cost a
rebuild.
