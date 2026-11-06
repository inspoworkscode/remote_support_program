# Branding

All identity lives in **`branding/brand.json`** plus a **`branding/logo.png`**. Every app loads it at
startup, and `build.ps1` copies the folder into each payload. Change it once, rebuild, done.

```json
{
  "companyName": "Your Company",
  "productName": "Remote Support",
  "supportEmail": "support@yourcompany.co.za",
  "supportPhone": "+27 00 000 0000",
  "primaryColor": "#1F4E79",
  "accentColor": "#2E9BFF",
  "logo": "logo.png",
  "serverUrl": "wss://support.yourcompany.co.za",
  "welcomeText": "Need help? Click Request Support and one of our technicians will connect with you."
}
```

| Field | Used for |
|---|---|
| `companyName` / `productName` | Window titles, banners, service display names |
| `primaryColor` | Header/banner colour (hex) |
| `accentColor` | Primary buttons (hex) |
| `logo` | PNG shown in the client and technician banners (transparent PNG, ~400×120 works well) |
| `serverUrl` | Where every app connects. `wss://` for TLS, `ws://` for plain (testing only) |
| `welcomeText` | Message on the client window |
| `supportEmail` / `supportPhone` | Reserved for display / future use |
| `technicianTheme` | `light` or `dark` — the console the tenant's own staff use (default `light`) |
| `clientTheme` | `light` or `dark` — the app the tenant's customer sees (default `light`) |
| `requireConsent` | Ask the person at the machine to approve attended sessions (default `true`) |
| `unattendedRequireConsent` | Also ask for unattended/agent sessions (default `false`) |
| `consentTimeoutSeconds` | Seconds before the consent prompt auto-declines (default `60`) |

## Steps

1. Drop your logo at `branding/logo.png`.
2. Edit the fields above (at minimum `companyName`, `productName`, colours, `serverUrl`).
3. Rebuild: `powershell -ExecutionPolicy Bypass -File build.ps1 -Installers`.
4. The installer names, shortcuts, service names and in-app look all follow the new brand.

To also rename the setup files / Start-menu entries, edit the `#define CompanyName` and
`#define AppName` lines at the top of `installers/client.iss` and `installers/technician.iss`.

## Installer icon (optional)

Add `SetupIconFile=..\branding\app.ico` under `[Setup]` in each `.iss` to brand the installer icon,
and `UninstallDisplayIcon={app}\RemoteSupport.Client.exe`.

## Appearance

Two settings rather than one, because they are different audiences: the console is a tool the
company's own technicians look at all day, and the client app is how that company presents itself to
its customer. A tenant can reasonably want a dark console and a light, on-brand customer app.

**The signature colour is the tenant own `accentColor`, not `primaryColor`** — lifted only as far as it must
be to stay readable on the navy ground. A reseller's apps never carry our colours. `primaryColor`
and `accentColor` themselves are passed through untouched: they are someone's brand and we do not
get to repaint them.

Because of that, one thing cannot be fixed for a tenant: a near-black `primaryColor` on a dark
background stops reading as a banner at all. The portal warns them at the moment they choose it
(Branding → Appearance) rather than silently substituting a different colour.

In the platform these live on the tenant, editable in the portal or from the server:

```bash
./RemoteSupport.Platform set-brand <slug> tech-theme dark
./RemoteSupport.Platform set-brand <slug> client-theme light
```

A change reaches **new downloads only**. `Branding.Load()` reads `brand.json` from disk and nothing
pushes it to an installed machine — see roadmap 4.6. Say so to a tenant who asks why their 200
endpoints still look the same.

To look at a change without touching a tenant's settings, `RSP_THEME=light` or `RSP_THEME=dark`
overrides the branding for one launch. Unset — which is every installed copy — means the branding
decides.

### What Windows draws, not us

`RemoteSupport.UI/NativeDark.cs` handles the three parts of a window WinForms colours never reach:
the title bar (DWM), scrollbars inside list and text controls (the `DarkMode_Explorer` visual
style), and the strip of column header to the right of the last column in a ListView, which needs
the header subclassed and painted by hand. All of it is wrapped and fails silently, so a Windows 10
build that predates immersive dark mode gets a light title bar and nothing else breaks.

**Known and not fixed:** in the technician console's chat panel, the target dropdown and the message
box still paint white on a dark ground. The dropdown is owner-drawn and correct once it has items —
it is only the empty state that shows through. The message box is an `InputBox`, the same control
that renders correctly on the login form, and has not been explained yet.

### Which of the two brand colours is the signature

**The accent, not the primary** — corrected 2026-09-16 after the portal was seen in dark.

The two fields have always meant different things: `primaryColor` is the **banner** colour and
`accentColor` is the **button** colour. One is a background, the other a foreground. `BrandPalette`
nevertheless derived the portal's links, headings and figures from the primary, which happened to
look fine while the portal was white — a dark banner colour darkened to near-black text reads as
ordinary ink and nobody questions it.

On a dark ground it falls apart. Anywhere IT's primary is `#0E0E12`; lifted far enough to be
readable on navy it becomes `#858587`, a flat grey, and every number and link in the portal went
grey with it. Their accent `#29A8E0` — the colour anyone would name if asked what colour Anywhere
IT is — was barely used anywhere.

So the signature now comes from the accent, and a tenant whose accent already passes contrast sees
it untouched: Anywhere IT's portal is `#29A8E0` exactly, with `#1F7CA4` as the deeper fill under
white button text. The primary is still emitted as `--grape-bright` and `--brand-banner`, and is
still what the banners are painted with, which is the one job it was ever given.

**Portal page titles now carry the signature** rather than plain ink, for the same reason: an ink
heading read as deliberate on white and reads as plain white on navy, leaving a tenant's own page
with no brand colour on it at all. Only the page title — `h2` and `h3` are section labels inside
cards and stay ink, or the page becomes noise.
