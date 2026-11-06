# The roadmap in plain English

Written 2026-09-22 for reading, not for building. It says **what is finished, what is left, and
what to do next**, with no jargon.

Two companion files, and they win over this one when they disagree:

- `docs/ROADMAP-PARITY.md` — the same plan with the technical detail and the build order.
- `docs/HANDOVER.md` — the record of what is actually true in the code.

---

## Where we are, in one paragraph

The remote control half of the product is **built and working**. Screen, sound, keyboard and mouse,
files, folders, copy and paste both ways, reconnecting after a dropped line, restarting a machine,
the customer's real mouse pointer, and a video codec that cut the data used by roughly fourteen
times. The office half — customers, contacts, machines, tickets, photos, staff roles, light and
dark — is **also built**, but a large slice of it was written in the last few days and **has never
been run on a real machine**. So the next job is not building. It is proving.

---

## The stages, and where each one stands

| Stage | In plain words | Where it stands |
|---|---|---|
| **1** | Core remote control — screen, mouse, keyboard, files, reconnecting, restart | **Finished and proven on real machines.** Three optional extras left |
| **2** | Picture quality and data usage — the video codec | **Finished and proven.** Two loose ends, both testable at home |
| **3** | Sound | **Mostly finished.** Their sound reaches you and the Sound tab explains faults. Talking both ways built 2026-09-30, not yet on a real machine |
| **3.5** | Simplicity — the console gets out of the way | **Nearly finished.** Copy and paste is done and confirmed. Two items left |
| **3.6** | The dashboard and ticketing earning their keep | **Built in full. Never tested on hardware.** One item (staff roles as tick boxes) not built |
| **3.7** | A set route for each type of job | **Nothing built.** Waiting on you to write the five routes |
| **4** | Hosting, going online, security, seats and payment | **Not started.** The one that costs money |
| **5** | Digital signature on the installers | **Wired up and waiting.** Deliberately left until last |
| **6** | Mac, Linux, Android and iPhone | **Not started.** Last, on purpose |

---

## Stage 1 — core remote control

**Done:** files and folders both ways by drag and by copy/paste; the clipboard; reconnecting on
its own when the line drops; Ctrl+Alt+Del, Lock, Task Manager and Restart from the toolbar; the
Files tab opening on the customer's desktop; the customer being told in plain words what moved.

**Left, and none of it is urgent:**

- **1.6 Session recording** — record what the technician saw, as an audit trail. Both competitors
  have it. It records on your side, so it costs the customer's machine nothing.
- **1.7 Privacy screen** — blank the customer's monitor while you work. Useful in an open office.
  Must be proved with a picture of their actual screen: failing silently here is worse than not
  having it at all.
- **1.8 Remote printing and Wake-on-LAN** — real gaps, rarely wanted. Droppable.

## Stage 2 — picture quality

**Done:** the modern Windows screen capture (idle CPU went from a third of a processor core to
nothing); H.264 video compression (scrolling a document went from 2.8 MB a second to 198 KB a
second, and CPU from 12.7% to 1%); automatic quality that eases off when the line is struggling;
and the customer's real mouse pointer instead of a drawn ring.

**Left — two things, both testable on your own kit:**

- **Prove it backs off on a genuinely bad line.** Everything so far has been either a fast home
  network or a simulated fault. A phone hotspot on one bar is the real test.
- **The gaming laptop's NVIDIA chip refuses to do the compression.** It falls back and still works,
  but gaming laptops are exactly what customers ring about. Worth one session to chase.

## Stage 3 — sound

**Done:** you can listen to the customer's machine, and the Sound tab tells you *why* there is no
sound — muted speakers, wrong default device — instead of making you guess. Neither competitor
does that.

**Left:**

- **3.3 Microphone the other way** — talking to the customer through the tool instead of the phone. **Built 2026-09-30, both ways, in the app and the browser; not yet tried on a real machine.**
  Nice to have; the phone already works.
- **The Opus question** — a different way of compressing sound. Never decided. Low priority.

## Stage 3.5 — making it simple

**Done:** the right-hand panel is gone and every tool opens as its own movable window; the
customer's chat popup can be dragged and stays put; both sides remember their window positions;
the tray menu is down to three plain items; a support request must say what is wrong, and that
reason is the biggest text on your incoming-call alert; and **copy and paste works both ways**,
files and folders included, confirmed by hand.

**Left:**

- **3.5.2 Drag a file straight off the picture of their desktop.** Today you drag from the Files
  tab. This makes the gesture work on the screen itself.
- **3.5.3 Every movement shows on the ticket.** One line each: which way, what, how big, when.
  Automatic must not mean invisible.

## Stage 3.6 — the dashboard and ticketing

**Built, all of it, and not one part has been seen on a real machine.** Contacts under a customer;
the console home as Attended and Unattended tabs with machines grouped under their owner;
technicians signing in to the web portal at last; the Tickets button handing them a one-click
sign-in; what each role may do enforced on the server; three workshop photos in and three out;
every remote session getting its own ticket, with big jobs made of smaller ones; and light or dark
chosen by each person rather than imposed on everybody.

**Left:**

- **3.6.8 A person holds roles, not a role.** Today somebody is *either* owner *or* technician *or*
  office administrator, so a one-man business has to call itself the owner and see everything. This
  makes them tick boxes. It changes stored data, so it needs a careful upgrade step.
- **Testing all of the above.** `docs/TEST-3.6.md` is the checklist — 59 numbered checks, two of
  them ticked.

## Stage 3.7 — a route for each type of job

**Nothing built, and that is correct** — the routes are yours to write. There are now **five**
categories: General (the new default), Remote support, Walk in, Workshop and Call out. Each needs
its stages, who may move a job between them, what must be true before it leaves a stage, and who
it lands with to begin with.

They go into `docs/NEXT-SESSION-WORKFLOWS.md`. Two things worth knowing first:

- **Recommendation: build the five routes in**, rather than letting every reseller configure their
  own. A configuration screen in front of every IT company before they can open a ticket is exactly
  what this product is meant not to be.
- **If any category needs stages beyond Open, In progress, On hold, Resolved and Closed, the job
  gets much bigger.** If they all fit those five, it is rules only.

## Stage 4 — hosting, going online, security and money

Not started, and the first stage that costs money and waits on other people.

- **4.1 A secure address (TLS).** Today the server is a plain address on your home network. This is
  the one item allowed to jump the queue the moment a paying customer appears.
- **4.2 Replace the development key** still sitting in the code.
- **4.3 Reach beyond the local network** — a hosted server that customer machines dial out to.
- **4.4 Account security** — two-factor sign-in. Not optional once a second company is paying.
- **4.5 The rest** — monitoring, backups, password reset, and backing up the key ring (lose it and
  every stored workshop password is lost).
- **4.6 Automatic updates.** Two halves. The easy half — pushing a changed logo or colour to
  machines already installed — can be done any time. The hard half, replacing the program itself on
  a customer's machine, **must not ship before the signature in stage 5**.
- **4.7 Seats and pricing** — seven seats at R500, extras at R300. The code still has the old
  five-seat model.
- **4.8 Taking the money** — PayFast for South Africa, end to end.

## Stage 5 — the digital signature

**Deliberately last, and that is the right call.** An unsigned installer gives a Windows warning on
every customer machine, which is a poor first impression — but it costs nothing at all while you
are testing on your own two machines.

The decision is already made and recorded: **buy OV, one year, with the physical token — not EV.**
OV is $129 a year against EV's $349 for identical behaviour on every machine we ship to, and the
old reasons for EV expired in 2024. The business is registered, so nothing blocks the application.

**On the limited number of signings:** the physical token is **unlimited**. It is the cloud
alternative that is metered — roughly 18 full builds a year on the cheapest tier — which is itself
a reason to take the token. Either way the rule while testing is simple: **never sign a test
build.** Sign only when an installer is about to go to a real customer.

**Both done as at 2026-10-08.** The certificate is ordered and paid (Sectigo OV, two-year term,
through LeaderSSL), the YubiKey 5 NFC FIPS 140-3 has arrived, and the Windows SDK is installed and
checked. What is left is the CA's own identity check on the company, and generating the key on the
token when they send the instructions.

## Stage 6 — the other platforms

Mac host first, then a phone app for the technician, then Linux, then supporting a customer's own
phone. Four new codebases, and worth nothing until the Windows product is finished.

---

## Your two test machines, and what each is for

Everything below is done on the local network, with no signature and no hosting.

- **The desktop (DESKTOP-OPHQIDH)** — no hardware video compression. This is the machine that
  proves the **fallback** path: the older picture method, file transfers, copy and paste,
  unattended access. If something only works on fast hardware, this machine finds out.
- **The gaming laptop (DESKTOP-2N6FVGT)** — has the compression chips, including the NVIDIA one
  that currently refuses. This is the machine for **picture quality**, the codec, and the NVIDIA
  fault real customers will hit.

---

## Recommended next steps, in order

**1. Prove what is already built. Nothing new until this is done.**

Five separate pieces of work are finished and sitting untested: the whole dashboard and ticketing
stage, the tray menu changes, the unattended-access reporting fix, the new General category, and
device linking. That is the biggest risk in the project right now — not because any of it is likely
to be wrong, but because five untested changes debugged together is far harder than one at a time.

The checklist is `docs/TEST-3.6.md`. It needs one full build shipped to both machines, the platform
restarted, and the desktop's client reinstalled.

**2. Fix whatever that finds.** Assume it finds something. Every hardware run so far has turned up
at least one fault no test harness could have — the pointer that never moved because nobody was at
the machine, the clipboard that kept crossing after the technician had gone, the paste Explorer
refused to perform.

**3. Write the five job routes** (stage 3.7) while the testing is going on. They need no machine,
and they are the only thing blocking the next real build.

**4. Build stage 3.7**, then the two leftovers in 3.5 (drag off the picture, movements on the
ticket) and 3.6.8 (roles as tick boxes).

**5. Chase the two stage 2 loose ends** — the bad-line test over a phone hotspot, and NVIDIA on the
gaming laptop. Neither needs anything bought.

**6. Then, and only then, stage 4** — hosting and the secure address, the first thing that costs
money and the last wall between this and a paying customer.

**7. Order the signing certificate a few weeks before you intend to sell**, not before. Nothing in
stages 1 to 3.7 needs it.

---

## Faults still open

- **The "ding"** when pasting on the customer's machine. Never explained. Harmless.
- **A file transfer over a genuinely poor line.** The fix is proven on a simulated bad link and has
  never been pushed over a real one. Same test as the stage 2 loose end above — do them together.
- **The desktop shortcut's logo has a black square behind it.** The logo file has no transparency.
  Fixed by supplying a transparent image, not by changing code.
