# 04 — Where it runs

This is the map of every outside service the product depends on, as of 15 October 2026 (V2.2). Passwords
and logins are **not** here — document 08 says where they are kept.

The authoritative technical records are `docs/HOSTING-DNS.md` (domain, DNS, mail) and
`docs/GO-LIVE.md` (the server). If those and this page ever disagree, they win.

---

## The suppliers

| What | Supplier | Detail |
|---|---|---|
| The domain name `theremotesupportprogram.com` | **Afrihost** (registrar) | Only the registration is there. Renew it every year — if it lapses, every installed customer app stops working. |
| DNS (what the name points to) | **Cloudflare** | Nameservers `titan.ns.cloudflare.com` and `rosemary.ns.cloudflare.com`. **All DNS changes are made in Cloudflare**, not at Afrihost. |
| The live server | **xneelo Cloud** (OpenStack) | Ubuntu 24.04, 2 processors, 4 GB memory. A system disk plus **two volumes** added on 9 October: `rsp-files` (50 GB, the data folder and the backups) and `rsp-db` (20 GB, PostgreSQL). Public address **154.65.110.136**. A load test on a bigger server (4 processors, 8 GB) carried 18 companies of 10 technicians and 500 machines each (`HANDOVER.md` §126). |
| Email for the domain | **xneelo shared hosting** (konsoleH control panel) | Mailboxes such as `info@`. Separate login from xneelo Cloud. Mail stays here permanently, never on the platform server. |
| The website's security certificate | **Let's Encrypt**, through Caddy | Free, renewed automatically on the server. Nothing to do. |
| Code-signing certificate | **Sectigo**, bought through **LeaderSSL** | Ordered and paid 2 October 2026; **issued 14 October 2026, valid to 18 November 2027**, the first of the two-year term's two certificates. It is on the **YubiKey 5 NFC FIPS** token; **every program the server hands out has been signed with it since the 15 October deploy**. See `docs/CODE-SIGNING.md` and `docs/CODE-SIGNING-ORDER.md`. |
| Card payments | **PayFast** (South Africa) | A merchant account with Recurring Billing. Built and proved on PayFast's **sandbox**; the live server is still on the sandbox account and the pages are hidden until the real website goes up. PayFast's Recurring Billing page sends notices to `https://theremotesupportprogram.com/api/payfast/notify` and allows only the live server's address — **move it if the server moves**. `docs/NEXT-SESSION-PAYFAST.md`. |
| Company registration | **CIPC** | The Remote Support Program (Pty) Ltd, 2026/737434/07 |

---

## The live server

| | |
|---|---|
| Address | `154.65.110.136` — the website is `https://theremotesupportprogram.com` |
| Login | `ssh ubuntu@154.65.110.136` — **SSH key only**, no password login. The private key is on Clint's laptop (`C:\Users\Clint\.ssh\id_ed25519`) and should also be in the password manager. |
| If SSH fails | The xneelo Cloud panel has a **Console** button on the instance's page — the way back in. |
| Program folder | `/opt/rsp` |
| Data folder | `/opt/rsp/data` — the encryption keys (`keys/`), logos, photos, files on comments, recordings, health check reports. Since 9 October it lives on the `rsp-files` volume, mounted back at the same path |
| Database | **PostgreSQL** on the `rsp-db` volume (database and role `rsp`, signed in over the local socket — **no database password exists**, and nothing listens on the network). The drop-in file `/etc/systemd/system/remotesupport-platform.service.d/postgres.conf` is the one sign the server is on PostgreSQL. The old SQLite file `platform.db` is kept frozen as it was at the switch; `deploy\postgres\Switch-BackToSqlite.ps1` is the way back (`HANDOVER.md` §125) |
| Service name | `remotesupport-platform` (runs as the Linux user `rsp`) |
| Web front | **Caddy**, on ports 80 and 443, passing traffic to the platform through the Unix socket `/run/rsp/platform.sock` (since 15 October; `127.0.0.1:8090` is kept for checks on the server itself) |
| Owner key | `/etc/rsp-owner-key` (the first and last way in to `/owner`, always with the owner's authenticator code; named **platform admins** sign in with their own email instead). Replaced with `deploy\Rotate-Keys.ps1` if it is ever exposed — it was, and was, on 15 October |
| Server key | In the service settings `go-live.sh` writes (`/etc/systemd/system/remotesupport-platform.service.d/override.conf`, `RSP_Platform__ServerKey`); lets the control dashboard on one server read and change companies on another. Replaced together with the owner key by `Rotate-Keys.ps1` |
| Backup passphrase | `/etc/rsp-backup-key` |
| Nightly backups | `/var/backups/rsp` (on `rsp-files`) — a `pg_dump` of the database plus the data folder, encrypted, 14 days kept; each finished session recording encrypted once on its own under `recordings/`. A backup is also taken automatically before each update. An **office PC can fetch a copy every night** through a locked-down account `rspcopy` (`deploy/backup/README.md`; built and tested, check it is running) |
| Firewall | Inbound ports 22 (SSH), 80 and 443 only — set in xneelo's *Security Groups* |

**Useful commands on the server** (after `ssh ubuntu@154.65.110.136`):

```bash
sudo systemctl status remotesupport-platform        # is it running?
sudo journalctl -u remotesupport-platform -f        # watch its log live
sudo systemctl restart remotesupport-platform       # restart it (signs every console out)
cd /opt/rsp && sudo -u rsp ./RemoteSupport.Platform   # list the command-line commands
```

The command-line commands include: `create-tenant`, `invite`, `list-tenants`, `list-techs`,
`set-brand`, `set-logo`, `remove-tech`, `reset-2fa`, `signout`, `reset-owner-2fa`, `platform-mail`.
`set-brand <company> plan free` (or `full`) puts a company on the free version or takes it off — the
way to try the free version while public signup is off. `pilot-link "<company>" [email]` makes a pilot
company's one-use sign-up link (the same as **Owner → Pilots**); `set-brand <company> pilot true` makes an
existing company a pilot.

### The owner pages and the control dashboard

`https://theremotesupportprogram.com/owner` is where the platform itself is run: every company (approve,
suspend, change), the platform mailbox, PayFast, pilot links, reviews waiting for approval, and since
13 October a **card per server** with its readings and how full it is (amber at 70%, red at 85%), emailed to
the admins when it crosses a level. People sign in there as **platform admins** with their own email,
password and authenticator, at one of four levels (Viewer, Manager, Full, Owner); everything they do is
logged by name. The owner key plus the owner authenticator remain the first and last way in. Other servers,
when there are any, are read with their own server key, never a password (`HANDOVER.md` §132–133).

### What the public sees: the holding page

The public front of the website — the home, features and pricing addresses — is a **holding page**
(`landing/index.html` in the code), on purpose, until Clint decides the real site is ready. It carries
the company's details and the **terms, refund policy and privacy policy** that PayFast checks for.
Everything else (portals, downloads, every app's connection, a pilot's `/pilot/…` link) reaches the
platform as normal. **The real website is built** (twelve pages, light and dark, screenshots, company
reviews — `docs/WEBSITE.md`) and sits behind the holding page: every one of its addresses is in
`go-live.sh`'s holding list, and `Publish-Platform.ps1 -RealSite` is what shows it. A platform admin can
see it on the live server at **Owner → Preview** without publishing it.
**Public signup is switched off** on the live server too (`Platform:SignupsEnabled=false`), so the free
version cannot be signed up for yet. Document 05 says how each is changed.

**Going public on 1 November 2026.** The deploy with `-RealSite` shows the real website, **opens signups**
and puts **our chat bubble** on the site (answered by Anywhere IT's people). Since 27 October `go-live.sh`
writes all three from the same choice on every deploy, so they cannot disagree — and **every deploy after
1 November must carry `-RealSite`**, or the holding page comes back and signups close (HANDOVER §169).

**Updating the live server** is one command from the build PC — see document 05. It backs up the
data first and keeps it. Since 26 October a deploy restarts Caddy first and starts the platform after it,
so every machine and console reconnects **once** per deploy, not twice; if anything between fails, the
platform is started anyway (§165).

### Knowing when it is down

**The office PC watches the site** (built 27 October, HANDOVER §170; **not installed yet** — one command,
`deploy\monitor\Install-SiteWatch.ps1`, on the office PC that already copies the backups;
`deploy/monitor/README.md`). Every 2 minutes it asks `/health`; after **two misses in a row** it emails Clint
**once** that the site is down, and once when it is back with how long it was down. Once a day it also warns
if the website certificate has under 14 days left or the office's backup copy is more than 36 hours old. It
asks the office's own internet first, so an office outage is not reported as the site being down — but
while the office PC is off or offline, **nothing is watching**. Mail goes through a mailbox on xneelo's
shared hosting, apart from the server it watches.

### Restoring from a backup

Backups are `.tar.gz.gpg` files, encrypted with the passphrase in `/etc/rsp-backup-key`. Since the move
to PostgreSQL each one holds a **`pg_dump` of the database** and a copy of the data folder (without
recordings, which are backed up one by one). To look inside one on the server:

```bash
sudo mkdir -p /tmp/restore
sudo gpg --batch --passphrase-file /etc/rsp-backup-key -d /var/backups/rsp/rsp-YYYYMMDD.tar.gz.gpg | sudo tar -xz -C /tmp/restore
```

Restoring means stopping the platform, loading the dump into an empty `rsp` database, copying the data
folder back with ownership `rsp:rsp`, and starting it. A dump restored into an empty database is proved to
match the original row by row (`tools/backupcheck`), but **there is no one-command restore script yet**:
read `deploy/rsp-backup.sh` (which makes the backup) first, and practise a restore once on a spare server.

**If the server is lost completely:** build a new one with `docs/GO-LIVE.md` (about an hour), point
the DNS at it in Cloudflare, then restore the latest backup — which you can only do if a copy of the
backup and the backup passphrase exist somewhere other than the lost server. That is why document
08 asks for both to be kept off the server.

---

## DNS records (in Cloudflare)

| Name | Points to | Purpose |
|---|---|---|
| `@` and `www` | 154.65.110.136 (the live server) | The website, portals and every app's connection |
| `mail`, `smtp`, `webmail` | 197.221.14.40 (xneelo shared hosting) | Email — separate on purpose, so moving the server never breaks mail |
| MX, SPF, DMARC | xneelo mail | Email delivery and anti-spoofing |

All records are **"DNS only" (grey cloud)** in Cloudflare — not proxied. The full table and the
records that were deliberately removed are in `docs/HOSTING-DNS.md`.

## Email the product sends

The platform sends invitations, password resets, trial notices, new-sign-in alerts, health check
reports, payment receipts and reminders, and quote requests. It uses a mailbox set in the **owner portal → Mail**, sending through
`smtp.theremotesupportprogram.com` on port **587** (not 465). Each IT company can also set its own
mailbox for its health check reports.

---

## The office test setup (Anywhere IT, Knysna)

None of this is needed to run the live product, but it is where testing happens.

| | |
|---|---|
| Build PC | Clint's laptop, `C:\Users\Clint\Desktop\Remote Support`. Also runs a **test copy of the platform** at `http://192.168.1.115:8090` (office network only). |
| Test machine 1 | **DESKTOP-OPHQIDH** — a desktop with no hardware video compression; tests the fallback paths, file transfer, clipboard, unattended |
| Test machine 2 | **DESKTOP-2N6FVGT** — a gaming laptop with NVIDIA graphics; tests picture quality and the codec |
| Shared drive | `\\192.168.1.33\anywhereit` (a WD My Cloud) — holds the **Test Checklist** program staff use to log test results |
| The original health check | `C:\PCHealthCheck` on the build PC — the standalone toolkit the product's health check came from. Its own project; the product's copy is in `healthcheck/` |
