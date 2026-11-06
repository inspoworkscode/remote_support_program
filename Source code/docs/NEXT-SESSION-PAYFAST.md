# Next session: PayFast and the real website (roadmap 4.8, and the site the holding page hides)

Briefed 2026-10-13. Clint: *"We have now received our payfast verification ... the idea is to make things
as automated as possible ... whatever we build now still needs to remain hidden until we have received the
digital certificate and are ready to go live."* He also brought a report on Anywhere IT's own WordPress site
(below) to be folded in, and asked for the AI assistant to come off the site because of POPIA.

**Every question below was answered "go with your recommendations" (2026-10-13). Do not re-ask them.**

---

## What Clint has done in PayFast (2026-10-13)

- Verified merchant account.
- **Settings → Recurring Billing, saved:** Tokenization **off**; Notify URL
  `https://theremotesupportprogram.com/api/payfast/notify`; IP whitelist `154.65.110.136` (the live server);
  confirmation emails on; PayFast's recurring billing terms accepted.
- **The general "ITN status" switch is OFF and stays off.** PayFast's docs: *"The value provided during a
  transaction overrides the global setting"* — every checkout we build carries its own `notify_url`.
- **Still to do, by Clint:** a sandbox account at https://sandbox.payfast.co.za with a Salt Passphrase set,
  and its Recurring Billing page given the same Notify URL with the **IP whitelist left EMPTY** (the sandbox
  API is called from this PC while building). The live Merchant ID, Merchant Key and passphrase reach the
  server by a script (`Get-Credential` pattern) — **never in chat**.

**If the live server ever moves, the PayFast IP whitelist must move with it**, or every API call
(seat changes, cancel) is refused with *401 Request origin could not be verified*.

## The decisions (Clint, 2026-10-13: "go with your recommendations")

1. **Card at signup, first charge after the 14-day trial.** A subscription with an initial amount of
   **R0.00** and `billing_date` = the trial's end — PayFast supports exactly this (*"if the first
   cycle/period is free"*; the card still goes through 3D Secure, nothing is deducted).
2. **Full signups switch on by themselves** once PayFast's notice confirms the card. **Free signups stay
   approved by hand** (no payment proves anything about them — §102's assumption stands).
3. **Seat changes take effect from the next billing date** — matches the terms' "no part-month refunds" and
   `Pricing`'s own words ("A change shows on the NEXT bill"). The subscription's amount is updated through the
   API; the run date is not touched.
4. **Failed payment: 7 days' grace** — **BUT South Africa's terms say suspension only when "more than 14 days late" (found 2026-10-13, §135): use 14 unless Clint changes the terms.** **DECIDED 2026-10-14, Clint: "go with 14 days" — 14 days' grace, suspended on day 15.** As briefed: emails on days 0, 3 and 6, suspended on day 7 through
   `AdminCompanies` (so §132's `TurnAway` happens). A payment that arrives later lifts it by itself.
5. **Monthly only.** No annual plan for now.
6. **VAT: we are taken as NOT VAT-registered** (South Africa's terms already say no VAT). The hidden
   `/pricing` page says *"VAT excluded"* — wrong; it says no VAT is charged.
7. **The free version is on the site at launch**, beside Full.
8. **US, UK and Europe show their prices with "join the waiting list"** (§84's waiting list); only South
   Africa can buy.
9. **"The digital certificate" is taken to be the Sectigo code-signing certificate** (`docs/CODE-SIGNING.md`;
   the authorisation letter is in `docs/letterhead/`). Go-live waits for signed installers.
10. **Our own website chat (§70) goes on the site in place of the AI assistant.** People, not AI — no new
    POPIA question, and it shows the product working.
11. **Claude drafts the website's words from what the product does; Clint corrects them.** The USPs and
    the keyword list are drafts for him to check (keywords in Google Keyword Planner), not facts.

## What PayFast gives us (read 2026-10-13 from developers.payfast.co.za/docs and /api)

- **Checkout:** an HTML form POSTed to `https://www.payfast.co.za/eng/process` (sandbox
  `https://sandbox.payfast.co.za/eng/process`). Subscription fields: `subscription_type=1`, `billing_date`
  (YYYY-MM-DD), `recurring_amount` (**R5.00 minimum**), `frequency=3` (monthly), `cycles=0` (indefinite).
  `m_payment_id`, `custom_int1..5`, `custom_str1..5` come back in the notice.
- **Checkout signature:** MD5 of the non-blank fields **in the documented order** (NOT alphabetical),
  values URL-encoded with UPPER-case escapes and spaces as `+`, then `&passphrase=…`. A passphrase is
  REQUIRED for subscriptions.
- **The notice (ITN)** is POSTed to `notify_url`; answer 200 at once or it is resent (immediately, 10 min,
  then longer). `payment_status` is `COMPLETE` or `CANCELLED`; `token` identifies the subscription for
  every later API call — **capture it from the notice; the sandbox never shows it.** Four checks, all
  required: the signature; that it came from PayFast (their published ranges — 197.97.145.144/28,
  41.74.179.192/27, 102.216.36.0/28, 102.216.36.128/28, 144.126.193.139 — behind Caddy's socket, read
  `UnixSocket.TrustCaddy` before trusting an address); `amount_gross` is what we expected; and a POST back to
  `/eng/query/validate` answering `VALID`. **The same notice can arrive twice — record by `pf_payment_id`.**
- **A failed renewal sends NO notice.** PayFast retries, emails the buyer, and finally *locks* the
  subscription. So we must notice the ABSENCE of a payment: a daily sweep that, a day after a run date with
  no `COMPLETE`, asks `GET /subscriptions/{token}/fetch` (`status_text`).
- **API** `https://api.payfast.co.za` (sandbox: same host with `?testing=true`): headers `merchant-id`,
  `version: v1`, `timestamp` (ISO-8601), `signature` = MD5 of every header and body field **plus the
  passphrase, sorted ALPHABETICALLY** (the opposite of the checkout's rule). `PATCH …/update` (`amount` in
  CENTS, `run_date`, `cycles`, `frequency`), `PUT …/cancel`, `…/pause`, `…/unpause`, `GET …/fetch`.
- **Card update link** for the customer: `https://www.payfast.co.za/eng/recurring/update/{token}?return=…`.
- PayFast emails the buyer 7 days before a trial ends and before an amount rises (`subscription_notify_buyer`,
  on by default). Ours (the trial notices at 7, 1 and 0 days, `Tenant.TrialNoticesSent`) say the same — keep both; theirs is the legal one.
- **Recurring billing is CREDIT CARD only.** A company that pays by EFT cannot subscribe. Say so on the
  pricing page.
- Sandbox test merchant (public, PayFast's own): 10000100 / 46f0cd694581a / `jt7NOE43FZPn`. Prefer Clint's
  own sandbox account once it exists.

## Keeping it hidden

- Caddy's `@holding` matcher (`deploy/go-live.sh`) already sends `/`, `/features`, `/pricing`, `/support`
  and `/signup` to the holding page. `-RealSite` is never used until Clint says go.
- **A preview for us:** a platform admin (§132) opens `/owner/preview`, which sets an `rsp_preview` cookie
  holding a secret; Caddy's `@holding` becomes "those paths AND no such cookie". Everybody else still sees the
  holding page. go-live.sh writes the matcher; the secret lives in the server's settings.
- `Platform:SignupsEnabled` stays false; a preview-cookie holder may sign up anyway (test companies only).
- **The SANDBOX account is the one on live until go-live** (stored with the keys — `payfast set --sandbox`; see §134). The real sandbox walk runs ON the live server, because
  PayFast's notice needs a public address — no real money can move while it is true.

## The stages

**A. The PayFast core — BUILT 2026-10-13 (HANDOVER §134), not on hardware; `payfastcheck` 56 of 56, PayFast's real sandbox accepting our checkout. PayFast refuses anything more often than MONTHLY.** As built it is `Services/PayfastWire.cs` and `Services/PayfastService.cs`, and the sandbox flag lives WITH the keys, not in config. As briefed: `Services/Payfast/`: settings (merchant id, key, passphrase, sandbox flag — the
secrets in a file on the server written by `deploy/Set-PayfastKeys.ps1`, never in `appsettings`), the two
signature rules (one function each, each with PayFast's own worked example as a test), the notice receiver
`/api/payfast/notify` with the four checks, and records: `Subscription` (tenant, token, status, amount in
cents, next run, card-update link) and `PaymentRecord` (every notice, kept, unique on `pf_payment_id`).
Harness `payfastcheck` with a **fake PayFast** in it (checkout, validate, API) like `mailintakecheck`'s fake
IMAP — a forged signature, a wrong amount, a notice from the wrong address, `INVALID` from validate, and a
repeated notice must all change nothing. Two migrations (SQLite and `PgPlatformDb`, §125).

**B. Choosing a package and paying — BUILT 2026-10-13 (HANDOVER §135), not on hardware; `signuppaycheck` 39. The seat calculator and the free version on the pricing page moved to stage E; an approval refused while the card is at PayFast; approving by hand now emails.** As briefed: Pricing page: Free / Full, region, seats (5 included, up to 10), the
monthly total live — the same `Pricing.BillFor`. Signup carries the choice; Full ends at PayFast's checkout
(R0 now, `billing_date` = trial end, `recurring_amount` = the bill). Back on our return page: *"waiting for
PayFast to confirm"* until the notice lands, then the company is Trial and the owner's invitation (§73) goes.
A checkout abandoned leaves a Pending company that a sweep removes after a day. **Upgrade (§107) uses the same
checkout**, and is refused to an upgrade not paid for.

**C. Keeping the amount true — BUILT 2026-10-14 (HANDOVER §136), not on hardware; `signuppaycheck` 49. Card update link on Billing came in stage B.** As briefed: Every accepted seat change (`SeatAgreement`, §83) PATCHes the amount; a call
that fails is retried by the sweep, and the Billing page says so until it lands. Billing shows: the plan, the
amount, the next charge date, the card-update link, and every payment. A receipt email from us per payment
("Invoice", no VAT).

**D. When it goes wrong, and cancelling — BUILT 2026-10-14 (HANDOVER §137), not on hardware; `signuppaycheck` 70. 14 days' grace, not 7 (the terms); reminders at 1, 7 and 13 days; service ends the day before the unpaid charge.** As briefed: The daily sweep (no notice for a failure — see above): 7 days'
grace, days 0/3/6 emails to the owners, suspend on day 7, lifted by the next `COMPLETE`. **Cancel
subscription** on Billing (owners only): `PUT …/cancel`, service runs to the end of the month paid for, then
the company is Closed — the privacy policy already promises this button. A `CANCELLED` notice (the customer
cancelled at PayFast) is treated the same. The control dashboard (§133) shows each company's payment state.

**E. The real website.** Every page under `Pages/` that the public will see, rebuilt with the report in mind:
- **Remove** the AI assistant card (never built) and the "Time-window access" card (never built either).
- **Several pages, not one long one** (report 4): Home; a page per feature — remote control and unattended
  access, tickets and the workshop, health checks, knowledge base, website chat and browser support, security
  (end-to-end encryption, two-factor, POPIA); Pricing (Free and Full; card only; no VAT); About and contact;
  Terms and Privacy as they are.
- **Menus that say what we do** (report 5) — "Remote support", "Ticketing", "Pricing", not "Services".
- **SEO** (reports 1, 7): a title and description per page, one `h1`, headings from the keywords, alt text
  on every image, canonical links, Open Graph, schema.org `SoftwareApplication` and `Organization`,
  `sitemap.xml`, `robots.txt`.
- **Speed** (report 2): under 3 s, measured — no web fonts we do not need, images sized and compressed,
  nothing render-blocking.
- **Zero validator errors** (report 3), as a harness (`sitecheck`) that fails the build on one — run locally,
  never by sending the hidden pages to an outside service.
- **USPs** (report 6), drafted for Clint: everything built in-house, nothing bought in; tickets and remote
  support in ONE product; priced per seat, not per machine or session; your own brand on everything; built in
  South Africa, POPIA-first. **Keywords** (report 7): a draft list for Clint to check.
- Our website chat bubble (§70) on every page.
- Report 8 (new address, Google listing) is Anywhere IT's own site, not this one.

**F. Go-live (only when Clint says, after the certificate).** Live credentials (`deploy\Set-PayfastKeys.ps1 -Live`, which makes the server live),
signups on, `Publish-Platform.ps1 -RealSite`, one real payment of the smallest amount and refunded.

## Traps already known

- Two different signature rules (checkout: documented order; API: alphabetical). Mixing them gives
  *"Merchant authorisation failed"* and nothing more.
- Amounts: the checkout takes RAND with decimals (`500.00`); the API takes CENTS (`50000`).
- `recurring_amount` minimum R5.00 — any company billed less (none today) cannot subscribe.
- `NotBilled` companies (Anywhere IT) never go near PayFast.
- PayFast's timestamps default to GMT+2; send an explicit offset.
- Only ZAR. The other regions need another processor (not now).
