# The Remote Support Program

Loaded automatically at the start of every session. Keep it short; the detail lives in
`docs/HANDOVER.md`, which you should read before doing anything non-trivial.

## Read first

- `docs/HANDOVER.md` — full state of the build: architecture, decisions already made, gotchas
  that cost a day each, and the diagnostic tooling. Do not re-derive any of it.
- `docs/ROADMAP-PARITY.md` — **the running order for the whole product**: every feature
  TeamViewer and AnyDesk have that we do not, staged. Stage 1 core remote control, 2 codec,
  3 audio, then 4 hosting, 5 code signing, 6 the other platforms. Read it to know what comes
  next; the handover still wins on matters of fact. **Stage 1 is complete and proven on real
  hardware as of 2026-09-16. Stage 2.1 (Desktop Duplication) is done and PROVEN ON HARDWARE the
  same day, UAC prompt included. 2.2, the H.264 codec, is BUILT the same day — but its encoder has
  never produced a frame. **FIXED 2026-09-17 and it now encodes: scrolling 2785 → 198 KB/s, CPU
  12.7% → 1.0%. Two swapped interop constants told the encoder its stream had ended; missing
  silicon was never the explanation.** 2.3 (adaptive quality) is DONE the same day, floor and
  controller — though nobody has yet watched it back off on a genuinely bad link — and 2.4, the
  customer's real pointer, is DONE and PROVEN ON HARDWARE. That hardware run found a delivery fault
  no harness could: a reconnecting technician would have lost the pointer for the rest of the
  session. **STAGE 2 IS COMPLETE.**
- **Stage 3.5 started 2026-09-18: the console gets out of the way. 3.5.1 IS FINISHED as of
  2026-09-21 and PROVEN ON HARDWARE.** The clipboard mirrors text both ways, and FILES now paste
  on the customer's machine. **What it was: Explorer will not paste from a file list owned by a
  process above it**, and the host is SYSTEM. The data was always correct, which is why six rounds
  of inspecting it found six real faults and never this one. The host now hands that one write to
  a short-lived copy of itself running as the person at the machine. `userpaste` 4 of 4 against
  DESKTOP-OPHQIDH; **§39 of the handover supersedes §27.** §27 is
  kept for the six faults and for the lesson that found this one: **ask the question Explorer
  asks, from where Explorer stands** — every earlier probe read the clipboard back as SYSTEM,
  which is not who pastes. **The "ding" in §27 is still unexplained and still open.** A change to
  the paste path needs a FULL ship, because it changes the HOST.
- **COPYING FROM THE CUSTOMER WORKS TOO (2026-09-21), and it uncovered a LEAK. CONFIRMED ON
  HARDWARE BY HAND (§40)** — Clint, after the ship and the platform restart: *"copy and paste is
  now working both ways, this is excellent."* **STAGE 3.5.1 IS CLOSED.** Two faults.
  First, a real Ctrl+C over there leaves a **promise, not data**: the file list is delay-rendered
  through OLE inside Explorer and **does not cross to another account**, so SYSTEM saw nothing at
  all. Same boundary as §39, opposite direction, same answer — a helper running as the person
  (`--get-files`). Second, and worse: **`_subscribed` was never cleared when a viewer
  disconnected**, so the customer's clipboard kept crossing after the technician had gone, and a
  viewer that never subscribed received it. Pre-existing since 2026-09-18. Fixed on both sides —
  `BeginGrace` now tells the host, and the host clears subscriptions when no technicians remain.
  `usercopy` 3 of 3, `clipcheck` **16 of 16 with both leak tests**, `codeccheck` 10 of 10.
  **This one needs the PLATFORM RESTARTED as well as a full ship.** Two rules out of it: **a count
  is not a reading — print the paths** (a blank "1 item" made a wrong fix look right), and
  **`clipcheck`'s copies are made by SYSTEM, so they cannot see a fault that only affects what the
  PERSON copies** — that is what `tools/usercopy` is for.
- **3.5.4 IS DONE (2026-09-18) and confirmed on the real console** — Clint, after running it:
  *"all seems to be working well."* The right-hand side panel is gone, `Tools` is a dropdown on
  the top toolbar holding Chat, Files, Scripts, Sound, Info and Listen, and each opens as its own
  movable window. `toolbarprobe` 37 of 37, `dragoutcheck` 43 of 43; §28 of the handover.
- **3.5.5 IS DONE the same day and CONFIRMED ON HARDWARE** — Clint, after a full ship and a client
  reinstall: *"it works."* The customer's chat popup is draggable and stays where it is put; both
  sides remember their window positions in `%LOCALAPPDATA%\RemoteSupport\windows.json`.
  `chatprobe` 23 of 23; §29 of the handover. It needed a full `ship-build.ps1` rather than
  `-OnlyTechnician`, because it changes the CLIENT — that distinction is worth checking before
  every ship.
- **3.5.6 and 3.5.7 ARE DONE the same day, NOT yet on hardware, and need a full ship.** The tray
  menu is `Request Support`, `Chat with a Technician` and `Exit` (no `Open` — the double-click
  does that; **Exit stays, by Clint's decision, against what the roadmap item asked for**), and a
  support request must say what is wrong, which the alert then shows as the largest text at the
  top. `callprobe` 25 of 25; §30 of the handover. **Read §30 before adding any control to any
  window**: three faults of one shape turned up, two of them invisible on this machine, and the
  rule out of it is *do not write a control height as a number — measure it from its font.*
- **UNATTENDED ACCESS IS NOW A SWITCH THE CUSTOMER HOLDS (2026-09-20), and CONFIRMED ON HARDWARE**
  — Clint, after the ship and the platform restart: *"the unattended switch works."* It
  used to mean only "the agent service is connected", fixed at install time. It is now that AND a
  per-device permission stored and enforced on the server, ticked by a checkbox in the tray app,
  with the console's Access column flipping live. **Every install now installs the agent
  service** — it does nothing without permission — and `-Unattended` means "and tick the box for
  them". `unattendedcheck` 32 of 32; §31 of the handover, which also has the two traps: a
  customer's OWN call must still be allowed to use the agent (or attended machines lose UAC
  capture), and only the CLIENT connection may change the permission. **§33 is the one that bit**:
  a hub race whose fast path lost every single time, so the tray item was greyed for ever and
  looked unwired; `tools/unattendedwire` (now 13 of 13) tests the hub for real, against a platform
  of its own. Still unwatched by eye: the console's Access column flipping live.
- **THE SWITCH TOLD THE TRUTH AND THREE OTHER THINGS LIED (2026-09-21, §41). NOT on hardware.**
  Clint: the switch *"switches between attended and unattended on the console, but does not allow
  for unattended access. the client still has to click on allow."* All accurate. **The hub was
  right** — unattended needs the permission AND a connected agent, and without the agent it falls
  back to the tray app, which asks. What was wrong is that nothing anywhere could say so: the
  installer printed "installed and running" unconditionally with every `sc.exe` piped to
  `Out-Null`; the tray app enabled its box on a REGISTRY KEY (`sc create` writes it whether the
  service ever starts); the console's dot is `AgentOnline || ClientOnline`, so a dead agent behind
  a live tray app is green; and the hub fell back in silence. **The rule: installed is not
  running, and running is not connected** — only the hub can see the agent's socket. Fixed in all
  four. New: `tools/agentcheck` (ask the live hub which machines claim unattended and cannot do
  it) and `tools/agentstate.ps1` (run ON the machine; says why). `unattendedcheck` 40,
  `unattendedwire` 13 — **the hub's half is covered at last** — `dashtabs` 62. Needs a FULL ship
  (it changes the CLIENT and the INSTALLER), a platform restart, and a client reinstall. No
  migration. **Read §41 before touching this**: the wording left the form because the state that
  matters is unreachable on a machine with no agent service, which is every machine here.
- **DEVICES IS EVERYBODY'S NOW (2026-09-20), NOT on hardware.** Saying which customer a machine
  belongs to used to be the owner's alone; it is every role's, in the portal AND by right-click on
  the console's machine list. `Permissions.CanLinkDevices` is a QUESTION, not a role comparison —
  keep it that way. `devicelink` 19 of 19, proved to drop to 15 with the old policy back; §34.
  **Read §34 before adding any check to a harness that loads a page**: a refused page redirects to
  the sign-in form and arrives as a 200, so asserting the status code proves nothing — assert the
  page's own heading. No migration; platform restart and `-OnlyTechnician`.
- `docs/handover-pack/` — **the plain-English handover for a new development team, now V2.5
  (2026-10-28, THE FINAL VERSION BEFORE GOING LIVE; V2.4 2026-10-27, V2.3 2026-10-16, V2.2 2026-10-15, V2.1 2026-10-07, V2 2026-10-06, V1 2026-10-02)**, for the USB stick in the safe: what it is,
  every feature, security, where it runs, building, the code map, status, every key and account NOT on the
  stick (08), **10, what V2 added to V1**, **11, what V2.1 added (§108–§114)**, **12, what V2.2 added
  (§115–§146: PostgreSQL, PayFast, admins, signing, pilots)**, **13, what V2.3 added (§147–§148: automatic updates)**, **14, what V2.4 added (§149–§172)**, and **15, what V2.5 added (§173, the Job types fix)**. **The generic guide PDF sits at the top of the zip** (print it with `docsrochurePrint-Guide.ps1` first).
  `tools\Make-HandoverPack.ps1` (`-Version`, default V2.5) builds
  `TheRemoteSupportProgram-Handover-V2.5-<date>.zip` (docs + source + git bundle, checked for secrets) on
  the Desktop. **Every tracked document is in it (the source copy is the last COMMIT — commit first); only `legal\`,
  the load test's raw results and old server logs stay out, on purpose.** **When a feature, a supplier or a secret's location changes, update the pack and offer
  Clint a fresh zip; a rewrite is the next version, with its own "what is new" page.**
- `docs/pilot-brochure/` — **the pilot guide for IT companies (2026-10-15), 38 A4 pages**: Part 1 setting up
  a company step by step from the pilot link, Part 2 what is new since 9 October, Part 3 the full tour.
  `brochure.html` → `The Remote Support Program - Pilot Guide.pdf` (Edge headless print). Screenshots only from
  made-up companies (Highveld Tech Solutions, Coastal IT Solutions, Karoo Wool Traders) — never a real
  customer. Page numbers and the contents page are filled in by the script at the end of the page.
- `docs/brochure/` — **the GENERIC guide (2026-10-24), 30 A4 pages since Job types (2026-10-27), for anybody thinking of using or investing**: what
  it is, remote support, jobs and tickets, the company side (people, security, updates, where it runs, R500 for five
  seats, the free version). Nothing pilot-only, nothing unbuilt, no figures beyond the published price. `brochure.html` →
  `The Remote Support Program - Guide.pdf` by **`docs\brochure\Print-Guide.ps1`**; a page that outgrows A4 is cut off
  silently, so look at every page after a change. New pictures came from a throwaway platform seeded with Coastal IT
  Solutions and from siteshots-style windows — never a real customer or screen. Keep it true when features change.
- `docs/TEST-3.6.md` — **the hardware test checklist for everything built on 2026-09-20 and not yet
  run on a real machine.** 41 numbered checks, A–H, ordered so the parts needing no far machine
  come first. **Section I (checks 42–48), added 2026-09-21, is copy and paste by hand, both ways;
  42 and 45 are DONE and the rest are not. Section J (49–54), the same day, is the unattended
  fault of §41 — and 49 is the one that matters: run `tools/agentstate.ps1` on the machine.**
  Keep it true as things are proved.
- `tools/testchecklist/` — **the internal-testing program on the office shared drive (2026-09-29).**
  It reads TEST-3.6.md as `checks.md`; testers mark checks and log faults and upgrades; Clint brings
  the end-of-day **Export report** (`Test report <date>.md`) to Claude. Fold its results back into
  TEST-3.6.md and fix its faults. **Never renumber a check** — the number is the key. Publish with
  `Publish-TestChecklist.ps1` (`-ChecksOnly` after editing TEST-3.6.md); its README says how Claude
  marks a fault "Fixed — please retest" on the share.
- **THE WORKSHOP WORKFLOW IS BUILT (2026-09-24, §44). NOT on hardware.** Clint wrote it and
  approved the map; it is in `docs/NEXT-SESSION-WORKFLOWS.md` in his words. Stages live in
  `Services/Workflow.cs`, ONCE; `TicketService.ActAsync` is the only thing that moves one and
  `ActionsFor` is what every UI draws. **Billing is a lane, not a category**; out-photos are taken
  at COLLECTION; a technician never closes anything. Also: **Owner and Supervisor are separate
  roles** (`Admin` is the supervisor, `Billing` the office administrator, `Owner` is seat-free),
  a shared item list that refuses "memory DDR" beside "DDR memory", quote request emails (a .eml
  for desktop mail, a compose link for web mail, asked once), and **@mentions that stay flagged
  until the person replies**, badged in the portal and on the console's Tickets button.
  `rolecheck` 37, `workflowcheck` 85, `quotecheck` 50, `mentioncheck` 27, `mentionwire` 9.
  `tools/workflowdemo` + `tools/portallink` put it on a screen without touching the live server.
  **Full platform restart (four migrations) and `-OnlyTechnician`.** `TEST-3.6.md` section M.
  **The console's own ticket window is BUILT too (§45)**: the Tickets button opens it again, it draws
  only what the server sends, and Request quote and photos stay in the browser. `ticketwindowprobe`
  52, `ticketwire` 24. Platform restart and `-OnlyTechnician`; `TEST-3.6.md` checks 81–86.
- **THE WORKSHOP TICKET IS NOW A STORY, IN BOTH PLACES (2026-09-27, §46). NOT on hardware.** Clint:
  *"step by step... not everything on one screen"*, then screenshots of Anywhere's current system:
  stage at the top, a comment box always there, a numbered timeline that *"reads like a story."*
  **§46 supersedes §44's bench and §45's layout.** The order is now **Booked in → Inspection
  findings → Repairable / Not repairable → the endings**, and **what was done is written INTO
  Solved — to be invoiced**, with the time and amount (Clint's second correction, same day — the
  Work done step is gone, retired like On the bench; `TimelineForms` moved On the bench to
  Inspected). Quote lines have an optional **serial number** (migration `QuoteLineSerial`), never
  remembered on the item.
  Only the next steps are buttons; a step's form opens at the top of the story; one note per step
  (`FormTitle`, `MovedTo`). The Item box has **Add beside it**. The console matches it —
  `StageActionDialog` is gone. `workflowcheck` 99, `ticketwire` 28, `ticketwindowprobe` 75,
  `quotecheck` 54, `handoffcheck` 44. **Platform restart (migration `QuoteLineSerial` on top of
  `TimelineForms`) and `-OnlyTechnician`**; `TEST-3.6.md` section N, checks 87–94.
- **CALL OUT HAS ITS ROUTE TOO (2026-09-27, §47). NOT on hardware.** Call out booked → Work done
  (work and time) → Ticket completed — to be invoiced (the workshop's invoicing stages, then CLOSED:
  no machine to collect) / Invoiced — paid on site → Paid cash (amount) or Paid card machine
  (amount, reference) → the office closes it / **Needs workshop work: the SAME ticket becomes a
  Workshop job at Booked in**, after three photos in and the booking's (optional) questions.
  **Read §47 before touching `@close` or Machine collected** — both had assumed every workflow
  ticket was a workshop machine. `calloutcheck` 52 (50 with the old lines back). Migration
  `CallOutWorkflow`; platform restart and `-OnlyTechnician`; `TEST-3.6.md` section O.
- **EVERY CATEGORY HAS ITS ROUTE (2026-09-27, §48). NOT on hardware.** Walk in (repaired at the
  counter → invoiced / paid at counter / no invoice, or booked in to the workshop), Remote support
  (to be invoiced / SLA / do not invoice / **link to one of the same client's open tickets, which
  closes it**) and General (**no client needed**; Done closes it / quote / to be invoiced). Any ticket
  can be linked to a client at any stage. **Read §48 before touching `ActiveForDeviceAsync`, parent
  closing or the console's resize redraw** — three real faults were found and fixed there.
  `routecheck` 53, `ticketwindowprobe` 110. Migration `EveryCategoryRoute`; platform restart and
  `-OnlyTechnician`; `TEST-3.6.md` section P.
- **SEVERAL PEOPLE ON ONE TICKET (2026-09-28, §49). NOT on hardware.** Clint, before internal
  testing: *"multiple people must be able to be assigned to the same ticket from the start or added
  at any stage."* Everyone on it is equal; a technician who connects to the machine goes on it.
  **Stored in `AssignedTo` itself, as names joined by ", "** (`Services/Assignees.cs`) — no
  migration, and a display name may no longer contain a comma. **The trap: never ask
  `t.AssignedTo == me` again** — use `Assignees.Has`, or `TicketService.OnIt` in a query. A
  technician may join a bench job and add colleagues to their own; never take a colleague off.
  Tick boxes in the portal and the console. `assigncheck` 49 (12 fail with the old `==` back), `ticketwindowprobe` 123, `ticketformprobe` 91; walked in the browser, which found the form sending `peopleShown=1` to a bool.
  **Platform restart (no migration) and `-OnlyTechnician`**; `TEST-3.6.md` section Q, checks 109–115.
- **THE HEALTH CHECK IS A TICK ON THE CLIENT NOW (2026-09-28, §50). NOT on hardware.** Clint: only
  SLA clients and the ones chosen. **The default install is WITHOUT it**; the client's page has a
  *Monthly health check* tick every machine of theirs follows, and a machine can be set On/Off for
  itself (right-click in the console, or its row on the client page). **The AGENT SERVICE installs
  and removes it** — only a SYSTEM service can — told the wanted STATE by the hub, reporting what it
  then finds. One rule, `HealthCheckModes.Wanted`. It never touches a health check it did not
  install (no `Platform` in `config.json`). **Read §50 before touching `TellAgentItsHealthCheck`**:
  a reinstalled machine must never be told "off" before the hub recognises it, and a harness on
  local SQLite cannot see that without `Testing:PersistDeviceDelayMs`. Two old faults fixed on the
  way: the monthly run never read the server's answer (switching off stopped nothing), and "inspect
  only" was never obeyed. `healthcheckwire` 52 (51 without the guard), `Test-HealthCheckUninstall`
  8, `dashtabs` 79. **Platform restart (migration `ClientHealthCheck`), a FULL ship, and a client
  reinstall** — tick the SLA clients first. `TEST-3.6.md` section R, checks 116–124.
- **THE HEALTH CHECK TOOLKIT IS AT v2.9 (2026-09-28, §51). NOT on hardware.** Clint fixed the
  standalone `C:\PCHealthCheck` and asked for the changes here: drive health from the maker's
  thresholds (matches CrystalDiskInfo), no *Healthy* on no evidence, one backup verdict reached
  after the evidence, email settings checked after they are read, `-ValidateOnly`. Untouched
  scripts copied; ours three-way merged against `314b979`. **Our installer departs from v2.9 on
  purpose**: its email check runs after the KEPT settings, or the agent's Keep reinstall would be
  refused. Three faults fixed on the way — **`deploy-client-payload.ps1` (which `ship-build.ps1`
  runs) was missing `Uninstall-HealthCheck.ps1`**, so the next ship would have broken every install;
  a kept mail password failed its round trip; the installer's exit code was accidental.
  `Test-HealthCheckValidate` 21 (17 on v2.9's placement), `healthcheckwire` 52,
  `Test-HealthCheckUninstall` 8; `Test-HealthCheckReinstall` (now 20) needs elevation and is
  unrun. **Full ship and the client reinstall §50 already needs; no platform restart.**
  `TEST-3.6.md` section S, checks 125–128.
- **TOOLKIT v2.11 AND THE ESET RETRY (2026-09-29, §53). NOT on hardware.** The toolkit synced again
  (to `fe58b93`): **the report is one section per machine**, clients matched forgivingly, no browser
  header/footer on the PDF. Our installer keeps its own closing lines — `START HERE.bat` is never on
  a customer machine. **Bug 9 is built**: the agent retries a failed health check install after 1, 5
  and 15 minutes, reporting each failure with when the next try is, and **a newer answer ends the
  wait at once** (a machine switched off mid-wait is never installed); choosing the same thing again
  in the console now retries (`HealthCheckModes.WorthSending`). `keeperretry` 31, `dashtabs` 79,
  `healthcheckwire` 52. **Running the report from the repo shows Anywhere IT's logo — a test
  artefact, not a fault** (§53). Full ship and a client reinstall; no platform restart.
  `TEST-3.6.md` section U, checks 136–140.
- **INTERNAL TESTING: THE HOST CRASHED ON EVERY SECURE DESKTOP (2026-09-29, §54). PROVEN ON
  DESKTOP-2N6FVGT, not yet shipped.** Ctrl+Alt+Del worked; the HOST then died 2–4 s later
  (`0xc0000409`, Control Flow Guard) — and so did every UAC prompt — on any machine encoding with a
  **hardware** encoder: a rebuilt duplication is a new Direct3D device, and the encoder was only
  flushed, not rebuilt. **An encoder only ever sees textures from the device it was built on**
  (`DropEncoder`, `_encoderDevice`), plus a short retry and an honest Info tab. `sysactioncheck
  --observe` (new: watches the desktop, and the host's survival) 20 of 20 on the new host, 0 of 4
  survived on the old; `codeccheck` 11. **And "pastes the original files"** was the CONSOLE: a copy
  from the customer over 50 MB (or still in flight) left the technician's old clipboard in place.
  It is emptied the moment their copy is announced; `pullcheck` 11 (9 with the line out — it could
  never see this, because it emptied the clipboard itself), `tools/recopy` (new) 5 of 5 on both
  machines. **BUGS #10** (the first copy after a fresh logon was lost) is FIXED 2026-10-06, §93 (`tools/clipfirst`).
  **Before running harnesses with the console open, empty this laptop's clipboard** — the console
  joins harness sessions and sends it. **Full ship and a client reinstall; no platform restart.**
  `TEST-3.6.md` section V, checks 141–146.
- **ROLES ARE TICK BOXES (2026-09-29, §55, roadmap 3.6.8). NOT on hardware.** Owner, Supervisor,
  Technician, Office administrator; a person may do the UNION, and takes one seat if Technician or
  Supervisor is ticked. `TenantRole` is `[Flags]` (1, 2, 4, 8) — **the stored numbers changed**,
  migration `RolesAsTickBoxes`. **Never write `role == TenantRole.X` or `Seated.Contains(t.Role)`**:
  ask `Permissions`, or `role.Has(...)`, or `(t.Role & TenantRoles.Seated) != 0` in a query. An old
  portal cookie is read by the OLD numbers, never guessed upward. `rolecheck` 72 (67 with the old
  lines back), `handoffcheck` 45. **Platform restart only**; `TEST-3.6.md` section W, checks 147–152.
- **CLIPBOARD LINES REACH THE TICKET NOW (2026-09-29, §56, roadmap 3.5.3). NOT through a session on
  hardware.** It was built in 3.5.1 but never checked, and mostly lost: the host reported its minute
  only when ANOTHER copy came, and the server forgot the ticket before the host's last line arrived.
  `ClipLedger` (Shared) reports on a timer; the host flushes and closes itself on `SessionEndedMsg`;
  `EndSession` gives it `HostLastWords` (3 s) and writes "Session ended" after. **A harness about a
  race must not run on loopback** — `clipledger` passed against the fault until its fake host was
  given 150 ms. `clipledger` 20 (18 before), `clipcheck --ledger` (new). **Platform restart AND a
  full ship.** The new host is already on DESKTOP-OPHQIDH. `TEST-3.6.md` section X, checks 153–155.
- **EXIT IN THE TRAY APP TAKES THE MACHINE OFFLINE (2026-09-29, §57). NOT on hardware.** It used to
  close only the tray app, and the AGENT went on hosting unattended sessions. Now the tray app sends
  `ClientClosingMsg`; the server marks it closed (`Device.ClosedByUserUtc`, migration
  `DeviceClosedByUser`), ends its sessions, refuses every route in, and reports it offline — until
  the tray app says hello again (shortcut or logon). **`Recompute` in `TenantHub` is the one place
  `Online`/`Unattended` are decided — never set them anywhere else.** `AgentOnline` stays honest.
  Only a CLIENT connection may close a machine. `trayexit` 16 (13 without the refusal). **Platform
  restart and a full ship with client reinstall.** `TEST-3.6.md` section Y, checks 156–160.
- **DRAG FILES OFF THE PICTURE (2026-09-29, §58, roadmap 3.5.2). NOT on hardware. EVERY ITEM OF 3.5
  IS NOW BUILT.** A drag that leaves the session window takes what is selected in their front
  Explorer window or desktop — read by `--get-selection`, a helper running as the person
  (`Host/ExplorerSelection.cs`; Windows 11 tabs share one HWND, the visible tab wins). **With nothing
  selected their drag is left alone and the reason shown — never cancel their gesture on a guess.**
  Text dropped on the picture goes to their clipboard. `ScreenView.ButtonsNow` is the one place the
  button state is read, so `dragoffcheck` can stand in for a held button. `dragselcheck` 11,
  `dragoffcheck` 21 (15 with the cancel ahead of the answer). **Full ship and client reinstall.**
  `TEST-3.6.md` section Z, checks 161–167.
- **QUOTE REQUESTS: THE MACHINE, SUPPLIERS, BCC, THE DELIVERY FEE (2026-09-29, §59). NOT on
  hardware.** One machine per quote, optional (type Desktop/Laptop/All-in-one, make, model, serial);
  a workshop job starts with its booking's, and **once saved, blanks are an answer**
  (`QuoteMachineSaved`). A **Suppliers** page (next to Items) with the item list's duplicate guard
  plus one-address-one-supplier, and the company's To address and delivery address; every request
  asks the delivery fee. Suppliers are TICKED at Request quote: **one → To; several → all Bcc, the
  company in To** — `QuoteService.Addressing` is the one rule. The Item box is a dropdown
  (`BrowseAsync`: nothing typed is the whole list). **Harnesses that seed before a migration must
  seed EVERY table by SQL** — four broke on the Tenant row (`tools/Common/OldRows.cs`).
  `quotecheck` 108 (103 with everyone in To), `ticketwire` 31, `ticketwindowprobe` 128; walked in
  the browser. **Platform restart (migration `QuoteSuppliers`) and `-OnlyTechnician`**; then set
  accounts@anywhereit.co.za on the Suppliers page. `TEST-3.6.md` section AA, checks 168–175.
  **Same day: the Clients search bar narrows as you type**, by asking the page itself — one
  matching rule with Add a client — and `/api/portal/clients` answers technicians now (their
  type-ahead was silently empty). Check 176.
  **2026-10-02, §75 — read it before touching any client picker, the phone layout or sign-in.**
  (1) Every "which client" box is **`wwwroot/clientpick.js`**, asking `ClientService.SearchAsync`
  as you type (modes id / owner / name); half-typed is refused. (2) **The portal on a phone** (below
  700 px only): one-line header with Menu, the ticket list as tap-anywhere cards, every list as cards
  by **`wwwroot/phone.js`** (a page that swaps a table in later must call `phoneTables.label()`),
  fields at 16 px. (3) **The two-factor code once a week**: `AuthService.IssueTrust`, bound to the
  security stamp, `Security:TwoFactorTrustDays` (7); the `rsp_trust` cookie, the console's
  `TrustedSignIn` (DPAPI). `securitycheck` 75 (74 without the stamp), `signincheck` 20. Platform
  deploy + `-OnlyTechnician`; `TEST-3.6.md` section AQ, 284–292.
  **2026-10-01, §74: the customers' installer window CLOSES after a good install** (it never could —
  `_busy` was left set; `oneexecheck` 61, 58 old), **and the CONSOLE downloads as one .exe**
  (`/downloads/{slug}/technician.exe` as `{slug}-console.exe`, offered once `console-exe.ready` is
  in the payload): a copy with no `branding\` sets itself up per user in
  `%LOCALAPPDATA%\Programs\RemoteSupport\Console\{slug}` with shortcuts, Zone.Identifier stripped
  (`ConsoleDownload`; `DownloadIdentity` is in Shared now). `consoleexecheck` 31 (30 without the
  strip). Platform restart + `-OnlyTechnician`; live: `Publish-Platform.ps1`; launcher fix: full
  ship. `TEST-3.6.md` section AP, 278–283.
  **2026-10-01: the Items page has Add an item** (anybody, the same `AddItemAsync` rule, optional
  part number); `quotecheck` 110. Platform restart only; check 277.
- **INTERNAL TESTING, 2 OCTOBER: ATTENDED SESSIONS RUN AS SYSTEM NOW (2026-10-02, §76). NOT on
  hardware.** UAC black, Task Manager, DISM 740 were all one thing: without unattended permission
  the hub never offered the agent, so every attended session was hosted by the tray app AS THE
  PERSON. Now an agent that says `DeviceCan.AskConsent` hosts it with `StartSessionMsg.AskConsent` —
  same prompt, SYSTEM host; an older agent is never offered one. **The host connects FIRST and acts
  on nothing until the yes** (`consent=asking`, `ConsentGrantedMsg`); the console shows
  `ConsentWaitForm` (*Waiting for X to accept*, Cancel) and opens no window for a session that
  ended — `SessionInfo.Ended`/`EndReason`, and **a viewer for an ended session is answered, never
  404'd** (the 404 was the "could not get back after 124 seconds"). Also: the microphone question is
  a centred window (`MicQuestion`), Info is readable (it was 1.1:1), the sign-in window measures its
  heights. `consentwire` 34 (7 of 14 on the old hub), `fitprobe` 68 (54 old). **Platform deploy, FULL
  ship and client reinstall**; `TEST-3.6.md` section AR, 293–302.
- **AN UNATTENDED PASSWORD (2026-10-02, §77). NOT on hardware.** Clint: for somebody connecting to
  their own machine from the office. Tray → *Unattended password…* (at the machine only; typed twice,
  8+ characters, `UnattendedPasswords.Problem` is the one rule). With unattended on and a password
  set, a technician-started connection needs it — **nobody gets round it, owner included, by Clint's
  decision; do not add an override**. The server keeps a PBKDF2 hash (`TenantHub._unattendedPw`,
  never in the broadcast `DeviceInfo`); only the CLIENT connection may set it; a colleague cannot
  join a session opened with it (`Session.Admitted`); blank never counts as a guess, five wrong lock
  the machine 15 minutes. Calls and attended machines untouched. The console's Connect window has
  the password box and reopens on a refusal (`ErrorMsg.NeedsPassword`). `unattendedpw` 33 (12 of 28
  with the gate out), `fitprobe` 126. **Platform deploy (migration `UnattendedPassword`), a FULL ship
  and client reinstall**; `TEST-3.6.md` section AS, 303–310.
- **TWO MACHINES ON ONE JOB (2026-10-02, §78). NOT on hardware.** Clint: a data transfer's old and new
  machine *"booked in together under one ticket."* He chose BOTH kinds: *Add another machine* now asks
  first **Is it part of this job?** — No (default) is §64's separate linked ticket; Yes puts it on THIS
  ticket as a **`TicketMachine`** row (the ticket's `Asset*` stay machine 1), its photos marked by
  **`TicketPhoto.MachineId`**, no fault of its own, one route and invoice, and **photos out = three of
  EACH machine**. A refused form keeps the choice (portal `SameJobChosen`, console `_drafts`).
  `machinescheck` 56 (52 with the old photo rule), `ticketwindowprobe` 146; walked in the portal.
  **Platform deploy (migration `MachinesOnOneJob`) and `-OnlyTechnician`**; `TEST-3.6.md` section AT,
  311–317.
- **TICKETS BY EMAIL AND A CONTACT FORM (2026-10-05, §79, roadmap 3.8.4). NOT on hardware, and NOT
  yet against a real mailbox.** Portal → *Tickets by email*: an email to support@ or the website form
  opens a General ticket, linked when the address is on file; **client known by domain but person not
  → unlinked, SAID, one button to add them as a contact** (Clint's answer 3); unknown → no client
  (answer 2); an auto-reply `[Ticket #N]` from THEIR server (ours if theirs fails); a reply goes on the
  SAME ticket, only from the right sender. Any host (answer 1): **we read their mailbox READ-ONLY by
  UID** (IMAP, `Services/MailWire.cs`, **MailKit — the second library brought in**) or **they forward to
  our mailbox** (Owner → Mail, `forwarding-mailbox set`), confirmed by a link. **Microsoft 365 sign-in is
  LATER, by Clint's decision** — BUILT 2026-10-19, §151 (Sign in with Microsoft). Every rule is `Services/TicketIntake.cs`. **Read §79 before touching
  the starting point** — "last UID 0" is an empty mailbox, not "never looked at" (the first message was
  lost), and **a refused password is tried once** (cPHulk would block OUR server for everybody).
  `mailintakecheck` 135, with a fake IMAP and SMTP server in the harness (132 / 133 with the two
  faults back). **Platform deploy (migration `TicketsByEmail`), no ship**; `TEST-3.6.md` section AV,
  320–331 — 320 is our own cPanel mailbox.
- **A KNOWLEDGE BASE (2026-10-05, §80, roadmap 3.8.5). NOT on hardware.** Clint's six answers: *"go
  with your recommendations"*. Portal → Knowledge base (every role; a card on the dashboard; a
  console header button): **General** and **Clients**, one client per article. A ticket offers **Add to
  knowledge base** once its work is recorded as done (Work done, any `Outcome`, or closed — a
  technician never closes) to the people on it (`AssignedTo` OR `WorkedBy`) and supervisors/owner;
  it opens an editable DRAFT (nothing saved until Save). Everybody edits, every save kept and can be
  put back, a stale save refused with the words kept; only a supervisor/owner deletes. A client's
  tickets, client page and the console's session window say *Knowledge base for X (N)*. Pictures
  pasted in; a password nudged, never repeated. Every rule is `Services/KnowledgeBaseService.cs`; the
  words are `KbText` — **our own markup, everything encoded first; never store HTML**. **Read §80
  before the next form or header**: a form with `action=` written out gets NO antiforgery token
  without `asp-antiforgery="true"`, and a harness must take the token from the form it posts — `kbcheck`
  passed 108 against that fault by borrowing Sign out's. `kbcheck` 108 (96 / 70 with faults back),
  `dashtabs` 110, `ticketwindowprobe` 156, `toolbarprobe` 50; walked in the browser. **Platform deploy
  (migration `KnowledgeBase`) FIRST, then `-OnlyTechnician`**; `TEST-3.6.md` section AW, 332–341.
- **PROCESSES AND PERFORMANCE WITHOUT CONNECTING (2026-10-05, §81). NOT on hardware.** Right-click a
  machine → *Processes* / *Performance* (one `MachineWatchForm` per machine). Clint's rules: **straight
  through on every online machine — no consent prompt, no unattended password; look only (no End task —
  he said no); nothing logged, no ticket.** The Network card shows each network's **Private / Public
  (amber, "sharing off") and IP address**. `Shared/MachineWatcher.cs` measures (NtQuerySystemInformation,
  PDH, Network List Manager through TYPED COM — `dynamic` was refused); `DeviceLink.OfferWatch()` is the
  ONE switch for agent and tray app; `Hub/TenantHub.Watch.cs` — measured once however many watch, agent
  preferred, figures only to the consoles that opened it, strays dropped. Also: right-click **Chat**.
  `watchcheck` 57 (3 fail with two hub guards out), `watchprobe` 68, `dashtabs` 120. **Platform deploy, full
  ship, client reinstall** (live: `Publish-Platform.ps1`); `TEST-3.6.md` section AY, 342–349.
- **ONE SIGN-IN AT A TIME (2026-10-05, §82). NOT on hardware.** Clint: a second machine signs the
  first out and asks the code again. Per person: **one console and one browser** (his choice); the
  console's Tickets hand-off is not a new sign-in (`c:` claim, dies with its console). Another machine
  → the code even over a good trust token, with a notice, and **`Technician.TrustStamp` moves** so
  every machine's 7-day trust goes; the same machine again asks nothing (console `console-id`, browser
  `rsp_device`). Console Exit/Sign out → `POST /api/logout`. **A page re-issuing the portal cookie must
  carry the `signin` claim** (`PortalIdentity.SignInOf`). **Live-server harnesses now sign out the
  console of whoever `rs-credentials.json` names — keep it a test account.** `onesignin` 42 (28 with
  three guards out). **Platform deploy (migration `OneSignInAtATime`) and `-OnlyTechnician`**;
  `TEST-3.6.md` section AZ, 350–356.
- **SEATS ARE A PRICE THE OWNER AGREES TO (2026-10-05, §83, roadmap 4.7). NOT on hardware.** Past the
  package (`Tenant.Seats`, 7) each person is one more seat at R300/$20/£20/€20, **any role** — office
  administrators are no longer free (`TenantRoles.Paid`; `Seated` still means the console — keep them
  apart). The owner sees Now/After on the People page and must Accept; a supervisor is told the figure
  and to ask the owner; **the gate is in `TenantService`** (`asker`, `agreed` = `PriceRise.Token`), and
  every yes is a `SeatAgreement` shown on Billing. Prices live in `Services/Pricing.cs` (and the
  landing page's `PRICES`). `Tenant.Region`, `Tenant.NotBilled` (Anywhere IT). `seatcheck` 44 (21 with
  the gate out), `rolecheck` 74. **Platform deploy (migration `SeatsAndPrice`) only.** `TEST-3.6.md`
  section BA, 357–364.
- **SIGNUP: COUNTRY FIRST, THE REGION'S TERMS, THEN THE COMPANY (2026-10-05, §84). NOT on hardware;
  signups still OFF live.** Five regions; `Platform:Region` is what this server holds and
  `Platform:RegionServers:{code}` sends a region to its own server; none → a **waiting list** (Owner →
  Approvals). The terms are `src/RemoteSupport.Platform/Terms/*.txt` (core + one per region, embedded,
  `KbText` markup) — **a DRAFT by Claude for Clint's lawyer** (`Terms.Draft`, `Terms.Version`; move the
  version on any change); the [brackets] are the lawyer's questions. Nothing is made unless the very
  text shown (SHA-256) was ticked, and it is kept **word for word** (`TermsAcceptance`, on Billing).
  **Name every handler on a multi-step page** — the company form looped back to the terms step until it
  had its own. `signupcheck` 39 (34 with the checks out). **Platform deploy (migration
  `SignupTermsAndWaitingList`)**; `TEST-3.6.md` section BB, 365–371.
- **EVERY TIME IN THE COMPANY'S OWN TIME ZONE (2026-10-05, §85). NOT on hardware.** The portal showed the
  SERVER's local time — the live server is `Etc/UTC`, so two hours behind — and read typed appointments
  the same way (**live-portal appointments 1–5 Oct were saved two hours early**). And on Windows, with
  `InvariantGlobalization`, no IANA zone could be found, so web chat hours ran in UTC. Now
  `Tenant.TimeZone`, **chosen at signup** (US/Canada/Europe must pick; ZA/UK filled in), changeable on
  Branding; `Services/CompanyClock.cs` is the one clock (`.Local()`, `ToUtc`, `Now`), set per request and
  at each hub entry; `Find` tries the Windows name too. **Never `ToLocalTime()`/`DateTime.Now` in the
  platform** (the console keeps its PC's clock). `clockcheck` 33 (29 / 23 with faults back — **built with
  `InvariantGlobalization` like the platform, or it cannot see the Windows fault**). **Platform deploy
  (migration `CompanyTimeZone`).** `TEST-3.6.md` section BC, 372–376.
- **TERMS, REFUNDS AND PRIVACY ON THE LANDING PAGE, FOR PAYFAST (2026-10-06, §88).** South Africa's
  terms are FINAL now (version 2026-10-06, every bracket answered by Clint: no VAT, cancel any time,
  no part-month refunds, errors refunded in 30 days, Information Officer Clint); other regions stay
  draft. A privacy policy (`Terms/privacy-ZA.txt`). The landing page's LEGAL block is **generated**:
  `RemoteSupport.Platform landing-legal <full path to landing\index.html>` after ANY change to `Terms/`
  — `legalcheck` 16 fails until you do. **Publish the page alone with `Publish-Platform.ps1 -LandingOnly`;
  never `Publish-Landing.ps1 -Server` on the live server** (it rewrites Caddy). `TEST-3.6.md` section BF, 392–395.
- **(2026-10-22: a TECHNICIAN can ask for outside help too — §155, below.)**
- **OUTSIDE HELP: FILES ON THE CUSTOMER'S TICK (2026-10-06, §92). NOT on hardware. ROADMAP 3.9 IS BUILT IN
  FULL.** The Allow prompt for an outsider has *Also let them copy files…*, **unticked**; the host sends
  `ConsentGrantedMsg.Files`; the hub keeps `SessionInfo.VisitorFiles` for THAT session and `FilesTicked`
  widens the allow-lists by the Files tool's messages and file chunks only — never clipboard, scripts,
  sound or voice. The prompt is now **sized from its text** — fitprobe (which now builds the host's real
  prompt by reflection) found a long guest name cutting off *"press Deny"*. `outsidervisit` 62 (58 / 56
  with two faults back), `fitprobe` 244. Platform deploy, FULL ship, client reinstall; section BJ, 417–420.
- **OUTSIDE HELP, STEP 4: A GUEST IN A BROWSER (2026-10-06, §91). Walked in the browser; NOT on hardware.**
  **`/guest/web`** + **`wwwroot/guestview.js`**: the guest console's conversation from a page — no server
  code changed. H.264 by **WebCodecs** (Annex B, no description, codec string from the SPS; a re-subscribe
  is the key-frame request) else JPEG tiles; the customer's real pointer; mouse, wheel and keys by
  `e.code` → Windows key codes. **Take the click's position BEFORE focusing, and focus with
  `preventScroll`** — the walk found clicks landing 180 px low. Proved against **`tools/guestwebdemo`**
  (REAL-PC = the real host on this PC: H.264 via Quick Sync decoded in the browser — only LOOK at it;
  FAKE-PC prints every input). `guestcheck` 33. Platform deploy only; section BI, 411–416.
  **Roadmap 3.9 is built in full except files on the customer's tick.**
- **OUTSIDE HELP, STEP 3: A GUEST — THE GUEST CONSOLE (2026-10-06, §90). NOT on hardware.** For somebody
  with no account (Clint's accounting firm): public **`/guest`** → **`/guest/console.exe`**, the SAME
  console .exe served as `remote-support-guest.exe`; the console sees it is a guest copy **before** the
  downloaded-console set-up (`GuestForm.GuestServer`: `--guest`, Zone.Identifier, file name) and opens
  `GuestForm` — name, company, code; nothing installed. **`/ws/guest`** (`GuestVisitor`): guess limit by
  ADDRESS, then §89's `StartVisitAsync` unchanged. **Who a guest is, is what they typed** — labelled
  *"…, a guest — not checked"* everywhere. **Their only credential is a token** (`HubRegistry.AddGuest`;
  tenant 0, negative id) that opens only their session and **dies with their guest connection**. No
  record for tenant 0. `guestcheck` 30 (22 with three faults back), `fitprobe` 218. **The guest window's
  own connection is driven by no harness — check 405.** Platform deploy from a FULL build; section BH.
- **OUTSIDE HELP, STEP 2: ANOTHER COMPANY CONNECTS WITH A CODE (2026-10-06, §89). NOT on hardware.**
  *Connect with a code…* in the visitor's own console; the code is spent only after a check (own
  company, offline, busy, too-old app → refused UNSPENT); the machine's hub starts the visit itself
  (`StartVisitAsync`, **never `StartSession`**) and the person is ALWAYS asked — **the tray app used to
  ask only if the BRANDING required consent**; now `RequireConsent || AskConsent`, and it says
  `DeviceCan.AlwaysAsk`. **`/ws/session` admits another company's viewer only via `AdmitsVisitor`** (that
  visitor, that technician). **`VisitorMaySend` / `VisitorMaySee` are ALLOW-lists** — screen and control
  only, binary by first byte; chat and Ctrl+Alt+Del results skip the visitor. Labelled *"Name
  (Company)"* everywhere here; the visitor's console gets its own copy (`TellVisitor`). The machine's
  company gets `VisitorPopup` (Watch / End); the visitor leaving ends it; switching off ends it.
  `outsidervisit` 54 (44 with four faults back). **Platform deploy, FULL ship, client reinstall**;
  `TEST-3.6.md` section BG, 396–404.
- **OUTSIDE HELP, STEP 1 OF 4 (2026-10-06, §87, roadmap 3.9). NOT on hardware.** A customer lets
  somebody OUTSIDE the IT company in with a code they make in the tray app (*Let someone else
  connect…*). Step 1 is the switches and the code; **nothing can connect with one yet** — step 2 is
  another company with a code. **`OutsiderAccess.Allowed`** (Shared) is the one rule: company switch
  (`Tenant.OutsidersAllowed`, off by default) overridden either way per machine (`Device.OutsiderMode`).
  Supervisor/owner only (`Permissions.CanAllowOutsiders`). `OutsiderCodeService`: nine digits, ten
  minutes, once, **hash only**, the guess limit on the guesser. Only the CLIENT connection makes a code;
  switching off stops live codes. Portal → Outside help; console right-click → Outside help.
  `outsidercode` 53 (50 with two faults back), `fitprobe` 164. **Platform deploy (migration
  `OutsideHelp`), console re-download, client reinstall**; `TEST-3.6.md` section BE, 385–391.
- **CALL OUTS RESCHEDULE; THE OFFICE IS TOLD OF BILLING (2026-10-05, §86). NOT on hardware.** Clint:
  *"Call outs need the option to reschedule and when something is assigned to billing, all office
  administrators should be notified."* **Reschedule** (`Workflow.Reschedule`) at Call out booked: new
  date and time + why, `@stay` (no `MovedTo`), the people on it, a supervisor **or the office**
  (`ActionDef.OfficeToo`, asked by `Workflow.MayPress` — use it, not `MayAct`, for a button).
  **Billing**: a badge and a pop-up, portal (`chat.js`, `event: billing`) and console (`BillingQueueMsg`,
  `BillingPopup`, one count on Tickets). Who: `Permissions.HearsOfBilling` — office administrators, or
  supervisors/owner when there are none, never both. The badge counts the Office lane NOT on hold.
  `ActAsync` raises `TicketService.BillingChanged`. `calloutcheck` 68, `billingwire` 37 (30 with two
  faults back). **Platform deploy, no migration, then re-download the console**; `TEST-3.6.md` section
  BD, 377–384.
- **BUGS #10, WAKE-ON-LAN AND PRINTING (2026-10-06, §93–§95). NOT on hardware.** Clint took the last three roadmap items; Microsoft 365 sign-in he left for later. **§93**: the first copy after a fresh logon is now reported (`tools/clipfirst` 11). **§94**: right-click an OFFLINE machine → *Wake up*; another of the SAME customer's machines on the same network (same internet address AND a shared network) sends the magic packet, handed MACs only (`tools/wakecheck` 50). Migration `WakeOnLan`; platform deploy, full ship, client reinstall. **§95**: remote printing BOTH ways — Tools → *Print on my printer* adds "Sue Tech's printer (…)" on their machine (Microsoft Print to PDF on a file port in THEIR temp — the spooler writes it as them) and the console prints each PDF; *Print a file on their printer…* prints from `%PRINT%` only, by a helper AS the person. PDFs are drawn by `Windows.Data.Pdf` called by hand (`Media/PdfPages`; GUIDs read from the .winmd) — no 20 MB SDK. `printcheck` 32 (+ `--elevated`), `sessionextras` 35, `outsidervisit` 65. Full ship, client reinstall. **`tools\Run-ElevatedChecks.ps1`** runs the two checks that need admin.
- **SEVERAL PHOTOS PER QUOTE ITEM, KNOWLEDGE BASE FOLDERS, LINK BUTTONS (2026-10-06, §96). Walked in the
  browser; NOT on hardware.** A photo names its line now (`TicketPhoto.QuoteLineId`, up to 12; `QuoteLine.PhotoId`
  is dead — read `QuoteLine.PhotoIds`, filled by `LinesAsync`); the portal sends each one on its own, made
  smaller, then the line; *Add photos* on a line, portal and console; the .eml names them `item-N-photo-M`.
  `/api/portal/photos` now takes part photos on ANY category (General quotes had none from the console).
  **Folders**: `KbFolder`, one SECTION each (General or one client) and everything in it the same section;
  six deep; removing one moves its contents up (a full one is a supervisor's/owner's); `?folder=N`, a
  breadcrumb, search below where you are; the editor's Folder box follows the client (**`clientpick.js`
  raises `change` now**). **Link buttons**: `[button: words | address]`, and an address alone on a line is
  *Open site*. `quotecheck` 129 (122 with two faults back), `kbcheck` 151 (147), `ticketwindowprobe` 161.
  **Platform deploy (migration `KbFoldersQuotePhotos`, with a backfill) then the console re-download**; no
  client reinstall. `TEST-3.6.md` section BN, 434–442.
- **QUOTE REQUESTS ASK STOCK AND ETA, AND ARE SIGNED (2026-10-06, §97). Walked in the browser; NOT on
  hardware.** One sentence, `QuoteService.AskDeliveryStockEta`. **Signatures** on the Suppliers page
  (`Services/QuoteSignatures.cs`): lines as JSON, each its own font/size/colour/style from FIXED lists —
  **never HTML** — and/or an image in the row (PNG/JPEG/GIF, 1 MB), below the text by default. Several →
  Request quote asks *Which signature?*, last used ticked (`Technician.LastQuoteSignatureId`), refused
  unchosen BEFORE the job moves; the ticket keeps it (`Ticket.QuoteSignatureId`) for downloading again.
  **A signature with words is the whole closing** — no "Thank you," of ours (Clint: *"Kind Regards, with
  the signature image directly below"*). Desktop draft: HTML + inline `cid:` image. Web mail: the link
  stops at *Our reference* and Open the email COPIES the drawn signature — **by selection first**; the
  clipboard API's write was refused in the built-in browser. Image at public `/quote-signature/{token}`.
  `quotecheck` 190 (185 with two guards out). **Platform deploy (migration `QuoteRequestSignatures`)
  only.** `TEST-3.6.md` section BO, 443–449.
- **ONE LOOK: ROUNDED, NO BLACK CORNERS, ONE COLOUR RULE (2026-10-06, §98). NOT on hardware.** Clint: the
  console (and the customer app) had square and half-square boxes, *"black lines sticking out"*, and a blue
  Tickets button among grey ones. **The black lines: Windows' `Button` is `ControlStyles.Opaque`**, so
  WinForms never painted a `RoundButton`'s corners, and the double buffer showed black (a new buffer) or a
  scrap of the last control (the shared ≤225×96 one). **Never give a `ButtonBase` its own `OnPaint` without
  `SetStyle(Opaque, false)`.** Square or Windows-coloured controls replaced in `RemoteSupport.UI`
  (`RoundControls.cs`): rounded `ThemedCombo` (`Ground.Round`), `FieldFrame` round plain text boxes and
  lists (**show/hide the FRAME**), `RoundCheck`, `DateBox`/`TimeBox` for `DateTimePicker`, `NumberBox`,
  `RoundSlider`, `RoundProgress`, `.Themed(t)` menus, list headers to the edge and no column rules
  (`EmptyFiller`). **Ghost and Neutral are one look, the portal's outlined `.btn`; Accent is the one main
  action**; the light signature is the tenant's accent, not our purple. **`tools/lookprobe`** (new): 28
  windows × 2 themes, corners checked against what is behind them — 163/250 old, **330/330** now; read
  §98 before the next pixel harness. Publish-Platform, console re-download, client reinstall; no
  migration. `TEST-3.6.md` section BP, 450–457.
- **THE FREE VERSION IS BRIEFED (2026-10-06), `docs/NEXT-SESSION-FREE.md`** — Clint's fourteen answers:
  one owner/technician, two unattended machines with a password set at install and no tray app, *"Being
  controlled remotely"* while connected, printing and Files only, no tickets, same signup with Free/Full,
  upgrade in place. Stages A–G; read it before any of them.
  **Stage A, REMEMBERED UNATTENDED PASSWORDS, IS BUILT (§100), both versions. NOT on hardware.** Typed
  right once, a technician is not asked again (any PC); a colleague still is. The server keeps a row per
  technician per machine with the machine's SALT at the time — **never the password or its hash** — so a
  password changed at the machine forgets everybody's. Checked in `PasswordLets` AHEAD of the lockout.
  Right-click *Forget saved password*; a supervisor/owner gets *Saved passwords ▸* (names, Clear, Clear
  everybody's). `unattendedpw` 64 (60 with two lines out). **Platform deploy (migration
  `RememberedUnattendedPasswords`), console re-download.** `TEST-3.6.md` section BR, 459–464.
  **Stage B, THE LIMITS AT THE HUB, IS BUILT (§101).** `Tenant.Plan` "free" (ask `FreePlan.Is`, Shared —
  the ONE place the limits live); `set-brand <slug> plan free`. One person; two machines **counted by
  hardware**; Remove twice a calendar month, the removed install refused on return; unattended only, with
  a password; no reason, no ticket; allow-lists for the session and the console (`TenantHub.Free.cs`).
  Found on the way, BOTH versions: **a reinstall's old id lingered as an offline twin** until a restart —
  `ForgetReinstalledId`. `freecheck` 42 (30 with four guards out). Migration `FreeVersionRemovals`.
  **Stage C, SIGNUP, IS BUILT (§102).** Free or Full after the country; the free terms are the region's
  with `Terms/free.txt` ahead of section 1 (`Terms.For(region, free)`); company name and phone optional,
  no colours, Owner+Technician; still approved by hand (an assumption), then Active with no trial.
  `signupcheck` 51. **AND NO TERMS SAY DRAFT ANY MORE** — Clint, 2026-10-06: *"assume they are correct
  as is"*; his lawyer's changes come back as edits and a new `Terms.Version`. US/CA/UK/EU still hold
  `[to be confirmed]` facts for before they open. **`landing\index.html` IS THE PUBLIC HOLDING PAGE** —
  nothing about the free version goes on it until the real site.
  **Stage D, THE FREE INSTALLER, IS BUILT (§103).** `Branding.Free` → `FreeInstallForm` (no Get help now):
  `FreeSetup.MayAddAsync` asks `/download/{slug}/may-add` BEFORE installing; `Install-Client.ps1 -Free`
  is the agent only (a dead agent fails it); then ONE client connection sets unattended on and the
  password — **answered by `RequestId`**, because a machine is told its state twice as it connects. An
  installed free copy never becomes a tray app. `freeinstall` 16 (13 without the echo). **FULL ship.**
  `TEST-3.6.md` section BT, 469–474 — the install itself is only proved on hardware.
  **Stage E, "BEING CONTROLLED REMOTELY", IS BUILT (§104).** `Host/ControlledNotice.cs`, a sibling of the
  privacy screen: an amber bar top centre of every monitor naming who is connected, topmost,
  click-through, **hidden from capture** (but never refuses if it cannot be). Follows `ParticipantsMsg`.
  Only on the free version: `StartSessionMsg.ShowNotice` → `--notice` (`AgentService.HostArguments`). A
  free session never falls back to a tray app. `noticeprobe` 21, with a not-excluded control. FULL ship.
  **Stage F, THE FREE PORTAL AND CONSOLE, IS BUILT (§105).** `Services/FreePortal.cs` is ONE allow-list a
  middleware asks of every `/portal` and `/api/portal` address — a new page is refused to a free company
  until added. `/portal/machines` (Remove: post-redirect, **codes not words in the address**). The console:
  `ApplyFree` from `StateMsg.Free`, Connect with only a password, Tools of Files and printing.
  `freeportal` 32 (21 with the middleware off), `dashtabs` 132, `toolbarprobe` 53.
  **Stage G, UPGRADE IN PLACE, IS BUILT (§107) — THE FREE VERSION IS COMPLETE, none of it on hardware.**
  `/portal/upgrade` (and an Upgrade button on the free console): the owner ticks the region's FULL terms and
  names the exact price (`TenantService.UpgradeQuote`, free → the package) → `UpgradeAsync`: full plan, a
  14-day trial (no approval again — an assumption), terms and price on record. `TenantHub.PlanChangedAsync`
  re-sends every open console its state (`StateFor`), so nothing needs a sign-in; machines and saved
  passwords untouched. `upgradecheck` 21 (16 with the hub not told). Hardware: `TEST-3.6.md` BR–BX, 459–496.
- **REMOVING A MACHINE ON THE FULL VERSION, AND UNINSTALL IN THE TRAY APP (2026-10-06, §106). NOT on
  hardware.** A supervisor or the owner, console or portal, with a reason (`RemovalReasons`, Shared; a note for
  Other); history kept; Devices lists *Removed machines*; an ONLINE agent uninstalls itself
  (`ErrorMsg.Uninstall` → `AgentService.UninstallSelf` → `Shared/ClientUninstall.cs`, the same
  `Uninstall-Client.ps1`, **now kept in Program Files**). The tray app's *Uninstall…* runs it elevated, then
  `ClientUninstallingMsg` (client only) records *Uninstalled at the machine*. **One removal for both versions**
  (`RemoveCoreAsync`); a removed install is refused on return on BOTH. `removecheck` 31 (25 with two guards
  out). Migration `DeviceRemovalReasons`, FULL ship, client reinstall. `TEST-3.6.md` section BW, 485–491.
- **RECURRING TASKS, A SIXTH TICKET CATEGORY (2026-10-07, §108). Walked in the browser; NOT on hardware.**
  Clint: daily / weekly / monthly and on what day, for maintenance, billing, readings; *"setup the task once
  ... and forget about it"*, with a cancel. **A `RecurringTask` is a schedule, never a ticket**: on each due
  day, at **06:00 company time**, it opens an ordinary `TicketCategory.Recurring` (5, offered last) ticket,
  assigned — Due → Done (closes) / To be invoiced, both **`OfficeToo`** (a billing task is often the office
  administrator's, so *Who it goes to* offers everybody). The next opens even if the last is open (Overdue).
  Anybody sets one up; its people, a supervisor or the owner change or cancel it; cancel stops future ones
  only. Types: Maintenance/Billing/Readings plus the company's own. **Every rule is
  `Services/RecurringTaskService.cs`.** Portal `/portal/recurring`; console New ticket → Recurring task, and a
  card on its tickets. **Read §108 before storing a DATE**: `PlatformDb` shifts any `DateTime` not marked UTC
  by the SERVER's zone — `RecurringTaskService.AsDate`. `recurringcheck` 98 (82 with that fault),
  `ticketformprobe` 126, `ticketwindowprobe` 175, `ticketwire` 43. **Platform deploy (migration `RecurringTasks`), console
  re-download**; `TEST-3.6.md` section BY, 497–505.
- **A DING FOR A CHAT MESSAGE, A RING FOR SOMEBODY WAITING (2026-10-07, §109). Walked in the browser; NOT on
  hardware.** Clint: *"a simple ding ding"* and, for support requests, *"a constant ringing which is not overly
  intrusive."* Two sounds made in code, **`Shared/Chimes.cs`** (one source: the platform serves the same bytes
  at `/sounds/{ding,ring}.wav`); played by **`UI/Sounds.cs`** (console, tray app) and **`wwwroot/sounds.js`**
  (portal, website chat frame, browser support). The ring repeats every 4 s while a call waits that this person
  has not dismissed, or a website visitor nobody has answered — `DashboardForm.UpdateRinging`; `Sounds.Ringing`
  is told the truth, never start/stop. Dings: customer chat, office chat from anybody else, the website visitor
  I answer, the tray app's technician messages — **never the history a reconnect hands back**. Mute: the
  console's header bell (🔔/🔕) and tray menu, per Windows user (`sounds.json`); the portal's button, per browser.
  Calls do not ring in the portal (it cannot answer one). **Read §109 before laying out the header: never read a
  control's `Visible` back in `LayoutHeader`** — it is false before the window is shown, and hid Light/Dark for
  good (`dashtabs` 131 of 133). `soundcheck` 52 (`--save <folder>` writes them to listen to). **Platform deploy
  (no migration), console re-download, client reinstall**; `TEST-3.6.md` section BZ, 506–512.
- **WINDOWS SHORTCUTS GO TO THEIR MACHINE (2026-10-07, §110). NOT on hardware.** Win and every Win+ shortcut,
  Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc, Alt+Esc and Print Screen used to act on the TECHNICIAN's PC — Windows
  keeps them before any key message. `Technician/ScreenView.Shortcuts.cs`: a `WH_KEYBOARD_LL` hook that exists
  **only while the picture has the focus**, takes ONLY those keys, and lets go of everything held over there
  when the focus leaves. **No on/off switch, by Clint's decision.** **Ctrl+C/V never pass through it** — keep it
  that way. The hook runs AHEAD of queued key messages, so a held Ctrl/Shift/Alt not yet sent goes first
  (`_remoteDown`), or Alt+Tab arrives as Tab then Alt. Win+L still locks the technician's own PC.
  `shortcutcheck` 39 (36 without the held-modifier line) — **it presses REAL keys; leave the keyboard alone
  while it runs.** Console only: `Publish-Platform.ps1` (the console rides in the server package), then re-download it; no client reinstall. `TEST-3.6.md` section CA, 513–518.
- **END-TO-END ENCRYPTION OF SESSIONS (2026-10-07, §112, roadmap 4.4; brief `docs/NEXT-SESSION-E2E.md`). NOT on
  hardware.** Clint's answers: encrypt AND pin machine keys; older apps allowed but marked; the browser routes
  included. **`Shared/SessionSeal.cs`** seals everything EXCEPT `ClearTypes` (what the hub acts on) at the
  connection — `WsConnection.Seal`, so no call site changed; a seal-less connection (the server) relays sealed
  frames untouched. **Ask `BinKind.Of(b[0])`, never `b[0]`** (a sealed frame sets the top bit). The receiver
  refuses a sealed message whose type is not its tag or is a clear type. Handshake `E2eHandshake` (P-256 ECDH,
  HKDF, AES-GCM, ECDSA identity from `Host/MachineIdentityStore`, SYSTEM only); pins `Shared/MachinePins` (a
  changed or UNSIGNED pinned machine holds the session until the technician answers); the hub's
  `TenantHub.E2e.cs` says seal or clear (an old console → clear, and every new badge SAYS so). Browser:
  `wwwroot/e2e.js` — **its clear list must equal C#'s (`e2ecore` checks)**; WebCrypto needs https or localhost.
  `ScriptAuditMsg` keeps the ticket's script line. `e2ecore` 48, `e2ewire` 31 (fails with either guard out),
  `e2einterop` + the guest page walked. **`Publish-Platform.ps1` (no migration), console re-download, FULL ship
  and client reinstall.** `TEST-3.6.md` section CC, 525–534.
- **A REINSTALLED MACHINE WAITS FOR THE OFFICE; PASSWORDS AT 600,000 ROUNDS (2026-10-07, §111, roadmap
  4.4). NOT on hardware.** A machine back from an uninstall is matched by its fingerprint — readable by any
  program on it — so a stranger's PC could take its row. The agreed "clear the tick" fix was no defence (the
  impostor holds the tray app); **Clint chose: the office confirms it.** `Device.ReinstallUnconfirmed`; the
  tick is kept; **`TenantHub.MayGoUnattended` is THE question for every unattended door — never ask
  `UnattendedEnabled` alone**; unconfirmed goes attended (free: refused). Confirm: right-click (console),
  Devices (portal), *It is mine* (free) — `ConfirmReinstallAsync`, supervisor/owner. **Found on the way:** a
  tick or password sent before the stored record was read was lost — every free reinstall left an unreachable
  computer; `WhenRecordReadAsync` now holds them. Passwords: `"600000$…"` salts, old ones re-made at sign-in
  without moving the stamp, **never a machine's unattended password** (remembered passwords key on its salt).
  `reinstallcheck` 33 (30 / 28 with the two guards out), `hashcheck` 15, `freecheck` 45 (found the free console refusing Confirm). **`Publish-Platform.ps1` (migration
  `ReinstallConfirm`), console re-download; a FULL ship and client reinstall for the tray app's and free
  installer's words.** `TEST-3.6.md` section CB, 519–524.
- **BRANDING REACHES INSTALLED MACHINES (2026-10-07, §113, roadmap 4.6 — the settings half). NOT on hardware.** A
  hello says `BrandHash`; a link with `DeviceCan.Branding` and another look is sent `BrandingMsg`, and saving Portal →
  Branding sends it to every connected machine (1.5 s settle: the page saves twice). **Only `BrandLook` travels —
  never the server address or the key.** `Shared/BrandRefresh.cs` is the one rule; the agent keeps it in
  `branding\live\` and **the installed `brand.json` is never rewritten** (the uninstaller finds the shortcuts by it);
  `Branding.Load` lays it over. The tray app restarts into it (`--tray --after <pid>`) only when idle — **"showing"
  is visible AND not minimised: started in the tray, its window is "visible" at -32000.** `brandpush` 45 (40 with two
  hub guards out). **`Publish-Platform.ps1` (no migration), FULL ship, client reinstall**; `TEST-3.6.md` section CD,
  535–541.
- **THE CONSOLE'S WINDOWS CARRY THE COMPANY'S ICON (2026-10-07, §114). NOT on hardware.** Only the main window had
  it; the sign-in, session, ticket, chat and tool windows showed WinForms' default. `BrandIcon.UseForEveryWindow`
  (in `Program.Main`, before the first window) makes the logo every window's default; **a `FixedDialog` takes no
  default icon, so one that shows in the taskbar sets `Icon = BrandIcon.Current` itself** (the default is a clone —
  setting the default object itself counts as a reset). The console's set-up writes `console.ico` for its
  shortcuts. `iconcheck` 24, `consoleexecheck` 32. `Publish-Platform.ps1`, then re-download the console;
  `TEST-3.6.md` section CE, 542–545.
- **PRIVACY SCREEN AND SESSION RECORDING (2026-09-29, §60 and §61, roadmap 1.7 and 1.6). NOT on
  hardware.** Privacy: black windows on every monitor marked `WDA_EXCLUDEFROMCAPTURE`, so the
  customer sees black and the technician's capture sees through; it REFUSES rather than half-works,
  and the console's tick follows what the host reports. `privacyprobe` 14 (the control captures
  black). Recording: Tools → *Record this session*, a red marker on the status bar; the console
  records what the technician sees into an .mp4 (`Media/ScreenRecorder.cs`, stride positive or it
  is upside down) and uploads it to the session's ticket (`RecordingService`, found through
  `SessionLog.TicketId`); only someone in the session may attach one, only a supervisor or the
  owner may delete; the tray app and the ticket are told. **Anything shown on a ticket goes in a
  partial both `_Timeline` and the classic page draw** — the card first sat only in the classic
  page, which no remote-support ticket uses. `recordcheck` 11, `sessionextras` 26 (25 without the
  in-the-session check). **`data/recordings` needs backing up.** Platform restart (migration
  `SessionRecordings`) and a full ship; `TEST-3.6.md` section AB, checks 177–186.
- **AS MANY WORKSHOP PHOTOS AS THE DAMAGE NEEDS, AND SMALLER (2026-09-29, §62). NOT on hardware.**
  Three is the LEAST, never the most. Fourteen phone photos in one request were 60 MB against the
  server's 30 MB default and ALL were lost; now one photo per request (portal and console, with
  "Uploading 5 of 14…"), the server takes 400 MB in one anyway (old consoles), and *as it arrived*
  can be added to after booking, at both ends, in both places. Photos are made smaller where they
  are picked — 2048 px, JPEG 85% — by `wwwroot/photos.js` and `Technician/PhotoShrink.cs`, the same
  rule; not on the server, which is headed for Linux. **A harness about upload size must use real
  sizes** — `photomany`'s first run used 1.3 MB photos and passed. `photomany` 21 (0 of 14 in one
  upload on the old code). Platform restart and `-OnlyTechnician`; `TEST-3.6.md` section AC.
- **DELIVERY INSTEAD OF COLLECTION (2026-09-30, §63, roadmap 3.7.1). NOT on hardware.** Clint's
  next four, in order: delivery, several machines per booking, office chat (3.8.1), support in a
  browser (3.8.2) — his words and answers in `NEXT-SESSION-WORKFLOWS.md` ("Fourth round") and the
  roadmap. **Out for delivery** sits beside Machine collected (same people, photos out taken at the
  office), keeps the billing stage in `StageReturn`, goes to `WorkedBy`; then Delivered — work done
  (time ADDED to the bench's) → To be invoiced / paid on delivery / nothing more (back to the
  billing). `TicketService.Offered` hides To be invoiced while an invoice is out unpaid.
  `deliverycheck` 48 (44 without `Offered`); walked in the portal. **Platform restart (migration
  `Delivery`) and `-OnlyTechnician`**; `TEST-3.6.md` section AD, checks 193–199.
- **SEVERAL MACHINES AT ONCE (2026-09-30, §64). NOT on hardware.** *Add another machine* is a STEP at
  Booked in (not a repeating block on New ticket), so both UIs draw it with the generic step form.
  Fault first, the booking's questions, three photos of THAT machine staged by `PhotoStageId`. A
  ticket of its own, same client/people/priority; the whole booking shares `Ticket.BookingId` (the
  first ticket's id) as EQUALS — nothing waits for a sibling. `machinescheck` 36 (33 with the booking
  keyed on the ticket pressed); walked in the portal. **Platform restart (migration
  `BookedTogether`) and `-OnlyTechnician`**; `TEST-3.6.md` section AE, checks 200–205.
- **OFFICE CHAT (2026-09-30, §65, roadmap 3.8.1). NOT on hardware.** One to one and Everyone, staff
  only. `StaffChatService` holds every rule; its `Sent`/`ReadChanged` events reach the hub (consoles,
  `StaffChatForm`, made at sign-in and kept hidden so it can pop up) and the portal's server-sent
  event stream (`wwwroot/chat.js`, on every portal page). A message TO you, or an Everyone message
  naming you with @, pops up without taking the keyboard; a portal write needs `X-Chat: 1`.
  `chatcheck` 51 (50 with the hub leaking to every console), `staffchatprobe` 18; walked in the
  portal with `tools/chatsay`. **Platform restart (migration `StaffChat`) and `-OnlyTechnician`**;
  `TEST-3.6.md` section AF, checks 206–213.
- **SUPPORT IN THE BROWSER (2026-09-30, §66, roadmap 3.8.2). NOT on hardware.** `/help/{slug}`: a
  form that rings the technicians; the customer shares their screen, VIEW ONLY (no browser allows
  input). Switch and hours on Portal → Browser support (`WebSupportHours`, off by default). A caller
  is a pretend device `web-…` in `TenantHub.Web.cs`, never saved or listed; they become a contact
  and a ticket. The page (`wwwroot/help.js`) speaks the HOST protocol: H.264 from WebCodecs in the
  host's own binary layout, so the console's decoder shows it. **Read §66 before touching
  `DeviceConns`, `RouteChat` or `StartSession`.** `websupportcheck` 64 (63 without view only);
  `--browser` decoded a real browser's H.264, 62 of 62; `viewonlyprobe` 12. **Platform restart (migration
  `WebSupport`) and `-OnlyTechnician`**; then switch it on. `TEST-3.6.md` section AG, checks 214–222.
- **TALKING TO THE CUSTOMER, BOTH WAYS (2026-09-30, §67, roadmap 3.3). NOT on hardware.** Tools →
  *Talk to the customer*: the technician's voice to their speakers; the customer is ASKED (tray
  banner, or the browser page) whether to use their microphone — every session, **never unattended**
  (the hub enforces it). Open mic, a pill that mutes; recordings stay picture only. Windows' echo
  canceller (`EchoCancelledMic`, Media) at both ends; **it delivers nothing until a render stream is
  open**, so the player runs for the whole call. Listen leaves the host out during a call (process
  loopback). **The hub drops a `VoiceMicAnswerMsg` from a viewer** — without that a technician could
  open the customer's mic; found by `voicewire` (37). Browser: host socket opens at the answer, so a
  phone can talk; `websupportcheck --browser-voice` 26. `docs/NEXT-SESSION-VOICE.md`. **Platform
  restart (no migration), full ship, client reinstall**; `TEST-3.6.md` section AH, 223 first
  (micprobe on Windows 11 — only Windows 10 is measured as SYSTEM).
- **CUSTOMER NAMES READABLE, CUSTOMERS FIRST, USERNAME REMEMBERED (2026-09-30, §68). NOT on
  hardware.** The console's customer headings were Windows' own ListView group headers, black on
  the dark list; `UI/GroupHeaders.cs` now draws them (custom draw, `LVCDI_GROUP`) in the column
  headings' signature colour. **Read §68 before touching it**: `NMHDR` nested, groups asked by ID
  (WinForms hides a default group at index 0), and the chevron sits on the list's own fold button.
  The portal's Devices page lists unlinked machines LAST, as the console does. The console fills in
  the last username that WORKED (`LastSignIn`, per Windows user and per server; never the password).
  `dashtabs` 84, `devicelink` 20 (19 old), `signincheck` 12 (7 old). **Platform restart (no
  migration) and `-OnlyTechnician`**; `TEST-3.6.md` section AI, checks 233–237.
- **A TECHNICIAN DOWNLOAD LINK AND SIGN OUT (2026-09-30, §69). NOT on hardware.** `/tech/{slug}`: the
  technicians' `/get` — they sign in ON the page with their console login, then download; who may is
  `Permissions.CanDownloadConsole` (a seat, or supervisor/owner), asked by the zip route too, for the
  cookie's own company only. Copy link on Portal → Downloads. The console has **Sign out** (header,
  far right, and the tray menu); the X still goes to the tray on purpose. Sign out relaunches the
  console as a FRESH PROCESS at sign-in, then the old one `Environment.Exit`s. `techdownload` 22 (19
  old), `dashtabs` 98; the relaunch itself is unharnessed (check 241). **Platform restart (no
  migration) and `-OnlyTechnician`**; `TEST-3.6.md` section AJ, checks 238–243.
- **WEBSITE CHAT (2026-09-30, §70, roadmap 3.8.3). NOT on hardware, and NOT LIVE until hosting.** A
  company pastes `<script src=".../webchat/{slug}.js" async>` on its own site; a bubble opens OUR frame
  (`/webchat/{slug}/frame`); visitors give a name and an email or phone and chat; it rings EVERYBODY
  signed in — console (`WebChatForm`, a button shown only while it is on) and portal (the Chat corner,
  amber). **First reply takes it** (one conditional update). Nobody signed in (`StaffPresence`: a
  console or an open portal page) or outside its hours → a message → a General ticket. Every rule is
  `WebChatService`; the visitor socket `/ws/webchat` is plain JSON, never the console protocol.
  `Services/Visitors` is now browser support's contact rule too (phones by digits). **Read §70 before
  touching the frame's query (`from`, never `page`) or the embed endpoint (`WebRootFileProvider`).**
  `webchatcheck` 80 (76 with the claim broken), `webchatprobe` 32; walked in the browser.
  **Platform restart (migration `WebsiteChat`) and `-OnlyTechnician`**; `TEST-3.6.md` section AK,
  checks 244–251.
- **ONE DOWNLOADABLE .EXE INSTEAD OF THE ZIP (2026-09-30, §71). NOT on hardware.** Clint: *"every
  download an exe"*. The download is the customer app with the host, agent and scripts packed
  inside (`ClientPayload`), the SAME file for every company — a per-company file could not be signed
  (`CODE-SIGNING.md`, "One .exe for every company"). `/download/{slug}` serves it as
  `{slug}-support.exe`; a copy with no branding beside it learns whose it is from the browser's
  Zone.Identifier, its name, the last company, or asks (`DownloadIdentity`, `Launcher`), then offers
  Get help now / Install (`LauncherForm`, same `Install-Client.ps1`). **`tools/Pack-ClientExe.ps1`
  signs inside first, outside last**; ship-build step 4b runs it; signing moved to `tools/Signing.ps1`.
  `oneexecheck` 52, a REAL packed .exe driven through `--identify`/`--unpack`/`--stage-install`.
  **Full ship, no platform restart**; `TEST-3.6.md` section AL, checks 252–258.
- **SIGNING IN SAFELY, BEFORE GO-LIVE (2026-10-01, §72). NOT on hardware.** Clint feared the tenant
  key was "easily hackable": it is 192 random bits AND public by design (every install needs it) — an
  address, not a lock; **do not "fix" it**. 256-bit encryption is TLS (Caddy). What guarded every
  machine was a technician's PASSWORD, so: **two-factor for every sign-in** (authenticator app,
  `Shared/Totp.cs`; QR at the next sign-in; ten recovery codes; Reset two-factor and Sign out on the
  People page), **a security stamp** on every token and cookie — removing somebody or changing their
  password/boxes/2FA closes their console AT ONCE (hub) and the portal on its next page (a 15 s sweep
  catches the CLI) — tokens expire after 12 idle hours, **lockout** (5 per account, 30 per address),
  **`UseForwardedHeaders`** (behind Caddy every caller WAS 127.0.0.1), HSTS/no framing except
  `/webchat`, **apps refuse plain http/ws to a public address** (`SecureTransport`), owner portal key
  + code, and `go-live.sh` step 6 (updates, fail2ban, SSH keys-only IF a key exists, nightly AES-256
  backup — **copy `/etc/rsp-backup-key` off the server**). **Harness platforms set
  `RSP_Security__RequireTwoFactor=false`; never a live server. Live-server harnesses need
  `"totpSecret"` in `tools/rs-credentials.json`.** `securitycheck` 63 (54 with the three fixes out).
  **Platform restart (migration `TwoFactorSignIn`, signs everybody out once) and a full ship**;
  `TEST-3.6.md` section AM, checks 259–268.
- **FIVE FAULTS FROM INTERNAL TESTING (2026-10-09, §122). Walked in the browser; NOT on hardware.** (3) **Off reads
  offline**: `WsConnection.DropWhenSilent` aborts a device/console socket that pinged and then went quiet 45 s
  (`Platform:SilenceSeconds`) — only ones that have pinged, so harness fakes are untouched (`silentoffline` 13).
  (4) **Reconnecting asks nothing**: the console connects with NO reason first; the hub carries on the ticket or says
  `ErrorMsg.NeedsReason`; **`ActiveForDeviceAsync` counts the 30 minutes from the END of the last session** (moved,
  not widened — read its comment) (`reasonwire` 20). (5) **Link to parent ticket no longer closes the child**; the
  job waits for it — `TicketService.ClosesJob` is the one rule, `Shared/JobParts.Refusal` the one wording, a pop-up
  in both UIs (`routecheck` 61). (1) **Part of / Not part of it** on the routed ticket page and the console;
  *Unlink from {customer}* on right-click, *Unlink* on Devices. (2) **Request Support: subject + optional
  description + a picture of the screen, ticked with a preview** (`Client/ScreenSnap`; `PhotoKind.Screenshot`;
  `Shared/SupportRequest.Words` tidies old tray apps' one box) (`requestwire` 20, `callprobe` 50).
  **`Publish-Platform.ps1` (no migration) + console re-download; a FULL ship and client reinstall for (2).**
  `TEST-3.6.md` section CH, 556–571.
- **UPDATING THE CLIENT FROM A TICKET (2026-10-09, §123). NOT on hardware, NOT against a real mailbox.** Clint: a comment
  can go to the client — *the original fault plus only that comment*, with the commenter's name; their reply comes back
  onto the same ticket. *Also email this to the client* (unticked) under the comment box, and *Email to the client…* on a
  comment already posted — portal (both layouts) and console; the ticket's contact by default, changeable or typed. Sent
  from Tickets by email's address (`TicketIntake.SendAsCompanyAsync`) and **offered only once that works**; signup now asks
  for the address (saved switched off). A reply is `TicketNote.FromCustomer` and flags everybody on the ticket plus the
  last sender, **cleared by OPENING the ticket** (`SeenCustomerRepliesAsync`); also accepted from any address an update
  went to. Every rule is `Services/ClientUpdates.cs`; **a bad address posts nothing** (`CheckAsync` first). **Found: `dotnet
  ef --context PlatformDb` made a PostgreSQL migration** — `PlatformDbDesignTime` fixes it; check a new migration's
  Designer says `typeof(PlatformDb)`. `clientupdatecheck` 91 (83 with three guards out), `ticketwindowprobe` 207; walked in the browser. **`Publish-Platform.ps1`
  (migration `ClientUpdates`), console re-download; no ship.** `TEST-3.6.md` section CI, 572–581.
- **JOB TYPES IN COLOUR, THE PORTAL MENU AS TABS (2026-10-09, §124). NOT on hardware.** A wrong category had been chosen.
  `TicketSummary.CategoryColors` (Shared) is the ONE list; portal `.cat-pick`, console a row of `RoundButton`s (new
  `Tint`/`TintInk`/`Chosen`) across the top of New ticket — **the hidden `_category` dropdown is still the record of the
  choice**; no tick in the console (it wrapped the row at 150%). The portal/owner menu is `nav.tabs`, the current page
  filled by a script in `_Layout`. `ticketformprobe` 140. `Publish-Platform.ps1`, console re-download; section CJ, 582–585.
- **THE LIVE SERVER RUNS ON POSTGRESQL SINCE 2026-10-09 11:56 UTC (§125)** — rehearsed first on a copy of live (all
  twelve steps), then Step A and the switch on live: down 11 s, 2,538 rows identical. Data and backups on `rsp-files`
  (vdb, 50 GB), PostgreSQL on `rsp-db` (vdc, 20 GB). **Every `Publish-Platform.ps1` deploy keeps it on PostgreSQL**
  (the drop-in `postgres.conf`). The way back: `Switch-BackToSqlite.ps1` (the SQLite file is frozen at the switch).
  Still to do: retire the SQLite fallback about a week later (not built); **step 5, the xneelo load test — brief
  `docs/NEXT-SESSION-LOADTEST.md`; RUN ON XNEELO 2026-10-12/13 (§126): 18 COMPANIES PER CELL** of Clint's profile (10
  technicians, 500 machines with agents, 10 sessions each) — ~18,500 connections on `s-g-4cpu-8gb`. **It bends at ~27
  companies on CADDY'S PORTS to 127.0.0.1:8090 (BUGS #12) and the failure is total — every sign-in refused; live has
  the same ceiling. Then memory at ~33 (the platform OOM-killed; no swap). A restart at 20 passed but tight (97% CPU,
  pool at 40, Caddy kept +1.8 GB).** **THE PORT FIX IS BUILT AND PROVEN (same day, `39d6498`): Caddy reaches the platform
  through `/run/rsp/platform.sock` (caddy in group rsp; 127.0.0.1:8090 kept for local checks); 30 companies on live's
  default ports then carried — 0 TCP, 30,902 through the socket. **ON LIVE since the 2026-10-15 deploy: step 7 said
  "platform on /run/rsp/platform.sock, as caddy -> HTTP 200".** Read `UnixSocket.TrustCaddy` before touching forwarded headers** (a socket has no
  remote address; `unixsockcheck` 16). Memory is now the limit, so a bigger flavor does help; the cell stays 18.
  `Run-LoadStep.ps1 -Companies N [-Storm]`, `Get-LoadResults.ps1`, `loadstatscheck` 44. `copy-to-postgres`
  (`pgcopycheck` 42), `deploy/rsp-backup.sh` (`backupcheck` 30), `Switch-ToPostgres.ps1` / `Switch-BackToSqlite.ps1`
  (`switchcheck` 51). **`PgInitial` is FROZEN: every model change now needs TWO migrations, the same name in each —
  `--context PlatformDb` AND `--context PgPlatformDb --output-dir Data/PgMigrations`** (`pgcheck` fails otherwise). The
  drop-in `postgres.conf` is the one sign a server is on PostgreSQL; go-live never writes it.
  (Steps 1–3: §118–§121, BUGS #11 fixed; the brief was `docs/NEXT-SESSION-POSTGRES-SWITCH.md`.) **MOVING TO POSTGRESQL (2026-10-08, §118–§119), steps 1 and 2 of 5 done: the platform CAN run on it, and 61 of 61 harnesses pass on BOTH databases.** `RSP_TEST_DB=postgres` puts a harness on PostgreSQL (`tools/Common/HarnessDb*.cs`; `tools/Run-DbHarnesses.ps1 -Postgres` runs them all) — **a new harness that makes a database uses `UseHarnessDb`, `HarnessDb.MigrateAsync` and `HarnessDb.Use`, never `UseSqlite` or `Database.MigrateAsync()`**. SQLite stays
  the default until the live switch (step 4); `ConnectionStrings:Postgres` switches it. **`dotnet ef` now
  needs `--context`**: `--context PlatformDb` for every SQLite migration as before. The PostgreSQL set
  (`Data/PgMigrations`, context `PgPlatformDb`) begins with `PgInitial`, which is **FROZEN since 2026-10-09 (§125) —
  never make it again**; each model change adds a PostgreSQL migration beside its SQLite one — **`tools/pgcheck` fails
  until it does** (and when a SQLite migration is missing, or PgInitial was remade). Load test first:
  `tools/loadtest` and `deploy/loadtest` (§115), BUGS #11.
- **A NEW MACHINE'S TWO HELLOS AT ONCE (2026-10-12, §127). NOT on hardware.** The load test's duplicate key was not
  harmless: a first install's tray app and agent both inserted the row, the loser's details were lost, and the shared
  wait opened early so a password set next was undone in the live record. **`TenantHub.RecordGate`**: one machine's
  hellos stored in turn, the wait opening after the last. **A retry on the duplicate key made it worse** — do not "simplify"
  to one. `firsthello` 9 of 9 on both databases (`Testing:PersistDeviceGapMs` opens the gap on SQLite). `Publish-Platform.ps1`, no migration.
- **DAMAGE REPORTS, FILES ON COMMENTS, GOING BACK, BOXES THAT GROW (2026-10-13, §128). Walked in the browser; NOT on
  hardware.** Clint took every recommendation. **Not repairable → Damage report needed** (`Workflow.DamageReport`):
  comments (optional, written FIRST — `ClientUpdateService.Words` cuts before "Time spent:", so time and amount never
  reach the client), the report (required), time, amount, emailed to the client TICKED by default, then To be invoiced.
  **Files and photos on every comment** (`TicketAttachmentService`): each uploaded as picked, held LOOSE by its picker,
  claimed only by that person on that ticket; files alone are a comment; emailed as attachments, refused over 15 MB
  together before posting. **Back at every stage** (`TicketService.Back.cs`, key `go-back`): every stage move now writes
  `TicketNote.Before` (a `StepSnapshot`) — **any new path that moves a stage must write it**; Back puts every route field
  back, marks the step undone, warns (never refuses) of invoices, payments, emails; a closed job is reopened by a
  supervisor or the owner only; never across a category change. **Boxes grow to 15 lines**: `autogrow.js` (portal),
  `InputBox.GrowWithText` (console). `ticketstepcheck` 124 (120 with two guards out), `ticketwindowprobe` 231. Found:
  a `ticketwire` race (a refused step's late ticket). **`Publish-Platform.ps1` (migration `CommentFilesAndGoingBack`),
  console re-download**; no client reinstall. `TEST-3.6.md` section CL, 588–599.
- **THE CUSTOMER'S NAME REMEMBERED; A PICTURE OF WHAT IS OPEN (2026-10-13, §129). NOT on hardware.** The request window
  starts with the last name SENT (`Client/LastContact.cs`, `%LOCALAPPDATA%\RemoteSupport\contact.json`, per Windows user).
  **The capture was never the fault** — measured: it takes an open Explorer window whole. The shortcut is on the DESKTOP,
  so customers Win+D to reach it and minimise the very window they need help with. So `ScreenSnap.OnlyDesktop` says so
  beside the preview, and **Take a new picture** puts our windows at Opacity 0 (never Hide — it ends a modal) with
  `PictureBar` at the top until they press *Take the picture*. Do not "fix" it by restoring their windows. `callprobe` 66.
  **`Publish-Platform.ps1` (no migration), client reinstall**; `TEST-3.6.md` section CM, 600–605.
- **EDIT A TICKET, SORT BY DATE, AND THE UNASSIGNED TILE (2026-10-13, §130). Walked in the browser; NOT on hardware.**
  Subject, description, appointment and category, by anybody who can see the ticket — every rule `TicketService.EditAsync`,
  one story entry with what each was. **A category change restarts at the new route's first stage**, and Back never crosses
  it; **to Workshop needs a technician and three photos in** (arrival photos now go on any open ticket). Portal
  `_EditTicket.cshtml` — **read `subject`/`description` from `Request.Form`: binding makes an empty box null**; console
  `TicketEditMsg`, `TicketsForm.EditCard`. Sort: `Shared/TicketSort`, **sorted on the server before the take**; portal
  *Sort by* (Customer = the grouped view), console *Sort:*. **The dashboard's Unassigned counted billing-queue jobs** (no name
  on them either) — it asks the list's rule now. `ticketeditcheck` 60 on both databases, `ticketwindowprobe` 247.
  **`Publish-Platform.ps1` (no migration), console re-download**; `TEST-3.6.md` section CN, 606–615.
- **PAYFAST AND THE REAL WEBSITE ARE BRIEFED, `docs/NEXT-SESSION-PAYFAST.md` (2026-10-13)** — roadmap 4.8 plus the
  site the holding page hides, with a website report folded in and the AI assistant card removed (POPIA; never built).
  Clint's eleven answers are all *"go with your recommendations"*; do not re-ask them. Card at signup, R0 now and
  charged after the 14-day trial; Full switches on by itself, Free stays by hand. **Everything stays hidden** (holding page,
  `Payfast:Sandbox` true on live) until the Sectigo certificate and Clint's go. **PayFast's Recurring Billing page is set
  (notify URL `/api/payfast/notify`, IP whitelist = the live server — move it if the server moves); the general ITN switch
  stays OFF.** **Stage A, THE PAYFAST CORE, IS BUILT (§134). NOT on hardware.** `Services/PayfastWire.cs` (three signature
  rules — checkout in PayFast's DOCUMENTED order, a notice in the order POSTED with blanks, the API ALPHABETICAL — and PHP's
  urlencode) and `Services/PayfastService.cs` (the account, stored with sandbox/live and only changed together; checkouts;
  `/api/payfast/notify` believed only from PayFast's addresses, signed, our merchant, PayFast's own `VALID`, our subscription;
  refused notices are never stored; 503 = send again). `/owner/payfast` with a SANDBOX test checkout. The account goes on the
  server by **`deploy\Set-PayfastKeys.ps1`** (never chat, never an argument); **PayFast's public test merchant is refused by
  the real sandbox — test with Clint's own (`tools\Save-PayfastSandbox.ps1`).** `payfastcheck` 56, real sandbox included (42 of 50 with the address check
  out), both databases. **`Publish-Platform.ps1` (migration `Payfast`), then `Set-PayfastKeys.ps1`**; `TEST-3.6.md` section CQ,
  631–638. **Stage B, SIGNING UP AND PAYING, IS BUILT (§135). NOT on hardware.** `Services/CompanyBilling.cs`: a
  full-version SOUTH AFRICAN signup ends at PayFast (R0 now, the package from 14 days on), and PayFast's notice switches it
  on through `AdminCompanies` and emails the owner — Approvals marks it and **refuses to approve it**. `/signup/paid` waits
  for the notice; Billing's *Add your card* (owner only); Upgrade goes on to PayFast. **Approving by hand now emails the
  owner** (it never did). **`/owner/preview`** gives one browser the real site and signup (`SitePreview`; Caddy's holding
  matcher skips the `rsp_preview` cookie). **Stage D must use 14 days' grace: the ZA terms say "more than 14 days late".**
  `signuppaycheck` 39, both databases. `Publish-Platform.ps1`, no migration; section CR, 639–646. **Stage C, THE AMOUNT
  KEPT TRUE, IS BUILT (§136). NOT on hardware.** `CompanyBilling.SyncAmountAsync` is the one rule (owed vs what PayFast has,
  PATCH in CENTS from the next charge); `TenantService.PeopleChanged` tells it at once, `PayfastSweep` (hourly) retries and
  catches the command line; a refusal shows on Billing. Our receipt per payment; Billing lists payments. **Stage D: 14 days'
  grace (Clint, 2026-10-14).** `signuppaycheck` 49 (46 without the hook). Migration `PayfastAmounts`; section CS, 647–651.
  **Stage D, A PAYMENT THAT DID NOT COME AND CANCELLING, IS BUILT (§137). NOT on hardware.** `Services/CompanyBilling.Late.cs`:
  lateness is an ABSENCE (no notice comes for a failure) — PayFast's API asked first (`LookAsync`: moved on, or cancelled
  there); reminders at 1, 7 and 13 days with PayFast's change-the-card link; **suspended when MORE than 14 days late** through
  `AdminCompanies` (which now takes the email to send); a payment lifts only a suspension WE made. Cancel on Billing tells
  PayFast first; **service ends the day BEFORE the charge it was waiting for**, then Closed; a new card resumes on the old
  date. All companies shows each card. `signuppaycheck` 70 (68 with two guards out), both databases. Migration
  `PayfastLateAndEnd`; section CT, 652–658. **Stage E, THE REAL WEBSITE, IS BUILT (§138), words NOT yet checked by Clint
  (`docs/WEBSITE.md`).** Twelve public pages in `Services/SitePages.cs` — **every one must be in go-live.sh's `@holding` list**
  or it is public the moment it deploys; `tools/sitecheck` (61) fails otherwise, and parses every page for HTML errors, SEO,
  links, size and stale claims. `/privacy` is new (PayFast's list). No migration; section CU, 659–664. **REVIEWS FROM
  COMPANIES ARE BUILT (§139). NOT on hardware.** Clint: asked a week in, Later a month, then Don't ask again; stars, words,
  name and **company name (required)**; **published only after a platform admin approves it** (`/owner/reviews`). Every rule
  `Services/ReviewService.cs`; the home page shows the six newest, encoded, links `rel="ugc"`. `reviewcheck` 28. Migration
  `CompanyReviews`; section CV, 665–669. Earlier, 631–638. **On the live server 2026-10-13: a checkout and its notice PASSED; a cancellation was refused (INVALID from PayFast's confirmation) — now confirmed through PayFast's API instead, with Check with PayFast; redeploy.**
- **THE WEBSITE IN LIGHT AND DARK, SCREENSHOTS, THE COOKIE LINE (2026-10-14, §140). Walked in the browser; NOT on hardware.** Public
  pages pick light or dark before the stylesheet (visitor's choice in `localStorage`, else their computer's, else dark) with the
  portal's switch in the menu. **Screenshots come from `tools/siteshots` ONLY** — the real windows with a made-up company
  (*Coastline IT*) and made-up customers, the customer's screen drawn in code; **never capture a real screen for the site.**
  `SitePages.Shots` holds each picture's size; `_Shot.cshtml` draws its dark and light pair. Found on the way: the console's
  machine-list columns ignored display scaling — measured now, shared by `FitDeviceColumns` when narrow. The privacy policy names
  its cookies (`Terms.Version` 2026-10-14, `landing-legal` re-run). `sitecheck` 93, `dashtabs` 133, `legalcheck` 16.
  **`Publish-Platform.ps1`, console re-download**; `TEST-3.6.md` section CW, 670–676.
- **EVERYTHING IS ON .NET 10 (2026-10-14, §141). LIVE AND PROVEN ON HARDWARE: every harness passes, the old .NET 8 apps still work with it (677), and the new apps on Windows 10 passed 678–683 on 2026-10-15 — Clint: *"all working."*** .NET 8 leaves support on 10 November
  2026, and a Mac host (stage 6) would need .NET 10 — macOS 14+, so not Catalina. Projects `net10.0`, packages 10.0.x, the
  patched `System.Security.Cryptography.Pkcs` named in the platform (MimeKit asks for a vulnerable one), **WFO1000 off** in
  `Directory.Build.props` (nothing uses the WinForms designer). **Building needs the .NET 10 SDK.** Self-contained, so nothing
  installs on a customer machine; **Windows 10 Home/Pro 22H2 runs it but is off Microsoft's .NET 10 list** (only LTSC/IoT).
  **The local platform's data is now `bin\Release\net10.0\data`.** A new worktree needs the git-ignored `tools\rs-postgres.txt`,
  `branding\brand.json`, `tools\rs-credentials.json` copied in. 74 of 74 database harnesses on both databases, `lookprobe` 346,
  `fitprobe` 324, `iconcheck` 24, H.264 at 202 KB/s. **`Publish-Platform.ps1` (no migration), console re-download, client
  reinstall**; `TEST-3.6.md` section CX, 677–683 — on a Windows 10 machine.
- **A BLURRY PICTURE THAT NEVER SHARPENED (2026-10-15, §142). NOT on hardware.** DESKTOP-STD8M9C: *"very pixilated when
  connecting and then slowly starts to render correctly, but is never clear."* At a constant bit rate the automatic key frame every
  24 frames threw the sharpened picture away (46.9 → 36.1 dB), and nothing sharpened a still screen. Now **key frames a minute
  apart** (`H264Encoder.KeyFrameSeconds`) and **`Host/StillRefiner.cs`**: 2 s of the still screen encoded again after a change of
  1%+ of the screen (`DxgiCapture.LastVideoChange`) — **a blinking cursor must not re-arm it**, and those frames are NOT changes to
  the link controller. **The hub asks for a picture when it drops a video frame to a slow viewer** (`KeyFrameRequestMsg`, clear,
  once a second) — the old 2 s floor had hidden that. **Found: `DXGI_OUTDUPL_FRAME_INFO` had C# `bool`s behind a raw pointer
  since 2026-09-16**, so every field after `AccumulatedFrames` was misread; `int`s now, and `UseRectsForTiles = false` keeps the
  tile path comparing every tile as it always has. New: `codecbench --sharp` / `--hostloop` (5 of 5 at quality 55 and 35, the
  old host fails), **`tools/sharpwire` 9 of 9 with the REAL host** (covers the screen ~40 s), `tools/keyframewire` 12 (11 with the
  request out). **`Publish-Platform.ps1` (no migration) FIRST, then reinstall the client**; `TEST-3.6.md` section CY, 684–690.
- **TWO MACHINES AT ONCE, AT MOST; COPY ON ONE, PASTE ON THE OTHER (2026-10-15, §143). NOT on hardware.** Clint: two sessions
  side by side, one per monitor. **`Shared/SessionLimit.cs`** is the one number (2) and wording; the console refuses a third
  BEFORE asking (`DashboardForm.OverSessionLimit`: open windows + `_waits`), the hub too (`Hub/TenantHub.SessionLimit.cs`:
  `ConnectDevice`, `AcceptCall`, the viewer socket — sessions on OTHER machines where they have a viewer socket now,
  `Session.ViewerIds`). **A machine already open never counts** — never refuse a reconnect. Copy/paste between them already went
  through this PC's clipboard; the gap was an early paste while A's files were still arriving — **`SessionForm.Between.cs`**: B
  says so, sends nothing, and sends them when they land (still only to the window in front). `sessionlimitwire` 18 (8 with the
  rule off), `twosessions` 27 (23). **Host-to-host transfer (not through this PC) is the asked-for next step, NOT built** — it needs
  its own E2E handshake between the two machines. `Publish-Platform.ps1` (no migration), console re-download; section CZ, 691–697.
- **COPY ON ONE MACHINE, PASTE ON THE OTHER — STRAIGHT BETWEEN THEM (2026-10-15, §144). NOT on hardware.** Clint: not through
  the technician's PC. With two windows open, A's copied files are HELD (A's status bar: *click into B's window and paste, or click
  here to bring them to this PC*); clicking into B asks the hub to **bridge** the two sessions for that copy, and A's machine sends
  them to B's through the server — never the technician's line or disk. **The hub's yes is the ask sent back**; only then does the
  console make a key for that copy and hand it to each machine **inside its own sealed session** (`BridgeKeyMsg` is NEVER on the
  clear list; `SessionLink.SendSealed`). `Shared/BridgeSeal.cs` (the bridge id clear for routing, nothing else), `HostEngine.Bridge.cs`
  (receiver empties its clipboard first and picks its OWN staging; sender within 8 MB of the receiver's acks so A's picture never
  waits on B), **`TenantHub.Bridge.cs` — `BridgeProblem` is THE rule** (same technician in both sessions now, own company only,
  both sealed, both hosts able, under 5 GB), `SessionForm.Bridge.cs`. The ticket names the other machine; the person is told
  *"another computer"*. Older host / refusal / silent server → through the PC by itself; failed half-way → OFFERED. Free version
  too. `bridgewire` 34 (two REAL hosts), `twosessions` 53. **`Publish-Platform.ps1` (no migration), console re-download, FULL ship
  and client reinstall**; `TEST-3.6.md` section DA, 698–706.
- **A WEBSITE CHAT CAN BE HANDED OVER (2026-10-15, §145). Walked in the browser; NOT on hardware.** Clint, putting the bubble on
  his own site: can a chat be transferred? *Hand over…* in the console's Website chat window and the portal's Chat corner offers
  whoever is signed in now. **Every rule is `WebChatService.HandOverAsync`**: the person answering, a supervisor/owner
  (`Permissions.CanHandOverAnyWebChat`), or anybody while nobody has answered; only to somebody of THIS company signed in now
  (`StaffPresence`); **one conditional update on the answerer it read**, so two at once cannot both win. The visitor is told by a
  **note — a `WebChatLine` with no name** (`WebChatLineInfo.Note`). The dings move because they key on `AnsweredBy`; the person
  it is handed TO is brought it and dinged (`WebChatInfo.HandedOverBy`, never stored). `webchatcheck` 127 on both databases
  (122 / 119 / 118 with the three guards out; `Testing:WebChatHandOverGapMs` makes the race meet), `webchatprobe` 46,
  `soundcheck` 60. **`Publish-Platform.ps1` (no migration), console re-download**; `TEST-3.6.md` section DB, 707–713.
- **PILOT COMPANIES, AND AN OWNER LOGIN THAT ONLY OVERSEES (2026-10-15, §146). NOT on hardware.** Clint: the two pilot IT
  companies get **five seats free for ever** and pay R300 for each extra one. **Owner → Pilots** (or `pilot-link "<company>"
  [email]`) makes a link that works once, 30 days, SHA-256 only; it opens **`/pilot/{token}`** (outside the holding page, works
  with signups off): the pilot terms (`Terms/pilot.txt` ahead of section 1), the company's own details, **Active at once — no
  trial, no approval, no card**, the link spent by a conditional update in the same save. `Tenant.Pilot` makes the package R0 in
  `Pricing.BillFor`; extra seats are agreed by the owner as always and **INVOICED, never carded** (PayFast cannot hold R0 — do not
  "add the card route"). Every rule `Services/PilotService.cs`. **And the owner-only login no longer works tickets** (Clint: it
  oversees and comments): **`Permissions.CanWorkTickets`** (any paid box) gates every ticket-work question, and
  **`Pages/Portal/TicketWorkAttribute` is an ALLOW-LIST** on the ticket pages — a new handler there is refused to an owner-only
  login until named in it. Terms version 2026-10-15 (`landing-legal` re-run). `rolecheck` 105 (92 with the lock off),
  `pilotcheck` 38 on both databases (37 / 35 with the two guards out). **Before deploying, tick Supervisor beside any Anywhere I.T
  login that is Owner only and works tickets.** `Publish-Platform.ps1` (migration `PilotCompanies`); `TEST-3.6.md` section DC,
  714–722.
- **CODE SIGNING WORKS (2026-10-15).** Sectigo OV certificate issued 14 October, **valid to 18 November
  2027** (the term's second certificate comes free after), on the YubiKey in slot 9a; a test file signed and
  timestamped as *The Remote Support Program (Pty) Ltd*. **`tools\Install-SigningCert.ps1`** loads and proves
  a certificate (run it again for the second one). **Our key is ECC: a signing PC needs Yubico's Smart Card
  Minidriver** — Windows' own PIV driver says "Missing stored keyset". **Never check a signature on a copy of
  a Windows file with `Get-AuthenticodeSignature`** — its catalog answers "Microsoft". **`Publish-Platform.ps1`
  now REFUSES to upload unless all 12 apps it serves are signed by us** (builds with `-RequireSigning`; plug the
  YubiKey in; `-Unsigned` is the deliberate way past). **The first signed deploy went live the same day — all 12
  signed and timestamped.** `docs/CODE-SIGNING.md`, "SIGNING PROVEN".
- **KEYS ARE NEVER PRINTED, AND THE OWNER KEY OPENS NOTHING ALONE (2026-10-15).** A deploy log with both keys was
  pasted into a chat. `go-live.sh`'s summary now says WHERE each key is (`sudo cat /etc/rsp-owner-key`), never the
  key; **`deploy\Rotate-Keys.ps1 -Server <ip>`** replaces the owner and server keys (sent as base64 — PowerShell 5.1
  mangles double quotes in an ssh argument), putting the old ones back if the platform does not answer; tested 18 of
  18 on stand-ins. And `/api/admin/status` + `/api/admin/tenants` took the key ALONE (no code) and listed every
  company — unused, so REMOVED; `securitycheck` 78 (76 with them back). **Deployed and the keys ROTATED on live
  2026-10-15** — the keys in that pasted log are dead.
- **AUTO-UPDATE IS BRIEFED, `docs/NEXT-SESSION-AUTOUPDATE.md` (2026-10-16), roadmap 4.6's binary half.** Clint:
  every app — tray app, host, agent, health check, console — keeps itself current. **Decided: silent on customer
  machines; the rollout is ours alone** (rings: Anywhere IT → pilots → everyone, a Pause; companies cannot hold
  back). The agent installs only files **signed by The Remote Support Program (Pty) Ltd AND newer than what is
  there** (a compromised server must not push an old signed build), **with no test mode in the verifier**; waits
  for idle; swaps by renaming aside; rolls back if the new agent has not connected in 5 minutes. **Every app says
  1.0.0 today — real version numbers (stage A) come first.** Every machine needs ONE last reinstall to get the
  updater. Stages A–F. **Stage A, REAL VERSION NUMBERS, IS BUILT (2026-10-16, §147). NOT on hardware.** A build is the
  moment it was made in UTC, `2026.10.16.1432` (`Shared/AppVersion.cs`; `1.0.0.0` = any other build and every install
  before today, shown *1.0 (old)*). `tools\BuildVersion.ps1` stamps build.ps1 (`-Version` to choose one), ship-build and
  Pack-ClientExe — **pass all of `VersionArgs`, never `-p:Version` alone: `Directory.Build.props` pins AssemblyVersion and
  FileVersion.** The hub keeps `Device.AgentVersion`/`ClientVersion`; console column and title, portal Devices (amber when
  older than the server's apps), `/owner` server cards. `versioncheck` 27 on both databases (20 with the storing out).
  **`Publish-Platform.ps1` (migration `AppVersions`), console re-download**; `TEST-3.6.md` section DD, 723–727.
  **STAGES B–F ARE BUILT THE SAME DAY (§148). NOT on hardware.** Clint: *"lets finish all the stages before i deploy."*
  (B) `Shared/UpdateTrust`: WinVerifyTrust + revocation, signer read from the verification (scripts too), O= and C= as
  fields, stamped and NEWER — **no test mode** (`updatetrust` 8). (C) The release is the packed customer app + the console,
  `/update/client.exe` `/update/console.exe`; rings our own (NotBilled) → pilots → everybody on **`/owner/updates`** (Full or
  Owner, any server via `ControlServers.Dispatch`), Pause, **a rollback pauses it by itself**; `UpdateSweep` offers a few a
  minute to agents that said **`DeviceCan.Update`**; a machine that rolled back or refused is never offered that version again
  (`updatewire` 33, both databases). (D) `UpdateRunner` → `--apply-update` → `UpdateSwap` (rename aside, only the release's
  own files, written down first) → kept only if the new agent says it is healthy within 5 minutes; **only the real service
  says DeviceCan.Update**; the tray app restarts into a newer copy when idle (`updateapply` 36). (E) An installed console
  updates before its sign-in window, `--install-update`, rolls back if the new copy never gets there; *Update ready* in the
  header (`consoleupdate` 14). (F) **Every machine installed before today needs ONE update by hand: Scripts → Update this
  machine** — not a reinstall, so it keeps its identity (`updatebyhand` 9). Migration `AutoUpdate`. **Deploy: a SIGNED
  `Publish-Platform.ps1`, re-download every console once, update every machine by hand once; from the next deploy on they
  update themselves.** `TEST-3.6.md` section DE, 728–739; **737 (the rollback) matters most.**
  **ON HARDWARE 2026-10-19 (§152): the AGENTS update by themselves — 2N6FVGT took four releases, ~20 s each, no rollback — but
  the TRAY APP NEVER RESTARTED into one.** Windows answers `FileVersionInfo` from the module loaded at that path, and the renamed
  running tray app still goes by its original path, so it read the new file as its OWN old version for ever. Fixed:
  **`UpdateTrust.FileVersionOnDisk`** reads the file's bytes — **never `FileVersion` on a path the asking program started from.**
  `tools/trayversion` 14 (13 with `--old`). **Every running tray app in the field is the old code and cannot notice: after the
  deploy, each machine needs ONE sign-out or restart**, then it follows by itself. The console's Version column is the OLDER of
  agent and tray app. `tools/updatestate.ps1` (new, read-only; runs in Scripts — paste into the SMALL box, the big one is output)
  says why a machine is behind. Signed `Publish-Platform.ps1`, no migration; `TEST-3.6.md` section DI, 761–764.
- **THE ALLOW PROMPT COMES TO THE FRONT (2026-10-16, §149). NOT on hardware.** Clint: it opened behind other windows.
  Measured: **Windows takes `TopMost` OFF a window a background program shows** while the person is in another program —
  gone at 0 ms. `UI/ComeForward.cs`: `Now` joins the foreground's input, takes the foreground, re-marks it topmost (flashes the
  taskbar if refused); `StayOnTop` each countdown tick, never taking the keyboard back. `tools/consentfront` 15 of 15 (8 of 12
  old, 14 with `StayOnTop` out). **Not proved as SYSTEM** — no harness can. Signed `Publish-Platform.ps1`; the machine needs
  the new host. `TEST-3.6.md` section DF, 740–743.
- **THE NAME GIVEN AT A SUPPORT REQUEST NAMES THE MACHINE (2026-10-18, §150). Walked in the browser; NOT on hardware.** Clint:
  machines were not being linked, so the lists filled with DESKTOP-names. Now an UNLINKED machine's Who is the name last typed
  into Request Support (`Device.RequestedBy`) — **the latest wins**, shown **grey italic** (only what they typed) in the console
  and the portal, until anybody links it. **`Shared/OwnerNames` is the ONE rule** (contact, client, given name, Windows sign-in);
  `DeviceInfo.OwnerName` and `ContactService.OwnerName` both ask it. The Link window and Devices page **suggest** people on file by
  that name and offer **Add {name} as a new contact** at a chosen client (`DeviceLinkMsg.NewContactName`, through `CreateAsync`, so
  never a duplicate). The hub waits for the stored record before keeping it (a first install asks at once). The migration
  backfills from past support-request tickets. `requestedname` 43 on both databases (33 with the wait out), `dashtabs` 145.
  **Portal `<em>` is upright** — the mark is `.requested-by` in site.css. Found: **BUGS #13**, a message straight behind a hello
  is dropped (not fixed). **Signed `Publish-Platform.ps1` (migration `RequestedByName`), console update; no client reinstall.**
  `TEST-3.6.md` section DG, 744–750.
- **TICKETS BY EMAIL: SIGN IN WITH MICROSOFT (2026-10-19, §151). NOT on hardware, NOT against a real 365 mailbox.** The third
  way in, beside a password and forwarding (kept as the fallback): ONE multi-tenant Entra app of ours, **delegated
  IMAP.AccessAsUser.All + SMTP.Send only — never Mail.Read**; the mailbox read is ALWAYS the support address (a shared one
  through a person with Full Access); IMAP and SMTP by XOAUTH2; the refresh token Data Protection, rotated and stored.
  **Microsoft refusing THEIR sign-in deletes the token, stops checking and is not asked again** (`TicketIntake.MicrosoftTokenAsync`
  — §79's tried-once rule), said on the page and the status line with *Sign in with Microsoft again*; refusing OUR app
  (`invalid_client`) keeps their token and says *ours to fix*. The way back (`/portal/mailintake/microsoft`) is bound by
  state + `rsp_ms` cookie + PKCE + nonce (`MicrosoftSignIn.CheckReturn`); codes, not words, in the address. Disconnect, or
  saving another way in, deletes the token. Every Microsoft call is `Services/MicrosoftSignIn.cs`. `m365check` 103 on both
  databases (93 / 96 with the two guards out). **Signed `Publish-Platform.ps1` (migration `MicrosoftSignIn`), THEN Clint's
  one-time steps in `docs/MICROSOFT-365.md`** — register the app, `deploy\Set-MicrosoftApp.ps1` for the secret (it runs out:
  diary it), publisher verification. `TEST-3.6.md` section DH, 751–760.
- **THE FREE VERSION IS BACK BESIDE BASIC; BASIC CONNECTS ATTENDED TOO (2026-10-25 evening, §163) — SUPERSEDES §161's "no free
  version". NOT on hardware.** Clint: Free = *"one connection for unattended access with password as it was previously"*; Basic =
  *"attended and unattended, but still only a maximum of 20 machines"*; Free → Basic or Full, Basic → Full or down to Free. **Stored
  `"free"` (Free) and `"basic"` (Basic)**: `FreePlan.Is` = either (the shared one-person rules), `IsFree` / `IsBasic` / `Attended`,
  **`MachinesFor(plan, paid)`** (Free 1, Basic 3..20) — never compare the string. Basic attended goes ONLY through the agent asking
  (`AskConsent`), never a tray app; no Request Support, no Get help now. Basic's installer may leave the password blank
  (`FreeSetup.SetAttendedOnlyAsync`). The console lists everything in one list (`DeviceTab.All`). `/portal/upgrade?to=basic|full|free`;
  **down is `CompanyBilling.DowngradeToFreeAsync`** (one computer at most, card cancelled, no refund). Old free accounts stay Free and keep
  their computers; **Give notice is gone**. Migration **`FreeVersionBack`** (data). `basiccheck` 71, `freecheck` 53, `freeinstall` 25.
  **Signed `Publish-Platform.ps1` (migration `FreeVersionBack`, both databases)**; `TEST-3.6.md` section DV, 862–873.
- **SORT THE TICKET LIST BY ANY COLUMN; A DUE DATE ON EVERY TICKET (2026-10-26, §165). NOT on hardware.** Clint, from SuperOps'
  list: every heading sorts (click, again to turn round, ▲/▼, remembered) in the portal and the console's queue; the Sort box is
  gone; **Group by customer is a tick**; Priority, Last updated (*2 min ago*) and Due columns; orange *Customer replied* while the
  customer's reply is the newest entry (`TicketService.CustomerRepliedAsync`, filled for every `TicketChangedMsg` in
  `BroadcastTechs`). **`Shared/TicketSort.cs` is the one rule** (`"col:asc|desc"`; the four old keys keep their meaning);
  `TicketService.Sorted` sorts on the server, **blanks last both ways**. **`Ticket.DueDate`** — a day, kept APART from `DueOn` (a
  schedule's day); every rule `Services/TicketDue.cs` (never a past day; overdue = past, on the bench, open). **A console sends the
  DAY at midnight marked UTC.** The console's queue takes only the width the ticket side can spare. `ticketsortcheck` 61 on both
  databases (57 with two guards out), `ticketwindowprobe` 259, `ticketformprobe` 140. **Signed `Publish-Platform.ps1` (migration
  `TicketDueDate`, both databases)**; `TEST-3.6.md` section DX, 885–896. Saved "ticket views" are left for later.
- **LATEST ACTIVITY (2026-10-26, §164). NOT on hardware.** Clint (a technician asked): every update on the tickets as it is
  posted, newest first; All or My tickets; by client and by who posted it; and *"i assign a ticket to Ryan, it shows up in his
  recent activity?"* — yes, *assigned it to you*. Portal tab beside Tickets (every role) + a dashboard card; console button
  beside Tickets (never red). **The entries ARE the tickets' stories and the tickets — nothing extra is written.** Every rule is
  `Services/TicketActivityService.cs`; **who sees what is `TicketService.VisibleTo`**; Mine = on it, worked on it, opened it,
  @mentioned. **`TicketNote.Automatic`** (not `IsSystem`) keeps a session's lines and each ticket's own "opened" line out —
  **a new session-style writer must pass `AddNoteAsync(..., automatic: true)`**. The count: `Technician.ActivitySeenUtc` and
  `ActivityMine`, told live by **`PlatformDb.TicketActivitySaved`** (every save that adds a note or ticket) → `Changed`, once per
  700 ms; console `ActivityCountMsg`, portal `event: activity` (chat.js). **Refetch only on a count above nought** or a look
  answers itself for ever. A line opens `/portal/t/{n}#note-{id}`. `activitycheck` 83 on both databases (77 / 81 of 82 with two
  guards out). **Signed `Publish-Platform.ps1` (migration `LatestActivity`, both databases)**; `TEST-3.6.md` section DW, 874–884.
- **NO FREE VERSION: THE BASIC VERSION FOR REMOTE WORKERS, R50 A MONTH, THREE COMPUTERS (2026-10-25, §161). NOT on hardware.**
  Clint: *"no longer have a free version ... R50 per month ... maximum amount of machines to 3. All the usual rules ... would still
  apply."* **The code keeps `FreePlan` and the stored plan `"free"` on purpose — only the words changed**: `FreePlan.FullName` /
  `ShortName` / `TheName`; never write "free version" where a person reads it (`sitecheck` fails). `FreePlan.MaxMachines` 3;
  `Pricing.Region.Basic` R50/$5/£5/€5, `Bill.Basic`; it pays by card like the full version (signup → PayFast, 14-day trial, sync, late
  payment — the old free-plan skips are gone); Billing is on `FreePortal`; upgrading from a card is no new trial. **The free accounts
  from before get 30 days' notice — Owner → Basic version → Give notice (`FreeCompanies.GiveNoticeAsync`) puts them on the trial
  machinery and EMAILS them; nothing goes by itself.** It never counts against the five places (Clint). Terms `Terms/basic.txt`.
  **And `/remote-workers`**, a keyword-rich page for remote working (keywords meta, FAQ schema), in `SitePages.Public` and go-live.sh's
  `@holding` list. `basiccheck` (new), `sitecheck` 103. **Signed `Publish-Platform.ps1`, no migration**; `TEST-3.6.md` section DT,
  845–855. **Same day, §162: MORE COMPUTERS AT R10 / $1 / £1 / €1 EACH, set by the owner on Billing, no approval from us, up to 20**
  (Clint: *"cap it at 20"*). `Tenant.BasicMachines` (0 = three); **`FreePlan.MachinesFor` is THE number** — never `MaxMachines` alone
  for a limit now (it is the three in the price); `TenantService.SetBasicMachinesAsync` (a rise names the exact figure; never below
  what is installed), then `TenantHub.PlanChangedAsync` so the hub and consoles take it at once. Migration `BasicExtraMachines`, both
  databases; `TEST-3.6.md` section DU, 856–861.
- **FIVE SEATS; MORE ON REQUEST (2026-10-25, §160) — SUPERSEDES §153's "NO MORE". NOT on hardware.** Clint: a company can ask, and
  *"if I decide it's fine ... allow more seats"*, charged through PayFast. An owner asks on Billing (`TenantService.AskForSeatsAsync`);
  we Allow up to N or Decline on **Owner → Approvals** (or *Allow up to* on All companies) — `AdminCompanies.AllowSeatsAsync` /
  `DeclineSeatsAsync`, actions `allow-seats` / `decline-seats`. **`Tenant.MostSeats` (0 = the package) and `Pricing.MostSeatsFor` is
  THE limit**; `SeatsAllowed` is still the package. Each seat past it is R300/$20/£20/€20, **agreed by the owner first** — `RiseIf`,
  `PriceRiseAsync` and the People page's Now/After are back from before `b2c6fe0`; PayFast follows by `OwedCents` as before; a pilot's
  extras are invoiced. Taking it back removes nobody. Terms version 2026-10-25, `landing-legal` re-run; the website says *"five seats;
  more on request"*. `seatcheck` 99 (96 / 97 with two guards out). **Signed `Publish-Platform.ps1` (migration `MoreSeatsOnRequest`,
  both databases)** only; `TEST-3.6.md` section DS, 836–844.
- **FIVE SEATS AND NO MORE (superseded on seats by §160); ENDING A PILOT; FIVE COMPANIES AT A TIME (2026-10-22, §153). NOT on hardware.** Clint: *"a 5
  license only product, with no option to have extra seats ... small I.T companies only"*, a way to revoke the two idle pilots, and
  *"allow 5 companies in at a time"*. **§153 supersedes §83's seat price and §116's ten.** `Pricing.MaxSeats` = 5, no extra-seat
  price, **`RiseIf`/`PriceRiseAsync`/the People page's price question are GONE** — a sixth seat is refused to everybody;
  **`Pricing.Limited`: the company not billed (Anywhere IT) is never limited**; `Pricing.WhyFive` is the one wording. **A pilot is
  ended by NOTICE** (Owner → Pilots, 30 days offered — the terms they accepted give 30 days' notice): `close` (suspended, then
  Closed 30 days later by `PilotSweep`) or `pay` (a 14-day trial at R500); withdrawable until the day. **`OpenPlaces`** (Owner →
  Approvals, `PlatformSettings.OpenPlaces` = 5, 0 = no limit): billed, non-pilot, non-free companies Active/Trial/Suspended or
  Pending < 3 days, plus live offers; full → the waiting list; *Offer a place* = a once-only 7-day link spent with the company.
  Terms version 2026-10-22, `landing-legal` re-run. `seatcheck` 54, `pilotcheck` 57, `placescheck` 26 (both databases); all 85 database harnesses on SQLite. **Signed
  `Publish-Platform.ps1` (migration `PilotEndingAndOpenPlaces`, both databases)** only. `TEST-3.6.md` section DL, 773–782.
  **The real site and LIVE PayFast go on 1 November 2026** — until then `Payfast:Sandbox` and the holding page stay.
  **`-RealSite` now also OPENS SIGNUPS and puts our chat bubble on the site (2026-10-27, §169)** — Anywhere IT's chat, saying *The
  Remote Support Program* in our purple there only (`Services/SiteChat.cs`, honoured only for `Platform:SiteChatSlug`'s company).
  Without `-RealSite` both close again, so **from 1 November every deploy needs `-RealSite`**. `webchatcheck` 135; `TEST-3.6.md` DZ, 912–917.
  **And the site is WATCHED (2026-10-27, §170)**: the office PC checks `/health` every 2 minutes and emails Clint once down, once
  back (two misses in a row first), plus the certificate and the backup copy — `deploy\monitor\README.md`, `Install-SiteWatch.ps1`
  on the office PC (NOT installed yet). `tools\Test-SiteWatch.ps1` 24. `TEST-3.6.md` EA, 918–921.
  **The site speaks beyond IT companies and carries the logo (2026-10-27, §171)**: it leads with *businesses that look after other
  people's computers and jobs* (IT companies, software/POS suppliers, in-house IT, repair shops, call-out businesses), uses the
  words South Africa searches (*job cards*, *remote access*, *live chat*, AnyDesk first — `docs/WEBSITE.md`, from Google Trends),
  and shows `docs/logo` in every page's header (`wwwroot/img`, light/dark by `data-theme`) and the holding page (inline). `sitecheck` 105.
- **A COMPANY TAKES ITS DATA WITH IT (2026-10-22, §154, roadmap 4.9). NOT on hardware.** Portal → **Export** (owner and supervisors,
  `Permissions.CanExportCompany`) makes one zip in the BACKGROUND (`CompanyExportSweep`, kept 7 days, a newer one replaces it):
  `clients.xlsx` whose first sheet is EXACTLY Import clients' columns (it goes straight back in — `ImportRows` says how), the tickets
  with their story (`story.txt` per ticket, never cut), quote lines, photos and files, and the knowledge base as `.html` + `.txt` with
  its pictures; every cell text; **never a device password**; recordings only when ticked. **A suspended or closed company is exported
  BY US** (Owner → All companies → *Export their data*, action `export`): its owners are emailed `/export/{token}`, which opens a page
  with a button — mail filters open links. Every rule `Services/CompanyExport.cs`; `Spreadsheet.Workbook` writes row by row.
  `deploy/rsp-backup.sh` leaves `exports` out. `exportcheck` 92 on both databases (88 with three guards out). **Signed
  `Publish-Platform.ps1` (migration `CompanyExports`, both databases)** only. `TEST-3.6.md` section DM, 783–791.
- **A TECHNICIAN ASKS FOR OUTSIDE HELP (2026-10-22, §155, roadmap 3.9.3). NOT on hardware.** Clint: *"sometimes it happens that a
  tech needs the outside assistance rather than the client."* In a session, **Tools → Get outside help…** makes a code for THAT
  session and a message with both ways in to Copy / WhatsApp / Email — **the browser link carries the code after `#`** (in no server
  log; `guestview.js` fills the box). Whoever types it (another company, a guest, a browser) waits for **the technician who made it**,
  who lets them in, files on a tick; **the person at the machine is TOLD (`OutsiderJoinedMsg`), never asked** — Clint's answer. **The
  session is the company's**: the outsider's Leave, Remove (any technician; the red pop-up says *Remove*) or lost connection takes only
  them out; **nobody of the company left → they go at once**; the company's Leave with only them left ends it. Any technician IN the
  session; **the machine's outside-help switch is the customer's and is NOT asked** — the company's own is
  `Tenant.TechnicianInvitesAllowed`, ON by default, on Portal → Outside help. Every rule `Hub/TenantHub.Invite.cs`; **`Session.InvitedBy`
  is what every old visitor path asks** (a late Leave from a removed visitor must not end the session — `NoLongerTheVisitor`). On the
  console's signed-in socket, never the sealed session socket. `helpcheck` 77 on both databases (66 with three guards out),
  `fitprobe` 416 (found three layout faults). **Signed `Publish-Platform.ps1` (migration `TechnicianOutsideHelp`, both databases),
  console re-download; a FULL ship and client reinstall for the customer's notice. Same day: the CUSTOMER's code window sends too — Copy the message / WhatsApp / Email, the same message (`helpcheck` 79, `fitprobe` 426).** `TEST-3.6.md` section DN, 792–804.
- **THE INSTALLER ASKS WHO THE COMPUTER IS FOR (2026-10-23, §156). NOT on hardware.** Clint: name, surname and email at the install
  become the machine's name (§150's, grey italic) and Request Support's; linking them to a client files them as a contact. All eight
  recommendations: required in the window (a scripted install asks nothing), **the latest name still wins**, Request Support has an
  optional email box, linking adds them **by email first, then name** (a blank email filled, never overwritten) with a tick ticked by
  default to say no (shared PC), an email never links anything by itself, new installs only, not free. **`Shared/GivenPerson.cs` is
  the one rule**; the window writes `person.json` beside the installer (Install-Client.ps1 copies it to Program Files); every hello
  says it — **the server keeps it ONCE per id (`Device.InstalledForId`), or every reconnect would put it back over a later request's
  name**; the email always belongs to its name (`ContactService.Given`) and linking takes it from the machine's record, never the
  caller. `installedfor` 48 on both databases (46 / 47 with the two guards out), `oneexecheck` 39, `callprobe` 77, `dashtabs` 155.
  **Signed `Publish-Platform.ps1` (migration `InstalledForPerson`, both databases)** only; `TEST-3.6.md` section DO, 805–812.
- **A FREE ACCOUNT FROM THE OWNER PAGES (2026-10-23, §157). NOT on hardware.** Clint: a button to create the client and send the
  links, and *"an option to cc the tech in ... so that he can help her set it up."* **Owner → Free version** (Manager or above):
  name and email (company optional) → Active on the free plan at once, Owner + Technician with no password; ONE email from us with
  the set-up link, `/download/{slug}` and `/tech/{slug}`, the technician in a **visible Cc and Reply-To** (they get the set-up link
  too — on purpose). *Send the links again* per row (a new link while not set up; the downloads alone after). **The free terms are
  accepted on the set-up page before the password** (no `TermsAcceptance` yet → shown, ticked, kept word for word) — §84's rule,
  kept. Every rule `Services/FreeCompanies.cs`. `freeaccount` 40 on both databases (33 with two guards out). **Signed
  `Publish-Platform.ps1`, no migration**; `TEST-3.6.md` section DP, 813–819.
- **THE OUTSIDE SECURITY REVIEW (Daniel, 19 Oct), CHECKED AND FIXED (2026-10-23, §158). NOT on hardware.** No way into another
  company's data was found. **F1's per-address limit, F2's `/forgot` limit, F11 (the holding page) and F12 (the runtime: self-contained
  10.0.13, read it in the build's deps.json — never the server's `dotnet --list-runtimes`) were ALREADY right; do not "fix" them.**
  Fixed: **a machine with the person's trust token is counted apart by `SignInGuard`** (`k:`), so a stranger's five wrong passwords
  never lock it out (portal, console and platform admins); the holding page's headers in `go-live.sh`; a full policy
  **report-only** (`Services/CspReports.cs`, reports to `/api/csp-report`, logged as `CSP report`) — 45 pages still have inline
  script, so it is not enforced yet; a missing ticket or article is a 404; **a stored picture must hold together**
  (`TicketPhotoService.WholeImageType`, `QuoteSignatures.WholeImage`) — **a harness storing a photo uses `tools/Common/TestImages.cs`**;
  `/health` is `{"ok":true}`; `/.well-known/security.txt`. Left: HSTS preload (mail subdomains), ids in addresses. Still to ask him:
  cross-company access, with a second company. **Signed `Publish-Platform.ps1`, no migration**; `TEST-3.6.md` section DQ, 820–826.
- **PORTAL ADDRESSES NAME THINGS BY THE COMPANY'S OWN NUMBER, NOT THE DATABASE ID (2026-10-23, §159, the review's F8). NOT on
  hardware.** One id sequence for every company showed the platform's volume (`/portal/tickets/129` for ticket #2). Now
  **`/portal/t/{n}` `/portal/c/{n}` `/portal/a/{n}` (`/edit`) `/portal/r/{n}`**, queries **`?c=` `?t=`**, and stored files by a
  random **`Token`** on the row: **`/portal/p|f|i|v/{token}`** (photo, comment file, article picture, recording). Every address
  is **`Services/PortalAddresses.cs`** — one lookup per kind, **always with the company in the same query**. `Client`, `KbArticle`
  and `RecurringTask` have a `Number` (unique per company), **given on save by `PlatformDb`** (`PlatformDb.Numbers.cs`) — so a
  harness seeding rows straight into a table still works. **The old id addresses keep working for good**: extra page routes with
  `SuppressLinkGeneration` (never `AddPageRoute` — it makes the OLD route every link's) send a company's own id on (302; 307 for a
  post) and are the same 404 as a missing number for anybody else (`PortalPage.AddressedIdAsync`); `/portal/enter` translates an
  old console's shapes; the console sends the new ones (additive protocol fields). **A ticket/client page's handlers take the id
  from the NUMBER in the address — the hidden `id` fields are gone and a posted id is never read.** Out of scope on purpose:
  `/api/portal/…` JSON and the console protocol, and what comes from them (the client picker's hidden values). `addresscheck`
  122 (107 / 118 / 109 with its three guards out), both databases. **Signed `Publish-Platform.ps1` (migration
  `PerCompanyAddresses`, both databases, backfills)**, then the console updates itself; `TEST-3.6.md` section DR, 827–835.
- **THE CONTROL DASHBOARD IS BRIEFED, `docs/NEXT-SESSION-CONTROL.md` (2026-10-13)** — admins, every server and its
  capacity, automatic placement, moving a company. Clint's nine answers are all *"go with your recommendations"*; do not
  re-ask them. Stages A–E. **Stage A, PLATFORM ADMINS, IS BUILT (§132). NOT on hardware.** Named admins at `/owner`
  (Viewer / Manager / Full / Owner — **every rule `Services/PlatformAdmins.cs`; an Owner is never removed from the pages**),
  set up from a mailed link, 7-day trusted browser, a log of everything by name; the admin key stays as the first and last
  way in. **Every change to a company goes through `Services/AdminCompanies.cs`** (stage B's remote servers will too).
  Found: **Suspended never took effect on anything already connected** — now `TenantHub.TurnAway` + `AuthService.SignOutCompany`,
  and the owners are emailed both ways. `admincheck` 75 on both databases. **`Publish-Platform.ps1` (migration
  `PlatformAdmins`)**, then add yourself as Owner with the admin key. `TEST-3.6.md` section CO, 617–624.
  **Stage B, THE DASHBOARD, IS BUILT (§133). NOT on hardware.** `/owner` is a card per server (specs, five meters, amber 70 /
  red 85 from §126's measurement — **`ServerCapacity` is the one rule**) and every company's people, technicians and
  machines. Other servers are read with THEIR `Platform:ServerKey` (go-live makes it) at `/api/control/*`, never a password;
  `ControlServers.ActAsync` changes a company wherever it lives, through ITS `AdminCompanies`. `ControlSweep` emails Full
  admins and Owners once per level. `controlcheck` 50 on both databases. Same deploy, migration `ControlServers`; section CP.
- **THE PLATFORM IS LIVE (2026-10-01)** at `https://theremotesupportprogram.com` on xneelo Cloud,
  `154.65.110.136`, login `ubuntu` (key only), deployed by `deploy\Publish-Platform.ps1 -Server
  154.65.110.136` — `docs/GO-LIVE.md` has the first-live-test steps at the top. Found on the way:
  Caddy's apt repository signs with an expired key (installed from its GitHub release now,
  checksum-verified); the downloadable .exe fell back on the LAN address; `approve` would have made
  our own company a 14-day trial; the product mails through `mail.`/`smtp.theremotesupportprogram.com`
  on **587**, never 465. Measured TLS: 1.3, X25519MLKEM768, AES-128-GCM.
  **The public front is the HOLDING PAGE, on purpose (2026-10-03)**: going live had silently put the
  unfinished home/features/pricing pages up. `Publish-Platform.ps1` now packs `landing/index.html` and
  Caddy serves it for those six paths only; everything else reaches the platform. **`-RealSite`** when
  the real site is ready — never before Clint says so. `GO-LIVE.md`, "Going live afterwards".
- **PEOPLE CHOOSE THEIR OWN PASSWORDS (2026-10-01, §73). NOT on hardware.** Add a person = name,
  email, boxes; they are mailed an invitation (company's name, support Reply-To, no owner copy, 72 h,
  console link for a seat) → `/account/setup` → password → QR → recovery codes. Send reset link,
  Forgot password (same answer for anyone; 3/hour/address), My account (current + code). **A link is
  spent by the POST, never the GET** (mail filters open links). Every rule is `AccountLinks`.
  `invite` on the command line for a company's first owner. `go-live.sh` writes
  `appsettings.live.json` so the CLI knows the public address. `invitecheck` 51 (49 with the owner
  copy back and no stamp move), with a real SMTP server in the harness. **Migration
  `AccountInvitations`; platform deploy and `-OnlyTechnician`**; `TEST-3.6.md` section AN, 269–276.
- **CLIENTS IMPORT FROM ANOTHER PROGRAM (2026-09-28, §52). NOT on hardware.** Clients → Import
  clients: a template (`.xlsx`, Phone column as text), or your own `.xlsx`/`.csv` with Client,
  Contact person, Phone, Email, Address, Notes; a preview; then Import. Owner, supervisor, office
  administrator — `Permissions.CanImportClients`, asked by the service too. **Never overwrites**:
  a client on file only has its blanks filled, so the same file twice adds nothing. Repairs
  garbled letters and (South Africa only) lost leading zeros, and lists every repair. An address on
  3+ clients is ticked to be left out — Clint's `jason@anywhereit.co.za` on 157. `importcheck` 115 (with both of his files). **Phone and Email hold ONE value each** — for texting and emailing on a stage change — and the rest goes to Notes word for word;
  and Clint's real 878-row list imported through the real page on a throwaway platform. **Platform
  restart only, no migration**; `TEST-3.6.md` section T.
- **JOB TYPES ARE BRIEFED, `docs/NEXT-SESSION-JOBTYPES.md` (2026-10-27).** Clint: a company chooses its
  categories, and switches off steps inside them, *"without breaking the flow"*. All ten answers were
  *"lets go with your suggestions"*; do not re-ask them. Owner and supervisors, Portal → Job types, everything
  on by default. **Remote support and General are locked on.** Photos are two RULES (in, out), not stages.
  **A ticket keeps the switches as they were when it was opened** (`Ticket.RouteOff`), and categories are read
  live. **`JobTypes.Problem` is the one judge of a route**, and `jobtypecheck` walks every combination.
  **STAGE A, THE RULE AND THE SERVICE, IS BUILT (2026-10-27, §166). NOT on hardware.** Everything is
  `Services/JobTypes.cs`; `TicketService.RouteSteps` is what `ActionsFor`, `ActAsync` and the judge all ask, and
  `JobTypes.ToOf` is where a skipped step sends a job (read by `TargetOf`). **Payment before ordering off needs Parts
  fitted → To be invoiced**, or a quoted job closes unbilled — the judge refuses it. Two new stages, `order-parts` and
  `parts-on-order`. `jobtypecheck` 65 (59 with that reroute out), `jobtypewire` 48 on both databases (43 with two
  guards out). Signed `Publish-Platform.ps1` (migration `JobTypes`, both databases).
  **STAGE B, THE PAGE, IS BUILT THE SAME DAY (§167). Walked in the browser; NOT on hardware.** Portal → Job types
  (`/portal/jobtypes`): the cards are `JobTypes.Cards`, the line under each is `JobTypes.Path`, both judged live by the
  server (`?handler=Path`) — the browser has no copy of the routes. Everybody reads it; a supervisor or the owner saves.
  **An OWNER's first dashboard visit goes there once** (`?first=1`, Keep everything) — **a harness that opens `/portal`
  as an owner must save Job types for its company first** (four did not). The portal's New ticket, ticket page and Edit
  follow the switches. `jobtypecheck` 74. `TEST-3.6.md` section DY, 897–911.
  **STAGE C, THE CONSOLE, IS BUILT THE SAME DAY (§168). NOT on hardware.** `StateMsg.CategoriesOff` and `BookingPhotos`
  (additive; an older server reads as everything on and 3), kept on `TechLink`; New ticket's own `_offered` list and
  photo count, the ticket window's words from the TICKET's counts, Edit's categories. **The hub keeps its own copy of the
  switches and only `JobTypesChangedAsync` (called by the page's save) refreshes it** — anything else that ever writes
  `JobTypesOff` must call it. `jobtypepage` 31 on both databases (29 without the call), `ticketformprobe` 145,
  `ticketwindowprobe` 267. Signed `Publish-Platform.ps1`; the console updates itself. **D, the papers, is done too (§168): JOB TYPES IS COMPLETE** — the handover pack (F2b) and the guide's page 16.
- **REMOVING A CLIENT (2026-10-27, §172). Walked in the browser; NOT on hardware.** Clint: a staff member made a misspelt
  duplicate. **Removing is MOVING**: *Remove this client* at the bottom of the client page — choose the right client, and every
  ticket (an automatic story line, `UpdatedUtc` untouched), machine (then `RefreshDeviceOwner`), person (one person on both
  becomes one), knowledge base article, version and folder, recurring task, website chat and health report moves; the right client
  keeps its details and only fills blanks, **and its own health check tick**; then the row is DELETED (an archived one would be
  found again by `ByNameAsync`). Nothing on it → simply removed. Owner, supervisor or office administrator
  (`Permissions.CanRemoveClients`). Every rule `Services/ClientService.Remove.cs`. `clientremovecheck` 47 on both databases (42
  with two moves out). **Signed `Publish-Platform.ps1`, no migration**; `TEST-3.6.md` section EC, 927–932.
- `docs/NEXT-SESSION-WORKFLOWS.md` — **stage 3.7: ALL FIVE categories BUILT** (Workshop, Call out,
  Walk in, Remote support, General), in Clint's words. A defined route per ticket category: the stages a job moves through, who may move it
  between them, what must be true before it leaves each stage, and who it lands with. **Clint is
  writing the workflows himself and they go INTO that file** — it has a section per category
  waiting for them, **five of them since 2026-09-21, not four**. Read it before anything else on
  this subject: it holds the fork that decides the
  size of the job (built in versus configured per reseller — recommendation is built in), the trap
  that decides whether the data model changes (`TicketStatus` is one enum shared by every
  category), and the reminder that automatic tickets from sessions and calls never pass through a
  booking form at all.
- **A FIFTH TICKET CATEGORY, `General`, IS THE DEFAULT (2026-09-21, §42). NOT on hardware.** A new
  ticket is General unless something says otherwise; sessions and calls still say Remote support
  for themselves. **It is stored as 4 and offered FIRST**, because 0 has meant Remote support in
  every row ever written — so for the first time a category's position on screen is not its number.
  `TicketSummary.CategoryNames` is by value, `CategoryOrder` is display order, and a picker walks
  the second carrying the VALUE it finds. The three `Math.Clamp(raw, 0, 3)` casts are now
  `TicketService.CategoryOf(int)`, so an unrecognised number lands on General rather than on Call
  out. `photogate` 37, `ticketformprobe` 86. **No migration**;
  platform restart and `-OnlyTechnician`.
- **THE CONSOLE HAS ITS OWN LIGHT/DARK BUTTON (2026-09-23, §43). NOT on hardware.** Same setting
  on the person as the portal's switch (`POST /api/appearance`), applied the next time the console
  STARTS — deliberately not live. It found a real fault: the Tickets hand-off signed the portal in
  with the appearance from the console's sign-in, so a switch made since was undone; `/portal/enter`
  now reads it fresh. `appearancecheck` 14 of 14 (12 with the old line back), `dashtabs` 72.
  No migration; platform restart and `-OnlyTechnician`. `TEST-3.6.md` section L.
- `docs/NEXT-SESSION-DASHBOARD.md` — **stage 3.6, BUILT IN FULL 2026-09-20 and NOT ON HARDWARE.**
  Contacts; the console home as Attended/Unattended tabs listing machines under the person who owns
  them; technicians signing in to the portal at last, with the console's Tickets button handing them
  a one-use link; roles enforced in the service rather than in markup; three workshop photos in and
  three out; and **every remote session getting its own ticket**, with jobs made of sessions.
  Six harnesses — `contactcheck` 34, `dashtabs` 47, `handoffcheck` 42, `ticketscope` 26,
  `photogate` 36, `ticketpersession` 28 — and **HANDOVER §32 to §36 is what each stage turned into
  and what building it found.** Read §36 before touching the reuse window: it exists to be 30
  minutes, and widening it back because the queue looks busy would undo the whole stage.
  **3.6.9 (a light or dark each person owns) is DONE the same day** — `themecheck` 40 of 40, both
  palettes complete and every text pair passing WCAG AA; §38, which also records the half that was
  nearly missed. **3.6.8 (roles as tick boxes) is NOT built**, and it is why nothing in the ticket
  pages compares a role — they ask `Permissions.CanSomething(role)` instead.
  **Needs a full platform restart** (migrations `ContactsAndDeviceOwner`, `WorkshopPhotos`,
  `TicketParent`) **and `ship-build.ps1 -OnlyTechnician`.** No client reinstall.
  `tools/testtech` makes one account of each role for testing.
- `docs/NEXT-SESSION-ADAPTIVE.md` — **stage 2.3, DONE 2026-09-17: floor and controller.** The floor
  was a real fault — the send channel dropped screen frames and the host marked them delivered
  anyway, so the picture went stale and stayed stale. The controller (`LinkController`) then chooses
  quality and frame rate from the queue depth: `codecbench --adapt` 13 of 13, and on hardware it
  proved the property that mattered by doing **nothing** — one trace line for a whole session, no
  hunting. **The gap: nobody has watched it back off on a genuinely bad link, and its thresholds
  are guesses rather than measurements.**
- `docs/NEXT-SESSION-CODEC.md` — **stage 2.2. FIXED 2026-09-17 and IT ENCODES.** A 1920x1200 screen
  in 10.9 KB at 1.0/255 against the original; scrolling **2785 → 198 KB/s**, CPU 12.7% → 1.0%. Two
  faults, one hiding the other: `MFT_MESSAGE_NOTIFY_START_OF_STREAM` and `..._END_OF_STREAM` were
  **swapped**, so starting the stream told the encoder it had ended; then the first
  `ProcessOutput`'s `MF_E_TRANSFORM_STREAM_CHANGE` was declared and never read. **§25 of the
  handover is superseded — the missing-silicon conclusion was wrong.** **PROVEN over a real session
  on DESKTOP-2N6FVGT: `codeccheck` 8 of 8**, first picture in 585 ms, decoded screen looked at. The
  host also now RELEASES its Desktop Duplication after 3 s with no subscriber, so an idle session no
  longer locks the machine's screen away from everything else. Still open: NVIDIA will not activate
  on an Optimus machine, and bandwidth over a session has only been compared on an idle screen.
- `docs/NEXT-SESSION-FILES.md` — **finished 2026-09-15, proven on a machine 2026-09-16.** Text,
  files and folders moving both ways, by drag and drop and by copy and paste. Roadmap 1.3, widened.
  §22 of the handover is what was built and what the hardware run confirmed: the Files tab opens on
  the customer's desktop unprompted, a real drag out lands and opens, `%DESKTOP%` resolves, and the
  customer is told in both the tray app and the ticket. Nothing outstanding.
- `docs/NEXT-SESSION-RECONNECT.md` — **finished 2026-09-15.** Surviving a dropped socket (1.4) and
  the system-action buttons (1.5). §19 is what was built; **§21 is what testing it on a real machine
  found** — Ctrl+Alt+Del needing a machine policy, the restart reconnecting before the machine had
  gone down, the toolbar vanishing in full screen, and the downloaded-from-internet mark that put a
  security warning on every customer logon. Kept for the reasoning.
- `docs/NEXT-SESSION-AUDIO.md` — **finished 2026-09-13.** Streaming the customer's sound and the
  Sound tab, shipped and confirmed over a live session; see §18 of the handover. Still open: the
  Opus question (the bandwidth is measured and does not settle it) and the microphone.
- `docs/NEXT-SESSION-IO.md` — **finished 2026-09-13.** Copy and paste both ways, files and folders
  both ways, drag and drop in. All proven on real hardware; see §8 of the handover. Kept for the
  audio measurements and the reasoning, not as a to-do list.
- `docs/NEXT-HEALTHCHECK.md` — the health check brief. **Finished 2026-09-13.** §17 of the
  handover describes the result; `docs/PLAN-HEALTHCHECK.md` records what was built and how each
  piece was proven. The toolkit itself now lives in `healthcheck/`.
- `docs/PRICING.md` — **the commercial model. CHANGED AGAIN 2026-10-22 (§153): FIVE seats and no more,
  any mix of roles, at R500 / $49 / £49 / €49 — no extra seats; every company but Anywhere IT (not
  billed); five paying companies at a time. (2026-10-08, §116, had extras at R300 up to ten.) One
  owner-only login free, any other person (a second owner-only login too) a seat.** `Pricing.SeatsFor` is the one rule and `Pricing.LimitProblem` the limit — never count
  seats with `Technician.UsesSeat`. **Several owners since the same day (§117)**: only an owner
  makes, changes or removes one, and the last is never lost (`rolecheck` 84). Migration
  `FiveSeatPackage`; `seatcheck` 78 (58 with the limit out); South Africa's terms are version
  2026-10-08. `TEST-3.6.md` sections CF and CG, 546–555. South Africa opens first, PayFast for payment
  (4.8, not built).
- `docs/HOSTING-DNS.md` — **where the domain, its DNS and its mail actually live, as of 2026-09-16.**
  The old VPS is permanently gone; DNS moved to Cloudflare and the domain now resolves to xneelo
  shared hosting carrying a holding page and the mail. It wins over every other doc on those facts,
  including the runbooks. §23 of the handover is the short version.
- `docs/GO-LIVE.md` — **the hosting runbook (stage 4.1–4.3), written 2026-09-14.** Server spec, the
  two DNS records, `deploy\Publish-Platform.ps1` (build, upload and install in one command from this
  PC) and `deploy/go-live.sh` (Caddy, TLS, systemd, firewall). Read it before touching production
  hosting, and keep it true when the deployment changes.
- `docs/BUGS.md` — open faults, with what was already observed and ruled out. Check it before
  investigating anything that misbehaves; half the diagnosis may already be there.
- `docs/NEXT-TICKETING.md` — finished 2026-09-09. Kept for the reasoning behind the portal, not
  as a to-do list.

## What this is

A from-scratch Windows remote support suite plus a job/ticketing system, built for Anywhere IT
(Clint, clint@anywhereit.co.za) and resold to other IT companies as a multi-tenant SaaS,
**The Remote Support Program** (theremotesupportprogram.com).

Nothing is bought in. Screen capture, input injection, file transfer, script execution, the
protocol and the portal are all ours.

## Build and run

```bash
dotnet run -c Release --project src/RemoteSupport.Platform     # the live server, port 8090
.\build.ps1                                                    # stage everything into dist\
```

`demo/technician` and `demo/client` are ready-to-run branded copies. **They go stale** — refresh
them from `dist/` before testing, or you will test yesterday's binary. That has wasted time twice.

## Working agreements

- **Measure before changing code.** When something is reported broken, prove where it breaks
  first. `tools/` exists because "the screen is black" turned out to be a rendering bug, and
  guessing would have sent the fix to the wrong file. See §7 of the handover for the harnesses.
- Give Clint **one file or one command to run**, not a list of manual steps. Shell commands go in
  their own fenced block so he can click Run.
- **Never take Clint's passwords in chat.** The agreed pattern is `Get-Credential | Export-Clixml`
  so plaintext is never seen.
- Test any diagnostic script locally before sending it to a customer machine.
- This is a git repository. Commit before large or risky edits so there is a way back. §14 of the
  handover lists what is deliberately not tracked — secrets, build output, staged payloads.
