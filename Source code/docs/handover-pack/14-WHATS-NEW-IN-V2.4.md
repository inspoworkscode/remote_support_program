# 14 — What is new in V2.4

**V2.3** of this pack was written on **16 October 2026**. **V2.4** was written on **27 October 2026**, the week
before the product goes public on **1 November 2026** — the real website, signups and live card payments.
Documents 00–09 have been updated to match; documents 10–13 still list what V2, V2.1, V2.2 and V2.3 added.

Unless it says otherwise below, everything was **built and passed its automatic test tools** but has **not
yet been tried on real customer computers**. The hardware checklist is `docs/TEST-3.6.md`, sections **DF to
EC** (checks 740–932), and `docs/HANDOVER.md` **§149–§172** is the full engineering record.

---

## The two decisions that changed the business

| What | In short | Record |
|---|---|---|
| **Three plans** | **Full** (R500 a month for five people), **the Basic Version for Remote Workers** (R50 a month, one person reaching 3 to 20 of their own computers at R10 each extra, attended or unattended) and **the Free Version** (one computer, unattended with a password, nothing to pay). Free moves up to either; Basic up to Full or down to Free. In the code, Free and Basic share the one-person rules (`FreePlan`), stored as `"free"` and `"basic"` | §161–§163, DT–DV |
| **Five seats; more on request** | A company asks on its Billing page; we allow up to a number on **Owner → Approvals**; each seat past five is R300 a month, accepted by the company's owner first, charged through PayFast. **Five paying companies at a time** (Owner → Approvals sets it), others go on a waiting list; a pilot can be **ended by 30 days' notice** | §153, §160, DL, DS |

## Remote support

| What | In short | Record |
|---|---|---|
| **The Allow prompt comes to the front** | Windows was taking the always-on-top mark off it, so it opened behind other windows. It now takes the front and stays there while it counts down | §149, DF |
| **The tray app follows an update** | Computers updated themselves (seen on hardware: four releases on one PC), but the tray app never restarted into them — it was reading its own old version number. Fixed; each computer needs one sign-out or restart after the deploy that carries the fix | §152, DI |
| **A technician asks for outside help** | In a session, **Tools → Get outside help…** makes a code for that session, with a message to copy, WhatsApp or email. Whoever types it waits for that technician to let them in; the person at the computer is **told**, not asked | §155, DN |

## Machines and customers

| What | In short | Record |
|---|---|---|
| **The name at a support request names the machine** | An unlinked machine shows the name last typed into Request Support, in grey italic, instead of DESKTOP-ABC123, and linking it offers that person as a contact | §150, DG |
| **The installer asks who the computer is for** | Name, surname and email, required in the install window; it names the machine the same way and becomes a contact when the machine is linked | §156, DO |
| **Removing a client** | A client added twice or spelt wrong: everything on it (tickets, machines, people, knowledge base, recurring tasks, chats) moves to the right client, then it is deleted. Owner, supervisor or office administrator | §172, EC |

## Tickets

| What | In short | Record |
|---|---|---|
| **Latest activity** | Every update on the tickets as it is posted, newest first; everybody's or only mine; by client or by who wrote it; a count on the tab and in the console | §164, DW |
| **Sort by any column, and due dates** | Every heading in the ticket list sorts, in the portal and the console; every ticket can have a due date, red when overdue | §165, DX |
| **Job types** | Each company switches off the categories and steps it does not use; a check refuses any choice that would break a route, and a ticket keeps the switches it was opened with | §166–§168, DY |
| **@ lists the people on the ticket** | Typing @ in a comment box offers the people on the ticket first | DK |
| **Tickets by email: Sign in with Microsoft** | A Microsoft 365 mailbox can now be connected by signing in, with no password stored. Needs Clint's one-time app registration (`docs/MICROSOFT-365.md`) | §151, DH |

## The company and the platform

| What | In short | Record |
|---|---|---|
| **A company takes its data with it** | Portal → **Export**: one zip of clients (re-importable), tickets with their stories, photos, files and the knowledge base. We can export a suspended or closed company for its owners | §154, DM |
| **A free account from the owner pages** | Owner → Free version makes an account and emails the links, with a technician copied in to help set it up | §157, DP |
| **The outside security review** | An outside reviewer (19 October) found no way into another company's data. Fixed: a lockout that a stranger could cause, the holding page's headers, a content-security policy (report-only for now), stored pictures checked whole, and more. Document 03 | §158, DQ |
| **Portal addresses use the company's own numbers** | `/portal/t/12` is the company's ticket #12, not the database's row number, so an address gives nothing away about other companies. Old addresses keep working | §159, DR |
| **The health check switch says what it does** | It read as "install on every client", so it was left off. Reworded, and a save with it off says so in red | DJ |

## Going public

| What | In short | Record |
|---|---|---|
| **`-RealSite` opens signups and the chat bubble** | Every deploy wrote "signups off". Now the holding page and signups move together; with `-RealSite` our site shows a chat bubble saying **The Remote Support Program**, answered by Anywhere IT. **From 1 November every deploy needs `-RealSite`**, or the holding page comes back | §169, DZ |
| **The website speaks beyond IT companies** | It leads with *businesses that look after other people's computers and jobs* — IT companies, software and point-of-sale suppliers, in-house IT teams, repair shops, call-out businesses, remote workers — and uses the words South Africa searches (*job cards*, *remote access*, *live chat*). **The logo** is on every page | §171, EB |
| **Knowing when the site is down** | The office PC checks the site every 2 minutes and emails Clint once when it is down and once when it is back, plus the certificate and the backup copy. **Not installed on the office PC yet** (`deploy/monitor/README.md`) | §170, EA |
| **A deploy cuts connections once, not twice** | The platform now starts after Caddy restarts, so machines reconnect once per deploy | §165 |

---

## New test tools

`consentfront`, `requestedname`, `m365check`, `trayversion`, `placescheck`, `exportcheck`, `helpcheck`,
`installedfor`, `freeaccount`, `addresscheck`, `basiccheck`, `activitycheck`, `ticketsortcheck`,
`jobtypecheck`, `jobtypewire`, `jobtypepage`, `clientremovecheck`, and `Test-SiteWatch.ps1`; with changes
to `seatcheck`, `pilotcheck`, `securitycheck`, `freecheck`, `freeinstall`, `webchatcheck`, `sitecheck`,
`mentioncheck` and `healthcheckwire`. The ones that use a database run on **both** SQLite and PostgreSQL.

---

## Putting V2.4 live

1. **Signed deploys** (`deploy\Publish-Platform.ps1` with the YubiKey plugged in). The database changes
   apply by themselves. The console and customer computers update themselves (document 13).
2. **1 November**: the deploy with **`-RealSite`** (opens the website, signups and the chat bubble),
   PayFast's **live** account (`deploy\Set-PayfastKeys.ps1`, then `Payfast:Sandbox` false). **Every deploy
   after that needs `-RealSite`.**
3. **Install the site check on the office PC** (`deploy/monitor/README.md`), and check the office copy of
   the backups is running.
4. **Sign in with Microsoft** only works once Clint has registered the app (`docs/MICROSOFT-365.md`); its
   secret runs out — diary it.

## Known gaps

- **BUGS #13**: a message a machine sends in the same instant as its hello can be lost. No real program is
  known to do that. Not fixed.
- The content-security policy only **reports** for now: 45 pages still have script written into them.
- The site check cannot see anything while the office PC is off or the office internet is down.
- Nothing in this list has been tried on real customer computers except the self-updating agents (§152).
