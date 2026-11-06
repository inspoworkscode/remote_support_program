# Hardware test — stage 3.6 and the theme switch

Written 2026-09-20; sections I, J and K added 2026-09-21. Everything in A-H was
built on 2026-09-20 and **no part of it has been run on a real machine.**

Sections A-H need **no client reinstall** and nobody at the far machine except for the two session
tests. **Section J does**: it is the 2026-09-21 unattended fault (HANDOVER §41), which changes the
client and the installer, so that machine has to be reinstalled before J means anything.

---

## Before you start — two commands

**1. Restart the platform.** Three migrations apply on startup: `ContactsAndDeviceOwner`,
`WorkshopPhotos`, `TicketParent`.

```bash
dotnet run -c Release --project src/RemoteSupport.Platform
```

**2. Refresh the console.** `demo\technician` is yesterday's binary until this runs.

```bash
powershell -ExecutionPolicy Bypass -File .\tools\ship-build.ps1 -OnlyTechnician
```

**Optional, for the roles section only.** Makes one account of each role in one company, so the
three can be compared side by side. The password is generated into `data/test-accounts.txt` rather
than printed. `--remove` takes them away afterwards.

```bash
dotnet run --project tools/testtech
```

---

## A. The console home screen — 3.6.1, 3.6.2

Open `demo\technician` and sign in. Everything in this section is the first thing on screen.

1. **Two tabs, Attended and Unattended**, each carrying a count. No Status column, no Access
   column.
2. **Machines are gathered under the customer who owns them**, one expandable row per customer.
   Collapse one, close the console, open it again — it should still be collapsed.
3. **The first column is never blank.** It is the contact's name, else the client's, else whoever
   is signed in at the machine.
4. **A green or red dot at the left edge** in place of the old Status column.
5. **No faint vertical lines between the columns.** This is the one to look at hardest — it was
   invisible from here and you reported it from a screenshot. The scrollbar on that list is the
   old-style one and the collapse box is a square `[-]` rather than a chevron: **that is the
   trade you chose, not a regression.**
6. **The two cards are one line each while nothing is happening** — "No calls waiting." and the
   same for the other — and open to full size the moment something arrives. They were eating
   430 px of a 900 px window.
7. **Nothing is clipped.** `SectionTitle` was cutting the descender off "Incoming calls" at your
   150%. Look at the tails of the g and the p.
8. **A ticked box moves a machine between tabs** and the row moves live, without a refresh. This
   is the far end of the unattended switch — the half nobody has watched yet.

## B. Contacts — 3.6.1

9. Add a contact to a client that has one machine. Point the machine at the contact rather than
   the company. The console's first column should show **the person**.
10. **Restart the platform and do not touch the far machine.** The name must still be the
    person's. A machine that has not reconnected since a restart used to fall back to the
    company's name — that gap was found while wiring 3.6.3 and fixed, and this is the only way to
    prove it on real hardware.

## C. The browser hand-off — 3.6.3

11. **Press Tickets in the console.** A browser opens, already signed in as you. Nothing asks for
    a password.
12. **Copy that URL and paste it into a second browser.** It must refuse — the link is single use
    and dies after a minute.
13. Restart the platform with a link unused. It must refuse too; the tokens are in memory.

## D. Roles — 3.6.4

Run `tools/testtech`, then open three browser windows, one signed in as each account.

14. **Technician**: their own tickets and the unassigned pool. Nothing of a colleague's. They can
    claim from the pool **to themselves** and cannot assign to anyone else.
15. **Office administrator**: sees everything, opens tickets, assigns and reassigns, comments —
    and **cannot change a status or a priority**.
16. **Owner**: everything, including status.
17. **`/portal/workload`** — every technician, busiest first, with the unassigned pool on the same
    page.
18. The console's **Tickets** button no longer opens the old in-console window at all. `TicketsForm`
    is still in the build, unreachable, as a one-line fallback if the hand-off misbehaves on a
    machine that is not this one. **That is what this test decides.**

## E. Workshop photos — 3.6.5

19. **New ticket** on the dashboard header — it is a button beside Tickets now, because the dialog
    was only reachable from a window nothing opens any more.
20. Category **Workshop**. It should refuse to book in with two photos and take three. The same
    from the web booking form.
21. **Try to close a workshop job with no after photos.** It must refuse — *and the note you typed
    in the same form must still save.* That half matters more than the refusal.
22. **Rename a text file to `.jpg` and upload it.** It must be refused on its content, not its
    name.
23. A **remote support** ticket closes with no photos at all.
24. New folder to know about when the platform is deployed or backed up:
    `data/ticketphotos/<tenant>/`.

## F. The New ticket dialog — §37

25. It is **1092x941 now, not 640x720 with 1180 px inside it.** Two columns: the job on the left,
    what the category needs on the right.
26. **Change the category several times.** The window must not jump about — it is sized for the
    tallest one.
27. **No empty gaps.** The client-match list used to leave an 84 px hole with nothing in it. The
    right column always says something, even for remote support and walk-ins, which need nothing.
28. Resize it. It has a floor, and the floor can never be bigger than your screen.

## G. One session, one ticket — 3.6.6, 3.6.7

This is the part that needs a real session, and it is the one that changes behaviour that used to
be deliberate.

29. **Start an unattended session. You are asked why first**, three characters minimum. Your answer
    becomes the ticket's subject and description — so the queue reads as work, not as
    "Remote session on DESKTOP-2N6FVGT" a hundred times.
30. **End it and start another within 30 minutes: same ticket.** After 30 minutes: a new one.
    The window exists to be 30 minutes — §36 — and widening it because the queue looks busy would
    undo the stage.
31. **A session from a customer's CALL asks you nothing.** They already said what was wrong.
32. **Press Reconnect, or restart the far machine.** It must NOT ask you again — it re-sends the
    original reason.
33. **`/portal/tickets` groups by customer**, busiest first, each block collapsible with its own
    total.
34. Make one ticket the parent of another — it offers **recent work on the same machine**. The
    parent totals its own time plus its children's. **It must refuse to close while a child is
    open**, and closing the parent must leave the children alone.

---

## H. Devices is everybody's, and the console can link one

Added 2026-09-20, after A–G were written. `devicelink` is 19 of 19 and was proved to fail without
the fix, but none of it has been seen on a real machine. **No migration** — a platform restart and
`ship-build.ps1 -OnlyTechnician` cover it.

35. **Right-click a machine on the console's home screen.** A menu appears with Connect and
    **Link to a customer…**. The section title now says so: "double-click to connect, right-click
    for more".
36. **Right-click a machine that is NOT selected.** The menu must act on the one under the
    pointer, not on whatever was selected before. Link it and check the right machine moved.
37. **Link one to a company.** It moves under that customer in the list, live, without a refresh.
38. **Link one to a person.** The first column shows the person, and the machine still sits under
    their company's block — not in a block of its own and not in "Not linked to a client".
39. **Unlink one** ("Not linked to anybody"). It drops to the strays block at the bottom.
40. **Sign in to the portal as a technician.** Devices is in the nav, the page opens, and the
    owner picker works. Same as the office administrator.
41. **A machine linked in the console changes in the browser, and the other way round.** They are
    the same write path; this is the check that says so.

---

## I. Copy and paste, by hand — 3.5.1 (HANDOVER §39)

**CONFIRMED BY HAND ON HARDWARE, 2026-09-21** — Clint, after the ship and the platform restart:
*"copy and paste is now working both ways, this is excellent."* That closes checks 42 and 45, the
two that carry the claim, and with them the gesture no harness could perform. The code paths are
also proven by `userpaste` 4 of 4 and `usercopy` 3 of 3 on DESKTOP-OPHQIDH.

**43, 44, 46, 47 and 48 have NOT been done by hand** and are still worth doing: they are the
cases a single file cannot show you — several at once, a folder, a second paste of the same
clipboard, and the leak. Leave them unticked until somebody has actually watched them.

The host on DESKTOP-OPHQIDH is already the fixed one. **Any other machine needs a full ship**
(`ship-build.ps1`, not `-OnlyTechnician`) and a client reinstall, because this changes the HOST.
**The platform must also be restarted**, because the leak fix in §40 changes the server too.

42. **DONE by hand 2026-09-21.** Copy a file on this PC in Explorer (Ctrl+C), click into the
    session window, then press Ctrl+V on the customer's desktop. The file appears on their
    desktop. Before the fix, Paste was greyed out and nothing happened.
43. **Do it again with three files at once, including one with a space and a non-ASCII
    character in the name.** All three must land.
44. **Copy a FOLDER and paste it there.** The folder and everything in it must land.
45. **DONE by hand 2026-09-21.** The other way: copy a file on the customer's machine and press
    Ctrl+V in your OWN Explorer here. It arrives. This direction was the one broken on
    2026-09-21 and fixed in §40; it needs the PLATFORM restarted, not just a ship.
46. **Paste the same file a second time without copying it again.** It must land again — a
    paste that MOVES the staged copy would work once and then silently do nothing.
47. **Copy a FOLDER on their machine and paste it here.** The folder and its contents must arrive.
48. **Close the console with a session open, reopen it, and copy something on their machine before
    you look at their screen.** Nothing may reach you until you are watching again. This is the
    leak §40 found: the clipboard used to keep crossing after the technician had gone.

---

## J. Unattended access tells the truth — HANDOVER §41

The fault Clint reported on 2026-09-21. Do these on a machine you have just reinstalled with
the new build, and note that **49 is the one that matters** — the rest are the reporting.

49. **Run `tools\agentstate.ps1` on the customer's machine** (it changes nothing). It prints
    whether the agent service is installed, running, connected, and to which server, and ends
    with a verdict. **Send me that output.** If the service is not running, this is the whole
    answer and the rest of this section is about making sure you are told next time.
50. **Watch the installer's own words.** It must no longer say `Agent service installed and
    running.` unless it is: it now waits and checks. If it says anything in red, that machine
    will not do unattended access and you now know at install time rather than at the customer.
51. **Tick the box in the tray app on a machine whose agent IS running.** The line under it must
    say technicians can connect when nobody is there. Connect from the console — **no prompt on
    their screen.** That is the pass.
52. **Now stop the service** (`sc stop RemoteSupportAgent`, as an administrator) and look at the
    tray menu. It must read `Allow unattended access — the background service is not running`,
    and the wording must say you will still have to ask them first. It must NOT still be
    claiming technicians can connect with nobody there.
53. **With the service still stopped, look at the console.** That machine's dot goes **amber**,
    the Unattended tab reads `Unattended  n  (1 not ready)`, and hovering the row explains it in
    words. Connect to it — the customer IS asked to accept, which is now what both ends said
    would happen.
54. **Start the service again** (`sc start RemoteSupportAgent`). Within a few seconds the dot goes
    green on its own, the tab warning clears, and the tray wording goes back to the plain promise
    — with no refresh, no reconnect and nobody touching the tick box.

## K. The General category (55-59) — HANDOVER §42

Added 2026-09-21. Needs the platform restarted and the console refreshed
(`ship-build.ps1 -OnlyTechnician`); **no client reinstall and no migration**, so this section can
be done on this machine alone.

55. **Open New ticket in the CONSOLE.** Type of job must already read **General**, and General must
    be the **first** entry in the dropdown, ahead of Remote support. The right-hand column must
    say *"A general job needs nothing else — fill in the job on the left and create it."* — with a
    dash, not the digits `2014`, which is what it used to print for walk ins and remote support.
56. **Create that ticket without touching the category.** In the ticket list it must show the
    pill **General**, and the first note on it must read *"General ticket opened for …"*.
57. **In the console, pick each of the other four in turn.** Workshop must still bring up make,
    model, serial, accessories and the photo button; Call out must still bring up the site
    address and the appointment. **This is the one that matters**: the list is no longer in the
    order the categories are numbered, so if anything is mis-wired you will see one category's
    name above another category's fields.
58. **Do 55-57 again in the PORTAL** (`/portal/tickets/new`). Same five buttons, General on the
    left and already chosen, and choosing Workshop or Call out still opens their extra fields.
    Nothing in the portal half of this has been exercised by a harness — only compiled — so this
    check is the proof.
59. **Start a remote session with somebody, and look at the ticket it opens by itself.** It must
    still say **Remote support**, not General. A session states what it is; General is only what
    a ticket gets when nobody says.

## L. The console's light-or-dark button (60-63) — HANDOVER §43

Added 2026-09-23. Needs the platform restarted and the console refreshed
(`ship-build.ps1 -OnlyTechnician`); **no client reinstall and no migration**, so this section can
be done on this machine alone.

60. **Open the console.** Left of New ticket there is a button reading the palette the console is
    NOT in — `Dark mode` on a light console, `Light mode` on a dark one. Hover it: the tooltip says
    it switches the console and the portal.
61. **Click it.** A message says the new palette applies the next time the console starts, and how
    (tray icon → Exit → open it again). The console itself does not change colour yet, and the
    button now reads the palette it IS in — that is correct; clicking it again would undo it.
62. **Exit from the tray and open the console again.** It comes up in the palette you chose, and
    the button now offers the other one.
63. **Press Tickets.** The portal opens in the SAME palette. Then flip the portal's own switch,
    come back to the console and press Tickets again: the portal must stay in what you just chose
    in the portal. Before §43 it snapped back to the palette the console signed in with.

## M. The Workshop workflow (64-80) — HANDOVER §44

Added 2026-09-24. Needs a **full platform restart** (migrations `OwnerRole`, `WorkshopWorkflow`,
`QuoteMail`, `Mentions` run by themselves) and `ship-build.ps1 -OnlyTechnician`. No client
reinstall. Everything here can be done on this machine. You need three logins: your own, a
technician, and an office administrator — `tools/testtech` makes them.

**Before any of it:** your own row on the People page should now read **Owner / Supervisor**,
and every other administrator **Supervisor**.

64. **People page.** Your row offers only Owner (no seat) and Owner / Supervisor (uses a seat).
    Switch to Owner: the seat count drops by one, and the console refuses your sign-in with a
    sentence saying why. Switch back.
65. **Book a workshop machine in** with no technician chosen. It is refused: *"Choose the
    technician this machine is booked in to."* Choose one; it books in at **Awaiting diagnosis**.
66. **As that technician**, open it: the right-hand card says *Awaiting diagnosis · With <name>*
    and offers Start work. There is no status dropdown on a workshop job any more.
67. Start work, then press **Repairable** without a Work done entry: the outcomes appear, and
    pressing one is refused with *"Add a Work done entry first"*. Add one on the left.
68. **Solved, to be invoiced** with time `1:15` and amount `R 1 250,00`. The card now reads
    *To be invoiced · In the billing queue*, 1 h 15 min, 1,250.00.
69. **As the office administrator**: menu → **Billing queue**. The job is there with its stage.
    Client invoiced (amount and invoice number) → *Invoiced — awaiting payment*. Payment received
    → *Awaiting collection* (the machine has not been collected).
70. Close ticket **is refused** until the machine is collected. Upload three photos under "As it
    leaves", press **Machine collected**, then Close ticket. It closes.
71. **Quote route.** A second job: Work done, then add two items under Quote. Type `memory ddr`
    — the item list offers matching items whatever the word order. Type a new name like
    `SO-DIMM memory DDR4` when "DDR4 memory 8GB SO-DIMM" exists: **"Did you mean"** appears.
72. Press **Quote needed**. As the office administrator, **Request quote**: the first time it asks
    which email you use. Desktop Outlook: a draft email downloads — open it; it is addressed, has
    the items and part numbers, and the part photo attached, ready to send.
73. Quote sent (amount + reference) → **Re-quote** goes back to Quote needed with the items kept →
    request again → Quote sent → **Quote accepted** → Client invoiced → Payment received, order
    placed → Parts received. The job goes **back to the technician** for Parts fitted.
74. Parts fitted → back in the billing queue → collect with three photos → Ticket complete closes it.
75. **Rejected quote.** On a third job, Quote rejected: it returns to the technician offering ONLY
    Invoice inspection fee or Do not invoice.
76. **Do not invoice** needs a reason, then waits for a supervisor. The office administrator cannot
    approve it; you (owner) can. Decline sends it back to the technician's bench.
77. **Abandoned** from any stage, as the office administrator, with a reason. No photos needed.
78. **@mentions.** As the office administrator, comment `@<technician> is it ready?` The
    technician's portal menu shows **1 waiting on you**, and the console's Tickets button shows the
    count. The flag stays when they press a stage button, and clears when they comment or add Work
    done on that ticket.
79. **History** on any workflow job is grouped under stage headings and reads top to bottom as the
    job's story.
80. **Items page** (menu → Items), as the office administrator: rename one; merge two. The merged
    one disappears from the list and old quotes still show what they said.

**The console's ticket window (HANDOVER §45).**

81. **Press Tickets in the console.** A window opens (not the browser) on **My work**, the queue
    on the left. With somebody waiting on your reply, it opens on **Waiting on my reply** instead.
82. **Open a workshop job on the bench.** "Is it repairable?" — press Repairable: Solved, Quote
    needed and Do not invoice appear under "Then, how does it end?". Press Not repairable: they
    change to Invoice inspection fee and Do not invoice.
83. **Solved, to be invoiced** opens a small window asking for time and amount. Type `1:15` and
    `R 1 250,00`; the job moves to To be invoiced and the card shows 1 h 15 min and 1,250.00.
84. **Quote:** type `memory ddr` in Item — matching items drop down. Add one with a part photo;
    type a near-duplicate name and a "Did you mean…" window appears.
85. **Comment** with "Ask someone…" → a name: `@Name` goes in the box. The other person's Tickets
    button turns red with the count; it clears when they reply on that ticket.
86. **Open in browser** opens the portal on the ticket you have open, signed in. Resize the window
    down as far as it goes: nothing is cut off on the right, and in dark nothing stays white.

## N. The ticket as a story, and the new order (87-94) — HANDOVER §46

**Built 2026-09-27. This REPLACES checks 66, 67, 79, 82 and 83** — the bench is now Booked in → Inspection findings → Repairable → Work done,
and neither screen has the Repairable toggle or the pop-up any more. Everything from 68 on the
billing side still stands.

87. **In the portal, open a job just booked in.** At the top: `Workshop › Booked in`, and only two
    buttons — ☞ Inspection findings and ☞ Waiting on customer. Under them: Activity, Photos,
    Details. The comment box is first; the booking is the bottom entry, numbered 0.
88. **☞ Inspection findings** opens a form at the top of the story with Submit. Submit it empty:
    refused. Type what you found, Submit: the crumb reads `Booked in › Inspected`, and the story
    has an "Inspection findings → Inspected" card with your words.
89. **☞ Repairable**: the buttons are at once Solved — to be invoiced, Quote needed, Do not invoice,
    then Waiting on customer. **☞ Solved** asks *what was done*, the time and the amount, in that
    order. Submit it; on Details, **Copy the work done for the invoice** and paste it somewhere: it is
    the work only, no time or amount. (On another job, ☞ Not repairable offers only Invoice
    inspection fee and Do not invoice.)
90. **☞ Quote needed**: the Item box has **Add** beside it. Type `DDR` — matching items appear under
    it. Type a name that is not there, give it a **serial number**, and press Add: it goes on the
    list with its serial in the Serial number column. Submit sends the job to the office. Press F5
    afterwards: nothing is posted twice. Later, Request quote's email has the serial after the part
    number.
91. **As the office administrator**, ☞ Request quote opens ONE form (supplier address, and which
    mail you use the first time). Submit: the stage becomes Quote requested.
92. **In the console**, press Tickets and open a workshop job: the same crumb, the same ☞ buttons,
    the same tabs as buttons, the comment box first and the story underneath, newest first.
93. **Console, a step with a form** (Inspection findings or Work done): the form opens at the top of
    the story. Type half a sentence, and from the portal post a comment on the same ticket — the
    console redraws and **your half sentence is still there**. Submit: the form closes and the
    story gains the card.
94. **Console, ☞ Request quote** (as a supervisor, on a job in Quote needed) opens the portal on
    that ticket **with the Request quote form already open**; "See them in the browser" on the
    Photos tab opens it on the photos. In dark, look at the queue's column header — say if there is
    a white strip at its right-hand end (§46).

## O. The Call out route (95-100) — HANDOVER §47

**Built 2026-09-27.** After the restart, any call out that was open is at *Call out booked*.

95. **Book a call out** with a site address and an appointment. It opens at `Call out › Call out
    booked`, with one step: ☞ Work done. There is no Photos tab; the bottom of the story says who
    booked the call out, with the site and the appointment.
96. **☞ Work done** asks what was done and the time spent. Submit: the three options are
    ☞ Ticket completed — to be invoiced, ☞ Invoiced — paid on site, ☞ Needs workshop work.
97. **Ticket completed — to be invoiced** asks only the amount, and lands in the billing queue at
    *To be invoiced*. As the office: Client invoiced → Payment received — and it **closes**. It is
    never offered "Machine collected".
98. **Invoiced — paid on site** on another call out: ☞ Paid cash asks the amount; ☞ Paid card
    machine asks the amount and the card machine reference number. Either lands in the billing queue
    as *Paid on site — to be closed*; the technician has nothing to press, the office closes it.
    Details says *Paid on site · amount · cash* (or card machine and the reference).
99. **Needs workshop work** on a third: it asks for three photos first (Submit without them is
    refused and says so), then make, model, serial, accessories and password, all optional. Submit:
    the SAME ticket number is now `Workshop › Work done › Booked in`, with ☞ Inspection findings
    next, a Photos tab with 3 in, and on Details both the site and the machine, password included.
100. **Do the same three in the console** (Tickets): the same buttons and forms; "Add photos as it
     arrives…" on Needs workshop work takes photos from the PC.

## P. Walk in, Remote support and General (101-108) — HANDOVER §48

**Built 2026-09-27.** After the restart, open walk in, remote and General tickets are on their
routes; closed and resolved ones are closed.

101. **Walk in:** book one. Two buttons: ☞ Repaired at counter and ☞ Needs to be booked in. Repaired
     at counter asks the work done and the time; then To be invoiced, Invoiced — paid at counter,
     No invoice.
102. **Invoiced — paid at counter** → Paid cash (amount) or Paid card machine (amount and reference)
     → the billing queue as *Paid at counter — to be closed*; the office closes it. **No invoice**
     asks why and goes to a supervisor.
103. **Needs to be booked in** on another walk in: three photos, then the machine questions — the
     same number becomes a Workshop job at Booked in.
104. **Remote support:** start a session. Its ticket offers To be invoiced, Do not invoice (SLA
     client), Do not invoice and Link to parent ticket. Choose one, then start another session on the
     same machine within 30 minutes: it gets a NEW ticket, because the first is decided.
105. **Link to parent ticket** on a session ticket for a client with another open job: only that
     client's open tickets are offered. Choose one: the session ticket closes and the job's story says
     it was filed there. On a machine with no client, it says to link a client first.
106. **Do not invoice (SLA client)** asks nothing and lands with the office to close.
107. **General:** New ticket, General, no client — it opens. Buttons: Done, Quote needed, To be
     invoiced. Done closes it. On another, Quote needed takes items and goes to the office with no
     client.
108. **Link to a client** on the Details tab of any ticket, in the portal and the console (Link to a
     client… / Change the client…). The header shows the client, and the story says it was linked.

## Q. Several people on one ticket (109-115) — HANDOVER §49

**Built 2026-09-28.** Best done with the new staff accounts — you as supervisor, Ryan and Anthony
as technicians — in two browsers or a browser and a console.

109. **From the start:** New ticket (portal), tick Ryan AND Anthony under *People on this job*. The
     ticket's header says *With Ryan and Anthony*; Details says *People on it: Ryan, Anthony*. Do
     the same from the console's New ticket — you are ticked for you; tick a second person too.
110. **Both see it:** signed in as Ryan, it is under My tickets; signed in as Anthony, the same. As
     a third technician not on it, it is not in the list and its number gives "not found".
111. **Added later:** as the supervisor or office administrator, open any ticket and tick a third
     person, Save. The story says *added … to the ticket*. Untick one: *took … off the ticket*.
112. **What a technician may change:** as Ryan on a job with Anthony, the box for Anthony is greyed
     (he cannot take a colleague off); his own box and anybody not on it can be ticked. Tick a
     colleague, Save — they are on it. Untick himself, Save — he is off, Anthony stays.
113. **Two on the bench:** a workshop job with Ryan and Anthony — EITHER may press Inspection
     findings, Repairable and the endings. Ask for Do not invoice; when a supervisor declines it, it
     comes back to BOTH of them.
114. **Workload** (`/portal/workload`): the shared job counts on Ryan's row and on Anthony's.
115. **Sessions:** a second technician joining a remote session already running goes onto that
     session's ticket, and its story says *… joined the session on …*. A technician connecting to a
     machine whose ticket is still inside its 30 minutes is added beside whoever is on it.

## R. The monthly health check, per client (116-124) — HANDOVER §50

**Built 2026-09-28.** It changes the platform, the console, the CLIENT and the installer. Before
these mean anything: platform restart (migration `ClientHealthCheck`), a **full** ship, and the
test machine **reinstalled** from the new download.

```bash
powershell -ExecutionPolicy Bypass -File .\tools\ship-build.ps1
```

116. **The default install is without it.** Reinstall a machine whose client is NOT ticked. The
     installer ends by saying the health check is switched on per customer; afterwards Task
     Scheduler has no *Monthly Maintenance and Health Check*. (If it had one from before, it is gone
     within a few seconds of the agent connecting — that is 119 happening by itself.)
117. **Prerequisite, and the thing to check first:** on that machine run
     `tools\agentstate.ps1`. Everything below is done by the agent service; with no agent
     connected, nothing on the machine changes and the menu says *will install when the machine's
     background service connects*.
118. **Tick the client** (portal, the client's page, *Monthly health check* above Machines). The
     message says each machine installs it now. Within a minute: the task exists on the machine,
     and the machine's row on the client page says *On - installed on the machine* (refresh).
119. **Right-click the machine in the console → Monthly health check.** The top line reads the same
     as the portal. Choose *Off for this machine*: the task disappears from the machine, the line
     says *Off*. The client stays ticked, and *As &lt;the client&gt; (on)* is the choice offered to go back.
120. **As the customer** again: the task comes back.
121. **Move the machine to a client with no tick** (Link to a customer…): the task disappears.
122. **The company switch:** untick *Offer the monthly check to my clients* on Health check and
     Save. Every machine that had it loses it.
123. **A machine that was off:** switch a ticked machine OFF in the console while the PC is shut
     down. Start it: the task is removed within a minute of the agent connecting.
124. **Inspect only:** on Health check, untick *Do the maintenance as well*. Run the task by hand
     on a machine that has it (Task Scheduler → Run). `C:\ProgramData\HealthCheck\run_<month>.log`
     says *Inspection only… no maintenance this month*, and no restore point is made.
     Before this build that setting was never obeyed.

If any step goes wrong, `C:\ProgramData\RemoteSupport\healthcheck-agent.log` on the machine holds
the installer's own output.

## S. The health check toolkit at v2.9 (125-128) — HANDOVER §51

Same ship as R (a full ship and a client reinstall), so these run on the same machine after it.

125. **This PC first, elevated:** run
     `powershell -ExecutionPolicy Bypass -File "C:\Users\Clint\Desktop\Remote Support\tools\Test-HealthCheckReinstall.ps1"`
     from an administrator PowerShell. It should end *ALL 20 CHECKS PASSED*. It could not be run
     in the session that made the change, because that session was not elevated.
126. **The far machine picked it up.** With the health check on for it, after the reinstall:
     `C:\ProgramData\HealthCheck\Get-ClientHealthCheck.ps1` contains `$ToolkitVersion = '2.9'`.
     The agent does that by itself; `healthcheck-agent.log` shows the re-install.
127. **Drive health beside CrystalDiskInfo.** Run the task by hand (Task Scheduler → Run) and open
     the month's report. The *Drive health* row shows a percentage, *No problems reported*,
     *Failing* or *Not checked* - never a bare *Healthy* for a drive whose wear could not be read.
     Where there is a percentage, it matches CrystalDiskInfo's.
128. **One backup finding, and a true one.** The same report has exactly one backup line. If the
     machine has a USB install stick or old backup files plugged in, they are not called a recent
     backup.

## T. Importing a client list (129-135) — HANDOVER §52

Needs the platform restarted, nothing else. Sign in to the portal as the owner or the office
administrator.

129. **The button is there.** Clients shows **Import clients** at the top right. Signed in as a
     technician it is not there, and `/portal/clients/import` says who can import.
130. **The template opens clean in Excel.** *Download the template*; it opens with no repair
     warning, headings in bold, and a second sheet *How to fill this in*. Type `0821234567` under
     Phone: it keeps its 0.
131. **The preview, not the import.** Upload `Copy of Clients 28 September 2026.xlsx`. The page says
     **878 new clients** (fewer if some are already on file — they show as *On file*), **848 contact
     people**, and lists `jason@anywhereit.co.za — on 157 clients`, ticked. Nothing is saved yet:
     open Clients in another tab and the count has not moved.
132. **The repairs are listed.** At the top of the list: Morné Pienaar with its letters repaired,
     and phone numbers with their leading 0 put back (rows 214–230, 491, 494, 730, 778).
133. **Import.** Press *Import 878 clients*. Clients now holds them; search `morn` finds Morné
     Pienaar; opening a client shows its contact person under People; nobody's email is
     `jason@anywhereit.co.za`. (No client appears twice in this file, so it never makes one client
     from several rows — `importcheck` proves that case.)
134. **Twice adds nothing.** Upload the same file again: every client is *Already on file* and
     there is no Import button.
135. **One value per field, each in its own column.** In the preview, and on Clients afterwards:
     Contact person, Phone, Email and Address are four columns, and the Clients list uses the whole
     width (*Add a client* opens from above it). A client's own page labels the four. No Phone cell
     holds two numbers or a name, and no Email cell holds two addresses — anything like that is in
     the client's Notes, word for word.

## U. Toolkit v2.11, and the install that keeps trying (136-140) — HANDOVER §53

After a full ship and a client reinstall, on a machine with the health check switched on. 138-140
want the ESET machine from bug 9.

136. **v2.11 arrived.** `C:\ProgramData\HealthCheck\New-ClientReport.ps1` contains
     `$ToolkitVersion = '2.11'` within a minute or two of the agent connecting.
137. **One section per machine.** Run the task by hand and open the month's PDF in
     `C:\ProgramData\HealthCheck\Reports`. Page one lists the machine; its own section follows on
     the next page. No file path or username at the top or bottom of any page, and the masthead
     is your logo or your company name - never another company's.
138. **First, before anything else on the ESET machine:** run
     `Get-Content "$env:ProgramData\RemoteSupport\healthcheck-agent.log" -Tail 80` and note what
     ESET's own log says it blocked. If it names `host.exe` and not a `.ps1`, say so - that is a
     second problem this build does not touch.
139. **It keeps trying.** On that machine, right-click → Monthly health check → On. If ESET holds
     it, the menu's top line says *could not install it … trying again in 1 minute*; right-click
     again after a few minutes and it has moved on - to *installed*, or to the next retry. It never
     just stops at the first failure.
140. **On again means try now.** While the top line says *trying again in …*, choose On again. The
     agent log shows a new attempt within a few seconds, not at the end of the wait. Choose Off
     while it is waiting instead: the task never appears afterwards.

## V. The secure desktop, and a paste that gave the old files (141-146) — HANDOVER §54

After a full ship and a client reinstall. DESKTOP-2N6FVGT already runs this host (swapped in on
2026-09-29), so 141–143 can be done there before the ship.

141. **Ctrl+Alt+Del on the gaming laptop.** In a session on DESKTOP-2N6FVGT, toolbar → system menu →
     Ctrl+Alt+Del. The Windows security screen appears **in the console**, you can click Cancel on it
     there, and **the session is still connected afterwards**. (Pressing Ctrl+Alt+Del on your own
     keyboard only ever reaches your own machine — Windows keeps that sequence for itself.)
142. **A UAC prompt on the gaming laptop.** In a session, right-click Start → Terminal (Admin). The
     prompt shows in the console, you can answer it from there, and the session survives either answer.
143. **The picture after the prompt.** Tools → Info after 142: *Video codec* says H.264, not JPEG
     tiles.
144. **Big copy from the customer.** Copy a folder over 50 MB on this laptop and paste it on the
     customer's machine (click the status bar offer). Then copy a DIFFERENT folder over 50 MB on the
     customer's machine. Before clicking the new offer, Ctrl+V on this laptop pastes **nothing** —
     never the first folder. Click the offer; Ctrl+V now pastes the second folder.
145. **Small copy from the customer, straight after one of yours.** The same as 144 with small
     folders: the customer's folder is what pastes here, every time.
146. **The test machine is unchanged.** 141 and 144 on DESKTOP-OPHQIDH.

## W. Roles are tick boxes (147-152) — HANDOVER §55

After a platform restart (migration `RolesAsTickBoxes`). Nothing to ship, no far machine needed.

147. **Nobody changed.** Open People. Every person has exactly the box their old role was — you are
     Owner (locked) plus Supervisor if you were Owner / Supervisor, and the seat count is what it was.
148. **Two boxes on one person.** Tick Office administrator beside a technician's Technician and
     press Save: *Roles updated.*, both stay ticked, and the seat count does **not** move.
149. **That person gets both.** Sign in to the portal as them: every ticket shows (not only theirs),
     the Billing queue and Items are in the menu, and the console still signs them in.
150. **Nothing ticked is refused.** Untick every box on a row and Save: *Tick at least one role.*,
     and the row is unchanged.
151. **The owner's seat.** On your own row, untick Supervisor and Technician and Save: the seat count
     drops by one and the console refuses you (sign in to it to see the message). Tick one again.
152. **Already signed in stays signed in.** A browser that was signed in to the portal BEFORE the
     restart keeps working and keeps the same menu — it is read by the old numbers.

## X. Every clipboard movement on the ticket (153-155) — HANDOVER §56

After a platform restart and a full ship (DESKTOP-OPHQIDH already has the new host).

153. **One copy, then nothing.** In a session, copy a line of text on the customer's machine and do
     nothing else. Within about a minute the ticket says *Text copied on <machine> was shared with
     <you> (N characters)* — the count, never the text.
154. **Copy, then end straight away.** Put text on their clipboard from your side, then End the
     session within a few seconds. The ticket has the clipboard line, and **Session ended** below it.
155. **The tray app saw it too.** On the customer's machine, the tray app's chat log carries the same
     lines as the ticket.

## Y. Exit in the tray app takes the machine offline (156-160) — HANDOVER §57

After a platform restart and a full ship with a client reinstall. Use a machine with **unattended
access ticked** — that is the one where Exit used to change nothing.

156. **Exit.** On the customer's machine, right-click the tray icon → Exit. Within a second or two
     the machine's dot goes red in the console and it reads offline in the portal.
157. **Nobody gets in.** Connect to it from the console: refused, with *"The person at that machine
     closed the support app…"*
158. **Exit during a session.** Start a session, then choose Exit over there: your session window
     closes, and the ticket says *The person at <machine> closed the support app.*
159. **The shortcut brings it back.** Open the desktop shortcut on that machine: the dot goes green
     and Connect works again, unattended, with no prompt.
160. **A restart does not undo Exit.** Exit on the machine, restart the platform: it is still
     offline until the tray app is opened again. (Shutting the machine down or signing out is NOT
     Exit — the machine stays reachable through the agent, as it always did.)

## Z. Dragging files off the picture (161-167) — HANDOVER §58

After the full ship and a client reinstall. In a session, with Control on.

161. **Off an Explorer window.** On their screen, open Documents in Explorer and click a file. Drag
     it on the picture, out past the edge of the session window, and drop it on this laptop's
     desktop. It arrives, and opens. Their file has NOT moved on their machine.
162. **Several, and a folder.** Select two files and a folder there, drag them off the same way:
     all three arrive, the folder with everything in it.
163. **Off their desktop.** Click an icon on their desktop itself and drag it off: it arrives.
164. **Windows 11 tabs.** In an Explorer window over there with two TABS open, select a file in the
     tab you are looking at and drag it off: that file arrives — not something from the other tab.
165. **Nothing selected.** Drag one of their WINDOWS by its title bar and overshoot the edge of the
     session window: the window still moves over there, nothing is cancelled, and the status bar
     says there was nothing to drag.
166. **Let go early.** Drag something large off and let go before it has arrived: the status bar
     says it is still coming, and it lands on your clipboard — Ctrl+V puts it on this desktop.
167. **Text onto the picture.** Select some words in a document here and drag them onto the picture:
     the status bar says *on <machine>'s clipboard — Ctrl+V to place it*; click into Notepad over
     there, press Ctrl+V, and the words appear.

## AA. Quote requests: the machine, suppliers, BCC, the delivery fee (168-175) — HANDOVER §59

After a platform restart and `-OnlyTechnician`. No far machine needed.

168. **Set it up once.** Portal → Suppliers: put accounts@anywhereit.co.za in *Your own address* and
     the workshop's delivery address, Save. Add two real suppliers you ask for quotes.
169. **No duplicates.** Add one of them again with its words in another order, then again with the
     same address under another name: both refused, naming the one already there.
170. **The machine comes from the booking.** Open a workshop job at Repairable or Quote needed: *The
     machine* shows its make, model and serial. Choose Laptop, Save; the timeline says *The quote is
     for: …*.
171. **Not for a machine.** On another job, empty the machine and Save: it stays empty after a
     refresh, and the request says nothing about a machine.
172. **The dropdown.** Click into the empty Item box, in the portal AND in the console: every item on
     the list drops down. Type a few letters: it narrows. Pick one: it fills the box.
173. **Two suppliers, BCC.** Request quote, tick both: the form says they go in BCC and accounts@
     goes in To. Submit. In Outlook (or Gmail): To is accounts@, **Bcc** has both suppliers, and the
     body has the machine, the items and *Please include the delivery fee, for delivery to:* with
     the address.
174. **One supplier, To.** On another job tick one: that supplier is in To, and there is no Bcc.
175. **Outlook on the web**, if anyone uses it: choose it (Request quote → *change*), and check the
     compose window really has the suppliers in Bcc — never checked, since it needs a signed-in
     mailbox.
176. **The Clients search as you type.** Portal → Clients, type the first letters of a client in the
     search bar without pressing Search: the list narrows with each letter, and the count beside
     *Clients* follows. Clear brings them all back. Then, signed in as a TECHNICIAN, open Add a
     client and type the same letters: the existing client is offered (it was silently empty
     before).

## AB. The privacy screen and session recording (177-186) — HANDOVER §60, §61

After a platform restart and a full ship with a client reinstall. In a session, with Control on.

177. **Privacy on.** System → *Privacy screen — black out their screen*. Over there, every monitor
     goes black with *"<your company> is working on this computer…"*; here you still see their
     desktop, and the status bar says it is on. The ticket says so.
178. **Working through it.** With it on, open something over there and type in it. It all happens;
     the person at the machine sees only black.
179. **A UAC prompt with it on.** Right-click Start → Terminal (Admin) over there: the prompt shows
     on THEIR screen too (it must — they are the one approving it), and the black comes back after.
180. **Off, and ending.** Turn it off: their screen is back. Turn it on again and End the session:
     their screen comes back at once.
181. **Two monitors**, if a machine has them: both are black, edge to edge.
182. **Record.** Tools → *Record this session*. A red *● Recording* marker counts up on the status
     bar. Over there, the tray app's log says you started recording; so does the ticket.
183. **Stop.** Click the red marker after a minute or two. The status bar says it was saved; the
     ticket shows a *Recordings* card with a player — it plays, seeks, and shows what you saw,
     pointer included, the right way up.
184. **Close while recording.** Record, then close the session window: the recording still arrives
     on the ticket.
185. **Who may.** As a technician who was NOT on that ticket, the recording cannot be watched. As a
     technician, there is no Delete; as a supervisor or the owner, Delete removes it.
186. **Privacy AND recording.** With the privacy screen on, record a minute: the recording shows the
     real desktop (what you saw), not black.

## AC. As many workshop photos as the damage needs, and smaller (187-192) — HANDOVER §62

After a platform restart and `-OnlyTechnician`. Use a phone for the portal ones.

187. **Fourteen at booking.** Portal → New ticket → Workshop, pick 14 photos at once from the phone:
     "Uploading 1 of 14…" counts up, all 14 appear, and the job books in.
188. **More as it arrived, after booking.** Open that job → Photos: *Add more photos of the machine as
     it arrived* takes another handful, and the count beside *As it arrived* goes up.
189. **As many as it leaves.** The same with *as it leaves* — well past three.
190. **The console, both ends.** In the console's ticket window, *Add photos as it arrived…* and *Add
     photos as it leaves…* each take several at once, the button counting "Uploading 3 of 8…".
191. **Smaller.** Open one of the photos in the browser (click it): it is sharp enough to see a
     scratch, and a phone's photo taken upright is upright — not on its side.
192. **An iPhone.** From an iPhone's camera (HEIC), photos still go up — resized if Safari can read
     them, as they are if not.

## AD. Delivery instead of collection (193-199) — HANDOVER §63

After a platform restart (migration `Delivery`) and `-OnlyTechnician`. Use a workshop job that has
been repaired and sent for invoicing.

193. **Beside Machine collected.** Once the bench is finished, *Out for delivery* sits next to
     *Machine collected* — for the office and for the technician who did the work, not for another
     technician.
194. **Photos at the office.** Pressing it asks for at least three photos of the machine before it
     leaves; without them it is refused and says so. With them the job is *Out for delivery*, with
     the technician who did the work.
195. **Delivered — work done.** Out for delivery offers *Delivered — work done* and *Not delivered*.
     Delivered asks what was done there and the time; the time is ADDED to the bench's on the
     Details tab, and Details says *Delivered* with the date and who, not *Collected*.
196. **Paid on delivery.** From a job not yet invoiced: *Invoiced — paid on delivery* → Paid cash or
     Paid card machine → it goes to the office, whose Details say *Paid on delivery*; the office
     closes it.
197. **Paid before.** From a job paid and waiting to leave: Delivered → *Nothing more to invoice* →
     back with the office, which can close it straight away.
198. **An invoice out and unpaid.** From *Invoiced — awaiting payment*: after Delivered there is NO
     *To be invoiced*; *Nothing more to invoice* goes back to Awaiting payment, and Payment received
     closes it outright.
199. **Not delivered, and the console.** *Not delivered* asks why and puts it back where it was,
     still able to be collected or sent out again. Do one delivery in the console's ticket window
     too: the same buttons, the same form wording.

## AE. Several machines booked in at once (200-205) — HANDOVER §64

After a platform restart (migration `BookedTogether`) and `-OnlyTechnician`. Book one workshop
machine in for a client first, the usual way.

200. **The button.** At *Booked in*, *Add another machine* is there for the office and for the people
     on the job — before *Abandoned*, not after it.
201. **Its questions.** It asks what is wrong with it first, then make, model, serial, accessories and
     password, and wants at least three photos of THAT machine. Submit with none: refused, and what
     you typed is still wanted — the photos you then add still count.
202. **The new ticket.** Submit with three photos: a new ticket appears for the same client, contact,
     people and priority, its subject the machine and the fault, its photos its own (Photos: 3 in).
203. **Linked both ways.** Each ticket shows *Booked in with:* and the other's number, subject and stage.
     Add a third from the SECOND ticket: all three list the other two.
204. **Their own way.** Inspect one: the others stay at Booked in, and the inspected one no longer
     offers Add another machine. Abandon one: the others carry on, and list it as Abandoned.
205. **The console.** Do it once in the console's ticket window: *Add photos of this machine…* counts
     up without clearing what you typed, Submit books it in, the form closes, and the *Booked in with*
     buttons open each ticket.

## AF. Office chat (206-213) — HANDOVER §65

After a platform restart (migration `StaffChat`) and `-OnlyTechnician`. Two people, on two PCs.

206. **The button.** Every portal page has *Chat* in the bottom-right corner; the console has *Office
     chat* beside Tickets. Click Chat in the portal the first time: the browser asks to show
     notifications — allow it.
207. **Picking someone.** In the list, type `@` and the start of a name, then Enter: their window
     opens. Or click a name.
208. **It pops up.** Send a message to the other person. On their side it pops up by itself — in the
     portal a window, in the console the Office chat window, flashing in the taskbar — and does not
     take the keyboard from whatever they were typing in.
209. **A notification.** With the portal tab in the background (another tab in front), send them
     another: a Windows notification appears. Clicking it brings the conversation up.
210. **Everyone.** A message to Everyone does NOT pop up for anybody — the count goes up. One with
     `@` and their name DOES pop up for that person only. Typing `@` in the Everyone box offers the names.
211. **The count.** The red count on Chat / Office chat goes up with each unread message and clears
     when the conversation has been seen — in the portal or the console, whichever is read first.
212. **Private.** A third person sees nothing of a conversation between two others.
213. **Moving about.** Open two conversations in the portal, then go to another page: they are still
     there, with their messages.

## AG. Support in the browser (214-222) — HANDOVER §66

After a platform restart (migration `WebSupport`) and `-OnlyTechnician`. A second PC with Chrome or
Edge plays the customer.

214. **Switch it on.** Portal → *Browser support*: tick *Take support requests*, set the hours, Save.
     It says whether it is open now, and shows the link. Copy it.
215. **Closed.** Untick a day that is today (or set hours that have passed) and open the link: it
     says you are closed, with the hours and your phone and email — no form. Put the hours back.
216. **The form.** On the second PC open the link: your company's name and logo, the form. Try
     sending it with no name, then with no phone and no email: each is refused, saying why.
217. **It rings.** Fill it in properly and press *Call a technician*. Your console rings like a tray
     app call, showing the name, company and what is wrong — and a ticket appears for them, under a
     client of their company's name (or theirs), with them as its contact.
218. **Chat before answering.** Type something on the page: it reaches the console and the ticket.
219. **Answer and share.** Accept the call. The page says who is with them and offers *Share my
     screen*; press it and choose a screen. The console shows their screen, says *View only* in a
     banner, and has no Control tick, no System menu and no Files/Scripts/Sound.
220. **Moving about.** On the customer PC, switch to another window and work in it for a minute:
     the picture keeps up (the page's tab is behind, which is the point).
221. **Stop and end.** Stop sharing from the browser's own bar: the page offers *Share my screen
     again*, and the console gets a chat line. End the session in the console: the page says thank you.
222. **Two ways in.** Open your download page (`/get/…`): two cards side by side — *Download the
     app* and *Get help in your browser*, each saying what it gives. Outside your browser-support hours
     the second still shows, with the hours; switch browser support off and it disappears.

## AH. Talking to the customer (223-232) — HANDOVER §67

After a platform restart (no migration), a **full ship** and a **client reinstall** on the far
machine. It changes the host, the tray app, the console and the help page. Headphones on this end
make the first go easier to judge; then try it on speakers, which is the case the echo canceller is for.

223. **Is the microphone reachable at all?** Before anything else, on a machine with a live session:
     `dotnet run --project tools/runscript -- <MACHINE> micprobe.ps1`. Sections 1 and 2 must both say
     *real audio arrived*. **Do this once on a Windows 11 machine** — both machines measured so far are
     Windows 10, and Windows 11's microphone privacy is the one thing not yet measured as SYSTEM.
224. **Talk.** In a session: Tools → *Talk to the customer*. A green *Talking — click to mute* pill
     appears on the status bar with *asking to use their microphone* beside it. On the far machine the
     banner says you are talking and asks, with *Use my microphone* and *Not now*; a balloon too.
     Speak: they hear you through their speakers.
225. **Not now.** Press *Not now* on the far machine. The console says *their microphone is off — use
     chat*. They can still hear you.
226. **Yes.** Press *Use my microphone*. The console says *their microphone is on*, and you hear them.
     Their banner says *Your microphone is on* with *Turn my microphone off*.
227. **Echo.** On SPEAKERS at their end (not headphones), keep talking for half a minute. Say how much
     of your own voice you hear coming back — none, a little, or a lot.
228. **Mute.** Click the pill: it turns amber, *Muted — click to unmute*, and they stop hearing you.
     Click again: back.
229. **Off.** Press *Turn my microphone off* on the far machine: you stop hearing them at once.
230. **The ticket.** Open the session's ticket: it says you started talking, that the customer allowed
     their microphone, and that they turned it off — each once.
231. **Listen during a call.** With Talk on, also press Tools → *Listen* and play some music there. You
     hear the music and them — and NOT your own voice coming back out of their speakers.
232. **Unattended.** Connect to a machine unattended and press Talk. Their banner (if anyone is signed
     in) says you are talking, with NO microphone buttons; the console says *unattended, so no
     microphone*. Nothing of the room reaches you.

**In the browser** (a second PC with Chrome or Edge, browser support switched on — section AG): answer
a browser caller and press Talk BEFORE they share anything. The page shows *is talking to you* with
*Use my microphone* and *Not now*; the browser asks for the microphone too. Try it from a PHONE as well:
it cannot share a screen, but it can talk.

## AI. Readable customer names, customers first, the username remembered (233-237) — HANDOVER §68

After a platform restart (no migration) and `-OnlyTechnician`. No far machine needed.

233. **Dark console, customer names.** Console in dark mode: every customer's name above its machines
     is in the same colour as the column headings (Who, Machine…), not black. The count after a name
     (*2 machines, 1 online*) is grey. Then switch to light mode and restart the console: still readable.
234. **Folding.** Click the small arrow at the right-hand end of a customer's heading: their machines
     fold away and the arrow points down. Click it again: they come back. Restart the console: the
     fold is remembered, as before.
235. **Nearly full disks.** A machine over 90% disk is in red you can read on the dark list, not dark red.
236. **Portal Devices page.** Portal → Devices: customers first, alphabetical, and *Not linked to a
     client* LAST. Its count is still in the third box at the top.
237. **Sign in twice.** Sign in to the console, close it, open it again: your username is already
     there and the cursor is in Password — type it and press Enter. A wrong password once, then the
     right one, still remembers the right username; a typo'd username that failed is never remembered.

## AJ. A download link for technicians, and signing out (238-243) — HANDOVER §69

After a platform restart (no migration) and `-OnlyTechnician` — the new console must be on the server
for the link to hand it out.

238. **The link.** Portal → Downloads: *For your technicians* shows `…/tech/<your-slug>` with Copy.
     Open it in a private browser window: the company's banner and *Sign in to download*, no button.
239. **A technician downloads it.** Sign in there as a TECHNICIAN (not the owner). The download button
     appears; the zip opens, and the console inside signs in. Do it once on a PC that has never had it.
240. **Not everybody.** Sign in on the link as the office administrator: told the console is not for
     them, no button. *not you?* brings you back to the same page, signed out.
241. **Sign out.** In the console, *Sign out* (top right). It closes, and a moment later the sign-in
     window opens by itself with your username filled in. Task Manager shows ONE console, not two.
242. **Sign out during a session.** Connect to a machine, then *Sign out*: it asks first. *No* keeps
     you connected; *Yes* ends the session and comes back to sign-in.
243. **The tray.** Close the console with X: it goes to the tray, and the balloon says *sign out or
     exit*. Right-click the tray icon: *Sign out* and *Exit* are both there and both work.

## AK. Website chat (244-251) — HANDOVER §70

After a platform restart (migration `WebsiteChat`) and `-OnlyTechnician`. It cannot go on the real
anywhereit.co.za until the platform is hosted, so every check here uses **Try it**. Two people help:
one in the console, one in the portal.

244. **Switch it on.** Portal → Website chat: tick *Show the chat bubble on our website*, check the
     hours, Save. The page says it *takes chats — somebody is signed in*. The console grows a *Website
     chat* button beside Office chat (sign out and in again if it was already open).
245. **Try it.** Press *Try it*: a sample website with a round button, bottom right, in your colour.
     Click it, fill in a name, an email (or a phone) and a question, *Start chat*.
246. **It rings everybody.** The console's Website chat window comes up by itself WITHOUT taking the
     keyboard, the button says *Website chat (1)* in red; in the portal the Chat corner turns amber,
     *Website visitor 1*, and a window pops open. Both show who, their email, the page and the question.
247. **First reply takes it.** Reply from the console. The console says *You are answering*; the portal
     says *<their name> is answering*; the visitor sees the reply and *… is chatting with you*. Reply from the
     portal too: it appears, and the chat stays the console person's.
248. **The links.** Press *App link*: the visitor gets your download page as a link they can click. Switch
     on Browser support (Portal → Browser support, Save): *Browser support link* appears in the console at
     once, and in the portal after a reload.
249. **Another page.** On the sample site click *another page*: the bubble opens again by itself with
     the whole conversation. Close the tab: the console says they have left the page.
250. **Nobody there.** Everybody signs out of the console and closes the portal. Open Try it again: the
     bubble now says *Leave us a message*. Leave one with a phone number: it becomes a General ticket
     under a new contact, and appears on the Website chat page under *Recent chats and messages*.
251. **On a phone.** Open Try it on a phone on the office Wi-Fi (the page's address, not 127.0.0.1): the
     chat takes the whole screen, and × brings the bubble back.

## What to tell me

The number of anything that is wrong, and for the console ones a screenshot — three of the four
faults in §32 were things the harnesses passed and a picture did not. `dashtabs` was 47 of 47
before you found the column rules.

If everything but G, I and J is clean, say so plainly: G, I and J are the only parts that need the
far machine, and the rest — K to Q included, apart from 115, which needs a session — can be signed off without it.
R needs the far machine too, reinstalled, with its agent service connected.


## AL. One downloadable .exe (252-258) — HANDOVER §71

After a full ship (`tools\ship-build.ps1` — step 4b makes the .exe). A customer PC WITHOUT the app on it.

252. **The page.** Open your download page (`/get/…`). *Download the app* now downloads
     `<your-code>-support.exe` — one file, no zip — and the page says to open it and choose. At the
     bottom, a small *The zip for IT staff* link still gives the zip.
253. **Open it.** Double-click the downloaded file (Windows warns until the apps are signed — say Run
     anyway). A window in your colours asks *How would you like us to help?*: *Get help now* or
     *Install …*, with *Also let them connect when I am away*, unticked.
254. **Get help now.** Press it: the usual support app opens. Request support; take the session from
     the console; the picture and control work. Close it: nothing was installed (no Program Files
     folder, no service).
255. **Install.** Run the download again and press *Install*: Windows asks for permission once. It
     says *Installed*, the tray icon and desktop shortcut appear in your branding, and the machine
     shows in the console.
256. **Unattended.** Uninstall, then install again from the download with the tick box ticked: the
     machine comes up allowing unattended access, as the old *Install-Client-Unattended.bat* did.
257. **Renamed.** Rename the downloaded file to `setup.exe` and open it: it still knows it is yours
     (Windows keeps the address it came from). Copy it to a USB stick formatted FAT32 (which drops that),
     rename it `setup.exe` there, and open it on a PC that never had it: with nothing to go on, it asks for your company's code — type it and it carries on.
258. **Already there.** On a PC with the app installed and running, open the download: it says the
     app is already running.

## AM. Signing in safely (259-268) — HANDOVER §72

After a platform restart (migration `TwoFactorSignIn`) and a full ship (`tools\ship-build.ps1`).
**The restart signs everybody out once, on purpose.** Have your phone, with Microsoft Authenticator
or Google Authenticator installed.

259. **Setting up.** Sign in to the portal. After the password it shows a QR code: scan it with the
     app, type the six digits it shows. It says *Two-factor sign-in is set up* and shows ten recovery
     codes — Copy them into your password manager, then *continue*. You are in.
260. **The console.** Open the console and sign in: after the password a window asks for the code
     (no QR code this time — you already have one). Type it: the dashboard opens as before.
261. **Each code once.** Sign out of the portal and straight back in, using the SAME code again
     within its 30 seconds: refused. Wait for the next code: accepted.
262. **A recovery code.** Sign in and, at the code step, type one of your recovery codes instead:
     you are in. Try the same recovery code again later: refused.
263. **Sign out everywhere.** With a technician signed in to the console (another PC, or the same
     one), press *Sign out* beside them on the People page: within a second their console says
     *Signed out* and goes back to its sign-in window. If they had a session open, it ends.
264. **A new password.** Reset a technician's password on the People page: their console and their
     open portal tab are both signed out (the tab on its next click).
265. **Lost phone.** Press *Reset two-factor* beside someone: their next sign-in shows a QR code
     again, and their old recovery codes no longer work. The Two-factor column says *At next sign-in*.
266. **Guessing.** Type a wrong password five times in a row: the sixth try says *Too many attempts*
     even with the right password. It lets you in again after 15 minutes.
267. **The owner portal.** Sign in at `/owner` with the admin key: the first time it shows a QR code
     to set up (a separate entry in your app); after that it asks for the key and a code.
268. **On the live server** (after `go-live.sh`): step 6 says SSH passwords were switched off (or
     tells you to add a key first), and that a test backup was made. **Copy `/etc/rsp-backup-key` off
     the server.** Make a health check report: the PDF still arrives (the service is fenced in now).

## AN. People choose their own passwords (269-276) — HANDOVER §73

After the platform deploy. **Owner → Mail must be set first**, or invitations fall back to a link
shown on the screen. Use a real mailbox you can open for the person you invite.

269. **Invite.** People → Add a person: a name, an email you can read, Technician — no password box.
     It says an invitation has gone. The row says *Invited — not set up*.
270. **The email.** It comes from your company's name, *Reply* goes to your support address, and it
     has a *Set up your account* link and the technician download link.
271. **Setting up.** Open the link on a phone or PC: choose a password (twice), scan the QR code,
     type the code, save the recovery codes, and you land on the console download page, signed in.
     Open the same link again: *already been used*.
272. **Resend.** Invite somebody else, then press *Resend invitation*: a second email; the first
     email's link now says *not valid*.
273. **Forgot password.** Sign out, *Forgot your password?* on the sign-in page, your email: an email
     with a link. Choose a new password — it still asks for your code. The old password no longer works.
     Also from the console's sign-in window: *Forgot your password?* opens the same page.
274. **Send reset link.** On the People page beside a technician: they get an email naming you.
275. **My account.** In the portal menu: change your password with the current one and a code. The
     browser stays signed in; your console (if open) is signed out and wants the new one.
276. **The command line** (for a new company's first owner): on the server,
     `sudo -u rsp /opt/rsp/RemoteSupport.Platform invite <code> <their email> "Their Name" --owner --admin`
     — they get the invitation; the link starts `https://theremotesupportprogram.com`.

## AO. Adding items on the Items page (277) — HANDOVER §59, "Items can be added"

After a platform restart. Any account will do — a technician too.

277. **Add an item.** Portal → Items → *Add an item*: type a new item and a part number, *Add item*.
     It appears in the list with that part number. Type the same words in another order: *already on
     the list — Nothing was added.* Type something close to an existing item (e.g. the same name with
     one more word): it asks, and *No — it is different* adds it. Quote the new item on a ticket: the
     part number is filled in for you.

## AP. The installer closes; the console as one .exe (278-283) — HANDOVER §74

278. **The customers' installer closes.** Download from `/get/<code>`, open it, *Install*, Yes to
     Windows. When it says *Installed*, press *Close*: the window goes. Again on another machine (or
     after uninstalling), and this time the **X**: it goes too.
279. **The console downloads as one file.** On a PC that has never had the console: open your
     technician link (`/tech/<code>`), sign in, *Download the technician console*. You get
     `<code>-console.exe`, not a zip.
280. **It sets itself up.** Open it (Windows may warn about the publisher until code signing). No
     administrator prompt; the sign-in window opens in your company's name, and there is a
     *<Company> technician console* shortcut on the desktop and in the Start menu. Sign in and work
     as usual.
281. **Next time, from the shortcut.** Close the console (tray icon → Exit), start it from the desktop
     shortcut: no warning about the publisher, straight to sign-in.
282. **Updating while it is open.** With the console open, open the downloaded file again: it says to
     Exit the console first. Exit, open the file again: it starts.
283. **The old zip consoles still work.** A console unzipped from an older download still opens as
     before; nothing changed for it.

## AQ. Typing to pick a client, the portal on a phone, the code once a week (284-292) — HANDOVER §75

After the platform deploy and `-OnlyTechnician`.

284. **Pick a client by typing.** On a ticket's Details tab, click the client box and type part of a
     name, a phone number or an email: the list narrows as you type, as on the Clients page. Pick one,
     press Link. The same on the Devices page's *Whose* box (type a contact's name — their company
     comes with them) and on the Dashboard's unlinked machines.
285. **Half-typed is refused.** On the Dashboard, type half a name and press Link without picking:
     it says *Pick one from the list* and links nothing.
286. **On your phone: the header.** Sign in to the portal on a phone. The header is one line with a
     *Menu* button; the links open under it. If somebody @mentioned you, the count is on *Menu*.
287. **On your phone: the ticket list.** Your tickets are cards on the first screen; tap anywhere on
     one to open it. *Dates* shows the date filters.
288. **On your phone: a ticket.** The next steps are full-width buttons; open one, fill it in,
     Submit. Post a comment. Add a photo from the camera. Tapping a field does not zoom the page.
289. **On your phone: Clients and Devices** read as cards, nothing to swipe sideways; link a machine
     to a client from the phone.
290. **The code once a week — browser.** Sign in to the portal with password and code. Sign out, sign
     in again: password only. In another browser (or a private window) the code is asked.
291. **The code once a week — console.** Sign in to the console with password and code. Sign out
     (header button), sign in again: password only.
292. **Signing somebody out ends it.** As a supervisor, press *Sign out* beside a technician on the
     People page. Their next sign-in asks for the code again, in the console and the browser.

## AR. Internal testing, 2 October: UAC on attended machines, waiting to be accepted, the microphone question, Info, sign-in (293-302) — HANDOVER §76

After the platform deploy, a FULL ship and a client reinstall on the customer machine (the agent,
the host and the tray app all changed). On the customer machine, unattended access OFF.

293. **An attended session runs as SYSTEM.** Connect to an attended machine; the person there clicks
     *Allow*. Tools → Info: *Host running as* starts **SYSTEM, through the background service**.
294. **UAC is visible.** In that session, open something that asks for administrator rights (e.g.
     right-click Command Prompt → Run as administrator). The UAC prompt shows on the technician's
     screen, not black, and the technician can click *Yes* on it.
295. **System → Open Task Manager** opens Task Manager on their screen; the technician can close it.
296. **DISM runs.** Tools → Scripts, batch: `DISM /online /Cleanup-image /CheckHealth`. It runs to
     the end — no *Error: 740*.
297. **Waiting is shown.** Connect to an attended machine and do NOT answer on it. The console shows
     *Waiting for <machine> to accept*, with the time left. Then click *Allow* on the machine: the
     waiting window goes and the session window opens.
298. **Nobody answers.** Connect again and let the prompt run out. The waiting window says *<machine>
     did not connect* and *Nobody at <machine> answered within 60 seconds…*. **No session window
     opens.** *Close* closes it.
299. **Declined, and cancelled.** Connect and click *Deny* on the machine: the waiting window says it
     was declined. Connect again and press *Cancel* in the waiting window: the prompt on the machine
     disappears at once.
300. **The microphone question is in the middle.** In a session, Tools → *Talk to the customer*. On
     the customer's screen a window appears in the middle: *Can your technician hear you?* with *Use
     my microphone* and *Not now*, and no Windows notification covers it. Both buttons work; the X
     is *Not now*.
301. **Tools → Info is readable**, in the dark theme and the light one: every line shows, long lines
     wrap.
302. **The sign-in window** at 125% and 150% display scaling (Settings → Display → Scale): every label
     shows whole, and *Forgot your password?* sits in the middle under the *Sign In* button.

If a session on an attended machine still says *the logged-on user* under Info, the console also
shows a yellow line *Run by the support app, not the background service…* — then the agent service
is not connected on that machine: run `tools/agentstate.ps1` on it (check 49) and note what it says.

## AS. An unattended password for a personal machine (303-310) — HANDOVER §77

After the platform deploy, a FULL ship and a client reinstall on the customer machine. On the
customer machine, unattended access ON. You need two people signed in to consoles for 306.

303. **Setting it.** Right-click the tray icon → *Unattended password…*. Try `short`: it says at least
     8 characters. Type two different ones: it says they are not the same. Type the same good one
     twice, *Save*. The chat log says a password is set; the menu now reads *Unattended password —
     set…*.
304. **Connecting needs it.** In the console, Unattended tab, Connect to that machine. The window asks
     why AND has a password box. Type a wrong one: the window comes back with your reason still in it
     and *…4 more tries…*. Type the right one: it connects without anybody at the machine clicking
     Allow.
305. **Nobody gets round it.** Sign in as the owner or a supervisor and Connect: the password is asked
     for them too.
306. **A colleague cannot slip in.** While 304's session is open, a second technician double-clicks it
     in the Sessions list: they are refused and told to use Connect. They select the machine, Connect,
     type the password: they join the same session.
307. **A restart does not ask again.** In the session, System → Restart. The console reconnects when
     the machine is back, without the password window.
308. **Five wrong.** End the session. Connect and type a wrong password five times. The fifth says it
     is now refused for 15 minutes; then the RIGHT one is refused too.
309. **Removing it.** On the machine, tray → *Unattended password — set…* → *Remove password*. The
     chat log says so. (Wait out 308's 15 minutes, or set a new password, which also lifts it.)
     Connect asks no password.
310. **What it does not touch.** Set a password again, then untick *Allow unattended access*: Connect
     asks no password, and the person at the machine is asked to Allow, as before. And with
     unattended on again, *Request Support* from the machine: the technician accepts and no password
     is asked.

## AT. Two machines on ONE job — a data transfer (311-317) — HANDOVER §78

After the platform deploy (migration `MachinesOnOneJob`) and `-OnlyTechnician`. Book one workshop
machine in for a client first, the usual way: the OLD machine, fault "data transfer to new laptop".

311. **The choice.** At *Booked in*, *Add another machine* asks first *Is it part of this job?* —
     *No — a separate job* is ticked to begin with. Tick *Yes — the same job*: the box under it now
     asks what the machine is for, and says it may be left empty. Same in the console (a dropdown).
312. **Refused, still chosen.** Choose *Yes*, add NO photos, Submit: refused for the photos, and the
     form still says *Yes* (portal and console). Nothing has been created.
313. **Booked in on the same ticket.** Add three photos of the NEW machine, its make, model, serial and
     password, Submit. No new ticket appears; the story has *Another machine on this job* with
     *Machine 2 on this job*, its details and *Photos in: 3*. The ticket number is unchanged.
314. **Shown as two machines.** Details tab: *Machine 1* and *Machine 2* cards, each with its own
     password behind *Show*. Photos tab: *As it arrived* in two groups, one per machine. Same in the
     console's ticket window.
315. **One job.** Inspect, Repairable, Solved — to be invoiced: once, for the whole job. *Add another
     machine* is no longer offered after the inspection.
316. **Leaving together.** *Machine collected* asks for at least **6** photos (3 of each of the 2
     machines). With 3 it is refused, saying so; with 6 it goes, and the story says *Both machines
     collected*. *Out for delivery* asks the same 6.
317. **Separate jobs unchanged.** On another booking, *Add another machine* with *No* still makes a
     ticket of its own, linked under *Booked in with:*, as in section AE.

## AU. The billing queue says who it took off (318-319)

After the platform deploy (no migration, no ship). Found on ticket #6: *To be invoiced* took Jason off
and the story said only *In the billing queue*, so putting him back read *reassigned from unassigned to
Jason* — true, and it looked like a fault.

318. **Taken off, by name.** On a Remote support ticket with one technician on it, press *To be
     invoiced*. The step's last line reads *Jason taken off — in the billing queue.* (with the real
     name). *Client invoiced* afterwards, with somebody back on it, says the same.
319. **Put back, saying from where.** While it is in the billing queue, put the technician back on it.
     The story reads *assigned to Jason (was in the billing queue)*, never *from unassigned*. A bench
     job nobody was on still reads *reassigned from unassigned to …*.

## AV. Tickets by email and the contact form (320-331) — HANDOVER §79

After the platform deploy (migration `TicketsByEmail`); no ship. Sign in to the portal as the owner
and open **Tickets by email**. Have a phone or a personal mailbox (Gmail is fine) to write from, and
put one test client on file first: *Test Client Ltd* with a contact whose email is that address.

320. **Our own mailbox — the one that matters.** Tick *Turn email to our support address into
     tickets*, give `support@anywhereit.co.za`, choose *We check your mailbox*, press *cPanel / most
     hosting*, type the mailbox's own password, *Save and test*. The green box says it signed in for
     reading AND for sending, how many messages the inbox holds, and that only mail from now on
     becomes tickets. If it names a certificate for another server name, put that name in and test
     again — and note here which name it was.
321. **Nothing old, nothing touched.** No tickets appear for mail already in the inbox. Open the
     mailbox in Outlook or webmail: nothing has been marked read, moved or deleted, then or later.
322. **A known person.** From the test contact's address, email support@ with a subject and a line.
     Within about a minute a General ticket opens, linked to *Test Client Ltd* and the contact, with
     the subject and text. The reply arrives in the sender's inbox **from support@anywhereit.co.za**
     (not from the platform), subject *[Ticket #N] your subject*, and **not in spam**.
323. **Reply onto the same ticket.** Reply to that auto-reply with one new line. The line appears on
     the SAME ticket in the sender's name, without the quoted history; no new ticket; no second reply.
324. **The client known, the person not.** From another address at the test client's domain (if it
     has its own domain — Gmail does not count, by design), email support@. The ticket opens NOT
     linked, with the yellow notice *… belongs to Test Client Ltd, but … is not on file*. Press *Add as
     a contact … and link this ticket*: linked, and the contact now exists with that address.
325. **A stranger.** From an address nobody has on file: the ticket opens with no client and says so.
326. **Left alone.** Turn on an out-of-office on the sending mailbox and send support@ something, so
     it answers the auto-reply (or wait for one to arrive). No ticket comes of the out-of-office, and
     the *Recent messages* list shows it *left alone* with the reason.
327. **A wrong password.** Change the password box to something wrong, *Save and test*: it says the
     server refused the username or password and that checking has stopped. Put the right one back,
     *Save and test*: working again. (cPanel must not have blocked the platform's address — if
     webmail or the test then fails for no reason, say so here.)
328. **The contact form.** Under *A contact form for your website*, tick it on, *Save*. Open the
     link it shows on a phone: fill it in with the test contact's address and send. *Thank you — we
     have opened ticket #N*, the ticket is linked, and the auto-reply arrives.
329. **On a real website.** Paste the line into a test page of anywhereit.co.za (or ask whoever looks
     after it). The form appears where it was pasted, the right height with no scroll bar inside it,
     and a message sent from it opens a ticket.
330. **The existing website form.** If anywhereit.co.za's own contact form already emails support@,
     send one through it: the ticket is for the person who filled it in (not WordPress), linked if
     their address is known, and the reply goes to them.
331. **Forwarding (only once `tickets@theremotesupportprogram.com` is set up — GO-LIVE §5b).** With a
     second test company, choose *You forward it to us*, give an address it owns, save: a confirmation
     link arrives there; opening it shows a Confirm button; Confirm. Set that address to forward to
     `tickets@theremotesupportprogram.com`; an email to it becomes that company's ticket. If it is a
     Gmail address, Gmail's confirmation code shows at the top of the company's page.

## AW. The knowledge base (332-341) — HANDOVER §80

After the platform deploy (migration `KnowledgeBase`) and `ship-build.ps1 -OnlyTechnician`. Two
people help: a technician and a supervisor (or the owner). Have one client on file with at least
one machine linked to it — *Test Client Ltd* will do.

332. **Everybody has it.** Sign in to the portal as a technician, as the office administrator and as
     the owner in turn: each menu has *Knowledge base*, it opens, and the owner's dashboard has the
     📘 Knowledge base card. In the console, the header has a *Knowledge base* button that opens it
     in the browser, signed in.
333. **Not before the work is done.** As the technician, open one of your General tickets for Test
     Client Ltd that is still open: no *Add to knowledge base*. Press *To be invoiced* and write what
     you did, one thing to a line. Now the ticket shows *Add to knowledge base* — in the portal AND in
     the console's ticket window.
334. **The write-up.** Press it. The editor opens with the ticket's subject as the title, Test Client
     Ltd as the client, *What was needed*, and what you did as numbered steps — with no time and no
     amount. Close it without saving: nothing was added to the knowledge base.
335. **Word it, add a picture, save.** Open it again. Reword the steps; add a step with *1. Step* (it
     carries on the numbering); take a screenshot (Win+Shift+S) and press Ctrl+V in the box — a
     picture tag appears; *Preview* shows the steps and the picture. *Save*: the article shows, and
     the ticket now says *Written up: …* and has a line in its story.
336. **Found without looking.** Open ANOTHER ticket of Test Client Ltd: it says *📘 Knowledge base for
     Test Client Ltd (1)*. Open the client's own page: the same. In the console, connect to the
     client's machine: the session window's status bar says it, and *Tools* has it; clicking either
     opens that client's articles in the browser.
337. **General and search.** Press *New article*, leave the client empty, write something; it is under
     *General*, and the Test Client one under *Clients → Test Client Ltd*. Type a word from inside a
     step into the search: the list narrows as you type.
338. **Two people, one article.** The technician and the supervisor open the SAME article's *Edit*.
     The supervisor changes a line and saves. The technician then saves theirs: refused, naming the
     supervisor, and the technician's words are still in the box.
339. **History.** Open *History*: every save, with who and when. *View* an older one and *Put this
     version back*: it is back, and the history has one more version, not fewer.
340. **Never a password.** In an article, type *Password: Test1234* on a line: a yellow warning appears
     under the box while typing. Save anyway: the article warns everybody who opens it, and the
     warning shows ••••••, never the password. Edit it out: the warning goes.
341. **Only a supervisor or the owner deletes.** As the technician: no *Delete* button. As the
     supervisor: *Delete*, confirm — it is gone from the list and from the ticket, which offers *Add to
     knowledge base* again.

## AX. Chat from the machine list (342)

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` and a fresh console download from
`/tech/anywhereit` — console only, no client reinstall.
Two customer machines online, so the chat box has a choice to make.

342. **Right-click, Chat.** In the console, note which machine the chat box's dropdown shows. Right-click
     the OTHER online machine in the machine list and choose *Chat* (under *Connect*): the dropdown now
     shows that machine and the cursor is in the message box — type and press Enter, and it arrives on
     that machine. Right-click an offline machine: *Chat (offline)*, greyed.

## AY. Processes and Performance without connecting (343-349) — HANDOVER §81

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136`, a fresh console download from
`/tech/anywhereit`, and the support client REINSTALLED on the test machines — until then their
right-click says *(update the support client)*. One machine with unattended access ticked, one without.

343. **Straight through, attended.** Right-click the machine WITHOUT unattended access → *Processes*. A
     window opens, fills within a couple of seconds, busiest first. At the machine: nothing appeared on
     the screen and nobody was asked anything.
344. **Unattended, with an unattended password.** On the unattended machine set an unattended password
     (tray → *Unattended password…*). Right-click → *Performance*: it opens with no password asked.
     (Connect still asks for it — check that too.)
345. **The agent's view.** On the unattended machine, *Processes*: the **User** column names SYSTEM,
     LOCAL SERVICE and the signed-in person — the agent is sending. On a machine whose agent is not
     running, those service rows have no user: the tray app is sending. Both are right.
346. **The network line.** *Performance* → Network card: *Private network · <IP> · Wi-Fi "<name>"*. On
     the machine run `ipconfig` and Settings → Network: the IP and Private/Public match. Switch the
     network to Public in Settings: within about five seconds the line turns amber, *Public network,
     sharing off*. Switch it back.
347. **One window per machine.** With *Processes* open, right-click the same machine → *Performance*:
     the SAME window switches page; no second window.
348. **It says when it stops.** With the window open, choose *Exit* in that machine's tray app: the
     window says the person closed the support app. Open the tray app again: the figures come back by
     themselves.
349. **Look only, nothing logged.** In the window there is no way to end or change anything. Afterwards
     the portal's tickets show no new ticket and no session for any of this.

## AZ. One sign-in at a time (350-356) — HANDOVER §82

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (migration `OneSignInAtATime`) and a fresh
console download from `/tech/anywhereit` on BOTH office PCs. No client reinstall. Two PCs (A and B), a
phone, and your authenticator app. Use a test technician account, not your own, so your console is
not signed out mid-morning.

350. **The same PC again.** On PC A, sign in to the console (code if asked). Exit from the tray, open it
     again and sign in: the password alone, no code — the same PC is not "somewhere else".
351. **Another PC pushes the first off.** With PC A's console open, sign in to the console on PC B with
     the same account. PC B asks for the code even if it was trusted, and says the account *is signed in
     to the console on another computer*. Type the code: within a second PC A's console shows *You were
     signed out because your account was just signed in on another computer…* and returns to its
     sign-in window.
352. **The code again on the first PC.** Sign in on PC A again: the code is asked, though PC A was
     trusted this week. (Being pushed off cancels every PC's 7-day trust.)
353. **Exit gives it up.** On whichever PC is signed in, choose *Exit* from the console's tray menu. Sign
     in on the OTHER PC: nobody is pushed off, and on a PC that was trusted since step 352 only the
     password is asked.
354. **Browsers.** Sign in to the portal on PC A's browser. Then on the phone: the code is asked, with
     *signed in on another browser*. After the code, click anything on PC A's browser: it lands on the
     sign-in page with *You were signed out because your account was just signed in on another
     computer*. Refresh: the message has gone.
355. **Console and browser together.** With the console signed in on PC A, sign in to the portal on
     the phone and on PC B's browser in turn: the console stays signed in throughout.
356. **The Tickets button.** In the console, press Tickets: the browser opens signed in, and the phone's
     portal stays signed in. Then sign the console in on the other PC: the Tickets browser, on its next
     page, lands on the sign-in page.

## BA. Seats are a price the owner agrees to (357-364) — HANDOVER §83

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (migration `SeatsAndPrice`). No console
download needed, no client reinstall. Anywhere IT is marked *not billed*, so it never asks — test this
on a second company (`create-tenant` on the server, or Owner → Tenants → *Bill them* on a test company,
never on Anywhere IT). You need its owner's login and a supervisor's.

357. **The migration.** Owner → Tenants: every company shows *Seats in package* 7 (or more, if it had
     more), a monthly price, and Anywhere IT says *Not billed*.
358. **The People page.** As the test company's owner: the heading says *N of 7 seats in your package*,
     and the text says office administrators use a seat now and what each extra one costs.
359. **The question.** With 7 people in seats, add an 8th: nobody is added; a panel says *Adding X raises
     your monthly price*, Now and After with the sum, *Accept R… a month* and *Don't do it*.
360. **Don't do it.** Press it: *not done. Nobody was added*, and Billing still shows the old price.
361. **Accept.** Add them again and press Accept: they are added and invited; Billing shows the new
     total, the package and the extra seat, and under *Price changes agreed* the date, who was added,
     old → new price, and the owner's name.
362. **A supervisor.** Sign in as a supervisor of the same company and add a 9th: no question, an error
     with the figure and *Only the owner can agree*; nobody added.
363. **The owner's own seat.** As the owner, if your row is Owner only, tick Technician and Save: the
     same question. Don't do it, so nothing changes.
364. **Removing lowers it.** Remove the 8th person: Billing drops back, with nobody asked.

## BB. Signup: country first, the terms, then the company (365-371) — HANDOVER §84

Signups are OFF on the live server, so test this on Clint's own PC: `dotnet run -c Release --project
src/RemoteSupport.Platform` with `RSP_Platform__SignupsEnabled=true` in the environment, then open
`http://localhost:8090/signup`. (Or ask Claude to start a throwaway one.)

365. **Where.** Five cards: South Africa R500, United States $49, Canada $49, United Kingdom £49, Europe
     €49, each with its extra-seat price. All but South Africa say *Coming soon — join the list*.
366. **The waiting list.** Click United Kingdom: *We are not open in the United Kingdom yet* and a short
     form. Fill it in: *we will email … as soon as we open in the United Kingdom*. As the platform owner,
     `/owner` shows it under *Waiting list*; Remove takes it off.
367. **The terms.** Click South Africa: *Our terms for South Africa*, a yellow DRAFT notice with the
     version, and the terms in a box — read section 4 (not responsible for loss of data) and 5 (POPIA).
368. **No tick, no account.** Press Continue without ticking: *Please read the terms and tick the box*.
369. **The company.** Tick and Continue: the company form, with *You accepted our terms for South
     Africa*. Fill it in and press *Create my account*: *Thanks, …! Your signup is in.*
370. **What was kept.** Approve it at `/owner`, sign in as that owner, open Billing: *Terms accepted on
     … by …* unfolds to the exact terms.
371. **Every region's terms.** Open `/terms/UK`, `/terms/EU`, `/terms/US`, `/terms/CA`: each names its
     own privacy law and governing law. **Send these to the lawyer** with the [bracketed] questions.

## BC. Times in the company's own time zone (372-376) — HANDOVER §85

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (migration `CompanyTimeZone`). No console or
client change.

372. **The live portal shows South African time.** Sign in at theremotesupportprogram.com, open People:
     your *Last sign-in* is the time on your watch, not two hours earlier. Same on a ticket's timeline.
373. **Branding.** Branding has *Time zone: South Africa (SAST)*. Change it to US Eastern and Save: People
     now shows the time six hours earlier. Change it back.
374. **An appointment.** Portal → New ticket, a General ticket with an appointment tomorrow at 09:00: the
     ticket shows 09:00. **Check any appointment booked on the live PORTAL between 1 and 5 October** — it
     now shows two hours later than intended (it was saved in UTC); correct it on the ticket.
375. **Web chat hours** (if switched on): at 08:30 South African time on a weekday the bubble takes chats;
     at 17:30 it takes messages.
376. **Signup asks it** (local test platform with signups on, as in section BB, with
     `RSP_Platform__Region=US`): the company step has a required *Time zone* list of US zones; South
     Africa's says *Time zone: South Africa (SAST)*.

## BD. Call outs reschedule; the office is told of billing (377-384) — HANDOVER §86

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (no migration), then re-download the console
from `/tech/anywhereit`. Needs a test office administrator account (Office administrator ticked only)
and a technician.

377. **Reschedule is offered.** Open a call out at *Call out booked*: beside *Work done* is
     *Reschedule*. Press it: *New date and time — it is booked for …* with the current date and time
     filled in, and *Why is it being moved?*
378. **The past and no reason are refused.** Choose yesterday: *That time has already passed*, and what
     you chose is still in the boxes. Leave the reason empty: *Say why.*
379. **It moves.** A date next week and a reason: the job stays at *Call out booked*, Details shows the
     new appointment, the story has a *Reschedule* entry with *New appointment*, *Was* and *Reason*, and
     the dashboard's *Today / This week* list follows it. Do the same once from the CONSOLE's ticket
     window (date and time pickers).
380. **Not after the visit.** Press *Work done*: *Reschedule* is no longer offered.
381. **The pop-up in the portal.** Office administrator signed in to the portal on any page; the
     technician sends a job to *To be invoiced* (or *Paid cash*): within a second a card appears top
     right — *In the billing queue*, the ticket number and subject, the stage and who sent it — and
     *Billing queue* gets an amber *1 to do*. *Open the ticket* goes to it. The technician sees nothing.
382. **The badge clears.** The office administrator presses *Client invoiced*: the badge goes out (it is
     waiting on the customer now) and nothing pops up. Paid on site — to be closed counts until closed.
383. **The pop-up in the console** (an account with Office administrator AND Technician ticked, or a
     company with no office administrator, where it goes to supervisors): a box bottom right, *In the
     billing queue*, which does not take the keyboard; the Tickets button counts it (*Tickets (1)*,
     red), its tooltip says *1 job is in the billing queue waiting for the office*. *Open the ticket*
     opens the Tickets window on the billing queue and the job.
384. **Nobody else.** A supervisor in a company WITH an office administrator sees no pop-up and no
     count for billing.

## BE. Outside help, step 1: the switches and the customer's code (385-391) — HANDOVER §87

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (migration `OutsideHelp`), re-download the
console, and reinstall the support app on one test machine. **A code lets nobody in yet** — connecting
with one is step 2 — so this is safe to try on a real customer's machine.

385. **The page.** As the owner or a supervisor, Portal → *Outside help*: the switch is OFF, every
     machine reads *As the company (off)* and *Can make a code: no*. As a technician, the menu has no
     *Outside help* and `/portal/outsiders` does not open.
386. **Hidden by default.** Right-click the support app's tray icon on the test machine: there is no
     *Let someone else connect…*.
387. **One machine on, from the console.** As a supervisor, right-click the test machine → *Outside
     help*: the top line says *Not allowed — your company has it off*; choose *On for this machine*.
     Within a few seconds the tray menu has *Let someone else connect…*. As a technician the same click
     says *Only a supervisor or the owner can let people from outside the company in.*
388. **Making a code.** Open it: the red warning first, then *Who is it for?* — *Make a code* with it
     empty says to fill it in. Type a name: a big nine-digit code in threes, *For …*, *Works once,
     until HH:mm — 10 minutes left* counting down, and the line about pressing Deny. The chat log says a
     code was made. The portal page's *Codes made* lists it as *Live until …*.
389. **Stopping it.** *Stop this code*: the page now says *Stopped — stopped at the machine*. Make
     another and leave it ten minutes: the window says it has run out; the page says *Ran out unused*.
390. **Switching off kills the code.** Make a code, then set the machine *Off for this machine* (or the
     company switch off): the open window says the code no longer works, the tray item disappears, and
     the page says *Stopped — switched off by <your name>*.
391. **The company switch.** Tick it on the page: every machine set *As the company* gets the tray item;
     one set *Off for this machine* does not. Untick it: they lose it. Put the test machine back to *As
     the company* and the company switch OFF when done.

## BF. Terms, refunds and privacy on the public page (392-395) — HANDOVER §88

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136 -LandingOnly` (the page alone) or a full
deploy. On a phone as well as a PC.

392. **The footer.** At theremotesupportprogram.com the footer names The Remote Support Program (Pty)
     Ltd, Reg. 2026/737434/07 and 19 Tide Street, Knysna, with three links: Terms and conditions,
     Refund policy, Privacy policy.
393. **Each link.** *Terms and conditions* opens the terms (version 2026-10-06) and scrolls to them;
     *Refund policy* shows the five points; *Privacy policy* opens the policy. None says DRAFT and none
     has [square brackets].
394. **Read them once, as a customer would.** Anything untrue for the business — say so; the words live
     in `src/RemoteSupport.Platform/Terms/`, and Claude regenerates the page from them.
395. **PayFast.** Send PayFast the address and tell them the links are in the footer.

## BG. Outside help, step 2: another company connects with a code (396-404) — HANDOVER §89

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (no migration), a **client reinstall** on the
test machine (host, agent and tray app all changed), and the console re-downloaded. Needs a SECOND
company on the live server with one technician — a test company, approved at `/owner` — and outside
help switched on for the test machine (section BE). Two consoles: yours (the machine's company) and the
other company's, on another PC or another Windows account.

396. **The button.** In the other company's console, under the machine list: *Connect with a code…*
     beside Connect. *Connect* with five digits says a code has nine.
397. **A code for your own machine is refused, and kept.** In YOUR console, type a code made on one of
     your own machines: *"That code is for one of your own company's computers…"*. The tray window still
     shows it counting down; it still works.
398. **The prompt.** On the test machine, make a code for "Test Visitor". In the other company's console,
     type it: they see *Waiting for … to accept*. On the test machine the prompt reads **Allow somebody
     from outside?** with their name and company, *They are NOT from <your company>*, no files or tools,
     and *If you did not give them a code, press Deny*. **Look at it at 150%: is all of it readable?**
399. **Your consoles are told.** The moment they typed it, YOUR console showed a red *Somebody from
     outside is connecting* box: who, which machine, whose code for whom, *Watch* / *End it* / *Dismiss*.
400. **Allow.** Press Allow: the box says they are connected; their session window opens with a banner
     *You are on another company's customer's computer…*; Tools has only Info; no system-action button.
     They can move the mouse and type. Copy something on the test machine: it does NOT reach them.
401. **Watch.** In your console press *Watch*: you are in the session beside them, with every tool. Pull
     a file from the test machine: you get it; they do not.
402. **They leave, it ends.** In their window press Leave/End: the session ends for you too.
403. **You end it.** A new code, a new visit, Allow; then *End it* in your box: their window says *Ended
     by <you>*. Do it once more and instead set the machine *Off for this machine*: it ends, saying so.
404. **The records.** Your portal: a ticket *Outside help: <name> (<company>)* with who, whose code, the
     Allow and the end — not assigned to them. Their portal → Sessions: a line *<machine> (<your
     company>'s customer)*. Put the test machine back to *As the company* when done.

## BH. Outside help, step 3: a guest with the guest console (405-410) — HANDOVER §90

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (no migration) **from a full build**, so the
payload carries the console .exe. Outside help on for the test machine (section BE). Use a PC that has
never had a console on it, or a fresh Windows account — the guest console must not need one.

405. **The guest window connects.** Open `https://theremotesupportprogram.com/guest`: the page, and
     *Download the guest console*. The file is `remote-support-guest.exe`; run it. It opens on **Connect
     with a code** — your name, your company, the code — and asks for NO company code and installs
     nothing. Make a code on the test machine, type it with a name: *Waiting for the person at … to
     allow you…*. **This is the check no harness does.**
406. **The prompt says "not checked".** On the test machine: *<name> (<company>, a guest — not
     checked) … They are NOT from <your company>*. Your console's red box says the same.
407. **Allow.** The guest's session window opens with only Info under Tools and the banner; they can
     move the mouse and type. Copy something on the test machine: it does not reach them.
408. **Closing ends it.** Close the guest's session window: the session ends for you too, and the guest
     window says a new code is needed.
409. **No name, no connection.** Connect with the name empty: *Type your name…*; the code still works
     afterwards.
410. **The record.** Your portal: a ticket *Outside help: <name> (<company>, a guest — not checked)*.

## BI. Outside help, step 4: a guest connects from a browser (411-416) — HANDOVER §91

After `deploy\Publish-Platform.ps1 -Server 154.65.110.136` (no migration, no ship). Outside help on
for the test machine (section BE). Try it in Chrome or Edge first, then Firefox; Safari on a Mac if one
is to hand.

411. **Both ways in.** `https://theremotesupportprogram.com/guest`: *Connect in this browser* and
     *Download the guest console*.
412. **Connect.** *Connect in this browser*: name, company, code (made on the test machine). *Waiting
     for the person at … to allow you…*; on the test machine the prompt names the guest as *not checked*.
413. **The picture.** Allow: the test machine's screen appears in the page and moves when something on
     it moves; the pointer is the machine's own. Watch it for a minute: no smearing, no freezing.
414. **The mouse and keyboard.** Click the picture, then click something on the test machine's screen:
     it lands where you clicked. Type into Notepad there, use the arrows and Backspace, right-click.
415. **Leave.** *Leave*: the session ends on the test machine, and the page asks for a new code.
416. **Another browser.** The same in Firefox. On a Mac, Safari: if the picture is slower and blockier
     it has fallen back to JPEG tiles — say so; it still works.

## BJ. Outside help: files when the customer ticks it (417-420) — HANDOVER §92

After a platform deploy from a **full** build and a **client reinstall** on the test machine (the host
changed). Use the other company's console (section BG) or the guest console (section BH).

417. **The box.** Make a code and connect from outside: the Allow prompt has *Also let them copy files
     to and from this computer*, **unticked**, and says files need the box. All of it readable at 150%,
     *press Deny* included.
418. **Without the tick.** Allow without ticking: their session window has no *Files*; your red box
     says *allowed it, without files*; the ticket says *without files*.
419. **With the tick.** A new code; tick the box, then Allow: their window has *Files* and says so;
     copy a file each way — it arrives. Your box and the ticket say files were ticked. Copy text on the
     test machine: it still does NOT reach them.
420. **The browser.** Connect at `/guest/web` and tick files: the page says files were allowed and to
     use the guest console for them.

## BK. The first copy after a fresh logon (421-422) — HANDOVER §93, BUGS #10

After a **full** ship and a **client reinstall** on the test machine (the host changed).

421. **The first copy.** Restart the test machine and log in. Before copying ANYTHING there, connect
     from the console. Now copy some text in Notepad on the test machine: it arrives on this laptop's
     clipboard — paste it here. (Before the fix, this first copy never came; the second did.)
422. **What was there before.** Copy something on the test machine, THEN connect: what was copied
     before the session must NOT appear here. Copy again: that one does.

## BL. Wake-on-LAN (423-426) — HANDOVER §94

After a platform deploy and a **full** ship, with the new client on TWO machines at the same place,
linked to the same customer. Wake-on-LAN must be on in the sleeping machine's BIOS/network card,
and it should be on a cable — Wi-Fi seldom wakes.

423. **Known.** With both machines on, right-click either in the console: no *Wake up* (they are on).
424. **Wake.** Shut one down (Start → Shut down). When it shows offline, right-click it → *Wake up*: the
     message names the other machine as the sender, and within a couple of minutes the machine starts
     and turns green.
425. **Nobody there.** Shut down both. *Wake up* on either says none of the customer's other machines
     on that network is on — nothing is sent.
426. **Another customer.** (If to hand.) A machine of a DIFFERENT customer on the same network is NOT
     used to send the wake-up: with only that one on, the answer is the same as 425.

## BM. Remote printing, both directions (427-433) — HANDOVER §95

After a **full** ship and a **client reinstall** on the test machine. This laptop needs a printer
(a real one, or "Microsoft Print to PDF" — it will then ask where to save each print).

427. **Switch it on.** In a session, Tools → *Print on my printer*: Windows' Print window asks which
     printer; choose one. The status bar says the test machine has a printer called *"<your name>'s
     printer (…)"*.
428. **It is there.** On the test machine, open Notepad → File → Print: that printer is in the list.
429. **Print to it.** Type a line, print to it: within a few seconds it comes out of YOUR printer, and the
     status bar says *Printed "…" on <your printer> (1 page)*. The ticket has the line; the test machine's
     tray app shows it too.
430. **A PDF or a Word document.** Print a several-page PDF and a Word document from the test machine:
     every page arrives, in the right order and orientation.
431. **Off.** Tools → *Print on my printer* again (untick): the printer disappears from the test machine.
     Switch it on again and END the session instead: it disappears too.
432. **Your file on their printer.** Tools → *Print a file on their printer…*: pick a PDF. It prints on
     the test machine's default printer; the status bar names that printer and the pages; the ticket says so.
433. **What will not print.** The same with an odd file (e.g. a .zip): the status bar says nothing there
     prints that kind of file and to send it as a PDF.

## BN. Several photos per quote item; knowledge base folders; link buttons (434-442) — HANDOVER §96

After a **platform deploy** (it migrates) and a **console re-download**. Use a job at Repairable or
Quote needed.

434. **Several at once, in the portal — on a phone.** On the job's Quote, type an item, then *Photos of
     the part*: pick THREE photos from the phone's gallery at once, press Add. It says *Uploading 1 of
     3…*, then *Added …, with 3 photos*; all three thumbnails are on the line.
435. **More later.** *Add photos* on that line: pick two more. The line now shows five.
436. **In the console.** Open the same kind of job in the console's ticket window: *Photos of the part…*
     picks several (Ctrl-click); Add; the line says *· 3 photos*. *Add photos* on the line adds more.
437. **The supplier gets them all.** Request the quote (desktop mail): the draft email has every photo
     attached, named *item-1-photo-1.jpg*, *item-1-photo-2.jpg*… and the text says *(5 photos attached)*.
     With Gmail or Outlook on the web: one download button per photo, named the same way.
438. **Folders.** Knowledge base → *Open General* → *New folder*: **Printers**; open it → *New folder
     inside*: **HP**. The top reads *Knowledge base › General › Printers › HP*. Do the same under a client
     (open the client's knowledge base → *New folder*).
439. **An article in a folder.** Inside HP, *New article here*: the Folder box already says HP. Save it.
     Printers shows *HP — 1 article*; a search inside Printers finds it and says *📁 Printers › HP*.
     On the article, the Folder box moves it to the top and back.
440. **Removing a folder loses nothing.** As a technician, a folder with something in it has no *Remove
     folder*; an empty one does. As a supervisor, remove Printers: HP and its article are now at General's
     top.
441. **Link buttons.** Edit an article: *Link button* → paste a very long web address, words *Download the
     driver* → Add the button; also paste another address on a line of its own. Save: the first is a
     button saying *Download the driver*, the second a button saying *Open* and the site's name; neither
     long address shows. Both open the right page in a new tab.
442. **An old quote.** A quote line made before this deploy that had one photo still shows it, and its
     request email still attaches it.

## BO. Quote requests: stock, ETA and signatures (443-449) — HANDOVER §97

After a **platform deploy** (it migrates). As the office administrator, a supervisor or the owner.

443. **The words.** Request a quote on any job: the email asks for *the delivery fee, whether each item is
     in stock, and the ETA (how soon each item can be delivered)*. With no signature set up it still ends
     *Thank you,* and your name and the company's.
444. **A signature with an image.** Portal → Suppliers → *Signatures* → *Add a signature*: name it, type
     *Kind regards* on the first line, *Add a line* with your name (bold, bigger, a colour), and upload a
     picture of your signature. *How it will look* shows the text with the picture directly below. Save.
445. **One signature: nothing asked.** Request a quote: the form says *Signed with …*. With desktop mail,
     open the draft in Outlook: after *Our reference: job #N.* comes *Kind regards*, your name in its font
     and colour, and the picture directly below — shown in the email, not as an attachment.
446. **Several: asked.** Add a second signature (an image only is fine). Request a quote: *Which
     signature?* shows both as they look; the one you used last is ticked. Choose the other one.
447. **Web mail.** With Gmail or Outlook on the web (change it under *Using … — change*), Request quote,
     then *Open the email*. In the compose window, paste (Ctrl+V) at the bottom: the signature arrives with
     its fonts, colours and picture. Send one to yourself: the picture shows in the received email.
448. **Download again.** On a job already requested, *Download the quote request email again*: it is
     signed with the signature chosen at the time, even after that signature is removed from the list.
449. **Who may.** As a technician, the Suppliers page shows the signatures but no Add, Edit or Remove.

## BP. One look: rounded, no black corners, one colour rule (450-457) — HANDOVER §98

After a **platform deploy**, re-download the console; reinstall the customer app on one test machine. Do
each check in BOTH light and dark (the console's *Light mode* / *Dark mode* button; restart the console).

450. **Sign in.** The *Sign In* button has four rounded corners and nothing black at any of them. Click
     into each box: the ring round it is the company's blue, not purple.
451. **The dashboard header.** *Dark mode*, *New ticket*, *Knowledge base*, *Office chat*, *Tickets*, *Sign
     out* all look the same — outlined, blue words. Get an @mention: *Tickets (1)* turns red like Office
     chat does; reply, and it goes back to the others' look. *Connect* has no black corners.
452. **Hover everything.** Move the mouse slowly across every button on the dashboard, the ticket window
     and the session toolbar: no corner ever shows a black mark or a sliver of another button's colour.
453. **The ticket window.** Open a workshop job at Repairable: every step button, the tabs, *Open in
     browser* and *Search* are rounded at BOTH ends. The queue's header band runs to the list's edge, and
     there are no faint vertical lines in the empty space under the last ticket. Scroll the ticket up and
     down: no stray lines are left behind.
454. **Dropdowns and dates.** The queue's *My work*, *Ask someone…* and a quote's *Type* are rounded. On a
     call out, *Reschedule*: the date opens a calendar in the console's colours (dark in dark mode); arrows
     change the month; pick a day. The time is a dropdown in quarter hours, and an appointment made at an
     odd time (e.g. 10:10 in the portal) still shows 10:10.
455. **Tick boxes and the quantity.** New ticket: the people tick boxes and *Book an appointment* are
     rounded and visible when unticked in dark mode. A quote line's quantity box takes digits only, and
     Up/Down change it.
456. **A session.** The toolbar's buttons all look the same apart from the red *Leave / End*; *Monitor* has
     a rounded box whose top edge is whole; *Control* is a rounded tick box; *Quality* is a rounded slider
     that still changes the picture. *System ▾* and *Tools ▾* open menus in the console's colours with
     rounded corners. Tools → Scripts: the output and the script box are rounded. Send a file: the
     progress bar is rounded and blue.
457. **The customer's side.** On the reinstalled machine: right-click the tray icon — the menu is in the
     app's colours. Chat with a technician: the reply box and *Send* are rounded. A technician from
     outside connects with a code: the Allow window's *Also let them copy files…* box is rounded.

## BQ. A program opened or closed shows at once, without Refresh (458) — HANDOVER §99

Added 2026-10-06. Needs a `Publish-Platform.ps1` deploy and the client reinstalled on DESKTOP-STD8M9C.

458. **DESKTOP-STD8M9C, no Refresh.** Connect; Tools → Info still says *with a GPU colour conversion*.
     Open Notepad through the session: it appears on the console within a second, without Refresh. Close
     it: it disappears within a second. Do it five times each, and once with the window maximised.

## BR. Remembered unattended passwords (459-464) — HANDOVER §100

Added 2026-10-06. Needs a `Publish-Platform.ps1` deploy and the console re-downloaded. Use a machine with
unattended access on and an unattended password set in its tray app.

459. **Asked once.** Connect: the window asks the reason and the password. Type it; the session opens.
     End it, Connect again: the window asks only the reason. Hover the machine: the tooltip says yours is
     saved.
460. **From another PC.** Sign in to the console on a second PC as the same person: Connect asks no
     password.
461. **A colleague is still asked.** Signed in as somebody who has never typed it: Connect asks for it.
462. **Changed at the machine.** In the tray app, change the unattended password. Connect as the first
     person: the password box is back, and the old password is refused.
463. **Forget.** Right-click the machine → *Forget saved password*. Connect: the password box is back.
464. **The owner's list.** As a supervisor or the owner, right-click the machine → *Saved passwords*: the
     people who typed it, by name and date — never the password. *Clear* one: that person is asked again.
     *Clear everybody's* asks first, then everybody is asked again. As a technician, *Saved passwords* is
     not on the menu.

## BS. Signing up for the free version (465-468) — HANDOVER §102

Added 2026-10-06. Needs a `Publish-Platform.ps1` deploy, and signups switched on (on a test server, or
briefly on the live one).

465. **Free or full.** `/signup` → South Africa: two cards, *The full version* and *The free version*.
     *Change* goes back.
466. **The free terms.** Choose the free version: the terms start with *The free version* ahead of
     section 1, and no page of terms anywhere says DRAFT (also `/terms/UK`).
467. **The free form.** Tick, Continue: company name and contact number say *(optional)*; there are no
     colour boxes. Leave both blank and create: the thank-you names you.
468. **Approved, it is free.** Owner → Approvals → approve it: on Owner → Tenants it is Active, with no
     trial date.

## BT. Installing a free computer (469-474) — HANDOVER §103

Added 2026-10-06. Needs a FULL ship, a `Publish-Platform.ps1` deploy, and a free company (sign one up,
section BS, or `set-brand <slug> plan free` on a test company).

469. **Add a computer.** On a test PC, download the free company's installer and run it: one window, *Add
     this computer*, no *Get help now*. Type a password twice, *Add this computer*, Yes to Windows: it says
     *Done*. No tray icon, no desktop shortcut, nothing in Startup. The console lists the PC as unattended.
470. **Connect.** From the console, Connect: the password is asked, once. Connect again: not asked.
471. **The second, then a third.** Add a second PC: it works. On a third, the window says the free version
     allows 2 computers, and nothing is installed (no service in services.msc).
472. **Change the password.** On the first PC run the installer again: *Change this computer's password*.
     Set a new one. In the console, Connect: the password is asked again, and only the new one works.
473. **Opened by hand.** Open Program Files\…\RemoteSupport.Client.exe on a free PC: a message says how
     the computer is reached; no tray app starts. Sign out and in: nothing of ours appears.
474. **A wrong password typed twice** at install (two different): the window says so and installs nothing.

## BU. "Being controlled remotely" (475-478) — HANDOVER §104

Added 2026-10-06. Needs a FULL ship, a `Publish-Platform.ps1` deploy, and the free computer of section BT.

475. **On their screen.** Connect from the console to the free computer: at the top centre of EVERY
     monitor there, an amber bar says *Being controlled remotely by* and your name.
476. **Not on yours.** In the console's picture of that computer there is no bar, and you can click
     whatever is under it (on the computer itself, clicking the bar clicks what is under it).
477. **Gone when you are.** End the session: the bar goes within a second or two. Connect again: it comes
     back. (Its re-wording for a second person is proved by noticeprobe; the free version is one person.)
478. **A full company's machine** shows no bar during a session (its tray app says so instead).

## BV. The free version's portal and console (479-484) — HANDOVER §105

Added 2026-10-06. Needs a `Publish-Platform.ps1` deploy and the console re-downloaded, signed in as a free
company's owner.

479. **The portal.** Sign in: you land on *Your computers*. The menu is Computers, Downloads, My account.
     Type `/portal/tickets` in the address bar: back on Your computers, saying it is the full version's.
480. **Downloads** offers the console and the link for your computers, nothing for customers.
481. **Remove in the portal.** Remove one computer: it goes, the page says so, removals left drops to 1.
     Switch light/dark straight after: you stay on the page.
482. **The console.** No Tickets, Office chat, Knowledge base, customer chat, calls or tabs; the list is
     headed *Your computers (N of 2 …)*.
483. **Connect** asks only the password (no reason), once; Tools is Files and the two printing items.
484. **Remove in the console.** Right-click → *Remove this computer…*: asked first, then gone. A third in a
     month is refused, saying when the next can be.

## BW. Removing a machine; Uninstall from the tray app (485-491) — HANDOVER §106

Added 2026-10-06. Needs a `Publish-Platform.ps1` deploy, a FULL ship and the test machines reinstalled (an
older install cannot uninstall itself).

485. **Only a supervisor or the owner.** As a technician: no *Remove* on right-click, none on the portal's
     Devices page. As the owner: both there.
486. **A reason.** Console → right-click a machine → *Remove this computer…*: Remove with nothing chosen says
     *Choose why*; Other with no note says to write one.
487. **Online, it uninstalls itself.** Remove an ONLINE test machine as *Replaced*: it leaves the list on every
     console; on the machine, within a minute, the service, the Program Files folder, the shortcuts and the
     tray icon are gone (and its monthly health check, if it had one).
488. **History kept.** Its tickets are still there (Tickets → search the machine name). Devices → *Removed
     machines* shows it, with when, who, *Replaced* and the note.
489. **Offline.** Remove an OFFLINE machine from the portal (*Private device*): it leaves the list. Switch it on:
     it does not come back, and the program is still installed on it.
490. **Uninstall from the tray app.** On a test machine: tray icon → *Uninstall…* → Yes → Yes to Windows. The
     program goes. In the portal, *Removed machines* says *Uninstalled at the machine*, by the person there.
491. **Changing their mind.** *Uninstall…* → Yes, then **No** to Windows: nothing happens and the machine stays
     on the list.

## BX. Upgrading from the free version (492-496) — HANDOVER §107

Added 2026-10-06. Needs a `Publish-Platform.ps1` deploy and the console re-downloaded, with a free company
that has a computer or two (sections BS–BT).

492. **The way there.** The free portal's menu has *Upgrade*; the free console's header has an *Upgrade*
     button that opens it in the browser, already signed in.
493. **What it says.** Free now, R500 a month after a 14-day free trial; the full terms (no *free version*
     section). Upgrade without ticking: refused, nothing changes.
494. **Upgrade.** Tick, *Upgrade*: you land on Billing, which says the trial has started and shows the full
     terms you accepted. Owner → Tenants: the company is in a 14-day trial.
495. **Nothing lost.** With the console left open: it becomes the full console straight away (Tickets, the
     tabs, customer chat). Both computers are still there, and Connect to one needs no password typed.
496. **Now full.** Install the support program on a third computer: it is let on. Reinstalling one of the
     first two gives it the tray app.

## BY. Recurring tasks (497-505) — HANDOVER §108

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy (migration `RecurringTasks`) and the console
re-downloaded. No client reinstall. Times are the company's own (Branding → time zone).

497. **The way there.** Portal → Tickets has a *Recurring tasks* button; New ticket has *Recurring task…* as
     a sixth type, which opens the set-up form. In the console, New ticket → Type of job → *Recurring task*.
498. **Set one up for today.** Weekly, today's day ticked, yourself ticked, after 06:00: the list says
     *Today's ticket is open*, and the ticket is in your queue at once — *Recurring task*, stage *Due*,
     already assigned, with *Done* and *To be invoiced*.
499. **Set one up for later.** Monthly, *Last day of the month*, given to the office administrator: the
     list says when the first opens. The office administrator is offered in *Who it goes to*; a workshop
     job's people are not (console: tick a box, switch to Workshop — the office's box goes).
500. **The morning.** Leave one due tomorrow. Before 06:00 there is no ticket; just after, it is in the
     queue and the console's ticket list shows it without a refresh.
501. **Not done in time.** Leave a daily one's ticket open: the next morning a new one opens anyway, and
     yesterday's says *Overdue* in the list and on the ticket.
502. **Done and To be invoiced.** *Done* closes it. *To be invoiced* asks what was done, the time and the
     amount, and goes to the billing queue. The office administrator, if it is theirs, can press both.
503. **Change it.** On the task (portal, or the console's *Change in the browser*): add a day and save. A
     colleague who is not on it, signed in as a technician, cannot open it at all.
504. **Cancel it.** *Cancel this recurring task* (portal) or *Cancel the task…* (console ticket): it asks
     why. Afterwards the list shows it *Cancelled*; a ticket of it that was open stays open and says so in
     its story; no more are opened.
505. **Your own type.** *New type…* → "Printer toner": it is offered next time. Typing "toner printer"
     later uses the same type rather than making a second.

## BZ. A ding for a chat message, a ring for somebody waiting (506-512) — HANDOVER §109

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy (no migration), the console re-downloaded and the
client reinstalled. Turn the PC's volume up first: these are meant to be soft.

506. **A support request rings.** From a customer machine, Request Support. Every signed-in console rings at
     once, softly, and again every four seconds. Accept it on one console: every console stops.
507. **Dismiss and cancel.** Two requests at once; Dismiss both on the pop-up: this console stops, a second
     console still rings. Another request, then the customer cancels it: it stops.
508. **A chat message dings.** The customer types in the tray app's chat: the console dings (two notes),
     once even if they send three lines quickly. The technician replies: the customer's PC dings. Sign the
     console out and in again: the old messages are shown without a ding.
509. **Office chat.** A colleague sends you a message, and one to Everyone: each dings in the console and in an
     open portal page (click the portal page once first — a browser stays silent until somebody has).
     Your own messages never ding.
510. **Website chat.** A visitor starts a chat on the website: the console and the portal ring until somebody
     replies. Once you are answering, each of their lines dings for you and not for your colleagues. The
     visitor hears a ding when you reply.
511. **Mute.** The bell (🔔) left of New ticket in the console: click it, it shows 🔕 and a call or message
     plays nothing, but the call still shows and flashes. The tray menu says *Unmute sounds*; it brings the
     ring back while a call still waits. Close and reopen the console: still muted. The portal's own
     *Mute sounds* button does the same for that browser only.
512. **Nothing extra on the free version.** A free company's console has no bell and no *Mute sounds* on its
     tray menu.

## CA. Windows shortcuts go to their machine (513-518) — HANDOVER §110

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy, then the console re-downloaded. Nothing changes on the customer's machine.
Connect to a customer machine and click once on the picture before each check.

513. **The Windows key.** Press it: the Start menu opens on THEIR machine, not yours. Press it again: it
     closes.
514. **Win shortcuts.** Win+E opens Explorer over there; Win+R opens Run over there; Win+D shows their
     desktop. Nothing opens on your machine.
515. **Alt+Tab and Task Manager.** Hold Alt and tap Tab: their task switcher, not yours, and letting go of Alt
     picks the window over there. Ctrl+Shift+Esc opens Task Manager on their machine.
516. **Copy and paste unchanged.** Ctrl+C a file or some text over there, Ctrl+V it on your machine, and the
     other way round: works exactly as before.
517. **Your shortcuts come back.** Click the Chat window or the toolbar, or another program: the Windows key
     opens YOUR Start menu again. Click the picture: it is theirs again.
518. **Nothing stuck.** Hold Ctrl on the picture and click another program while still holding it; let go.
     Back on the picture, type a letter over there: a plain letter, not a Ctrl shortcut.

## CB. A reinstalled machine waits for the office (519-524) — HANDOVER §111

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy (migration `ReinstallConfirm`), the console
re-downloaded, and — for the tray app's wording and the free installer's message — a FULL ship and a client
reinstall. A machine that only reconnects or is upgraded in place is NOT a reinstall; these need an
**uninstall first** (Uninstall… in the tray app, or Apps & features), then the download run again.

519. **The mark.** On a test machine with unattended access ticked, uninstall the client, then install it
     again. In the console the machine comes back as *"… — reinstalled, not confirmed"* with an amber dot,
     still on the Unattended tab; the Unattended tab says *(1 not ready)*. Its customer, history and
     password are still there.
520. **Held back.** Connect to it from the console: the person at the machine is ASKED (the Allow prompt),
     and no unattended password is asked for. The tray app's note says the IT company must confirm it.
521. **Only the office.** Signed in as a plain technician, right-click it: the top item is greyed and reads
     *"Reinstalled — a supervisor or the owner confirms it"*. On the portal's Devices page the machine has a
     *reinstalled — not confirmed* pill and no Confirm button.
522. **Confirm.** As a supervisor or the owner, right-click → *Confirm it is the same machine…* → OK. The
     mark and the amber dot go, connecting is unattended again (the password once, if it has one), and the
     tray app's note is back to normal. Doing it from the portal's Devices page (*Confirm same machine*)
     instead does the same, and the page says so.
523. **Upgrades are not reinstalls.** Run the same download over an installed machine WITHOUT uninstalling
     first: nothing to confirm.
524. **The free version.** On a free company's computer, uninstall and run the free download again: the
     installer finishes by saying to confirm the computer first. The console refuses to connect until
     right-click → *Confirm it is the same machine…* (or *It is mine* on Your computers); then it connects.

## CC. End-to-end encrypted sessions (525-534) — HANDOVER §112

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy (no migration), the console re-downloaded, and a FULL
ship with a client reinstall — until a machine has the new support app its sessions say *Not end-to-end
encrypted*, which is checks 531's point. Look at the session window's status bar, beside the connection pill.

525. **The lock.** Connect to a reinstalled test machine (unattended, or by a call). Within a few seconds the
     status bar says **🔒 End-to-end encrypted** in green. The picture, mouse, keyboard, clipboard, Files and
     Scripts all work exactly as before.
526. **First time, then remembered.** Close the session and connect again: still green, no question asked.
     On a second technician's PC (a different Windows user) the first connection is green too.
527. **A reinstall is asked about.** Uninstall the support app on the test machine, install it again, confirm
     it (§111, check 522), connect: the session window shows **Identity changed — waiting for you** and a
     question *"Is this the same computer?"*, with the picture blank. **No** ends the session. Connect again
     and answer **Yes**: green, and the next connection asks nothing.
528. **The ticket still says what was done.** In a session, run a two-line script whose first line is a
     comment. The ticket says *"Ran PowerShell script: <the comment>"* once — never the second line.
529. **A colleague on an old console.** While you are connected, have a colleague join from a console that has
     NOT been re-downloaded. Within about five seconds your badge changes to **Not end-to-end encrypted** and
     their picture works. (Re-download theirs afterwards.)
530. **Attended and the agent.** A machine whose agent hosts as SYSTEM shows green. If a machine shows
     **🔒 Encrypted — identity not checked** (amber), its session is hosted by the tray app as the person — an
     older agent; note which machine.
531. **An old machine.** Connect to a machine that has NOT been reinstalled: **Not end-to-end encrypted**, and
     everything works as before.
532. **Outside help.** A visitor from another company connects with a code: their window shows green too.
533. **A guest in a browser** (`/guest/web` on the LIVE https address — a plain http LAN address cannot use the
     browser's cryptography): under the status line, **🔒 End-to-end encrypted — our server cannot see this
     session.** The picture, mouse and keys work.
534. **Browser support.** A customer shares their screen from `/help/{slug}` on the live https address: the
     console's badge says **🔒 Encrypted — identity not checked** (a browser has no installed identity), and the
     picture and talking work.

## CD. Branding reaches installed machines (535-541) — HANDOVER §113

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy (no migration) and a FULL ship with a client reinstall —
only a machine with the new support app takes a new look. Use a test company, or put your own branding back
afterwards. Reinstall the test machine BEFORE changing anything, so the change is the only thing that moves.

535. **The logo and colours arrive by themselves.** With the test machine on, change the company's logo and
     accent colour on Portal → Branding and Save. Within about 10 seconds — if nobody has its window open — the
     tray icon on the test machine disappears and comes back with the new logo. Open the tray app: the new logo
     and colours.
536. **Never in front of anybody.** Open the tray app's window on the test machine, then save another change in
     the portal. The window stays as it is, in the old look, for as long as it is open. Close it with the X: a few
     seconds later the tray icon comes back in the new look. Do the same while a support session is running: no
     restart until the session ends.
537. **A machine that was off.** Switch the test machine off, change the welcome text, switch it on: after logon
     the tray app shows the new words (it may restart once, quietly, a few seconds after it appears).
538. **The consent prompt.** Change the colours, then connect to the test machine by a support request: the
     Allow prompt on its screen is in the new colours with the new logo.
539. **The desktop shortcut.** The shortcut's icon is the new logo — at once, or after Windows next redraws its
     icons (sign out and in). Note which; this one is not proven.
540. **Nothing else moved.** On the test machine, `C:\Program Files\<company>\Remote Support\branding\brand.json`
     still holds the OLD look (it is the install's record), and beside it `branding\live\look.json` holds the
     new one. Then uninstall from the tray app: the shortcuts and the folder go, as before.
541. **A reinstall starts from the download.** Run the current download over the test machine: `branding\live`
     is gone afterwards, and the look is the one the download carried.

## CE. The console's windows carry the company's icon (542-545) — HANDOVER §114

Added 2026-10-07. Needs a `Publish-Platform.ps1` deploy (no migration), then re-download the console from
`/tech/{slug}` and open the download once. No client reinstall.

542. **The sign-in window.** Start the console: the sign-in window's taskbar button shows the company's logo, not a
     blank or Windows' default icon.
543. **Every window.** Signed in, open a session, Tickets, office chat, and right-click a machine → Processes. Each
     one's title bar and taskbar button (or Alt+Tab) shows the logo. Connect to an unattended machine with a
     password: the password window's taskbar button shows it too.
544. **The shortcuts.** The Start menu and desktop shortcuts *"<company> technician console"* show the logo.
545. **Pinned.** Pin the console to the taskbar from the running window (unpin an older pin first): the pinned
     button keeps the logo after the console is closed.

## CF. Five seats, ten at most (546-550) — HANDOVER §116

Added 2026-10-08. Needs a `Publish-Platform.ps1` deploy (migration `FiveSeatPackage`). No console re-download,
no client reinstall. Use a TEST company that is billed (Owner → Tenants, *Bill them*), not Anywhere IT.

546. **The package is five.** Portal → People: the pill reads *"N of 5 seats in your package · 10 at most"*. Owner →
     Tenants shows 5 in *Seats in package* for every company.
547. **The sixth costs extra.** With five people in seats, the owner adds a sixth: the price question shows R500 → R800.
     Accept adds them; Billing shows R800 and names the six, without the owner (if the owner has only Owner ticked).
548. **One person, three roles, one seat.** Tick Technician, Supervisor and Office administrator on one person: no
     price question, and the seat count does not move.
549. **Ten is the most.** Add people (accepting each price) until the pill says 10: the Add a person form is replaced
     by *"All 10 seats are in use, which is the most a company can have"*. On the owner's own row, tick Technician
     beside Owner: refused with the same reason, and no price question.
550. **Anywhere IT too.** On the live portal, Anywhere IT's People page also says *"10 at most"*. If it already has
     more than ten people, nobody is removed — only adding is refused.

## CG. Several owners (551-555) — HANDOVER §117

Added 2026-10-08. Needs a `Publish-Platform.ps1` deploy (no migration). Use a TEST company.

551. **Only an owner sees it.** Signed in as an owner, Portal → People: Add a person has an *Owner* box. Signed in as a
     supervisor: no Owner box anywhere, and owners' rows cannot be changed.
552. **A second owner costs a seat.** As the owner (with only Owner ticked), add somebody with only *Owner* ticked: the
     price question appears if the package is full, and once added the seat count goes up by one.
553. **There is always one.** With one owner, their Owner box is greyed and ticked, and there is no Remove beside them.
     With two, each can untick the other's Owner box or remove them — but not the last.
554. **The new owner can do owner things.** Sign in as the second owner: they can accept a price, change an owner's
     row and see the Owner box.
555. **A supervisor is refused.** As a supervisor, try to remove an owner (or post the form another way): refused,
     *"Only an owner…"*.

## CH. Offline, reconnecting, parent jobs, unlinking, the request (556-571) — HANDOVER §122

Added 2026-10-09. Needs a `Publish-Platform.ps1` deploy (no migration), then re-download the console; 566-571 also a FULL
ship and a client reinstall. Use a TEST
company and a TEST machine.

556. **Off reads offline within a minute.** With the console open, pull the power (or the network cable) on a test
     machine that shows online. Within about a minute it shows offline, and Connect says it is offline.
557. **Asleep too.** Put a test machine to sleep: offline within about a minute; wake it: online again on its own.
558. **The first connection asks why.** Connect to a machine with no recent ticket: the *Why are you connecting?*
     window opens as before, and the ticket's subject is what you wrote.
559. **Reconnecting asks nothing.** End that session and connect again straight away: the session starts with no
     window, and the console's ticket list still shows ONE ticket for it, with the first reason as its subject.
560. **After a long session too.** Stay connected for over 30 minutes, end it, reconnect within a few minutes:
     nothing asked, same ticket. Wait more than 30 minutes after ending and connect: asked why, and a new ticket.
561. **A machine with an unattended password, as a colleague.** Inside the 30 minutes, a colleague who has never
     typed that machine's password connects: the window asks for the PASSWORD only (no reason box), with no red
     complaint; the right password gets them in on the same ticket.
562. **Linking leaves the child open.** On a remote support ticket press *Link to parent ticket* and choose one of
     the client's open tickets: it stays at *Remote session*, says *Part of: #N …* with *Not part of it*, and no
     longer offers *Link to parent ticket*. The same in the console's ticket window.
563. **The job waits for it.** On the parent press *Done* (or any step that closes it): a pop-up says *"This job
     cannot be closed yet: #… is part of it and still open. Close it first."*, and the job stays open. In the
     console: the same words in a message box.
564. **Close the child, then the job.** Close the child through its own steps; the parent's *Part of this job*
     line shows it closed; now the parent closes.
565. **Unlinking.** *Not part of it* on the child (portal and console) takes it out, and *Link to parent ticket*
     comes back. Right-click a linked machine in the console: *Unlink from {customer}…* asks and unlinks; on the
     portal's Devices page, *Unlink* beside Set does the same.
566. **A subject and a description.** On a test machine with the NEW tray app, Request Support: the window asks your
     name, *What do you need help with? A few words* (try a long sentence — it stops at 80 characters) and *Tell us
     more (optional)*. Leave the subject empty: it says so and stays open. Fill both and send.
567. **The picture is ticked, and shown.** Before the window, leave an error message on screen. The window shows a
     small picture of the whole screen (both monitors if there are two) under *Include a picture of my screen*,
     ticked. The request window itself is NOT in the picture.
568. **The technician's end.** The alert shows the subject large. The ticket's subject is the subject, *Fault
     reported* is the description, and under it *Their screen — when they asked for help* shows the picture; it opens
     full size. In the console's ticket window, *🖼 See their screen* opens it in the browser.
569. **Unticked, nothing is sent.** Request again from another machine with the tick off: no picture on its ticket.
570. **Asking again.** Within 30 minutes, ask again from the first machine: the SAME ticket, a line *Customer
     requested support again: …*, and a second picture.
571. **An old tray app.** From a machine NOT yet reinstalled, request support with a long description: the ticket's
     subject is the first line cut short with "…", and the whole text is under *Fault reported*.

## CI. Updating the client from a ticket (572-581) — HANDOVER §123

Added 2026-10-09. Needs a `Publish-Platform.ps1` deploy (migration `ClientUpdates`), then re-download the console. No
ship. Tickets by email must be ON with a real mailbox (check 320 of section AV) — use Anywhere IT's own, and send the
updates to a mailbox of YOURS standing in for the client.

572. **Not set up, said so.** On a company with Tickets by email off, open a ticket: under the comment box it says to
     set up Tickets by email (with a link for the owner or a supervisor; "ask the owner" for a technician), and there
     is no tick box.
573. **The tick box.** With it on, open a ticket linked to a client contact whose address is yours: under the comment
     box, *Also email this to the client*, unticked. Tick it: the contact is chosen, the client's other people are in
     the list, and *Another address…* shows a box to type in.
574. **What arrives.** Write an internal comment first, unticked. Then post another, ticked. One email arrives, from the
     support address, subject *[Ticket #N] subject*, with your logo, *"<your name> has an update on ticket #N"*, that
     comment, and *Your original request* with the ticket's fault — and NOT the internal comment.
575. **The ticket says so.** Under the comment: *✉ Emailed to … by <you>, <time>*, and *Email it again…*.
576. **A comment already posted.** On the internal comment, press *Email to the client…*, choose *Another address…*,
     type a second address of yours, Send: it arrives there with THAT comment and the fault only — not the first one.
577. **A wrong address.** Tick, type `not an address`, Post: refused, the comment is NOT posted, and your words are
     still in the box.
578. **The reply comes back.** Reply to the email from your mailbox: within a minute or two it is on the SAME ticket as
     the customer's words, *✉ Their reply, by email*, with the history under it cut off.
579. **The people on it are flagged.** Everyone on the ticket — and whoever sent the last update — sees the *waiting on
     you* badge (portal and console); opening the ticket says *"<name> replied by email"* and the badge clears.
580. **The console.** In the console's ticket window: the same tick box and chooser under the comment box, the same
     *✉ Emailed to …* lines, *Email to the client…* on a comment, and the *replied by email* banner.
581. **Signup.** On a test signup (full version), the details step asks *Ticket email address (optional)*; once
     approved, Portal → Tickets by email opens with it filled in and switched off.

## CJ. Job types in colour, and the menu as tabs (582-585) — HANDOVER §124

Added 2026-10-09. Needs a `Publish-Platform.ps1` deploy (no migration), then re-download the console.

582. **Portal, New ticket.** Each job type is outlined in its own colour (General grey, Remote support blue, Walk in
     green, Workshop orange, Call out purple, Recurring task teal); pressing one fills it with a tick and only it.
583. **Console, New ticket.** The same six coloured buttons in one row across the top; pressing one fills it, and the
     right-hand side changes to that type (Workshop asks for the machine, Call out the address).
584. **The menu as tabs.** Every portal page: the menu items are rounded buttons and the page you are on is filled —
     Tickets on a ticket, Billing queue on the billing queue, nothing on a page not in the menu. Light and dark.
585. **On a phone.** Menu: full-width rounded rows, the current page filled; New ticket's coloured buttons wrap and
     nothing scrolls sideways.

## CK. A new machine's first hello (586-587) — HANDOVER §127

Added 2026-10-12. Needs a `Publish-Platform.ps1` deploy (no migration); nothing on the machine changes.

586. **A first install.** Install the customer app on a machine that has NEVER been installed (or after an uninstall
     and Remove). In the console, the machine shows the person's Windows user name, and right-click *Wake up* is
     offered once it is switched off (it has its network card). On the server, `journalctl -u remotesupport-platform` around the install
     has no `persist device` warning and no `duplicate key`; it may say *"waits for its other half's hello"*.
587. **A first FREE install.** Install the free version on a new machine with a password: the console sees it
     unattended WITH a password, and connecting asks for that password, straight away — no restart of anything.

## CL. Damage reports, files on comments, going back, boxes that grow (588-599) — HANDOVER §128

Added 2026-10-13. Needs a `Publish-Platform.ps1` deploy (migration `CommentFilesAndGoingBack`), then re-download the
console. No client reinstall. For 591-592 and 595, Tickets by email must be set up (Portal → Tickets by email).

588. **The boxes grow.** Portal and console: type ten lines into the comment box — it grows a line at a time and all
     ten can be read. Keep going past fifteen: it stops growing and a scroll bar appears. The same in a step's form
     (Inspection findings, a Reason) and the console's New ticket description.
589. **Damage report needed is there.** A workshop job at *Not repairable*: beside *Invoice inspection fee* and *Do not
     invoice* is *Damage report needed*. It is NOT offered after a rejected quote.
590. **Its form.** Comments (optional), *Attach the damage report*, time spent, amount, and *Email the damage report to
     the client* — ticked, the ticket's contact chosen. Submit with no report attached: *"Attach the damage report."*
591. **Submitted.** Attach a PDF, fill in the time and amount, Submit: the job goes to *To be invoiced* with the time and
     amount; the step shows the report as a link that opens the PDF. The client receives ONE email saying *"… has sent
     the damage report"*, your comments, the fault, the PDF attached — and NOT the time or the amount.
592. **Files on a comment, portal.** On any ticket, *Attach files or photos*: pick two phone photos and a Word file.
     Each shows with its size and an ×; × takes one off. Post with no words: the comment shows the photos as pictures
     and the Word file as a link (it downloads). Tick *Also email this to the client*: the photos arrive attached.
593. **Files on a comment, console.** The same from the console's ticket window: *📎 Attach files or photos…*, the picked
     files listed with ×, Post. Each file is a button under the comment that opens it in the browser.
594. **Too large to email.** Attach files over 15 MB together and tick the email: refused, *nothing* posted, words and
     files still there.
595. **Back, by mistake.** Press *Solved — to be invoiced* on a repairable job by mistake. As the technician, *↶ Back to
     Repairable* is offered; give a reason: the job is at Repairable again, the amount and time are gone from Details,
     and the story keeps the step struck through with *"Gone back over by …"*, under a *"↶ Back to Repairable"* entry
     with the reason.
596. **Back again.** Press Back again: one step further (Inspected). At Booked in there is no Back.
597. **Warned, not stopped.** Office: *Client invoiced* with a number, then *Back*: the form warns that the invoice was
     issued and to credit it; after going back the invoice number is cleared. After a damage report was emailed, Back
     warns that the client was emailed.
598. **A closed job.** As a technician, a closed job offers no Back. As a supervisor or the owner, *↶ Back to …* is the
     only button, warns *"This reopens a closed job"*, and reopens it.
599. **Who may.** A technician NOT on the job is offered no Back; a call out that became a workshop job never goes back
     to being a call out.

## CM. The customer's name remembered; a picture of what is open (600-605) — HANDOVER §129

Added 2026-10-13. Needs a `Publish-Platform.ps1` deploy (no migration), then **reinstall the client** on the test
machine from its `/download/...` link — it changes the tray app.

600. **The name comes back.** Request Support, type a name that is NOT the Windows username, send it, cancel the call.
     Request again: the name is already there. Exit the tray app, start it again (or sign out of Windows and back in):
     still there.
601. **Only what was sent.** Change the name, then press Cancel on the request window. Request again: the LAST SENT
     name is there, not the one cancelled.
602. **The desktop case.** Open Explorer on a folder, then Show desktop (Win+D) and double-click the app's desktop
     shortcut, then Request Support. The preview is the desktop, and beside it, in amber: *"Only your desktop is
     showing. Open what you need help with, then take a new picture."*
603. **Take a new picture.** Press it: the request window and the tray window disappear and a bar sits at the top of
     the screen. Click Explorer on the taskbar to bring it back, then *Take the picture*: the request window returns
     with Explorer in the preview, and the words say *"Taken just now — this is what will be sent."* Send it: the
     ticket's picture shows Explorer, and NOT the bar.
604. **Cancel on the bar.** Press Take a new picture, then Cancel on the bar: the window comes back with the picture it
     had.
605. **From the tray menu.** With Explorer open (not minimised), right-click the tray icon → Request Support: the
     preview shows Explorer straight away, with no amber words.

## CN. Editing a ticket, sorting by date, the Unassigned tile (606-615) — HANDOVER §130

Added 2026-10-13. Needs a `Publish-Platform.ps1` deploy (no migration), then re-download the console. No client reinstall.

606. **The Unassigned tile.** On the dashboard, note the Unassigned number and click it: the list shows exactly that many.
     Jobs in the billing queue or with a supervisor are NOT in it — they are under Show → Billing queue.
607. **Change a subject.** Open any ticket → *✎ Edit* (beside the title) → change the subject → *Save changes*. The title
     changes, and the story has a *Details changed* entry with the new subject and what it was.
608. **As the office.** Signed in as an office administrator, the same works.
609. **Empty subject.** Empty the subject and save: refused, *"A ticket needs a subject"*, nothing changed, and the
     description you typed in the same save is still in its box.
610. **Change the appointment.** On a call out, move the appointment to tomorrow: the dashboard's appointments follow it, and
     the story says the new time and the old. A time already gone is refused.
611. **Wrong type of job.** Make a General ticket a Call out: it is now at *Call out booked*, the story says *Category changed*
     with where it was. *Back* is not offered across it.
612. **To Workshop.** Choose Workshop on a ticket with nobody on it: refused. Put a technician on it, choose Workshop: it says
     three photos are needed — add them with the picker in the card, choose Workshop again, save: Booked in.
613. **In the console.** The ticket window → *✎ Edit the ticket* → the same card. Change the type to Call out: the warning
     shows and *No appointment* is ticked; untick it, choose a date, save. *Saved.* and the ticket redraws as a call out.
614. **Sort, portal.** Tickets → *Sort by* → *Oldest first*: one list, oldest at the top, an Opened column and a Client column.
     Leave the page and come back: still Oldest first. *Customer* puts the grouped view back.
615. **Sort, console.** *Sort: Appointment, soonest first*: the date column reads Appointment and the soonest is at the top.
     Close the Tickets window and open it again: still sorted that way.
616. **Back after collection (§131).** A workshop job at *Invoiced — awaiting payment*: *Machine collected*, then *Back to To
     be invoiced* (no warning that the machine comes back), *Client invoiced* again, *Payment received — close ticket*: the job
     is CLOSED, not at Awaiting collection.

## CO. Platform admins (617-624) — HANDOVER §132

Added 2026-10-13. Needs a `Publish-Platform.ps1` deploy (migration `PlatformAdmins`). No ship, no console re-download.
Do these on the live server, with a test company you can suspend.

617. **The first Owner.** `/owner/login` → *Use the admin key instead* → key and code → **Admins** → add yourself as Owner. The
     email arrives; the link → a password → scan the QR → the code: you land on Approvals, signed in by name.
618. **Once a week.** Sign out, sign in with email and password: the code is asked. Sign out and in again: it is NOT asked.
     A different browser asks it.
619. **A Viewer.** Add a second address of yours as Viewer, set it up, sign in with it in another browser: every company is
     there, nothing can be changed, and *Admins* has no Add form.
620. **A Manager.** Make that admin a Manager: their other browser is signed out at once. Signed in again they can change a
     company's seats, but still not add an admin.
621. **Suspend.** With the test company's console open and one of its machines online: suspend it. The console says the
     account has been suspended and closes; the machine goes offline in the list; its portal page goes to sign-in; the
     company's owner gets the *suspended* email.
622. **Active again.** Make it Active: within a minute the machine is online by itself, the console can sign in, and the
     owner gets the *active again* email.
623. **Email the owners.** *Email the owners* on a company → send: it arrives signed with your name; replying reaches you.
624. **The log.** *Log* shows every one of the above, by name, newest first.

## CP. The dashboard (625-630) — HANDOVER §133

Added 2026-10-13. Needs the same `Publish-Platform.ps1` deploy as CO (migration `ControlServers` as well). Nothing in
Cloudflare until there is a second server.

625. **The dashboard.** Sign in at `/owner`: one card for the live server — South Africa, 4 cores, about 7.8 GB memory, the
     database says PostgreSQL — with five meters. After ten minutes the processor meter is a real average, not 0%.
626. **The level.** On a quiet live server the card is green, *OK*, and the advice says *Room to spare*.
627. **Headcount.** The Companies table: each company's people, how many are technicians, console users and machines match
     its People and Devices pages.
628. **Pending.** With a signup waiting, the dashboard says so above the servers, and the link opens Approvals.
629. **Servers page.** *Servers* lists the live server as *this server*. A Manager sees it but cannot add one.
630. **The email.** Servers → set the live server's capacity to 100 → Save. Within ten minutes every Full admin and Owner gets
     *"… is at capacity"*. Set it back to 0: the log says it is back to OK.

## CQ. PayFast, stage A (631-638) — HANDOVER §134

Added 2026-10-13. Needs a `Publish-Platform.ps1` deploy (migration `Payfast`). No ship, no console re-download. All of it
is on PayFast's SANDBOX — no real money moves.

631. **Not set up.** After the deploy, `/owner` → *PayFast*: *Not set up*.
632. **The sandbox account.** `deploy\Set-PayfastKeys.ps1` → merchant ID and key, then the passphrase, then your ssh key's
     passphrase: *Done: the SANDBOX account is on the server and PayFast accepts it.* The PayFast page now says SANDBOX and
     your sandbox merchant ID — never the key or passphrase.
633. **The API.** *Test PayFast's API*: *accepts this server (PayFast API)*.
634. **A test checkout.** **PASSED 2026-10-13.** *A sandbox test checkout* → a test company → *Start the test checkout*: PayFast's sandbox opens on
     its own. Pay with the sandbox's test card. You come back to the PayFast page.
635. **The notice.** **PASSED 2026-10-13** (`rsp-c9212753…`).
     Within a minute (refresh): the subscription is *Active*, and a payment of R0.00 is listed, *Acted on: Yes*.
     *Notices refused* is EMPTY — a genuine notice there is a fault. In the sandbox's own *ITN* page, the notice went to
     `/api/payfast/notify` and was answered 200.
636. **The first charge.** The next day: a R5.00 payment for the same subscription, *Acted on: Yes*, *Last paid* moved, and
     *Next* a month on. (PayFast runs subscriptions monthly at the most often, so the second charge is a month later.)
     **NOT SEEN, 2026-10-15**: `rsp-c9212753…` still *Active*, *Next* 2026-10-14, no R5.00 payment. *Check with PayFast* now
     also says PayFast's own next charge date: a month on = PayFast charged and the notice went missing (look at the
     sandbox's ITN log); still 2026-10-14 = *"That date has passed and PayFast has not charged it"* — the sandbox never ran it.
     **ANSWERED 2026-10-15: the second** — *"PayFast's next charge is 2026-10-14. That date has passed and PayFast has not
     charged it."* PayFast's SANDBOX does not run a subscription's scheduled charge; nothing was lost on our side. The
     scheduled charge itself can only be seen on the live account; a renewal's handling is proved by `payfastcheck` and by
     650 (a charge pressed in the sandbox dashboard). Expect 656's reminders on any sandbox card subscription for the same reason.
637. **Cancelled at PayFast.** In the sandbox, cancel a second test subscription (one PAID with the test card): within a
     minute the PayFast page shows it *Cancelled*, and *Notices refused* stays empty. **First run, 2026-10-13: FAILED** — the
     notice was refused *"PayFast did not confirm it (INVALID)"*; fixed the same day (§134). After the fix is deployed: press
     **Check with PayFast** on `rsp-66d5d0540a7662334a53` — it says PayFast has it CANCELLED and puts ours right — then cancel
     another paid test subscription to see the notice itself accepted. **PASSED after the fix, 2026-10-13** (`rsp-197600b7…`:
     Cancelled, its CANCELLED notice recorded under the SAME PayFast id as the payment that started it, 3447285 — the cause, seen).
638. **Not from PayFast.** From this PC, `curl -X POST https://theremotesupportprogram.com/api/payfast/notify -d "m_payment_id=x"`
     answers *refused: not from a PayFast address*, and it is listed under *Notices refused*.

## CR. PayFast, stage B: signing up and paying (639-646) — HANDOVER §135

Added 2026-10-13. Needs a `Publish-Platform.ps1` deploy (no migration; it rewrites Caddy for the preview). Still the
SANDBOX — no money moves. Use an email address you have not signed up with before.

639. **The preview.** `/owner` → *Preview* → *Turn the preview on*. Open `/pricing` in that browser: the real pricing page,
     not the holding page. On your phone (or a private window) `/pricing` is still the holding page.
640. **Pricing.** It says the card is taken at signup and nothing is charged for 14 days, and *No VAT is charged*.
641. **Sign up.** **PASSED 2026-10-27 in the sandbox** — Clint: *"from start free trial, right through to setting up colours and what categories, all working fine."* In the preview browser, *Start 14-day free trial* → South Africa → Full → the terms → a test company. PayFast's
     sandbox opens, asking R0.00 now and R500.00 a month from 14 days on. Pay with the test card.
642. **Switched on by itself.** **PASSED 2026-10-27** (the same walk: signed in, Branding and Job types set up). Back on *Waiting for PayFast to confirm your card…*; within seconds it changes to *Your card is
     in, and your account is ready* without refreshing. The email *"…: your account is ready"* arrives; *Sign in* works.
643. **Billing.** **PASSED 2026-10-27 in the sandbox** — Clint: *"This all works."* In that company's portal, Billing: *By card, through PayFast*, R500 a month, next on the date 14 days on;
     no *Add your card*. `/owner/payfast` lists the subscription, *Why* = signup, Active.
644. **Not finished at PayFast.** **PASSED 2026-10-27 in the sandbox** — Clint: *"This all works."* Sign up a second test company and press *Cancel* on PayFast's page: back on *Your card was
     not added*. `/owner/approvals` shows it *Waiting for the card at PayFast* with no Approve. *Add your card* → PayFast
     again → pay: it switches on as in 642.
645. **The free version.** Sign up for the free version with another address: *We'll email you as soon as your account is
     approved*. Approve it on Approvals: the email *"…: your account is approved"* arrives, with the sign-in address.
646. **Preview off.** *Turn the preview off*: `/pricing` is the holding page again in that browser.

## CS. PayFast, stage C: the amount kept true (647-651) — HANDOVER §136

Added 2026-10-14. Needs a `Publish-Platform.ps1` deploy (migration `PayfastAmounts`). Sandbox only. Use the test company
from section CR that pays by card.

647. **More people.** In that company's portal, People: add people until there are six in seats, accepting the new price.
     Billing says R800 a month by card, with no warning. `/owner/payfast` shows its *Each charge* as R800.00, and in the
     sandbox dashboard the subscription's amount is R800.00.
648. **Fewer.** Remove one: within a minute Billing and the sandbox both say R500 again.
649. **The log.** `/owner` → Log: *"Monthly amount R500 → R800 from the next charge"* and back, by PayFast.
650. **A payment.** In the sandbox dashboard, charge that subscription now (Subscriptions → the subscription → charge): our
     email *"…: payment received, R500"* arrives with PayFast's reference and *No VAT is charged*, and Billing lists it.
651. **Nothing stuck.** Billing never shows *"PayFast has not yet taken the new amount"* for more than an hour; if it does,
     its words say why.

## CT. PayFast, stage D: a payment that did not come, and cancelling (652-658) — HANDOVER §137

Added 2026-10-14. Needs a `Publish-Platform.ps1` deploy (migration `PayfastLateAndEnd`). Sandbox only. The sweep runs within
a minute of the server starting, then hourly — after a step that should send an email, allow up to an hour.

652. **Cancel on Billing.** **PASSED 2026-10-27 in the sandbox** — Clint: *"This all works."* On a test company paying by card: Billing → *Cancel the subscription*. Without the tick nothing
     happens; ticked, it says the service carries on to the day before the next charge, the email *"…: subscription
     cancelled"* arrives, and in the sandbox dashboard the subscription is cancelled.
653. **Changed my mind.** **PASSED 2026-10-27 in the sandbox** — Clint: *"This all works."* *Add your card* on the same Billing page: PayFast's checkout asks R0.00 now and the first charge on
     the date the cancelled one would have been.
654. **All companies.** **PASSED 2026-10-27 in the sandbox** — Clint: *"This all works."* `/owner/tenants` shows *Card* under that company — and *Cancelled, ends …* under one that cancelled.
655. **Cancelled at PayFast.** **PASSED 2026-10-27 in the sandbox** — Clint: *"This all works."* Cancel another test company's subscription in the sandbox dashboard: within a minute its
     Billing says it is cancelled, and the email arrives.
656. **A failed payment (if the sandbox can make one).** In the sandbox dashboard, edit a test subscription's next run date to
     two days ago, or fail its charge: within the hour the owner gets *"…: your payment did not go through"* with a link to
     change the card, and Billing says the payment has not come.
657. **Moved on.** Press *Check with PayFast* on that subscription after charging it in the sandbox: no reminder follows.
658. **The end.** A company cancelled with its last day past is Closed within the hour, and *"…: your service has ended"*
     arrives.

## CU. The real website (659-664) — HANDOVER §138

Added 2026-10-14. Needs a `Publish-Platform.ps1` deploy (no migration). Turn the preview on at `/owner` → *Preview* first.

659. **Still hidden.** On a phone (not the preview browser): `/remote-support`, `/ticketing`, `/about` and `/privacy` all show the
     holding page.
660. **Read it.** In the preview browser, read every page from the menu and the footer, as a customer would. Mark every sentence
     that is wrong or that you would say differently — `docs/WEBSITE.md` lists the ones to look at first.
661. **The calculator.** Pricing: move *People* from 5 to 7 — R1,100 a month. Switch to the UK: £49, then £89 at seven.
662. **On a phone.** The preview on a phone (turn it on there too): every page fits the screen with no sideways scrolling, and the
     footer's three columns stack.
663. **Every link.** In the preview browser, click every link in the menu and the footer: each opens a page — none says *not
     found*. (Link previews in WhatsApp and search results come only after go-live: until then robots.txt asks search engines
     to stay out, on purpose.)
664. **Keywords.** Look up the words in `docs/WEBSITE.md` in Google Keyword Planner (South Africa) and send back the ones people
     search.

## CV. Reviews (665-669) — HANDOVER §139

Added 2026-10-14. Needs a `Publish-Platform.ps1` deploy (migration `CompanyReviews`). A company approved more than a week ago is
asked at once.

665. **Asked.** Sign in to the portal as the owner of a full-version company older than a week: a banner asks for a review, with
     *Write a review* and *Later* — no *Don't ask again* yet. A technician of the same company sees no banner.
666. **Write it.** *Write a review*: try sending it without a company name — refused. Fill it in with stars, a few lines, your
     company's website, the tick: *"Thank you! Your review is in."* The banner is gone. The platform's Full admins get an email.
667. **Not yet public.** In the preview browser, the home page has no reviews.
668. **Approve.** `/owner` → *Reviews*: it is there, waiting. *Publish on the website*: the home page (preview browser) now shows
     it with its stars, and the company name links to its website.
669. **Take it down.** *Take it down*: gone from the home page; the owner's review page says it was not published.

## CW. The website in light and dark, screenshots, the cookie line (670-676) — HANDOVER §140

Added 2026-10-14. Needs a `Publish-Platform.ps1` deploy (no migration), then the console re-downloaded for 676. Use the preview
browser (`/owner` → *Preview*).

670. **Your computer's choice.** With Windows set to light, open the home page in the preview browser: it is light. Set Windows to
     dark and open it in a private window: dark.
671. **The switch.** Top right, *☀ Light mode* / *☾ Dark mode*: the page and every picture change at once. Go to another page and
     come back tomorrow: it remembered.
672. **The tour.** Home → *See it for yourself*: five tabs, each a different window, with a few lines under it.
673. **Full size.** Click any picture: it opens over the page, full size; click or press Esc to close it.
674. **On a phone.** The home page and Remote support on a phone: the pictures fit, nothing scrolls sideways; the switch is in the
     Menu.
675. **The privacy policy.** Footer → *Privacy policy*: dated 14 October 2026, and *Visiting this website* names the cookies and
     says no analytics, trackers or advertising.
676. **The console's machine list** (after the re-download, on a laptop at 150% or 125% scaling): machine names, RAM and disk are
     not cut off with "…". Make the window narrow: the columns shrink together and each customer's ˄ fold arrow stays visible.

## CX. Everything on .NET 10 (677-683) — HANDOVER §141

Added 2026-10-14. Needs a `Publish-Platform.ps1` deploy (no migration), then the console re-downloaded and the client
reinstalled. Use a **Windows 10** machine (DESKTOP-OPHQIDH or DESKTOP-2N6FVGT, both 22H2) — that is the point of it.

677. **An old client still connects.** **PASSED 2026-10-14** — Clint connected to both test machines after the deploy, still on the .NET 8 apps: *"still working properly."* BEFORE reinstalling anything, on DESKTOP-2N6FVGT (still the .NET 8 tray app): it shows
     online in the console after the deploy, and a session to it shows the screen and takes the mouse.
678. **The console.** **PASSED 2026-10-15.** Re-download it from `/tech/anywhereit`, sign in (the code), the machine list appears.
679. **The new client installs on Windows 10.** **PASSED 2026-10-15.** On DESKTOP-OPHQIDH, download and install from `/download/...`: the window closes
     after a good install, the tray icon appears, and `tools\agentstate.ps1` says the agent service is running and connected.
680. **A session.** **PASSED 2026-10-15.** Connect: the picture appears, the mouse and keyboard work, and the Info tab names the hardware H.264 encoder.
681. **The secure desktop.** **PASSED 2026-10-15.** Ctrl+Alt+Del from the toolbar, then a UAC prompt (run something as administrator over there): both
     show and both can be used, and the session is still there 30 seconds later (§54).
682. **Copy and paste.** **PASSED 2026-10-15.** Text both ways; a file from here pasted on their desktop, and a file copied there pasted here.
683. **The rest at a glance.** **PASSED 2026-10-15** — Clint: *"all working."* Files tool opens on their desktop; Processes/Performance by right-click; Chat both ways.

## CY. A picture that sharpens and stays sharp (684-690) — HANDOVER §142

Added 2026-10-15. Needs a `Publish-Platform.ps1` deploy (no migration) FIRST, then the client reinstalled on **DESKTOP-STD8M9C** —
the machine it was reported on (Quick Sync with a colour conversion, 1366x768). No console re-download.

684. **Connecting.** Connect to DESKTOP-STD8M9C with a still desktop. Within about two seconds the picture is sharp — small text in
     the taskbar and a File Explorer window readable — not blocky and slowly clearing.
685. **It stays sharp.** Leave it still for two minutes, glancing now and then: it never goes soft again for a moment (that was the
     key frame every 24 frames).
686. **After a change.** Open a window (File Explorer, Settings) over there: it is a little soft as it appears and sharp within
     about two seconds.
687. **A blinking cursor costs nothing.** Click into a text box over there and leave the cursor blinking for a minute: the Info tab's
     *Frames sent to sharpen a still screen* does not keep climbing (refresh Info by moving the quality slider).
688. **Moving pictures still flow.** Scroll a long web page, then play a video over there: smooth as before, no worse than it was.
689. **A slow link.** From a phone hotspot or another weak connection, connect and scroll: if the picture breaks up, it is whole
     again within a second or two, not a minute.
690. **Another machine.** The same 684–686 on DESKTOP-OPHQIDH (hardware encoder taking the frame directly) after its reinstall.

## CZ. Two machines at once, and copy on one, paste on the other (691-697) — HANDOVER §143

Added 2026-10-15. Needs a `Publish-Platform.ps1` deploy (no migration), then the console re-downloaded. No client reinstall. Two
customer machines online (DESKTOP-OPHQIDH and DESKTOP-2N6FVGT), one session window on each monitor.

691. **Two at once.** Connect to both machines: both windows work, side by side.
692. **A third is refused.** With both open, Connect to a third machine (or accept a support request from one): the console says
     *"You are already connected to 2 machines (…). 2 at a time is the most — close one of those sessions first."* — and the third
     machine's person is NOT asked to allow anything, and no ticket appears for it.
693. **Going back is never refused.** With both open, Connect again on one of the two: its window comes forward, no refusal.
694. **Text between them.** Copy a line of text on machine A, click into B's window, paste in Notepad over there: it arrives.
     Then the other way round.
695. **Files between them.** Copy two files on A's desktop, click into B's window, paste on B's desktop: both arrive and open.
     Then a folder from B to A.
696. **An early paste is told to wait.** Copy a large-ish folder on A (10–40 MB) and click into B's window AT ONCE: B's status bar
     says the files from A are still arriving and not to paste yet; a moment later they are sent to B by themselves, and paste.
697. **After closing one.** Close one of the two windows; connecting to a third machine now works.

## DA. Copy on one machine, paste on the other — straight between them (698-706) — HANDOVER §144

Added 2026-10-15. Needs a `Publish-Platform.ps1` deploy (no migration), the console re-downloaded, and a **FULL ship with the
client reinstalled on both machines** — the hosts change. Two customer machines online (DESKTOP-OPHQIDH and DESKTOP-2N6FVGT), one
session window on each monitor. With both machines on the new app, checks 695 and 696 above go straight across as described here.

698. **Held, not fetched.** Copy two files on A's desktop. A's status bar says *"they copied 2 items — click into B's window and
     paste, or click here to bring them to this PC"*, and nothing new appears in your `Downloads\Remote Support`.
699. **Straight across.** Click into B's window, paste on B's desktop: both files arrive and open. B's status bar said *"coming
     straight here — don't paste yet"*, then *"… on B's clipboard — paste it now"*; A's said *"sent … straight to B"*.
700. **Your line is left out.** Copy a 40 MB folder on A and paste it on B while watching Task Manager → Performance → your
     network: no 40 MB down and up on your PC.
701. **Told on both machines.** The tray app's log on A says the files were copied *"straight to another computer"*, on B *"sent
     … straight from another computer"*; A's ticket names B, B's ticket names A.
702. **An early paste.** Copy a folder on A, click into B and press Ctrl+V at once: nothing pastes (never B's own old files), and
     a moment later Ctrl+V gives the folder.
703. **Over 50 MB.** Copy something over 50 MB on A and click into B: B's status bar offers *"click here to send it straight to B"*;
     nothing moves until you click; then it arrives.
704. **Bring them to this PC.** Copy on A and click A's status-bar offer: they come to your `Downloads\Remote Support` and paste on
     your own PC.
705. **Closed half-way.** Start a big copy (a few hundred MB) A → B and close B's window part way: A's status bar says it did not
     finish; on B, `%TEMP%\RemoteSupport\paste` holds no half-written copy.
706. **An older machine.** With one of the two machines still on the old app, a copy goes through your PC as before, and the
     status bar says *"(through this PC — X's app is older)"*.

## DB. Handing a website chat over (707-713) — HANDOVER §145

Added 2026-10-15. Needs a `Publish-Platform.ps1` deploy (no migration), then the console re-downloaded. No client reinstall. Website
chat switched on (Portal → Website chat, within its hours), the bubble on a page you can open (*Try it* will do), and two people
signed in: a technician at the console (Sue) and a supervisor or the owner in the portal (Sam). Sounds on in both.

707. **Give away a waiting chat.** Start a chat from the bubble. Both ring. In Sue's console press **Hand over…** → the menu shows
     the people signed in, Sue marked *(you)*; choose Sam. Both rings stop; Sam's portal window comes up with one ding and *you are
     answering*; the visitor sees *"Sam … will be chatting with you."*
708. **The one answering hands it on.** In Sam's window press **Hand over…**, choose Sue, press **Hand over**. Sue's console comes
     up with a ding and a tray balloon *"Sam … handed you the chat with …"*; the visitor sees *"Sue … has taken over this chat."*
709. **The dings follow it.** The visitor writes: Sue's console dings, Sam's portal does not.
710. **Not someone else's.** Sign in a second technician (Bob). On Sue's chat, Bob has no Hand over button; Sam (supervisor) does.
711. **Only people who are here.** With everybody but Sue signed out, Hand over says *"Nobody else is signed in right now."*
712. **The record.** Portal → Website chat → the chat's lines: both hand-over notes are there, in order, without a name.
713. **On the real website.** On Anywhere I.T's own site with the bubble pasted in: 707–709 again from a phone as the visitor.

## DC. Pilot companies, and an owner login that oversees but does not work tickets (714-722) — HANDOVER §146

Added 2026-10-15. **First, on Anywhere I.T's People page: anybody who shows only *Owner* and works tickets gets Supervisor
ticked too.** Then a `Publish-Platform.ps1` deploy (migration `PilotCompanies`). No ship, no console re-download.

714. **Make a link.** Owner → Pilots → company name and their email → Make the link. The link is shown once with a Copy button; the
     list shows it as *Waiting* until a date 30 days on.
715. **It works while signup is closed.** Open the link in a private window: the pilot terms come first (*Your pilot agreement*:
     five seats free for as long as you are with us, R300 for each beyond five, invoiced); tick, Continue; the company and email are
     filled in. Finish: *Welcome, …! Your account is ready.* — no approval, no card.
716. **Ready at once.** Sign in with it straight away: two-factor is set up, the portal opens, Billing says **R0** a month with the
     five pilot seats free and *we invoice* seats beyond five; the accepted terms are on Billing, word for word.
717. **Once only.** Open the same link again: *already been used*. Owner → Pilots shows it *Used* with the company's name; All
     companies shows *Pilot · R0 / month (we invoice)*.
718. **The sixth seat.** As the pilot's owner, add people until there are six in seats: at the sixth the owner is asked to agree
     *R0 → R300*; agree. Billing shows R300 a month and the agreement; All companies shows *Pilot · R300 / month (we invoice)*.
     Remove one: back to R0.
719. **A cancelled link.** Make another link, Cancel it on Owner → Pilots, open it: *This link no longer works.*
720. **The owner-only login.** On any company, add a person with only *Owner* ticked and sign in as them. Tickets: every ticket is
     there, no *New ticket*. Open one: the comment box works (try an @mention), and there is no Edit the ticket, no stage buttons, no
     client box, no people, no photo upload, no *Email to the client*.
721. **...and the office still works.** On the same ticket, the technician and the office administrator see everything they did
     before; a person with *Owner* and *Supervisor* ticked can still move, edit and close it.
722. **...and website chat.** With website chat on, start a chat from the bubble while only the owner-only login is signed in to
     the portal: the bubble takes a message (nobody here to answer) rather than ringing; with a technician signed in too, it rings
     for the technician only, and the owner-only login sees the chat with no reply box.

## DD. Real version numbers - auto-update, stage A (723-727) - HANDOVER §147

Added 2026-10-16. A `Publish-Platform.ps1` deploy (migration `AppVersions`), then re-download the console. No client reinstall.

723. **The server says its version.** Owner -> Dashboard: the server card ends *Apps version 2026.10.x.y* with today's date, and how
     many machines are on it and older. Every machine installed before 16 October counts as older.
724. **The console says its own.** After re-downloading, the console's title ends *- version 2026.10.x.y*, the same as 723.
725. **Machines say theirs.** The console's machine list has a Version column. Machines installed before 16 October read
     *1.0 (old)*; on the portal's Devices page the same machines show *1.0 (old)* in amber, the tooltip naming the background
     service and the tray app.
726. **A reinstall shows the new one.** Reinstall the client on one machine from a download made after the deploy: its Version
     becomes the date of the build in both places, not amber, and the dashboard's *on it* count goes up by one.
727. **It survives being off.** Switch that machine off, restart nothing: its row (offline) still shows its version; after a
     platform restart it still does.

## DE. Auto-update: every app keeps itself current (728-739) — HANDOVER §148

Added 2026-10-16. A SIGNED `Publish-Platform.ps1` deploy (migrations `AppVersions` and `AutoUpdate`), the console re-downloaded. From 731
on it needs a SECOND signed deploy later (any change will do: every build has a new version), so the machines have something newer to
take. DESKTOP-OPHQIDH and DESKTOP-2N6FVGT are the machines; both are Anywhere I.T's, so they are in the first ring.

728. **The release is offered to us first.** Owner → Updates: *Release 2026.10.x.y*, *Offered to: our own company only*, not paused; every
     machine counted under *Need a reinstall*. Run `tools\updatetrust` once on this PC after the signed build: it now proves the newer /
     same / older checks too (no NOTE line).
729. **Update by hand, once.** Connect to DESKTOP-OPHQIDH → Tools → Scripts → **Update this machine**: the box fills with a script naming our
     server. Run: *Release …, signed by us*, *Every file checked*, the session drops, the machine comes back within a minute. Its Version
     shows the release in both places, *update by hand once* is gone, and Owner → Updates lists *Updated* for it.
730. **The same on DESKTOP-2N6FVGT**, leaving its tray app running: it restarts into the new version by itself within a minute or two of the
     swap, as long as no window of it is open (the console's Version tooltip shows both halves the same).
731. **The next deploy updates them by itself.** Make the second signed deploy. With nobody connected, within about 15 minutes each machine
     shows *Offered → Downloading → Downloaded, waiting → Installing → Updated* on Owner → Updates, and the new version in the console.
     Nothing appeared on either screen except the tray icon going away and coming back.
732. **It waits for a session.** Before 731's deploy, stay connected to one machine: it stops at *Downloaded, waiting* and installs only about
     ten minutes after you disconnect.
733. **The console updates.** After the second deploy the console's header shows **Update ready**; click it, Yes: it closes, says *Updating
     the console to version …* for a few seconds, and opens its sign-in window in the new version (the title says so).
734. **A console started later updates before signing in.** On another PC with the old console installed, open it: *Updating…* first, then
     the sign-in window, in the new version.
735. **Other companies wait.** A pilot's or another company's machine (one that has had its update by hand) is NOT offered it while the
     release is at our own company. Owner → Updates → **Offer it to the pilots**, then **to everybody**: each is offered it within a minute.
736. **Pause.** Owner → Updates → Pause: nothing new is offered (a machine that comes online stays on its version); Resume: it is.
737. **A rollback (the one that matters).** On DESKTOP-2N6FVGT, the moment Owner → Updates says it is *Installing*, block its agent as
     administrator: `New-NetFirewallRule -DisplayName "rsp block" -Direction Outbound -Action Block -Program "<install folder>\RemoteSupport.Agent.exe"`.
     Wait six minutes, then `Remove-NetFirewallRule -DisplayName "rsp block"`. It reports **Rolled back** (*the new agent did not connect
     within 5 minutes*), runs the OLD version, the release is **paused automatically**, and every Full admin and Owner got an email. Resume
     afterwards: that machine is never offered that version again. (`update\apply.log` in its install folder tells the story.)
738. **The health check follows.** On a machine with the health check on, after an update: the toolkit's installed scripts are the new ones
     within a few minutes (the agent reinstalls them, keeping the mail settings), and the next monthly run works.
739. **A refused update says why.** (Optional, needs an unsigned build on a test server.) A release that is not ours shows *Refused* on
     Owner → Updates with the reason, and nothing on the machine changed.

## DF. The Allow prompt comes to the front (740-743) — HANDOVER §149

Added 2026-10-16. A signed `Publish-Platform.ps1` (no migration); the machine must have the new host — it updates itself if it has
had its update by hand (§148), otherwise Scripts → *Update this machine* or a reinstall. Attended access, so the customer is asked.

740. **Behind a program they just clicked.** On DESKTOP-OPHQIDH (unattended OFF), open a browser full screen and click into it. Connect
     from the console: the Allow prompt appears IN FRONT of the browser, with the keyboard (Enter allows), not behind it.
741. **Over an always-on-top window.** Open Task Manager there and tick Options → *Always on top*, drag it over the middle of the
     screen, click into it. Connect: the prompt is in front of Task Manager.
742. **Something opens while it waits.** Connect, and while the prompt is counting down, open Task Manager (always on top) over it: within
     a second the prompt is back in front, and whatever they were typing in keeps the keyboard.
743. **Never missed.** If on any machine the prompt does NOT come to the front, its taskbar button must be flashing orange — note the
     Windows version, it means Windows refused the foreground there.

## DG. The name given at a support request names the machine (744-750) — HANDOVER §150

Added 2026-10-18. A signed `Publish-Platform.ps1` (migration `RequestedByName`), then the console updates itself or is
re-downloaded. No client reinstall.

744. **Past requests, at deploy.** Before deploying, note an unlinked machine on the console that has asked for help before (its Who
     is a Windows name like *user*). After the deploy, its Who is the name typed at its last support request, **in grey italic**;
     hover: *"… is the name they gave when they asked for help … — not checked."*
745. **A new request.** On an unlinked machine, Request Support with a new name. Within a second, without refreshing, the console's
     Who changes to that name, and the portal's Devices page shows it (italic, Windows' sign-in under it).
746. **The latest wins.** Request Support again from the same machine with a different name: Who follows.
747. **Linking suggests.** Right-click it → *Link to a customer…*: the window says *They gave the name …*; anybody on file by that
     name is at the top, with their company; *Not linked to anybody* is still the chosen row. Pick one, Save: Who becomes the
     contact, no longer italic.
748. **A new contact.** On another such machine: Link → *+ Add {name} as a new contact…* → choose the customer → Save. The contact
     is on that client's page and the machine is under that customer with that person's name. Doing it again with the same name at
     the same customer does not make a second contact.
749. **The portal does the same.** Devices → a machine with a given name: a button per person on file by that name; *Add {name} as a
     contact at* → type the client → *Add and link*. The message says *… linked to {name} at {client}.* and the console moves it.
750. **Unlinking brings it back.** Unlink a machine that had given a name: Who goes back to that name in grey italic, not to the
     Windows name.

## DH. Tickets by email: Sign in with Microsoft (751-760) — HANDOVER §151

Added 2026-10-19. A signed `Publish-Platform.ps1` (migration `MicrosoftSignIn`), then `docs/MICROSOFT-365.md` steps 1–4 (the
app registered in Entra, its secret put on by `deploy\Set-MicrosoftApp.ps1`). No console re-download, no client reinstall. Needs a
Microsoft 365 mailbox with IMAP and Authenticated SMTP on (MICROSOFT-365.md step 7) — `support@` and a person `admin@` in it.
Sign in to the portal as the owner and open **Tickets by email**.

751. **Offered.** *Microsoft 365 — sign in with Microsoft* can be chosen (not *Not available on this server yet*). In a browser,
     `https://theremotesupportprogram.com/.well-known/microsoft-identity-association.json` shows the app's client ID.
752. **Sign in.** Choose it: above the button a yellow *Before you sign in* note says to tick IMAP and Authenticated SMTP for the
     mailbox. Type the support address, press **Sign in with Microsoft**. Microsoft's page names *The Remote Support
     Program*, asks which account (the support address filled in), and lists ONLY: sign you in, read your profile, maintain access,
     *read and write access to mailboxes via IMAP* and *send emails from mailboxes using SMTP AUTH* — nothing about other people's
     mail. Note whether it says *unverified*, and whether it asked for an administrator. Accept: back on our page, green, *Signed in
     with Microsoft as support@…*, the test saying it read the inbox AND signed in for sending, and the status line *on — signed in
     with Microsoft as …*.
753. **An email in, the reply out.** From a personal address, email support@. Within about a minute a ticket opens; the reply
     arrives **from support@ the 365 mailbox** (not from the platform), not in spam. In Outlook on the web, the message in support@'s
     inbox is still **unread** and in the inbox.
754. **The next day.** Leave it overnight (Microsoft's access tokens last about an hour): the next morning an email still becomes a
     ticket — the sign-in renews itself.
755. **Revoked.** In Entra → Users → support@ → **Revoke sessions**. Within a few minutes (the next look at the mailbox) the page's
     status line says *stopped — Microsoft no longer accepts the sign-in; sign in again*, with the reason in words, and the button reads
     *Sign in with Microsoft again*. Email support@ meanwhile: no ticket yet. Sign in again: the email sent while it was stopped becomes
     a ticket, and nothing older does.
756. **IMAP switched off.** In the Microsoft 365 admin centre untick IMAP for support@, then *Save and test*: it says Microsoft
     *would not let … read …*, that IMAP is switched off, and where to switch it on; checking stops. Tick it back, *Save and test*:
     working.
757. **SMTP AUTH switched off.** Untick *Authenticated SMTP* for support@ and email it: the ticket still opens; the reply comes from
     the platform's mailbox in the company's name; *Recent messages* says *sent from our mailbox, because yours said: … SMTP AUTH …*.
     Tick it back.
758. **A shared mailbox.** Convert support@ to a shared mailbox (or make a new shared one), give admin@ **Full Access** (not yet *Send
     as*), and sign in as **admin@** with the shared mailbox's address in the box. The page says *We read support@… as admin@…*; an
     email to it becomes a ticket; the reply comes from our mailbox, and *Recent messages* names **Send as**. Give admin@ *Send as*
     (it can take up to an hour at Microsoft): the next reply comes from support@.
759. **Admin approval.** (If the tenant allows users to consent, set Entra → Enterprise applications → Consent and permissions → *Do
     not allow user consent* first.) Sign in as a person who is not an administrator: Microsoft says *Need admin approval*, and our page
     says only an administrator can approve it, with the approval link. An administrator opens that link and accepts: our page says
     *approved — now press Sign in with Microsoft*; the person signs in.
760. **Disconnect.** Press **Disconnect**, confirm: the page says the sign-in is deleted from our server and how to remove the app from
     the organisation; the status says *waiting for somebody to sign in with Microsoft*; an email to support@ makes no ticket. In Entra →
     Enterprise applications, *The Remote Support Program* is listed and can be deleted.

## DI. The tray app restarts into an update (761-764) — HANDOVER §152

Added 2026-10-19. A signed `Publish-Platform.ps1` (no migration); the machines take the new tray app by themselves. The tray app
that is RUNNING is still the old one, which cannot notice an update — so each machine needs one sign-out or restart first.

761. **Once by hand.** After the deploy, when DESKTOP-2N6FVGT's history on `/owner/updates` says *Updated*, restart it (or sign out
     and in). The console's Version column for it shows the new version within a minute of the person signing in.
762. **By itself, from then on.** At the NEXT deploy, do nothing at the machine: within a few minutes of *Updated* in the history,
     with nobody connected and the tray window closed, the Version column moves to the new version on its own (the tray icon may
     blink out and back).
763. **Not while it is being used.** At a deploy, keep the tray window open on screen: the Version column stays on the old version
     (agent updated, tray app not); close the window and it moves within a minute. Same while connected to it: it moves only after
     you disconnect.
764. **When it does not move.** Run `tools\updatestate.ps1` through Tools → Scripts (paste into the SMALL box under the output) and
     keep its report: it says which tray app is running, from when, and what of it is on screen.

## DJ. The health check switch stays on (765-766)

Added 2026-10-21. Clint saved the health check settings twice and it was off every time. **What it was:** the company switch,
*Offer the monthly check to my clients*, read as "install it on every client", so he left it unticked — and Save said only
*Saved.* It is now *Switch the monthly check on for my company*, says it installs nothing by itself, and a save with it off says
in red that the check is still OFF. Found on the way: the report address box was `type="email"`, which the BROWSER refuses for two
addresses without sending anything (Save did nothing at all); the form is `novalidate` now and the server judges the addresses
(several allowed, `;` or `,`). `healthcheckwire` 61 on both databases. `Publish-Platform.ps1`, no migration; nothing on the machines.

765. **On, and still on.** Portal → Health check: tick *Offer the monthly check*, put two addresses in *Send reports to* separated
     by `;`, fill the mail server, Save: *Saved.* at the top. Sign out, sign in, open the page again: still ticked, both addresses.
766. **A slip is said, not swallowed.** Type `you@yourcompany.co.za; oops` and Save: a red line at the top names `"oops"`, the tick
     and what you typed are still there, and nothing is saved until it is corrected.
767. **Off is said.** Untick *Switch the monthly check on for my company* and Save: a red line says *Saved, but the monthly check is
     still OFF*. Health checks (the results page) says it is switched off. Tick it and save: no client's machines get it until
     that client is ticked on its own page.

## DK. Typing @ lists the people on the ticket (768-772)

Added 2026-10-21. Clint: *"putting in the @ should list everyone assigned to the task."* Typing @ in a ticket's comment box,
portal and console, pops up a list: **On this ticket** (everybody assigned, and whoever did the work) first, then **Everybody
else**, never the person typing, narrowing by any word of the name as it is typed. Up/Down, Enter or Tab or a click puts the whole
name in. The *Ask someone* dropdown and the *Ask:* name buttons are gone. One rule, `Shared/MentionPick.cs` (console) and
`wwwroot/mentionpick.js` (portal); who is flagged is still read from the posted words by the server. `mentioncheck` 43,
`mentionwire` 11 (both databases), `ticketwindowprobe` 258 with real keys; walked in the browser. `Publish-Platform.ps1` (no
migration), then re-download the console.

768. **The list.** Open a ticket assigned to two people, type `@` in the comment box: a list under the @ with those two under *On
     this ticket*, everybody else below, and not you. Same in the console's ticket window.
769. **Narrowing.** Type the first letters of a SURNAME: only the matching people are left. Enter puts `@Full Name ` in; Post; that
     person's Tickets badge goes up.
770. **It never takes the keyboard.** In the console, with the list showing, keep typing: the letters go into the box, not the list.
771. **Out of the way.** Esc closes it and it stays closed for that @; an email address (`sales@supplier.co.za`) never opens it.
772. **Light and dark.** Both themes, portal and console: the list is readable and the chosen name stands out.

## DL. Five seats and no more; ending a pilot; five companies at a time (773-782) — HANDOVER §153

Added 2026-10-22. Clint: a five-seat product for small IT companies, no extra seats (Anywhere IT excepted); a way to end the
pilots; five paying companies at a time. `seatcheck` 54, `pilotcheck` 57, `placescheck` 26. Signed `Publish-Platform.ps1`
(migration `PilotEndingAndOpenPlaces`); nothing on the machines, no console re-download.

773. **Anywhere IT is not limited.** Portal → People as Anywhere IT: the pill says *"… seats in use · not limited"* and the Add form
     is there however many people there are.
774. **Five and no more.** In a test company with five people in seats: the pill says *5 of 5 seats in use*, the Add form is
     replaced by *All 5 seats are in use…*, and there is no price question anywhere. Billing says R500 and *5 seats and no more*.
775. **A seat freed is a seat.** Remove one person: the Add form is back; add somebody: 5 of 5 again.
776. **The pilots' use.** Owner → Pilots: each pilot company with its people, machines, tickets, sessions and last sign-in.
777. **Notice, not a switch.** *End the pilot…* on a pilot: the day offered is 30 days away; give notice with *Suspend…*. The row says
     *Notice given — ends …*; the company's owners get the email; their Billing page says when it ends. They can still sign in.
778. **Withdraw.** *Withdraw notice*: the row says Pilot again; the owners get *your pilot carries on*.
779. **An earlier day asks first.** Choose tomorrow: the browser asks *only if the company has agreed*. (On the day, the sweep
     suspends it within 10 minutes: its people are signed out and its machines disconnected, and the owners are emailed.)
780. **The places.** Owner → Approvals: *Open places — n of 5 places taken*. Anywhere IT and the pilots are not counted.
781. **Full.** Set places to the number taken. In a private window, `/signup` (through `/owner/preview` while the site is hidden) →
     South Africa: the full version's card says *All places taken*; choosing it shows the waiting list, not the terms. The free
     version still opens its terms. Join the list.
782. **A place offered.** Approvals: *Offer a place* on that entry: the email arrives with a link; it opens the terms though every
     place is taken, signs the company up, and the same link a second time says it has been used. Put the number back to 5.

## DM. A company takes its data with it (783-791) — HANDOVER §154

Added 2026-10-22, roadmap 4.9. Clint: *"the ability for companies to export their clients, tickets and knowledgebase."* Portal →
**Export** (owner and supervisors only) makes one zip in the background: `clients.xlsx` in Import clients' own columns, the tickets
with their story, quote lines, photos and files, and the knowledge base. A company that cannot sign in is exported **by us** from
Owner → All companies, and its owners are emailed a link. `exportcheck` 92 of 92 on both databases (88 with three guards out).
Signed `Publish-Platform.ps1` (migration `CompanyExports`, both databases); nothing on the machines, no console re-download.

783. **Who.** Sign in to the portal as a technician, then as an office administrator: no *Export* in the menu, and `/portal/export`
     goes back to the sign-in page. As a supervisor, and as an owner-only login: *Export* is there and the page opens.
784. **Made, and told.** As a supervisor, *Make the export* (recordings unticked): the row says *Being made* with where it has got
     to, the page looks again by itself, then *Ready* with its size and what is in it. The ready email arrives.
785. **What is in it.** Download and open the zip. README.txt says what each file is. Open `clients.xlsx`: one row per contact
     person, phone numbers with their leading 0; the second sheet has archived clients too. Open `tickets/<a busy ticket>/story.txt`:
     it reads start to finish, with the files named.
786. **Back in.** Clients → Import clients → upload that `clients.xlsx` into a TEST company with no clients: the preview shows every
     client and contact person, no errors, no columns ignored. Import, and spot-check three clients against the real company.
787. **No passwords.** Search the whole unzipped folder for a device password you know a workshop ticket holds: it is not there;
     `tickets.xlsx` says only *Password protected: Yes*.
788. **The knowledge base.** Open an article's `.html` from `knowledge-base\…` in a browser straight from the folder: its words and
     its pictures show.
789. **Recordings.** Tick *Include session recordings* and make another: the recordings are in `tickets\<n>\recordings\` and play;
     the first export's row now says it was replaced.
790. **By us.** Owner → All companies → a SUSPENDED test company → *Export their data*: the page names who will be emailed. The
     owner's email arrives with a link; opening it shows a page with *Download the zip* (nothing downloads by itself); the button
     gives the zip without signing in. The admin log has *Export started* and *Export ready … link emailed*.
791. **Seven days.** On the server, an export older than 7 days is gone from `data/exports/<company>/`, and its link says it no
     longer works. (Or check the next morning that the nightly backup is no bigger for the exports — `exports` is left out of it.)

## DN. A technician asks for outside help (792-804) — HANDOVER §155

Added 2026-10-22. Clint: *"Sometimes it happens that a tech needs the outside assistance rather than the client."* In a session,
**Tools → Get outside help…** makes a code for THAT session and a message with both ways in (the browser link fills in the code)
to Copy, or send by WhatsApp or Email. Whoever types it waits for the technician who made it to let them in; the person at the
computer is **told, not asked**. They leave without ending the session, and go when nobody of yours is left in it. `helpcheck`
77 of 77 on both databases (66 with three guards out), `fitprobe` 416. **Signed `Publish-Platform.ps1` (migration
`TechnicianOutsideHelp`, both databases), then re-download the console; a FULL ship and client reinstall only for the customer's
notice** (an older tray app simply says nothing). Needs two consoles of yours, one test machine, and a browser or a second
company's console.

792. **The menu.** Connect to the test machine. *Tools* ends with **Get outside help…**. In a session opened from somebody
     else's code (a visit) or a browser support session, it is not there.
793. **The code.** Open it, type who it is for, *Make a code*: a nine-digit code, *Works once, for 9:59 more* counting down, and
     the message under it with the code and both links. The test machine's tray app shows nothing about it.
794. **Copy, WhatsApp, Email.** *Copy* and paste it into Notepad: the whole message. *WhatsApp* opens WhatsApp (desktop or web)
     with it typed in, waiting for you to pick who. *Email* opens your mail program with it as a new message.
795. **The link.** Open the message's `…/guest/web#123456789` link in a browser: the code is already in the box, the address bar no
     longer shows it, and the page says to type your name.
796. **Let them in.** Type a name and Connect. The page says *Waiting for <you> to let you in…*; on your screen **<name> wants to
     join** comes to the front, with *Also let them copy files…* UNTICKED. The test machine is asked nothing.
797. **Told.** *Let them in*: the browser shows the screen. On the test machine a balloon and the support window's log say
     *<name> has joined at <you>'s request. They are not from your IT company…*. Your colleague's console gets the red pop-up,
     *Brought in by <you>*, with **Remove**, not End it.
798. **What they can do.** From the browser: the mouse and keyboard work. Try a file: there is none (browser). Your window's *Get
     outside help* says they are connected, with *Remove them*.
799. **They leave.** Press *Leave* in the browser: your session carries on. The test machine says *<name> has left.*
800. **Turned away, and no answer.** A new code; *Turn them away*: the browser says you did not let them in. Another; ignore the
     question: after two minutes they are told you did not answer, and the question goes.
801. **Files, and another company.** A new code typed in ANOTHER company's console (*Connect with a code…*), let in WITH files:
     their session window has *Files*, and copying a file works both ways. A colleague presses *Remove* on the red pop-up: they
     are out, your session goes on.
802. **You leave.** Let somebody in, then press *Leave* in your session window: the whole session ends for them too — never left
     on the machine alone. The ticket tells the story: code made, typed, let in (with or without files), told, left.
803. **The switch.** As a supervisor, Portal → *Outside help* → untick *Let our technicians bring somebody in…*, Save, while
     somebody is in: they are removed at once, and *Get outside help* says it is switched off and where to switch it on. Tick it
     again.
804. **The customer's own code sends too.** With outside help on for the test machine, tray → *Let someone else connect…*, make a
     code: under it, *Copy the message*, *WhatsApp* and *Email*. The message is signed with the name last given at Request Support
     and its browser link fills the code in. Stop the code: the three buttons go grey.

## DO. The installer asks who the computer is for (805-812) — HANDOVER §156

Added 2026-10-23. Clint: the client install asks *"their name, surname and email address"*; that becomes the name the console
shows and the name Request Support starts with; and linking that person to a client *"automatically registers it as a contact
under that client in the portal."* All eight recommendations taken: required in the installer window, the latest name still wins,
the email optional in Request Support, linking adds the person **by email first, then name**, with a tick to say no (a shared PC),
an email never links anything by itself, new installs only, not the free version. `installedfor` 48 of 48 on both databases (46
and 47 with the two guards out), `oneexecheck` 39, `callprobe` 77, `dashtabs` 155. **One signed `Publish-Platform.ps1` (migration
`InstalledForPerson`)** — the console and installed machines update themselves; new downloads have the new window. Needs a test
machine you can install on, and one console.

805. **The installer asks.** Download the customer .exe and run it. The *Install* card has *Who is this computer for? So {company}
     knows who you are when you ask for help.*, then **Name**, **Surname** and **Email address**. Press Install with them empty: it
     says *Type your name.* and Windows asks nothing. Type `jane at acme` as the email: *That email address does not look right.*
806. **It installs.** Fill in a real name, surname and email, Install, say Yes to Windows. In Program Files\{company}\Remote Support
     there is a `person.json` with them. In the console the machine's Who is that name, **grey italic**, and its tooltip names
     the email.
807. **Request Support starts with it.** On the machine, Request Support: *Your name* is the installed name and *Your email
     (optional)* the installed email. Send it: the ticket and the console say that name.
808. **Somebody else asks.** Request Support again with ANOTHER name and no email: the console's Who becomes that name, and its
     tooltip has no email. Restart the machine (or sign out and in): the console STILL says the second name — the install's does
     not come back.
809. **Link it to a customer, ticked.** Install on a machine again (or use one not yet linked). Console → right-click → Link to a
     customer: under the list, *Add {name} ({email}) as a contact when I choose a customer*, ticked. Choose a COMPANY row, Save:
     the machine shows the person's name (not italic), and the portal's client page has them as a contact WITH the email.
810. **The same email already on file.** At a client, add a contact under ANOTHER name with the email you will install with. Install
     with that email, link to that client with the tick on: the machine goes to THAT contact — no second one is made.
811. **Unticked.** On another unlinked machine with an install name, untick it and choose a company: the machine shows the company,
     and no contact is added. In the portal → Devices, the same machine's Set form still has the tick (linked to a client, no
     person); pressing Set with it on adds them now.
812. **The portal's tick.** On an unlinked machine in Portal → Devices: under Set, *Add {name} ({email}) as a contact when I choose
     a client*, ticked. Type a client, Set: *…linked to {name} at {client}.* and the contact has the email.

## DP. A free account from the owner pages (813-819) — HANDOVER §157

Added 2026-10-23. Clint: *"a button that i can create the client in the portal from and send the links directly from there"*, and
*"an option to cc the tech in when sending the links so that he can help her set it up."* Owner → **Free version**: their name and
email, an optional technician to copy; the account is ready at once and one email carries the set-up link, the installer and the
console. The free terms are accepted on the set-up page, before the password. `freeaccount` 40 of 40 on both databases (33 with the
terms check and the Cc out). **One signed `Publish-Platform.ps1`, no migration.** Use two addresses of your own for the first run,
not a customer's.

813. **The page.** Sign in at `/owner` → **Free version** (beside Pilots). The *Set up a free account* form: Their name, Their email,
     Company (optional), Phone (optional), the time zone line, **Copy to a technician**.
814. **Make one, copied.** Your test name and email, Copy to a technician = a second address of yours. *Create and send the links*:
     *{name} is ready — free version, active. The set-up and download links went to … (copy to …).* The row says *Not set up yet*,
     *Terms not accepted yet*, *0 of 2*. Owner → Log has it with your name.
815. **The email.** Both inboxes have ONE email, *Your free remote support account: set up and download*, from The Remote Support
     Program; the technician in **Cc** (visible), and Reply goes to them. It has the set-up link, the `/download/…` link (two
     computers at most) and the `/tech/…` link, and says the technician has a copy.
816. **The terms first.** Open the set-up link: *Your free remote support account is ready*, the free terms in a box, a tick, then
     the password. Continue without the tick: refused, and the link still works. Tick, choose a password: on to the authenticator.
     Back on Owner → Free version, the row now says *Set up*, with no "Terms not accepted yet".
817. **Again, set up.** *Send the links again…* with another copy address: only the download links go — no set-up link — copied to
     the NEW address.
818. **Again, not set up.** Make a second account and do not use its link. *Send the links again…*: a new set-up email; the FIRST
     link now says it cannot be used.
819. **On to the free version itself.** With the account from 816: install on a test computer from the `/download/…` link (it asks
     for that computer's password), download the console from `/tech/…`, sign in, connect with that password — sections BR–BX.

## DQ. The outside security review's fixes (820-826) — HANDOVER §158

Added 2026-10-23. Daniel's review of the live site found no way into another company's data. Of his findings, F1 (sign-in limit
per address), F2 (`/forgot`), F11 (the marketing pages) and F12 (the runtime) were already right; what changed is below.
`securitycheck` 87 of 87 covers 820 and 823–826 (86 with the known-device line out); `photogate` 41 covers 822; all 89 database harnesses pass. **One signed `Publish-Platform.ps1`, no
migration, nothing to ship to machines.** Use a test account for 820, never your own.

820. **A stranger cannot lock you out of a browser you use.** Sign in to the portal on your usual browser with the code (so it is
     trusted). In a private window, type that account's address with five wrong passwords: *Too many attempts.* Back in the usual
     browser, sign out and in with the right password: it lets you in.
821. **The holding page has its headers.** From any PC: `curl -sI https://theremotesupportprogram.com/` lists
     `strict-transport-security`, `x-content-type-options`, `x-frame-options`, `referrer-policy` and `content-security-policy`.
822. **A fake photo is refused.** On a ticket, add a photo that is a text file renamed to `.png`: *That is not a photo.* A real phone
     photo and a Windows screenshot still go in.
823. **A missing ticket is a 404.** Signed in, open `/portal/tickets/999999`: it says *Ticket not found*; the browser's developer
     tools (F12 → Network) show 404.
824. **`/health` says only that it is up.** `https://theremotesupportprogram.com/health` shows `{"ok":true}`.
825. **`/.well-known/security.txt`** shows a Contact and an Expires a year ahead.
826. **The report-only policy is reporting.** After a day of use: `sudo journalctl -u remotesupport-platform | grep "CSP report" | head`
     on the server shows what it would have blocked — the list of inline scripts to move before the policy is enforced. Nothing
     on any page should look or work differently.

## DR. Per-company numbers in portal addresses, not database ids (827-835) — HANDOVER §159

Added 2026-10-23. The outside review's F8: every ticket, client, article and photo took its id from one sequence shared by every
company, and the portal showed it (`/portal/tickets/129` for a company's ticket #2), so the gap between two of a company's own
tickets showed the whole platform's volume. Now the portal names them by a number that counts within the company, and stored
files by a random token. `addresscheck` 122 of 122 (107, 118 and 109 with its three guards taken out). **One signed
`Publish-Platform.ps1` — migration `PerCompanyAddresses` on both databases, which numbers every existing client, article and
recurring task — then the console updates itself. Nothing to ship to machines.** Run them as Anywhere IT; 834 needs a login
to a second company — a test one, never a pilot's.

827. **A ticket opens at its own number.** Open any ticket from the list: the address bar says `/portal/t/` and the number the page
     shows as *#N* — the same number, not a bigger one.
828. **Clients, articles and recurring tasks too.** Open a client: `/portal/c/N`. An article: `/portal/a/N`, and its Edit
     `/portal/a/N/edit`. A recurring task: `/portal/r/N`. Each N is small (1, 2, 3 … for your company), not the old large ids.
829. **Photos and files are not numbered at all.** On a workshop ticket, right-click a photo → *Copy image address*: it ends
     `/portal/p/` and 24 letters and digits. The same for a comment's file (`/portal/f/…`), a picture in an article (`/portal/i/…`)
     and a session recording's Download (`/portal/v/…`).
830. **An old bookmark still works.** Open an old address from your browser's history, such as `/portal/tickets/…` with a large
     number: it lands on the same ticket at `/portal/t/N`. The same for an old `/portal/clients/…` and `/portal/kb/…`.
831. **The console's buttons land on the number.** In the console's Tickets window, *Open in browser*, *See their screen*, a
     comment's file, *Knowledge base for …* and a recurring task's *Change in the browser* each open the browser at the new
     address. **A comment's file button used to open the ticket list instead** — it now opens the file.
832. **Search the page source.** On a ticket page, press Ctrl+U and search (Ctrl+F) for `/portal/tickets/` — only
     `/portal/tickets/new` and the plain list should be there. Search for `name="id"`: nothing.
833. **Everything still posts.** On a ticket: add a comment, add a photo, move a step on, edit the ticket. On a client: save a
     change, add a contact. In the knowledge base: write an article, add a picture, move it to a folder. Each lands where it
     should and comes back to the same `/portal/…/N` address.
834. **Another company cannot use yours.** Signed in to the test company, paste one of Anywhere IT's old addresses (`/portal/tickets/…`):
     *Ticket not found*, a 404 — exactly what a ticket number that does not exist shows. `/portal/t/1` shows the test company's own
     ticket #1 (or none), never Anywhere IT's.
835. **The billing pop-up opens the right ticket.** Move a job into the billing queue as a technician; on the office
     administrator's portal the pop-up's *Open the ticket* opens `/portal/t/N` for that job.

## DS. Five seats; more on request (836-844) — HANDOVER §160

Added 2026-10-25. Every company still starts with five seats. An owner can now ask for more on Billing; a platform admin allows up to
a number (Owner → Approvals, or All companies) or declines; the owner then adds people past five, accepting R300 a month each first,
and PayFast's amount follows from the next payment. `seatcheck` 99 of 99 (96 and 97 with two guards taken out). **One signed
`Publish-Platform.ps1` — migration `MoreSeatsOnRequest` on both databases. Nothing to ship to machines, no console re-download.** Use a
TEST company with a sandbox card, never a pilot's or a paying one; Anywhere IT is not limited, so it cannot show any of this.

836. **Five, and how to ask.** As the test company's owner with five people in seats, open People: *5 of 5 seats in use*, no Add form,
     and the reason ends *ask us for more seats* as a link to Billing. A supervisor sees *ask an owner to ask us*.
837. **Ask.** Billing → *More seats*: ask for 7, with a reason. *Thank you — we have your request for 7 seats*, and the box says *You asked
     for 7 seats on …*. People still says 5 of 5. A supervisor's Billing has no form.
838. **We are told.** Every Full admin and Owner gets *"<company> asks for 7 seats"* by email, with the reason and a link to Approvals.
     Owner → Approvals → *More seats asked for* lists it: 7 seats, the reason, who, *may have 5 now*.
839. **Allow.** On Approvals press **Allow** (up to 7). The row goes; the company's owners get *"you may now have up to 7 seats"*. All
     companies shows *5 of 7 seats in use · 2 extra allowed*; the Log has *Most seats 5 → 7*.
840. **Add the sixth, agreeing the price.** People now says *5 of 7 seats in use · 5 in your package* with the Add form and *costs R300 a
     month more*. Add somebody: *Adding … raises your monthly price*, Now R500, After **R800**. Press *Don't do it*: nobody added. Add again
     and press *Accept R800 a month*: added, *6 of 7*.
841. **PayFast takes it.** Billing shows R800 — the package and *1 extra seat × R300* — and the agreement under *Price changes agreed*.
     With a sandbox card on: *How to pay* says R800 a month within a minute (or *PayFast has not yet taken the new amount — we keep trying*,
     which must clear by itself), and PayFast's sandbox subscription shows 800.00 from the next run date. Remove the person: back to R500
     at PayFast too.
842. **A supervisor cannot agree.** Signed in as a supervisor, add a seventh: refused, *from R800 to R1,100 … Only an owner can agree*.
843. **Decline.** As the owner ask for 10; on Approvals type a reason and press **Decline**: the row goes, the owners get *"your request for
     more seats"* with the reason, and Billing no longer shows the request.
844. **Take it back.** On All companies set *Allow up to* to 5 and press Allow: *back to its 5 seats … nobody was removed*. People keeps all
     six, with no Add form.

## DT. No free version: the Basic Version for Remote Workers, R50 a month, three computers (845-855) — HANDOVER §161

Added 2026-10-25. The free version is now *The Basic Version for Remote Workers*: R50 a month by card after a 14-day trial, up to
three computers, every other rule the same; the free accounts from before are given 30 days' notice from Owner → Basic version; it never
counts against the five places; and a new public page for remote workers. `basiccheck` (new) on both databases. **One signed
`Publish-Platform.ps1` — no migration. The apps update themselves (one console sentence changed).** Use a TEST Basic company and the
PayFast sandbox — **and do NOT press Give notice on real free accounts until Clint says so: it emails them.**

845. **The price.** As a Basic owner, open Billing: *The Basic Version for Remote Workers — one person, up to 3 computers*, **R50** a
     month, no seats, no ticket-history button. The menu has Billing between My account and Upgrade.
846. **Three computers.** Install on three computers: all three connect (the portal and the console say *3 of 3*). A fourth install is
     refused before anything is installed, *"The Basic version allows 3 computers"*.
847. **Signing up.** /signup → South Africa → the Basic card (*R50 a month, after a 14-day free trial*, up to 3 computers) → its terms (R50,
     at most 3 computers, *"or whose owner has allowed you to reach them"*) → details → **on to PayFast's sandbox**, R0.00 now, R50 from
     14 days on. Back on the site: ready, and the owner is emailed *your account is ready*.
848. **Not held by the five places.** On Owner → Approvals set the places to the number already taken. /signup → the full version shows
     the waiting list; the Basic version still goes to its terms. Put the places back.
849. **A free account from before.** On Owner → Basic version, a running account with no card says *Free — needs notice*, and the
     banner counts them. Press **Give notice** on the TEST one: it says the day (30 days on), the owner gets *"… becomes R50 a month on
     …"* with Billing's address and the terms, the row now says *No card yet · pays from …*, and the Log has *Basic notice*.
850. **Its Billing after the notice.** The test owner's Billing says the trial ends on that day and offers *Add your card*; adding the
     sandbox card makes it Active, with nothing charged until that day.
851. **No card by the day.** On a second test account, give notice, then move its trial end to yesterday (or wait): its computers are
     refused, the owner gets *add your card to keep connecting*; adding the card switches it back on at once.
852. **Made from the owner pages.** Owner → Basic version → make an account: it says *Basic version, 14-day trial*; the email says up to 3
     computers, R50 after 14 days, and to add the card; the set-up page says the same.
853. **Upgrading, paying by card.** As the Basic owner with the card in, Upgrade: *Now R50 a month*, *After R500*, *the new price is
     charged from your next payment*. Upgrade: no new trial; PayFast's sandbox subscription shows R500 from the next run date.
854. **The remote-workers page.** With the preview cookie (Owner → Preview), open /remote-workers: *Work from home on your office
     computer*, R50, the questions, *Start your free trial*. View source: the keywords and the FAQ data are there. Without the cookie it is
     the holding page.
855. **No "free version" anywhere.** Click through /, /pricing, /signup, the footer, the owner pages and the Basic portal: it says Basic.

## DU. The Basic version buys more computers: R10 each, up to 20 (856-861) — HANDOVER §162

Added 2026-10-25. A Basic owner sets on Billing how many computers they pay for — three in the price, R10 / $1 / £1 / €1 a month for
each one more, up to 20, with no approval from us; a rise is shown and accepted first, and PayFast follows. `basiccheck` and `freecheck`.
**One signed `Publish-Platform.ps1` — migration `BasicExtraMachines` on both databases. The console updates itself.** A TEST Basic company
with the PayFast sandbox card.

856. **Billing → Computers.** *You pay for 3 computers (N installed)*, R10 a month each, up to 20. Change it to 5: *Paying for 5 computers
     raises your monthly price from R50 to R70*; *Don't do it* changes nothing; *Accept R70 a month* → *You now pay for 5 computers*, the
     plan shows *2 extra computers × R10*, and *Price changes agreed* lists *Computers 3 → 5*.
857. **PayFast takes it.** With the sandbox card on, *How to pay* shows R70 within a minute; PayFast's sandbox subscription shows 70.00 from
     the next run date.
858. **The fourth and fifth install.** Install on a fourth and a fifth computer: both connect, the console's header says *n of 5* (no
     sign-out needed), and a sixth is refused before installing, *"The Basic version allows 5 computers. Add a computer on Billing …"*.
859. **Fewer.** Try 4 with five installed: refused, *remove one first*. Remove one, then 4: done with no question, R60.
860. **The cap.** Try 21: *20 computers is the most*. At 20, a 21st install is refused without the "Add a computer" advice.
861. **The words.** /pricing, the signup plan card, /remote-workers and the Basic terms all say three included, R10 each, up to 20.

## DV. The Free version is back beside the Basic version; Basic connects attended too (862-873) — HANDOVER §163

Added 2026-10-25 (evening). Three plans: **Free** (nothing to pay, ONE computer, unattended with a password), **Basic** (R50, 3 to 20
computers, unattended with a password OR attended — the person asked by the agent service), and the full version. Free moves up to
Basic or full; Basic moves up to full or down to Free. `basiccheck`, `freecheck`, `freeinstall`. **One signed `Publish-Platform.ps1` —
migration `FreeVersionBack` on both databases; the apps update themselves.** A TEST company for each, the PayFast sandbox card for Basic.

862. **Three cards at signup.** /signup (preview cookie) → South Africa: *The full version*, *The Basic Version for Remote Workers* (R50,
     *Unattended with a password, or attended*), *The Free Version* (*Nothing to pay*, one computer). Free → its terms say **1 computer**
     and **No fees** → the account is made with no card → *signup for the Free version is in*. Approve it: Active, no trial.
863. **One computer on Free.** Install on one computer: the password is REQUIRED. On a second, the download says *"The Free version allows
     1 computer …"* before installing anything.
864. **Free connects unattended.** Connect with the password: in, nobody asked, the *Being controlled remotely* bar shows.
865. **Basic, password left blank.** On a Basic company's computer, run the download and leave both password boxes empty: *Done … whoever
     is here is asked to allow it*. Connect from the console: the person at it gets the Allow prompt (from the support service — there
     is no tray icon), *Waiting for … to accept* on the console; Allow → in, with the bar; Deny → nothing.
866. **Basic with a password.** Run the download again on the same computer and choose a password: connecting asks nobody. Run it once
     more with the boxes blank: attended again.
867. **No asking from the computer.** On a Basic computer there is no tray icon, no Request Support and no *Get help now* in the download.
868. **Moving down.** As the Basic owner with two computers installed: Billing → *Move to the Free version* → tick → refused, *2 computers
     are installed*. Remove one, try again: done — Billing says *You are on the Free version now*, nothing to pay; PayFast's sandbox shows
     the subscription cancelled; *Price changes agreed* lists *Moved down … R50 → R0*. Connecting to a computer without a password is now
     refused (*unattended only*) — set one by running the download again.
869. **Moving up from Free.** As the Free owner: Upgrade asks which. *Basic*: Now R0, After R50, the Basic terms → on to PayFast (R0 now,
     R50 from 14 days on) → Basic, 14-day trial, three computers, and attended works.
870. **Basic to full.** As 853: Now R50, After R500, no new trial when the card is in.
871. **A free account from before, after the deploy.** One that had two computers: both still connect; a third computer is refused with
     the Free version's words. Owner → Free and Basic lists it as *Free*; there is no *Give notice* any more.
872. **Made from the owner pages.** Owner → Free and Basic: make a *Free* account (active, *nothing to pay*, the email says one computer)
     and a *Basic* one (*Basic version, 14-day trial*, R50 after 14 days, the card).
873. **The words.** /pricing has *The Free Version — nothing to pay*; the Basic card says *or attended*; /remote-workers has *Attended too*
     and *Is there a free version?*; the home page mentions both. The Free and Basic portal menus end with *Sign out* (missing since §105).

## DW. Latest activity: every update on the tickets, as it is posted (874-884) — HANDOVER §164

Added 2026-10-26. Clint, raised by one of his technicians: a **Latest activity** button that shows every update on every ticket as it is
posted or the stage changes, newest first; all tickets or only one's own; narrowed by client and by technician. And: *"i assign a ticket
to Ryan, it shows up in his recent activity?"* — yes, as *assigned it to you*. In the portal it is a tab beside Tickets (every role) and a
card on the dashboard; in the console a header button beside Tickets. `activitycheck` 83 of 83 on both databases. **One signed
`Publish-Platform.ps1` — migration `LatestActivity` on both databases; the console updates itself.** Two people of one TEST company,
one of them a plain technician, one console each.

874. **The tab and the count.** Sign in to the portal as anybody: the menu has *Latest activity* after Tickets. As a colleague, comment on
     a ticket the first person can see: within a few seconds, without a reload, the first person's tab shows *1 new* beside it.
875. **The page.** Click it: *All tickets* or *My tickets* (whichever was chosen last), newest first — *who · what* (Comment, *Booked in →
     Inspection findings*, Customer replied, New ticket …), the ticket number, subject and client, the first words, *new* on what came since
     the last look. The badge goes as soon as the page is open.
876. **Live.** Leave the page open; post a comment as the colleague: the line appears at the top by itself, with no reload.
877. **A line opens the ticket at that entry.** Click a comment's line: the ticket opens with that entry in view and outlined.
878. **Assigned to you.** As a supervisor, assign a ticket to the technician (or add them beside someone): the technician's Latest
     activity shows it in red as *assigned it to you*, and the ticket is in their *My tickets*. An @mention shows *asked you*.
879. **The boundary.** As the plain technician, *All tickets* shows nothing of a colleague's ticket that they are not on — the same tickets
     as their Tickets page, plus the unassigned ones.
880. **Narrowing it.** *Client*: type part of a name, pick one — only that client's lines; the address says `c=` and a number, never an id.
     *Posted by*: one person. *Include my own* shows your own posts; *Include automatic entries* shows a session's lines (connected, ended,
     clipboard) — both are off by default. *Clear* puts it back.
881. **Show older.** On a busy company, *Show older* at the bottom carries on further back, with nothing repeated.
882. **The console.** The header has *Latest activity* beside Tickets; a colleague's comment makes it *Latest activity (1)* by itself. Click:
     the same lines in a window; a click on one opens that ticket in the console's ticket window; the count goes back to plain *Latest
     activity*. Change *All tickets / My tickets*, type a client, choose *Posted by* — the list follows.
883. **Seen only when looked at.** Minimise the console's Latest activity window and have the colleague post: the button counts it; restore
     the window — the line is there and the count clears.
884. **The same choice everywhere.** Choose *My tickets* in the portal: the console's next count counts only your tickets (and the console's
     window opens on *My tickets*).

## DX. Sort the ticket list by any column; a due date on every ticket (885-896) — HANDOVER §165

Added 2026-10-26. Clint, showing SuperOps' ticket list: *"Every column (Status, Subject, Company, Tech, Priority, Last Updated, Due Date)
can be sorted by simply clicking on Last Updated, for example. Can we do something similar?"* — then *"add a due date now as well"*. In
the portal's Tickets list and the console's queue, every heading sorts; there is a Priority column, Last updated says *2 min ago*, a Due
column, and *Customer replied* in orange; every ticket may have a due date. `ticketsortcheck` 61 of 61 on both databases. **One signed
`Publish-Platform.ps1` — migration `TicketDueDate` on both databases; the console updates itself.** A TEST company with a few tickets of
different clients, people, priorities and types.

885. **Click to sort.** Portal → Tickets: every heading (#, Type, Subject, Client, Assigned to, Status, Priority, Last updated, Due) is a
     link. Click *Priority*: Urgent first, *Priority ▼*. Click it again: Low first, *Priority ▲*. The filter, search and dates stay.
886. **Blanks last.** Sort by *Client*, then turn it round: the tickets with no client are at the bottom both ways. The same for
     *Assigned to* (nobody) and *Due* (no due date).
887. **Remembered.** Sort by *Due*, go to another page and back to Tickets: it is still sorted by Due. The *Sort by* box is gone.
888. **Group by customer.** Tick it: gathered under each customer as before, with Machine instead of Client; click a heading — each
     customer's jobs sort that way. Untick it: one list again. Both remembered.
889. **The columns.** Priority shows its word and a coloured dot; Last updated says *5 min ago* (the exact time on hover); Due shows the
     day, or a call out's appointment with 📅 and its time, or —.
890. **A due date at New ticket.** Portal → New ticket: *Due date (optional)* beside Priority. Set one: the ticket's Details say *Due …*
     and the list shows it. A day already gone is refused, and nothing is made.
891. **Changing it.** On a ticket's Details → Edit the ticket: *Due date* — change it, empty it, save. The story says *Due: … (was …)* /
     *Due date removed (was …)*. A closed job shows its due date and keeps it.
892. **Overdue.** A job whose due day has gone, still on the technicians' side, is red in the list with *Overdue*, and its Details say
     *— overdue*. Send it to be invoiced: no longer overdue.
893. **Customer replied.** Email a reply to a ticket from the customer's address (Tickets by email): the list shows an orange *Customer
     replied* on it. Comment on the ticket: it goes.
894. **The console's queue.** Console → Tickets: the queue has #, Subject, Where it is, Due, Priority, Client, Assigned to, Updated; click
     any heading to sort (▲/▼), again to turn it round; remembered on this PC. On a narrow window the later columns scroll in from the
     right; maximised, the queue widens.
895. **The console's due date.** Console → New ticket: tick *Due date* beside Priority and pick a day — the ticket is due on THAT day (not
     the day before). On a ticket's Details → Edit the ticket: *Due date*, *No due date* to take it away.
896. **On a phone.** The portal's ticket list on a phone: the headings fold away, and a *Sort by* box above the list does the same.

## DY. Job types: each company chooses its categories and steps (897-911) — HANDOVER §166, §167, §168
Added 2026-10-27. Clint: *"one I.T company might not want to have the before and after photos ... deselect stages they dont want
without breaking the flow."* Portal → **Job types**: a card per kind of job, its steps as tick boxes in order, the ones that can't go
with a lock and the reason, and under each card the way a job usually goes, redrawn as you tick. Tickets already open finish the way
they started. `jobtypecheck` 74, `jobtypewire` 48, `jobtypepage` 31 (both databases), `ticketformprobe` 145, `ticketwindowprobe` 267. **One signed `Publish-Platform.ps1` —
migration `JobTypes` on both databases; the console updates itself.** Use a TEST company (not Anywhere I.T) with an owner, a supervisor, a technician and an office
administrator, and a few open tickets made BEFORE anything is switched off.

897. **Shown once.** Sign in to the portal as the TEST company's owner: you land on *Job types* with *Everything is switched on. Untick
     what your company doesn't use…* and a *Keep everything* button. Press it: you are told everything is on. Go to the dashboard: it is
     the dashboard, and it stays that way at every later sign-in. A supervisor signing in first is NOT sent there.
898. **Read, not changed.** Sign in as the technician and open `/portal/jobtypes`; then as the office administrator (it is in their menu):
     both see every card, every box greyed, *Only a supervisor or the owner can change this*, and no Save.
899. **The line moves as you tick.** As the owner: untick *Inspection findings* — Workshop's line under its card loses *Inspected*. Untick
     *Track payment after invoicing* — every card's line loses *Invoiced — awaiting payment*. Untick Walk in's *We use this* — its steps
     grey out and its line says *Switched off*. Nothing is saved until Save.
900. **Save.** Untick *Inspection findings*, *Photos when booked in*, *Photos when collected or delivered* and Walk in; Save. *Saved. New
     tickets follow this from now on…* and *Last changed by …* at the bottom. Reload: the boxes are as saved.
901. **New ticket.** Portal → New ticket: no *Walk in* button. Choose Workshop: *Photos of the machine as it arrived (optional)* — book a
     machine in with NO photos; it is made.
902. **That job's route.** Open it: at Booked in the buttons are *Repairable* and *Not repairable* — no *Inspection findings*. Take it to
     *Solved — to be invoiced*, then *Machine collected* with NO photos: it is collected, and the story says *Machine collected.*
903. **A job from before keeps its route.** Open a workshop job made before step 900: it still has *Inspection findings*, and still needs
     three photos as it leaves.
904. **Edit.** On a General ticket → Edit the ticket: no *Walk in* among the types. A walk in made before step 900 can still be finished.
905. **The console follows it at once.** With the console OPEN and signed in, save the step 900 changes in the portal. Without signing out:
     console → New ticket has no *Walk in* button; choose Workshop — *Your company doesn't ask for them*; book a machine in with NO
     photos (*Create ticket* is not stopped). (A console from before 27 October still offers Walk in: refused, *Your company has
     switched Walk in off (Job types)…*.)
906. **A quote without the supplier step or payment first.** Untick *Ask suppliers for prices* and *Payment before ordering parts*; Save.
     Quotes' line reads *A quote: Open → Quote needed → Quote sent — awaiting client → Quote accepted — order the parts → Parts ordered
     from supplier → …*. On a NEW General ticket: Quote needed (one item) → *Quote sent to client* is offered straight away → Quote
     accepted → *Order placed with supplier* → Parts received → Parts fitted → it goes to *To be invoiced*.
907. **Recurring tasks pause.** Untick Recurring task; Save. The next morning no recurring ticket opens and none can be set up. Tick it
     again: the morning after, ONE opens — that day's, not the ones missed.
908. **Back to everything.** Tick everything again; Save. New tickets have every step again; the jobs made while things were off finish
     the way they started.
909. **The console's ticket window — collected with no photos.** In the console, open the workshop job from step 901 and take it to
     *Solved — to be invoiced*, then *Machine collected*: the step says *Photos of it as it leaves are not asked for, but you can add
     some* (never *at least 0*), and Submit collects it. Its Photos tab asks for none.
910. **The console's Edit.** On a General ticket → Details → Edit the ticket: no *Walk in* among the types. On a walk in made before step
     900: *Walk in* is there, chosen.
911. **A job from before, in the console.** The workshop job from step 903: the console still asks for three photos as it leaves.

## DZ. Going live: -RealSite opens signups, and our site's chat says our name (912-917) — HANDOVER §169

Built 2026-10-27. **Only on 1 November**, with the go-live deploy (`Publish-Platform.ps1 -Server 154.65.110.136 -RealSite`). Before
it, only 912 can be done.

912. **Before go-live, nothing changes.** After an ordinary deploy (no `-RealSite`) its output says *Signups closed; no chat bubble on
     the site (holding page in front)*; `https://theremotesupportprogram.com/` is still the holding page.
913. **The go-live deploy says so.** With `-RealSite` the output says *Signups will be OPEN. Chat bubble: answered by anywhereit*, and on
     the server *Signups OPEN (-RealSite)* and *Chat bubble on the site, answered by: anywhereit*.
914. **Signups are open.** `/signup` from a phone (not signed in): the country step, not *signups are closed*.
915. **The bubble on our site.** On `/` and `/pricing`, bottom right: the button reads *Chat with The Remote Support Program* (hover it),
     purple. Open it: headed *The Remote Support Program*.
916. **Anywhere IT answers it.** With somebody signed in to the console, type a message in that bubble from a phone: it rings in the
     console's Website chat window, as a chat from the Anywhere I.T site does, and the chat says which page it started on.
917. **Anywhere I.T's own site is unchanged.** On the Anywhere I.T website the bubble still says Anywhere I.T in its own colour.

## EA. Knowing when the site is down — the office PC watches it (918-921) — HANDOVER §170

Built 2026-10-27. On the office PC, `deploy\monitor\README.md`.

918. **It is set up.** `Install-SiteWatch.ps1` on the office PC ends *Done. Every 2 minutes …*, and *"The website check is set up"*
     is in clint@anywhereit.co.za's inbox.
919. **It is checking.** An hour later, `%USERPROFILE%\.rsp-watch\watch-log.txt` there has about 30 lines of *up (… ms)*.
920. **A deploy sends nothing.** After the next `Publish-Platform.ps1`, no email (the log may show *one missed check*).
921. **A real outage, once.** At a quiet time: `ssh ubuntu@154.65.110.136 "sudo systemctl stop remotesupport-platform"`, wait 6
     minutes — ONE *is DOWN* email; `sudo systemctl start remotesupport-platform` — within 2 minutes ONE *is back up (down about
     N minutes)*. (Every machine and console reconnects by itself afterwards.)

## EB. The website's words and the logo (922-926) — HANDOVER §171

Built 2026-10-27. After a signed `Publish-Platform.ps1` (and `-LandingOnly` for 922).

922. **The holding page has the logo.** `https://theremotesupportprogram.com/` on a phone: the purple mark and *The Remote Support
     Program* in white at the top left, and the browser tab shows the mark.
923. **The portal has it.** Sign in to the portal: the logo top left, white on dark; switch to light — dark lettering.
924. **Read the home page** in the preview (`/owner` → Preview): *Who it is for* names IT companies, software and point-of-sale
     suppliers, in-house IT teams, repair shops, call-out businesses and remote workers. Mark anything you would say differently.
925. **The menu** says *Remote support · Job cards · All features · Pricing · About*; *Job cards* opens *Job cards and ticketing*.
926. **A shared link.** After 1 November, paste the home page's address into WhatsApp: the preview shows the mark.

## EC. Removing a client — a misspelling or a duplicate (927-932) — HANDOVER §172

Built 2026-10-27. After a signed `Publish-Platform.ps1`. Use a made-up pair first, e.g. add *Test Clientt* beside *Test Client*.

927. **Who sees it.** Open a client's page as the owner, a supervisor or an office administrator: *Remove this client* is the last
     card. As a technician only: it is not there.
928. **Nothing on it.** Add *Test Clientt* with nothing else: the card says nothing is on it; *Remove Test Clientt*, OK — back on
     Clients with *Client removed.*, and it is not in the list.
929. **It says what will move.** Add *Test Clientt* again, open a ticket for it and link a machine to it: the card says *1 ticket
     and 1 machine*. Press the button without choosing a client: the picker asks you to.
930. **Moved and removed.** Choose *Test Client* and confirm: you land on Test Client's page — *The other client was removed…* —
     with the ticket in Open work and the machine in its list; Test Clientt is gone from Clients.
931. **The ticket says so.** Open that ticket: its story has *Moved from Test Clientt to Test Client — Test Clientt was removed.*
     It is NOT on Latest activity.
932. **The console follows.** In the console, the machine now sits under Test Client (no restart). Then do the real one: the
     misspelt client into the proper one.

## ED. The pricing page's country switch (933) — HANDOVER §173

933. **Every figure follows the country.** On `/pricing` (Owner → Preview until 1 November), press each country: the full version
     (R500 / $49 / £49 / €49), the Basic version (R50 / $5 / £5 / €5), its extra computer (R10 / $1 / £1 / €1) and the *Why five* card's
     extra seat (R300 / $20 / £20 / €20) all change together.
