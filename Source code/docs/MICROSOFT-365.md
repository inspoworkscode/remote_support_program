# Sign in with Microsoft — the one-time steps

*Written 2026-10-19 with the build (HANDOVER §151). Tickets by email's third way in: a company on Microsoft 365
connects its support mailbox by signing in with Microsoft — no password given to us, no forwarding to allow.*

Until steps 1–5 are done the choice on **Portal → Tickets by email** reads *Microsoft 365 — sign in with Microsoft:
Not available on this server yet — use forwarding*, and nothing else changes. Everything here is done **once**, by
Clint, for the whole platform — never by each company. Step 6 (publisher verification) can follow later; until it is
done Microsoft shows **"unverified"** beside our name, and some organisations will need their administrator to approve
the app (the page explains both).

**Why it is needed.** Microsoft 365 no longer accepts a mailbox password from a program like ours: basic
authentication is gone for IMAP, and SMTP AUTH with a password is switched off by default for existing tenants from
the end of December 2026 (Microsoft message MC786329). OAuth — this — keeps working.

**What the app may do, and nothing more.** Four OpenID basics (`openid`, `profile`, `email`, `offline_access` — who
signed in, and staying signed in) and two **delegated** Exchange permissions: `IMAP.AccessAsUser.All` (read the
mailbox *of the person who signed in*, or a shared mailbox they may already open) and `SMTP.Send` (send *as that
person*). Never `Mail.Read` as an application permission, which would open every mailbox in their organisation. The
platform opens the mailbox read-only, as it does every mailbox (HANDOVER §79).

---

## 1. Register the app (about 5 minutes)

Sign in to **https://entra.microsoft.com** with an admin account of the organisation that will own the app — ideally
one on **theremotesupportprogram.com** (see step 6 for why).

1. **Identity → Applications → App registrations → New registration.**
2. **Name:** `The Remote Support Program` (customers see this on Microsoft's sign-in page).
3. **Supported account types:** *Accounts in any organizational directory (Any Microsoft Entra ID tenant — Multitenant)*.
   Not personal accounts: Outlook.com cannot be read this way, and the platform asks Microsoft for work accounts only.
4. **Redirect URI:** platform **Web**, address exactly:

   ```
   https://theremotesupportprogram.com/portal/mailintake/microsoft
   ```

   Every other server that runs the platform (a regional server, a test server) needs its own line added later under
   **Authentication → Web → Redirect URIs** — `https://<that server's address>/portal/mailintake/microsoft`. On any
   server, `microsoft-app show` prints the exact one it uses.
5. **Register.** On the app's **Overview** page, copy the **Application (client) ID** (a GUID) — step 4 needs it.

## 2. Branding (what customers see on Microsoft's page)

**Branding & properties:**

- **Logo:** `docs/microsoft-app-logo.png` — the purple RSP mark from the website header, 240×240 PNG, 9 KB (Entra wants square, under 100 KB).
- **Home page URL:** `https://theremotesupportprogram.com`
- **Terms of service URL:** `https://theremotesupportprogram.com/#terms`
- **Privacy statement URL:** `https://theremotesupportprogram.com/#privacy`
  (both anchors are on the holding page today; change them to `/terms` and `/privacy` when the real site goes up).

## 3. Permissions

**API permissions → Add a permission → Microsoft Graph → Delegated permissions**, tick:

- `openid`, `profile`, `email`, `offline_access` (under *OpenId permissions*)
- `IMAP.AccessAsUser.All` (search *IMAP*)
- `SMTP.Send` (search *SMTP*)

Remove `User.Read` if Entra added it by itself — we never call Graph. Add nothing else. **Do not** press *Grant admin
consent* for any organisation but your own (it only affects your own).

*(The sign-in itself asks for these on `https://outlook.office.com/…`, which is how Microsoft documents IMAP and SMTP
with OAuth; Entra lists the same permissions under Microsoft Graph. Both name the same thing.)*

## 4. The secret, onto the server

1. **Certificates & secrets → Client secrets → New client secret.** Description `platform`, expires **24 months**
   (the longest). **Copy the Value at once** — Entra never shows it again. (Not the Secret ID.)
2. Put a reminder in the diary for **a month before it runs out**: make a new secret and run step 4.3 again. **The day
   it runs out, every company's Microsoft sign-in stops** (the page tells them it is ours to fix, not theirs).
3. On this PC, after the deploy in step 5:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\Set-MicrosoftApp.ps1
   ```

   A window asks — **user name = the Application (client) ID, password = the secret's Value** — then the date it runs
   out, then your ssh key's passphrase. The secret goes down ssh on standard input and is stored encrypted on the
   server; it never appears in a chat, a file or a command line. The script prints `microsoft-app show`: check the
   **redirect URI** it prints is exactly the one on the app.

## 5. Deploy

The signed `Publish-Platform.ps1` deploy carries migration **`MicrosoftSignIn`** (both databases — columns on
`TicketIntake` and `PlatformSettings`, nothing else). Deploy first, then step 4.3 (the command it runs is new in this
build). No console re-download, no client reinstall — nothing on a machine changed.

## 6. Publisher verification (removes "unverified")

Microsoft marks an app *unverified* until the company behind it is checked, and since November 2020 many
organisations let their people approve only **verified** apps — the rest need an administrator. Two parts:

**6a. Publisher domain.** **Branding & properties → Publisher domain → Update domain → Verify a new domain** →
`theremotesupportprogram.com`. Entra asks for a file at
`https://theremotesupportprogram.com/.well-known/microsoft-identity-association.json` naming the app — **the platform
already serves it** (from the client ID stored in step 4), so just press **Verify and save domain**. Check it first in
a browser: it shows `{"associatedApplications":[{"applicationId":"<the client ID>"}]}`.

**6b. Microsoft Cloud Partner Program (the old MPN).**

1. At **https://partner.microsoft.com** enrol *The Remote Support Program (Pty) Ltd* (CIPC 2026/737434/07) in the
   **Microsoft Cloud Partner Program** — free. Use an email address **on theremotesupportprogram.com** (e.g.
   `clint@theremotesupportprogram.com`): Microsoft requires the Partner Center account's email domain to match the
   app's publisher domain (6a) or a domain verified in the Entra tenant. Microsoft checks the company (a few days; they
   may ask for the CIPC certificate) — the account must show **Verified** before the next step.
2. Copy the **Partner ID** (Partner Center → Account settings → Legal info / Identifiers).
3. Back in Entra: **App registrations → The Remote Support Program → Branding & properties → Publisher verification →
   Add Partner ID**, paste it, **Verify and save**. The account doing this needs the Entra role *Application
   Administrator* (or *Cloud Application Administrator*) and, in Partner Center, *Account administrator* or *MPN admin*.

When it works, Microsoft's consent page shows a blue **verified** tick beside *The Remote Support Program (Pty) Ltd*.

## 7. Test with a real Microsoft 365 mailbox

Anywhere IT's own mail is cPanel, so this needs a Microsoft 365 mailbox: a customer's test mailbox, or a
**Microsoft 365 Business Basic** trial tenant (a month free; `<name>.onmicrosoft.com`). In it:

1. Make a mailbox `support@<tenant>` and a person `admin@<tenant>`.
2. **Microsoft 365 admin centre → Users → Active users → support@ → Mail → Manage email apps:** tick **IMAP** and
   **Authenticated SMTP**. (New tenants have SMTP AUTH off for the organisation: also Exchange admin centre →
   Settings → Mail flow → untick *Turn off SMTP AUTH protocol for your organization*.)
3. Then `TEST-3.6.md` **section DH (751–760)**, in order: sign in, the green test, an email in, the reply out, a shared
   mailbox, IMAP off, **Revoke sessions** (Entra → Users → support@ → *Revoke sessions* — the quickest way to make
   Microsoft refuse the sign-in), Disconnect.

## If something goes wrong

| What the page says | What it means |
|---|---|
| *Not available on this server yet* | Step 4.3 has not run on this server (`microsoft-app show` says not set). |
| *Microsoft did not accept our app's own sign-in (its secret may have run out). This is ours to fix* | The secret ran out or was deleted: step 4 again. Companies' sign-ins are kept and carry on by themselves once it is fixed. |
| *Microsoft did not recognise our app* (at the way back) | The client ID on the server is not the app's, or the app was deleted. `microsoft-app show`, compare with Entra's Overview page. |
| Microsoft says *redirect URI … does not match* (AADSTS50011) | The server's redirect URI is not on the app (step 1.4) — copy it from `microsoft-app show`. |
| *Your organisation lets only an administrator approve a new app* | Their tenant blocks user consent (very likely until step 6). Their admin opens the approval link under the button once. |
| *Microsoft no longer accepts the sign-in: …* | Their password changed, their sessions were revoked, the permission was withdrawn, or a new policy (MFA, conditional access) asks for another step. *Sign in with Microsoft again* carries on where it stopped. |
| *… would not let … read …* / *… send by SMTP* | Their mailbox has IMAP or Authenticated SMTP switched off (step 7.2), or — for a shared mailbox — the person who signed in lacks Full Access / Send as. |

**To take our access away completely**, a company presses **Disconnect** (the token is deleted from our server) and,
if they wish, removes the app in **Entra admin centre → Enterprise applications → The Remote Support Program → Delete**.
