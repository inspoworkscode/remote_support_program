# The Remote Support Program — multi-tenant platform

`src/RemoteSupport.Platform` is the SaaS server, public website and customer portal for
**theremotesupportprogram.com**. One process hosts many IT companies (tenants). It is separate from
`RemoteSupport.Server`, the single-company server, which stays as-is for your own testing. Both speak the
same protocol, so the client, host and technician apps work against either.

## What a customer (an IT company) experiences

1. Visits the site, reads Features / Pricing, clicks **Start free trial**.
2. Signs up: company, contact name and number, country, email, password, colours. Status = **Pending**.
3. You approve it in the **owner portal** (`/owner`). Status = **Trial**, 14 days.
4. They sign in to **their portal** (`/portal`): upload a logo, tweak colours and consent settings, add up to
   5 technicians, download the technician console, and copy their customer link
   `theremotesupportprogram.com/get/<company>`.
5. Their customers open that link, see the company's logo and colours, and download the branded client.
6. Before the trial ends you invoice them ($49/month international, R500/month South Africa) and set the
   tenant to **Active**. Trial expiry or **Suspended** blocks logins and devices immediately.

## How tenancy works

- Every tenant has a **tenant key** baked into its downloads (`brand.json`). Devices present it in `hello`.
  No key, or a key for a non-active tenant, is refused with a friendly message.
- Technicians log in with **email + password**; the server derives the tenant. The desktop technician app
  is unchanged (it sends `username`, which the platform accepts as email).
- Each tenant gets its own in-memory `TenantHub`. Tenants never share a hub, so isolation is structural.
- **Seats** are enforced when adding technicians. Owner can change seats per tenant.
- Persistence is SQLite (`data/platform.db`) via EF Core. Switch to Postgres later by changing the provider.
  Before the first production deploy, move from `EnsureCreated` to EF migrations so schema changes are safe.

## Design language ("warm colourful UI")

Carried over from the earlier phase 9 prototype in `D:\trsp`, whose README lists *warm colourful UI* as a
feature. All of it lives in `wwwroot/site.css` as custom properties, so it is one file to adjust.

| Token | Value | Used for |
|---|---|---|
| Canvas | `linear-gradient(135deg, #fff7ed, #eef2ff 55%, #ecfeff)` on `html` | Peach → indigo → ice, full page height |
| Cards | `rgba(255,255,255,.82)`, radius **24px**, shadow `0 18px 45px rgba(15,23,42,.08)` | Translucent panels floating on the gradient |
| Radii | hero **28px**, card **24px**, button **18px**, input **14px**, pill **999px** | Everything is generously rounded |
| Signature | grape `#7c3aed` → `#8b5cf6` gradient | Primary buttons, links, stat numbers, price |
| Pastels | blue `#60a5fa`, green `#34d399`, pink `#fb7185`, purple `#a78bfa`, orange `#fb923c` | Icon tiles, decorative gradients, accents |
| Type | Inter first, headings **800–900**, tight tracking | Bold and friendly |

Two rules worth keeping:

- **Contrast.** The prototype put white text on pastel `#60a5fa`, which is hard to read. Pastels are used
  here for decorative fills only; anything carrying text uses the deeper grape or a dark ink.
- **Tenant brand wins inside tenant surfaces.** `/get/{slug}` and the portal's branding preview render in
  the *tenant's* colours (e.g. Anywhere IT's black and cyan) sitting on the warm platform canvas. The
  platform's own identity never overrides a customer's.

Avoid `backdrop-filter` on the sticky header — it caused repaint artifacts during testing.

### The desktop apps use the same language

`src/RemoteSupport.UI` is a small WinForms toolkit shared by the client, technician console and consent
prompt, so all three match the web: `CanvasPanel` (peach→indigo gradient), `Card` (rounded white panel),
`RoundButton` (rounded, gradient, hover lift), `Pill`, `InputBox` (rounded field with a purple focus ring),
`BrandHeader` (tenant colour band with the logo) and `SectionTitle` (coloured dot). Radii are scaled down
for desktop (16/12/10px) because the web's 24px reads as oversized in a 460px window. Tenant colours drive
the header and primary buttons; the warm canvas and pastels are the shared chrome.

### Incoming-call alert

`IncomingCallPopup` is a borderless, always-on-top window shown centre-screen the moment a call rings, so
a technician cannot miss one with the console minimised and the sound off. Design decisions worth keeping:

- It **never steals keyboard focus** (`ShowWithoutActivation`), so it cannot swallow what someone is typing
  elsewhere — a real risk for an alert that appears without warning.
- It **flashes the taskbar continuously** (`FlashWindowEx` with `FLASHW_TIMERNOFG`) and pulses an accent
  border, because sound cannot be relied on.
- It shows the **longest-waiting** call and a "+N more waiting" line; **Dismiss** hides only that call for
  that technician, and dismissals are forgotten once the call stops ringing so the set cannot grow forever.
- The dashboard owns its lifetime and closing it only hides it.

### Session start-up: late joiners

The host announces its monitors and system info **once**, the instant it connects. A technician whose
viewer attaches a moment later would otherwise never receive them, and the console cannot work without
the screen size: it never subscribes (blank window) and never sends input (`ToRemote` fails), so the
session looks completely dead. The hub therefore caches the host's last `MonitorsMsg` and `SysInfoMsg`
on the session and replays them to every viewer that joins. Keep that behaviour if the relay is
reworked, and add any future one-off host announcement to the same cache.

If the unattended agent's host process never connects within 12 seconds, the hub asks the attended
client app to host instead, so a locked-down machine still gets a working session.

WinForms traps worth remembering when extending these screens:

- `Label` treats `&` as a keyboard mnemonic and swallows it — set `UseMnemonic = false` (this ate the
  ampersand in "Team & client chat").
- A `Panel` wrapping a `TextBox` swallows Tab. `InputBox` sets `TabStop = false` and exposes
  `NextField(next)` so Tab and Enter move focus properly.
- A `Resize` handler that sets its own control's `Height` re-raises `Resize` and stack-overflows. Guard it
  with a re-entrancy flag (see the welcome label in the client's `MainForm`).
- Size dialogs with `ClientSize`, not `Size`, or the title bar eats the space and content clips.

## Pages

| URL | Who | What |
|---|---|---|
| `/`, `/features`, `/pricing`, `/support` | public | Marketing site (layout modelled on instanthousecall.com) |
| `/signup` | public | Trial signup → Pending |
| `/login` | company admins | Portal sign-in (technicians use the desktop console) |
| `/portal` … `/portal/billing` | company admins | Dashboard, Branding (+ live preview, logo upload, consent settings), Technicians, Devices, Sessions, Downloads, Billing |
| `/get/{slug}` | the company's customers | Branded download page |
| `/downloads/{slug}/client.zip` | public (active tenants) | Client package with tenant branding injected |
| `/downloads/{slug}/technician.zip` | signed-in admin | Technician console with tenant branding |
| `/owner/login`, `/owner`, `/owner/tenants` | you | Approvals, live stats, all tenants (status, seats) |

## Running it

```powershell
cd src\RemoteSupport.Platform\bin\Release\net10.0     # or the published folder
$env:RSP_Platform__AdminKey = "a-long-random-secret"   # or set Platform:AdminKey in appsettings.json
.\RemoteSupport.Platform.exe run
```

- Port **8090** by default (`Urls` in `appsettings.json`).
- `Platform:PublicUrl` (default `https://theremotesupportprogram.com`) becomes the `serverUrl` in every
  tenant download and the link shown in the portal. For local testing set it to `http://<your-ip>:8090`.
- Downloads need the app payloads next to the exe: `payloads/client` and `payloads/technician`
  (copies of `dist/client` and `dist/technician`). `build.ps1 -Platform` stages them automatically.

### CLI (also usable while the service runs)

`create-tenant`, `approve <slug>`, `list-tenants`, `add-tech`, `list-techs`, `remove-tech`, `set-password`,
`set-brand <slug> <field> <value>`, `set-logo <slug> <file>` — run the exe with no args for the exact syntax.

## Deploying to Linux (production)

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Platform
```
Copy `dist/platform-linux/` to the server, then `sudo ./install-platform-linux.sh`. Edit the admin key in
`/etc/systemd/system/remotesupport-platform.service`, restart, and put Caddy or Nginx in front on 443
(see DEPLOYMENT.md — same reverse-proxy recipe, upstream port 8090).

## Verified

Automated checks that were run against the live platform: 14 tenant-isolation checks (Phase 1) and 38
portal-flow checks (Phase 2) — public pages, signup → pending, pending blocks login, owner approval,
14-day trial, every portal page, seat counter, branding save, branded download page, client zip with the
tenant key, technician-console login into the right tenant, and suspension gating — all passing.

## Ticketing (technician-only)

Tickets live in the platform database and are worked from the **Tickets** button in the technician console.
Customers never see them.

**Tickets open themselves.** A support request creates one straight away, with the customer's description as
the subject. An unattended or ad-hoc connection to a machine with no open ticket creates one too, so no work
goes unrecorded. If the customer calls again while a ticket is still open, it is reused rather than duplicated.

**They fill themselves in.** Accepting a call assigns the ticket and moves it to *In progress*. The
technician connecting, every chat message either way, every script run, and the session ending with its
duration are all appended automatically. Status, priority and assignment changes are logged too, so a
ticket is a full audit trail of what happened on a customer's machine.

Technicians can also raise tickets by hand for phone calls and follow-up work, filter by open / mine /
unassigned / resolved / all, search, add notes, and reassign. Numbering is per tenant (#1, #2, …).

## Job categories and clients

Tickets have a **category** — General unless something says otherwise — and the form changes with it:

| Category | Extra fields |
|---|---|
| General | none — **the default**, the job that is simply a job |
| Remote support | none (opens automatically from calls and sessions) |
| Walk in | none |
| Workshop | make, model, serial number, accessories supplied, password-protected flag + password |
| Call out | site address, appointment date and time |

They are offered in that order, which is **not** the order they are stored in: General is
value 4, because 0 has meant Remote support in every row ever written. `TicketSummary.CategoryNames`
is the table by value and `TicketSummary.CategoryOrder` is the order to offer them; a picker walks
the second and carries the value it finds. See HANDOVER §42.

**Clients are real records.** Every ticket links to one. The new-ticket form (portal and console)
searches as you type and offers matches, so staff pick an existing client instead of creating a
duplicate; an exact name match is reused rather than duplicated even if nobody clicks a suggestion.
Machines can be linked to a client on the Devices page, and a ticket raised from that machine is then
filed under that client automatically.

**Anyone in the office can raise and assign a job** — reception and accounts get Clients, Tickets and
New ticket in the portal, while still being kept out of branding, people and downloads.

### Device passwords

Workshop tickets can hold the machine's password because the workshop needs it. It is **encrypted at
rest** with the platform's data-protection key (`ClientService.Protect`), never stored in plain text, and
decrypted only for signed-in staff. In the portal it sits behind a **Show** button rather than being
visible on screen by default. Keep the `data/keys` folder backed up and private: it is what decrypts them.

### Machine specification at a glance

The client and agent report the operating system, processor, memory and disk when they connect
(`SystemSpecCollector`), so the technician console and the portal's Devices page show what a machine is
without opening a session. Machines over 90% disk usage are flagged red, since that is the usual cause of
"my PC is slow". Windows 11 still reports itself as "Windows 10" in the registry, so the build number is
used to correct the name.

### Dates

SQLite does not store timezones, so EF is configured to mark every `DateTime` as UTC on read
(`PlatformDb.OnModelCreating`). Without this, every `ToLocalTime()` in the portal is silently wrong by the
local offset — appointments especially.

## Roles and seats

| Role | Desktop console | Portal | Uses a seat |
|---|---|---|---|
| Technician | yes | no | yes |
| Administrator | yes | everything | yes |
| Billing / reception | no | billing page only | **no** |

Billing logins exist so reception or accounts staff can see the plan, status and how to pay without taking
one of the five technician seats. They are refused by the console with a clear message, and the portal only
routes them to `/portal/billing`. Note that Razor folder and page policies combine with **AND**: the folder
allows both roles and each admin-only page then narrows it.

## Private testing mode

`Platform:SignupsEnabled` is **false** in `appsettings.json`. Public signup is closed: `/signup` shows an
"invitation only" page, posting to it is refused, and every call-to-action on the site points at `/support`
instead. Set it to `true` when you are ready to resell. Existing tenants and logins are unaffected.

## Database migrations

The schema is managed by **EF Core migrations** (`Data/Migrations`), applied automatically at startup, so
tenant data survives schema changes. After changing an entity:

```powershell
dotnet-ef migrations add <Name> --project src\RemoteSupport.Platform --context PlatformDb -o Data/Migrations
```

Never go back to `EnsureCreated` — it silently ignores new columns and tables on an existing database.

## Roadmap

| Phase | Status | Delivers |
|---|---|---|
| 1 | **done** | Database, multi-tenant hub, tenant-aware logins and device keys, seat limits, owner CLI/API |
| 2 | **done** | Public site, signup with approval, 14-day trial, tenant portal, owner portal, branded download page + zips |
| 4 | **done** | Ticketing (technician-only), billing/reception role, signups switched off for private testing, EF migrations |
| 3 | deferred | Code-signed generic installer + email notifications. **Deferred by choice** — clients are onboarded with AnyDesk for now and code signing waits until reselling |
| 5 | | Stripe + PayFast subscriptions, TLS, monitoring, backups, password reset |

Ideas from the reference site for later: a customer-opened **time-window** for unattended access, and a
"leave a review" prompt at session end.
