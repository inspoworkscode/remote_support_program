# The website — what it says, why, and what Clint must check

Stage E of `docs/NEXT-SESSION-PAYFAST.md`, built 2026-10-14 (HANDOVER §138). The words are **Claude's draft, for Clint to
correct** (his answer 11). Nothing here is public until go-live: every page is behind the holding page (`deploy/go-live.sh`),
and `/owner` → *Preview* shows it to one browser.

## The pages

| Address | What it is for | Main search words |
|---|---|---|
| `/` | Home: what it is, why choose it, how it works, the free version | branded remote support, remote support software, IT company |
| `/remote-support` | Attended and unattended remote support, everything in a session | remote support software, unattended access, remote desktop |
| `/ticketing` | Workshop, call-out, walk-in, recurring, quotes, billing queue | ticketing software, job card, IT helpdesk, workshop |
| `/health-checks` | The monthly health check and maintenance | computer health check, PC maintenance report |
| `/knowledge-base` | The knowledge base | IT knowledge base |
| `/website-chat` | Website chat, contact form, browser screen sharing | website chat, live chat for IT company |
| `/security` | Encryption, sign-in, POPIA | secure remote support, POPIA |
| `/features` | Everything, with links to the pages above | features |
| `/pricing` | Full and free, the calculator, paying, regions | remote support pricing South Africa |
| `/about` | Who we are, contact | — |
| `/support` | Help and common questions | — |
| `/privacy` | The privacy policy (`Terms/privacy-ZA.txt`, the holding page's own text) | — |
| `/terms/ZA` | Terms and refunds (already there) | — |

`Services/SitePages.cs` is the list; `/sitemap.xml` and `/robots.txt` are made from it.

## The USPs (on the home page — Clint, correct these)

1. **Remote support and tickets in one** — a session opens its own ticket; workshop, call-outs, quotes and billing in the same place.
2. **Your brand, not ours** — customers never see our name.
3. **Priced per person, not per computer** — R500 for five people; every computer and session included.
4. **Your customer stays in charge** — consent, their unattended switch, their own password.
5. **Secure by design** — end-to-end encryption, two-factor for everybody, data in South Africa.
6. **Built by an IT company** — we use it ourselves; everything is our own work, nothing rebadged.

## Keywords to check (report item 7)

A draft list for **Google Keyword Planner** (free with a Google Ads account) — look up each for South Africa, keep the ones
people actually search, and send back the winners: they become page titles, headings and first sentences.

- remote support software · remote support software South Africa · remote desktop software for IT support
- unattended remote access · remote access software for business · TeamViewer alternative · AnyDesk alternative
- IT helpdesk software · IT ticketing system · job card software · computer repair shop software · workshop management software
- MSP software South Africa · software for IT companies · managed service provider tools
- PC health check report · computer maintenance report
- live chat for website · website chat South Africa

**"TeamViewer alternative" and "AnyDesk alternative"** are probably the most valuable — people searching them are ready to
buy. A page each (`/teamviewer-alternative`) is the usual way, and is a decision for Clint (naming a competitor's trademark
in comparison is normal practice, but say only what is true of them).

### What South Africa actually searches — Google Trends, 2026-10-27

Keyword Planner needs a finished Google Ads account (Clint's was never finished), so these come from **Google Trends,
South Africa**: relative interest, the biggest term in each comparison = 100, past 12 months (five years for the small ones).
Trends rounds anything under 1% of the biggest term down to 0, so a 0 means "too few to count", not "none".

**The big words** (12 months, against each other):

| Term | Interest | What the searches around it are |
|---|---|---|
| it support | 75 | mostly *jobs*, *salary*, *what is IT support*, students; *IT support services* and *IT support Cape Town* are the buyers |
| anydesk | 56–77 | **2.5× TeamViewer** in South Africa |
| remote desktop | 26 | *remote desktop chrome*, *windows remote desktop*, *remote desktop download* — people looking for a free tool |
| teamviewer | 22–31 | |
| help desk | 13 | mostly *help desk jobs* and university help desks |
| remote access | 12 | *remote access to computer*, *remote access software* (53 of its related searches), *remote access app*, *windows remote access* |
| remote support | 11 | **mostly jobs** (*remote support jobs*, *remote jobs South Africa*); *remote support software* is 13 of its related searches |
| job card | 9 | **the South African word for it**: *job card template*, *job card online*, *job card app*, *workshop job card*, *job card software* |
| live chat | 54 vs *website chat* 8 | people say *live chat*, not *website chat* |
| work from home / remote work | 60 / 27 | heavy with job hunting; *access work computer from home* too small to count |

**The phrases** (five years, against each other — all small): *ticketing system* 4, *IT support software* 3, *remote access
software*, *remote desktop software*, *TeamViewer alternative*, *MSP software*, *workshop software*, *PC health check*, *computer
maintenance* 1 each; *remote support software*, *AnyDesk alternative*, *job card software*, *helpdesk software*, *computer repair
software*, *unattended access* 0. Rising names around remote access: **RustDesk, Parsec, Tailscale**.

**What it says for the pages** — **1 to 5 DONE 2026-10-27 (HANDOVER §171)**, with the site widened beyond IT companies (below):

1. **Ticketing → lead with "job card".** It is what South Africans call it, and *job card app / online / workshop job card / job
   card software* are its own searches; *ticketing system* and *help desk* barely register for buyers. A title like *"Job cards
   and ticketing for IT companies"*.
2. **Remote support → add "remote access".** *Remote support* alone is mostly job seekers; *remote access software* and *remote
   access to computer* are buyers. A title like *"Remote support and remote access software"*.
3. **AnyDesk before TeamViewer** wherever a competitor is named (2.5× the searches here). If one comparison page is made, make it
   AnyDesk's.
4. **Website chat → say "live chat"**: *"Live chat for your website"*.
5. **Remote workers**: keep *work from home*; add *remote access to your work computer* in the first lines (it is already in the
   description).
6. **Don't lead with "IT support" or "help desk"** in a title: big numbers, but students and job hunters.
7. Precise numbers per month still need Keyword Planner, once the Google Ads account is finished.

### Not only IT companies (2026-10-27, HANDOVER §171)

Clint: *"other types of industries also require this type of software."* The home page leads with **businesses that look after
other people's computers and jobs** and names, IT companies first: **software and point-of-sale suppliers, in-house IT teams
(schools, practices, accounting firms, franchises, offices with branches), computer and cellphone repair shops, call-out businesses
(office equipment and printer dealers, CCTV and alarm installers)** and remote workers — each card saying only what is built for
them. Keep new words true for all of them; *About* still says WE run an IT company, which is true.

## Claims to check before go-live

Every sentence on the pages was written from what is built (HANDOVER §1–§137). These are the ones a person should confirm:

- **"We use it every day at our own IT business"** (home, about) — true of Anywhere IT's internal testing; is it how Clint wants to say it, and should Anywhere IT be named?
- **"Your data is kept on servers in South Africa … backed up encrypted every night"** (security) — true of xneelo and `rsp-backup`; the terms say the same.
- **Health checks: "Security: antivirus, firewall, encryption"** — the toolkit's section 6 is "security posture"; check it reports all three.
- **Health checks: "emailed from your own address with your own logo"** — as built (§17, §53).
- **"Nothing is closed until it has been invoiced or marked as not to be"** (ticketing) — the workflow's rule as Clint wrote it.
- **The free version: "Signups for the free version are approved by us first"** (pricing) — the assumption of §102; still wanted?
- **Cookies — DONE 2026-10-14 (HANDOVER §140).** The privacy policy (dated 14 October 2026, `Terms.Version` 2026-10-14) now says the
  site sets only the cookies it cannot work without — signed in, the weekly sign-in code, signed out elsewhere, form protection — and
  no analytics, trackers or advertising; and that the browser keeps the currency, light or dark and a chat for the visitor alone.
  Strictly necessary cookies need no consent banner under POPIA, so there is none. **If anything is ever added that tracks or
  advertises, this paragraph and a banner come first.**
- **The screenshots (HANDOVER §140)** show a made-up IT company, *Coastline IT*, and made-up customers (Zenith Accounting, Harbour
  View Dental, Milkwood Guest House, Protea Legal) — none of them real, nothing of Anywhere IT. Remake them with `tools/siteshots`,
  never by capturing a real screen. Check you are happy with the names.

## Still to do

- **Reviews from companies** — BUILT 2026-10-14 (HANDOVER §139): asked a week in, approved on `/owner/reviews` before they
  show on the home page, which shows nothing until the first is published.
- **Our own chat bubble — DONE 2026-10-27 (HANDOVER §169)**: `Publish-Platform.ps1 -RealSite` sets `Platform:SiteChatSlug` to
  `anywhereit` (`-SiteChat` to change it) and opens signups. On our site the bubble says *The Remote Support Program* in our purple;
  Anywhere IT's people answer it, exactly as on Anywhere IT's own site.
- **The W3C checker itself** once the pages are public (go-live): `https://validator.w3.org/nu/?doc=https://theremotesupportprogram.com/`.
  `tools/sitecheck` uses an HTML5 parser's errors meanwhile — no Java or Node on this PC, and the pages are not to be sent to
  an outside service while hidden.
- **Speed on a real phone line** once public (PageSpeed Insights). `sitecheck` holds every page to ≤ 150 KB with its
  stylesheet, nothing from another website, and ≤ 500 ms locally.
- **Report item 8** (new address, Google Business listing) is Anywhere IT's own website, not this one.
