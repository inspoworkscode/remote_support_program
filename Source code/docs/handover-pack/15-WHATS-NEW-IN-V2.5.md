# 15 — What is new in V2.5

**V2.4** of this pack was written on **27 October 2026**. **V2.5** was written on **28 October 2026** and is **the last
version before the product goes public on 1 November 2026** (the real website, signups and live card payments). Little
changed in the code in that day; what changed most is the pack itself.

## The pack

| What | In short |
|---|---|
| **The guide is in the zip** | `The Remote Support Program - Guide.pdf` sits at the top of the zip, beside this pack's start page: the 30-page guide for anybody thinking of using or investing in the product (what it is, remote support, jobs and tickets, the company side, the prices). It is the same file as `docs/brochure/` in the source code, printed from `brochure.html` by `docs\brochure\Print-Guide.ps1` |
| **Documents 00, 02 and 07** | Brought up to 28 October |

## The product

| What | In short | Record |
|---|---|---|
| **The pricing page's country switch** | Choosing another country changed only the full version's price; the Basic price, its extra computer and the extra seat still said South Africa's. All four figures now come from the one price list (`Services/Pricing.cs`) for each country | §173, ED (933) |
| **Job types shown to an owner once — really once** | The dashboard was meant to send an owner to Job types once. It actually kept sending them there on every Dashboard click until they pressed **Save** or **Keep everything**, so an owner who just looked and clicked away could not reach the dashboard. Now the trip itself is remembered (`Tenant.JobTypesShownUtc`, migration `JobTypesShown`). `jobtypepage` 34 of 34 on both databases (32 with the old rule) | §167 |

**Not yet on the live server: the Job types fix.** It is committed and goes out with the next signed
`Publish-Platform.ps1`, which applies its migration. After that deploy, an owner already sent to Job types goes there once
more, then never again. (The pricing fix was in the build of 27 October, 17:20, made after it was committed; the page
itself stays behind the holding page until 1 November.)
