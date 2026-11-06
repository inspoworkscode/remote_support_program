# Next session: switching the live server to PostgreSQL (step 4 of 5)

Written 2026-10-08 at the end of the session that did steps 1–3. **Read HANDOVER §115, §118–§121 and BUGS #11
first** — this brief is the plan and the decisions; those sections are the facts and the proof.

## Where things stand

| Step | State |
|---|---|
| 1. The platform can run on PostgreSQL (`ConnectionStrings:Postgres`, `PgPlatformDb`, `Data/PgMigrations`) | DONE, §118 |
| 2. Every harness runs on either database (`RSP_TEST_DB=postgres`, `tools/Run-DbHarnesses.ps1`) — 61/61 on each | DONE, §119 |
| 3. The local load test again — BUGS #11 was cold hubs, fixed; PostgreSQL 3 ms through a restart storm, SQLite still stalls | DONE, §121 |
| **4. The live switch** | **BUILT 2026-10-09, §125 — waiting for the rehearsal (`deploy/postgres/README.md`), then Clint's go** |
| 5. The xneelo load test (`deploy/loadtest`, §115) | after 4 |

The live server is **still on SQLite**. Committed and not yet deployed: the hub fix (BUGS #11), the clipboard-line
fix (§119), the PostgreSQL pool cap — a normal `Publish-Platform.ps1` carries them (Step A below). Clint also has
fixes of his own to apply; **ask him before running builds**, they share this folder.

## What Clint decided (do not re-ask)

- Move to PostgreSQL **before** the xneelo load test, because it is needed anyway.
- PostgreSQL on the **same xneelo server** (data stays in South Africa; the privacy policy is unchanged).
- **Three disks**, all attached and confirmed by `deploy/Get-ServerReport.ps1` at 15:50 on 2026-10-08:
  - `rsp-server` = `vda`, 20 GB, the system disk — Clint enlarged it from 10 GB; the **root partition is still
    9 GB** (`vda1`; grow it with `growpart /dev/vda 1` + `resize2fs /dev/vda1`, online).
  - `rsp-files` = `vdb`, 50 GB standard, **blank** — for everything in `/opt/rsp/data` AND the backups.
  - `rsp-db` = `vdc`, 20 GB premium, **blank** — PostgreSQL's data directory. (He was offered the system disk
    instead and chose its own disk.)
- Backups: keep the nightly encrypted backup, kept on `rsp-files` not the system disk; **recordings left out of
  the nightly copy and backed up ONCE each** (a recording never changes); `pg_dump` instead of the SQLite copy.
- The office copy of the backups (`deploy/backup`, §120) is **built but not yet set up** by Clint; the switch must
  keep it working (it reads `/var/backups/rsp` as group `rspcopy`; `rsp-backup-serve` serves `rsp-YYYYMMDD.tar.gz.gpg`).
- One command for Clint on switch day; **one command back** to the untouched SQLite file; a quiet time.

## The live server (report, 2026-10-08)

`154.65.110.136`, login `ubuntu` (key only, passphrase-protected — every ssh/scp asks). Ubuntu 24.04.4, **4 vCPU,
7.8 GB memory (under 1 GB used), no swap**. Platform at `/opt/rsp` (service `remotesupport-platform`, user `rsp`,
behind Caddy on 443), data 41 MB (1 company, 5 machines, 64 tickets), SQLite. Backups 27 files, 443 MB, in
`/var/backups/rsp` on the 9 GB root. A reboot is waiting for updates. Only 22/80/443 open.

## What to build (in this order), each proven before the next

1. **The data copy** — `RemoteSupport.Platform copy-to-postgres --from <platform.db> --to "<conn>"`. A draft is in
   `docs/drafts/PostgresCopy.cs.draft` (moved out of `src/` so it cannot break a build; **never compiled**). Its
   design: refuse a SQLite file behind on migrations and a non-empty PostgreSQL; build the schema with
   `PgPlatformDb`'s migrations; copy every table in foreign-key order, ids kept, a self-referencing link (a
   ticket's parent) written empty then filled; move every id counter past the copied ids; then check EVERY table
   — row count AND a SHA-256 of every column of every row in key order, times to the microsecond. Hook it in
   `Program.cs` beside `seed-loadtest`. **Prove it here** on real-shaped data (a harness's SQLite file, the load
   test's, and a deliberately damaged copy that it must call DIFFERENT), then start the platform on the copy
   (`pgcheck`-style) and see it work.
2. **`go-live.sh` made PostgreSQL-aware** (every deploy rewrites these, so the switch must survive a redeploy):
   the connection string in a SEPARATE drop-in (`…service.d/postgres.conf`, which go-live does not overwrite)
   AND in `appsettings.live.json` (the CLI never sees systemd's environment, §72); `After=postgresql.service`;
   `rsp-backup` using `pg_dump` when PostgreSQL is in use, recordings excluded and backed up once each, backups
   on `rsp-files`. Keep the `rspcopy` group handling (§120).
3. **The switch script** (bash, run with sudo by a PowerShell wrapper Clint runs from his laptop):
   - finds the blank disks **by size and blankness — never trust `vdb`/`vdc`**, names can change across a
     reboot; refuses if anything is ambiguous or not blank; formats ext4 with labels; mounts by UUID with `nofail`;
   - `rsp-files` mounted e.g. at `/srv/rsp-files`, with `/opt/rsp/data` and `/var/backups/rsp` bind-mounted from
     it, so **no path the platform, go-live, backups or `rspcopy` use changes**; file counts checked after the move;
   - `rsp-db` mounted at `/var/lib/postgresql` BEFORE `apt install postgresql-16`, so the cluster lands on it;
   - role `rsp` + database `rsp`, **peer authentication over the local socket — no password exists**
     (`Host=/var/run/postgresql;Database=rsp;Username=rsp`); modest tuning for 8 GB;
   - fresh backup → stop platform → copy (step 1, must say OK) → switch config → start → `/health` → counts;
   - grows the root partition; stops with a clear message at the first failure, SQLite untouched.
4. **The way back**: remove the PostgreSQL drop-in and connection string, restart — on the SQLite file, which the
   switch never wrote. Anything written after the switch exists only in PostgreSQL: say so plainly.
5. **Rehearse on a throwaway xneelo server** before the live one — there is NO WSL on this PC. Clint creates an
   Ubuntu 24.04 server with two small extra volumes (sizes that tell them apart), deploys the platform to it
   with `Publish-Platform.ps1 -Domain` a test name (as `deploy/loadtest` does), restores a copy of a live backup
   onto it, and runs the switch AND the way back. Give him click-by-click steps.

Then the live day: **Step A** a normal deploy, still on SQLite, some days before; **Step B** the switch at a
quiet time; check by hand (portal, a ticket, machines back online); a week later retire the SQLite fallback.

## Traps already paid for (do not re-learn them)

- **`dotnet ef` needs `--context`** (`PlatformDb` for SQLite, `PgPlatformDb` for PostgreSQL). `PgInitial` must
  match the model before the copy — **run `tools/pgcheck` first**. **SUPERSEDED 2026-10-09 (§125): PgInitial is
  FROZEN — never regenerate it; a model change gets its own PostgreSQL migration beside the SQLite one**
  (`dotnet ef migrations add <Name> --context PgPlatformDb --output-dir Data/PgMigrations`, never `--namespace`).
- **PostgreSQL keeps microseconds, .NET ticks** — compare times read back, never held in memory (§119).
- **Pool cap**: Npgsql's pool and `max_connections` are both 100 — `PgPlatformDb.Prepare` caps the platform at 40;
  the CLI, backups and an administrator use the rest.
- **Windows PowerShell 5.1**: a `.ps1` must be plain ASCII (an em dash broke parsing); never double quotes inside
  an argument to `ssh` (send a script by `scp` or on standard input with `tr -d '\r' | bash -s`); `>` turns binary
  into text; an empty `""` argument is dropped (use `Start-Process -ArgumentList` with one string).
- **Line endings**: strip `\r` from every script that reaches the server (`sed -i 's/\r$//'`).
- **Measure before changing code**; prove a check fails with the fault back; give Clint one command per step in
  its own `bash` block, as `powershell -ExecutionPolicy Bypass -File …`; never take his passwords in chat.
- **Stage files explicitly** — other sessions commit in this folder; never `git add -A`.
