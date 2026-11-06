# 02 — Every feature, and how it works

Each feature has three parts: **what it does**, **how it works** in plain terms, and **where it
lives** in the code (for developers). A mark after the title says how far it has been proven:

- **[Proven]** — used on real computers and confirmed working.
- **[Built]** — written and passed its automatic tests, but **not yet tried on real customer
  computers**. Treat these as "probably works, needs checking". The checklist is
  `docs/TEST-3.6.md`.

Document 07 has the full picture of what is proven and what is not. **"V2"** after a mark means it
was added between V1 of this pack (2 October 2026) and V2 (6 October 2026) — document 10 lists them all;
**"V2.1"** (or "after V2") means it was added on 7 October 2026 — document 11; **"V2.2"** means it was
added between 8 and 15 October 2026 — document 12; **"V2.3"** on 16 October 2026 — document 13; **"V2.4"**
between 17 and 27 October 2026 — document 14; **"V2.5"** on 28 October 2026 — document 15.

---

## Part A — Remote control

### A1. Seeing the customer's screen [Proven]

**What:** The technician sees the customer's screen live, on any of their monitors, docked in the
console, in its own window, or full screen.

**How:** The host takes a picture of the screen many times a second using Windows' own fast screen
copy (*Desktop Duplication*). It compresses the pictures into video using **H.264** — the same
compression as online video — using the computer's graphics chip where it can. Only what changed
is sent. Scrolling a document went from 2.8 MB per second to under 0.2 MB per second when this was
added. If a computer cannot do H.264, it falls back to an older method (small JPEG squares of the
parts that changed), so it still works, just using more data.

**Automatic quality:** if the internet line is struggling, the host lowers the picture quality and
frame rate on its own, and raises them again when the line recovers (`LinkController`).

**Fixed 6 October (V2):** on computers whose graphics chip converts colours for the compression, a
program opened or closed sometimes did not appear until the technician pressed Refresh. The
converter was asked for its result only once per picture; it is now asked until it has finished
(`Media/GpuColourConverter.cs`, `HANDOVER.md` §99). Not yet seen on the affected machine.

**A picture that never stays blurry (V2.2, Built):** on one test machine the picture arrived blocky and
never became clear. Two causes: an automatic "start again" picture every 24 frames threw the sharpened
picture away, and nothing sharpened a screen that had stopped changing. Now those pictures are a minute
apart, and **a still screen is encoded again for two seconds after any real change** so it comes out
sharp (`Host/StillRefiner.cs`). A blinking cursor does not count as a change. When the server has to drop
a picture for a slow technician, it asks the customer's computer for a fresh one. Found on the way: a
Windows structure had been read wrongly since September (`HANDOVER.md` §142).

**Where:** `Host/DxgiCapture.cs`, `Host/ScreenCapture.cs` (fallback), `Media/H264Encoder.cs`,
`Media/H264Decoder.cs`, `Technician/ScreenView.cs`.

### A2. Mouse and keyboard [Proven]

**What:** The technician's mouse and keyboard control the customer's computer, including on
several monitors, and the customer's real mouse pointer shape is shown.

**How:** The console sends each mouse movement and key press to the host, which "presses" them on
the customer's computer using Windows' input function (`SendInput`). Because the host runs with
full system rights, this also works on programs running as administrator, on Task Manager, and on
Windows' password prompts (UAC) and the lock screen.

**Windows shortcuts** (added 2026-10-07, after V2; Built): while the picture has the keyboard focus,
the Windows key and its shortcuts, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc, Alt+Esc and Print Screen act
on the customer's computer, not the technician's. Windows keeps these keys for itself, so the console
uses a keyboard hook that exists only while the picture is focused and takes only those keys.
Ctrl+C, Ctrl+V and every other key travel exactly as before. There is no on/off switch. Win+L and
Ctrl+Alt+Del cannot be taken by any program; the toolbar has buttons for both. Handover §110.

**Where:** `Host/InputInjector.cs`, `Host/DesktopFollower.cs`, `Host/CursorShape.cs`,
`Technician/ScreenView.Shortcuts.cs`.

### A3. Several technicians on one session [Proven]

**What:** More than one technician can watch and help in the same session.

**How:** The server simply sends the picture to every technician watching and passes every
technician's input to the host.

### A4. Surviving a dropped connection [Proven]

**What:** If the internet drops briefly, the session reconnects on its own.

**How:** The console keeps retrying for two minutes; the server keeps the session waiting for 90
seconds. If the line comes back in that time, the session carries on.

**Where:** `Technician/SessionLink.cs`, the hub's grace period in `Platform/Hub/TenantHub*.cs`.

### A5. System buttons [Proven]

**What:** From the session toolbar: **Ctrl+Alt+Del**, **Lock the machine**, **Open Task
Manager**, **Restart the machine** (and reconnect automatically after it comes back).

**How:** The host carries these out with system rights. Ctrl+Alt+Del needs a Windows policy
setting, which the customer installer switches on.

### A6. Copy and paste, both ways [Proven]

**What:** Text, files and whole folders copied on one computer can be pasted on the other, in
either direction, as if both were the same computer.

**How:** Text is simply sent across. Files are different: when you copy a file, Windows only
remembers *where it is*, not its contents. So the product first **sends the file contents
across**, then tells the other computer's clipboard where the copy landed. Pasting then works
normally in Windows Explorer.

A small helper program running **as the logged-in person** (not as the system) does the final
clipboard step on the customer's side, because Windows Explorer refuses to paste a file list put
there by a more powerful account. This took several days to discover and is written up in
`HANDOVER.md` §39 and §40.

Privacy: the clipboard is only shared while a technician is connected, and stops the moment they
leave.

**Every movement is written on the ticket** (what, which way, how big, when) [Built].

**Where:** `Host/ClipboardAccess.cs`, `Host/UserProcess.cs`, `Shared/FileSetTransfer.cs`,
`Shared/ClipLedger.cs`.

### A7. Moving files: drag and drop, and the Files window [Proven, drag-off-the-picture Built]

**What:**
- Drag a file or folder from the technician's desktop **onto** the customer's screen — it lands on
  the customer's desktop. [Proven]
- The **Files** window shows the customer's drives and folders; drag files out of it to the
  technician's computer. [Proven]
- Drag a file straight **off the picture** of the customer's desktop onto the technician's
  desktop. The product reads what the customer has selected in Explorer. [Built]

**How:** Files are sent in small pieces (64 KB). Every received path is checked so a file can never
be written outside the folder it was meant for (this blocks a known type of attack). The customer
is told in plain words what was moved.

**Where:** `Host/FileTransfer.cs`, `Host/ExplorerSelection.cs`, `Technician/DragOut.cs`.

### A8. Running scripts [Proven]

**What:** The technician can run a PowerShell or batch script on the customer's computer and watch
the output appear live.

**How:** The host saves the script to a temporary file and runs it **with system rights**, sending
back each line of output and the final result code.

**Where:** `Host/ScriptRunner.cs`.

### A9. Hearing the customer's computer [Proven]

**What:** **Listen** plays the customer's computer sound on the technician's speakers. The **Sound**
window explains *why* there is no sound on the customer's machine (muted, wrong speakers chosen,
driver problem) instead of leaving the technician guessing.

**Where:** `Host/AudioCapture.cs`, `Host/AudioDiagnostics.cs`, `Technician/AudioPlayer.cs`.

### A10. Talking to the customer, both ways [Built]

**What:** **Talk to the customer** sends the technician's voice to the customer's speakers. The
customer is asked whether to use their microphone too — **every session, and never for unattended
sessions**. Echo cancelling is used at both ends.

**Where:** `Host/VoiceCall.cs`, `Media/EchoCancelledMic.cs`, `Technician/SessionForm.Voice.cs`.

### A11. Privacy screen [Built]

**What:** Blanks the customer's monitors (they see black) while the technician still sees and works
normally. Useful in open offices.

**How:** Black windows that Windows is told to leave out of screen capture. If it cannot work
properly on that computer, it **refuses** rather than half-working, and says so.

**Where:** `Host/PrivacyScreen.cs`.

### A12. Recording a session [Built]

**What:** Tools → **Record this session**. A red marker shows while recording. The video is saved
by the technician's console and uploaded to the session's ticket. Only someone in the session can
attach one; only a supervisor or the owner can delete one. The customer is told.

**Where:** `Media/ScreenRecorder.cs`, `Technician/SessionRecorder.cs`,
`Platform/Services/RecordingService.cs`. Recordings are stored in the server's `data/recordings`.

### A13. Session chat and the Info window [Proven]

**What:** Chat with the customer during a session (it pops up on their side and can be moved).
**Info** shows the customer computer's details: Windows version, processor, memory, disk, and
which account the host is running as.

### A14. Remote printing, both ways [Built — V2]

**What:** Tools → **Print on my printer**: a printer named after the technician (for example
"Sue Tech's printer") appears on the customer's computer, and whatever the customer prints to it comes
out on the technician's own printer. Tools → **Print a file on their printer…**: the technician picks
a file and it prints on the customer's default printer.

**How:** No printer driver of ours: the added printer is Windows' own *Microsoft Print to PDF* writing to
a file, which the host picks up and sends to the console, which prints it. The printer is removed when
the technician leaves the session. Known limit, said to the technician: two documents printed within a
moment can arrive as one.

**Where:** `Host/PrintRedirect.cs`, `Technician/SessionForm.Print.cs`, `Media/PdfPages.cs`. `HANDOVER.md` §95.

### A15. Wake-on-LAN [Built — V2]

**What:** Right-click an **offline** machine → **Wake up**. Another of that customer's machines on the
same network sends the wake-up signal — nothing to set up on the customer's router. The technician is
told which machine sent it, or why it could not be sent.

**Where:** `Platform/Hub/TenantHub.Wake.cs`, `Shared/WakeOnLan.cs`. `HANDOVER.md` §94.

### A16. "Being controlled remotely" [Built — V2, the Free and Basic versions only]

**What:** On a Free or Basic computer, which has no tray app, a small amber bar at the top centre of
every monitor says *Being controlled remotely by …* for the whole session, naming who is connected.

**How:** Like the privacy screen, the bar is left out of screen capture, so it is on the person's
screen but **not** in the technician's picture, and clicks pass straight through it.

**Where:** `Host/ControlledNotice.cs`. `HANDOVER.md` §104.

### A17. Two machines at once, and copying straight between them [Built — V2.2]

**What:** A technician can have **two sessions open side by side**, one per monitor — two at most
(`Shared/SessionLimit.cs`; the console refuses a third before asking, and so does the server; reconnecting
to one already open never counts). **Copy on one machine, paste on the other:** files copied on machine A
are held, and when the technician clicks into machine B's window and pastes, the server joins the two
sessions for that one copy and **A's computer sends the files straight to B's** — through the server, but
never through the technician's PC or line. A key made for that copy is handed to each machine inside its
own sealed session, so the server cannot read the files. If an older program is involved, or the server
says no, the files go through the technician's PC as before.

**Where:** `Platform/Hub/TenantHub.Bridge.cs` (`BridgeProblem` is the one rule), `Host/HostEngine.Bridge.cs`,
`Shared/BridgeSeal.cs`, `Technician/SessionForm.Bridge.cs`, `SessionForm.Between.cs`. `HANDOVER.md` §143–144.

---

## Part B — Access and permission

### B1. Asking for help (attended support) [Proven]

**What:** The customer clicks **Request Support** in the tray app and must say what is wrong. Every
signed-in technician at that IT company is rung. The reason is the biggest text on the alert.

**How:** The alert is always on top, flashes the taskbar, and never steals the keyboard from
whatever the technician is typing. The first to accept gets the call.

**Since 9 and 13 October (V2.2, Built):** the request has a short **subject** and an optional
**description**, and a tick to send **a picture of the screen**, previewed first, which lands on the
ticket. The app **remembers the customer's name** (per Windows user). Because customers usually minimise
everything to reach the desktop shortcut, the picture often showed only the desktop: the window now
**says so**, and **Take a new picture** hides it while they bring the right window up. It never restores
their windows for them, on purpose (`Client/ScreenSnap.cs`, `Client/LastContact.cs`, §122 and §129).

**Since 18 and 23 October (V2.4, Built):** the name typed into a request **names the machine** while it is
not linked to a client — shown in grey italic in the console and the portal instead of DESKTOP-ABC123, the
latest name winning — and linking it offers that person as a contact (`Shared/OwnerNames.cs`, §150). The
**installer asks who the computer is for** (name, surname, email) and that does the same from the start; the
server keeps it once per install, so a later request's name still wins (`Shared/GivenPerson.cs`, §156).

**Where:** `Client/RequestDialog.cs`, `Technician/IncomingCallPopup.cs`.

### B2. The permission prompt (consent) [Proven; the system-level version Built]

**What:** Before anyone sees the customer's screen in an attended session, the customer sees a
branded prompt naming the technician. If nobody answers within the time limit (60 seconds by
default), the answer is **no**.

**How:** The host connects to the server first but **does nothing at all** until the customer says
yes. The technician sees "Waiting for … to accept" with a countdown and a Cancel button.

**Since 16 October (V2.4, Built):** the prompt **comes to the front and stays there**. Windows takes the
always-on-top mark off a window a background program shows, so it used to open behind whatever the person
was using (`UI/ComeForward.cs`, §149).

**Where:** `Host/ConsentPrompt.cs`, `Technician/ConsentWaitForm.cs`.

### B3. Unattended access — a switch the customer holds [Proven]

**What:** A technician can connect without anyone at the computer — but **only if the customer has
ticked "Allow unattended access" in their tray app**. They can untick it at any time.

**How:** Unattended access needs **both** the customer's permission (stored on the server) **and**
the agent service running and connected. The console shows which machines can actually be reached
unattended right now. If the agent is not connected, the server says so instead of silently
falling back to asking the customer.

**A reinstalled machine waits for the office (V2.1, Built):** a machine recognised after a reinstall
keeps its history and its tick, but goes attended until a supervisor or the owner confirms it is the
same machine — right-click → Confirm in the console, or the Devices page. `HANDOVER.md` §111.

**Where:** `Agent/AgentService.cs`, `Agent/SessionLauncher.cs`, permission rules in the hub
(`TenantHub.MayGoUnattended` is the one question every unattended route asks).

### B4. An unattended password [Built]

**What:** The customer can set a password on their own machine in the tray app. Then nobody —
**including the IT company's owner** — can connect unattended without typing it. This is meant for
people who connect to their own home PC from the office. There is deliberately **no override** in
the office; a forgotten password is removed at the machine itself.

**How:** The server stores only a scrambled one-way version (a "hash") of the password. Five wrong
tries lock that machine for 15 minutes.

**Remembered, per technician (V2):** once a technician types a machine's password correctly, they are
not asked again — on any computer they sign in on. A colleague who never typed it still is. The
server keeps only *that it was typed right* (never the password); changing or removing the password at
the machine forgets it for everyone, and so does removing the technician. Right-click → **Forget saved
password** forgets one's own; a supervisor or the owner can see **who** has it saved (names only) and
clear one or all. `HANDOVER.md` §100.

**Where:** `Client/UnattendedPasswordDialog.cs`, `Shared` (`UnattendedPasswords`), the gate in the
hub's connect code, `Platform/Hub/TenantHub.SavedPasswords.cs`.

### B5. Exit means offline [Built]

**What:** If the customer chooses **Exit** in the tray app, the machine goes offline for remote
support completely — the agent stops accepting sessions too — until the tray app is started again.

### B6. Outside help — let in by the customer with a code [Built]

**What:** A customer sometimes needs help from somebody OUTSIDE their IT company — their accountant,
or another IT company that does something different. Where the IT company has switched it on (off by
default; per company and per machine, supervisors and owner only), the person at the computer makes
a **9-digit code** in the tray app ("Let someone else connect…"), after a warning about scammers. It
lasts 10 minutes and works once. A technician of **another company on the platform** types it in
their own console ("Connect with a code…"). The person at the computer is **always asked** to Allow,
told by name and company that this is not their IT company. The visitor gets the **screen, mouse and
keyboard only** — no files, scripts, clipboard or chat. The IT company's consoles get a pop-up and
can **watch** or **end** it; when the visitor leaves, the session ends. Both companies keep a record.

A **guest with no account** (the accounting firm) downloads the **guest console** from `/guest` —
nothing to install, no sign-in — and types their name and the code; everything they are shown as says
"a guest — not checked".

Or they choose **Connect in this browser** on the same page (`/guest/web`) and do the same with nothing
downloaded at all.

**Files** only if the customer ticks *Also let them copy files* in the Allow prompt, for that session —
and then only files, never the clipboard or anything else. (The browser page has no file tool; it
says to use the guest console.)

**The technician asks, too (22 October, V2.4, Built):** in a session, **Tools → Get outside help…** makes a
code for that session and a message to Copy, WhatsApp or Email (the browser link carries the code after `#`,
so it is in no server log). Whoever types it waits for **the technician who made it** to let them in, files
on a tick. **The person at the computer is told, not asked** (Clint's decision). The session stays the IT
company's: the outsider leaving or being removed takes only them out, and when nobody of the company is
left they go too. Each company can switch this off on Portal → Outside help (on by default); it does not
depend on the customer's own outside-help switch (`Hub/TenantHub.Invite.cs`, §155).

**Where:** `Shared` (`OutsiderAccess`), `Platform/Services/OutsiderCodeService.cs`,
`Platform/Hub/TenantHub.Outsiders.cs` (the allow-lists), `Client/OutsiderCodeDialog.cs`,
`Technician/CodeEntryDialog.cs`, `Technician/VisitorPopup.cs`, `Technician/GuestForm.cs`, `Platform/Hub/GuestVisitor.cs`, `Platform/wwwroot/guestview.js`.
Handover §87, §89, §90, §91 and §92.

### B7. Removing a machine, with a reason [Built — V2]

**What:** For when machines are upgraded, replaced, or were somebody's private device. A **supervisor
or the owner** right-clicks a machine in the console (**Remove this computer…**) or presses **Remove**
beside it on the portal's Devices page, and picks why: **Upgraded, Replaced, Private device, No longer a
customer, or Other** (Other needs a note).

**How:** The machine leaves every list at once. Its **tickets, sessions and reports stay**. The removal
is recorded, and the Devices page ends with a **Removed machines** list: what, when, who, why and the
note. If the machine is **online**, it **uninstalls itself** (the service, the files, the shortcuts and
its health check). If it is **offline**, it is refused when it next connects, and the program stays on
it until somebody removes it. A removed install can never put itself back; installing again adds it as
new.

**Where:** `Platform/Hub/TenantHub.Free.cs` (`RemoveDeviceAsync`, one removal for both versions),
`Shared` (`RemovalReasons`, `ClientUninstall`), `Agent/AgentService.cs` (`UninstallSelf`),
`Technician/RemoveDeviceDialog.cs`, `Pages/Portal/RemoveDevice.cshtml`. `HANDOVER.md` §106.

### B8. Uninstall, from the tray app [Built — V2]

**What:** A customer who no longer wants the product chooses **Uninstall…** in the tray menu. It asks
once; Windows then asks for administrator permission (No changes nothing). The program is removed, and
the IT company sees the machine leave its list as **"Uninstalled at the machine"**, by the person who
was signed in there.

**Where:** `Client/MainForm.cs` (`UninstallAsync`), `deploy-client/Uninstall-Client.ps1` — the same
script the agent uses, now kept in the program's folder for this. An install made before 6 October does
not have it, and says so. `HANDOVER.md` §106.

---

## Part C — The customer's side

### C1. One download per IT company [Built]

**What:** Each IT company has a page, `theremotesupportprogram.com/get/<company>`, with its own
logo and colours. The customer downloads **one .exe file**, which offers **Get help now** (run
without installing) or **Install**.

**How:** Every company's .exe is the same file. When it runs, it works out which company it came
from (from the download's own name and Windows' download record) and fetches that company's
branding. This is what lets one signature cover every reseller.

**Where:** `Client/Launcher.cs`, `Client/LauncherForm.cs`, `Shared/DownloadIdentity.cs`,
`tools/Pack-ClientExe.ps1`, `deploy-client/Install-Client.ps1`.

**And afterwards (7 October 2026, HANDOVER §113, not yet seen on a real machine):** when a company changes its
logo, colours, wording or permission-prompt settings, every installed machine takes the new look — at once if it
is on, otherwise when it next connects. The server sends only what a person sees, never where the machine connects
or whose it is. The machine keeps it beside what it was installed with; the tray app restarts itself quietly to
show it, only when nobody is using it. `Shared/BrandRefresh.cs`, `Platform/Hub/TenantHub.Branding.cs`,
`tools/brandpush`.

### C2. The tray app [Proven; Uninstall… Built in V2]

The menu: **Request Support**, **Chat with a Technician**, **Allow unattended access**, **Unattended
password…**, **Let someone else connect…** (only where outside help is on), **Uninstall…** and
**Exit**. Double-click opens the window. A small strip in the corner shows while a session is live.

On the **Free and Basic versions** there is no tray app at all: the downloaded file asks for the computer's
password (on Basic it may be left blank — then the person there is asked each time, by the background service), installs
only the background service, and leaves nothing on the screen (Part H).

### C3. Support in a web browser [Built]

**What:** `…/help/<company>` — a customer without the app can fill in a form, ring the technicians,
and share their screen from the browser. **View only**: no browser lets a website control a
computer. They become a contact and a ticket. Off by default; switched on with opening hours in
the portal.

**Where:** `Platform/Hub/TenantHub.Web.cs`, `wwwroot/help.js`, `Pages/Help.cshtml`.

### C4. Website chat [Built — needs the live site]

**What:** An IT company pastes one line of code onto its own website. A chat bubble appears; visitors
give a name and an email or phone number and chat. It rings everyone signed in, and **the first to
reply takes it**. If nobody is available or it is outside hours, the message becomes a ticket.
Since 2026-10-15 a chat can be **handed over** to anybody else signed in — by the person answering, a
supervisor or the owner, or by anybody while nobody has answered; the visitor is told who has taken
over, and the new person hears the dings from then on (HANDOVER §145).

**Where:** `Platform/Services/WebChatService.cs`, `Pages/WebChatFrame.cshtml`, `Technician/WebChatForm.cs`.

### C4b. Tickets by email and a contact form [Built — not yet against a real mailbox]

**What:** A customer emails the IT company's support address, or fills in a contact form on its
website, and a ticket opens by itself. If their email address is on file, it is linked to their
client. If only their company is recognisable (by the part after the @), the ticket says so and
offers one button to add them as a contact. They get an automatic reply with the ticket number, and
when they answer that reply it goes onto the same ticket. Out-of-office replies, bounces,
newsletters and spam are left alone. Works with any mail host: the platform either reads the
company's mailbox (cPanel, Gmail with an app password, Zoho…) without changing anything in it, or the
company forwards its mail to a mailbox of ours (the way for Microsoft 365). Microsoft 365 sign-in is
planned for later.

**Where:** `Platform/Services/TicketIntake.cs` (every rule), `Services/MailWire.cs` (reading and
sending mail), `Pages/Portal/MailIntake.cshtml`, `Pages/ContactForm.cshtml`.

**Updating the client from a ticket (added 9 October 2026, not yet against a real mailbox):** under
any comment box, *Also email this to the client* (unticked — most comments are internal), and *Email
to the client…* on a comment already posted, in the portal and the console. The client gets the fault
the ticket was opened for and that ONE comment, with the name of whoever wrote it, from the support
address above — never the rest of the ticket. Their reply comes back onto the same ticket, and
everybody on it is flagged until they open it. Offered only once Tickets by email is set up; signup
now asks for the address. Every rule is `Platform/Services/ClientUpdates.cs`; section 123 of the
handover is the record.

### C4c. A knowledge base [Built — not yet on hardware]

**What:** How things are done, written down once. A **General** section and a **Clients** section
(each article is General or belongs to one client). Everybody at the company can read, write and
edit; every save is kept and an older version can be put back; only a supervisor or the owner can
delete. When a ticket's work is done, the people on it see **Add to knowledge base**, which opens the
ticket as a draft article — what was needed and what was done, as numbered steps — to be reworded and
saved. Both sections can be sorted into **folders and subfolders** (up to six deep), opened like folders
on a PC with the path shown above; a search looks through everything below where you are. Removing a
folder never deletes anything — what was in it moves up a level. A long web address can be shown as a
**button** with words of your choosing (or a web address on a line of its own becomes an *Open site*
button). Screenshots can be pasted straight in. A ticket, a client's page and the technician's session
window all say when that client has articles. A line that looks like a password is warned about.
Staff only — customers never see it.

**Where:** `Platform/Services/KnowledgeBaseService.cs` (every rule), `Services/KbText.cs` (how the
words are drawn — everything typed is made safe first), `Pages/Portal/Knowledge*.cshtml`.

### C5. The monthly health check [Proven in parts]

**What:** Once a month, a customer machine runs maintenance (with a restore point first) and a
read-only health check — disks, drive health, backups, updates, security — and a branded PDF report
is emailed to the IT company.

**How:** It is **off by default**. The IT company ticks *Monthly health check* on a customer's page
(usually for service-contract clients); every machine of that customer follows, and a single
machine can be switched on or off by itself. The agent service installs or removes it on the
machine and reports what it actually found. If the install fails (for example antivirus blocks it
for a moment), it retries after 1, 5 and 15 minutes.

The machine stores no email settings — it uploads its results, and the server builds the PDF and
sends the mail.

**Where:** `healthcheck/` (the PowerShell toolkit, with its own README), `Agent/HealthCheckKeeper.cs`,
`Platform/Services/HealthCheckService.cs`, `ReportPdfService.cs`.

### C6. Keeping itself up to date [Built — V2.3]

**What:** Every program on a customer's computer — the tray app, the background service, the program that
does the work in a session, the install scripts and the health check — updates itself when we release a new
version, and so does the technician's console. Nobody at the customer's computer is asked (Clint's decision).

**How:** Every build carries a version made from the moment it was built (**2026.10.16.1432** is 14:32 UTC
on 16 October 2026), shown in the console, the portal's Devices page and the control dashboard. A new
release goes to **our own company's** computers first; a Full admin or Owner moves it on to **the pilots**,
then **everybody**, on **Owner → Updates**, and can pause it. The server offers it to a few computers a
minute. Each computer:

1. downloads it from its IT company's own server;
2. checks every file is **signed by us**, not revoked, and **newer** than what it has — anything else is
   refused, and that version is never taken again;
3. waits until **nobody has been connected for 10 minutes**;
4. swaps the files (a running program is renamed aside, never overwritten; only the release's own files are
   touched — never the computer's identity, branding or settings) and restarts its service;
5. keeps the new version only if it **connects back within 5 minutes**; otherwise it puts the old files back
   by itself. A computer that rolls back **pauses the release** for everybody and emails the admins.

The console checks before its sign-in window and restarts itself in the new version; while open, it shows
**Update ready**.

**Computers installed before 16 October need one update by hand** — connect, then **Tools → Scripts → Update
this machine** — after which they update themselves. The portal's Devices page counts and marks them.

**Seen on hardware (19 October, V2.4):** the background services updated themselves — one PC took four
releases, about 20 seconds each, with no rollback. **The tray app never followed**: Windows answered "what
version is this file?" from the copy already running, so the tray app read the new file as its own old one.
It now reads the file itself (`UpdateTrust.FileVersionOnDisk` — never ask Windows for the version of a file a
program started from). Tray apps already running in the field are the old code and cannot notice: **each
computer needs one sign-out or restart** after that deploy, then follows by itself. `tools/updatestate.ps1`,
run on a computer through Scripts, says why it is behind (§152).

**Where:** `Shared/AppVersion.cs`, `UpdateTrust.cs` (the signature check), `UpdateRunner.cs`,
`UpdateSwap.cs`, `UpdateByHand.cs`; `Agent/AgentUpdate.cs`; `Technician/ConsoleUpdate.cs`;
`Platform/Services/UpdateService.cs` (every rule of the rollout), `UpdateSweep.cs`,
`Pages/Owner/Updates.cshtml`; `tools\BuildVersion.ps1`. HANDOVER §147–§148.

---

## Part D — The technician console

| Feature | What it does | Status |
|---|---|---|
| Machine list | Customer machines in **Attended** and **Unattended** tabs, grouped by customer and the person who owns each machine; online status; processor, memory, disk (red over 90% full), Windows version | Built |
| Incoming-call alert | Centre-screen, always on top, flashes, never steals typing | Proven |
| Ding and ring | A soft two-note **ding** for every chat message from somebody else (customer, office, website visitor); a soft **ring every four seconds** while a support request or a website visitor waits, stopping the moment it is answered, cancelled or dismissed. Our own sounds, made in code (`Shared/Chimes.cs`), the same in the console, the customer's tray app and the portal. A bell in the header mutes them on that PC (the portal has its own button, per browser) | Built (V2.1) |
| Tools menu | Chat, Files, Scripts, Sound, Info, Listen, Record, Privacy, Talk, Print on my printer, Print a file on their printer — each in its own movable window that remembers where it was put | Proven / Built |
| Wake up | Right-click an offline machine (see A15) | Built (V2) |
| Remove a machine | Right-click → Remove this computer…, with a reason — supervisors and the owner (see B7) | Built (V2) |
| Saved passwords | Right-click → Forget saved password; supervisors and the owner also see who has a machine's password saved (see B4) | Built (V2) |
| One look | Every button, box, list and menu rounded, in one colour rule, light and dark; checked window by window by `tools/lookprobe` | Built (V2) |
| The company's logo everywhere | Every console window — sign-in, sessions, tickets, chat, tools — and the console's shortcuts carry the company's logo, not Windows' default icon (`HANDOVER.md` §114) | Built (V2.1) |
| Encrypted badge | Each session window says *End-to-end encrypted* (green) or *Not end-to-end encrypted* (an older program is involved); if a customer computer's identity has changed, the console stops and asks first (document 03) | Built (V2.1) |
| Tickets window | Work tickets without opening a browser; draws exactly what the server says can happen next. The queue's headings sort, as in the portal (§165). New ticket and Edit offer only the categories the company uses, and ask for photos only if it does (Job types, F2b) | Built |
| Latest activity | Beside Tickets: every update on the tickets as it is posted — comments, stage changes, customer replies, assignments ("assigned it to you"), new tickets — newest first; all tickets or only mine; by client and by who posted it; the count of what is new since I last looked moves by itself. A click opens the ticket. A technician sees only what their Tickets list shows (`HANDOVER.md` §164) | Built (2026-10-26) |
| Link a machine | Right-click a machine to say which customer and person it belongs to, or **Unlink** it (V2.2) | Built |
| Two sessions at once | Two session windows side by side; copy on one, paste on the other (see A17) | Built (V2.2) |
| Off means offline | A machine switched off at the wall, asleep or off its network shows offline within about 45 seconds (the server notices the silence, §122) | Built (V2.2) |
| Reconnecting asks nothing | Connecting again within 30 minutes of the end of the last session carries on the same ticket without asking for a reason (§122) | Built (V2.2) |
| Chat from the list | Right-click a machine → Chat: the chat box switches to that machine, ready to type | Built |
| Processes and Performance | Right-click a machine → a live process list (CPU, memory, user) and graphs (processor, memory, disk, network) **without connecting** — nothing shows on the customer's screen, nobody is asked, no password, look only, nothing logged (the owner's decision). The Network card says whether each network is **Private or Public** (Public in amber) and the machine's **IP address**. Figures come from the agent, or the tray app where there is no agent | Built |
| Office chat | Chat with colleagues one to one or with everyone; @mentions pop up | Built |
| Light or dark | Each person chooses; applied when the console next starts | Built |
| Sign out | Header and tray menu. The X button hides to the tray on purpose | Built |
| Closes to tray | Keeps ringing while hidden | Proven |
| Download as one .exe | `…/tech/<company>` — sign in, download, it installs itself for that Windows user with shortcuts | Built |

---

## Part E — The office portal (website for staff)

Signed-in staff use `https://theremotesupportprogram.com/portal`. It also works on a phone: below
a certain width, lists turn into cards and the menu folds away.

| Page | What it does |
|---|---|
| Dashboard | Open jobs by status, unassigned jobs, today's call-outs, workshop jobs, stale jobs, machines online, machines not yet linked to a customer |
| Tickets | All jobs, filters (mine, unassigned, open, by client, by machine) and search. **Every column heading sorts** — click it, click again to turn it round (#, Type, Subject, Client, Assigned to, Status, Priority, Last updated, Due); **Group by customer** is a tick; an orange *Customer replied* while the customer's email is the newest entry (2026-10-26, `HANDOVER.md` §165) |
| New ticket | Category first, as a row of coloured buttons (each job type its own colour, V2.2), then the client (found by typing), the people assigned, an optional **due date** (§165), and the category's questions |
| A ticket | A **story**: the stage at the top, a numbered timeline below, a comment box always there (it grows as you type, up to 15 lines), and only the next allowed steps shown as buttons. **✎ Edit** changes the subject, description, appointment, **due date** (§165) or category; **Back to …** undoes the last step; files and photos can go on any comment (V2.2, Part F) |
| Latest activity | The same list as the console's (Part D), a tab beside Tickets for every role and a card on the Dashboard, with a count of what is new; each line opens the ticket at that entry (§164) |
| Clients | Every customer, searchable as you type. Each has a page: contacts (people), machines, open work, history, health check tick |
| Remove a client | A misspelling or a duplicate: everything on it (tickets, machines, people, knowledge base, recurring tasks) moves to the right client, which keeps its own details and only has its blanks filled; then it is deleted. Owner, supervisor or office administrator |
| Import clients | From another program's spreadsheet (.xlsx or .csv), with a preview; never overwrites; repairs common errors (lost leading zeros in South African phone numbers, garbled letters) |
| Devices | All machines, grouped by customer, unlinked ones last. Supervisors and the owner can **Remove** a machine with a reason; the page ends with **Removed machines** (V2) |
| Items and Suppliers | A shared list of parts, which refuses near-duplicates ("DDR memory" beside "memory DDR"); suppliers with their email addresses |
| Workload | Who is carrying which jobs, and the jobs nobody has |
| Sessions | Past remote sessions |
| Health checks | Every report received, its findings, the PDF, whether the email went |
| People | Add staff by name and email (they are emailed an invitation), tick their roles, reset their two-factor, sign them out everywhere |
| Branding | Logo, colours, support email and phone, permission-prompt settings, with a live preview |
| Job types | Which categories and which steps the company uses, card by card, with the way a job goes under each; a supervisor or the owner saves it (see F2b) |
| Downloads | The two links to hand out: customers' and technicians' |
| Browser support, Website chat | Switch on, set hours |
| My account | Change your own password (needs the current one and a code) |
| Billing | Plan, status, how to pay, the terms accepted and every price agreed; since V2.2 the card on file (PayFast), payments received, and cancelling (G6) |
| Write a review | A week after joining, the portal asks once for a short review (G8) |

The menu is a row of **tabs** across the top (since 9 October), the current page filled in.

A **free-version** company has a much smaller portal: Your computers, Downloads, My account and
Upgrade (Part H). Every other page sends it back to Your computers.

---

## Part F — Tickets and job routes

### F1. Tickets open and fill themselves in [Built]

Every remote session gets its own ticket automatically. Accepting a call assigns it. Connecting,
chat, scripts, clipboard and file movements, and the session's end and length are all written on
it. Bigger jobs can be made of several sessions (a "parent" ticket). Staff can also open tickets by
hand.

Since 9 October (V2.2): **linking a ticket to a bigger job no longer closes it** — it stays open, is
finished and closed on its own, and the bigger job **cannot close until its parts are done** (a pop-up
names them; `TicketService.ClosesJob`, `Shared/JobParts.cs`). A ticket can be marked **Part of / Not part
of** the job, and unlinked.

### F2. Six job categories, each with its own route [Built]

A **route** is the set of stages a job moves through. Each stage says what must be done before the
job can leave it, and who it lands with next. The routes were written by Clint and are recorded in
`docs/NEXT-SESSION-WORKFLOWS.md` in his words. All the rules live in **one file**,
`Platform/Services/Workflow.cs`, and both the portal and the console just draw what it allows.

| Category | The route, simplified |
|---|---|
| **General** (the default) | Done (closes) / Quote needed / To be invoiced. No client needed. |
| **Remote support** | To be invoiced / Do not invoice (service-contract client) / Do not invoice (needs supervisor approval) / Link to another open ticket of the same client |
| **Walk in** | Repaired at counter → to be invoiced / paid at counter / no invoice — or booked into the workshop |
| **Workshop** | Booked in (with at least three photos of the machine as it arrived) → Inspection findings → Repairable / Not repairable → quote, parts, fitting → Solved, to be invoiced (with what was done, time and amount) → invoiced → paid → collected (three photos out) or delivered. **Not repairable** offers an inspection fee, do not invoice, or (V2.2) **Damage report needed**: a note for the client, the report attached (required), time and amount, emailed to the client by default — never the time or the amount — then to be invoiced |
| **Call out** | Booked (can be rescheduled: new date and time, and why) → Work done → to be invoiced / paid on site (cash or card) — or the same ticket becomes a Workshop job |
| **Recurring task** (added 2026-10-07, after V2) | Set up ONCE — daily (or weekdays), weekly on chosen days, or monthly on a date or the last day — for maintenance, billing, readings or a type the company adds. A ticket opens for its people at 06:00 company time on each day it is due → Done (closes) / To be invoiced; the office may press both. The next opens even if the last is open (it shows as Overdue). Cancel stops future tickets only. Rules in `Platform/Services/RecurringTaskService.cs`. |

Common pieces across routes:

- **Quotes:** request prices from suppliers by email (one supplier in To, or several in Bcc), with
  the machine's details, and asking the delivery fee, whether each item is in stock, and the ETA.
  Each item can carry several photos of the part (up to 12), all attached to the email. The email is
  signed with one of the company's **signatures** (Suppliers page: lines of text, each with its own
  font, size and colour, and/or an image); with more than one, the person is asked which, and the one
  they used last is already ticked. Quote → sent → accepted (invoice, wait for
  payment, order parts, parts received, fitted) or rejected.
- **Billing is a lane, not a category:** office staff handle "To be invoiced", "Invoiced — awaiting
  payment" and closing. **A technician never closes a ticket.** When a job lands in billing, every
  office administrator (or, if there are none, the supervisors and owner) gets a pop-up, and a badge
  counts the jobs waiting for the office until they are dealt with.
- **"Do not invoice"** needs a supervisor's approval (except for service-contract clients).
- **Delivery instead of collection**, with the delivery's time added to the job.
- **Several machines:** booked in together as separate linked jobs, or as **one job** (for example a
  data transfer from an old PC to a new one — three photos of each).
- **Several people on one ticket**, all equal. Anyone working on the machine is added.
- **@mentions** stay flagged until the person replies; the console's Tickets button shows a count.
- **Photos** are shrunk before upload (2048 pixels, JPEG), uploaded one at a time with progress.
- **Workshop device passwords** are stored **encrypted** and shown only behind a Show button.
- **Abandoned** is available from any stage, with a reason.
- **Go back a step (V2.2):** every stage has **Back to …**, with a reason. Every stage move writes a
  snapshot of the job before it (`TicketNote.Before`), and Back puts every route field back; the undone
  step stays in the story, crossed through. It warns (never refuses) when an invoice, payment or email
  went with that step. A closed job is reopened only by a supervisor or the owner. **Any new code that
  moves a stage must write the snapshot** (`Services/TicketService.Back.cs`, §128).
- **Edit a ticket (V2.2):** subject, description, appointment and category, by anybody who can see the
  ticket; each save is one story entry saying what each field was. A new category restarts that route
  at its first stage; making it a Workshop job needs a technician and three photos in
  (`Services/TicketService.Edit.cs`, §130).
- **Files and photos on any comment (V2.2):** each uploaded as it is picked (photos made smaller first),
  20 MB each, a comment may be files alone, and they go as attachments when the comment is emailed to
  the client (15 MB together at most, refused before anything is posted). Pictures and PDFs open in the
  browser; anything else downloads (`Services/TicketAttachmentService.cs`, §128).
- **Emailing the client from a ticket** — see C4b.

### F2b. Job types: each company chooses its categories and steps [Built 27 October, not on hardware]

Clint: some IT companies *"wont want all the categories, or in some instances may not want all the stages of a
category"* — for example no before-and-after photos — and they must be able to say so *"without breaking the flow."*

- **Portal → Job types.** A card for each category, its steps in order: a tick box for each one that may be switched
  off (saying what happens with it off), and a padlock, greyed, with the reason, for each one that may not. Under each
  card, **the way a job usually goes**, redrawn as boxes are ticked — the server works it out, so it always matches what
  saving will do. Everybody may read it; **a supervisor or the owner** saves it.
- **What can go (24 switches):** the categories Workshop, Call out, Walk in and Recurring task (**Remote support and
  General never** — sessions, emails and website chats open them by themselves); **photos when booked in** and **photos
  when collected or delivered**; skippable steps (inspection findings, asking suppliers for prices, payment before
  ordering parts, tracking payment after invoicing, supervisor approval for *Do not invoice*); and single choices
  (damage report, inspection fee, waiting on customer, out for delivery, another machine, quotes, reschedule, paid on
  site, paid at the counter, needs workshop work, needs to be booked in, the SLA button, link to parent).
- **Nothing can break a route.** Before anything is saved one check walks every route and refuses a set of switches
  that would leave a job with no way forward, or let one close without ever being invoiced or deliberately not. The
  locks exist so no combination is refused; a test walks every combination (25,652 routes).
- **A job finishes the route it started on.** Each ticket keeps a copy of the switches from the day it was opened;
  only new tickets follow a change. A category switched off takes no new tickets, but its open ones finish.
- **Shown once to the owner** the first time they open the portal after it arrived, with *Keep everything*. **Once means
  once** (V2.5, 28 October): it used to send them back on every Dashboard click until they saved, which looked like the
  Dashboard tab opening Job types; now the trip itself is remembered. Not on the live server until the next deploy.
- New ticket, the ticket page, Edit and **the console** follow it at once — an open console is told the moment the page
  is saved.
- **The rules are in one file**, `Platform/Services/JobTypes.cs` (`HANDOVER.md` §166–§168; the brief with Clint's
  answers is `docs/NEXT-SESSION-JOBTYPES.md`).

---

## Part G — Running the business

### G1. Many IT companies on one platform [Proven]

Each company has its own branding, people, customers, machines and tickets, and cannot see anyone
else's. See document 01.

### G2. The owner portal [Built]

`…/owner` — sign in with the owner key **and** a code from an authenticator app, or (since 13 October)
as a named **platform admin** with your own email, password and code. Approve companies, see all
companies, change seats, status (Trial, Active, Suspended) and country, and set the platform's own
email account (used for trial notices, invitations, new-sign-in alerts).

**Since V2.2:** **Admins** (four levels: Viewer, Manager, Full, Owner; set up from a mailed link; a log of
everything by name — `Services/PlatformAdmins.cs`); **the control dashboard** — a card per server with
its specifications and five readings, amber at 70% and red at 85% of what a server was measured to carry,
emailed to the admins when it crosses a level, and every company's people, technicians and machines; a
company on another server is changed through that server's own rules (`Services/ControlServers.cs`,
§133). **Pilots** (G7), **Reviews** waiting for approval (G8), **PayFast** (the account and a sandbox test
checkout), and **Preview** (see the hidden real website). **Suspending** a company now takes effect at
once: its consoles and machines are turned away and its people signed out, and the owners are emailed.

### G3. Signups, trials and notices [Built]

Public signup is **switched off** while the product is tested privately — **until 1 November 2026**, when the
deploy with `-RealSite` opens it together with the real website (since 27 October the two cannot disagree:
every deploy writes both, so **every deploy after 1 November needs `-RealSite`**, §169). When on, a company signs up
in steps (added 2026-10-05): **where** it is (South Africa, United States, Canada, United
Kingdom or Europe — which sets the price, the currency and the server its data lives on), **Free or
Full** (added 6 October, V2), **the terms** for that region (privacy law, "we are not responsible for
loss of data", and so on, in `Platform/Terms/`; the free version has its own short section ahead of
them). Since 6 October **no terms are marked DRAFT** — Clint: *"assume they are correct as is"*; his
lawyer's changes, when they come, are edits and a new version number. Then **the company, its time zone and its owner's login** (the US, Canada and Europe choose a time zone; every time on the portal is shown in it, never the server's). The
exact terms they accepted are kept on their account. A region with no server yet takes their details
for a **waiting list** instead. Then it waits for approval, gets a 14-day trial, and is emailed before
the trial runs out. While signup is off, new companies are created on the server's command line
(`create-tenant`, then `invite`).

### G4. Roles as tick boxes [Built]

A person can be any mix of **Owner**, **Supervisor**, **Technician** and **Office administrator**.
Every role uses a paid seat (since 2026-10-05); one owner with no other box is free. A company may
have several owners (since 2026-10-08): only an owner makes, changes or removes one, every owner-only
login past the first takes a seat, and there is always at least one. Office
administrators can never connect to a customer's machine. All permission questions are answered in
one place, `Platform/Services/Permissions.cs`.

**The free owner-only login oversees (since 15 October, V2.2):** it runs the company (billing, people,
branding, clients and imports, items, suppliers, the knowledge base), sees every ticket and may **comment
and @mention** — but does **not** work tickets: no moving stages, editing, assigning, invoicing, closing,
emailing the client, answering website chats, or connecting. Ticking any other box gives all of that
back and takes a seat. Enforced twice: `Permissions.CanWorkTickets` in the services, and an
**allow-list** on the ticket pages (`Pages/Portal/TicketWorkAttribute`) that refuses any action not named
in it (§146).

### G5. People choose their own passwords [Built]

Adding a person sends them an **invitation email** with a one-use link (good for 72 hours). They
choose a password, set up the authenticator app, and receive ten recovery codes. **Forgot
password** works the same way. Nobody ever types a password for somebody else.

### G6. Pricing, seats and paying [Built 2026-10-05; FIVE SEATS, MORE ON REQUEST since 2026-10-25; PayFast built 13–14 October]

**Five seats; more on request** for R500 / $49 / £49 / €49 a month (South Africa / US and Canada / UK /
Europe), any mix of roles; one owner-only login is free on top, and everybody else (a second
owner-only login too) takes a seat. Every company starts with five (Clint: the product is for small IT
companies — *"our vision is to make proper I.T support more affordable for smaller I.T companies"*). **An
owner who needs more asks on Billing; a platform admin allows up to a number, or declines, on Owner →
Approvals** (or sets it on All companies). Then each seat past five is **R300 / $20 / £20 / €20 a month**,
agreed by the owner on the People page before the person is added, and PayFast's amount follows from the
next payment; a pilot's extras are invoiced. Without an allowance a sixth person is refused to everybody.
**Anywhere IT (marked "not billed") is never limited.** (5–22 October: extras up to ten for anybody;
22–25 October: none at all.) See `docs/PRICING.md` and HANDOVER §160; the prices live in
`Platform/Services/Pricing.cs`.

**Five companies at a time** (2026-10-22): each server lets in five paying companies (not Anywhere IT,
pilots or the free version); past that, signup offers a waiting list, and a platform admin offers the
next company a place from Owner → Approvals — a link that works once and holds the place for 7 days.
The number is set on the same page (`Services/OpenPlaces.cs`).

**Paying by card, through PayFast (V2.2, built and proved on PayFast's sandbox; hidden on live):** a
South African company choosing the full version at signup ends at **PayFast's own page**: R0 now, the
package from 14 days on, the card held by PayFast. PayFast's notice switches the company on and emails
the owner — no approval by hand. The owner can **add or change the card** on Billing, and Upgrade (from
the free version) goes on to PayFast. **The amount is kept true**: when people are added or removed,
PayFast is told the new amount for the next charge (an hourly sweep retries). Our own receipt goes out for
each payment, and Billing lists them. **A payment that does not come** (PayFast sends no notice for a
failure, so we ask PayFast's API): reminders at 1, 7 and 13 days with PayFast's change-the-card link,
**suspended when more than 14 days late**, lifted by a payment. **Cancel** on Billing tells PayFast first;
service ends the day before the charge it was waiting for. Every rule: `Services/PayfastService.cs`,
`PayfastWire.cs`, `CompanyBilling.cs` and `CompanyBilling.Late.cs` (§134–137). Free signups and pilot
companies do not use a card.

The **Basic version** (the free version until 2026-10-25) is R50 / $5 / £5 / €5 a month by card. Upgrading from it is the same kind of yes: the owner accepts the
price from R50 to the package, and it is recorded on Billing (Part H).

### G7. Pilot companies [Built — V2.2, not on hardware]

Clint's deal with the first two IT companies: **five seats free while they are pilots** — no card (PayFast
cannot hold a subscription of R0), and since 2026-10-22 nothing to invoice either, as five is the most any
company has. **A pilot is ended by notice** at Owner → Pilots (the terms give 30 days): on the day it is
either suspended and closed 30 days later, or moved to R500 with a 14-day trial to add a card; the notice
can be withdrawn until then. The page shows each pilot's people, machines, tickets, sessions and last
sign-in (§153). A platform admin makes a
**one-use link** at Owner → Pilots (or `pilot-link "<company>" [email]` on the server): it works for 30 days,
only its fingerprint is kept, and it is shown once. It opens **`/pilot/{token}`**, which works with signups
off and behind the holding page: the pilot terms (`Terms/pilot.txt` ahead of the region's), then the
company form. The company is **Active at once — no trial, no approval, no card** — and the link is spent
in the same save, so two people cannot both use it. The person signing up is Owner and Supervisor (one of
the five seats). All companies shows a pilot's price as *"Pilot · free"*, with the end date once notice
is given. An existing company becomes a pilot with `set-brand <slug> pilot true`. Every rule:
`Services/PilotService.cs` (§146). The guide sent with the link is
`docs/pilot-brochure/The Remote Support Program - Pilot Guide.pdf`.

### G8. Reviews from companies [Built — V2.2]

A week after joining, the portal asks the company once for a review (*Later* asks again in a month, then
*Don't ask again*): stars, a few words, a name and the **company's name**, with a tick to allow publishing.
A platform admin approves each at Owner → Reviews before it appears; the real website's home page shows
the six newest, safely encoded (`Services/ReviewService.cs`, §139).

### G9. The real public website [Built — V2.2, hidden]

About a dozen public pages (the home page, a page for each part of the product, pricing, about,
contact, help and questions, terms and privacy), in light and dark, with screenshots made only from the real windows
fed made-up companies (`tools/siteshots` — never a real customer's screen), checked for broken HTML,
search-engine basics, links, size and stale claims by `tools/sitecheck`. **Hidden behind the holding page
until Clint says so** (`Publish-Platform.ps1 -RealSite`); a platform admin can preview it on the live
server. The words have not yet been checked by Clint (`docs/WEBSITE.md`). Pages: `Services/SitePages.cs`.

**Since 25–27 October (V2.4):** it speaks to more than IT companies — *businesses that look after other
people's computers and jobs*: IT companies, software and point-of-sale suppliers, in-house IT teams, computer
and cellphone repair shops, call-out businesses, and remote workers (with a page of their own,
`/remote-workers`) — in the words South Africa searches (*job cards*, *remote access*, *live chat*, from
Google Trends, `docs/WEBSITE.md`), and carries **the logo** on every page (`wwwroot/img`, §171). Our own site
gets **a chat bubble saying The Remote Support Program**, answered by Anywhere IT's people as their own website
chat (`Services/SiteChat.cs`; only for the company named by `Platform:SiteChatSlug`, §169). **Any new public
page must be in `go-live.sh`'s holding list** until 1 November, or it is public the moment it deploys
(`tools/sitecheck` fails otherwise).

### G10. A company takes its data with it [Built — V2.4, 22 October]

**What:** Portal → **Export** (owner and supervisors) makes one zip, in the background, kept 7 days: the
clients as a spreadsheet whose first sheet goes straight back into Import clients, every ticket with its whole
story, quote lines, photos and files, the knowledge base as web pages and text, and — only when ticked — the
session recordings. **Never a device password.** A **suspended or closed** company is exported **by us**
(Owner → All companies → *Export their data*): its owners are emailed a link to a page with a download button
(mail filters open links, so the link alone downloads nothing).

**Where:** `Services/CompanyExport.cs`, `Pages/Portal/Export.cshtml`. HANDOVER §154.

---

## Part H — The Basic Version for Remote Workers (the free version until 2026-10-25) [Built — V2; R50 since 2026-10-25; none of it on hardware]

**Changed 2026-10-25 (HANDOVER §161):** Clint ended the free version. It is now *The Basic Version for Remote Workers*: **R50 /
$5 / £5 / €5 a month** by card through PayFast after a 14-day trial, **up to three computers**, every other rule below
unchanged. The free accounts from before are given 30 days' notice from Owner → Basic version. It never counts against the
five places. In the code it is still `FreePlan` and the plan `"free"`; read "two computers" and "free" below as three and R50.

For **people with remote offices**: one person reaching **their own** computers. Clint's fourteen
decisions are in `docs/NEXT-SESSION-FREE.md`, and `HANDOVER.md` §100–§107 is the full record.

### H1. What it is

- **One person**, who is the owner and the technician. Nobody else can be added.
- **At most two computers**, counted by the actual machine, so reinstalling one is not a third. A
  computer can be **removed** to free its place, **twice a calendar month** (so two places cannot be
  passed round a whole customer base).
- **Unattended only.** Nobody at a free computer is ever asked anything, and it cannot ask for help.
  Every free computer **must have an unattended password**, chosen when it is installed. The
  technician types it once in the console, and it is remembered.
- **What a session can do:** see and control the screen; Ctrl+Alt+Del, lock, Task Manager and restart;
  Files, copy and paste, dragging files; printing both ways. **Not:** scripts, sound, talking,
  recording, the privacy screen, chat, tickets, browser support, website chat, outside help.
- **For their own computers only** — written into its terms. Supporting customers needs the full
  version. Free for business use, no time limit, no nagging.
- **The server enforces all of it.** The hub refuses a third computer when it says hello, refuses
  anything the free version does not have whatever the console sends, and a single rule in front of
  the portal refuses every page that is not the free version's (`Hub/TenantHub.Free.cs`,
  `Services/FreePortal.cs`, `Shared/FreePlan.cs`).

### H2. Signing up

The same signup page: country, then **Free**, then the terms. **No logo or colours** are asked — it uses
the product's own colours, light and dark. The company name and phone number are optional. Like any
signup it waits for the platform owner's approval, and is then **active, with no trial** (there is
nothing to run out).

### H3. Adding a computer

On the computer to be added, the owner opens the link from their portal (`/download/<company>`) and
runs it. One window, **Add this computer**: the password twice, then:

1. It asks the server whether this computer may be added — a third is told why **before anything is
   installed**.
2. Windows asks for administrator permission, and **only the background service** is installed — no
   tray icon, no shortcuts, nothing at logon.
3. It connects once, as the computer itself, to switch unattended access on and set the password.

**Changing a computer's password** is running the same download on it again.

### H4. While connected

The amber **Being controlled remotely by …** bar (A16) is on the computer's screens for the whole
session — the only sign, since there is no tray app. It is not in the technician's picture.

### H5. The portal and the console

- **Portal:** *Your computers* (each one, online or not, *1 of 2*, removals left this month, Remove, and
  how to add one), *Downloads* (the console and the add-a-computer link), *My account*, *Upgrade*.
- **Console:** only *Your computers*, Connect (asks the password only the first time — no reason, as
  there are no tickets), right-click Remove, and an **Upgrade** button. The session's Tools menu offers
  Files and the two printing items.

### H6. Upgrading, in place

Portal → **Upgrade** (or the console's Upgrade button): what the full version adds, the price — **free
now, R500 a month after a 14-day free trial** — and the full terms. The owner ticks and presses Upgrade.
The **same account** becomes a normal company: the same computers, the same saved passwords. A console
that is open switches to the full console at once. The full terms and the price agreed are kept on the
Billing page. The free computers keep working as they are; reinstalling one gives it the full version's
tray app.

**Where:** `Platform/Pages/Portal/Upgrade.cshtml`, `Services/TenantService.cs` (`UpgradeAsync`),
`Hub/TenantHub.Free.cs` (`PlanChangedAsync`).
