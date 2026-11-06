# 06 — Map of the code

For developers. Where everything is, and the files you will most likely need first.

Language: **C# on .NET 10**. Windows programs use **WinForms**. The server is **ASP.NET Core** with
**Razor Pages** for the website, **WebSockets** for live connections, and **Entity Framework Core**
on **PostgreSQL** for the database on the live server (since 9 October 2026; **SQLite** for local testing and as the way back). Scripts are **PowerShell 5.1** (Windows) and **bash** (the Linux
server). About 70,000 lines of hand-written code in `src` (not counting the database migrations, which are generated), plus the test tools.

---

## Top-level folders

| Folder | What is in it |
|---|---|
| `src\` | The product: the nine projects below |
| `tools\` | About 200 test and diagnostic programs, plus the ship, packing and signing scripts. Not shipped to customers |
| `docs\` | All technical documentation. `HANDOVER.md` is the master record |
| `deploy\` | Putting the server live: `Publish-Platform.ps1` (run on the build PC), `go-live.sh` (runs on the server), the systemd service file, `rsp-backup.sh` (the nightly backup), `Rotate-Keys.ps1`, `Set-PayfastKeys.ps1`; `postgres\` (the move to PostgreSQL and the way back), `backup\` (the office copy of the backups), `monitor\` (the office PC's check that the site is up, V2.4), `loadtest\` |
| `docs\pilot-brochure\` | The pilot guide for IT companies (`brochure.html`, its screenshots, and the printed PDF) |
| `docs\brochure\` | The general guide, for anybody thinking of using or investing (`brochure.html` → `The Remote Support Program - Guide.pdf` by `Print-Guide.ps1`) |
| `docs\logo\` | The logo and the mark, light and dark, with the source files |
| `deploy-client\` | The customer installer scripts: `Install-Client.ps1`, `Uninstall-Client.ps1` and double-click wrappers |
| `healthcheck\` | The monthly health check toolkit (PowerShell), with its own README and decisions |
| `branding\` | Anywhere IT's branding for local testing. `brand.example.json` shows the format |
| `installers\` | Old-style Inno Setup installer scripts (`.iss`). No longer the main download |
| `landing\` | The holding web page used before the platform went live |
| `pitch\` | The investor presentation |
| `RemoteSupport.sln` | The Visual Studio solution with every project |
| `build.ps1` | The main build script |
| `Directory.Build.props` | Publisher name for every program, and the version a program built by hand says (1.0.0). A real build is stamped by `tools\BuildVersion.ps1` (V2.3) |
| `CLAUDE.md` | A short index of the project state, kept up to date after each piece of work |

---

## The projects in `src\`

### `RemoteSupport.Platform` — the server (the biggest)

| Folder or file | What it does |
|---|---|
| `Program.cs` | Start-up: settings, security headers, sign-in, web routes, WebSocket endpoints, and the **command-line commands** (`create-tenant`, `invite`, …) at the bottom |
| `Hub\TenantHub.cs` | **The heart of live support.** One per IT company: who is online, ringing, starting and ending sessions, relaying, chat, unattended permission and password. `Recompute` is the one place that decides if a machine is online/unattended |
| `Hub\TenantHub.Web.cs`, `TenantHub.WebChat.cs` | Browser support and website chat |
| `Hub\TenantHub.Outsiders.cs`, `GuestVisitor.cs` | Outside help: codes, visitors from another company, guests |
| `Hub\TenantHub.Watch.cs`, `TenantHub.Wake.cs` | Processes and Performance without connecting; Wake-on-LAN |
| `Hub\TenantHub.SavedPasswords.cs` | Remembered unattended passwords, per technician per machine (V2) |
| `Hub\TenantHub.Free.cs` | **The free version's rules** at the hub (two computers, unattended only, the session and console allow-lists), **removing a machine** for both versions, and the **upgrade** (V2) |
| `Hub\TenantHub.E2e.cs` | **End-to-end encryption** at the hub: says whether a session is sealed or clear, and refuses what must stay readable if it arrives sealed (V2.1) |
| `Hub\TenantHub.Branding.cs` | Sending a company's new look to its installed machines (V2.1) |
| `Hub\TenantHub.SessionLimit.cs`, `TenantHub.Bridge.cs` | Two sessions at once at most, and **copying straight from one session's machine to the other's** (`BridgeProblem` is the one rule) (V2.2) |
| `Hub\TenantHub.Update.cs`, `Services\UpdateService.cs`, `Services\UpdateSweep.cs`, `Pages\Owner\Updates.cshtml` | **Automatic updates** (V2.3): the release this server hands out (`/update/client.exe`, `/update/console.exe`), its rings and Pause, offering it to a few computers a minute, and what each computer reported |
| `Hub\HubRegistry.cs` | Finds the right company's hub |
| `Data\PlatformDb.cs` | The database: every table |
| `Data\Migrations\`, `Data\PgMigrations\` | Every change ever made to the database, for SQLite and for PostgreSQL (`PgPlatformDb.cs`), applied automatically. **Each change needs one in each** (document 05) |
| `Services\UnixSocket.cs` | How Caddy reaches the platform on the live server (`/run/rsp/platform.sock`), and trusting Caddy's forwarded addresses through it (V2.2) |
| `Services\Workflow.cs` | **Every ticket route and stage**, in one place |
| `Services\JobTypes.cs`, `Pages\Portal\JobTypes.cshtml`, `Hub\TenantHub.JobTypes.cs` | **Job types**: which categories and steps a company has switched off, the check that no route can break, the page, and telling open consoles (27 October) |
| `Services\TicketService*.cs` | Tickets: creating, moving between stages (`ActAsync` is the only thing that moves one), mentions |
| `Services\Permissions.cs` | **Who may do what.** Every permission check asks this |
| `Services\AuthService.cs`, `SignInGuard.cs`, `WebSignIn.cs`, `OwnerTwoFactor.cs`, `AccountLinks.cs`, `Passwords.cs` | Signing in, two-factor, lockouts, invitations and resets, password hashing |
| `Services\ClientService.cs`, `ContactService.cs`, `ClientImport.cs` | Customers, their people, importing a client list. `ClientService.Remove.cs` (V2.4): **removing a client**, everything on it moved to the right one first |
| `Services\PortalAddresses.cs` | **Every portal address** names things by the company's own number (`/portal/t/12`) or a file's random token, always looked up with the company in the same query (V2.4, §159) |
| `Services\TicketActivityService.cs` | **Latest activity** — every update on the tickets, newest first; who sees what is `TicketService.VisibleTo` (V2.4) |
| `Services\CompanyExport.cs` | **A company's data as one zip**, made in the background by `CompanyExportSweep` in the same file (Portal → Export, or ours for a closed company) (V2.4) |
| `Services\SiteChat.cs`, `CspReports.cs` | Our own site's chat bubble saying our name; the report-only content-security policy and its reports (V2.4) |
| `Hub\TenantHub.Invite.cs` | **A technician asking for outside help** in a session (V2.4, §155) |
| `Services\QuoteService.cs` | Quote requests, suppliers, items |
| `Services\HealthCheckService.cs`, `ReportPdfService.cs` | Receiving health check reports, making PDFs, emailing them |
| `Services\PayloadService.cs` | Builds each company's downloads (same files, their branding) |
| `Services\PlatformMailService.cs` | Sending email |
| `Services\StaffChatService.cs`, `WebChatService.cs` | Office chat and website chat |
| `Services\RecordingService.cs`, `TicketPhotoService.cs` | Session recordings and ticket photos |
| `Services\TicketIntake.cs`, `MailWire.cs`, `KnowledgeBaseService.cs`, `KbText.cs` | Tickets by email, and the knowledge base |
| `Services\Pricing.cs`, `Terms.cs`, `CompanyClock.cs` | Prices and seats, the signup terms, every time in the company's own time zone |
| `Services\FreePortal.cs` | The free version's portal: the **one** list of pages it may open (V2) |
| `Services\RecurringTaskService.cs` | **Recurring tasks**: every rule, and opening each due ticket at 06:00 company time (V2.1) |
| `Services\TicketService.Edit.cs`, `TicketService.Back.cs`, `TicketAttachmentService.cs`, `ClientUpdates.cs` | Editing a ticket, **going back a step** (every stage move writes a snapshot so it can be undone), files and photos on comments, emailing the client from a ticket (V2.2) |
| `Services\PayfastWire.cs`, `PayfastService.cs`, `CompanyBilling.cs`, `CompanyBilling.Late.cs` | **PayFast**: the signatures, checkouts and notices; switching a company on when it pays; keeping the amount true; late payments, reminders, suspension and cancelling (V2.2) |
| `Services\PilotService.cs` | **Pilot companies**: the one-use link and every rule (V2.2) |
| `Services\PlatformAdmins.cs`, `AdminCompanies.cs`, `ControlServers.cs`, `ControlSweep.cs` | **Platform admins** and their levels; every change to a company goes through `AdminCompanies`; the **control dashboard** reading other servers (V2.2) |
| `Services\SitePages.cs`, `SitePreview.cs`, `ReviewService.cs` | The real public website (hidden behind the holding page), previewing it, and companies' reviews (V2.2) |
| `Terms\` | The terms text: shared, one per region, and the free version's section |
| `Pages\` | The website: public pages, `Portal\` (office staff), `Owner\` (platform owner and admins), `Account\` (invitation and reset links). V2 added `Portal\Machines` (the free version's computers), `Portal\Upgrade` and `Portal\RemoveDevice`; V2.1 `Portal\Recurring`; V2.2 `Pilot` (a pilot company's sign-up), `SignupPaid`, `Portal\Review`, `Portal\_EditTicket`, `Portal\_Attach`, and the owner's Admins, Pilots, PayFast and server pages. `Pages\Portal\TicketWorkAttribute` is the **allow-list** of ticket-page actions an owner-only login may use |
| `Pages\Portal\_Timeline.cshtml` | How a ticket's "story" is drawn |
| `wwwroot\` | Browser files: `site.css` (all styling), and scripts for client search, phone layout, photos, chat, browser support, website chat; V2.1 `sounds.js` (the ding and ring) and `e2e.js` (end-to-end encryption in the browser — its list of readable messages must equal `Shared/SessionSeal.cs`'s, which `tools/e2ecore` checks) |
| `appsettings.json` | Settings. On the live server these are overridden by environment variables (`RSP_...`) in the service file and `appsettings.live.json` |

### `RemoteSupport.Technician` — the console

| File | What it does |
|---|---|
| `Program.cs`, `LoginForm.cs`, `TwoFactorDialog.cs`, `TrustedSignIn.cs` | Starting up and signing in |
| `DashboardForm.cs`, `DeviceList.cs` | The main window and the machine list |
| `TechLink.cs` | The console's connection to the server |
| `SessionForm.cs`, `SessionForm.Voice.cs`, `SessionLink.cs` | The session window, its toolbar and tools, and reconnecting |
| `SessionForm.Between.cs`, `SessionForm.Bridge.cs` | Copy in one session window, paste in the other: through this PC, or straight between the two machines (V2.2) |
| `ScreenView.cs` | Drawing the customer's screen and sending mouse and keyboard |
| `ScreenView.Shortcuts.cs` | Sending Windows shortcuts (Win, Alt+Tab, …) to the customer while the picture has the focus (V2.1) |
| `IncomingCallPopup.cs` | The centre-screen call alert |
| `TicketsForm.cs`, `NewTicketDialog.cs` | Tickets in the console |
| `DragOut.cs`, `SessionRecorder.cs`, `AudioPlayer.cs` | Dragging files out, recording, sound |
| `StaffChatForm.cs`, `WebChatForm.cs` | Office chat, website chat |
| `SessionForm.Print.cs`, `MachineWatchForm.cs` | Remote printing; Processes and Performance |
| `CodeEntryDialog.cs`, `VisitorPopup.cs`, `GuestForm.cs` | Outside help: connecting with a code, watching a visitor, the guest console |
| `RemoveDeviceDialog.cs`, `ReasonDialog.cs` | Removing a machine with a reason (V2); why you are connecting, and the unattended password |
| `ConsoleDownload.cs` | The single-.exe console setting itself up |
| `ConsoleUpdate.cs` | The console updating itself before its sign-in window, and putting the old one back if the new one does not start (V2.3) |
| `Trace.cs` | The diagnostic log |

### `RemoteSupport.Host` — does the work on the customer's computer

| File | What it does |
|---|---|
| `HostEngine.cs` | Runs the session: separate threads for capture, pointer and input |
| `DxgiCapture.cs`, `ScreenCapture.cs`, `LinkController.cs` | Screen capture (fast and fallback) and automatic quality |
| `StillRefiner.cs` | Sharpening a still screen after a change, so the picture never stays blurry (V2.2) |
| `HostEngine.Bridge.cs` | Sending or receiving copied files straight from another session's machine (V2.2) |
| `InputInjector.cs`, `DesktopFollower.cs` | Mouse and keyboard, and following Windows onto secure screens |
| `ConsentPrompt.cs` | The customer's permission prompt |
| `ClipboardAccess.cs`, `FileTransfer.cs`, `ExplorerSelection.cs`, `UserProcess.cs` | Clipboard, files, and the helper that runs as the logged-in person |
| `ScriptRunner.cs` | Running scripts |
| `AudioCapture.cs`, `AudioDiagnostics.cs`, `VoiceCall.cs` | Sound and voice |
| `PrivacyScreen.cs` | Blanking the customer's monitors |
| `ControlledNotice.cs` | The free version's *Being controlled remotely* bar (V2) |
| `PrintRedirect.cs` | Adding the technician's printer, and printing their file on the customer's printer |
| `UserContext.cs` | Finding the logged-in person's name and folders (the host runs as the system) |
| `MachineIdentityStore.cs` | The computer's own identity key for end-to-end encryption, readable only by the system (V2.1) |

### `RemoteSupport.Client` — the customer's tray app and the downloadable .exe

`MainForm.cs` (the window and tray icon), `RequestDialog.cs` (asking for help), `ChatPopup.cs`,
`SessionBanner.cs` (the corner strip), `UnattendedPasswordDialog.cs`, `MicQuestion.cs`,
`HostSpawner.cs` (starting a host), `Launcher.cs` + `LauncherForm.cs` + `ClientPayload.cs` (the
one downloadable .exe: Get help now / Install), `FreeInstallForm.cs` (the free version's *Add this
computer*, V2), `OutsiderCodeDialog.cs` (a code for outside help). Uninstall… is in `MainForm.cs`.

### `RemoteSupport.Agent` — the background service for unattended access

`AgentService.cs` (the service, and `UninstallSelf` when its machine is removed), `SessionLauncher.cs`
(starting the host with system rights inside the logged-in session — Windows services cannot see the
screen themselves), `HealthCheckKeeper.cs` (installing and removing the health check). Since V2.3:
`AgentUpdate.cs` (taking an update, and `--apply-update`, the short-lived process that swaps the files,
restarts the service and rolls back).

### `RemoteSupport.Shared` — used by everything

`Protocol.cs` (**every message the programs exchange**), `WsConnection.cs` (the connection),
`SecureTransport.cs` (refuses unencrypted connections over the internet), `Totp.cs` (two-factor
codes), `DeviceIdentity.cs`, `MachineFingerprint.cs`, `FileSetTransfer.cs` (moving folders safely),
`ClipLedger.cs`, `DownloadIdentity.cs`, `SystemSpecCollector.cs`, `Branding.cs`, `WakeOnLan.cs`,
`MachineWatcher.cs` (`OutsiderAccess`, the outside-help rule, is inside `Protocol.cs`). Since V2: **`FreePlan.cs`** (every free-version limit, and
`RemovalReasons`), `FreeSetup.cs` (setting up a free computer), `ClientUninstall.cs` (running the
uninstaller, shared by the agent and the tray app). Since V2.1: **`SessionSeal.cs`** (end-to-end
encryption: the seal, the handshake `E2eHandshake`, and the list of messages the server must still
read), `MachinePins.cs` (each console's memory of every computer's identity), `BrandRefresh.cs` (a
company's new look on an installed machine), `Chimes.cs` (the ding and ring, made in code). Since V2.2:
`SessionLimit.cs` (two sessions at once), `BridgeSeal.cs` (sealing files sent between two machines),
`TicketSort.cs` (the ticket list's orders), `JobParts.cs` (a job waits for its linked parts),
`SupportRequest.cs` (a request's subject and description). Since V2.3, automatic updates:
**`AppVersion.cs`** (version numbers), **`UpdateTrust.cs`** (the signature check every update passes),
`UpdateRunner.cs` (download, check, wait, hand over; and the apply step), `UpdateSwap.cs` (swapping files and
putting them back), `UpdateByHand.cs` (the script for the one update by hand).

### `RemoteSupport.Media` — video and sound

`H264Encoder.cs` / `H264Decoder.cs` (Windows Media Foundation), `GpuColourConverter.cs`, `Nv12.cs`,
`ScreenRecorder.cs` (writing .mp4), `PcmPlayer.cs`, `EchoCancelledMic.cs`.

### `RemoteSupport.UI` — the look of the Windows programs

`Theme.cs` (colours, light and dark), `Controls.cs` (rounded buttons, cards, inputs), `BrandIcon.cs`
(making icon files from a company's logo, and — V2.1 — making it every console window's icon),
`WindowPlacement.cs` (remembering window positions), `GroupHeaders.cs`, `Sounds.cs` (playing the ding
and ring, and muting them, V2.1).

### `RemoteSupport.Server` — the old single-company server

Kept in the solution but **not used live**. Superseded by the Platform.

---

## Where to start when…

| You need to… | Start at |
|---|---|
| Understand a message between programs | `Shared/Protocol.cs`, then search for the message name in `TenantHub.cs` |
| Change a ticket route | `Platform/Services/Workflow.cs`, then run `tools/workflowcheck`, `tools/routecheck` and **`tools/jobtypecheck`** (which walks every route with every combination of Job types switches) |
| Change who can do what | `Platform/Services/Permissions.cs`, then run `tools/rolecheck` |
| Change sign-in | `Platform/Services/AuthService.cs`, then run `tools/securitycheck` |
| Fix something on the customer's screen side | `Host/HostEngine.cs`, and read `HANDOVER.md` §4 first (Windows permission levels and secure screens) |
| Add a control to a Windows window | Read `HANDOVER.md` §30 first: measure heights from the font, never type a number |
| Change the database | Edit `Data/PlatformDb.cs` and add a migration (document 05) |
| Change what the free version may do | `Shared/FreePlan.cs` (the limits), `Platform/Hub/TenantHub.Free.cs` (the hub), `Platform/Services/FreePortal.cs` (the portal), then run `tools/freecheck`, `tools/freeportal`, `tools/freeinstall`, `tools/upgradecheck` |
| Change removing a machine | `TenantHub.Free.cs` (`RemoveDeviceAsync`, `RemoveCoreAsync`), then run `tools/removecheck` |
| Add or change a message a session sends | Read `HANDOVER.md` §112 first. Ask `BinKind.Of(b[0])`, never `b[0]` (a sealed frame sets the top bit); a message the server must read goes in the clear list in **both** `Shared/SessionSeal.cs` and `wwwroot/e2e.js`. Then run `tools/e2ecore` and `tools/e2ewire` |
| Change anything that lets a technician in unattended | Ask `TenantHub.MayGoUnattended`, never the permission alone (a reinstalled machine waits for the office, §111). Then run `tools/reinstallcheck` and `tools/unattendedpw` |
| Add a new way to move a ticket's stage | It must write `TicketNote.Before` (the snapshot that Back puts back) — read `HANDOVER.md` §128; run `tools/ticketstepcheck` |
| Add an action to a ticket page | Name it in `TicketWorkAttribute` only if an owner-only login may use it (§146); run `tools/rolecheck` |
| Touch payments | Read `docs/NEXT-SESSION-PAYFAST.md` and `HANDOVER.md` §134–137; run `tools/payfastcheck` and `tools/signuppaycheck` on both databases |
| Add a public web page | Add it to `go-live.sh`'s holding list; run `tools/sitecheck` |
| Change automatic updates | Read `HANDOVER.md` §147–§148 first. The signature rule is `Shared/UpdateTrust.cs` — never add a way round it; the rollout is `Platform/Services/UpdateService.cs`. Run `tools/updatetrust`, `updatewire` (both databases), `updateapply`, `consoleupdate`, `updatebyhand`, `versioncheck` |
| Add a file customers' computers must get | It reaches them in an update only if it is in the packed customer app AND `UpdateSwap.TopFiles` (or is a `healthcheck\*.ps1`); it must be signed |

## Reading the engineering record

`docs/HANDOVER.md` has 146 numbered sections, mostly in date order. Each one says what was asked
for (often in Clint's own words), what was built, **what went wrong on the way**, which test tool
proves it and what count it should give, and what still needs doing. When a section says it
supersedes an older one, the newer one wins. It is long but it is the fastest way to avoid repeating
a mistake that has already cost a day.
