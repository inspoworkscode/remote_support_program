# Moving the live server to PostgreSQL

Step 4 of HANDOVER §118. The live server moves from its SQLite file to PostgreSQL on the same xneelo server,
and its data and backups move onto their own disks (`rsp-files`, `rsp-db`) — in one quiet window, with one
command, and one command back. **First a full rehearsal on a throwaway server with a copy of live's data.
Nothing touches the live server until the rehearsal has passed and Clint says go.**

What each piece is and how it was proved: HANDOVER §125.

---

## The rehearsal

About an hour. Every command is run on this PC, one at a time; each asks for your key's passphrase. Where a
command says **1.2.3.4**, put the rehearsal server's address instead.

### Part 1 — in the xneelo panel

1. **Compute → Instances → Launch Instance**
   - *Details*: Instance Name **`rsp-rehearsal`**
   - *Source*: Image **Ubuntu 24.04** - the same as live, NOT a newer one (26.04 was picked once, 2026-10-09, and the
     switch rightly refused: a rehearsal on another Ubuntu proves nothing about live); Volume Size **10 GB**
   - *Flavor*: **2 vCPU / 4 GB**
   - *Networks*: **Public**
   - *Key Pair*: **clint-laptop** (the one the live server uses)
   - *Security Groups*: **the same ones the live instance `rsp-server` has** (look at it under Compute →
     Instances). NOT `default` alone: it lets in ssh but not the web (80/443), and the rehearsal's page then
     times out (found 2026-10-09; fix afterwards with Actions ▾ → Edit Security Groups).
   - **Launch Instance**. Wait until it says **Active**, and note its IPv4 address.
2. **Volumes → Create Volume**, twice — they must be different sizes, which is how the switch tells them apart:
   - Name **`rsp-rehearsal-files`**, Size **10 GB**, type **standard**. **Create Volume.**
   - Name **`rsp-rehearsal-db`**, Size **20 GB**, type **premium** (as `rsp-db` is). **Create Volume.** (Found
     2026-10-09: xneelo's smallest volume is 10 GB - two of 10 GB cannot be told apart, and the switch refuses.)
3. For each of the two new volumes: **Actions ▾ → Manage Attachments → Attach To Instance: `rsp-rehearsal`**
   → **Attach Volume**. Both should then say **In-use**.
4. *(Optional — it rehearses growing the root partition, as live needs.)* Find the instance's own 10 GB boot
   volume in **Volumes**, then **Actions ▾ → Extend Volume → New Size 20 → Extend Volume** (as you did for
   live's). If the panel will not do it while the server runs, skip this step.

### Part 2 — in Cloudflare

`theremotesupportprogram.com` → **DNS** → **Add record**: Type **A**, Name **`rehearsal`**, IPv4 address =
the rehearsal server's address, Proxy status **off** (grey cloud). **Save.** Change nothing else.

### Part 3 — on this PC

1. Put the platform on it, exactly as live has it (about 10 minutes; ends with *Done*):

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server 1.2.3.4 -Domain rehearsal.theremotesupportprogram.com
   ```

2. Make it a copy of live (four passphrases; ends with *The rehearsal server is a copy of live*). This reads
   live's newest nightly backup and changes nothing on live. **It blocks the rehearsal server's outgoing mail
   first, and proves it** — the copy holds real companies' mail settings:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\postgres\Copy-LiveToRehearsal.ps1 -Rehearsal 1.2.3.4
   ```

3. Look at it, still on SQLite — open **https://rehearsal.theremotesupportprogram.com/login** and sign in as
   you do on live (same password and authenticator; it is a copy). Open a ticket. Then:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server 1.2.3.4
   ```

4. **Step A, rehearsed** — a normal deploy on top of the copied data, as live will have some days before:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server 1.2.3.4 -Domain rehearsal.theremotesupportprogram.com -SkipBuild
   ```

5. **The switch** (the rehearsal's volumes are 10 and 20 GB). It looks first and changes nothing, then asks you
   to type **SWITCH**:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-ToPostgres.ps1 -Server 1.2.3.4 -FilesGB 10 -DbGB 20
   ```

6. Check by hand: sign in to the rehearsal portal again, open a ticket, and **add a note to it** (a write that
   exists only in PostgreSQL — step 10 will report it). Then the status — it should say PostgreSQL, with
   `/srv/rsp-files`, `/var/lib/postgresql`, `/opt/rsp/data` and `/var/backups/rsp` all on their disks:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server 1.2.3.4
   ```

7. A deploy after the switch must keep it — look for **Database: PostgreSQL** in its output:

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server 1.2.3.4 -Domain rehearsal.theremotesupportprogram.com -SkipBuild
   ```

8. A restart must bring the disks and PostgreSQL back by themselves:

   ```bash
   ssh ubuntu@1.2.3.4 sudo reboot
   ```

9. Wait two minutes, then the status again (still PostgreSQL, everything mounted, `/health -> 200`):

   ```bash
   powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server 1.2.3.4
   ```

10. **The way back.** It first lists what changed since the switch (your note's table should be there), then
    asks you to type **BACK**:

    ```bash
    powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-BackToSqlite.ps1 -Server 1.2.3.4
    ```

11. The status says SQLite again, and on the portal your note from step 6 is gone — as expected:

    ```bash
    powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server 1.2.3.4
    ```

12. **The switch once more** — the disks and PostgreSQL are already there now, so this proves a second run:

    ```bash
    powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-ToPostgres.ps1 -Server 1.2.3.4 -FilesGB 10 -DbGB 20
    ```

13. Tell Claude **"the rehearsal is done"** (or where it stopped). Every step's output is saved in
    `deploy\reports\`.

### Part 4 — afterwards

- xneelo: **delete the instance `rsp-rehearsal`, AND the volumes `rsp-rehearsal-files` and `rsp-rehearsal-db`**
  (and its boot volume, if the panel keeps it) — volumes are charged until deleted, and these hold a copy of
  live data and live's key ring.
- Cloudflare: delete the `rehearsal` record.

---

## The live day — only after the rehearsal passed, and Clint says go

**The rehearsal passed on 2026-10-09** (HANDOVER §125): the switch was down 8 seconds, and a deploy, a reboot, the
way back and a second switch all worked.

**Step A — some days before, at any time:** a normal deploy, still on SQLite. **No `-SkipBuild`** — the
rehearsal left a build made for the rehearsal's address in `dist\`.

```bash
powershell -ExecutionPolicy Bypass -File deploy\Publish-Platform.ps1 -Server 154.65.110.136
```

**Step B — at a quiet time:** the switch. The platform is down for a minute or two; machines reconnect by
themselves.

```bash
powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-ToPostgres.ps1 -Server 154.65.110.136
```

Then by hand: the portal, a ticket, the machines coming back online. The restart Ubuntu has been waiting for
is a good test of the disks — `ssh ubuntu@154.65.110.136 sudo reboot`, two minutes, then:

```bash
powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server 154.65.110.136
```

**If something is wrong** — the way back:

```bash
powershell -ExecutionPolicy Bypass -File deploy\postgres\Switch-BackToSqlite.ps1 -Server 154.65.110.136
```

**About a week later:** retire the SQLite fallback (the frozen `platform.db` and the system-disk copies in
`/var/lib/rsp-before-disks`) — ask Claude; that step is not built yet.

The office copy of the backups (`deploy/backup`) is unaffected: every path it uses is the same.
