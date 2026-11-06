# Health check toolkit, absorbed into the platform

Imported 2026-09-11 from `C:\PCHealthCheck` at its v2.7, unmodified, as the baseline for this
work. See `docs/PLAN-HEALTHCHECK.md` for what is being built and why.

**Brought up to the toolkit's v2.9 on 2026-09-28** (HANDOVER §51). To do it again: scripts not
changed here since the last sync are copied; the rest are three-way merged with
`git merge-file`, the base being the toolkit at the commit last synced (`36d759a` for v2.9,
**`fe58b93` for v2.11 on 2026-09-29, HANDOVER §53** - `git -C C:\PCHealthCheck log fe58b93..` shows
what has moved since). Then read the merged installer's order
of events: our "keep the settings already here" step does not exist in the toolkit, and a check
that the toolkit places before it has to come after it here.

**`C:\PCHealthCheck` is a separate, finished project and is deliberately left alone.** Its scope
rules — no multi-tenancy, no web UI, no database — were right for it and are reversed here on
purpose. Do not tidy that repo to match this one, and do not treat changes here as belonging
there.

Read `C:\PCHealthCheck\HANDOVER.md`'s "Traps" section before editing any of this PowerShell.
Every one of them cost real time there and will recur:

- a decision made before its evidence has arrived - four bugs in one week there (v2.9); when a
  check depends on data, find where the data is loaded and confirm the check comes after it
- `@($null).Count` is 1, so count a field that may be missing with `@($x | Where-Object { $_ }).Count`
- single-item pipelines return a scalar, so `@($x | Sort-Object)[0]`
- empty values change type, so `[string]$text = (Get-Content $f -Raw)` before `.Trim()`
- variable names are case-insensitive, so `$allMonths` and `$AllMonths` are one variable
- switches cannot cross a `powershell.exe -File` boundary; use a `[bool]` with a default
- `Set-Content -Encoding UTF8` writes a BOM; use `[System.IO.File]::WriteAllText`
- headless Edge exits 0 having produced nothing when it cannot write where it was told

Keep every `.ps1` plain ASCII. Windows PowerShell 5.1 reads a `.ps1` as ANSI unless it has a BOM,
so an em dash becomes three bytes and the parser reports an unterminated string two lines later.

## What is different here

Nothing yet beyond branding — see the git history for this folder. The intent is that the report
carries the **tenant's** identity, not Anywhere IT's hardcoded one, because this platform serves
many IT companies.
