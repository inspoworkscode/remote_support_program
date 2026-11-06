# Going live on theremotesupportprogram.com

Written 2026-09-14, DNS corrected 2026-09-16. Getting the platform onto a public server so real
customers can be supported over the internet, ahead of code signing (stage 5). This is roadmap
stage 4.1–4.3.

Everything here is two commands: one on the server's control panel, one on this PC.

> **Read `HOSTING-DNS.md` first.** The original server is gone and the domain now resolves through
> **Cloudflare** to xneelo shared hosting carrying a holding page and the mail. DNS records are
> edited **in Cloudflare, not in Afrihost ClientZone** — that changed on 2026-09-15. This runbook's
> procedure is otherwise unaffected: it is still two A records and one command.

---

## The first live test, October 2026 — in order

Agreed 2026-10-01: go live **empty** and test for real while the certificate is on its way; only
Anywhere IT on it at first; two other IT companies once the apps are signed. Each step points at the
detail further down.

**A. Before anything (this PC)**
1. Make an SSH key, if this PC has none: `ssh-keygen -t ed25519 -C "clint-laptop"` (Enter, then a
   passphrase). Copy the public half: `cat ~/.ssh/id_ed25519.pub | clip`. Keep a copy of the private
   half (`C:\Users\Clint\.ssh\id_ed25519`) in the password manager.
2. Stop the platform running on this laptop (the build needs its files).

**B. The server (xneelo cloud panel)** — §1
3. Create the VM — xneelo Cloud is OpenStack (checked against xneelo's help centre 2026-10-01):
   **Compute → Instances → Launch Instance**. *Flavors*: 2 vCPU / 4 GB. *Sources*: Image, Ubuntu
   24.04, boot volume 40 GB. *Networks*: leave **Public** (it gives the public IP). *Key Pair*:
   **import** the `ssh-ed25519 … clint-laptop` line. *Security Groups*: `default`.
   Then **Network → Security Groups → default → Manage Rules**: make sure there are INBOUND rules
   for ports **22, 80 and 443** from `0.0.0.0/0` — add any that are missing (*Add Rule*). Without
   80 and 443 nobody reaches the site and the certificate cannot be issued.
   The **IP** is on **Compute → Instances** once the status is *Active* — the IPv4 one (four numbers
   with dots). The console is on the instance's page (*Console*) — the way back in if SSH fails.
4. **The login is `ubuntu`, not root** (xneelo's Ubuntu images; passwords are off, the key is the
   only way in). Check it: `ssh ubuntu@<THE-IP>` — it asks for your key's passphrase, not a password.

**C. The name (Cloudflare)** — §2
5. Point `@` and `www` at the IP, **grey cloud**. Touch nothing else (mail lives on the shared host;
   the `*` record can stay where it is — nothing uses tenant subdomains yet).
6. Wait until `nslookup theremotesupportprogram.com` answers with the IP.

**D. Deploy (this PC, one command)** — §3
7. `powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server <THE-IP> -User ubuntu`
   (`-User ubuntu` is the default now; it asks for the key's passphrase three or four times — normal). It builds every app, uploads, and runs
   `go-live.sh`. Read the end of its output: **where the owner key is** (`sudo cat /etc/rsp-owner-key` —
   since 2026-10-15 no key is printed, because deploy logs get pasted), SSH (passwords off, or a warning),
   the test backup, and `https://theremotesupportprogram.com -> HTTP 200`.
8. **Copy `/etc/rsp-backup-key` and `/etc/rsp-owner-key` into the password manager now.**

**E. Owner portal** — §3, §5
9. `https://theremotesupportprogram.com/owner` → the owner key → scan the QR code → code. That is
   the platform owner's authenticator, a separate entry from your company login.
10. Owner → Mail: the platform mailbox (trial notices, new-sign-in alerts). Send the test.

**F. Our company, empty** — §4
11. `create-tenant 'Anywhere IT' anywhereit 7` — **and no `approve`** (that is a 14-day trial).
    **Then set its country** — `/owner/tenants`, the Country dropdown, or
    `set-brand anywhereit country "South Africa"`. A company made on the command line has NONE, which
    means **billed in dollars** and **no South African phone repairs on Import clients** (the
    leading 0 Excel drops). Set it before importing (found 2026-10-01, after Anywhere IT's import).
12. `add-tech … clint@anywhereit.co.za … --owner --admin`, typed with `read -s` (§4).
13. Sign in at `/login`: scan the QR code, save the ten recovery codes. Then the portal:
    **Branding** (logo, colours, support email and phone — the support email is where replies to
    invitations go), **People** (name, email and role only: each person is **emailed an
    invitation** to choose their own password and set up their authenticator, with the console
    download link if they have a seat — HANDOVER §73; Owner → Mail must be set first),
    **Health check** (the company's own mail settings), **Clients → Import clients** (the
    spreadsheet).
14. Signups stay closed (`SignupsEnabled=false`), so nobody else can make a company: the signup page
    says so, and only the server's command line can create one.

**G. Every link, from a PC that is NOT on the office network** (a phone hotspot will do)
15. `/get/anywhereit` — the customer page shows our logo and colours.
16. *Download the app* → `anywhereit-support.exe`. Windows warns (unsigned): More info → Run anyway.
    On a PC with **Smart App Control on, it is blocked outright** — use machines where it is off.
17. *Get help now*, then *Install*: the machine appears in the console. *The zip for IT staff* at the
    bottom of the page downloads too.
18. `/tech/anywhereit` — sign in (password + code) → *Download the technician console*. Unzip, run,
    sign in: the console's own code window, then the dashboard with the machine.
19. Portal → **Downloads**: both *Copy* buttons give the two links above (customers, technicians).
20. Optional, off until switched on: Portal → **Browser support** (`/help/anywhereit`) and
    **Website chat**.

**H. Does it work at all, over the internet** — `TEST-3.6.md`
21. A session from the console to a machine on ANOTHER network: picture, mouse, keyboard, a UAC
    prompt, Ctrl+Alt+Del, copy and paste both ways, a file each way, chat, sound. Then unattended
    (the customer ticks the box in the tray app). Then section AM (signing in). Log faults in the
    Test Checklist as usual.
22. Watch the server while testing: `ssh ubuntu@<THE-IP> "sudo journalctl -u remotesupport-platform -f"`.

**I. Two other IT companies — after the certificate**
23. Ship a signed build (`Publish-Platform.ps1` again — the data stays).
24. Per company, on the server: `create-tenant '<Name>' <code> 7`, set its country (`/owner/tenants`), then
    `invite <code> <their email> "Their Name" --owner --admin` — they are emailed the invitation and
    choose their own password; nobody types one for them. Leave them **Active** (no `approve`) for the
    length of the test; end it with `set-brand <code> status Suspended`. They do their own branding,
    people (by invitation) and import, exactly as in F.

---

## 0. What we are putting where

| Piece | Where it lives | Why |
|---|---|---|
| `RemoteSupport.Platform` (site, portal, relay) | a Linux VM with a public IP | it is a long-running ASP.NET Core process holding WebSockets open; shared/cPanel hosting cannot run it |
| TLS on 443 | Caddy on the same VM | certificate issued and renewed automatically; `wss://` is required before a real customer connects |
| DNS for the domain | **Cloudflare** (the domain is registered at Afrihost but its nameservers are delegated) | only two A records change |
| Email the product sends from | any mailbox with SMTP (an xneelo mailbox is fine) | configured in the owner portal afterwards, not at deploy time |

**Shared hosting will not do.** The platform is a self-contained Linux binary that must listen on a
port and keep thousands of sockets open. It needs a VM with root — xneelo Cloud, an xneelo
self-managed dedicated server, or any Ubuntu VPS.

**Sizing to start:** 2 vCPU, 4 GB RAM, 40 GB disk, Ubuntu 24.04 LTS. The binary plus the client and
technician payloads is about 350 MB. CPU is not the constraint — **bandwidth is**, because every
screen frame is relayed through this box. A single session at the current codec runs roughly
0.5–2 Mbit/s, so check the traffic allowance on the plan before the seat count grows.

---

## Stage 0. The holding page (do this first)

Decided 2026-09-14: the domain goes up as a **static holding page** while 2FA (4.4) is finished.
Nothing of the platform is installed or exposed, so the open-portal risk does not exist yet, and
there is something to hand a prospect in the meantime.

It lives on the **same server** the platform will use, so this is not throwaway work: the VM and the
DNS are set up once, and going live later changes only what Caddy serves.

**As of 2026-09-16 a different holding page is already live**, hand-written during the hosting
recovery and sitting in `public_html` on xneelo shared hosting — dark navy, blue accent, not built
from our tokens. `landing/index.html` below replaces it when the server exists. `HOSTING-DNS.md` §6
has the detail; do not assume what is on the domain today came from this repository.

**While the domain is on xneelo shared hosting there is no server to SSH into, so the page goes up
by hand through konsoleH.** One command produces the file to upload:

```bash
powershell -ExecutionPolicy Bypass -File deploy\Publish-Landing.ps1 -ForUpload
```

It writes `landing\upload\index.html` — already named `index.html`, contact details filled in — and
uploads nothing. Put that into `public_html`, replacing what is there. **Do not upload
`landing/index.html` itself**: it is the template, and it still has `{{EMAIL}}` and
`{{PHONE_DISPLAY}}` in it, which is why opening it in a browser shows no contact details.

The SSH route below is for when there is a server of our own again.

Do steps 1 and 2 below (create the server, point the two A records), then one command:

```bash
powershell -ExecutionPolicy Bypass -File deploy\Publish-Landing.ps1 -Server <THE-IP>
```

- The page is `landing/index.html`, built from the platform's own design tokens so the finished site
  is visibly the same product rather than a different one.
- **No third-party requests at all** — Clint's call, 2026-09-14. No web fonts, no analytics, no CDN:
  the whole page is that one file, favicon included, and the type is the visitor's own system stack.
  Keep it that way; a single font link would undo it.
- **The number is injected, not written into the markup.** It is the `-Phone` default in
  `Publish-Landing.ps1` (078 131 5618), so there is one line to change and no half-updated copy in
  the HTML. Any readable form works; `081…` is expanded to `+27…` for the `tel:` and `wa.me` links.
- **Two different addresses, on purpose.** `-Email` is what the page advertises for enquiries —
  **`queries@theremotesupportprogram.com` as of 2026-09-16**, moved off
  `theremotesupportprogram@anywhereit.co.za` (2026-09-14) so the address is on the product's own
  domain rather than Anywhere IT's. Still a shared mailbox rather than Clint's own, so it survives
  whoever answers it. **That mailbox has to exist in konsoleH**, or every enquiry the page invites
  bounces — see `HOSTING-DNS.md`. `-CertEmail` is who the TLS certificate
  is registered to and stays Clint's own address: Let's Encrypt writes there when a renewal has been
  failing for weeks, and that warning has to reach a person, not an enquiries inbox.
- `-PreviewOnly` renders the finished page to `landing/preview.html` and uploads nothing, which is
  how to check a wording change before it is public. `-ForUpload` does the same thing but names the
  output `index.html`, for the konsoleH upload above. Neither needs `-Server`.
- Editing the page later is the same command again — a couple of seconds, no reinstall.

**Going live afterwards** is `Publish-Platform.ps1` exactly as described below. **Since 2026-10-03
the holding page STAYS in front of the platform** until the real public site is finished: going live
on 2026-10-01 had replaced it with the platform's own home, features and pricing pages, which are not
ready, and nobody noticed for two days. Every publish now renders `landing/index.html` (through
`Publish-Landing.ps1 -ForUpload`), packs it as `holding/index.html`, and `go-live.sh` installs it to
`/var/www/landing` and has Caddy serve it for **`/`, `/index`, `/features`, `/pricing`, `/support` and
`/signup` only**. Everything else reaches the platform: sign-in, the portal, `/owner`, `/get`,
`/download`, `/help`, `/tech`, web chat, the API and the apps' WebSockets. When the real site is
ready, publish once with **`-RealSite`**: the Caddy configuration goes back to proxying everything.
Caddy's configuration is validated before it replaces the working one. **Since 2026-10-27 (HANDOVER §169) `-RealSite` also opens
signups and puts our chat bubble on the site** (Anywhere IT's chat wearing our name; `-SiteChat` names another company), and
without it both close again — so **from go-live on, every deploy needs `-RealSite`**, or the holding page comes back and signups
close with it.

---

## 1. Create the server

On xneelo Cloud (or whichever provider), create a VM:

- **Ubuntu 24.04 LTS**, 2 vCPU / 4 GB.
- Add **your SSH public key** so you can log in without a password. If you do not have one, from
  PowerShell on this PC: `ssh-keygen -t ed25519` and paste `C:\Users\Clint\.ssh\id_ed25519.pub`.
- Leave the firewall open on 22, 80 and 443; `go-live.sh` closes everything else itself.

Write down the **public IP**.

## 2. Point the domain at it

The domain is registered at Afrihost but its nameservers point at **Cloudflare**, so the records are
edited in the **Cloudflare dashboard** → the domain → DNS. Change exactly two records, which today
point at the shared host `197.221.14.40`:

| Type | Name | Value | Proxy |
|---|---|---|---|
| A | `@` | the server's public IP | **DNS only (grey cloud)** |
| A | `www` | the server's public IP | **DNS only (grey cloud)** |

**Both stay grey-clouded.** Cloudflare's proxy blocks raw TCP, which the relay's `wss://` needs, and
Caddy cannot get a certificate through it either. If the orange cloud is ever turned on, SSL/TLS
mode must be set to **Full (strict)** in the same sitting — never Flexible.

**Leave `mail`, `smtp`, `webmail` and every MX and TXT record alone** — they carry the domain's
email, they live on the shared host, and they are deliberately independent of the website so this
move does not touch them. Do not change the nameservers. See `HOSTING-DNS.md` for the full zone.

The wildcard `*` record is repointed at the new server too, but only once the platform is up — it is
what carries tenant subdomains such as `anyit.theremotesupportprogram.com`.

Allow up to an hour before the next step (usually minutes). Check from this PC:

```bash
nslookup theremotesupportprogram.com
```

It must answer with the server's IP. Caddy cannot get a certificate until it does.

## 3. Deploy

One command from this PC, in the repository folder:

```bash
powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server <THE-IP>
```

It builds the Linux payload, uploads it, and runs `go-live.sh` on the server, which installs Caddy
and Chrome, installs the platform as the systemd service `remotesupport-platform` bound to
`127.0.0.1:8090`, generates the owner key, gets the certificate, and locks the firewall to 22/80/443.
It finishes by checking `https://theremotesupportprogram.com` and saying **where the owner key is**.

Keep that key: `sudo cat /etc/rsp-owner-key` on the server. It does not change when you re-deploy.
**If it (or the server key) has been copied anywhere it should not be, replace both** with
`deploy\Rotate-Keys.ps1 -Server <ip>`: new keys, a few seconds' restart, the old ones put back if the
platform does not answer, and the new owner key shown once in your window only. The authenticator
is unaffected; every owner browser is signed out. **The first sign-in at `/owner` with it shows a QR code** for your authenticator app;
from then on the owner portal needs the key AND a code (HANDOVER §72). Lost phone:
`sudo -u rsp /opt/rsp/RemoteSupport.Platform reset-owner-2fa` on the server.

**Step 6 hardens the server** (2026-10-01): security updates install themselves, fail2ban guards
SSH, the program cannot rewrite itself and only it can read `data/`, and **SSH passwords are
switched off — but only if your SSH key is already on the server** (step 1). Without a key it says
so in capitals and leaves passwords on; add the key and run the deploy again.

It also makes **a nightly AES-256 encrypted backup** of `data/` in `/var/backups/rsp` (14 days).
**Its passphrase is in `/etc/rsp-backup-key` — copy it off the server the day you go live** (into
your password manager): without it no backup can be opened. To open one:

```bash
gpg --batch --passphrase-file /etc/rsp-backup-key -d rsp-20261001.tar.gz.gpg | tar xz
```

**Re-deploying later** is the same command. The database, uploaded logos, the Data Protection key
ring and the owner key are all left alone.

### A server for another region (added 2026-10-05, HANDOVER §84)

Each region's companies live on that region's own server. This one is **South Africa**
(`Platform:Region`, default `ZA`). A server for another region is this runbook again on a machine in
that region, with `RSP_Platform__Region=UK` (or `US`, `CA`, `EU`) in the service's environment. Then,
on EVERY other server, point signup at it — `RSP_Platform__RegionServers__UK=https://uk.theremotesupportprogram.com`
— so that region's companies are sent there instead of to the waiting list. Tell the people on the
waiting list (Owner → Approvals, *Waiting list*) once it is open.

### Every server on one dashboard (added 2026-10-13, HANDOVER §133)

The live server is the dashboard (`/owner`). Another server — a second South African one, or another region's — is
watched from it with that server's **server key**: go-live makes it once (`/etc/rsp-server-key`) and prints it at the end.
Run go-live on the new server with its name first — `RSP_SERVER_NAME=za2` — then, on the dashboard, *Servers* → *Add a
server* → its address and that key. Its Cloudflare record (`za2` → its address) must exist before go-live, as for any
server. The key is never an admin's password and never leaves the two servers.

## 4. Create the Anywhere IT tenant

Signups are deliberately closed (`Platform:SignupsEnabled=false`) until we are ready to sell, so the
first tenant is made on the server:

```bash
ssh ubuntu@<THE-IP> "cd /opt/rsp && sudo -u rsp ./RemoteSupport.Platform create-tenant 'Anywhere IT' anywhereit 7"
```

**Do NOT run `approve` on our own tenant.** `create-tenant` makes it **Active**, which never
expires. `approve` turns it into a **14-day trial**, and on day 15 every one of its machines is
refused at once (corrected 2026-10-01 — this page used to say to run it).

Then add yourself — **Owner and Supervisor**, so you have the whole portal AND a console seat. Log
in to the server and type the password where nobody can see it, so it never lands in the shell's
history:

```bash
ssh ubuntu@<THE-IP>
```
then, on the server:
```bash
cd /opt/rsp && read -s -p "Password for Clint: " PW && echo && sudo -u rsp ./RemoteSupport.Platform add-tech anywhereit clint@anywhereit.co.za "$PW" "Clint" --owner --admin; unset PW
```

Now:

- **Owner portal** — `https://theremotesupportprogram.com/owner`, key from step 3. Approvals, stats,
  all tenants.
- **Our portal** — `https://theremotesupportprogram.com/login` as `clint@anywhereit.co.za`. The
  first sign-in sets up your authenticator app and shows ten recovery codes — keep them. Upload
  the Anywhere IT logo, set the black/cyan colours, add technicians, download the technician console.
  Every technician sets up their own authenticator at their first sign-in.
- **Customer link** — `https://theremotesupportprogram.com/get/anywhereit`. This is what goes to a
  client; it serves the client app already branded and already pointing at
  `wss://theremotesupportprogram.com`.

Everything downloaded from the portal after this point carries the tenant key and the public URL, so
there is nothing to edit by hand on a customer machine.

## 5. Email the product sends

Sign in to `/owner/mail` and set the SMTP details for a mailbox we own on the domain:
**`smtp.theremotesupportprogram.com`, port 587, SSL on**, the full mailbox address as the username,
and its password. This is what trial expiry warnings, new-sign-in alerts and portal mail go out
from. Send the test from that page to confirm.

**587, not 465.** Mail clients use 465 (SSL from the first byte); the platform's mail code
(`System.Net.Mail.SmtpClient`) only does STARTTLS, which is 587. **And our own name, not
`smtp.xneelo.co.za`** — corrected 2026-10-01, measured: `smtp.theremotesupportprogram.com` is the
shared host our mailboxes live on (197.221.14.40) and on 587 presents a valid certificate for that
exact name (`Verify return code: 0`); `smtp.xneelo.co.za` is another xneelo server
(41.203.18.177) and did not answer on 587 at all. This page used to say the opposite.

Or from the server, which keeps the password out of the command line and out of shell history:

```bash
ssh ubuntu@<THE-IP>
cd /opt/rsp
read -s RSP_SMTP_PASSWORD; export RSP_SMTP_PASSWORD
sudo -u rsp -E ./RemoteSupport.Platform platform-mail set --host smtp.theremotesupportprogram.com --port 587 --ssl --user noreply@theremotesupportprogram.com --from noreply@theremotesupportprogram.com --from-name "The Remote Support Program" --copy-to clint@anywhereit.co.za
sudo -u rsp ./RemoteSupport.Platform platform-mail test clint@anywhereit.co.za
```

The domain's mail is already on xneelo shared hosting with the MX records in place, so this is only
a matter of creating the mailbox in konsoleH — see `HOSTING-DNS.md` §5. Note the outgoing host there
is `smtp.theremotesupportprogram.com` on 465 for a mail client — the product uses the same name on 587.

### 5b. The forwarding mailbox — tickets by email (2026-10-05, HANDOVER §79)

Only needed for companies that **forward** their support address to us (Microsoft 365, or anyone
who would rather not give us their mailbox password). Companies that let us check their own mailbox
need nothing here. Until it is set, the portal simply does not offer forwarding.

1. In konsoleH, create a mailbox **`tickets@theremotesupportprogram.com`**, used for nothing else.
2. Set it on `/owner/mail` → *Forwarding mailbox* → **Save and test** — or on the server:

```bash
ssh ubuntu@<THE-IP>
cd /opt/rsp
read -s RSP_INTAKE_PASSWORD; export RSP_INTAKE_PASSWORD
sudo -u rsp -E ./RemoteSupport.Platform forwarding-mailbox set --address tickets@theremotesupportprogram.com --host <the IMAP server konsoleH names> --port 993 --security ssl --user tickets@theremotesupportprogram.com
```

It signs in at once and says so (`Tested: Signed in to …`), or says in words what is wrong. **If it
says the certificate is for another name, use that name as the server** — shared hosting often
serves mail under the host's own name. The server reaches out on **993** (reading) and, for a company
that sends its auto-reply through its own server, **465 or 587** — outgoing, so the firewall's
inbound rules (22, 80, 443) are unaffected.

---

## Before a paying customer, not before testing our own

These are stage 4 items that are *not* blocking our own client testing but must be closed before a
second tenant is charging money. Kept here so they are not forgotten:

- ~~**2FA on the portal** (4.4).~~ **Done 2026-10-01** for every sign-in — portal, console,
  technician download page and owner portal (HANDOVER §72).
- **Backups OFF the box.** `go-live.sh` now makes a nightly encrypted backup ON the server
  (`/var/backups/rsp`). That is half of it: the previous server was lost outright in September 2026
  with its database and all its secrets, and the provider held no copies (`HOSTING-DNS.md` §1). A
  backup that lives only on the server is not a backup — copy `/var/backups/rsp` somewhere else
  nightly (rclone to cloud storage, or a pull from the office), and keep `/etc/rsp-backup-key` apart
  from it. If `data/keys` is lost, every stored workshop password, mail password and authenticator
  secret becomes unreadable, permanently.
- **Password reset** — there is no self-service reset; a forgotten admin password means a CLI
  `set-password` on the server.
- **Monitoring** — BUILT 2026-10-27 (HANDOVER §170): the office PC checks `/health` every 2 minutes and emails Clint once
  when the site is down and once when it is back, plus the certificate and the backup copy. `deploy\monitor\README.md`;
  one command on the office PC, `Install-SiteWatch.ps1`. Blind while the office PC is off or its internet is down.
- **Code signing** (stage 5) — until then, every client download shows a SmartScreen warning. Live
  testing with our own clients is fine; a reseller's customers are not.

## Updating without losing anything

`Publish-Platform.ps1` again is the whole update. **It never touches `/opt/rsp/data`** — the
database (companies, people, clients, tickets), logos, photos, recordings and the key ring: the
package carries none of it, `go-live.sh` drops a `data/` folder if a package ever did, and the
owner key, backup passphrase and settings are kept. Database changes (migrations) only ever ADD.
Two things an update does do: it restarts the service, so **every open console is signed out** and
asks for the password and code again (browsers stay signed in); and it takes **a backup first**,
`/var/backups/rsp/before-update-<date>-<time>.tar.gz.gpg`, kept 60 days. If the backup fails the
update stops before changing anything.

**Undoing an update** — putting the data back as it was just before it:

```bash
ssh ubuntu@<THE-IP>
ls /var/backups/rsp/before-update-*
sudo systemctl stop remotesupport-platform
cd /opt/rsp/data && sudo rm -f platform.db platform.db-wal platform.db-shm
sudo gpg --batch --passphrase-file /etc/rsp-backup-key -d /var/backups/rsp/before-update-<THE-ONE>.tar.gz.gpg | sudo tar xz
sudo chown -R rsp:rsp /opt/rsp/data && sudo systemctl start remotesupport-platform
```

## When something is wrong

```bash
ssh ubuntu@<THE-IP> "sudo journalctl -u remotesupport-platform -n 100 --no-pager"
ssh ubuntu@<THE-IP> "sudo journalctl -u caddy -n 50 --no-pager"
ssh ubuntu@<THE-IP> "sudo systemctl restart remotesupport-platform"
```

A certificate that never arrives is almost always DNS: Caddy has to reach the domain on port 80 and
it must resolve to this server.

---

## The version you hand to a technician

`go-live-runbook.html` is the same procedure written for someone who has never seen this codebase —
with the architecture explained, the two "never do this" warnings, a hand-back list and a
troubleshooting table. `Go-Live-Runbook.pdf` is that page printed. Rebuild the PDF after changing
the HTML, from the repository root (Edge is on every Windows machine; it must run as a logged-in
user, not SYSTEM — see pitch/README.md for why):

```bash
"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new --disable-gpu --no-pdf-header-footer --virtual-time-budget=15000 --print-to-pdf="C:\Users\Clint\Desktop\Remote Support\docs\Go-Live-Runbook.pdf" "file:///C:/Users/Clint/Desktop/Remote%20Support/docs/go-live-runbook.html"
```

The typefaces come from Google Fonts as it renders, so the machine must be online or the PDF falls
back to Georgia and Arial. **This file stays the authoritative copy**; if the two disagree, fix the
HTML and re-render.

### The hand-out kit

`tools\Make-LandingKit.ps1` assembles `dist\Landing-Page-Kit\` — the runbook PDF, the page, both
scripts and a one-screen `README.txt` — to be copied onto a memory stick and handed to a technician.
`docs/landing-runbook.html` is the source of that PDF; the kit builder re-renders it every time.

The technician needs nothing installed: no .NET, no repository, no build. `Publish-Landing.ps1`
looks for `index.html` next to itself when `..\landing\index.html` is absent, which is what makes
the same script work from the kit and from here.

Two things the kit builder checks before it declares success, because both have a silent failure
mode: that every expected file is present, and that `index.html` still has its `{{PHONE_DISPLAY}}`
placeholders — a kit shipped with those already filled in would publish whatever number was baked in
rather than the one on the command line.
