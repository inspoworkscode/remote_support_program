# Start here

**The Remote Support Program — handover pack, V2.5**

This pack is for whoever takes over **The Remote Support Program** if Clint (the founder) can
no longer look after it. It is written for a software development team, but it uses plain
language so the business side can follow it too.

**V2.5**, prepared 28 October 2026, is **the final version before the product goes public on 1 November** — it
replaces V2.4 (27 October 2026), and **document 15** lists the little that changed since, above all that **the guide (PDF)
is now in the zip**. Document 14 lists what V2.4 added to V2.3 (16 October 2026): **three plans** (Full, Basic and Free), **more seats
on request**, **job types**, **latest activity**, a technician asking for **outside help**, **exporting a company's data**,
the fixes from an **outside security review**, **removing a client**, and the **website ready to go public**. Document 13 lists what is new
since V2.2: **every program now keeps itself up to date** (customer computers silently, the console
before its sign-in window), released to our own company first, then the pilots, then everybody, with a
rollback if a new version does not come back. Document 12 lists what V2.2 added to V2.1: the live server moved to **PostgreSQL**, **PayFast** card payments, the **real website** (still hidden),
**platform admins and a control dashboard**, **code signing** in use, **pilot companies**, and a dozen ticket and
remote-control improvements. Document 11 lists what V2.1 added to V2 (above all **end-to-end encryption**), and
document 10 what V2 added to V1 (2 October 2026), the biggest being a **free version** of the product.
To refresh the pack later, run `tools\Make-HandoverPack.ps1` on the build PC.

---

## What this product is, in three sentences

1. It is a **remote support tool**, like TeamViewer or AnyDesk. An IT technician can see and
   control a customer's Windows computer over the internet, with the customer's permission.
2. It is also a **job and ticketing system** for an IT business: customers, their machines,
   workshop bookings, quotes, invoicing stages, photos, and staff chat.
3. It is sold to **other IT companies** — and other businesses that look after other people's computers and
   jobs — as an online service. Each company gets its own branded
   copy (its own logo, colours and download links), and all of them run on one shared server
   at **https://theremotesupportprogram.com**. The **Basic Version for Remote Workers** (R50 a month since 2026-10-25) lets one person reach
   up to three of their own computers (more at R10 each, up to 20), for example the office PC from home, attended or unattended;
   the **Free Version** does the same for ONE computer, unattended, for nothing. Both can be upgraded in place.

Every part of it was written from scratch for this business. No remote-control engine was
bought in or licensed, so there are no licence fees and nobody else's code to depend on.

The company that owns it is **The Remote Support Program (Pty) Ltd**, registration
**2026/737434/07** (South Africa). Its first customer is **Anywhere IT**, Clint's own IT company.

---

## What is in this zip

| Folder or file | What it is |
|---|---|
| `00-START-HERE.md` | This page. |
| `The Remote Support Program - Guide.pdf` | **The guide**, 30 pages, for anybody thinking of using or investing in the product: what it does and what it costs, with pictures. Start here if you are not a developer. |
| `Handover documents\` | The plain-English documents listed below, as text files (`.md`) and as one web page you can open in any browser (`Handover handbook.html`). |
| `Source code\` | Every file of the product: the programs, the website, the installers, the scripts, the test tools and all the technical documents. This is a copy of the latest version. |
| `Source code with history\` | The same code with its **full history** — every change ever made, when and why. A developer restores it with one command (see the `HOW-TO-RESTORE.txt` inside it). |

**Unzip it near the top of a drive** (for example `C:\Handover`). Some file paths inside are long,
and Windows refuses paths over 260 characters if the zip is unpacked deep inside other folders.

**Not in this zip, on purpose:** passwords, encryption keys, the live database, and the server
login key. Putting those on a USB stick would mean anyone holding the stick could take over every
customer's computer. Document 08 lists every one of them, says where each lives, and explains how
to hand them over safely.

---

## Read these in order

| # | Document | Who it is for | What it answers |
|---|---|---|---|
| 01 | What it is and how the pieces fit | Everyone | What the parts are and how they talk to each other |
| 02 | Every feature, and how it works | Everyone | What the product does, one feature at a time |
| 03 | How the security works | Everyone, then developers | How customers' machines and data are protected |
| 04 | Where it runs | Developers, the business | The server, the website address, email, backups, suppliers |
| 05 | Building and releasing | Developers | How to turn the code into programs and put them live |
| 06 | Map of the code | Developers | Which folder holds what |
| 07 | Where things stand | Everyone | What is finished, what is not yet tested, known faults, what was next |
| 08 | Keys, passwords and accounts | The business, then developers | Everything NOT on this stick and how to get it |
| 09 | Glossary | Everyone | What the words mean |
| 10 | What is new in V2 | Anyone who read V1 | Everything added or fixed between 2 and 6 October 2026 |
| 11 | What is new in V2.1 | Anyone who read V2 | Everything added on 7 October 2026 |
| 12 | What is new in V2.2 | Anyone who read V2.1 | Everything added between 8 and 15 October 2026 |
| 13 | What is new in V2.3 | Anyone who read V2.2 | Automatic updates, added on 16 October 2026 |
| 14 | What is new in V2.4 | Anyone who read V2.3 | Everything added between 17 and 27 October 2026 |
| 15 | What is new in V2.5 | Anyone who read V2.4 | The final version before going live, 28 October 2026 |

After these, a developer should read the technical documents in `Source code\docs\`, starting with
**`HANDOVER.md`** (the detailed engineering record, 173 numbered sections, about 12,500 lines),
**`ROADMAP-PARITY.md`** (the build plan) and **`NEXT-SESSION-FREE.md`** (the free version, in Clint's
words). `Source code\CLAUDE.md` is a short index of all of it.

---

## If you only have one hour

1. Read documents 01, 02 and 07 (or, if you know V2.4, just document 15; if you know V2.3, 14 and 15).
2. Read the table in document 08 and find out who holds the password manager.
3. Check the live website opens: https://theremotesupportprogram.com
4. Do not change anything on the live server until a developer has read document 04 and the
   `docs/GO-LIVE.md` runbook in the source code. Customer computers depend on it.

---

## The three things most likely to cause damage

1. **Losing the server's key folder (`data/keys`).** It unlocks every stored workshop device
   password and email password. The nightly backup contains it (and an office PC can fetch a copy
   every night — `deploy/backup/README.md`; check it is switched on), but the backup itself is locked
   with a passphrase kept in `/etc/rsp-backup-key` on the server. Both must be kept safe — see 08.
2. **Changing the setting called `SetApplicationName("RemoteSupportPlatform")`** in the server's
   `Program.cs`. It looks harmless. Changing it makes every stored password unreadable.
3. **Turning off two-factor sign-in on the live server.** It is switched off only inside test tools.
   A technician's password, without the second factor, would be enough to reach every customer
   machine of that company.

---

## A note on how this was built

The product was built between September and October 2026 by Clint working with an AI coding
assistant (Claude, by Anthropic). Every change was recorded in the history and in `HANDOVER.md`,
including what went wrong and why. The `tools\` folder holds about 200 small test programs
that prove each feature works — **run the relevant one before and after changing anything.**
That habit is the main reason the product works, and it is worth keeping.
