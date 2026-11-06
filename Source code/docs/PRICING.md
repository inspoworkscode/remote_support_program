# Seats and pricing — the commercial model

Decided by Clint 2026-09-16. **This is the authority on what a tenant gets and what it costs.**
Where the code still says something else, the code is out of date — §"Not built yet" lists exactly
where.

---

## Changed 2026-10-25 (evening) by Clint: THE FREE VERSION IS BACK, BESIDE THE BASIC VERSION (HANDOVER §163)

Clint: *"I think that we should have a free version as well. The free version should only allow one connection for unattended
access with password as it was previously. The basic version we should change to allow attended and unattended, but still only a
maximum of 20 machines."* And: *"will the free then have the option to upgrade to basic and full, and does basic have the option to
upgrade to full and downgrade to free?"* — yes to all of it. **This supersedes the next section's "no free version"; the rest of it
(Basic's price, cards, places) stands.**

| | The Free Version | The Basic Version for Remote Workers | The full version |
|---|---|---|---|
| Price | nothing, no card | R50 / $5 / £5 / €5 a month after 14 days, by card | R500 / $49 / £49 / €49 for five seats |
| Computers | **1** | 3, more at R10 / $1 / £1 / €1 each up to 20 | every one |
| Access | unattended, with a password | **unattended with a password, or attended** (the person asked, by the agent service) | attended and unattended |
| People | one (owner and technician) | one | five; more on request |
| Signup | approved by hand, then Active | PayFast, or a trial approved by hand | PayFast, or a trial; five places at a time |
| Moves | up to Basic or full | up to full; **down to Free** with one computer left | — |

- **Attended on Basic is guarded so it stays for one's own computers**: only installed computers that count toward the 20; no "Get
  help now", no Request Support, no tray app; one person; no tickets or branding; the "Being controlled remotely" bar always.
- **Moving down from Basic** cancels the card at PayFast at once; nothing more is charged and the month paid is not refunded (the terms
  say so). It is refused while more than one computer is installed — the owner removes the others first.
- **The free accounts from before stay Free.** One with two computers keeps both until it removes one; only a NEW computer past one is
  refused. The *Give notice* button is gone.
- Stored `"free"` (the Free version) and `"basic"` (the Basic version); migration `FreeVersionBack` moved the companies made as Basic
  that morning. Neither ever counts against the five places. Terms: `Terms/free.txt` (one computer, no fees) and `Terms/basic.txt`.

## Changed 2026-10-25 by Clint: NO FREE VERSION — THE BASIC VERSION FOR REMOTE WORKERS, R50 A MONTH (HANDOVER §161)

Clint: *"I have decided to no longer have a free version. I would like to change the free version to a R50 per month version and
set its maximum amount of machines to 3. All the usual rules for the free version would still apply."* His answers, all the
recommendations, with the name his own: **The Basic Version for Remote Workers** ("Basic" where space is tight).

- **R50 / $5 / £5 / €5 a month** (`Pricing.Region.Basic`), one person (owner and technician), **3 computers** — and, Clint again the
  same day, **more at R10 / $1 / £1 / €1 each, set by the owner on Billing with no approval from us, up to 20 in all** ("cap it at 20")
  (`Region.ExtraMachine`, `Tenant.BasicMachines`, HANDOVER §162)
  (`FreePlan.MaxMachines`), unattended with a password each, two removals a month, no tickets — every other rule of the free version.
- **Card at signup through PayFast, R0 now, the price after a 14-day trial**, switched on by PayFast like the full version (by hand
  only where card payment does not apply). Late payment, cancelling and receipts as for the full version.
- **The free accounts from before get 30 days' notice by email** (their terms promise it): Owner → Basic version → *Give notice* puts
  each on a trial ending 30 days on; with no card by then their computers stop connecting, nothing deleted, until the card goes in.
- **It never counts against the five places** — Clint: *"that should not apply to the basic version."*
- Upgrading: R50 → the package; from the next card payment if it already pays by card, else the trial it is on, else 14 days.
- The code keeps `FreePlan` and the stored plan `"free"`; only the words changed. Terms: `Terms/basic.txt` (was `free.txt`).

## Changed 2026-10-25 by Clint: FIVE SEATS; MORE ON REQUEST — THIS SUPERSEDES 2026-10-22's "NO MORE" (HANDOVER §160)

Clint: *"all tenants only get the 5 seats option, but they can request more seats, and if I decide it's fine, I right click on
their tenant, allow more seats, then they can add more seats to their basket"* — charged through PayFast, *"rather than me having
to bill them independently."* His answers, all the recommendations: R300 a seat; a number per company that we set; from the next
payment, no part-months; a pilot's extras invoiced; the terms say so; the website says *"five seats; more on request."*

- **Every company signs up with five**, R500 / $49 / £49 / €49, one owner-only login free on top — unchanged.
- **An owner asks on Billing** (*More seats*: how many in all, and why). We are emailed (Full admins and Owners) and it waits on
  **Owner → Approvals** until we **Allow up to N** or **Decline** (with a reason for them). Owner → All companies sets or takes back
  the number at any time (`Tenant.MostSeats`, 0 = the package; 50 at most). Taking it back removes nobody.
- **Each seat past the package is R300 / $20 / £20 / €20 a month**, agreed by the owner on the People page before the person is
  added (Now / After, Accept / Don't do it), kept as a `SeatAgreement`. PayFast's amount follows at once (`SyncAmountAsync`), from the
  next charge. A supervisor is told the figure and to ask the owner. **A pilot's package stays free; its extras are invoiced by us.**
- **Anywhere IT is still never limited**; the free version still upgrades instead.
- In the terms since **version 2026-10-25** (core section 2; the pilot section says its extras are invoiced).

## Changed 2026-10-22 by Clint: FIVE SEATS AND NO MORE; five companies at a time — superseded on "no more" by 2026-10-25 above (HANDOVER §153)

Clint: *"I have decided to make this product a 5 license only product, with no option to have extra seats. I want to make
this available to small I.T companies only."* And: *"allow 5 companies in at a time and rather test it that way."*

- **Five seats, any mix of roles, R500 / $49 / £49 / €49 a month — and no extra seats to buy.** A sixth is refused, to
  everybody. One owner-only login is still free on top (six logins at most).
- **Anywhere IT is never limited** — *"that is my company, so it can have as many seats as it wants."* (The company not billed.)
- **Why, in his words, as every page says it**: *"our vision is to make proper I.T support more affordable for smaller I.T
  companies, so we have restricted the seats to 5 only"* → `Pricing.WhyFive`.
- **Pilots**: five seats free while they are pilots; nothing to invoice any more. A pilot is ended by 30 days' notice
  (Owner → Pilots), then suspended and closed 30 days later, or moved to R500 with 14 days to add a card.
- **Five paying companies at a time** on each server (`OpenPlaces`, Owner → Approvals; 0 = no limit). Not counted: Anywhere IT,
  pilots, the free version. Past it, signup offers the waiting list, and we offer the next company a place by email.
- **The free version is kept**, unchanged.
- **Going live — the real site and live PayFast — on 1 November 2026.**
- The load measurement below assumed ten seats; a company is at most half that now, so a server carries more of them.

## Added 2026-10-15 by Clint: pilot companies, and what the free owner login may do (HANDOVER §146)

- **Pilot companies** — the two pilot IT companies: **the package of five seats is free for as long as they keep their
  account**; each seat beyond five is R300 a month, agreed by the owner before the person is added, **invoiced by us** (no
  card). Ten seats at most, as for everybody. Signed up from a one-use link (Owner → Pilots); Active at once, no trial.
- **The one free owner-only login oversees; it does not work.** It sets the company up and sees every ticket, client and
  machine, imports clients and comments on tickets — but does not move, edit, assign, open, invoice or close a ticket, email
  the client, or answer a website chat. Ticking any other box beside Owner gives that back, and takes a seat. In the terms
  since version 2026-10-15.

## How many companies one server carries (measured 2026-10-13, HANDOVER §126)

On a server like live (xneelo `s-g-4cpu-8gb`, 4 vCPU / 8 GB), companies of the worst shape the seat limit
allows — 10 technicians all signed in, 500 machines each with its agent, 10 sessions at once — **18 companies per
server** (it bends at ~27; a restart at 20 only just carried). Count connections rather than companies: about
**18,500 per server**, ~1,030 for such a company, ~2 per installed machine. Plan a second server at two-thirds full.
**Fix the port ceiling first (BUGS #12) — until then a bigger server buys nothing**; after it, memory and processor
are the limits and a bigger flavor roughly scales them.

## Changed 2026-10-08 by Clint: five seats, ten at most — THIS SUPERSEDES THE SEVEN BELOW

Clint: *"I think we are giving away too much for free. I would like to change the system to a 5 user
license with Owner only still being a free seat. So a company can have 5 users that can all be
assigned all 3 roles and thats their 5 seats taken. from there extra 300 rand for sa and extra 20 for
extra per seat taken internationally and the system must only allow for a maximum of 10 seats and one
owner. If more than one owner is selected, this must be considered a seat taken even if no extra roles
are assigned. The idea behind this system has always been to make a product that is more affordable
for small and medium enterprise, so going beyond ten seats does not sit well with me."*

His answers, the same day:

1. **The package price is unchanged** — R500 / $49 / £49 / €49 a month, now for **five** seats.
2. **One owner-only login is free; everybody else who can sign in takes a seat.** An owner who also
   ticks Supervisor, Technician or Office administrator takes a seat, and so does any second owner-only
   login. One person holding all three roles is still one seat.
3. **Ten seats at most, for every company** — Anywhere IT included, the platform's command line
   included. No override.
4. **The free version is untouched** (one owner/technician, two machines).

So: extra seats R300 / $20 / £20 / €20, the most any company pays is **R2,000 / $149 / £149 / €149** a
month, and the most logins a company can have is eleven (ten seats and the free owner). Every package
on file was moved to five by the migration `FiveSeatPackage`. HANDOVER §116 is what was built.

**Several owners, the same day (HANDOVER §117):** Clint — *"Lets make this change."* A company may
have any number of owners; only an owner makes, changes or removes one; the last is never lost. The
first owner-only login is free and every other is a seat, as above.

---

## The seat model (2026-09-16 — superseded on the count by the section above)

A subscription is **seven seats**, and they are not interchangeable. Naming them is deliberate:
"5 users" is what every competitor says, and it quietly leaves out the person who actually answers
the phone.

| Seats | Role | What it can do |
|---|---|---|
| 1 | **Owner / Supervisor** | Everything. Branding, billing, the team, and every session and job. |
| 1 | **Office Administrator** | Reception and accounts: log calls, book jobs, manage customers and invoicing. **No remote access to any machine.** |
| 5 | **Technicians** | Take calls, run sessions, work jobs. Five signed in at once. |

**"Office Administrator" means the job, not the permission.** It is the receptionist or the
bookkeeper. The name is a trap for whoever implements it — this seat must never inherit
administrative rights over the tenant, and must never be able to start or join a session. The
existing `--admin` flag on `add-tech` means the opposite thing and must not be reused for it.

**Refined 2026-09-24 by Clint:** the seven are **1 supervisor + 1 office administrator + 5
technicians, and the Owner is free on top** — the owner takes the supervisor seat only if they tick
Owner / Supervisor. A company may have several supervisors (each a seat) but only one owner. The
roles exist in the code since that day (HANDOVER §44); the seat COUNT is still the old default of
5 until roadmap 4.7.

Additional seats beyond the seven are bought individually, at the per-seat price below. Whether an
extra seat can be any of the three roles, or technician only, is **not yet decided** — see the
open questions.

## The prices

Per month, everything included. No per-session fee and no charge per machine under management.

| Region | Seven seats | Each additional seat |
|---|---|---|
| South Africa | **R500** | R300 |
| United States & Canada | **$49** | $20 |
| United Kingdom | **£49** | £20 |
| Europe | **€49** | €20 |

These are **prices, not conversions** — R500 is roughly $28, so the dollar price is deliberately
higher than the rand one. Do not "fix" them to match an exchange rate.

**The landing page shows one region's figures at a time**, chosen by the visitor, defaulting to
South Africa. The browser locale is deliberately not used — Windows in South Africa commonly
reports `en-US`, which showed a Johannesburg visitor dollars. The region buttons sit in a bar
**above the hero**, so the currency is picked before a word is read; they drive both the price
card and the price quoted in the hero line. The numbers live in the `PRICES` object at the bottom
of `landing/index.html` — one place, four entries.

## Rollout order

**South Africa first**, then the United States, Canada, the United Kingdom and Europe — which is
how the live landing page words it as of 2026-09-17. Europe was originally priced because the
question was asked rather than because it was scheduled; it is now named alongside the others.

Payment for South Africa goes through **PayFast**; Clint is getting the account details on
2026-09-17. Nothing is built for it yet. The other regions need a processor that handles their
currency, which is a separate decision and is not urgent while only South Africa is open.

---

## Built 2026-10-05 (HANDOVER §83) — and what is still not

**Built:** `Services/Pricing.cs` is the one place the prices live on the server (`landing/index.html`
keeps its own copy in `PRICES` — change both). `Tenant.Seats` is the seats **in the package** (7,
the platform owner may agree more with a company); `Tenant.Region` (ZA/US/CA/UK/EU) is what the
company is billed in; `Tenant.NotBilled` is Anywhere IT. **Every role takes a seat** except an
owner-only login (`TenantRoles.Paid`, `Technician.UsesSeat`) — office administrators are no longer
free. Past the package, the owner is shown Now / After on the People page and must Accept; a
supervisor is told the figure and to ask the owner; the service refuses anything else, and every
yes is a `SeatAgreement` row shown on the Billing page. The Billing page shows the true total.

**Still not built:**

- **No payment integration of any kind.** Roadmap 4.8. "Next bill" is a promise of the wording
  today: nothing invoices yet, so the price shown is what the next invoice should say.
- **Signup asks the region first** (built 2026-10-05, HANDOVER §84): the five, a waiting list for a
  region with no server, and the region's terms accepted before anything is made. **The terms are a
  DRAFT** (`src/RemoteSupport.Platform/Terms/`) until a lawyer approves them.
- **The public `/pricing` page describes the seven as 1 owner + 5 technicians + 1 office
  administrator**; the product now takes any mix. The holding page is what the public sees, so
  this waits for the real site.

## Decided 2026-10-05 by Clint — the old open questions, and four new rules

Clint reported that adding people "seems to allow as many as one wants". The code DID cap seats,
but only Technician and Supervisor: office administrators and the owner were free and unlimited
(`TenantService.cs`, and the People page said so). These answers close that.

**Seats and the price**

- **An extra seat can be any role, at the same price** (R300 / $20 / £20 / €20). An office
  administrator past the included seven costs the same as a technician.
- **Adding a person past the included seats asks first.** The owner sees the new monthly total and
  must Accept or Reject; **Reject adds nobody.** Only the **owner** may accept — a supervisor is told
  to ask the owner, with the figure.
- **Mid-month changes take effect on the NEXT bill.** The person gets access at once; the higher
  price starts on the next billing date. Removing a person lowers the next bill. No pro-rata.
- **Existing tenants keep everybody.** Nobody is removed; the billing page shows the true monthly
  total including the extra people. Anywhere IT (our own company) is marked not billed.
- *Assumed, not asked — correct if wrong:* the included seven are **any mix of roles**, the owner
  free on top (as refined 2026-09-24); and **Canada pays the US price, in US dollars**.
- The "five concurrent technicians" question is answered by the seat count itself: seats are a
  licence count, and the one-sign-in rule below stops one seat being shared.

**One sign-in at a time**

- Per person: **one console and one portal browser at a time.** A second console signs the first
  out; a second browser signs the first browser out. The console's own Tickets hand-off to the
  portal is not a new sign-in.
- Being signed out this way **cancels the weekly "skip the code" trust**, so the next sign-in
  anywhere asks for the two-factor code again — in case the second sign-in was somebody else.
  The signed-out machine is told why.

**Country at signup — five choices, one server each**

- Signup asks the country FIRST: **South Africa, United States, Canada, United Kingdom, Europe.**
  It decides the price and currency for as long as they are a customer, and which server the tenant
  lives on.
- **One server per country.** Until a country's server exists, its signup says "coming soon" and
  takes their details for a **waiting list** — no tenant's data is held outside its own country.
  Only South Africa is open.

**A country disclaimer before the tenant is set up**

- Each country has its own terms (POPIA for South Africa; GDPR / UK GDPR; PIPEDA for Canada; US
  law), including that we are not liable for loss of data. The owner must tick to accept before
  the tenant is created; the version accepted and when are stored.
- **Claude drafts, Clint's lawyer reviews.** The wording stays marked DRAFT until Clint says a
  lawyer has approved it.
