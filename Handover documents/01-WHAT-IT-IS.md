# 01 — What it is and how the pieces fit

## The people involved

It helps to keep four kinds of people apart, because the product treats them differently.

| Who | Example | What they use |
|---|---|---|
| **The platform owner** | The Remote Support Program (Pty) Ltd — Clint | The **owner portal** on the website: approves companies, sets prices and seats, the platform's own email |
| **An IT company** (a "tenant") | Anywhere IT, or any reseller | Their own **portal** (website) and the **technician console** (a Windows program) |
| **A technician or office worker** at that IT company | A technician, a receptionist, a supervisor | The console, the portal, or both, depending on their role |
| **A customer** of that IT company | A dentist's practice, an accountant | A small **tray app** on their computer, a download page, sometimes a web page |

One IT company can never see another IT company's customers, machines or tickets.

**Three versions (since 2026-10-25).** The **full version** is everything in this pack, for IT companies
supporting their customers. The **Basic version for remote workers** (R50 a month) is one person reaching **up to three of their own
computers** (more at R10 each, up to 20) — typically the office PC or branch offices — unattended with a password, or attended with
the person there asked to allow it; no tickets, no customers, no team. The **Free version** is the same for **one** computer,
unattended only, with nothing to pay; it moves up to Basic or full, and Basic moves down to it. It runs on the same server and the same programs; a setting on the company (`Plan`) decides
which rules apply, and the server enforces them. It can be **upgraded in place** to the full version.
Document 02, Part H describes it.

---

## The pieces

There are five programs and one website. They are all written in **C#** on **Microsoft .NET 10** (moved from .NET 8 on 2026-10-14).

```
   CUSTOMER'S COMPUTER                     THE SERVER                      TECHNICIAN'S COMPUTER
 +----------------------+          +---------------------------+         +-----------------------+
 | Tray app             |<-------->|                           |<------->| Technician console    |
 |  (asks for help,     |          |  The platform             |         |  (machine list,       |
 |   chat, permission)  |          |   - website & portals     |         |   calls, sessions,    |
 |                      |          |   - message hub & relay   |         |   tickets, chat)      |
 | Agent service        |<-------->|   - database              |         |                       |
 |  (unattended access, |          |                           |         |  Session window       |
 |   runs all the time) |          |                           |         |  (screen, mouse,      |
 |                      |          |                           |         |   keyboard, files...) |
 | Host                 |<-------->|  passes the picture and   |<------->|                       |
 |  (started for each   |          |  the mouse/keyboard       |         |                       |
 |   session: captures  |          |  between the two ends     |         |                       |
 |   screen, moves the  |          |                           |         |                       |
 |   mouse, files...)   |          |                           |         |                       |
 +----------------------+          +---------------------------+         +-----------------------+
                                            ^
                                            |  web browser
                                   Portal for office staff, owner portal,
                                   customer download page, website chat
```

### On the customer's computer (three programs)

- **The tray app** (`RemoteSupport.Client`). The small icon by the clock. The customer uses it to
  ask for help, chat, choose whether unattended access is allowed, set an unattended password, let
  somebody outside their IT company in with a code, and **uninstall** the whole product.
  It also asks the customer's permission before a technician connects. **A free-version computer
  has no tray app** — the same file is used only to install and set the password.
- **The agent** (`RemoteSupport.Agent`). A Windows service that runs in the background all the
  time, even when nobody is logged in. It is what makes **unattended access** possible, it installs
  and removes the monthly health check, and it **uninstalls itself** if the IT company removes the
  machine while it is online.
- **The host** (`RemoteSupport.Host`). Started fresh for each session and closed afterwards. It
  does the real work: takes pictures of the screen, presses keys and moves the mouse, copies
  files, runs scripts, captures sound, adds the technician's printer. It runs with full system rights
  so it can also work on Windows' secure screens (the password prompt, Ctrl+Alt+Del, the lock screen).
  On a free-version computer it also shows the **"Being controlled remotely"** bar for the whole session.

### On the technician's computer (one program)

- **The technician console** (`RemoteSupport.Technician`). The list of customer machines, the
  incoming-call alert, the session window (where the customer's screen appears), chat, tickets.

### The server (one program)

- **The platform** (`RemoteSupport.Platform`). A web server that does several jobs at once:
  1. **The public website** — marketing pages, download pages, website chat.
  2. **The portals** — the web pages office staff use for tickets, clients and settings, and the
     owner portal.
  3. **The hub and relay** — every program above connects to it. It rings technicians when a
     customer asks for help, and during a session it passes the screen picture one way and the
     mouse and keyboard the other. **It does not store the screen picture.** It just passes it on.
  4. **The database** — companies, staff, customers, machines, tickets, photos, chat history. On the
     live server it is **PostgreSQL** (since 9 October 2026), on a disk of its own; a developer's PC
     and most test tools use **SQLite**, a database in a single file, and the code works on both.
  5. **Payments** — card payments through PayFast, and the owner pages with the control dashboard
     (V2.2).

There is also an older program, `RemoteSupport.Server`, from before the product served many
companies. It is kept but no longer used live. Leave it alone unless you are removing it.

### Shared libraries (no program of their own)

- `RemoteSupport.Shared` — the "language" all the programs speak to each other, plus things
  every program needs (security rules, the two-factor code maths, device identity).
- `RemoteSupport.UI` — the look of the Windows programs (rounded buttons, colours, light and dark).
- `RemoteSupport.Media` — video and sound: turning the screen into compact video (H.264), playing
  it back, recording sessions, the microphone with echo cancelling.

---

## How a support call works, step by step

1. The customer clicks **Request Support** in the tray app and types what is wrong.
2. The tray app tells the server. The server **rings every technician** at that IT company who is
   signed in. A large alert appears in the middle of each technician's screen, even if the console
   is minimised and the sound is off.
3. A ticket is opened automatically with the customer's description.
4. The first technician to click **Accept** gets the call.
5. The server tells the customer's computer to start a **host**. The host connects to the server.
6. The customer sees a **permission prompt** ("Allow a remote support connection?"). If nobody
   answers in time, the answer is no.
7. Once allowed, the technician sees the customer's screen and can use the mouse and keyboard.
   Other technicians can join the same session.
8. When the session ends, the ticket is updated with what happened (who connected, chat, files,
   scripts run, how long it took).

**Unattended access** is the same, except the technician starts it from the machine list, and
the customer is not asked each time — because the customer has ticked *Allow unattended access*
in their tray app. If the machine has an unattended password, the technician types it once and it
is remembered for them. More in document 02.

**On the Free and Basic versions** there is no call: the one person picks one of their computers, types
its password the first time, and is in — or, on Basic, connects to one without a password and the person there is asked. The computer shows *Being controlled remotely by …* at the
top of its screens while they are.

---

## How the programs talk

- Everything travels over **one secure web connection** (HTTPS and secure WebSockets on port 443).
  Nothing needs a special port opened on the customer's network: every program dials **out** to
  the server, the way a web browser does.
- There are three kinds of connection on the server:
  - `/ws/device` — each customer computer (the tray app and the agent) keeps one open.
  - `/ws/tech` — each signed-in technician console keeps one open.
  - `/ws/session` — one for each live session, for the host and each technician viewing it.
- Messages are small pieces of JSON text. The screen picture, file contents and sound are sent as
  compact binary pieces. The definitions are in `src/RemoteSupport.Shared/Protocol.cs`.

---

## How one server serves many IT companies

- Each IT company has a **tenant key**: a long random code built into its downloads. When a
  customer computer connects, it shows this key, and the server knows which company it belongs
  to. The key is an **address**, not a password — it is public by design (see document 03).
- Inside the server each company gets its **own separate hub** in memory. Companies cannot see
  each other because they are not in the same place at all. The hub also knows whether its company
  is on the free version, and applies those limits itself (`Hub/TenantHub.Free.cs`).
- Every company downloads the **same program files**. Only the branding file (name, colours,
  logo, server address, tenant key) is different. That is what makes one code-signing
  certificate cover every reseller.
