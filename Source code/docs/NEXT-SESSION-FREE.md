# The free version, and remembered unattended passwords

**Brief written 2026-10-06 from Clint's description and fourteen decisions he made the same day.**
**ALL SEVEN STAGES ARE BUILT (2026-10-06, HANDOVER §100–§107), none on hardware.** Read `HANDOVER.md` §77 (the unattended password), §31 and §41 (unattended
access and the agent), §55 and §83 (roles and seats) and §84 (signup) before starting: this work
changes all five.

## The one-paragraph version

A free version of the Remote Support Program for **people with remote offices**: one person, who is
both the owner and the technician, controlling **at most two computers** that they or their own
company own. **Unattended only** — nobody at those machines is ever asked to Allow — and every one of
them has an unattended password, chosen at install. No ticketing, no clients, no branding step. Remote
printing and file transfer are in. Separately, and for **both** versions: once a technician has typed a
machine's unattended password, it is remembered **for that technician on that machine**, so they are
not asked again.

## Clint's words

> I would now like to create a free version of the remote support program. The free version, should
> be a single owner/technician with no ticketing system. It should allow for unattended access to a
> maximum of two computers and have no attended access. The two unattended access machines must have
> password protection, once the technician enters the password once, it should remember it for that
> machine. The same should apply for the full version. entering a password remembers it, but only for
> the particular technician that entered it, if that makes sense. The free version should allow for
> remote printing. When a person sets up the free version, it should not require logo or colour
> settings. It should default to the remote support programs colours, but with dark and light mode
> enabled. The idea behind the free version is for people who have remote offices.

And on the one thing he was asked to confirm afterwards: *"Lets add Being Controlled Remotely."*

## The decisions, so nobody re-opens them

| Question | Clint's answer, 2026-10-06 |
|---|---|
| Where is a remembered password kept, and when is it forgotten? | **On the server, against that technician and that machine,** so it works from any PC they sign in on. Forgotten when the password is **changed or removed at the machine**, when the **technician is removed**, or by right-click → *Forget saved password*. Nothing else expires it. |
| In the full version, may a supervisor or owner manage other people's saved passwords? | **Yes: see who, and clear.** A machine's right-click shows who has its password saved — never the password — and a supervisor or owner may clear one or all. |
| How is a free machine's password set, and what does the person there see? | **Set at install, no tray app.** The installer asks for the password (typed twice) and switches unattended on. Nothing of ours sits on that machine's screen. **Changing the password means reinstalling.** |
| Is anything shown during a session? | **Yes — "Being controlled remotely".** (Asked separately, after the tray was ruled out.) |
| A third machine? | **Refused, with the reason**: *"The free version allows two computers — remove one in the console, or upgrade."* Removing one frees its place. |
| How often may a place be freed? | **Limited — twice a calendar month**, so two places cannot be rotated round a whole customer base. |
| Which session tools does the free version keep? | **Remote control, printing (both ways), and Files with copy and paste and drag and drop.** Not Wake up, Processes/Performance, scripts, recording, the privacy screen, sound or talking. |
| May it be upgraded? | **Yes, in place.** Same account, same two machines, same saved passwords; it becomes a normal company once the owner accepts the full terms and price. |
| How does somebody sign up? | **The same `/signup`**: country first, then **Free or Full**, then the terms. A free company goes on its region's server like any other, so a region with no server gets the waiting list. |
| Two-factor? | **Required**, exactly as the full version. The account controls machines with nobody watching. |
| What do they have besides the console? | **A small portal**: My account (password, two-factor), Downloads (the console and the machine installer), their two machines, and Upgrade. No tickets, clients, people or knowledge base. |
| What may it be used for? | **Only computers they or their own company own** — business use included. Supporting customers needs the full version. **No time limits, no nag screens.** Written into its terms. |
| Is a company name asked? | **Optional.** Blank means the person's own name is used wherever a company name would show, and the slug is made from whichever is used. |
| Logo and colours? | **None asked.** Our own colours, with the light/dark switch every person already has (§38, §43). |

Taken as read, unless Clint says otherwise: the free version is **R0 / $0 / £0 / €0** on the signup
cards and in the landing page's `PRICES`; "twice a month" is a **calendar** month.

## Stage A — remembered passwords (both versions)

Today (§77) the console holds a typed password in memory for the run and the server checks it every
time. **This deliberately loosens §77's "nobody gets in without it"**: each technician still has to
type it once, but not again. Clint was told this and chose it.

- **A grant, not a copy.** When `TenantHub` accepts a typed password in `ConnectDevice`, it records a
  row — technician, device, and **the `Device.UnattendedPasswordChangedUtc` it was granted against** —
  not the password and not its hash. `PasswordLets` then accepts a technician with a grant whose stamp
  matches the device's current one, typed password or not. Changing or removing the password moves the
  stamp, so every grant dies at once without a sweep. **Never store the password, even encrypted.**
- **Forgotten when the technician is removed**: delete their grants in the same transaction that
  removes them. Do not tie it to the security stamp — a password change of their OWN must not forget
  the machines.
- **Right-click → *Forget saved password*** for one's own; **supervisor/owner** (`Permissions`, a new
  question, never a role comparison — §55) gets *Saved passwords…*: the people who have it, *Clear*
  each, *Clear all*. Names only.
- **The session-join guard** (`Session.Admitted`) keeps working: a colleague with a grant is admitted
  by Connect without typing; one without is asked, as now.
- The five-wrong lockout and "blank is not a guess" are unchanged. A grant is not a guess.

Harness: extend `tools/unattendedpw` — remembered across a console restart and from a second sign-in
of the same technician; a colleague still asked; a change at the machine forgets everyone; removal
forgets; Forget and Clear; a technician cannot clear a colleague's.

## Stage B — the plan, and the limits

- **`Tenant.Plan`** (Full, Free). One migration. Every limit below is asked of the plan in ONE place
  (say `Services/PlanLimits.cs`), the way `Permissions` and `OutsiderAccess.Allowed` are — never
  `plan == Free` scattered through pages.
- **One person.** A free company's only person is the owner AND technician (`TenantRole` flags,
  §55). People page absent; `AddTechnicianAsync` refuses on a free plan. Seats and prices (§83) do not
  apply; `Pricing` answers R0.
- **Two machines.** Refused at the hub when a third machine says hello on a free tenant, with the
  words above — the installer shows them (Stage D). A reinstall of one of the two (hardware-matched,
  §50) is NOT a third. **Removing** a machine frees its place, at most twice per calendar month; the
  third removal in a month is refused with when the next is allowed.
- **No attended access**: no calls, no Request support, no consent sessions, no outside help (3.9),
  no web support, no website chat. The hub refuses them on a free tenant — not just a hidden button.
- **Tools allow-list**: control, printing, Files, clipboard, drag and drop. The hub drops the rest
  for a free tenant's sessions, as `VisitorMaySend` does for outsiders (§89) — an allow-list.
- **No ticketing**: no session tickets (`AttachSessionToTicketAsync` does nothing on a free tenant),
  no clipboard ledger lines, no recordings.

## Stage C — signup and terms

- `/signup`: after the country, **Free or Full** (two cards). Free → the free terms → a shorter
  company form: name and email of the person, password, **company name optional**, no branding. The
  region and server rules of §84 are unchanged. **Name every handler** (§84's trap).
- **Terms**: a free-version section — own machines only, no customer support, as-is, two machines,
  may be ended. A draft for Clint's lawyer like the rest (`Terms.Draft`, move `Terms.Version`); run
  `landing-legal` afterwards (§88) or `legalcheck` fails.
- Two-factor set up at the first sign-in, as now.
- `Pricing.cs` and the landing page's `PRICES` both get Free.

## Stage D — the free machine installer, and no tray app

- The download for a free company is the same one .exe (§71); it learns the company is free from the
  server and then: asks the **unattended password, typed twice** (`UnattendedPasswords.Problem`, the
  one rule), installs the **agent service only**, switches unattended on, and sets the password.
- **The trap: §31 and §77 say only the CLIENT connection may set the permission and the password.**
  With no tray app the installer must BE that client connection for one exchange — the installed
  client exe run once by the installer (`--set-unattended`, say), then exit. Do not let the agent set
  it; that rule exists so a SYSTEM service can never unlock its own machine.
- **No tray app, so §57's Exit does not exist** — the machine is online while the agent runs. The
  helpers that run as the person (paste, copy, printing) are started by the host, not the tray app, so
  Files and printing still work; prove it.
- A third machine: the installer shows the hub's refusal and installs nothing.
- Changing the password: reinstall. The installer on an already-installed free machine asks for the
  new one.

## Stage E — "Being controlled remotely"

- While a session is open on a free machine, the **host** shows a small bar, top centre of every
  monitor: *Being controlled remotely by {name}*. Always on top, **click-through**, cannot be closed,
  gone when the last viewer leaves.
- **Marked `WDA_EXCLUDEFROMCAPTURE`** (as the privacy screen, §60) so it is not in the technician's
  picture and does not cover what they are working on.
- Drawn on the secure desktop too if it can be; if not, say so in the handover rather than half-work.
- **Free version only, as asked.** Whether full-version unattended sessions should show it too is
  Clint's to decide — not asked yet.
- Harness: `fitprobe` for its size from its text (§92's lesson), and `lookprobe` for both themes.

## Stage F — the small portal and the console

- **Portal** for a free tenant: My account, Downloads (console + machine installer), Machines (the two,
  online or not, Remove with the swaps left this month), **Upgrade**. Every other page refuses — and a
  refused page arrives as a 200 sign-in redirect, so **assert the page's heading** (§34).
- **Console**: one Unattended list (no Attended tab), no Tickets, Chat, Billing, Knowledge base, Office
  chat or Outside help; the Tools menu shows Files and the two printing items only; right-click has
  Connect, Forget saved password, Remove.
- Our colours, light and dark — the tenant simply has no branding.

## Stage G — upgrade in place

Owner presses Upgrade → the full terms for their region → the price (§83's Now/After, from R0) →
Accept. `Tenant.Plan` becomes Full; nothing else moves. The two machines stay unattended with their
passwords and grants; a tray app arrives with their next reinstall, if they want attended access.
Record the acceptance word for word, as signup does.

## Order

A first — it is wanted in the full version on its own and touches nothing else. Then B (the limits,
proved at the hub before any UI hides anything), C, D, E, F, G.
