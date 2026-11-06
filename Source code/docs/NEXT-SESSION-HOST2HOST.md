# Copying files straight from one machine to the other — the brief

**BUILT THE SAME DAY (2026-10-15), NOT on hardware — HANDOVER §144 is what was built and proved; `TEST-3.6.md` section DA
(698–706) is what is left to see on real machines.** Clint's answer to all six questions below: *"go with your recommendations"*.
One thing changed in building: the hub's yes is the ask sent back, and only then does the console hand out the key — so a
refusal never leaves a host waiting for a partner that is not coming.

Clint, 2026-10-15, with two session windows open (HANDOVER §143): *"Is it possible to build the copy and paste to
work directly between both machines that are open instead so that it doesnt go through the technician PC?"*

## What happens today

Ctrl+C on machine A → the console fetches the files to the technician's PC (`Downloads\Remote Support\...`) and puts
them on its clipboard → click into machine B's window → the console sends them up to B → Ctrl+V on B. So the files
cross the **technician's internet line twice** (down from A, up to B), cross **our server twice**, and **a copy stays on
the technician's PC**.

## What this changes

Same gesture — **Ctrl+C on A, click into B, Ctrl+V on B** — but the files go **A → our server → B**. The technician's
line and PC are left out of it entirely. The server is crossed once instead of twice.

**Why through the server and not truly direct.** Direct (A's machine talking to B's machine) needs one of them to
open a listening port as SYSTEM and needs them on the same network or a NAT trick. It would work for two office
machines on one LAN and fail for a remote worker, which is half the reason Clint asked. Through the server works
wherever the two machines are and opens nothing on either machine.

**Text stays as it is**, through the PC: it is a few characters, already in the console's memory, and the technician
may want it on their own PC too.

## How it works

### 1. The console decides, when the technician clicks into B

When A's customer copies files, A's window learns of it as now (`ClipboardMsg.Files`, with the size). Then:

- **One window open** — nothing changes. The files come to this PC exactly as today.
- **Two windows open** (see question 1) — the files are **not** fetched to this PC. A's status bar says *"3 files
  copied on DESKTOP-A — click into DESKTOP-B's window and paste, or Bring them to this PC"*. The technician's own
  clipboard is emptied as now (§54), so nothing stale can be pasted.
- **Click into B** — B's window sees A has files waiting (`SessionForm.Between.cs` already knows the other window)
  and asks the server to **bridge** them: A sends, B receives. B's status bar says *"files from DESKTOP-A arriving —
  don't paste yet"*, then *"ready to paste"*.
- **B's clipboard is emptied the moment the bridge starts**, so a Ctrl+V too early pastes nothing rather than B's
  own old files (the same rule as §54, on the other machine).
- When the last byte lands, **host B puts the files on its own clipboard as the person** (`SetFilesAsUser`, §39) —
  no round trip through the console — and tells B's window they are ready.

Nothing reaches B until the technician clicks into B's window: **the rule of §26 stays — a copy reaches a customer
only through the window in front.** That is why the bridge starts at the click and not at the copy: starting at the
copy would put customer A's files on customer B's disk before anybody chose B.

### 2. The server checks who may bridge — the hub, not the console

The console sends one clear message on its own connection: *bridge set X from session A to session B*. The hub
refuses unless **all** of these hold, in one rule (`TenantHub.Bridge.cs`, a new partial):

- the same technician has a live viewer socket in **both** sessions right now (`Session.ViewerIds`, already there
  from §143);
- both sessions are this company's own — **never** an outsider's visit (§89), a guest (§90/§91), browser support
  (§66) or a call still waiting for its yes;
- both sessions are **sealed end to end** (§112) and neither is held on *"Is this the same computer?"*;
- both hosts said they can bridge (a new word in the host's `SysInfoMsg`, which the hub already keeps — an older
  host is never asked);
- not over `FileSetLimits.MaxBytes`, and at most one bridge per pair at a time.

Refused → the console falls back to the PC route on its own (question 6), and the status bar says why.

### 3. The encryption: a fresh key per copy, handed over by the console

End-to-end encryption today is **per session**: console ↔ host A has one key, console ↔ host B another. Host A
cannot read B's key and should not. So each copy gets **its own key**:

- the console makes a random 256-bit key for this one transfer;
- it gives it to host A **inside session A's seal** and to host B **inside session B's seal** (a new sealed message,
  `BridgeKeyMsg`, never on the clear list) — the server relays both and can read neither;
- A and B seal the file chunks with that key (AES-256-GCM, the same frame shape as `SessionSeal`, sender numbers
  1 and 2, a counter, the bridge id as associated data) — the server routes them by the bridge id in a short clear
  header, and cannot read a byte of the files;
- the key is thrown away when the transfer ends.

**Why the console vouches rather than A and B checking each other's identity.** A and B could do their own key
exchange signed with their machine identities (§112), but each would then need to know the other's identity key —
and the only parties that could tell them are the console (which already checked both, `MachinePins`) or the
server (the very party we are hiding from). So the console vouches either way; handing over a key is the same trust
with far less machinery. The console could read the files — but it is the technician's own console, which can
already pull those same files today.

**Each host refuses a bridge whose key arrived unsealed.** A server that wanted to send A's files somewhere could
otherwise just ask. Clear sessions (an older console in the session) never bridge — the PC route as today.

### 4. Flow and failure

- **A host sends at most 8 MB ahead of what B has acknowledged.** Today a slow receiver stalls the hub's read loop
  on the sender's socket (`BinaryRelay`), which is right for one session — but here it would freeze **A's picture**
  because **B's** line is slow. A window of credit keeps the hub from ever having to hold A back.
- **Either window closed, either session ended or dropped, the technician gone from either** → the hub ends the
  bridge and tells both hosts; A stops, B deletes the half-written staging folder, both windows say so.
- **No progress for 30 s** → ended the same way (the `ClipFetchStallMs` rule §143 already uses).
- Files land in B's `%PASTE%` staging under their temp, as every paste does, swept after 12 hours.

### 5. The size rule

Unchanged in shape (question 4): under `FileSetLimits.AutoFetchBytes` (50 MB) the bridge starts when the
technician clicks into B; over it, B's status bar offers *"Send the 3.2 GB copied on DESKTOP-A to DESKTOP-B"* and
waits to be clicked. `MaxBytes` and `MaxEntries` still refuse outright.

### 6. The tickets and the customers

Each machine's host reports what actually moved, as now (`FileActivityMsg`, which is already clear for exactly
this): **A** *"3 files (12 MB) copied to another computer by Sue Tech"*, **B** *"3 files (12 MB) arrived from
another computer, sent by Sue Tech"*. The hub fills in the other machine's name for the **ticket** line — never
trusted from a host, the way `RecordingStateMsg.By` is filled in — and the tray app on each machine shows the
customer's version (question 3). The clip ledger (§56) is untouched: files never had ledger lines of their own.

## What it touches

| Where | What |
|---|---|
| `Shared/Protocol.cs` | `BridgeAskMsg` (console → hub, clear), `BridgeKeyMsg` (console → host, sealed: id, key, role, the paths for A / `%PASTE%` for B), `BridgeEndMsg` (hub → hosts, clear), a `BinKind.Bridge` frame with a clear bridge id; `FileActivityMsg.OtherMachine` (hub-filled) |
| `Shared/SessionSeal.cs` + `wwwroot/e2e.js` | the bridge frame and `BridgeEndMsg` on the clear list (the hub routes and ends them); both lists together or `e2ecore` fails |
| `Shared/BridgeSeal.cs` (new) | the per-transfer seal: one key, two senders, counters, replay refused |
| `Host/HostEngine.cs` | send a bridged set (the existing `SendFileSetAsync` with a different socket target and the credit window), receive one (the existing `FileSetReceiver`), empty the clipboard at the start, set it as the person at the end |
| `Hub/TenantHub.Bridge.cs` (new) | the one rule of who may bridge; the route table; end on any leave; the ticket lines |
| `Technician/SessionForm.Between.cs` | decide bridge vs PC at the click; the status words; *Bring them to this PC*; fall back |
| `Hub/TenantHub.Free.cs` | the free version's allow-list (question 5) |

## How it will be proved

- **`tools/bridgewire`** (new), on a platform of its own like `sessionlimitwire`: two stand-in hosts running the
  real `HostEngine` file code, the real hub, a real `SessionLink` per session. Files and folders (an empty one too)
  arrive at B **byte for byte**; a marker inside the files appears **nowhere in what the server relayed and nowhere
  in its database**; refused for: another technician's session, an outsider's visit, a guest, a clear session, a
  held identity, an older host, over `MaxBytes`; a key sent clear refused by the host; a tampered or replayed chunk
  refused; B closed half-way → A stops and B's staging is gone; a slow B never freezes A's picture (frames keep
  arriving from A while B is throttled); both tickets carry their line with the other machine named. **Each guard
  taken out must make it fail** (the count with and without, as every harness since §54).
- **`tools/twosessions`** extended: the real `SessionForm` pair choosing the bridge, the status words, the fall back
  to the PC route with an older host, *Bring them to this PC*.
- **Re-run**: `e2ecore`, `e2ewire`, `pullcheck`, `pastecheck`, `clipledger`, `sessionlimitwire`, `outsidervisit`,
  `freecheck`.
- **Hardware** (new `TEST-3.6.md` section): two real machines, a copy each way, a folder, a big set over 50 MB,
  closing B half-way, and `userpaste`-style proof that Explorer offers Paste on B.

**Deploy when built:** `Publish-Platform.ps1` (no migration expected), console re-download, and a **FULL ship and
client reinstall** — the hosts change. Until a machine has the new host, its copies go through the PC as today.

## Questions for Clint — each with a recommendation (answered 2026-10-15: all six as recommended)

1. **With two machines open, should a copy on A still come down to your PC as well?**
   *Recommendation: no — machine to machine only, which is the point. A's status bar has "Bring them to this PC"
   for the times you want them yourself. With one machine open, nothing changes.*
2. **Between any two of your machines, or only two machines of the same customer?**
   *Recommendation: any two, as it works today through your PC — you choose by where you click and paste. Limiting
   it would make the same gesture work sometimes and not others.*
3. **What is the person at each machine told?**
   *Recommendation: "copied to another computer by Sue Tech" / "arrived from another computer, sent by Sue Tech" —
   without naming the other computer, because it may belong to a different customer. The tickets (yours, internal)
   name both machines.*
4. **Keep the 50 MB rule?**
   *Recommendation: yes — under 50 MB it goes when you click into B; over, B's window offers it and waits for a
   click. It no longer costs your line, but it still costs both customers' lines.*
5. **The free version too?**
   *Recommendation: yes — two machines and Files is exactly its shape.*
6. **When the direct way cannot be used** (a machine's app is older, or the server refuses), **or fails half-way?**
   *Recommendation: older app → through your PC automatically, as today, with the status bar saying why. Failed
   half-way → it says so and offers "Send through this PC instead" — never doubling the traffic without asking.*
