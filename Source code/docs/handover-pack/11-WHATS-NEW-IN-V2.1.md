# 11 — What is new in V2.1

**V2** of this pack was written on **6 October 2026**. **V2.1** was written on **7 October 2026**. This
page lists what was added in that one day, so somebody who read V2 does not need to read everything
again. Documents 00–09 have been updated to match; document 10 still lists what V2 added to V1.

Every item below was **built and passed its automatic test tools**, but **none of it has yet been tried
on real customer computers**. The hardware checklist is `docs/TEST-3.6.md` (the lettered section is
named in each row), and the numbered section of `docs/HANDOVER.md` is the full engineering record.

---

## The biggest change: sessions are encrypted end to end

Before this, the connection between each program and the server was encrypted, but **inside the
server** a session could in principle be read: the picture, every key typed (passwords included), the
clipboard, files, sound and scripts. Now the customer's computer and the technician's console agree a
key **the server never sees**, and the server only passes sealed data along.

| What | In short | Record |
|---|---|---|
| Sealed sessions | Everything a session carries is sealed with AES-256-GCM between the two ends. The server still sees who connected to what and when, and the audit lines a ticket needs (file names moved, a script's first line, restarts) | §112, CC |
| Each computer has an identity | A key made on the customer's computer, readable only by Windows' system account, signs every session. Each console remembers it; if it ever changes (a reinstall, or somebody pretending) the console **stops and asks** before showing or sending anything | §112, CC |
| It says which | The session window shows a green *End-to-end encrypted*, or *Not end-to-end encrypted* when an older program is involved — older programs still work, by Clint's decision | §112, CC |
| The browser too | Browser support and the guest's browser page seal the same way | §112, CC |

**The honest limit:** a server that had been taken over could still *start* a session of its own; it
could not read or take over a real technician's. Document 03 explains it.

---

## Other new work

| What | In short | Record |
|---|---|---|
| **Recurring tasks** — a sixth ticket category | Set up once — daily, weekly on chosen days, or monthly — for maintenance, billing, readings or the company's own types. A ticket opens for its people at 06:00 company time each day it is due; Done closes it, or To be invoiced. Cancel stops future tickets only | §108, BY |
| **A ding and a ring** | A soft ding for every chat message from somebody else; a soft ring every four seconds while a support request or a website visitor waits. A bell in the console mutes them on that PC; the portal has its own button | §109, BZ |
| **Windows shortcuts go to the customer's computer** | While the picture has the keyboard, the Windows key and its shortcuts, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc and Print Screen act over there, not on the technician's PC. No switch, by Clint's decision. Win+L and Ctrl+Alt+Del cannot be taken by any program — the toolbar has buttons for both | §110, CA |
| **A reinstalled machine waits for the office** | A machine recognised after a reinstall is matched only by its hardware, which any program on it can read. So unattended access now waits until a supervisor or the owner confirms it is the same machine (right-click → Confirm, or the Devices page) | §111, CB |
| **Passwords scrambled harder** | 600,000 rounds instead of 120,000. Each person's is re-made at their next sign-in; nobody is signed out | §111, CB |
| **Branding reaches installed machines** | When a company changes its logo, colours, wording or permission-prompt settings, every installed customer computer takes the new look — at once if it is on, otherwise when it next connects. Only what a person sees travels, never where the computer connects. The tray app restarts itself quietly, only when nobody is using it | §113, CD |
| **The console's windows carry the company's logo** | Before, only the main window did; the sign-in, session, ticket and chat windows showed Windows' default icon, and so did the console's shortcuts | §114, CE |

The last two security items agreed on 1 October — the hardware-fingerprint takeover and the password
rounds — are now **built** (document 03, section 9). What remains there is code signing, an outside
penetration test and a copy of the backups off the server.

---

## New test tools

`recurringcheck`, `soundcheck`, `shortcutcheck` (it presses real keys — leave the keyboard alone while
it runs), `reinstallcheck`, `hashcheck`, `e2ecore`, `e2ewire`, `e2einterop` and `brandpush`.
`iconcheck` and `consoleexecheck` gained checks. Each one starts its own throwaway server where it
needs one.

## Putting V2.1 live

All of it goes out with `deploy\Publish-Platform.ps1` (two database changes, `RecurringTasks` and
`ReinstallConfirm`, applied by themselves), then **every technician re-downloads the console**. The
encryption, the reinstall check and branding also change the customer's programs, so they need a
**full ship and a reinstall** on customer computers. Document 05 has the commands.

## Changed on 8 October 2026: five seats, ten at most

Clint changed the licence after V2.1 was first made. The package is now **five seats** (still R500 /
$49 / £49 / €49 a month), extra seats are still R300 / $20 / £20 / €20, and **no company can have more
than ten seats**: the product is for small and medium IT companies. One owner-only login is free;
everybody else takes a seat, a second owner-only login included. The South African terms say so
(version 2026-10-08), and so does the holding page. Document 02 (G6) is updated; `docs/PRICING.md` and
section 116 of `docs/HANDOVER.md` are the record. It goes live with `deploy\Publish-Platform.ps1`
(database change `FiveSeatPackage`, which moves every company to five); nothing on customer computers
changes.

The same day, **a company may have several owners** (section 117): only an owner can make somebody an
owner, change an owner's roles or remove one; there is always at least one; and every owner with no
other role, past the first, takes a seat.

## Added on 9 October 2026: updating the client from a ticket

A comment can be emailed to the client from the ticket — the fault the ticket was opened for and that
one comment, with the writer's name, from the company's support address; the client's reply comes back
onto the same ticket and flags the people on it. Signup asks for the support address. Document 02 (C4b)
is updated; section 123 of `docs/HANDOVER.md` is the record. It goes live with
`deploy\Publish-Platform.ps1` (database change `ClientUpdates`) and a console re-download; nothing on
customer computers changes.
