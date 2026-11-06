# Code signing — why it is now urgent

## What happened

While testing the restyled desktop apps on Clint's laptop, Windows began refusing to launch them:

```
This command cannot be run due to the error: An Application Control policy has blocked this file.
System.IO.FileLoadException: ... An Application Control policy has blocked this file. (0x800711C7)
```

The cause is **Smart App Control**, which is enabled on this machine:

```powershell
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy').VerifiedAndReputablePolicyState
# 1 = enforcing
```

Smart App Control blocks executables that are **unsigned and have no reputation**. Freshly built binaries
are exactly that. Older copies of the same apps ran earlier in the session; once rebuilt, the new files had
no reputation and were blocked.

## Why this matters for the product

This is not a quirk of one laptop. Smart App Control is **on by default on clean Windows 11 installs**, and
it is the same reputation system behind SmartScreen. Without signing:

- Some customers cannot run the client at all — it is blocked outright, not just warned about.
- Others see "Windows protected your PC" on the installer, which kills trust in a support tool.
- Each new build starts from zero reputation, so the problem recurs at every release.

## Whose name goes on it

**The Remote Support Program**, the registered business — not Anywhere IT. Decided 2026-09-16, and the
codebase was changed to match the same day.

The certificate names the *publisher*: whoever built the binaries. Every tenant downloads byte-identical
executables (`PayloadService` swaps only `branding/brand.json` and the logo), so one certificate covers
every reseller that will ever exist, and one publisher name appears in every UAC prompt and every
Programs-and-Features entry. A reseller's own name reaches their customers through the branding — the
Program Files folder, the shortcut, the tray app, the service display name — and never through the
signature. Signing as Anywhere IT would have put one IT company's name on every competitor's installs.

Buy the certificate in the name of **The Remote Support Program**, and make sure the registration
documents and the verifiable phone listing the issuer checks belong to that entity, not to Anywhere IT.
The identity check is the slow part; a mismatch restarts it.

Where the publisher name now lives in the repository:

| File | What it sets |
|---|---|
| `Directory.Build.props` | `Company`, `Product`, `Copyright` and version, for every assembly in the solution |
| `installers/client.iss`, `installers/technician.iss` | `AppPublisher`, the install folder, the setup filename |
| `deploy-client/Install-Client.ps1` | the `HKLM\...\Run` value `TheRemoteSupportProgram` |
| `src/RemoteSupport.Technician/Trace.cs` | `%LOCALAPPDATA%\TheRemoteSupportProgram\` |

`branding/brand.json` is **not** on that list and must never be: that is Anywhere IT's own tenant
branding, one tenant among many.

### The legal entity, settled 2026-10-02

The certificate is bought in the name of the registered company:

| | |
|---|---|
| Legal name | **The Remote Support Program (Pty) Ltd** |
| Registration | **2026/737434/07** (CIPC) |
| Country | South Africa |

**The "(Pty) Ltd" matters and is not optional.** Sectigo validates the organisation name against
CIPC, so the name on the order must be the registered one exactly; a mismatch restarts the identity
check, which is the slow part. That full string is therefore also what appears as the publisher in
every UAC prompt and every Programs-and-Features entry on every customer machine.

**The build already copes with the longer name, and nothing needs changing.** `build.ps1` defaults
`-CertSubject` to `The Remote Support Program`, and `Get-SigningCert` in `tools/Signing.ps1` matches
with `$_.Subject -like "*$subject*"` — a certificate issued to
`CN=The Remote Support Program (Pty) Ltd, …` contains that substring and is found. Checked
2026-10-02, before the order was placed. Do not "tighten" that wildcard to an exact match.

`Directory.Build.props` keeps the shorter `Company` string on purpose: it is our own product name,
not the legal entity, and the two are allowed to differ. The signature carries the legal name; the
assemblies carry the product name.

## What to buy — revised 2026-09-21, and again 2026-09-23

**OV code signing, one year, with a physical token. Not EV.** This reverses the 2026-09-16
recommendation. The reasoning below is why, and it is worth reading before anyone "upgrades" the
order back to EV on the strength of the old entry.

### EV lost its advantage in March 2024

The original entry recommended EV because it granted **immediate** SmartScreen reputation where OV
had to earn it. That was true when written and is not true now. Microsoft's Trusted Root Program
update of **March 2024** removed EV's distinct SmartScreen status: EV and OV now build reputation
identically, through download volume. SSL.com states this plainly on their own EV product page — a
vendor arguing against their own more expensive product — and DigiCert's code-signing page no longer
makes a SmartScreen claim for EV at all, pitching it purely on kernel-mode drivers and Microsoft
attestation signing.

### `uiAccess` never required EV

`ROADMAP-PARITY.md` stage 5 claimed an EV certificate "unlocks `uiAccess="true"`". It does not.
Verified 2026-09-21 against Microsoft Learn: the UAC policy setting *User Account Control: Only
elevate UIAccess applications that are installed in secure locations* enumerates exactly two checks
before Windows will start an application at UIAccess integrity —

1. a digital signature verifiable against the machine's **Trusted Root Certification Authorities**
   store, and
2. installation in a folder writable only by administrators: `%ProgramFiles%` or `%WinDir%`.

*Trusted root*, not *extended validation*. An OV certificate satisfies that exactly as an EV one
does. The same page notes Windows applies the signature check whether or not the policy setting is
enabled, so it is unconditional — but it is still only "trusted root". Our installers already use
`DefaultDirName={autopf}\...`, so condition 2 is met today.

`HANDOVER.md` §4 had this right all along: it says only that `uiAccess` "needs a signed manifest"
and makes no EV claim. The roadmap drifted; the handover did not. That is the usual direction.

### So nothing EV-only applies to us

| Claimed EV benefit | Reality for this product |
|---|---|
| Instant SmartScreen reputation | Removed by Microsoft, March 2024 |
| Unlocks `uiAccess="true"` | Never true — trusted root is the bar |
| Kernel-mode driver signing | We ship no driver |
| Hardware Dev Center / WHQL | We do not use it |

### The decision — revised again 2026-09-23

**Sectigo (Comodo) OV code signing, 2 years, from LeaderSSL, on a YubiKey 5 FIPS we supply.**

`CODE-SIGNING-ORDER.md` is the step-by-step for actually buying it. This section is only why.

This supersedes the one-year SSL.com plan recorded here on 2026-09-21. **Clint's decision,
2026-09-23: two years, so the identity check is not repeated every year.** The earlier argument for
one year — that these rules keep moving — still holds and is still recorded below; it is simply
outweighed by not wanting to redo documents, a phone listing and a callback annually.

### The prices, read live 2026-09-21 and 2026-09-23

At about R16.25 to the dollar. All figures **exclude VAT**.

| Source | Product | Certificate | Token |
|---|---|---|---|
| **LeaderSSL** | Sectigo OV, **2 years** | **R9,942.42** | **must supply your own** |
| LeaderSSL | Sectigo OV, 3 years | R12,330.10 | must supply your own |
| LeaderSSL | Sectigo OV, 1 year | R5,857.26 | +R2,238.44 = **R8,095.70** with token |
| SSL.com | OV, 1 year | $129 ≈ R2,096 | YubiKey $379 ≈ R6,159 |
| SSL.com | EV, 1 year | $349 ≈ R5,671 | YubiKey $379 ≈ R6,159 |
| Smartweb (SA) | Comodo OV, 1 year | R9,200 | not mentioned on the page |

**A correction worth keeping.** LeaderSSL's headline R5,857.26 is the certificate *without* a token;
the page's own asterisk says the token is included "with the purchase on 1 year", which reads as
though it is in that price and it is not. Flipping the dropdown moves the figure to R8,095.70 —
exactly R2,238.44 more. **Read the configured total, not the headline.**

**LeaderSSL will not sell a token on a 2- or 3-year term at all.** Those cards offer one option,
"My own YubiKey token (5 series FIPS 140-2)". Only the 1-year card offers to supply one. That is why
the token is bought separately and why it must be in hand *before* the certificate is ordered — the
certificate is generated onto it.

Two years works out at roughly **R6,000/year all in**, against about R7,000/year doing consecutive
one-year terms, and with half the validation work.

### There is no South African certificate authority — checked 2026-09-23

Windows trusts only roots in **Microsoft's Trusted Root Program**, and no South African CA is in it.
Every code-signing certificate sold here is a foreign one behind a local shopfront. Do not spend
time looking for a local issuer; there isn't one.

Two things that look like exceptions and are not:

- **Thawte** genuinely was a South African CA — founded by Mark Shuttleworth in Cape Town in 1995 —
  but it was sold to VeriSign in 1999 and has been a DigiCert brand since 2017. South African in
  name only for twenty-five years.
- **LAWtrust** is accredited by the **South African Accreditation Authority** under the ECT Act, and
  that accreditation is real — but it is for *advanced electronic signatures*, the legally-binding
  signature-on-a-document sense. It is not Authenticode. Their root is not in Microsoft's programme,
  so a binary signed with it would still be untrusted on every customer machine. Worth knowing
  before somebody sells it to us as "an accredited South African certificate authority", which is
  true and irrelevant.

Local resellers exist and are mostly expensive: **Smartweb** is a real South African company at
R9,200/yr for the Sectigo OV — roughly four times SSL.com for the same product, and its page does
not mention hardware tokens at all, which for a product that has legally required one since June
2023 suggests a stale listing. **LeaderSSL** has a `.co.za` address but bills in Euros from the
Netherlands, so it is a European reseller with a local shopfront, not a local company.

**The token is best sourced locally if possible.** Kernel Afrika is Yubico's official African
distributor — but they list only the **standard** YubiKey 5 series, and a standard YubiKey 5 is
**not** a YubiKey 5 FIPS. Buying the R1,300 one because it is in stock is the mistake to avoid.
### What the CA actually confirmed — 2026-09-28

LeaderSSL (Nick, LeaderTelecom B.V.) answered the pre-purchase questions. Four things worth keeping,
two of which corrected this file.

**A multi-year purchase is a term, not a certificate.** *"Since February 2026, individual Code
Signing certificates may have a maximum validity of 459 days."* So the two-year order buys a two-year
**entitlement** with a **re-issue at roughly fifteen months**, not one certificate valid for
twenty-four. This weakens the reason two years was chosen — one identity check instead of two — but
does not reverse it: R9,942.42 against R11,714.52 for two one-year terms covering the same period,
and at worst the same number of validations. **Open question, asked 2026-09-28:** whether the
mid-term re-issue is free and whether it reuses the completed validation.

**FIPS 140-3, not 140-2.** *"Yubico's FIPS 140-2 certification moved to the Historical List in
September 2026."* The 140-2 devices are being phased out for new deployments. **The names are
identical across both generations** — a "YubiKey 5 NFC FIPS" is both — and **Yubico sells both, on
two different pages**, so `yubico.com/store/yubikey-5-fips-series/` still ships the superseded key.
The 140-3 range is at `yubico.com/products/yubikey-fips/`. Any 140-3 model does, provided it
supports PIV.

**The key is generated on the token, and proved to have been.** The private key must be created
inside the YubiKey in PIV **slot 9a** and never generated on a computer and imported; Sectigo
requires the CSR *and* a YubiKey hardware attestation certificate proving where the key was born.
The issued certificate then goes back into slot 9a. LeaderSSL supply the exact `ykman` commands on
request — ask, rather than improvising.

**The attorney's letter is accepted, but is a fallback not a prerequisite.** Sectigo will take a
letter from a qualified attorney or practising accountant confirming the company's details and
method of communication — provided that professional is themselves verifiable in a professional
registry, and Sectigo may telephone them to confirm it is genuine. **Their advice is to submit the
order normally first**; the letter only matters if the telephone number cannot be verified
independently, and they send the template at that point. So the phone listing, which this file
called the likeliest thing to stall a small South African company, is a softer risk than it looked.

**And the ordering constraint was wrong.** This file previously said the token had to be in hand
before ordering. Payment and ordering can happen first; the token is needed only for the CSR and
attestation. That mattered, because it is what made a 30 September promotional price reachable.

**Confirmed again 2026-10-02, after a mix-up worth recording.** LeaderSSL replied to the order
enquiry as though an **EV** order already existed in our name and could not be converted to OV.
There was none — we had never ordered anything from them. Nick apologised and confirmed it was his
error. **The lesson is small and real: read a supplier's reply for what it assumes, not only for
what it answers.** Had that gone unchallenged we would have placed a second order to "replace" one
that never existed.

In the same reply he confirmed, in writing:

- **The YubiKey 5 NFC FIPS 140-3, USB-A is the correct device.** The *"5 series FIPS 140-2"* wording
  still shown in their order dropdown *"refers to the older generation/wording and should not be
  taken to mean that you need a 140-2 device."* Do not let that dropdown cause second thoughts.
- **Order and pay before the token arrives.** The device is needed only at the CSR and attestation
  step, which comes after payment.
- **The two-year term is a product/subscription term**, with certificates issued and re-issued
  inside it at whatever maximum validity the CA/B Forum rules then allow — not one certificate
  valid for two years.


### Take the physical token, not cloud signing

Since **June 2023** the CA/Browser Forum baseline requirements have demanded that code-signing
private keys live on certified hardware — FIPS 140-2 Level 2 or better. This applies to **OV as well
as EV**; a `.pfx` by email is gone for both. Two ways to satisfy it:

- **A physical token** (YubiKey FIPS, $379 one-off). Its driver surfaces the certificate in
  `CurrentUser\My`, which is exactly where `Get-SigningCert` looks, so nothing in `build.ps1`
  changes.
- **Cloud signing** (SSL.com eSigner, DigiCert KeyLocker). **These meter signatures.** eSigner
  Tier 1 is $180/yr for **240 signings**, and a full `build.ps1 -Installers` signs **13 files** — so
  roughly **18 signed builds a year** before the next tier at $765/yr. Over three years the token is
  $379 once against $540 for the smallest cloud tier, and the token is unlimited.

Do not let a reseller move you onto the cloud tier because it is "easier for CI/CD". We have no CI;
it is a per-signature bill on a product that signs thirteen files every release.

**The token ships from the US.** SSL.com's quoted delivery is US-only — confirm international
shipping to South Africa, its cost and its lead time at order time, and expect import VAT and a
customs delay on top.

## Applying for it

**The step-by-step is `CODE-SIGNING-ORDER.md`.** It is the do-it list — what to ask before spending
anything, which token to buy and where, what to do while it is in transit, how to fill in the order,
and the first signed build. This section is only the standing facts behind it.

**The CA validates a business identity, never the software.** No code review, no binary submission,
no product approval. **An unfinished product is not a reason to wait** — the validation weeks run in
parallel with whatever else is being built, and having the certificate early is the only way to
prove the signing path below, which has still never executed.

**The Remote Support Program is registered with CIPC** (confirmed by Clint, 2026-09-21), and
**`info@theremotesupportprogram.com` is the order contact** (confirmed 2026-09-21, already created).
It is a role address, so the order must still carry **Clint's name personally** — the CA verifies a
named individual and the phone callback asks for that person by name — and the mailbox must be
watched daily from the day the order goes in, because every validation request and the final
collection instructions arrive there and nowhere else.

**The phone number is the real bottleneck.** The CA must be able to find the company's number
*independently*, not merely be given it on the order form. This is the step most likely to stall a
small South African company; the usual fallback is a letter from an attorney or practising
accountant covering existence, address and telephone in one document. Arrange it before ordering
rather than halfway through validation.

**The Windows SDK is installed, 2026-10-08**, by
`winget install --id Microsoft.WindowsSDK.10.0.26100 -e`. `Find-SignTool` searches only
`Windows Kits\10\bin`, so a NuGet-packaged signtool would not have been found — it needs the real
SDK. **`Find-SignTool` was then run for real and picks up
`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`**, and the binary runs.
That half of the build is no longer a guess.

## How it is wired in — done 2026-09-16

`build.ps1` signs automatically, and is a no-op until a certificate exists. Nothing about the command
changes on the day it arrives:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Installers
```

It looks for a valid code-signing certificate whose subject contains **The Remote Support Program**, in
`CurrentUser\My` then `LocalMachine\My`, and for `signtool.exe` under the Windows SDK. With both present
it signs; with either missing it prints a warning that the output is unsigned and carries on exactly as
before.

| Switch | Effect |
|---|---|
| *(none)* | Sign if possible, warn and continue if not |
| `-RequireSigning` | **Fail the build** rather than produce unsigned files. Use it for anything leaving this PC. Checked before `dist\` is cleaned, so a failure costs nothing |
| `-SkipSigning` | Sign nothing even with a certificate installed — a fast local build |
| `-CertSubject`, `-TimestampUrl` | Override the publisher subject or the timestamp server |

What gets signed, in two passes:

1. **After staging, before the payloads are copied** — `RemoteSupport.Client.exe`, `.Host.exe`,
   `.Agent.exe`, `.Technician.exe`, plus `Install-Client.ps1`, `Uninstall-Client.ps1` and the five
   health-check scripts. The ordering is load-bearing: the `-Platform` block copies `dist\client\`
   wholesale into `payloads\`, and `PayloadService` zips that verbatim for every tenant. Sign after
   that copy and every tenant gets unsigned binaries.
2. **After the Inno Setup compile** — the two setup `.exe` files, which are what SmartScreen judges
   first because they are what the customer downloads.

Details that matter:

- `/sha1 <thumbprint>` selects the exact certificate found, rather than letting signtool guess when
  there is more than one in the store.
- `/tr` timestamps every signature. Without it they all stop verifying the day the certificate
  expires, including on machines installed years earlier.
- `signtool verify /pa` runs on every signed binary, so a silent signing failure cannot ship.
- `.ps1` files carry their signature as an appended comment block, so `signtool` cannot sign them;
  `Set-AuthenticodeSignature` does. These are the scripts that install the client and the ones the
  monthly health check runs as SYSTEM — the two places an execution policy or an AV product is most
  likely to object.
- Inno generates `unins000.exe` on the customer's machine at install time, so it can never carry our
  signature. A known gap, not an oversight.

**Half proven, 2026-10-08.** The skip path and the `-RequireSigning` failure path were both run and
behave correctly. **`Find-SignTool` and `Initialize-Signing` have now been run for real**, with the
SDK installed:

```
signing on : False
signtool   : C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe
timestamp  : http://timestamp.sectigo.com
certificate: (not yet)
```

So discovery works, the newest x64 signtool is the one chosen, the binary runs, and the default
timestamp server is already Sectigo's — the CA we bought from. **The certificate is the only thing
still missing**, and on the day it reaches the token `On` flips to True with nothing in the build
changed.

**SIGNING PROVEN, 2026-10-15.** The certificate was issued on 14 October 2026 (valid to
**18 November 2027**, thumbprint `9390B00F092D1B8F1416F34B89D958BD1A8894E5`, issued by Sectigo
Public Code Signing CA E36) and is on the YubiKey in slot 9a. **`tools\Install-SigningCert.ps1`**
put it there and proved it: the slot's key matches the certificate, the certificate is listed in
`CurrentUser\My` with its key, `Initialize-Signing -RequireSigning` picks it, and `signtool sign`
with `/sha1`, `/fd SHA256` and Sectigo's timestamp produced an embedded signature that verifies as
**The Remote Support Program (Pty) Ltd**, timestamped. Run the same script for the second
certificate of the term.

Two things found on the way:

- **Our key is ECC P-256, and Windows' own PIV driver does not handle it.** It sees the card and
  `certutil -scinfo` says *"Missing stored keyset"*; nothing is listed. **Yubico's Smart Card
  Minidriver** (x64, 4.6.3.252 or later; 5.0.4.273 is installed) fixes it. **Every PC that signs
  needs it.** A CHUID was not the cause — `ykman piv certificates import` writes one itself.
- **Never test signing on a copy of a Windows file with `Get-AuthenticodeSignature`.** Windows'
  own files are signed through a catalog, and the catalog keeps matching after we embed a
  signature (the hash excludes it), so it answers "Microsoft Windows". Read the embedded signature
  (`signtool verify /pa`, `X509Certificate.CreateFromSignedFile`). Our own files are not in any
  catalog, so `tools\Signing.ps1` is unaffected.

**A deploy cannot ship unsigned by accident (2026-10-15).** `deploy\Publish-Platform.ps1` builds
with `-RequireSigning` and then checks every `.exe` and `.ps1` under `dist\platform-linux\payloads`
(12 files) for a valid signature by us before uploading anything — which also catches `-SkipBuild`
on an old dist. Run against the pre-certificate dist it named all 12 and stopped. `-Unsigned` is
the deliberate way past it.

**Still unproven:** a full signed build — `build.ps1 -Installers -RequireSigning`, the `.ps1` files
through `Set-AuthenticodeSignature` with a key on a token, and how many PIN prompts a release
costs.

## Testing before the certificate arrives

The apps themselves are fine — they compile and ran correctly until the rebuild. To test on a machine with
Smart App Control enforcing, either:

- test on a PC or VM where Smart App Control is off (older Windows 11 upgrades usually have it off), or
- turn Smart App Control off in **Windows Security → App & browser control → Smart App Control**.
  **One-way:** once it is off it cannot be re-enabled without reinstalling Windows. Do this on a test
  machine, not a daily driver.

## One .exe for every company (2026-09-30)

Clint asked whether making every download a single .exe affects signing. It does not limit it, and
it is the design signing needs:

- **No signing limits.** The key is on the YubiKey, and a hardware token signs as many files as we
  like; only cloud signing meters signatures (above). One .exe is signed once per RELEASE, on this
  PC, never per company or per download.
- **Signing per download is impossible anyway.** The key never leaves the token, and the server is
  not where the token is. A download that differed per company could not be signed; that is why the
  per-company download used to be an UNSIGNED zip. One identical .exe for everybody is the only way
  every company's customers get a signed file.
- **Renaming does not break a signature.** It covers the file's contents, not its name, so serving
  it as `anywhere-it-support.exe` is fine. The Zone.Identifier stream Windows writes beside a
  download is not part of the file either. What must NEVER happen is writing a company's details
  INTO the .exe after signing — that is why the company comes from the file name and the download
  address, and its branding from the server.
- **Reputation builds faster.** SmartScreen and Smart App Control judge a file partly by how many
  people have run it. One file for every company pools every download; a file per company would
  start each one from nothing.
- **Two rules for the build.** Timestamp every signature (`/tr`, already done) so a copy downloaded
  after the certificate's 459 days still verifies. And **sign the inside before the outside**: the
  host, the agent and the scripts packed inside the client are signed first, then packed, and the
  client .exe is signed last. `tools/Pack-ClientExe.ps1` and `build.ps1` do it in that order.
