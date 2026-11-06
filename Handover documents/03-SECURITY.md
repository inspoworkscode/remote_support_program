# 03 — How the security works

A remote support tool can control customers' computers, so security matters more here than in
most software. This document explains, in plain terms, what protects what, and is honest about
what is not yet done.

The detailed engineering record is `HANDOVER.md` §72 (signing in), §73 (passwords), §31/§41
(unattended), §77 (unattended password) and §76 (permission prompts); since V2 also §100 (remembered
unattended passwords), §101 and §105 (the free version's limits), §104 (*Being controlled remotely*)
and §106 (removing machines, and Uninstall).

---

## The one idea to hold on to

**The thing that guards every customer computer is a technician's sign-in.** A technician who is
signed in can reach every machine of their company that allows unattended access, and scripts run
there with full system rights. So most of the security below is about making sure **only the real
technician can sign in**, that **access stops immediately** when it should, and that **the customer
stays in control** of their own machine.

---

## 1. Everything is encrypted on the way

- All traffic between every program and the server uses **TLS 1.3** — the same protection as online
  banking. It was measured on the live server on 1 October 2026: TLS 1.3, with a key exchange that
  is designed to resist future quantum computers (`X25519MLKEM768`), and AES encryption.
- The certificate comes from **Let's Encrypt** and is renewed automatically by **Caddy**, the web
  server that sits in front of the platform.
- **The programs refuse to talk unencrypted over the internet.** If a program is ever pointed at a
  plain `http://` address on the public internet by mistake, it refuses to connect. Unencrypted is
  only allowed on a private office network (for testing). This rule is in
  `Shared/SecureTransport.cs`.
- None of the programs ever switches off certificate checking.

**And end to end, since 7 October 2026 (HANDOVER §112, not yet seen on a real machine).** TLS ends at the
server, so before this a session was readable INSIDE the server — the picture, every key typed (passwords
included), the clipboard, files, sound, scripts. Now the customer's machine and each technician's console agree
a key the server never sees, and everything a session carries travels sealed with AES-256-GCM; the server
relays it and cannot open it. Each machine has an identity key (made on the machine, readable only by the
system) that signs the session key, and every console remembers it: if it ever changes — a reinstall, or
somebody pretending — the console stops and asks before showing or sending anything. What the server still
reads, on purpose: who connected to what and when, and the audit lines for the ticket (file names moved, a
script's first line, reboots). Honest limit: a server that had been taken over could still START a session of
its own; it could not read or hijack a real technician's. The session window says which it is — a green
*End-to-end encrypted*, or *Not end-to-end encrypted* when an older app is involved. The design is
`docs/NEXT-SESSION-E2E.md`.

## 2. Signing in

**Passwords**
- Stored only as a **one-way scrambled value** (PBKDF2-SHA256, 600,000 rounds since 7 October 2026 —
  older ones are re-made at each person's next sign-in — a different random
  "salt" for each person). Nobody — not even someone with the database — can read a password back.
- At least 8 characters and not the person's email address.
- People **choose their own** passwords through an emailed link. Nobody types a password for
  someone else, so a supervisor never knows anyone's password.

**Two-factor sign-in (2FA), for everybody**
- After the password, a **six-digit code** from an authenticator app on their phone (Google
  Authenticator, Microsoft Authenticator, etc.). This is the standard called TOTP.
- Each code works **only once**.
- **Ten recovery codes** are given once at setup, for a lost phone. Each works once. They are stored
  only as a scrambled value.
- The code is asked **once every 7 days** on the same computer or browser; the password is asked
  every time. That "trusted for 7 days" token is tied to the person and is cancelled instantly by
  any password change, role change or sign-out.
- A supervisor can **reset** someone's two-factor (they set it up again at their next sign-in).

**Stopping guessing**
- 5 wrong attempts on one account → that account is refused for 15 minutes, even with the right
  password.
- 30 wrong attempts from one internet address → that address is refused.
- "Forgot password" gives the same answer whether or not the email exists (so it cannot be used to
  find out who has an account) and is limited to 3 emails an hour per address.
- A sign-in from a **new network** is emailed to the person.

**Being signed out at once when it matters**
- Every sign-in carries a **security stamp**. Changing a person's password, roles or two-factor,
  removing them, or pressing **Sign out** on the People page changes the stamp. Their console is
  **closed within a second**, and their web portal on the next page they open.
- A console's sign-in expires after 12 hours unused.

**One sign-in at a time (added 2026-10-05)**
- Each person can be signed in to **one console and one web browser** at a time. Signing in to the
  console on a second computer closes the first console; signing in on a second browser signs the
  first browser out on its next page. The person who was pushed off is told why.
- Signing in on a **different** computer asks for the two-factor code even if that computer was
  trusted, and cancels the 7-day trust on every computer, so the first one is asked for the code
  too. This is in case the second sign-in was somebody else who has the password.
- Signing in again on the **same** computer, or after pressing Exit or Sign out, asks for nothing
  extra. The console's Tickets button opens the portal without counting as a second browser.

**Sign-in links**
- Invitation links last 72 hours, password-reset links 2 hours, and each can be used **once**.
- A link is used up only when the new password is actually submitted, not when the email is
  opened (email scanners open every link).

**The platform owner** signs in with a long secret **owner key** and a separate authenticator code.

## 3. The customer stays in control

- **Permission before every attended session.** The customer sees who is connecting and must click
  Allow. No answer within the time limit means no. Until they say yes, the program on their machine
  connects but **does nothing at all** — no picture, no input.
- **Unattended access is the customer's switch**, in their tray app. Untick it and unattended access
  stops. Only the customer's own tray app can change it — not the server, not a technician.
- **An unattended password** can be set on the machine by the person there. Then nobody, including
  the IT company's owner, gets in unattended without it. There is no office override, on purpose.
  Five wrong tries lock the machine for 15 minutes.
- **Remembered for each technician (V2).** Once a technician has typed it correctly, it is not asked
  of *them* again. This loosens the rule above on purpose and only this far: every technician still has
  to type it once. The server keeps only *that it was typed right, against which password* — never the
  password or its scrambled form. Changing the password at the machine forgets it for everybody. Someone
  else guessing (and locking the machine) does not lock out somebody who already knows it.
- **Uninstall (V2).** The person at the machine can remove the product themselves from the tray app;
  Windows asks for administrator permission. The IT company sees that it was uninstalled.
- **Only the machine's own tray app speaks for the person at it.** The permission switch, the password,
  closing the app and uninstalling are all accepted by the server only from that connection — never from
  the agent service (which is what they guard) and never from a technician.
- **Exit** in the tray app takes the machine fully offline for support.
- **The microphone** is never opened without asking the customer in that session, and **never** in
  an unattended session. The server itself blocks any attempt by a technician's program to answer
  that question on the customer's behalf.
- The customer is **told** when a technician connects, when files move, and when a session is
  being recorded.
- The clipboard is shared **only while a technician is connected**.

## 4. What each person is allowed to do

- Roles are **Owner**, **Supervisor**, **Technician** and **Office administrator**, as tick boxes.
- Every permission check is done **on the server**, in one place (`Platform/Services/Permissions.cs`).
  Hiding a button is never relied on — the server refuses the action itself.
- Office administrators **cannot connect** to any customer machine.
- A technician never closes a ticket; "do not invoice" needs a supervisor.
- Only a supervisor or the owner can delete a session recording.
- Only a supervisor or the owner can **remove a machine** (V2), and it needs a reason. A removed
  install is refused if it ever comes back — it cannot quietly put itself back on the list. An online
  machine is told to uninstall itself; the instruction is sent only to its agent, only at that moment.
- Only a supervisor or the owner can see **who** has a machine's unattended password remembered, and
  clear it. Nobody ever sees the password.

## 4b. The free version (V2)

- Its limits — one person, two computers, unattended only, a password on every computer, and only
  control, files, copy and paste and printing — are **enforced by the server**, not by hiding buttons.
  The hub refuses a third computer when it connects and drops any session message that is not on the
  free version's **allow-list** (so a message added to the product later is refused to the free version
  until somebody decides otherwise). The portal has one rule in front of every page and API, also an
  allow-list.
- A free computer is installed with the password **typed at the computer**, and set by one connection
  made **as that computer**, so the agent service still never unlocks itself.
- With no tray app, the person at a free computer is told by the **"Being controlled remotely"** bar,
  for the whole of every session, naming who is connected.
- The free version's terms say it is for **the account holder's own computers only**.

## 5. Keeping companies apart

- Each IT company has its own separate hub in the server's memory, and every database query is
  filtered by company. One company cannot see another's customers, machines, tickets or sessions.
- **The tenant key is not a password.** It is a long random code (192 bits) that every customer's
  install needs, so it is public by design — it says "this machine belongs to Anywhere IT". It lets
  a machine *ask for help*; it never lets anybody *in*. Making it longer would change nothing. Do
  not try to "fix" it.

## 6. Customer machines

- Each customer computer gets its **own secret device key** on first connect. Another computer
  cannot pretend to be it without that key.
- A machine that is **reinstalled** is recognised by a scrambled hardware fingerprint, so its
  history and customer link are kept rather than it appearing as a new machine. Because any program
  on a computer can read its hardware fingerprint, **unattended access on a reinstalled machine waits
  until a supervisor or the owner confirms it is the same machine** (V2.1, `HANDOVER.md` §111).
- **Files received are checked** so they can never be written outside the folder intended. (Four
  real ways to break out of the folder were found by the test tools and closed.)
- The host runs with **full system rights**. That is normal for remote support tools and is what
  makes password prompts and administrator programs work — but it is a deliberate choice and is why
  the sign-in protections above matter so much. **Scripts also run with full system rights.**

## 7. Stored data

- **Workshop device passwords** and **email account passwords** are stored **encrypted with
  AES-256** using the server's key folder, `data/keys`. They are shown only to signed-in staff and
  only behind a Show button.
- **The key folder is critical.** Lose it and every stored password is unreadable. Never change the
  line `SetApplicationName("RemoteSupportPlatform")` in `Platform/Program.cs` — it is part of how
  those keys work, and changing it has the same effect as losing them.
- **The screen picture is never stored by the server.** It only passes through. The exception is a
  session recording, which a technician chooses to make and which is stored on the server in
  `data/recordings` and linked to the ticket.

## 8. The server itself

Set up automatically by `deploy/go-live.sh`:

- Security updates install themselves.
- **SSH (remote login to the server) uses a key only**; password login is switched off. Repeated
  failed logins are blocked by **fail2ban**.
- The platform runs as its own limited account (`rsp`), cannot change its own program files, and
  only it can read its data folder. Extra Linux sandboxing is switched on.
- The website sends browser security headers (HSTS so browsers always use https, no framing of
  pages except website chat, which must be framed to work).
- **Nightly backups** of the database and the data folder, encrypted with AES-256, kept for 14 days in
  `/var/backups/rsp`. The passphrase is `/etc/rsp-backup-key` — **without it the backups cannot be
  opened**, so it must be copied somewhere safe (see document 08). An office PC can fetch each night's
  copy through an account that can do nothing but list and fetch those encrypted files (`rspcopy`).
- **The database (PostgreSQL, since 9 October 2026) has no password and listens on no network port**:
  only the platform's own Linux account can reach it, over a local socket.
- **Caddy reaches the platform through a Unix socket** that only Caddy and the platform can open; the
  platform trusts the visitor's address Caddy passes on only when it arrives that way (`UnixSocket.TrustCaddy`).

## 8b. The people who run the platform (V2.2)

- **The owner key opens nothing on its own.** `/owner` needs the key **and** the owner's authenticator
  code. Two older addresses that answered the key alone were removed on 15 October 2026, and
  `tools/securitycheck` fails if they come back.
- **Platform admins** sign in to `/owner` with their own email, password and authenticator, at one of four
  levels (Viewer, Manager, Full, Owner). Every change they make is logged by name, and **every change to a
  company goes through one place** (`AdminCompanies`). An Owner-level admin can never be removed from the
  pages. Suspending a company now takes effect at once: its consoles and machines are turned away and its
  people signed out.
- **Keys are never printed.** The set-up script says where each key is kept, never the key. If one is ever
  exposed, `deploy\Rotate-Keys.ps1` replaces the owner key and the server key — done on 15 October 2026
  after a log containing both was pasted into a chat; the keys in it are dead.
- **Servers talk to each other with a server key**, never an admin's password (the control dashboard).

## 8c. Payments (V2.2)

- **No card number ever reaches our server.** The customer types it on PayFast's own pages; we keep only
  PayFast's token for the subscription.
- A payment notice is believed only if it comes **from PayFast's own addresses**, carries a valid
  signature, names our merchant account and our subscription, **and** PayFast itself confirms it when asked
  back. Refused notices are not stored. The account details are put on the server with
  `deploy\Set-PayfastKeys.ps1`, never typed into chat or a command line.

## 8d. Updates (V2.3)

A program that installs other programs on customers' computers is exactly what an attacker would want to
control, so **the server is never trusted to say what is safe — the signature is.** Each computer checks
every file of an update itself, with Windows' own signature check, before anything runs:

- **Signed by us**: the certificate's organisation must be *The Remote Support Program (Pty) Ltd* in South
  Africa, read as fields of the certificate (a certificate anybody makes with our name in it is refused —
  the test tool proves it). Not "signed by anyone": any company can buy a certificate.
- **Valid and not revoked**: if the revocation list cannot be reached, the update waits and tries again.
- **Newer than what is installed**: so somebody who took over the server still could not push an old,
  genuinely signed program with a known fault. Only the computer's own rollback ever goes back.
- **There is no test mode** that relaxes any of this.

An update only ever comes from the computer's own IT company's server, and only an agent that says it can
update itself is offered one. A release reaches our own company first and pauses itself if computers roll
back. See HANDOVER §148.

## 8e. The outside security review (V2.4)

On **19 October 2026** an outside tester (Daniel) reviewed the live site from outside and signed in as a
company owner. **They found no way into another company's data and no account takeover.** Every finding was
checked against the code before anything was changed (HANDOVER **§158** — read it before "fixing" any of them
again; four were already right and the report could not see why):

- **Fixed:** a stranger could keep a person locked out by typing five wrong passwords every 15 minutes. A
  computer holding that person's *trusted for a week* sign-in is now counted on its own, so a stranger's
  failures never lock it. The holding page now sends the same security headers as the platform. A full
  content-security policy is sent **in report-only mode** (45 pages still have script written into them;
  browsers report what it would block to `/api/csp-report`, and that log is the list to clear before it is
  enforced). A missing ticket or article is a plain 404. A stored picture must hold together as a picture,
  not only start like one. `/health` says only `{"ok":true}`. `/.well-known/security.txt` exists.
- **Already right:** a limit per address behind the per-account one; *Forgot password* is throttled but
  answers the same whatever happens (so nobody can use it to ask which addresses exist); the runtime is the
  patched one (it is packed inside the program — read the build's `deps.json`, not the server's
  `dotnet --list-runtimes`).
- **Addresses use the company's own numbers (§159).** The address bar used to show the database's row number,
  one sequence for every company, which let a company estimate the size of the whole platform. Now
  `/portal/t/12` is that company's ticket #12, clients and articles have their own numbers too, and stored
  files are reached by a random token (`/portal/p/…`). Every lookup is made **with the company in the same
  query** (`Services/PortalAddresses.cs`). Old addresses keep working for their own company and are a 404
  for anybody else.
- **Still to do:** the tester's database held one company, so **access from one company to another was
  never tried**. Give them a second company and a technician login and have that re-run.

## 9. Honest list of what is NOT done yet

These were identified on 1 October 2026 and agreed as the order of work after going live.

1. ~~**Taking over a machine by its hardware fingerprint.**~~ **BUILT 7 October 2026 (HANDOVER §111),
   not yet on hardware.** A machine matched to its old record by hardware alone keeps the record, but
   unattended access waits until a supervisor or the owner confirms it is the same machine — the
   customer's own tick is not enough, because an impostor would hold that too.
2. ~~**Raise password scrambling**~~ **BUILT the same day (§111)**: 600,000 rounds, older passwords
   re-made at each person's next sign-in.
3. ~~**Code signing.**~~ **DONE 15 October 2026.** The certificate (Sectigo OV, through LeaderSSL, on
   a YubiKey) was issued on 14 October, and the first signed deploy went live the next day: every
   program and script the server hands out is signed and timestamped as The Remote Support Program (Pty)
   Ltd, and `Publish-Platform.ps1` refuses to upload anything unsigned. Programs installed on customers'
   computers before then stay unsigned until their one update by hand (V2.3, section 8d); after that they
   update themselves, signed. Windows' SmartScreen may still warn on
   a brand-new certificate until it has built up a reputation. See `docs/CODE-SIGNING.md`.
4. ~~**End-to-end encryption of sessions.**~~ **BUILT 7 October 2026 (HANDOVER §112), not yet on
   hardware** — see section 1 above.
5. ~~**A copy of the backups off the server.**~~ **BUILT 8 October 2026 (§120)**: an office PC fetches
   each night's encrypted backup. Proved on this PC against the real gatekeeper; **check it has been set
   up on the real server and office PC** (`deploy/backup/README.md`).
6. **An outside penetration test** before selling widely. **A first one was done on 19 October 2026**
   (section 8e); it still needs re-running with two companies, to try reaching one from the other.

Smaller known points:
- Sign-in lockouts are counted in memory, so a server restart clears them. A restart also signs
  every console out (by design).
- Nobody is told at the machine when a wrong unattended password is typed; only the server log has it.

## 10. Rules for whoever maintains this

- Never take a password in a chat, email or ticket. To store one for a script, use Windows'
  `Get-Credential | Export-Clixml`, which keeps it encrypted to that Windows user.
- Never switch off two-factor (`Security:RequireTwoFactor`) on a server customers can reach. It
  exists only for test tools that run their own throwaway server.
- Never commit secrets to the code repository. The `.gitignore` file lists what must stay out and why.
- Run `tools/securitycheck` after any change to sign-in. It has 78 checks and has proved, by
  removing each protection in turn, that it notices when one is missing. After a change to
  unattended passwords run `tools/unattendedpw`; to the free version, `tools/freecheck` and
  `tools/freeportal`; to removing machines, `tools/removecheck`.
- **Never put words from a web address on a page.** A result passed back in an address is a short
  code or an id, and the page chooses its own words — otherwise anyone could craft a link that shows a
  sentence of their choosing inside the portal (found and avoided while building the free version's
  Remove, `HANDOVER.md` §105).
