# 08 — Keys, passwords and accounts

**None of the secrets below are on this USB stick.** That is deliberate. Anyone holding the
secrets could reach every customer computer that allows unattended access, read stored passwords,
or take the website down. A USB stick can be copied in seconds without anyone noticing.

This page lists **every** secret and account a new team will need, **where it lives today**, and
what to do if it is lost. The right-hand column is for Clint to fill in **by hand, on the printed
copy** (or in the password manager), saying where each one is kept. **Do not type passwords into
this document.**

---

## How to hand the secrets over safely

Pick one, and write which on the printed copy:

1. **Password manager emergency access (recommended).** Most password managers (Bitwarden,
   1Password, LastPass, Keeper) let you name a trusted person who can request access to your
   vault; if you do not refuse within a waiting period you choose, they get it. Put every item
   below into the vault, and name the trusted person.
2. **A sealed envelope in the same safe**, holding the password manager's master password and its
   recovery code (or a printed emergency kit) — not the individual passwords.
3. **An encrypted file on a second USB stick** (for example a 7-Zip archive with AES-256 and a long
   passphrase), with the passphrase kept somewhere else (the sealed envelope, or a lawyer).

The two most important items are **the SSH key** (row 1) and **the backup passphrase** (row 9).
With those and this USB stick, a development team can rebuild everything.

---

## The list

### Getting into the live server

| # | Secret | Where it is today | If lost | Kept in (fill in) |
|---|---|---|---|---|
| 1 | **SSH private key** for `ubuntu@154.65.110.136`, and its passphrase | `C:\Users\Clint\.ssh\id_ed25519` on Clint's laptop; should also be in the password manager | Log in to the xneelo Cloud panel, open the instance's **Console**, and add a new public key to `/home/ubuntu/.ssh/authorized_keys` | |
| 2 | **xneelo Cloud** panel login (the server) | Clint | xneelo support, with company proof of ownership | |

### Running the platform

| # | Secret | Where it is today | If lost | Kept in (fill in) |
|---|---|---|---|---|
| 3 | **Owner key** — signs in to `/owner` | On the server in `/etc/rsp-owner-key`; should be copied to the password manager | Read it again from the server with SSH: `sudo cat /etc/rsp-owner-key`. If it has been exposed, replace it: `deploy\Rotate-Keys.ps1 -Server <ip>` (also replaces the server key) | |
| 3b | **Server key** — lets the control dashboard on another server read and change companies on this one (V2.2) | On the server, in the service settings `go-live.sh` writes (`RSP_Platform__ServerKey`). The owner and server keys were **replaced on 15 October 2026** after a log holding both was pasted into a chat | `deploy\Rotate-Keys.ps1 -Server <ip>` makes new ones; any other server that reads this one is then told the new key at Owner → Servers | |
| 3c | **Platform admins' logins** — each admin's own email, password and authenticator for `/owner` (V2.2) | Each admin | An Owner-level admin, or the owner key with its code, can set the person up again from Owner → Admins | |
| 4 | **Owner authenticator** — the second factor for `/owner` | Authenticator app on Clint's phone | On the server: `cd /opt/rsp && sudo -u rsp ./RemoteSupport.Platform reset-owner-2fa`, then sign in with the owner key and scan the new QR code | |
| 5 | **Clint's company login** `clint@anywhereit.co.za`, its authenticator, and its **ten recovery codes** | Clint; authenticator on his phone | Another owner or supervisor can Reset two-factor or send a reset link from the People page; or on the server: `cd /opt/rsp && sudo -u rsp ./RemoteSupport.Platform reset-2fa anywhereit clint@anywhereit.co.za` | |
| 6 | **Platform mailbox password** (the mailbox the platform sends from, set in Owner → Mail) | Stored encrypted on the server; the mailbox itself is at xneelo shared hosting | Reset the mailbox password in konsoleH, then enter it again in Owner → Mail | |
| 6b | **Forwarding mailbox password** (`tickets@theremotesupportprogram.com`, for tickets by email — Owner → Mail, *Forwarding mailbox*; only once it is set up) | Stored encrypted on the server; the mailbox is at xneelo shared hosting | Reset it in konsoleH, then *Save and test* it again in Owner → Mail | |
| 6c | **Each IT company's support mailbox password**, when they let the platform check their mailbox (Portal → Tickets by email) | Stored encrypted on the server, one per company. Theirs, not ours | The company types it again on that page | |

### Backups and stored data

| # | Secret | Where it is today | If lost | Kept in (fill in) |
|---|---|---|---|---|
| 7 | **The data folder's encryption keys** (`/opt/rsp/data/keys`) | On the server, and inside every nightly backup | Every stored workshop device password and mailbox password becomes unreadable. Everything else survives | (inside the backups) |
| 8 | **Recent backup files** (`/var/backups/rsp/*.tar.gz.gpg`) | On the server (the `rsp-files` volume). Since 8 October an **office PC can fetch each night's copy** onto the office share (`deploy/backup/README.md`) — **check it has been set up and is running**; until it is, they are only on the server | If the server is lost too, and there is no office copy, all company data is lost | |
| 8b | **The office PC's key for fetching backups** (the `rspcopy` account; it can only list and fetch the encrypted files) | On the office PC that runs `Pull-Backup.ps1` | Run `Install-BackupPull.ps1` again on an office PC (`deploy/backup/README.md`) | |
| 8c | **The site check's mailbox password** (V2.4) — the theremotesupportprogram.com mailbox the office PC sends *site is down* emails through | On the office PC that runs the site check, in `%USERPROFILE%\.rsp-watch\mail.xml`, readable only by that Windows account on that PC | Run `Install-SiteWatch.ps1` again on the office PC and type it (`deploy/monitor/README.md`) | |
| 9 | **Backup passphrase** (`/etc/rsp-backup-key`) | On the server; should be copied to the password manager | The backups cannot be opened. Make a new one by deleting the file and re-running `go-live.sh`; older backups stay locked for ever | |

> **The database has no password.** PostgreSQL on the live server (since 9 October 2026) is reached only
> over a local socket by the platform's own Linux account; nothing to keep.

### The domain, DNS and email

| # | Account | Used for | If lost | Kept in (fill in) |
|---|---|---|---|---|
| 10 | **Afrihost** login | Domain registration and renewal | Afrihost support, with ID and company documents | |
| 11 | **Cloudflare** login | DNS — where the name points | Cloudflare account recovery | |
| 12 | **xneelo konsoleH** login | Shared hosting and the domain's mailboxes | xneelo support | |
| 13 | **Mailbox passwords** (`info@`, `queries@` and others on the domain) | Company email | Reset in konsoleH | |

### Code signing

| # | Item | Where it is today | If lost | Kept in (fill in) |
|---|---|---|---|---|
| 14 | **The YubiKey** — a YubiKey 5 NFC FIPS, serial **38992498**, firmware 5.7.4. The code-signing private key is inside it, in **PIV slot 9a** | In the safe. Plugged in only to sign a release | **A new certificate must be bought and validated again** — roughly R10,000 and some weeks. There is no other copy; see the note below | |
| 15 | **Its PIN**, and the **PUK** that unblocks the PIN | On paper, kept **apart from the token** | Three wrong PINs block it and the PUK is the only way back. Lose both and the key is gone | |
| 16 | **LeaderSSL** account (reseller) and **Sectigo** order details — proforma 2611897, reference 80819755/2611897 | `info@theremotesupportprogram.com` | LeaderSSL support, `info@leaderssl.com` | |

**The code-signing key cannot be backed up, and that is deliberate.** The private key was generated
*inside* the YubiKey on 8 October 2026 and cannot be read out of it by anybody, including us — which
is exactly what the certificate authority requires and what the attestation certifies. The
consequence is that **the token is a single point of failure**: lose it, destroy it, or forget the
PIN, and the certificate is worthless and must be bought again from the beginning.

The management key is stored on the token itself, protected by the PIN, so **the PIN is the only
secret a person has to keep**.

**Never run either of these against this token:**

```
ykman piv reset            # wipes everything
ykman piv keys generate    # quietly replaces the key in the slot
```

Either destroys the key the certificate was issued against. The certificate only works with that one
private key.

`docs/CODE-SIGNING-ORDER.md` is the full story of how it was bought and set up, including the steps
to repeat at renewal.

### The code itself

| # | Item | Where it is today | Note | Kept in (fill in) |
|---|---|---|---|---|
| 17 | **The source code and its history** | Clint's laptop, `C:\Users\Clint\Desktop\Remote Support`, **and this USB stick** | **There is no online copy** (no GitHub or similar). If the laptop and this stick are both lost, the code is gone. Consider a private online repository as a second copy | This USB stick |
| 18 | Test-tool login file `tools\rs-credentials.json` | Clint's laptop only | Only for running test tools against the live server. Recreate it by hand (format in `tools/Common/RsCredentials.cs`) | not needed |
| 19 | `branding\brand.json` with the tenant key | Clint's laptop only | **Not a secret.** Download it again from the portal (Downloads page) | not needed |
| 20 | The office test platform's database | Clint's laptop, `src\RemoteSupport.Platform\bin\Release\net10.0\data` | Test data only | not needed |

### Company and business documents

| # | Item | Where it is today | Kept in (fill in) |
|---|---|---|---|
| 21 | CIPC registration — The Remote Support Program (Pty) Ltd, 2026/737434/07 | Company records | |
| 22 | The investment agreement and other legal papers (`legal\` folder on the laptop, deliberately not in the code repository) | Clint's laptop | |
| 23 | **PayFast** merchant account login (the website), and the account's **merchant ID, merchant key and passphrase** — one set for the **sandbox** (testing) and one for the **live** account (V2.2) | The login: Clint. The ID, key and passphrase: on the live server (stored encrypted), put there with `deploy\Set-PayfastKeys.ps1` — never typed into chat. The sandbox set used for testing is on Clint's laptop, encrypted to his Windows user (`tools\payfast-sandbox.xml`, made by `tools\Save-PayfastSandbox.ps1`) | Read them again in PayFast's dashboard (Settings) and run `Set-PayfastKeys.ps1` again (`-Live` for the live account). **If the server's address changes, change the allowed address on PayFast's Recurring Billing page too** | |
| 23b | `tools\rs-postgres.txt` — how the test tools reach a local PostgreSQL | Clint's laptop only | Test only; recreate for a local PostgreSQL (format in `tools/Common/HarnessDb*.cs`) | not needed |
| 23c | The **Microsoft Entra app** *The Remote Support Program* (Tickets by email's *Sign in with Microsoft*, HANDOVER §151): its **Application (client) ID** and **client secret** (it runs out — two years at most), and the **Microsoft Cloud Partner Program** account used for publisher verification | The app: App registrations in the Microsoft Entra tenant it was registered in (Clint's admin login). The ID and secret: on the live server (secret stored encrypted), put there with `deploy\Set-MicrosoftApp.ps1` — never typed into chat; `microsoft-app show` on the server says when the secret runs out. The Partner Center login: Clint | Make a new secret in Entra (App registrations → Certificates & secrets) and run `Set-MicrosoftApp.ps1` again **before** the old one runs out — every company's Microsoft sign-in stops the day it does. All the steps: `docs/MICROSOFT-365.md` | |

### Test machines (Anywhere IT office only)

| # | Item | Note |
|---|---|---|
| 24 | Windows logins for DESKTOP-OPHQIDH and DESKTOP-2N6FVGT | Only needed to keep testing |
| 25 | The office shared drive `\\192.168.1.33\anywhereit` (WD My Cloud) | Holds the Test Checklist results |

---

### Keys that stay on each computer (V2.1 — never collected, never backed up)

End-to-end encryption (document 03) uses keys that **must not** be gathered anywhere central — that is
the point of it. Nobody needs to hand these over.

| Item | Where it is | If lost |
|---|---|---|
| Each customer computer's **identity key** | `%ProgramData%\RemoteSupport\identity.key` on that computer, readable only by Windows' system account | A new one is made. Every console that knew the old one stops at the next connection and asks the technician whether to trust the new one |
| Each console's **remembered identities** ("pins") | `%LOCALAPPDATA%\RemoteSupport\machine-keys.json` for each technician on each PC | The console simply learns each computer again at its next connection |

---

## Things that are NOT secrets (so nobody wastes time protecting them)

- The **tenant key** in each company's download. It is public by design (document 03, section 5).
- The live server's **IP address** and the **domain name**.
- The code itself contains no passwords. Test tools use made-up passwords on throwaway test servers.
