# 10 — What is new in V2

**V1** of this pack was written on **2 October 2026**. **V2** was written on **6 October 2026**. In
those four days a great deal was added. This page lists all of it, in the order it was built, so
somebody who read V1 can see what changed without reading everything again. Documents 01–09 have
been updated to match. **What came after V2, on 7 October, is document 11 (V2.1).**

Every item below was **built and passed its automatic test tools**, but **none of it has yet been
tried on real customer computers** unless it says so. The hardware checklist is `docs/TEST-3.6.md`,
and the section of `docs/HANDOVER.md` named in brackets is the full engineering record.

---

## The biggest change: a free version

A second way to use the product, for **people with remote offices** — one person reaching their own
computers in their own branch offices. Clint's decisions, in full, are in
`docs/NEXT-SESSION-FREE.md`. Document 02, Part H describes it.

| What | In short | Record |
|---|---|---|
| One person, two computers | The owner is also the technician. At most two computers, counted by the actual machine (a reinstall is not a third). A computer can be removed to free its place, twice a calendar month | §101 |
| Unattended only, with a password | Nobody at a free computer is ever asked anything. Every free computer must have an unattended password, chosen when it is installed | §101, §103 |
| What it can do | Remote control, Ctrl+Alt+Del and restart, files, copy and paste, printing both ways. No tickets, chat, scripts, sound, recording or browser support | §101 |
| Signing up | The same signup page: country, then **Free or Full**, then the terms. The free version has its own short section of terms ahead of the region's terms. No logo or colours asked; company name optional | §102 |
| Installing a computer | The download asks the server first whether this computer may be added (so a third is turned away before anything is installed), installs only the background service, and sets the password. **No tray icon stays on a free computer** | §103 |
| "Being controlled remotely" | Because there is no tray app, a small amber bar at the top of every monitor names who is connected, for the whole session. The technician's picture does not show it | §104 |
| Its portal and console | Portal: Your computers, Downloads, My account, Upgrade — every other page is refused. Console: just the computers, Connect (password only, asked once), Remove | §105 |
| Upgrading | One page: the full terms and the price (free → R500 a month after a 14-day trial). The same account, computers and saved passwords carry on. An open console switches to the full one at once | §107 |

**The free version is not public yet.** Signups are still switched off on the live server, and the
public front of the website is still the holding page.

---

## Changes to the full version

### Customers' machines and sessions

| What | In short | Record |
|---|---|---|
| **Remembered unattended passwords** | Once a technician types a machine's unattended password correctly, they are not asked again — from any computer they sign in on. A colleague who never typed it still is. Changing the password at the machine forgets it for everyone. A supervisor or the owner can see who has it remembered (names only) and clear it | §100 |
| **Removing a machine, with a reason** | Supervisors and the owner, from the console (right-click) or the portal (Devices). Reasons: Upgraded, Replaced, Private device, No longer a customer, Other (with a note). Its tickets and history stay; a **Removed machines** list shows who, when and why. An **online** machine uninstalls itself; an offline one is refused when it comes back | §106 |
| **Uninstall in the tray app** | For a customer who no longer wants it. Windows asks for administrator permission; the machine then leaves the IT company's list as "Uninstalled at the machine" | §106 |
| **Remote printing, both ways** | A technician can print what the customer prints on their own printer, and print a file on the customer's printer | §95 |
| **Wake-on-LAN** | Right-click an offline machine → Wake up. Another of that customer's machines on the same network sends the wake-up — nothing to set up on the customer's router | §94 |
| **Outside help** | A customer can let somebody outside their IT company in with a 9-digit code: another company on the platform, a guest with the guest console, or a guest in a browser. Screen and control only; files only if the customer ticks it | §87, §89–§92 |
| **Processes and Performance** | Right-click a machine to see its processes and live graphs without connecting | §81 |

### Faults fixed

| Fault | Fix | Record |
|---|---|---|
| BUGS #10: the first copy after the customer logged in did not reach the technician | Fixed | §93 |
| On some computers a program opened or closed did not appear until Refresh | The graphics chip's colour converter was only asked once per picture; it is now asked until it has finished | §99 |
| A reinstalled machine left an offline "twin" in the machine list until the server restarted | The old entry is removed when the reinstall is recognised | §101 |
| The console and customer app had square boxes, black corners and a mixed colour rule | One look: everything rounded, one colour rule. A test tool now checks the corners of 28 windows in both themes | §98 |

### The office side

| What | In short | Record |
|---|---|---|
| Tickets by email and a contact form | An email to the support address, or the website's contact form, opens a ticket and replies with its number | §79 |
| Knowledge base | General and per-client articles, with folders, history, and link buttons | §80, §96 |
| One sign-in at a time | One console and one browser per person; a second computer signs the first out | §82 |
| Seats are a price the owner agrees to | Adding a person past the package shows the new monthly price, and only the owner can accept | §83 |
| Signup by country, with that country's terms | Five regions, each with its price and its server; a waiting list where there is no server yet | §84 |
| The company's own time zone | Every time on the portal is in it | §85 |
| Call-outs can be rescheduled; billing notices | The office is told when a job reaches billing | §86 |
| Quotes | Several photos per item; the request asks stock, ETA and delivery fee; signed with a chosen signature | §96, §97 |
| **Terms are no longer marked DRAFT** | Clint, 6 October: "assume they are correct as is". His lawyer's changes, when they come, are edits and a new version number. The regions not yet open (US, Canada, UK, Europe) still have a few facts to fill in before they open | §88, §102 |

---

## New test tools since V1

`freecheck`, `freeinstall`, `freeportal`, `noticeprobe`, `upgradecheck`, `removecheck` (the free
version and removing machines); `wakecheck`, `printcheck`, `clipfirst`, `lookprobe`, `outsidercode`,
`outsidervisit`, `guestcheck`, `mailintakecheck`, `kbcheck`, `watchcheck`, `onesignin`, `seatcheck`,
`signupcheck`, `clockcheck`, `billingwire`; and `unattendedpw` grew from 33 to 65 checks. Each one
starts its own throwaway server where it needs one. Run the relevant one before and after any change.

## What to do with V2

Nothing here changes the advice in document 07: **the next job is proving, not building.** The
hardware checks for everything in this page are `docs/TEST-3.6.md` sections **AV–BX** (checks 320–496).
