# The control dashboard — admins, servers, capacity, placement, moving a company

Asked 2026-10-13, after the xneelo load test (HANDOVER §126). Clint's words:

> *"Firstly, there should be an option for me to add admins that help me manage everything like
> activating and suspending clients etc. Secondly there needs to be an easy to read dashboard that shows
> all the servers and their specs that we have up, how many tenants on each, how many users per tenant
> and importantly how many of those are technicians if that is possible. It should show us when we are
> nearing the recommended capacity, so that we know whether to upgrade the server, or setup a new one.
> One thing I would like to know is if it will be possible to move a tenant from one server to another.
> When signing into the dashboard, we should see all these options. Would that mean a sign in once into
> the servers that the dashboard remembers as one would not want to sign in every time. When setting up a
> new server, how will it know to start putting the next tenants onto it automatically?"*

Nine questions were put to him; his answer to all of them: **"Lets go with your recommendations."**
These are those recommendations, now decisions. Do not re-ask them.

## The decisions

1. **Where the dashboard lives: on the live server for now** (`/owner`), built so it can move to a small
   server of its own later without a rewrite. The server you sign in to is the *control*; it lists itself
   and every server added to it. If it is down, the dashboard is down — accepted for now.
2. **Admins are named people with levels.**
   - **Viewer** — sees everything, changes nothing.
   - **Manager** — approves and rejects signups, activates and suspends, seats, billing flag, country,
     the waiting list, emails a company's owners.
   - **Full** — all of that, plus admins (Viewer and Manager only), servers, mail settings, and (later)
     moving a company.
   - **Owner** — Clint. Only an Owner makes or changes a Full or an Owner, and **an Owner can never be
     removed or lowered from the pages** (the server's command line can).
   - **Every action is logged with the admin's name.**
   - The old admin key + authenticator stays on every server as the **way in when all else fails**; it
     signs in with Owner powers as "Platform owner (admin key)".
3. **What admins may do besides activating and suspending:** see a company's contact details (yes); email
   the company's owners from the dashboard (yes); **sign in AS a company — NO**, not built; **a company is
   emailed automatically when suspended** (and when it is made active again).
4. **"Users per tenant"** is everybody: staff (owners, supervisors, technicians, office administrators) with
   **technicians shown separately**, and their **machines** — because machines drive the load (500 machines
   with agents are ~1,000 of a company's ~1,030 connections).
5. **Warning levels: amber at 70%, red at 85%** of measured capacity; an **email to every Full admin and
   Owner** when a server crosses either.
6. **Each server has its own name and address** — `za1.theremotesupportprogram.com`, `za2.…`; the current
   live server is za1 and its old address keeps working. Moving companies depends on this.
7. **Moving a company: within the same country only** (their data stays in their country) unless the company
   itself relocates; **a few minutes at night, with the company emailed beforehand.**
8. **Placement is automatic**: a new company goes to the server in its country with the most room, until
   amber; a switch per server stops it taking new companies.
9. **Free-version companies count at their real, small weight on the same servers.**

## How each piece works

**Sign in once.** An admin signs in at `/owner/login` with email, password and an authenticator code; the
browser is trusted for 7 days afterwards (the code is not asked again; the password is), exactly as the
portal does (§75). The dashboard talks to every other server with that server's **server key** —
`Platform:ServerKey` in its settings, a long random secret made by go-live, pasted once into the dashboard
when the server is added, kept encrypted (Data Protection). **Never an admin's password.** Admin accounts
live only on the control server.

**What each server reports** (`GET /api/control/status`, the server key as a bearer token): its name and
region, version, cores, memory, disk, processor, open connections, PostgreSQL or SQLite, and every company
on it with its status, plan, people, technicians and machines. The control server reads itself directly.
Changes an admin makes to a company on another server go to that server's `/api/control/…` with the admin's
name, and are logged on both.

**Capacity** — measured, not guessed (§126): on `s-g-4cpu-8gb` one cell carries **~18,500 connections**,
the profile company being ~1,030 (10 technicians, 500 machines with agents, 10 sessions). So:
- **Planned load** from headcount: each company's expected connections = technicians + 2 × machines +
  2 × technicians (their sessions), against 18,500 × memory ÷ 8 GB (a server can be given its own figure).
- **Live readings**: memory in use (the test ran out of memory at ~33 companies; 70% used was ~20, 85% ~25),
  processor over 5 minutes, disk, and connections open now against the same capacity.
- The **worst of them** sets the server's level. Amber says *"plan the next step — a bigger size for this
  server, or a new server for ZA"*; red says *"full — new companies stop coming here."*

**Placement.** Signup (on the control server) sends a company to the server in its country that is taking
new companies, below amber, with the most room. None below amber → the one with most room below red, and the
Full admins are emailed *"set up a new server for ZA."* None below red → the existing waiting list, as for a
country with no server. A new server added to the dashboard joins at once.

**Moving a company** — its own stage, after the rest. Copy every row of that company to the other database
(renumbered consistently), its files (photos, recordings, knowledge-base pictures, logo), then the old server
answers that company's machines and consoles with *"you live at za2 now"* so they switch by themselves — no
reinstall. Minutes at night, emailed first.

## The order

- **A. Admins** — accounts, levels, invitations, sign-in with 7-day trust, the log, suspension emails,
  contact details, email the owners. *(Built 2026-10-13, §132.)*
- **B. Servers and capacity** — server key, `/api/control/status`, the dashboard, levels, the emails. *(Built the same day, §133.)*
- **C. Placement** — signup chooses the server.
- **D. Server names** — go-live for a second server, `za1.` for live. Mostly deployment.
- **E. Moving a company.** Its own brief when we get there.
