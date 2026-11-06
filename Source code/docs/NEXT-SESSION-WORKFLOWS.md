# Workflows per category

**Stage 3.7. Opened 2026-09-20. WORKSHOP IS BUILT (2026-09-24) — HANDOVER §44 — and not yet on
hardware. General, Remote support, Walk in and Call out are still Clint's to write, and this file
is where they go.** Both surfaces are built: the portal (§44) and the console's own ticket window
(§45), drawing the same server answer.

Read `ROADMAP-PARITY.md` stage 3.7 for the one-paragraph version and `HANDOVER.md` §32–§36 for what
stage 3.6 built, which this sits on top of.

## What Clint asked for

2026-09-20, after opening a workshop ticket and finding it did not ask for photos (the server was a
build behind — the gate was there):

> *"What I'm going to do is create workflows for each category, then give those to you, so that each
> ticket in each category follows certain routes and automatically assigns accordingly."*

Each category — **General, Remote support, Walk in, Workshop, Call out** — gets a defined
route: the stages a job moves through, who may move it between them, what must be true before it
can leave each stage, and who it lands with in the first place.

**There are FIVE, not four, since 2026-09-21.** General was added and is now the default a ticket
gets when nobody picks anything (HANDOVER §42). It was not part of Clint's sentence above, which
was written when there were four, so its workflow is the one still entirely unspoken.

**SIX since 2026-10-07: Recurring task** (HANDOVER §108), a schedule that opens a ticket on each day it is
due. Clint's words and answers are under "Recurring task" below.

## Why this fits rather than fights what is there

- **Categories are already a real distinction.** `TicketCategory` has existed since 2026-09-08 and
  each already shows its own fields on the booking form.
- **Workshop already has a workflow rule in everything but name.** Three photos in, three out,
  enforced in `TicketPhotoService` and wired into `TicketService.UpdateAsync` — the single place
  every status change passes through. That is exactly the shape of "what must be true before this
  stage can be left", and it is the pattern to generalise.
- **Assignment is already one question.** `Permissions.CanAssignToAnyone`, and auto-assign-on-the-
  spot is already the rule for sessions (§34). Routing rules sit beside that rather than replacing
  it.
- **`UpdateAsync` is already the single gate.** Anything a workflow forbids gets refused there, for
  the browser and the console at once, and `LastBlockedReason` already carries the explanation back
  to whoever tried. Do not add a second enforcement point.

## THE FORK, and it is much cheaper to settle before building

**Are these four workflows built into the product, or something each reseller configures in their
own portal?**

A configurable engine is a large build and cuts straight across Clint's standing rule that the
product be as simple and user friendly as possible — every IT company would meet a rules screen
before they could open a ticket. Four well-chosen built-in workflows cost a fraction, cannot be
misconfigured, and are the same four in every company that does this work.

**Recommendation: built in**, with per-tenant knobs limited to the handful that genuinely differ
between companies. Settle it when the four below are written; what they say may decide it.

## Where a technician works a ticket — REVERSED 2026-09-20

Stage 3.6.3 moved the ticket queue to the browser because the console's ticket window had terrible
padding and spacing. Once the New ticket dialog was rebuilt (§37), Clint looked again:

> *"Creating tickets in the console really looks good now, and makes me rethink a previous decision.
> Maybe technician tickets should rather sit in their console, especially since we'll be building
> workflows. Only the Supervisor/Owner really then needs the browser."*

**Agreed, and it is cheap**: the hub already carries every ticket message — list, open, create, update,
note — and all of them have been scoped and role-checked since 3.6.4. The console can already do
tickets over its socket. `TicketsForm` still builds.

| | Console | Browser |
|---|---|---|
| Technician | their queue, their tickets, workflows | kept as a second way in |
| Office administrator | **no access at all** (`CanUseConsole` is Technician or Admin) | opening, assigning, chasing |
| Owner | optional | everything, workload board, reporting |

**This does not reduce the UI work, it redistributes it.** The office administrator opens most
tickets and cannot use the console, so the ticket UI has to exist in both places whatever happens.
What the split buys is that each side only has to do what its own audience does.

**Two decisions, 2026-09-20:**

- **The browser stays available to technicians.** The Tickets button and the one-use hand-off link
  are kept — a long history or a big description is easier to read in a browser, and it is already
  built.
- **`TicketsForm` is rebuilt WHEN THE WORKFLOWS ARE WRITTEN, not before.** What a technician's
  ticket view needs — stage buttons, photo panels, checklists — is what the workflows decide.
  Building it first risks building it twice. It stays unreachable until then and the browser covers
  the gap. When it is rebuilt it gets the §37 treatment: measured from fonts, two columns, flow
  layout, and a probe.

### The condition on all of it

**A workflow is defined ONCE, on the server, and both surfaces render from that definition.** Not
"the console knows the workshop stages and the portal also knows them." Stage 3.6.4 already found
the hub standing open as a second door around every role rule (§34), and a workflow is far more
intricate than a role. Two copies WILL drift, and the symptom is a ticket a technician cannot move
and nobody can explain.

Concretely: the server owns the stages, the transitions and the gates; each UI asks what this ticket
may do next and draws buttons for the answer. Adding a stage later is then a server change, not two.

## What each workflow needs to say

Plain prose per category. No diagram, no format.

1. **Who it starts with.** A named person, whoever booked it, round robin, or the unassigned pool.
2. **The stages it moves through, in order, and who may move it between them.** If a category needs
   stages beyond Open / In progress / On hold / Resolved / Closed, say so — **this is the one answer
   that changes the data model** rather than only the rules, because `TicketStatus` is a single enum
   shared by every category today.
3. **What must be true before it can leave each stage.** Photos, a signature, a part ordered, a
   price agreed. Workshop's three-in-three-out is the worked example.
4. **What is required to open it at all** — which fields, and which are optional.
5. **What happens automatically** — a note written, somebody notified, the customer emailed.

## The five, as Clint writes them

*Empty on purpose. Paste each one in here as it is written, then build from this file rather than
from a chat message.*

**All five are BUILT as of 2026-09-27** — Workshop and Call out below, and General, Remote support
and Walk in here, from one message of Clint's (HANDOVER §48). Its opening and closing words, which
apply to all of them: *"Options for comments just like the workshop workflow should always be
there in all workflows"* and *"there should always be the option to link to clients at any
stage."* Both are built: the comment box is on every workflow ticket, and every ticket can be
linked to a client, or changed, from Details — in the portal and the console.

**His four answers, the same day:** plain Do not invoice needs a supervisor but **Do not invoice
(SLA client) goes straight to the office**; a remote ticket **linked to a parent closes**; General's
three choices are **buttons after the ticket is created**; and a General ticket's **Done closes it**.

### General

> When creating General Tickets, there should be no need to link to a client as this could just be
> for general internal instructions, but there should be three options, general ticket, quote needed
> and To be invoiced. General ticket would just be an instruction to someone internally, where quote
> needed and to be invoiced would follow the usual routes, but without linking to any clients,
> although there should always be the option to link to clients at any stage.

**The route.** Open [T] → **Done** (closes — nothing to bill, the office need not see it) · **Quote
needed** (the workshop's quote route, with or without a client; items are added in the step) ·
**To be invoiced** (what was done, time, amount → the usual invoicing route, closing on payment). A
rejected General quote offers only Do not invoice — there is no machine to charge an inspection for.
No client is required to open one, in the portal's form or the console's.

**Opened by a customer, 2026-10-05 (roadmap 3.8.4, HANDOVER §79).** An email to the company's support
address, or its website's contact form, opens a General ticket on this same route — linked to the
client when the sender's address is on file; unlinked and SAYING so (with one button to add them as
a contact) when only their domain is known; unlinked when they are a stranger. Clint's three answers
are in the roadmap.

### Remote support

> Even though remote support creates it's own tickets immediately, it should also have it's own
> workflow. After the session is completed, it should have To be Invoiced, Do not Invoice (SLA
> Client), Do not Invoice or link to Parent Ticket, with the option to link it to another ticket
> that is currently open under that specific client.

**The route.** Remote session [T] → **To be invoiced** (what was done, amount — the time is the
sessions') · **Do not invoice (SLA client)** (nothing asked; to the office to close) · **Do not
invoice** (why; a supervisor approves) · **Link to parent ticket** (one of the same client's open
tickets; filed under it, and closed). The options are there from the start; a new session within
the 30-minute window joins the ticket **only while nobody has chosen one**.

### Walk in

> Once the details are captured, there should be two options, Repaired at Counter or Needs to be
> booked in. If repaired at counter is selected, Work done and Time spent need to appear. After
> that, it's To be Invoiced, Invoiced Paid at Counter, or No Invoice. To be invoiced follows the
> usual route, Invoiced at Counter gives two options. Paid Cash with the amount, or Paid Card Machine
> with the amount and card machine reference number. No Invoice, asks for reason why just like the
> workshop workflow. Needs to be booked in, follows the Workshop Route.

**The route.** At the counter [T] → **Repaired at counter** (work done, time) → To be invoiced
(amount → the usual route, closing on payment) · Invoiced — paid at counter (→ Paid cash / Paid card
machine → the office closes it) · No invoice (why; a supervisor approves) — **or** → **Needs to be
booked in** (three photos in and the booking's questions; the same ticket becomes a Workshop job at
Booked in).

### Workshop

*Partly decided already: three photos of the machine as it arrived to book it in, three of the
finished machine before it can be resolved or closed. Built and proven — `photogate` 36 of 36, §35.
Whatever is written here must keep that or say plainly that it is being changed.*

**Clint's first draft, 2026-09-24, verbatim. The questions it raised are still open; the answers
go below this, and building waits on them. This one is perfected before any other is written.**

> We will need to add a billing category to our list. To be invoiced and Quote needed could be two
> separate new ticket items, that this workflow and the others could simply trigger when necessary.
>
> **Capture Hardware Details.** Captured at book-in: make, model, serial, accessories, and whether
> the device is password protected — the password itself is encrypted, never stored in plain text.
> Upload photos, at least three.
>
> Then there should be an area for **Work Done**.
>
> Then it should show two options: **Repairable / Not repairable.**
>
> Then four options: **Solved to be Invoiced, Not Repairable Invoice Inspection Fee, Quote Needed,
> Do Not Invoice.**
>
> **Quote needed.** Select Item (a drop down as more items are added) or Add Item. When adding an
> item it must use predictive text based on what is in the item list — one technician might start
> with "DDR" for memory and another might say "memory DDR"; it must recognise each to prevent
> duplicates. A field for part number and an option to upload a photo. Auto assign to the Office
> Administrator with the title Quote needed and move to the billing category. The office
> administrator then has a button to request a quote, which opens a new mail with a template
> requesting the quote using the item and part number, attaching the photo if one was supplied.
> Status becomes **Quote requested, awaiting feedback.** Once the quote is received the office
> admin sends it to the client: **Quote sent, awaiting client feedback**, with fields for quote
> amount and quote reference from the invoicing system. Next stage has three buttons: **Quote
> accepted, Quote rejected, Re-quote.** Re-quote goes back to Quote needed. Accepted goes to
> **Invoice client → Awaiting payment → Payment received, order placed with supplier → Parts
> received, notified customer → Parts fitted → Ticket complete.**
>
> **Solved to be invoiced.** Before it is re-assigned the technician must enter time spent and the
> amount to be invoiced. It is then re-assigned to the office administrator under the billing
> category with the description To Be Invoiced. Next: **Client invoiced** (fields: invoice amount,
> invoice number from their accounting package) **→ Awaiting payment → Payment received, ticket
> closed.**
>
> **Not repairable, invoice inspection fee** follows the same route as Solved to be invoiced.
>
> **Do not invoice.** The technician must give the reason, then it is assigned to the office
> administrator in the billing category with the header Do not invoice, ticket to be closed.
>
> **Only the Office Administrator can close tickets.**
>
> **Comments at every stage**, as many as necessary, so it reads like a story. **@user** in a
> comment flags that person to add to the comments in that section.
>
> **Three more photos before any ticket can be closed**, ideally taken in front of the customer
> before the machine leaves the workshop.

#### Decided 2026-09-24 — the recommendations Clint accepted

- **One ticket, one job, one story.** The ticket stays Workshop from book-in to close. "Billing" is
  NOT a sixth `TicketCategory` and NOT a separate ticket: it is a set of **billing stages** owned by
  the office administrator, and the office administrator's **Billing** view is every ticket, of any
  category, currently sitting in one. Call out and Walk in will hand off to the same billing stages
  later — that is the reuse Clint wanted from "separate ticket items", without two half-stories or
  a second level of `ParentTicketId`.
- **A ticket has a STAGE**, defined per category on the server. `TicketStatus` stays as the
  summary every existing list understands (open / waiting / closed), derived from the stage. This
  is the data-model change the traps section warned about.
- **Request quote downloads a draft email file (.eml)** with the photos attached, which opens in
  the office administrator's mail program. **3b, by Clint's choice.** Sending from the server
  through the company's own SMTP (3a) is a future release.
- **The three out-photos are taken at COLLECTION**, not at close. Close requires the machine to
  have been collected (or the job abandoned).
- **Duplicate items are suggested, not blocked.** Matching ignores word order and case ("memory
  DDR" finds "DDR memory"); a new item that looks close to an existing one asks "Did you mean …?"
  and can still be added. "DDR4 8GB" and "DDR4 16GB" are genuinely different parts.

#### Clint's answers, 2026-09-24

1. **Anyone can book a machine in, and names the technician at booking.** That technician can
   reassign it to someone else. *(Today a technician cannot assign to anyone — this changes
   `CanAssignToAnyone` for book-in and adds "reassign my own".)*
2. **Awaiting diagnosis → On the bench**, with **Waiting on customer** available as a side stage.
3. **Work done is entries over time, internal only.** They get copied into the invoice by hand.
4. **Repairable** → Solved to be invoiced, Quote needed, Do not invoice. **Not repairable** →
   Invoice inspection fee, Do not invoice.
5. **A quote can have several items**, each with its own part number and optional photo.
6. **The item list is shared by the whole company**; a supervisor or the office administrator may
   edit and merge it. **And: Owner and Supervisor become separate roles** — see below.
7. **The office administrator types the supplier's address.** A supplier list is a future release.
8. **Quote rejected goes back to the technician**, offering only Invoice inspection fee or Do not
   invoice.
9. **Quotes include labour.** Nothing is invoiced separately after Parts fitted.
10. Payment received / order placed, Parts received and Close are the **office administrator's**;
    **Parts fitted goes automatically back to the technician who did the work.**
11. **"Notified customer" is just a button.** Customer updates are a future release.
12. **Time spent and the amount are typed in.** No rates.
13. **Account customers stay on Awaiting payment** — no early close.
14. **Do not invoice needs a supervisor's approval.**
15. **Office administrator = the existing Billing / reception role.** Owner and supervisors can also
    close tickets and move billing stages.
16. **A shared billing queue** that everyone with the office administrator role sees; when a
    company has none, it falls to the supervisors and owner.
17. **One timeline per ticket**, each comment stamped with the stage it was written in and grouped
    under stage headings.
18. **@mentions are staff only; a badge in the console, and it stays until they reply on that
    ticket.** *(The office administrator cannot use the console, so their badge has to be in the
    portal.)*
19. **Nothing is shown or sent to the customer yet.** Future release.
20. **An Abandoned end, not enforced.** Anywhere offers three months' grace; other companies differ,
    so no timer.
21. **Workshop tickets open today start at Awaiting diagnosis / On the bench** when this goes live.

#### Owner and Supervisor, separated (answer 6) — a prerequisite

**One Owner per company; any number of Supervisors.** The Owner does not use a seat unless they are
also a supervisor. This is roadmap 3.6.8 (roles as tick boxes) arriving early: the workflow needs
"supervisor" to exist before it can ask one to approve a Do not invoice. Build it first, as its own
step, through `Permissions` — never a role comparison.

#### Second round, 2026-09-24

- **Mail.** Anywhere uses desktop Outlook, but other companies use Gmail or Outlook on the web,
  which cannot open a draft file. So Request quote asks ONCE, per person, which mail they use:
  desktop mail gets the .eml with the photos attached; Gmail and Outlook on the web get their
  compose window opened with the address, subject and body filled in, and the photos downloaded
  beside it to attach by hand. The answer is remembered and can be changed.
- **A machine may be collected before it is paid for.** Not enforced either way.
- **Seats: 1 supervisor + 1 office administrator + 5 technicians; the Owner is free on top**
  unless they also tick Supervisor, which then takes the supervisor seat.
- **Existing Administrator accounts:** the person who created the company becomes Owner +
  Supervisor; every other administrator becomes a Supervisor.
- **The assumptions below were accepted as drafted.** Re-quote keeps the items with the office
  administrator; Invoice client asks for amount and invoice number; the part number is per quote
  line, pre-filled from the item's last use; Collected is OA or T once the bench is done;
  Abandoned is OA from anywhere with a reason and no photos; book-in cannot be saved without a
  technician.

#### Third round, 2026-09-27 — the order changes, and so does the screen

Clint, after using it: *"it needs to be a step by step workflow, not everything on one screen as it
looks confusing"*, then, with screenshots of the system Anywhere uses today: *"you'll see the stage
transitions at the top with always a place to post comments through every stage, and then one can
scroll through it, reads like a story."*

- **The screen:** the stage at the top (Workshop › previous › **current**), and under it ONLY the
  buttons for the next step. Pressing one puts that step's form into the timeline; Submit moves the
  job on. "Post a comment" is always first under the tabs. One numbered timeline, newest first, that
  reads as the story. Photos, Quote and Details are tabs. Same in the portal and the console.
- **The order:** *"Lets use something like Inspection Findings before Repairable, and then work done
  after repairable."* So: **Booked in → Inspection findings → Repairable / Not repairable → Work
  done → Solved to be invoiced / Quote needed / Do not invoice.** Not repairable goes straight to
  Invoice inspection fee / Do not invoice. Everything after the outcome is unchanged.
- **Items:** the Item box has an **Add** button beside it, for when what is typed is not offered.
- **BUILT 2026-09-27, portal and console — HANDOVER §46.** Where the map below still says "On the
  bench" or "Work done entries at any bench stage", §46 wins.

#### Fourth round, 2026-09-30 — delivery, and several machines at once

Clint, verbatim:

> Something that happens often, is a machine might be booked in at our offices, but instead of it
> being collected, it needs to be delivered so this needs to be added to the workflow after the
> work is completed and then pretty much follow the call out process. Another thing that happens is
> sometimes a client may bring in more that one machine to the office at the same time, so at the
> beginning of the workflow, there must be an option to add machine with it's fault description and
> ask for the same details as the original.

**His answers, the same day:**

- **Delivery.** (a) **Both**: sometimes paid before delivery, sometimes on delivery. (b) **Always a
  technician** delivers. (c) Delivery is normally because something must be set up there — *"a
  printer needs to be installed or something else, so work done needs to be there."* (d) **The
  photos of the machine going out are taken at the office**, before it leaves.
- **Several machines.** **One ticket per machine**, booked together and linked, each with its own
  fault, the booking's questions and its own photos. (a) **Both**: sometimes the client pays for
  all of them at once, sometimes separately. (b) **Both**: sometimes they leave together, sometimes
  separately — *"and there's always a chance of one or two of them being abandoned."*

**The delivery route.** Wherever *Machine collected* is offered (the bench is finished and the
machine
has not gone), **Out for delivery** is offered beside it. It needs the photos out (at least three,
taken at the office) and lands on the bench with the technician who did the work — anyone can then
put someone else on it. Then, as the call out does it: **Delivered — work done** [T; what was done
there, time spent]; the machine counts as gone. Then one of: **To be invoiced** [T; amount for the
delivery's work — added to the invoice if the office has not sent it yet, a new one if it has] ·
**Invoiced — paid on delivery** → Paid cash / Paid card machine → the office closes it · **Nothing
more to invoice** → back to wherever the billing was, which then carries on and closes. **Not
delivered** [why] brings it back to the office unchanged. To be invoiced is not offered while an
invoice is already out and unpaid — the customer pays it on delivery or it stays with the office.

**Several machines: the route.** The booking form has **Add another machine**: each extra one asks
its own fault and the same booking questions and photos, and becomes its own ticket with the same
client, contact and technician. The tickets of one booking are linked **as equals** (none is the
parent), each ticket lists the others, and each moves through the workshop on its own. Paying
together and leaving together are the office's to do; nothing waits for its siblings, because any
of them may be abandoned.

#### Fifth round, 2026-10-02 — two machines on ONE job

Clint, verbatim:

> Lets confirm how a workshop ticket works when theres more than one machine. An example of when we
> get two machines for one ticket would be when we need to do a data transfer. the client would bring
> in their old and new machines which needs to be booked in together under one ticket

Asked to choose, he took **both kinds**: separate jobs stay a ticket each (above), and a machine
that is part of the SAME job goes on that ticket. **Add another machine** asks first which it is —
one control, not two buttons. On the same job: the booking's questions and three photos of that
machine, but no fault of its own (the job's is written; the box asks what the machine is for, and
may be left empty); one route, one invoice; the machines leave together, with **three photos out of
each**. HANDOVER §78.

#### The map — APPROVED 2026-09-24

Build order agreed: (1) Owner/Supervisor roles, (2) stages and gates in the service with a
harness, (3) item list and quote lines, (4) timeline, @mentions and badges, (5) the portal and
console screens. One commit per step.

Owner of each stage in brackets. **T** technician, **OA** office administrator (or supervisor or
owner), **S** supervisor (or owner).

**Bench.** Booked in [anyone; make, model, serial, accessories, password, 3 photos in, technician
named] → Awaiting diagnosis [T] → On the bench [T], with Waiting on customer [T] as a side stage
that returns to where it came from. Work done entries at any bench stage. Leaving the bench needs
Repairable or Not repairable, then one of the outcomes in answer 4.

**Solved to be invoiced / Invoice inspection fee.** Needs time spent and amount → To be invoiced
[OA] → Client invoiced [OA; invoice amount and invoice number] → Awaiting payment [OA] → Payment
received → Closed.

**Quote needed.** Needs at least one item line (item, part number, optional photo) → Quote needed
[OA; Request quote button downloads the .eml] → Quote requested, awaiting feedback [OA] → Quote
sent, awaiting client [OA; quote amount and quote reference] → Accepted / Rejected / Re-quote.
Re-quote → Quote needed, items kept and editable. Rejected → back to the technician's decision,
limited to Invoice inspection fee or Do not invoice. Accepted → Invoice client [OA; invoice amount
and number] → Awaiting payment [OA] → Payment received, order placed [OA] → Parts received,
customer notified [OA] → Parts fitted [T, the technician who did the work] → Closed.

**Do not invoice.** Needs a reason → Awaiting supervisor approval [S] → approved: Do not invoice,
to be closed [OA] → Closed. Declined: back to the technician's decision.

**Everywhere.** Collected [OA or T; 3 photos out] is a button available once the bench work is
finished. Closed [OA] requires Collected or Abandoned. Abandoned [OA] from any open stage, with a
reason and no photos. Every stage change writes a system entry to the timeline.

### Call out

**BUILT 2026-09-27 — HANDOVER §47.** Clint, verbatim:

> Now lets work on the call out workflow, once the ticket is created, the next stage should be work
> done. After work done, it should have the options, Ticket Completed to be invoiced, Invoiced Paid
> Onsite, or Needs Workshop Work. To be invoiced would follow the same route as the workshop to be
> invoiced. If Invoiced Paid on Site is selected, it should have two options. Paid Cash or Paid Card
> Machine. If Paid Cash is selected, it must ask for the amount. If Paid Card Machine is selected,
> it must ask for the amount and Card Machine Reference Number. If Needs Workshop Work is selected,
> it must follow the workshop workflow.

**His three answers, the same day:**

1. **Needs workshop work keeps the SAME ticket.** It becomes a Workshop job at Booked in, keeping
   its number and its story, after the questions a workshop booking asks (make, model, serial,
   accessories, password — optional, as at the counter) and the three photos in (required).
2. **After Paid cash or Paid card machine, the office closes it** — "Paid on site — to be closed" in
   the billing queue. Only the office closes tickets, as in the workshop.
3. **Work done asks the work AND the time**, so every call out has its time whichever way it ends.
   "Ticket completed — to be invoiced" then asks only the amount.

**The route.** Call out booked [T] → Work done [T; what was done, time spent] → one of:
Ticket completed — to be invoiced [T; amount] → the workshop's To be invoiced → Client invoiced →
Awaiting payment → Payment received → **Closed** (no machine to collect) · Invoiced — paid on site
[T] → Paid cash [T; amount] or Paid card machine [T; amount, card machine reference] → Paid on site —
to be closed [OA] → Closed · Needs workshop work [T; the booking's questions, 3 photos in] → the
Workshop route from Booked in. Abandoned [OA] from anywhere open. Call outs already on file start at
Call out booked.

**Amended 2026-10-05 — HANDOVER §86.** Clint, verbatim:

> Two things need amending. Call outs need the option to reschedule and when something is assigned
> to billing, all office administrators should be notified.

**His two answers, the same day:**

1. **Reschedule asks the new date and time AND why**, at Call out booked (before anybody has been
   out). It stays at Call out booked; the story shows the new time, what it was, and the reason. The
   people on the job, the office and supervisors may press it.
2. **The office is told by a badge AND a pop-up.** The pop-up the moment a job lands in the billing
   queue (portal and console, never to whoever put it there); the badge beside "Billing queue" (the
   console: in the Tickets button's count) is how many jobs wait for the office to act, and stays until
   they have. Every office administrator; in a company with none, the supervisors and owner (answer 16).

### Recurring task — a sixth category (2026-10-07)

**BUILT 2026-10-07 — HANDOVER §108.** Clint, verbatim:

> On the ticketing system, we need to have an option for recurring tasks. An option to select,
> daily, weekly, or monthly and on what day. Some cases are for maintenance, some are for billing,
> some are for readings so it would very much be a category of its own. Does this make sense? So one
> would setup the task once assign it to the correct person/persons and forget about it, but it
> should also have a cancel task option if the task becomes obsolete.

The shape, agreed: **a schedule set up once, which opens an ordinary ticket each time it falls due.**
The ticket is a *Recurring task*, already assigned, with notes, time and billing like any other job,
so the history builds itself. **Cancel stops future tickets only**; it asks why, and the schedule is
kept, marked cancelled. A schedule can be changed without being cancelled and set up again.

How often: **daily** (every day, or weekdays only), **weekly** (one or more days ticked), **monthly**
(a date, or the last day of the month — the 31st in a short month is its last day, never skipped).

**His five answers, the same day** ("1 to 4 we go with your suggestions"):

1. **The type** (Maintenance, Billing, Readings) is a list each company can add to, starting with
   those three.
2. **Done closes it, and To be invoiced is offered too** — the way General works (General's Done
   already closes from the bench, so this is no new exception to "a technician never closes").
3. **If last time's is not finished when the next falls due, the next opens anyway** and the old one
   shows as overdue.
4. **Anyone may set one up; only the people on it, a supervisor or the owner may change or cancel it.**
5. **"It opens on the morning it needs to be done"** — not days before.

**The route.** Due [the people on it] → **Done** (closes) · **To be invoiced** (what was done, time,
amount → the usual invoicing route, closing on payment). Abandoned [OA] from anywhere open, as ever.
No client is required. Not on the free version, which has no tickets.

## Traps already known

- **`TicketStatus` is one enum for all five categories.** Per-category stages either map onto the
  five that exist, or the model changes. Decide which before writing any code.
- **A category's number is not its position on screen** (§42). General is stored as 4 and offered
  first. Anything that looks up a workflow per category must key on the VALUE —
  `TicketSummary.CategoryOrder` is display order and nothing else.
- **Automatic tickets do not go through a booking form.** A session opens its own ticket in
  `TenantHub.AttachSessionToTicketAsync` (§36) and a customer's call in `OpenTicketForCallAsync`.
  Any "required to open it" rule has to say what those two do, or remote support tickets will start
  failing to open at the moment somebody needs help.
- **Roles are about to stop being a single value** (3.6.8). Ask
  `Permissions.CanSomething(role)` — never compare a role — or the workflow rules become part of
  that migration.
- **One enforcement point.** `TicketService.UpdateAsync`. A rule that lives anywhere else is a rule
  the console or a stale browser tab walks around.

## How it gets proved

A harness in `tools/`, the way every stage since 3.6.1 has been: the rules asked of the service and
never of a page, because a control that is not on the screen is manners and the rule is what runs
when the form is posted anyway. `photogate` is the closest model — it checks all five categories
rather than trusting the one it cares about.
