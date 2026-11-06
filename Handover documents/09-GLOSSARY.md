# 09 — Glossary

Words used in this pack and in the technical documents, in plain English.

| Word | Meaning |
|---|---|
| **Agent** | The background Windows service on a customer's computer that makes unattended access possible |
| **Attended** | A session where the customer is at the computer and clicks Allow |
| **Authenticator app** | A phone app (Google or Microsoft Authenticator, etc.) that shows a new six-digit code every 30 seconds, used as the second step of signing in |
| **Caddy** | The web server program on the live server that handles the security certificate and passes traffic to the platform |
| **Client** | (1) A customer of an IT company, in the ticketing system. (2) The customer's tray app program, `RemoteSupport.Client`. The meaning depends on context |
| **Code signing** | A digital signature on a program proving who made it and that it has not been altered. Without one, Windows warns about or blocks the program. Every program the live server hands out has been signed since 15 October 2026 |
| **Control dashboard** | The `/owner` page showing every server, how full it is, and every company on it (V2.2) |
| **Damage report** | A workshop step for a machine not worth repairing: a report (often for insurance) attached and emailed to the client (V2.2) |
| **Consent** | The customer's permission, given by clicking Allow on the prompt |
| **Console** | The technician's Windows program |
| **Data Protection keys** | The server's encryption keys (in `data/keys`) used to lock stored passwords |
| **End-to-end encryption** | The session sealed between the customer's computer and the technician's console with a key the server never sees, so the server passes it on without being able to read it (V2.1) |
| **DNS** | The internet's address book: it turns `theremotesupportprogram.com` into the server's number. Ours is managed at Cloudflare |
| **Free version** | One person reaching two of their own computers, unattended, each with a password; no tickets or team. Upgradeable in place to the full version (V2) |
| **Full version** | Everything else in this pack: an IT company supporting its customers, with a team, tickets and branding |
| **Harness / test tool** | A small program in `tools\` that automatically checks one feature and reports how many checks passed |
| **Identity key** | A key made on each customer computer that proves, in every session, that it is the same computer. Each console remembers it (a **pin**) and asks before going on if it ever changes (V2.1) |
| **Holding page** | The single public web page shown at the website's home address until the real site is published; it carries the terms, refund and privacy policies |
| **Hash** | A one-way scrambled version of something (like a password). You can check a password against it, but cannot turn it back into the password |
| **Host** | The program started on the customer's computer for each session; it captures the screen and carries out the technician's actions |
| **H.264** | The video compression standard (the same as most online video) used to send the screen picture efficiently |
| **Hub** | The part of the server that keeps track of who is online and passes messages between programs; one per IT company |
| **Migration** | A recorded change to the database's structure, applied automatically when the server starts |
| **Release** | One build of the programs, as the server hands it out for computers to update to; it carries a version (V2.3) |
| **Ring** | Who a release is offered to so far: our own company first, then the pilots, then everybody. Moved on, or paused, on Owner → Updates (V2.3) |
| **Rollback** | A computer putting its old programs back because a new version did not connect within five minutes. It pauses the release for everybody (V2.3) |
| **Update by hand** | The one-off update a computer installed before 16 October 2026 needs (Tools → Scripts → *Update this machine*) before it can update itself (V2.3) |
| **Version** | The moment a program was built, in UTC: 2026.10.16.1432 is 14:32 on 16 October 2026. "1.0 (old)" is a computer from before version numbers (V2.3) |
| **Owner (platform)** | The Remote Support Program itself — signs in to `/owner` |
| **Owner (tenant)** | The owner of one IT company, e.g. the owner of Anywhere IT. An **owner-only login** (Owner ticked and nothing else) is free, oversees and comments, and does not work tickets (V2.2) |
| **PayFast** | The South African payment company that holds companies' cards and charges them monthly. We never see a card number (V2.2) |
| **Pilot company** | One of the first IT companies, signed up from a one-use link: five seats free while a pilot (V2.2); ended by notice from Owner → Pilots (2026-10-22) |
| **Platform admin** | A named person who may sign in to `/owner`, at one of four levels, with everything they do logged (V2.2) |
| **PostgreSQL** | The database the live server has used since 9 October 2026 (before that, SQLite, which tests still use) |
| **Payload** | The set of program files offered for download, with a company's branding added |
| **Plan** | Whether a company is on the free or the full version — a setting on the company, enforced by the server |
| **Portal** | The website staff of an IT company sign in to: tickets, clients, settings |
| **Job types** | The portal page where a company switches off the ticket categories and steps it does not use. Each ticket keeps the switches from the day it was opened |
| **Recurring task** | A schedule (daily, weekly or monthly) that opens an ordinary ticket on each day it is due — the sixth ticket category (V2.1) |
| **Recovery codes** | Ten one-use codes given when two-factor is set up, for signing in without the phone |
| **Relay** | Passing data through the server from one end to the other without storing it |
| **Remembered (saved) password** | A machine's unattended password that a technician has typed correctly once, so they are not asked again. The server keeps only that it was typed right, never the password |
| **Removed machine** | A machine taken off a company's list by a supervisor or the owner, with a reason, or uninstalled by the person at it. Its history stays; it is refused if it ever comes back |
| **Route / workflow** | The set of stages a ticket of a particular category moves through. Every step can be undone with **Back** (V2.2) |
| **Sandbox** | PayFast's test system: payments that move no real money. The live server is still on it until go-live |
| **Server key** | A secret one server uses to read and change companies on another, for the control dashboard (V2.2) |
| **Seat** | A paid place for one person in a company's subscription — any role; only an owner with no other box is free |
| **Security stamp** | A hidden value on each person that changes when their password or permissions change; changing it signs them out everywhere at once |
| **Session** | One live remote-control connection between a technician (or several) and a customer computer |
| **Ship** | Building and putting a new version where it will be used (`ship-build.ps1` for the office test, `Publish-Platform.ps1` for live) |
| **Slug** | A company's short web name, e.g. `anywhereit` in `/get/anywhereit` |
| **SSH** | Secure remote login to the Linux server, using a key file instead of a password |
| **SYSTEM** | Windows' most powerful built-in account. The host and agent run as it so they can work on secure screens and administrator programs |
| **Tenant** | One IT company using the platform (Anywhere IT is a tenant) |
| **Tenant key** | The long code in a company's download that tells the server which company a customer computer belongs to. Public, not a password |
| **TLS** | The encryption that protects web traffic (the padlock in the browser) |
| **Tray app** | The customer's small program by the clock |
| **Two-factor (2FA)** | Signing in with something you know (password) and something you have (phone with an authenticator app) |
| **UAC prompt** | Windows' "Do you want to allow this app to make changes?" prompt. It appears on a special secure screen, which ordinary remote tools cannot see |
| **Unattended** | A session started by a technician without anyone at the customer's computer, allowed only if the customer has ticked the box |
| **Unattended password** | An optional password the customer sets on their own machine; needed for any unattended connection to it. Required on every free-version computer, chosen when it is installed |
| **Upgrade** | Turning a free-version account into a full one, in place: same account, computers and saved passwords |
| **V1, V2, V2.1, V2.2** | Versions of this handover pack: V1 written 2 October 2026, V2 on 6 October 2026, V2.1 on 7 October 2026, V2.2 on 15 October 2026 |
