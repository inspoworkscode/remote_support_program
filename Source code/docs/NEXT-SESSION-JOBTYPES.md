# Job types: each company chooses its categories and stages

**Brief written 2026-10-27 from Clint's description and his answer to ten questions: *"lets go with your
suggestions"*. ALL FOUR STAGES ARE DONE THE SAME DAY — A (THE RULE AND THE SERVICE,
HANDOVER §166), B (THE PAGE, §167), C (THE CONSOLE, §168) AND D (THE PAPERS, end of §168) — none on hardware.** Read `HANDOVER.md` §44 (the workshop route), §46 (the story), §47
(call out), §48 (every category's route), §62 (photos) and §128 (going back) before starting. This work
touches all of them.

## The one-paragraph version

Every company gets the same six job types and the same stages today. Some companies don't want all of
them: no walk ins, no before-and-after photos, no supervisor approving *Do not invoice*, no tracking of
payment once an invoice has gone out. A new portal page, **Job types**, lets the owner or a supervisor
switch a whole category off, or switch off single steps inside the categories they keep. Switching off
**never breaks a route**: some things are locked (shown greyed, with the reason), and before anything is
saved one check walks every route and confirms that every stage still has a way forward and every job can
still reach Closed. A short line under each category shows how a job runs with the current choices, and it
updates as boxes are ticked.

## Clint's words

> I was thinking that there may be companies that wont want all the categories, or in some instances may
> not want all the stages of a category. For example one I.T company might not want to have the before
> and after photos. Would it be possible for the owner of a company, when setting up, can have the option
> to choose what categories they want, and also would it be possible to show them the stages of the ones
> they do want so that they can deselect stages they dont want without breaking the flow.

## The decisions, so nobody re-opens them

| Question | Answer, 2026-10-27 |
|---|---|
| Who may change it? | **The owner and supervisors** (`Permissions.CanSetJobTypes`, a question like every other). Office administrators and technicians see the page read-only. |
| When? | **At setting up, and any time after**, on Portal → *Job types*. Everything is **on** by default. See "When setting up" below for how the first visit works. |
| May every category be switched off? | **No. Remote support and General are always on.** Every remote session, call, email ticket, website chat, contact form, browser-support call and outside-help visit opens one of those two by itself, and a new ticket defaults to General. Workshop, Call out, Walk in and Recurring task may be switched off. |
| Jobs already under way when something is switched off? | **They finish the route they started on.** Each ticket keeps the company's switches **as they were when it was opened** (see "How it is stored"). Only new tickets follow the new route. Nothing is moved behind anybody's back. |
| Photos? | **On or off, separately, at each end**: *Photos when booked in* and *Photos when collected or delivered*. When on, the least is still three (`TicketPhotoService.Required`). No choosing the number. |
| Shortening invoicing? | **Yes**: *Track payment after invoicing* may be switched off, and *Client invoiced* then finishes the job. |
| Supervisor approval for *Do not invoice*? | **May be switched off**: *Do not invoice* then goes straight to the office to close, as the SLA button already does. The button itself stays. |
| Renaming stages, or adding a company's own? | **No, switching off only.** Renaming may come later if a company asks. Their own stages would undo "the route is defined once" (`Workflow.cs`). |
| Per company, per technician or per branch? | **Per company.** |
| How does the page look? | **One card per category**, its steps as tick boxes in route order, locked ones greyed with the reason, and under them **the line a job takes**, e.g. *Booked in → Repairable → To be invoiced → Awaiting collection → Closed*, which updates as boxes are ticked. |

## What can be switched off, and what cannot

There are three kinds of switch. **A skippable step**: with it off, the job goes straight past it. **One
choice among several**: with it off, its button is not offered and the other choices still are. **A
rule**: with it off, the thing it asked for is no longer asked.

Locked means always on. Each locked line says why on the page, in the words given here.

### Categories

| Switch | Kind | Notes |
|---|---|---|
| Workshop | category | Off also hides *Needs workshop work* (Call out), *Needs to be booked in* (Walk in) and everything under Workshop below. |
| Call out | category | |
| Walk in | category | |
| Recurring task | category | Off **stops the schedules opening tickets**. The schedules are kept, not deleted. Back on, the next one opens on its next due day, with no catching up on the days missed. The Recurring page and the *Recurring task* choice on New ticket are hidden while it is off. |
| Remote support | **locked** | *"Every remote session opens one of these by itself."* |
| General | **locked** | *"Emails, website chats and new tickets with nothing chosen land here."* |

### Workshop

| Step | Kind | With it off |
|---|---|---|
| Booked in | **locked** | *"Where every workshop job starts."* |
| Photos when booked in | rule | No photos asked at booking, at *Needs workshop work*, at *Needs to be booked in*, or when a ticket is changed to Workshop. Photos can still be added. |
| Add another machine | choice | Not offered at Booked in. |
| Inspection findings | skippable | Booked in offers *Repairable* / *Not repairable* straight away. |
| Repairable / Not repairable | **locked** | *"This is where the job decides how it ends."* |
| Solved — to be invoiced | **locked** | *"The way a repaired machine is billed."* |
| Damage report needed | choice | Not offered at Not repairable. |
| Invoice inspection fee | choice | Not offered at Not repairable, or after a rejected quote. |
| Do not invoice | **locked** | *"Every job needs a way to end without a bill."* |
| Waiting on customer | choice | Not offered on the bench. |
| Out for delivery | choice | Only *Machine collected* is offered when the machine is ready. |
| Machine collected | **locked** | *"A finished machine has to leave somehow."* |
| Photos when collected or delivered | rule | *Machine collected* and *Out for delivery* no longer ask for photos, and *Close* no longer waits for them. |

### Quotes (Workshop's Repairable, and General)

| Step | Kind | With it off |
|---|---|---|
| Quote needed | choice | The whole quote route is gone, in Workshop **and** General. A rejected quote's own steps go with it. |
| Ask suppliers for prices | skippable | *Quote needed* goes straight to *Quote sent to client*: the price comes from stock or a price list. The Suppliers page stays. |
| Payment before ordering parts | skippable | *Quote accepted* goes straight to *Parts ordered from supplier* (the label loses "Paid —"). **And then *Parts fitted* goes to *To be invoiced* instead of closing**, or the job would close without ever being billed. The route check below must catch exactly this. |

### Call out

| Step | Kind | With it off |
|---|---|---|
| Call out booked, Work done | **locked** | *"Somebody went out and did something: that is the call out."* |
| Reschedule | choice | Not offered. |
| Ticket completed — to be invoiced | **locked** | *"The way a call out is billed."* |
| Invoiced — paid on site | choice | Not offered. |
| Needs workshop work | choice | Not offered. Also gone whenever Workshop is off. |

### Walk in

| Step | Kind | With it off |
|---|---|---|
| At the counter, Repaired at counter | **locked** | |
| Needs to be booked in | choice | Not offered. Also gone whenever Workshop is off. |
| To be invoiced | **locked** | |
| Invoiced — paid at counter | choice | Not offered. |
| No invoice | **locked** | The walk in's *Do not invoice*. |

### Remote support

| Step | Kind | With it off |
|---|---|---|
| To be invoiced, Do not invoice | **locked** | |
| Do not invoice (SLA client) | choice | Not offered. |
| Link to parent ticket | choice | Not offered on the route. Linking from the ticket's own *Part of* box is unaffected. |

### General and Recurring task

*Done*, *To be invoiced* are **locked** in both. General's *Quote needed* follows the Quotes switch.

### For every category

| Step | Kind | With it off |
|---|---|---|
| Track payment after invoicing | skippable | *Client invoiced* finishes the job (Closed, or Awaiting collection for a machine still here) instead of going to *Invoiced — awaiting payment*. |
| Supervisor approval for Do not invoice | skippable | *Do not invoice* and *No invoice* go straight to *Do not invoice — ticket to be closed*. The reason is still asked. |
| Abandoned, Back | **locked** | |

Retired stages (*Work done*, *On the bench*) are never shown.

## The route check: why nothing can break

**One function, `JobTypes.Problem(off)`, is the only judge.** It is asked by the page before saving and by
the service on save. Starting from each category's first stage, it walks every button the switches leave,
following `@return`, `@decision`, `@paid` and `@close` as `TicketService.TargetOf` does, and refuses if any
of these happen:

- a reachable stage is left with no way forward;
- a category that is on cannot reach Closed;
- a job could reach Closed **without passing a billing stage or a deliberate no-bill step**. This is the
  *Payment before ordering* trap above;
- a locked switch is off, or a switch is off whose category is off. That one is harmless, but it is cleaned
  rather than stored.

A refusal says which category and which step, in words, and nothing is saved. With the locks above, no
combination should be refused. **The check exists so that a switch added later can't quietly make one
that is.**

**The harness proves it by brute force**: `tools/jobtypecheck` walks each category with **every**
combination of the switches it can reach (Call out: 2^14, because it can become a workshop job), 25,652
walks in all, through the real `Workflow.Actions` and the real target resolution. It also proves that no
switch outside a category's list changes anything that category reaches, so nothing is left untried. Then it proves the check refuses a route broken on purpose, e.g. *Payment before ordering* off
without the *Parts fitted* change.

## How it is stored

- **`Tenant.JobTypesOff`**: the keys switched **off**, comma-separated. Storing what is off means a
  switch added in a later version is **on** for every company without a migration touching their rows.
- **The *Payment before ordering* road (built in stage A):** with it off, *Quote accepted* goes to a new
  stage *Quote accepted — order the parts*, then *Order placed with supplier* to *Parts ordered from
  supplier*, then the usual *Parts received* and *Parts fitted*, which goes to *To be invoiced*. The old
  *Paid — parts ordered* stage is not used on this road, because it says "Paid".
- **`Ticket.RouteOff`**: a copy of the company's `JobTypesOff` **made when the ticket is opened**, and
  never changed afterwards. This is how a job finishes the route it started on. Empty means everything on,
  which is exactly what every ticket opened before this change had.
- **Categories are read live, steps from the ticket.** A ticket of a category since switched off carries
  on to its end. But nothing new may be opened in it, changed into it (*Edit*), or converted into it
  (*Needs workshop work* on a call out opened while Workshop was on is refused with words, not hidden
  without saying why).
- **`Tenant.JobTypesChosenUtc`**: when somebody last saved the page, and who (`JobTypesChosenBy`), shown on
  the page as *Last changed by Sue on 27 October*.
- Migration **`JobTypes`**, both databases (`--context PlatformDb` and `--context PgPlatformDb
  --output-dir Data/PgMigrations`); `pgcheck` fails otherwise. No backfill: empty is everything on.

## Where it is enforced

- **`Services/JobTypes.cs` holds every rule**: the list of switches (key, label, kind, category, locked
  reason, route order), `On(off, key)`, `Problem(off)`, and `Path(category, off)` for the line on the page.
- **`TicketService.ActionsFor`** leaves out a switched-off button, and **`ActAsync` refuses it** with words
  (a stale tab or an old console must not walk round it). Retargets (*Inspection findings* skipped, *Track
  payment* off, approval off, payment-before-ordering off) are resolved in **`TargetOf`**, reading
  `t.RouteOff`, so `ClosesJob` and the pop-up both UIs show agree with where the job really goes.
- The **photo gates**: `ActAsync`'s photos-in, `TicketService.Edit`'s change to Workshop, the booking in
  `CreateAsync`, `MarkCollectedAsync`, `SendForDeliveryAsync`, `TicketPhotoService.CheckCloseAsync` and the
  *Close* refusal. Each asks `JobTypes.On`. **`d.PhotosRequired` / `PhotosOutRequired` become 0 when off**,
  so the portal and console draw no "0 of 3".
- **Categories**: New ticket (portal and console), *Edit*, the Recurring page and `RecurringTaskService`'s
  06:00 run, and the two conversions.
- **The console**: `StateMsg` gains the categories that are on. The steps need nothing new, because the
  console already draws only the buttons the server sends. **`NewTicketDialog`'s own `PhotosRequired = 3`
  goes**; it asks the state. Additive protocol fields only, so an older console still works. It offers
  every category and the server refuses the switched-off ones in words. Consoles update themselves anyway.

## When setting up

Signup already ends at PayFast (full version) or straight into the portal (pilots), so another step is not
added in the middle of it. Instead, **the first time an owner opens the portal after this is deployed,
they land on *Job types* once**, with a line at the top: *"Everything is switched on. Untick what your
company doesn't use. You can change this any time under Job types."* There are two buttons: *Save* and
*Keep everything*. Either one sets `JobTypesChosenUtc`, and they are not sent there again. This covers new
companies and the ones already running (Anywhere IT and the pilots) alike.

## Not in this

- Free and Basic: they have no tickets. The page is not added to `FreePortal`'s allow-list.
- Renaming stages, a company's own stages, choosing how many photos.
- Per-technician or per-branch settings.
- Reports: a switched-off step simply never appears in new tickets' stories.

## Stages

- **A: the rule and the service.** `JobTypes.cs`, the two columns and the date, `ActionsFor` / `ActAsync`
  / `TargetOf` / the photo gates / the category gates, `RecurringTaskService`. Harnesses: `jobtypecheck`
  (every combination), `jobtypewire` (a real platform on both databases: a switched-off step refused, a
  ticket opened before a switch keeps its route, a category refused at New / Edit / conversion, photos
  not asked, Recurring stops and resumes without catching up).
- **B: the portal page.** Portal → *Job types* (owner and supervisors edit, others read), the cards, the
  greyed locks with their reasons, the line each job takes (from `JobTypes.Path` through a small
  `/api/portal/jobtypes/path`, so the browser never has its own copy of the routes), the first-visit
  landing. Walked in the browser.
- **C: the console.** Categories from the state, photos from the server. `ticketformprobe`,
  `ticketwindowprobe`.
- **D: the papers.** HANDOVER section, `TEST-3.6.md` section DY (next free check number after 896),
  CLAUDE.md, the handover pack and the generic guide's job-types pages.

**Deploy: a signed `Publish-Platform.ps1` (migration `JobTypes`, both databases); the console updates
itself. No client reinstall.**
