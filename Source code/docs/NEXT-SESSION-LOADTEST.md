# Next session: the xneelo load test (step 5 of 5) — how many companies one cell carries

Written 2026-10-12, after the live switch (§125). **Read HANDOVER §115, §121 and §125, BUGS #11, and
`deploy/loadtest/README.md` first** — this brief is the goal, the decisions and the gaps; those are the facts.

## The question Clint wants answered

*"How many tenants can we sign up, with at most 10 technicians each, before we need to set up the next cell?"*

A **cell** is one complete, independent copy of what runs on xneelo today: one server (platform + Caddy +
PostgreSQL on the same machine), its `rsp-files` and `rsp-db` volumes, its nightly backup, its own address —
serving its own set of companies, sharing nothing with any other cell. So the number we need is
**companies per server like live's**, with a margin, and **which limit bends first** (processor, memory,
PostgreSQL, network) — because that says whether a bigger xneelo flavor would buy more (scale up, a panel
change) or only a second cell would (scale out, which needs code — below).

## What one company is (the profile) — CONFIRM WITH CLINT FIRST, then do not re-ask

Proposed, from the limits we already have (10 seats at most, §116) and a busy IT company:

| | Proposed | Why |
|---|---|---|
| Technicians signed in | **10** (all of them, all day) | the worst case the seat limit allows |
| Machines | **500**, each with tray app AND agent (2 sockets) | a 10-technician IT company; ask Clint his real ratio |
| Remote sessions at the busiest moment | **3** at once | 30% of technicians in a session |
| Picture per session | **200 KB/s** | the scrolling figure from §2.2 (tool default) |

**Per-company shape matters, not only totals**: a machine's change is broadcast to every signed-in console of
ITS company, so cost grows with machines × consoles per company. Seeding 1,000 two-person companies (what
`deploy/loadtest` does today) answers a different question.

The answer is then: companies per cell = the measured limit ÷ one company's share, taken at **about 60–70% of
the point where it bends** (room for a restart storm — every machine reconnecting at once after a deploy, BUGS #11).

## What exists, and the gaps to close BEFORE the run

`tools/loadtest` (pretend machines, technicians, sessions; VERDICT; `local` mode) and `deploy/loadtest/`
(Prepare-LoadServer, Prepare-LoadBox, Start-LoadRun, Watch-LoadServer, Run-LoadStep A–E, Save-Addresses).
**Never run on xneelo.** Gaps, each to be built and proved on this PC first:

1. **The load server must be like live: PostgreSQL on its own `rsp-db` volume.** `Prepare-LoadServer.ps1`
   installs with `Publish-Platform.ps1`, which gives SQLite. Use the switch that is already proven
   (`deploy/postgres/Switch-ToPostgres.ps1 -FilesGB 10 -DbGB 20`) after the install and BEFORE the seed — or a
   `-Postgres` step inside Prepare-LoadServer that does exactly that. Then `Get-DatabaseStatus.ps1` must say
   PostgreSQL before any run.
2. **Companies of 10 people and hundreds of machines.** `seed-loadtest <companies> <people> <file>` already
   takes people per company; Run-LoadStep's steps (`-Machines 10/20`, 1,000 companies, two people) must become
   steps by COMPANY COUNT at the profile above — e.g. 10, 20, 40, 80 companies of 10 people × 500 machines
   with agents × 3 sessions — and the tool must sign in ALL ten people of each company it uses. Check what
   `tools/loadtest` does with people beyond the first; prove the new steps with `loadtest local` first.
3. **The server's own readings**: `Watch-LoadServer.ps1` / `rsp-loadstats.sh` read /proc and were never run
   (no Linux here). Add PostgreSQL's (connections in use against the cap of 40, `pg_stat_activity`, slow
   queries) and the network's (bytes in/out per second) — bandwidth is the most likely ceiling: 3 sessions ×
   200 KB/s × 80 companies is 48 MB/s, about 400 Mbit/s, through ONE server.
4. **The load boxes may run out first** — the tool already says so in its VERDICT; then add a third box or a
   bigger flavor rather than read it as the server's limit.
5. Two-factor off on the load server only (Prepare-LoadServer does it), never live; every script refuses the
   live address — keep that.

## The xneelo set-up (lessons already paid for in the rehearsal, §125)

- **Server `rsp-loadtest`: the SAME flavor as live** (look at `rsp-server` → Overview; live is 4 vCPU / 7.8 GB),
  **Ubuntu 24.04 — not 26.04**, boot volume 20 GB, **live's security groups — not `default` alone** (it drops
  80/443), key pair `clint-laptop`.
- **Two volumes, different sizes (xneelo's smallest is 10 GB)**: `rsp-loadtest-files` 10 GB standard,
  `rsp-loadtest-db` 20 GB **premium** (as live's `rsp-db` — the database's disk speed is part of what is measured).
  Attach both. Never trust `vdb`/`vdc` — the switch finds them by size.
- **Two load boxes** `rsp-load-1`, `rsp-load-2`: Ubuntu 24.04, 2 vCPU / 4 GB to start, live's security groups.
- **Cloudflare**: A record `loadtest` → the server's address, grey cloud. (`www.loadtest` will fail its
  certificate — harmless, §125.)
- A rebuilt or new instance needs `ssh-keygen -R <address>` on this PC.
- **Delete everything afterwards** (three instances, two volumes, boot volumes, the DNS record) — billed until
  deleted. Ask Clint to check xneelo's charge for data transfer before the session-heavy steps.

## How Clint wants to work (do not re-learn)

- **Ask before building or running harnesses** — he edits in this folder too (he may say "no need to ask again").
- One command per step, in its own `bash` block, as `powershell -ExecutionPolicy Bypass -File …`, with the real
  addresses filled in once he gives them. Never his passwords in chat.
- **Measure before changing code**; prove a check fails with the fault back; stage files explicitly (never
  `git add -A`); every `.ps1` and script sent to a server plain ASCII; no double quotes inside an ssh argument
  from Windows PowerShell; strip `\r`.
- The xneelo panel is click-by-click for him; he pastes outputs back — read them closely (the rehearsal's
  status output caught the two 10 GB volumes and the 26.04 image).

## What to deliver

`HANDOVER §126`: the profile used; per step the companies, connections, sessions, processor, memory, PostgreSQL
connections, bandwidth, connect/first-picture times and VERDICT; **the first limit to bend and at how many
companies**; the recommended number of companies per cell (with the margin); and whether a bigger flavor or a
second cell is the next move. Update `CLAUDE.md` and `docs/PRICING.md` if the number changes the plan.

## Not in scope (but say so in the answer): what a SECOND cell needs in code

A second server is a copy of the first, but the program assumes one: (1) a company's installed machines and
consoles carry their server's address for good (branding updates never change it, §113), so a company is placed
on a cell at signup and stays — moving one later is a reinstall; (2) signup must place a company on a cell with
room (§84's `RegionServers` already sends a REGION to another server — the same idea, by fullness); (3) the public
address must send each company's sign-in, `/get`, `/download`, `/tech`, `/help` and web chat to its cell; (4) one
owner view and one billing across cells; (5) `Publish-Platform.ps1` and the office backup copy per cell. Build
it when the measured number says the first cell is two-thirds full, not before.
