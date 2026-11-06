# Domain, DNS and mail — the authoritative record

Written 2026-09-16 from the recovery carried out on another machine on 15–16 September 2026.
**This file wins on any question of where the domain, its DNS or its mail live.** `GO-LIVE.md` is
the runbook for putting the platform on a server; this is the record of what the names point at and
why. Keep it true when either changes.

---

## 1. What happened, briefly

The platform used to run on an xneelo **cloud VPS** at `154.65.107.158`. That account was disabled
and the VPS is **permanently gone** — on re-inspection the cloud account was empty, and xneelo
support confirmed they hold no copies. Lost with it: the deployed stack, its database, its `.env`
secrets, its TLS certificates and the site files. Not lost: the domain registration, the DNS zone,
and this repository.

**Do not attempt to reach `154.65.107.158`.** The IP is released and will belong to someone else.

Rather than rebuild the server immediately, the domain was brought back up on **xneelo shared
hosting** with a holding page, and mail was consolidated there. That is the state described below.
The server rebuild is a separate job — `GO-LIVE.md` step 1 onwards, unchanged.

## 2. Where everything lives now

| Component | Provider | Detail |
|---|---|---|
| Domain registration | **Afrihost** | `theremotesupportprogram.com` |
| DNS | **Cloudflare** | nameservers `titan.ns.cloudflare.com` / `rosemary.ns.cloudflare.com` |
| Web hosting (holding page) | **xneelo shared hosting** | basic package, R99/month |
| Mail | **xneelo shared hosting** | 7 mailboxes of 100 |
| Application server | **none** | to be rebuilt |

**DNS records are edited in Cloudflare, not in Afrihost ClientZone.** Afrihost is only the
registrar; the nameservers are delegated away. Anything in this repository that still says otherwise
is out of date — say so and fix it.

### The shared hosting

- **Control panel: konsoleH** — a completely separate system and login from the xneelo cloud/VPS
  panel. Domains never appear in the cloud panel at all; that cost time during the recovery.
- IP `197.221.14.40`, server `www40.cpt3.host-h.net`, web root `public_html`
- **Interim test URL:** `theremotesupportprogram.com.www40.cpt3.host-h.net` — reaches the site
  directly on the hosting server, bypassing DNS and browser caching. It mattered most before the
  certificate existed; still useful for confirming an upload without a cached page in the way
- FTP host `www40.cpt3.host-h.net`, credentials under konsoleH → FTP Users
- Package: 15 GB SSD, 100 mailboxes, 3 databases, 1 multiple-domain slot, daily backups kept 14 days

## 3. The DNS records, as verified live

| Type | Name | Content | Priority | Proxy |
|---|---|---|---|---|
| A | `@` | `197.221.14.40` | — | DNS only |
| A | `www` | `197.221.14.40` | — | DNS only |
| A | `mail` | `197.221.14.40` | — | DNS only |
| A | `smtp` | `197.221.14.40` | — | DNS only |
| A | `webmail` | `197.221.14.40` | — | DNS only |
| A | `*` | `197.221.14.40` | — | DNS only |
| MX | `@` | `mail.theremotesupportprogram.com` | 10 | — |
| TXT | `@` | `v=spf1 mx a include:spf.host-h.net ~all` | — | — |
| TXT | `_dmarc` | `v=DMARC1; p=none; rua=mailto:info@theremotesupportprogram.com` | — | — |

### Records deliberately removed — do not reinstate

The domain arrived carrying Afrihost's bundled mail configuration. All of it went when mail was
consolidated onto xneelo. **Their absence is not a fault:**

- MX → `mx7788603929.spe.ucebox.co.za`
- TXT → `v=spf1 include:spf.aserv.co.za +a +mx -all`
- TXT → `mailconf=https://envoy.aserv.co.za/mail/config-v1.1.xml`
- CNAME `autoconfig` and `autodiscover` → `envoy.aserv.co.za`
- SRV `_autodiscover._tcp`, `_imaps._tcp`, `_submission._tcp` → `envoy.aserv.co.za`
- CNAME `ww` → `wwwtheremotesupportprogram.com` (a typo pointing at an unrelated domain)
- a duplicate root A record reading `97.221.14.40` — the leading `1` missing

## 4. Decisions that must survive the rebuild

**Mail is decoupled from the website on purpose.** `mail`, `smtp` and `webmail` have their own A
records at the shared host. When the platform server exists, **only `@` and `www` change.** Mail
configuration, every mail client on every device, and the mail TLS certificate are untouched.
Hanging mail off the bare domain's A record is a trap that surfaces months later as bounced mail.

**Mail stays on xneelo shared hosting permanently.** Do not move it onto the platform server.
Self-hosting mail means owning deliverability, blacklisting, spam filtering and backups, and South
African VPS ranges have poor sending reputation. **Do not cancel the R99/month shared hosting when
the server comes back** — it carries the mail regardless of where the website lives.

**The mail records stay grey-clouded (DNS only) forever.** Cloudflare's proxy carries web traffic
only; it will not pass IMAP or SMTP.

**`@` and `www` are DNS only too, for now,** so Let's Encrypt can validate directly against the
origin. If the orange cloud is ever switched on for them, Cloudflare's SSL/TLS mode must be set to
**Full (strict)** in the same sitting — never Flexible.

**The bare domain must stay grey-clouded once the platform is live.** Cloudflare proxying blocks
raw TCP, which the relay's `wss://` and any future TURN traffic need.

**The wildcard `*` is the tenant-subdomain record.** It currently points at the shared host so stray
names land on the holding page; when the platform server exists it is repointed there and carries
`anyit.theremotesupportprogram.com` and the rest.

## 5. Open items from the recovery

1. **Let's Encrypt — DONE, verified 2026-09-16.** xneelo issued it automatically, as their
   documentation said it would; there was never anything to click. Verified from this machine:
   `https://theremotesupportprogram.com` answers 200 with `ssl_verify_result=0`, issuer
   `Let's Encrypt`, subject `CN=theremotesupportprogram.com`, valid **15 Sep – 14 Dec 2026**.
   `www` is covered too. Renewal is xneelo's to run.
2. **The HSTS wall — gone.** It only ever existed because there was no valid certificate; with one
   in place `https://` loads cleanly. The origin sends no HSTS header of its own.
3. **Force HTTPS — still OFF as of 2026-09-16.** `http://theremotesupportprogram.com` answers 200
   without redirecting, so a visitor who omits the scheme stays on plain HTTP. It is now safe to
   switch on, and the job sheet (`docs/konsoleh-upload.html`, step 6) has the technician do it
   after confirming the padlock. **Switching it on without a working certificate takes the site off
   the internet**, which is why it was deliberately left until now.
4. **Holding page — NOT yet replaced.** Checked live on 2026-09-16: the domain still serves the
   hand-written recovery page (it advertises `info@`, and has no pricing section). `landing/index.html`
   replaces it; see §6 and the job sheet. Whatever `index.html` / `index.htm` is in `public_html`
   has to be renamed first or it wins.
5. **Mail testing.** Sign in at `webmail.xneelo.com`, send out to an external address and reply
   back, to prove both directions. **The `queries@` mailbox on the domain was created by Clint on
   2026-09-16** — it is the only address the holding page advertises, so it has to work before the
   page is public.
6. **DKIM — not configured, and confirmed absent 2026-09-16** (no TXT record under any common
   selector). `docs/dkim-setup.html` and `docs/DKIM-Setup.pdf` are the job sheet for it. The key
   comes from **xneelo**, the record is published in **Cloudflare** — two different companies, and
   that split is where this job usually stalls.

   Live state of the other two, checked the same day: SPF is
   `v=spf1 mx a include:spf.host-h.net ~all`, DMARC is
   `v=DMARC1; p=none; rua=mailto:info.com`. **Those DMARC reports go to
   `info@`, which may not be a mailbox** — the job sheet has the technician check and repoint it at
   `queries@` if not.

   Tightening DMARC to `p=quarantine` comes later, once DKIM has been passing for weeks. Doing it
   while DKIM fails sends our own mail to spam.

### Mail client settings

- Incoming `mail.theremotesupportprogram.com`, port 993 (IMAP) or 995 (POP), SSL on
- Outgoing `smtp.theremotesupportprogram.com`, port 465, SSL on
- Webmail `webmail.xneelo.com`

For the mail the **product** sends, see `GO-LIVE.md` §5 — a separate mailbox configured in the owner
portal, not a mail client.

## 6. Two holding pages exist — know which one is up

`landing/index.html` in this repository is the holding page this project builds and deploys, via
`deploy\Publish-Landing.ps1`. It is built from the platform's own design tokens so the holding page
and the finished site are visibly the same product, and it makes no third-party requests at all.

The page currently sitting in `public_html` on the shared host is a **different, hand-written one**
from the recovery — a dark navy page with a blue accent, self-contained, contact address
`info@theremotesupportprogram.com`. It was written to get something up quickly, not from our tokens.

**Decided 2026-09-16: `landing/index.html` replaces it.** `Publish-Landing.ps1` deploys to a server
over SSH and cannot reach shared hosting, so this is a manual upload through konsoleH —
`docs/konsoleh-upload.html` and `docs/Upload-Landing-Page.pdf` are the job sheet written for whoever
does it. The rendered file to upload comes from `Publish-Landing.ps1 -PreviewOnly`, which fills in
the phone number and address; it is staged at `dist\upload\index.html`.

The old page is renamed rather than deleted, so it can be put straight back.

## 7. What NOT to take from the recovery handover

The recovery document (`D:\remote-support-program-handover.md`, 16 Sept 2026) contains a section
describing the platform as Node.js / Express / PostgreSQL / Redis / Docker with a React frontend and
an Electron client, a planned native engine built on Electron `desktopCapturer`, a C++ input-service
proof-of-concept, and WebRTC transport through Chromium.

**None of that describes this codebase.** It is the plan from *before* this suite was written. What
exists is a .NET 8 / ASP.NET Core platform with WinForms client and technician apps, and our own
capture, input injection, protocol, file transfer and relay — see `HANDOVER.md` §§1–3. The hosting
and DNS facts in that document are good, and are recorded above; its architecture section is history
and must not be acted on.

---

## 8. Quick reference

| Item | Value |
|---|---|
| Domain | `theremotesupportprogram.com` |
| Registrar | Afrihost (registration only) |
| DNS | **Cloudflare** — `titan.ns.cloudflare.com` / `rosemary.ns.cloudflare.com` |
| Shared hosting IP | `197.221.14.40` |
| Hosting server | `www40.cpt3.host-h.net` |
| Interim test URL | `theremotesupportprogram.com.www40.cpt3.host-h.net` |
| Web root | `public_html` |
| Control panel | konsoleH — **not** the xneelo cloud panel |
| Webmail | `webmail.xneelo.com` |
| Dead VPS IP | `154.65.107.158` — released, do not use |
