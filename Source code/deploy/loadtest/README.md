# The load test on xneelo — how many companies one cell carries

How much ONE server like the live one carries before it bends. `tools/loadtest` plays pretend companies —
their machines, their technicians and their remote sessions — against a **throwaway copy** of the platform,
on PostgreSQL on its own disks exactly as live is. The live server and its customers are never touched; every
script refuses the live address. HANDOVER §115, §121, §125 and §126; the brief is `docs/NEXT-SESSION-LOADTEST.md`.

**The company (Clint, 2026-10-12):** 10 technicians all signed in; 500 machines, each with its tray app AND its
agent; 10 remote sessions at once at 200 KB/s. About 1,030 connections and 2 MB/s of picture each way per company.

Try it on this PC first (no servers needed; `RSP_TEST_DB=postgres` puts it on PostgreSQL):

    dotnet run -c Release --project tools/loadtest/loadtest.csproj -- local --companies 5 --machines 500 --agents --techs 10 --sessions-each 10 --storm --resign 15

## Part 1 — in the xneelo panel

**Look first:** **Compute → Instances → `rsp-server`** (live) → *Overview*: note its **Flavor** and its
**Security Groups**. The test server gets the same flavor and the same groups.

Then **Compute → Instances → Launch Instance**, three times:

| Box | Name | Flavor | Boot volume |
|---|---|---|---|
| 1st | `rsp-loadtest` | **the same as `rsp-server`** | 20 GB |
| 2nd | `rsp-load-1` | 2 vCPU / 4 GB | 20 GB |
| 3rd | `rsp-load-2` | 2 vCPU / 4 GB | 20 GB |

For every one: *Source* image **Ubuntu 24.04** (NOT 26.04 — the switch refuses it); *Networks* **Public**;
*Key Pair* **clint-laptop**; *Security Groups* **the ones `rsp-server` has** (NOT `default` alone — it drops the
web ports). Then **Launch Instance**.

Then **Volumes → Volumes → Create Volume**, twice, and attach both to `rsp-loadtest`
(*Actions → Manage Attachments*):

| Name | Size | Type |
|---|---|---|
| `rsp-loadtest-files` | 10 GB | standard |
| `rsp-loadtest-db` | 20 GB | **premium** (as live's `rsp-db` — its speed is part of what is measured) |

Never mind which one is `vdb` or `vdc`: the switch finds them by size.

When all three instances say **Active**, note each one's IPv4 address from **Compute → Instances**.
**Ask xneelo (or look under Billing) what data transfer costs** before the bigger steps: each company is about
2 MB/s each way while its sessions run (about 15 minutes of each step), so a 40-company step is about 150 GB in
and out of the server — and as much again in and out of the load boxes.

## Part 2 — in Cloudflare

`theremotesupportprogram.com` → **DNS** → **Add record**: Type **A**, Name **`loadtest`**, IPv4 address =
the **rsp-loadtest** address, Proxy status **off** (grey cloud). **Save**. Change nothing else.

## Part 3 — on this PC, one command at a time

0. If any of these addresses was used before by another server: `ssh-keygen -R <address>` for it.

1. Save the addresses (it asks for each; a third load box is optional):

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Save-Addresses.ps1

2. Set up the test server — the platform as live has it, then THE SWITCH TO POSTGRESQL (it shows what it found
   and asks you to type **SWITCH**), then the 200 pretend companies of 10 people (about 20 minutes; asks for your
   key's passphrase many times; ends with *The test server is ready, on PostgreSQL*):

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Prepare-LoadServer.ps1

3. Check it is on PostgreSQL — the report must say **PostgreSQL**, rsp-files and rsp-db mounted:

       powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server <rsp-loadtest address>

4. Set up the load boxes (ends with *Load box … is ready* for each):

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Prepare-LoadBox.ps1

5. Run a step: N companies at the profile. A SERVER window and one LOAD BOX window per box open — type the
   passphrase in each, then leave them about 25 minutes, until every LOAD BOX window says *Results:*:

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Run-LoadStep.ps1 -Companies 5

   Then `-Companies 10`, `20`, `40`, `80` — stop at the first VERDICT that says *bent*, and tell Claude: the step
   between the last good one and that one is run next. Paste the bottom of each window to Claude.
   A VERDICT saying *THIS LOAD SERVER WAS THE LIMIT* does not count against the platform: add `rsp-load-3`
   (Part 1 again, then steps 1 and 4) or give the boxes a bigger flavor.

6. The restart storm, once, at the biggest step that carried comfortably:

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Run-LoadStep.ps1 -Companies 20 -Storm

   When the LOAD BOX windows say **READY FOR THE RESTART**, run:

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Restart-LoadServer.ps1

   and leave them until they say *Results:*.

7. Only if Claude asks — the two "would this help?" switches, on the load server only, then repeat the step that
   bent: `-MoreThreads` (more threads ready at start) and `-WidePorts` (Caddy may open 64,000 connections to the
   platform instead of 28,000):

       powershell -ExecutionPolicy Bypass -File deploy\loadtest\Prepare-LoadServer.ps1 -SkipInstall -WidePorts

## Part 4 — afterwards

- In the xneelo panel, **delete** the three instances AND the two volumes AND any boot volumes left under
  **Volumes** (all billed until deleted).
- In Cloudflare, **delete** the `loadtest` record.
- Tell Claude "the load test is done": the results are in `deploy\loadtest\out\results\`.
