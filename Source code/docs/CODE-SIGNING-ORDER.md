# Ordering the certificate — do this in order

Written 2026-09-23. **Revised 2026-09-28 and again 2026-10-02. THE ORDER IS PLACED AND PAID
(2026-10-02)** and the token is bought and in transit; what is left is Sectigo's validation and the
key generation. `CODE-SIGNING.md` holds the reasoning behind every choice; you do not need to read
it to follow this.

**What we are buying:** a **Sectigo (Comodo) OV code-signing certificate, 2-year term**, from
LeaderSSL at roughly R9,900–10,000 excl. VAT, on a **YubiKey 5 NFC FIPS 140-3** token bought
separately from Cyber Connect (Randburg) and couriered to Knysna — expected the week of
2026-10-05.

---

## Placing the order — DONE AND PAID 2026-10-02

**Clint placed the order and paid it on 2026-10-02.** What follows is kept because it is the record
of what was ordered and what was typed into the form, which matters if Sectigo queries anything
during validation — and because the whole thing is done again at renewal.

**Confirmed and matched by LeaderSSL the same day:** proforma **2611897**, our reference
**80819755/2611897**, invoiced at **0% VAT** (correct — the company is not VAT registered).

**Where it stands now:** paid, waiting on LeaderSSL to send the continuation link and the `ykman`
commands (Nick's step 4), and waiting on the YubiKey to reach Knysna. **Nothing is blocked on us
except telling Nick when the token arrives.**

**The token did not need to be in hand.** LeaderSSL (Nick, 2026-09-28, reconfirmed
2026-10-02): the order can be placed and paid first, and the device is needed only at the CSR and
attestation step, which comes after payment.

**Use this link — it pre-selects the right three things:**

<https://www.leaderssl.co.za/lead/new?days=730&product_id=49&kind_of_delivery=use_my_token>

Check the panel on the right before typing anything:

| Field | Must read |
|---|---|
| Validity period | **2 years** |
| Validation type | **OV** |
| Shipping method and USB token | **My own YubiKey token** |

**Ignore the "(5 series FIPS 140-2)" in that dropdown.** Nick has confirmed in writing it is stale
wording for the older generation and does not mean a 140-2 device is required.

**The price moves daily.** It is set in euros, so the rand figure drifts — R9,942.42 on 2026-09-28,
R9,907.78 on 2026-10-01, R9,963.58 on 2026-10-02. The proforma invoice fixes the actual amount.
Payment is processed in **euros**, so expect a conversion and a bank fee on top.

### What to enter

The form is three steps: **Contact Details → Billing Details → Finish**.

| Field | What to put |
|---|---|
| Email (step 1) | `info@theremotesupportprogram.com` |
| Organisation | **The Remote Support Program (Pty) Ltd** — the full registered name, including "(Pty) Ltd" |
| Registration number | **2026/737434/07** |
| Contact name | **Clint Vorster, Director** — a named individual; the callback asks for that person |
| Address | the registered address exactly as on the CIPC certificate |
| Telephone | **`+27 78 131 5618`** format — no leading zero after the country code |

**The organisation name is the one field with no margin for error.** Sectigo validates it against
CIPC, and it becomes the publisher string in every UAC prompt on every customer machine. "The Remote
Support Program" without the suffix is not the registered entity.

**The telephone format matters more than it looks.** `+27 078 …` is malformed — a country code and a
trunk zero cannot both appear. The phone callback is the step most likely to stall validation, so a
number the CA cannot dial is the expensive kind of typo.

**Nothing is charged at Finish.** The order is reviewed first, then a proforma invoice is issued,
then you pay. So the amount can be checked before any money moves.

---

## What happens after the order — Nick's six steps

1. You place the order through the website
2. LeaderSSL review it and issue a **proforma invoice**
3. You pay
4. They send a **link to continue** plus the exact **`ykman` commands** for slot 9a, the CSR and the
   attestation
5. You follow the link and enter the organisation and contact details; they review and approve
6. Sectigo performs the OV validation and issues; you install the certificate into the **same PIV
   slot 9a** where the key was generated

**Tell Nick when the YubiKey arrives** — he has asked to be told, and that is what triggers step 4's
instructions. **Do not generate anything on the key before then**; he said explicitly there is no
need, and improvising the key generation is how you end up with a key Sectigo will not accept.

---


## What the two-year term actually buys — settled 2026-09-28

Nick answered this twice, correcting himself, so here is the final position.

**The re-issue is free.** *"The required certificate re-issuance during the purchased two-year term
is included in the two-year price. There is no additional certificate charge."* Good — that was the
financial risk and it is closed.

**But the first certificate is only ~400 days, not 459.** Nick's correction: Sectigo structures
multi-year code-signing orders so the first certificate runs about **400 days**, and a second
certificate covers the remainder of the term.

**And it probably does NOT save you a validation.** This is the one that matters. Nick: *"I would
not recommend choosing the two-year term on the assumption that it avoids a second validation."*
Validation data has its own reuse period, and Sectigo may have to reconfirm company details and
redo the callback before issuing the second certificate.

*(His answer was framed around EV and we are buying OV, so it is hedged rather than definitive. But
the reuse period for validation data is close enough to 400 days that a re-validation is likely
either way. Do not plan around avoiding it.)*

**So the two-year was chosen to avoid an annual identity check, and it probably will not.** The
decision still stands, on what is left:

| Over 24 months | 2-year term | Two 1-year terms |
|---|---|---|
| Cost | **R9,942.42** | R11,714.52 |
| Certificates | 2 (second one free) | 2 |
| Purchases to make | **1** | 2 |
| Validations | 1 or 2 | 2, certainly |

**R1,772 cheaper, one purchase instead of two, and at worst the same validation effort.** That is a
thinner win than we expected but it is still a win, and there is no version of this where the
one-year comes out cheaper. Buy the two-year.

---

## Step 1 — Buy the token yourself — DONE 2026-09-30

**Bought from Cyber Connect** (Randburg, `cyberconnect.shop`), R2,242.50 plus R115 delivery,
collected in Johannesburg and couriered to Knysna. Kept below is how it was chosen, because the
choice has to be made again at every renewal and the traps do not go away.

**Cyber Connect is NOT one of Yubico's three authorised South African partners** (Global Micro
Solutions, The Kernel Afrika, Naspers — checked on Yubico's own partner locator 2026-09-29), and
they did not answer calls or WhatsApp before the sale. That is a known, accepted risk rather than an
oversight: **Sectigo requires a YubiKey attestation certificate chaining to Yubico's own root, so a
counterfeit cannot produce a valid one and would be caught at CSR time**, before anything is signed
or shipped. Run `ykman info` on it the day it arrives rather than waiting for that.

**Settled 2026-09-28: LeaderSSL does not sell YubiKeys.** Nick, twice, plainly: *"We do not supply
or ship YubiKey tokens at all... they must always be purchased independently from Yubico or another
supplier of your choice."*

The "USB token + International Shipping" on their one-year card is **Sectigo's own provisioned
token, not a YubiKey**, and it is not offered on the multi-year product at all. So there is no
one-stop shop. The token is a separate purchase from a separate supplier, and appendices B and C
exist to find one.

**Buy: `YubiKey 5 NFC FIPS (140-3)` — USB-A, $88.**

- <https://www.yubico.com/product/yubikey-5-fips-140-3-series/yubikey-5-nfc-fips-140-3/>
- The whole 140-3 range: <https://www.yubico.com/products/yubikey-fips/>
- **Do not use** `yubico.com/store/yubikey-5-fips-series/` — that page sells the **140-2** keys.
- USB-C only build PC? Buy **YubiKey 5C NFC FIPS (140-3)** instead, same $88.
- **Not a Nano** — those are made to live permanently in the port, which is wrong for a signing key.
- Any 140-3 model works **provided it supports PIV**. Both of the above do.

**The trap:** the model names are *identical* across both generations. A "YubiKey 5 NFC FIPS" is
both the old 140-2 key and the current 140-3 one, and **Yubico still sells both, on two different
pages**. Say **140-3** explicitly when ordering or you may be sent old stock.

**Where:**

1. **Kernel Afrika** (`info@kernelafrika.com`), Yubico's official African distributor — *if* they
   can get 140-3; their catalogue shows the standard range only. Local delivery, ZAR invoice, no
   customs. **Appendix B.**
2. **Any other South African partner Yubico names** — **Appendix C** asks them who.
3. **Yubico direct** — $88 plus international shipping, expect SA import VAT. Budget
   **R2,000–2,500 landed**.

Someone must be present to sign for it.

---

## Step 2 — While the token is in transit

### 2a. The documents — COMPLETE as at 2026-10-02

All of them exist and are on Clint's Desktop. Keep them in one folder; Nick asks for them when
Sectigo reaches the organisation validation.

| File | What is actually in it |
|---|---|
| `CoR15_1_A_<n>.pdf` | **three documents in one file** — see the trap below |
| `Beneficial Ownership_<n>.pdf` | CIPC Beneficial Ownership certificate, 21/09/2026 |
| `STMG - THEREMOTESUPPORTPROGRAM - 22-09-2026.pdf` | the attorney's letter |

**The trap, and the reason this section exists.** The file named `CoR15_1_A` is *not* only the
Memorandum of Incorporation. It holds:

- page 1 — CIPC's "what to do after registering" covering letter
- **page 2 — FORM COR 14.3, the REGISTRATION CERTIFICATE**
- pages 3–8 — the COR15.1A Memorandum of Incorporation
- **pages 9–10 — the DISCLOSURE**

Both of the documents Sectigo actually wants are inside a file named after the one it does not.
Nobody looking for a "registration certificate" or a "disclosure certificate" would open it. This
cost two wrong readings on 2026-10-02 before Clint pointed it out.

**What the CIPC Disclosure states**, and it matters because it must agree with the order:

| | |
|---|---|
| Enterprise name | THE REMOTE SUPPORT PROGRAM (PTY) LTD |
| Registration number | 2026/737434/07 |
| Registration date | 14/09/2026 |
| Enterprise status | **IN BUSINESS** |
| **Address of registered office** | **19 Tide Street, Knysna Central, Knysna, Western Cape, 6571** |
| Directors | Lesch, Jason — ACTIVE · Vorster, Clint — ACTIVE, both from 14/09/2026 |

**That the registered office on CIPC's own record is the same address given on the order is the
single most useful fact here**, and it closes the risk this section used to warn about: a company
registered three weeks earlier is in CIPC and nowhere else, so an address the registry did not
confirm would have been the thing to stall validation.

Still absent, and probably not needed: a utility bill, lease or bank statement in the company's
name. With CIPC itself naming 19 Tide Street as the registered office, a separate proof of address
is much less likely to be asked for — and a three-week-old company would struggle to produce one.

### 2b. The attorney's letter — ALREADY IN HAND

Nick confirmed Sectigo accepts a letter from a qualified attorney or practising accountant
confirming the company's details and method of communication, provided the professional is
verifiable in their own registry and may be telephoned to confirm it is genuine. **His advice was
not to commission one upfront** — Sectigo asks only if it cannot verify independently, and LeaderSSL
sends the correct template at that point.

**One already exists**, written before any of this came up: Spencer Tarr Malan Geyer Inc (Sandton),
22/09/2026, "To whom it may concern", from Spencer Jon Tarr BA LLB, a director of the firm, stating
he has acted for Clint Vorster for over a decade, that the company is duly registered, and that it
conducts business from 19 Tide Street, Knysna Central. **It agrees with CIPC on the address, the
directors, the company telephone number and the company email.**

**Know which number is which.** The letter's header carries the COMPANY's contacts —
`+27 78 131 5618` and `info@theremotesupportprogram.com`, both matching the order. Its body lists
the directors' PERSONAL numbers and `clint@` / `jason@` addresses, which are not the method of
communication Sectigo is verifying. A callback should come to the header number.

**Write the telephone number as `+27 78 …`, never `+27 078 …`** — a country code and a trunk zero
cannot both appear, and an unreachable number is how this stalls.

### 2b-ii. Sectigo asked for an authorisation letter and a legal opinion — 2026-10-12

Natallia (LeaderTelecom) asked for an **Authorisation / Power of Attorney** letter, uploaded in
Validation Manager under *Upload Documents*. Sectigo has **no template**. Hers is the list: company
letterhead, date and validity period, the exact CIPC name and number, registered address, the
representative's name and position, and the powers (apply for and manage code signing
certificates: issuance, renewal, revocation). It must be signed as the MOI says; **two signatures
avoid a round trip**, so both directors sign. Being the whole board, it is also the board's
resolution.

She also wants a **legal opinion letter from our attorney**: validly incorporated and in good
standing, the signer a duly appointed director, with authority to sign. Spencer Tarr's 22/09
letter covers registration and the address, but **not good standing or authority**, so he needs to
write a supplementary one. The draft for him is in the Claude doc *Code signing: authorisation and
legal opinion letters*.

The letter is `docs/letterhead/Sectigo Authorisation Letter.docx` (valid to 31 December 2028, so
the term's second certificate is covered); the blank `RSP Letterhead.docx` is beside it. Both were
built by hand-written WordprocessingML, not in Word. **Do not drive Word by COM on this PC**: a
background instance hangs on opening any file, and one attached itself to Clint's open Word and
locked the letterhead.

**Both letters were uploaded on 2026-10-13. On 2026-10-14 Sectigo telephoned Clint and Spencer Tarr
and verified everything, and the certificate was ISSUED the same day.** It was collected on
2026-10-15 (a .p7b bundle with no extension, `CollectCCC`), put on the YubiKey and proved by
`tools\Install-SigningCert.ps1` — see `CODE-SIGNING.md`, "SIGNING PROVEN". Copies of the bundle
and the certificate are in `Desktop\Code signing`; they are public halves, not secrets.
**Windows needed Yubico's Smart Card Minidriver** for the ECC key.

### 2c. The Windows SDK — DONE 2026-10-08

Installed with:

```bash
winget install --id Microsoft.WindowsSDK.10.0.26100 -e --accept-package-agreements --accept-source-agreements
```

and **then checked rather than assumed** — `Find-SignTool` picks up
`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe` and the binary runs.
The winget download is only 1.4 MB because it is a bootstrapper; the tools arrive behind it, so
"Successfully installed" is worth verifying.

`ykman` was installed the same day for the key generation
(`winget install --id Yubico.YubiKeyManagerCLI -e`), at
`C:\Program Files\Yubico\YubiKey Manager CLI\ykman.exe`. **It is not added to `PATH` for shells that
are already open** — start a new terminal, or call it by full path.

---

## Step 3 — When the YubiKey arrives

The private key must be **generated inside the YubiKey** and must never be created on the computer
and imported. Sectigo requires a YubiKey-generated key plus a hardware attestation proving it.

Nick's sequence — and **LeaderSSL will send the exact `ykman` commands**, so ask for them rather
than improvising:

1. Install Yubico's PIV tooling / `ykman`
2. Generate the private key in the YubiKey PIV application, **slot 9a**
3. Generate the CSR from that key
4. Generate the YubiKey **attestation certificate**, proving the key was created inside the device
5. Send LeaderSSL/Sectigo the CSR and the attestation
6. Sectigo validates the organisation and the applicant
7. Sectigo issues the certificate
8. **Import the issued certificate into the same PIV slot 9a** on the YubiKey
9. Sign

---

## Step 4 — Validation

The CA checks the business, never the software. The product being unfinished is irrelevant.

- **They will telephone.** Keep the phone on into the evening — the call may not come from Amsterdam
- **Answer as The Remote Support Program**, not as Anywhere IT
- Watch **info@theremotesupportprogram.com** daily — every request and the collection instructions
  arrive there and nowhere else

---

## Step 5 — The first signed build

With the key plugged in, check Windows can see the certificate:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, NotAfter, Thumbprint
```

You want **The Remote Support Program** and an expiry date. That is exactly what `build.ps1` looks
for — nothing in the build needs changing.

Then:

```bash
powershell -ExecutionPolicy Bypass -File build.ps1 -Installers -RequireSigning
```

`-RequireSigning` makes the build **fail rather than produce unsigned files**, and it is checked
before `dist\` is cleaned, so a failure costs nothing.

The token will ask for its PIN — the build signs thirteen files in nine batches. Yubico's tooling
can cache the PIN; set that up once rather than typing it nine times per release.

---

## What it costs

| | ZAR, excl. VAT |
|---|---|
| YubiKey 5 NFC FIPS (140-3), landed | ~2,000–2,500 |
| Sectigo OV code signing, 2-year term | 9,942.42 |
| **Total** | **~12,000–12,500** |

The token is a one-off you keep and re-use at every renewal from here on.

---

## Things that will trip you up

- **FIPS 140-3, not 140-2** — and the names are identical, so say the number explicitly when ordering.
- **A standard YubiKey 5 is not a YubiKey 5 FIPS.** Kernel Afrika stocks the standard one at R1,300.
- **The key is generated on the token, never on the PC.** A key made on the computer and imported
  will be rejected.
- **`info@` must have a human name behind it** on the order.
- **Do not lose the token or forget its PIN.** There is no recovery — a lost key means buying the
  certificate again. Put it somewhere deliberate on day one and write the PIN down offline.
- **The Windows SDK is free and unrelated to the purchase.** Do it now.

---

# The emails — ready to send

Fill in anything in `[square brackets]`.

**LeaderSSL is answered and settled** — Appendix A is a short acknowledgement to send with the
order, not a blocker. **Appendices B and C are now the live ones**, because LeaderSSL will not
supply the token and somebody has to.

---

# Appendix A — reply to LeaderSSL (send with the order)

**To:** `info@leaderssl.com`
**Subject:** Re: Sectigo Code Signing OV, 2-year — order placed

Good day Nick

Thank you — that is clear, and I appreciate the correction on the 400-day first certificate.

I have placed the two-year order using the "My own YubiKey token" option, and will source a
**YubiKey 5 FIPS 140-3** separately as you advise. I will send the CSR and the device attestation
once it arrives.

One clarification, purely so that I can plan: your note on validation referred to **EV** Code
Signing. Our order is **OV**. For an OV certificate, does the same apply — that Sectigo may need to
re-validate the organisation before issuing the second certificate at around 400 days — or is the OV
validation reuse period long enough to cover it? I am not expecting to avoid it, I would just like
to know what to budget for.

I would also be grateful for the exact `ykman` commands for generating the key in PIV slot 9a and
producing the attestation, whenever convenient — I would rather follow your steps than improvise.

Kind regards
Clint [surname]
The Remote Support Program
[your telephone number]

---

# Appendix B — Kernel Afrika (correction, send now)

**To:** `info@kernelafrika.com`
**Subject:** Re: YubiKey 5 FIPS — correction, I need the 140-3 version

Good day

A correction to my previous email. I asked about the **FIPS 140-2** models; I should have asked for
the **FIPS 140-3** version, which is the current generation. Yubico's FIPS 140-2 certification moved
to the Historical List in September 2026, and our certificate authority has advised us to buy 140-3.

What I need is:

- **YubiKey 5 NFC FIPS (140-3)** — USB-A + NFC (first choice)
- **YubiKey 5C NFC FIPS (140-3)** — USB-C + NFC (alternative)

Any model in the YubiKey 5 FIPS 140-3 series is suitable provided it supports the PIV application.

**Please note the model names are identical between the two generations**, so could you confirm
specifically that anything you quote is the **140-3** version and not remaining 140-2 stock.

Could you let me know:

1. Whether you can supply these, from stock or on order
2. The price in ZAR including VAT
3. The lead time for delivery to [your town/city]
4. Whether delivery is by courier and requires a signature

The key will hold the private key for an Authenticode code-signing certificate through its PIV
applet, which is why the FIPS-validated version is required — the standard YubiKey 5 NFC listed on
your site will not be accepted by the certificate authority.

We are a registered South African company and can issue a purchase order if you need one. This is
time-sensitive, so even a "no, we cannot get those" would be useful to know quickly.

Kind regards
Clint [surname]
The Remote Support Program
[your telephone number]

---

# Appendix C — Yubico (send now)

**To:** via <https://www.yubico.com/support/contact/> — or `sales@yubico.com`
**Subject:** YubiKey 5 FIPS 140-3 for code signing — South African availability

Good day

I am buying a YubiKey to hold the private key for an Authenticode code-signing certificate, and our
certificate authority requires a **FIPS 140-3** validated device with PIV support. I am looking at:

- **YubiKey 5 NFC FIPS (140-3)** — USB-A + NFC
- **YubiKey 5C NFC FIPS (140-3)** — USB-C + NFC

We are in **South Africa**, and I would be grateful for help with four things.

**1. Which of your South African partners stock the FIPS 140-3 series?** Kernel Afrika is listed as
your African distributor, but their online catalogue shows the standard YubiKey 5 range only. If
another partner carries the FIPS line locally I would prefer to buy locally.

**2. If no local partner stocks them, can Yubico ship direct to South Africa?** Please confirm the
shipping cost, the lead time, and whether duties and taxes are collected at checkout or on delivery.

**3. Are there any export restrictions** on the FIPS 140-3 models to South Africa that I should know
about before ordering?

**4. A sanity check on the product:** please confirm the YubiKey 5 NFC FIPS (140-3) supports
generating a private key **inside the device in PIV slot 9a** and producing a **hardware attestation
certificate** for that key. Our CA requires the attestation as proof the key was generated on the
device rather than imported.

This is time-sensitive, so any pointer to the fastest route to a 140-3 key in South Africa would be
much appreciated.

Kind regards
Clint [surname]
The Remote Support Program
[your telephone number]

---

## When the replies come back

**LeaderSSL is settled.** No token from them, re-issue free, first certificate ~400 days, validation
probably repeats. Nothing further is blocking the order.

Outstanding, and all about the token:

- **If Kernel Afrika can get 140-3** — buy from them. Local, ZAR, no customs, no import VAT surprise.
- **If Yubico names another South African partner** — try them before importing.
- **If nobody local has 140-3** — order direct from Yubico and accept the customs delay. This is the
  fallback, not a failure; $88 plus shipping is the smaller half of the bill either way.
- **Whoever supplies it, get it in writing that it is 140-3**, because the model names do not
  distinguish the generations.

The only thing that could still change the plan is if **no supplier anywhere can ship a 140-3 key to
South Africa in reasonable time**. If that happens, come back — the fallback worth asking about is
whether Sectigo's own provisioned token can be used on a multi-year order after all, which Nick says
it cannot, but which is worth pressing if the alternative is not shipping.
