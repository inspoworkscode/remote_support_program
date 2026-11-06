# Test Checklist

The internal-testing program on the office shared drive. Anyone opens `TestChecklist.exe` there,
marks checks from `docs/TEST-3.6.md` as Works / Partly works / Fault / Can't test yet / Skip, and
logs faults and upgrade ideas (with screenshots). At the end of the day **Export report…** writes
one markdown file, which Clint hands to Claude.

## The browser version — for Smart App Control

`web/TestChecklist.html`, published beside the exe, does everything the exe does, in **Chrome**
(or Edge). It exists because Smart App Control refuses the unsigned exe outright, with no "run
anyway". It reads and writes the **same files in the same shapes**, so exe and browser users see
each other's results, and a report from either is the same report.

A page opened from a file cannot read its own folder; the person picks the Test Checklist folder
once (the File System Access API) and the page remembers it — after that, one click on Continue.
**Firefox cannot do this at all** (no page may save into a folder there); the page says to use
Chrome. Each browser writes `data\<name> on browser-<4 hex>.jsonl`.

Keep it in step with `Checks.cs`, `Board.cs` and `Exporter.cs`. The test runs it in headless Edge
against the real checklist and against the exe, and must stay at 17 of 17:

```bash
powershell -ExecutionPolicy Bypass -File .\tools\testchecklist\web\Test-WebChecklist.ps1
```

It checks that the page reads checks.md line-for-line as the exe does, that the exe reads what the
page writes (and the page the exe's seven-decimal times), saving, a fault with a picture, a status
change, and the report; then screenshots. It runs on a local web server, because Edge refuses its
scratch storage to a page opened from a file — **the one step it cannot reach is the real folder
picker**, which only a person can click.

## Putting it on the share

```bash
powershell -ExecutionPolicy Bypass -File .\tools\testchecklist\Publish-TestChecklist.ps1 -To "\\SERVER\Share\Testing"
```

The folder is remembered in `share-path.txt` (not tracked). After changing `docs/TEST-3.6.md`,
`-ChecksOnly` copies just the checklist; open copies pick it up within 15 seconds.

## On the share

| Path | What |
|---|---|
| `TestChecklist.exe` | self-contained single file, no .NET needed |
| `checks.md` | a copy of `docs/TEST-3.6.md` — read, never written |
| `data\<name> on <PC>.jsonl` | one append-only log per person per PC |
| `data\pictures\` | screenshots attached to faults and upgrades |
| `exports\` | the end-of-day reports, with a `… pictures` folder beside each |

**Nobody writes anyone else's file**, so several people saving at once cannot collide and nothing
is locked. The state is every log replayed in time order. A half-written line is skipped and read
whole on the next refresh.

## How the checklist is read

`## A. Title` starts a section; `12. **Short.** More…` starts a check, continued by indented lines.
`**DONE by hand 2026-09-21.**` at the start of a check shows it as Works from the document.
A section intro saying `REPLACES checks 66, 67 and 79` shows those as Skip. Keep those shapes when
editing TEST-3.6.md. Check numbers are the key — **never renumber a check**; add new ones at the end.

## Claude answering back

To mark a fault fixed (or a check needing a retest) without anyone typing it, append lines to
`data\Claude.jsonl` on the share — the same shape as anyone's:

```json
{"type":"update","id":"<new guid>","at":"2026-09-30T08:00:00Z","by":"Claude","machine":"-","target":"<fault id>","status":"Fixed — please retest","comment":"Fixed in the build of 30 Sep."}
{"type":"result","id":"<new guid>","at":"2026-09-30T08:00:00Z","by":"Claude","machine":"-","check":"42","status":"untested","note":"Rebuilt — please test again."}
```

Fault statuses: `Open`, `Fixed — please retest`, `Closed`, `Not a fault`. Upgrade statuses: `Open`,
`Planned`, `Done`, `Won't do`. Results: `untested`, `works`, `partly`, `fault`, `blocked`, `skip`.

## Checking the program itself

```bash
dotnet run -c Release --project tools/testchecklist -- --root <folder with checks.md> --me Test --snapshot <out-dir>
```

Writes `parsed.txt` (how the checklist was read), a screenshot of every screen as drawn on this
monitor, a report for today and one for everything, and `selftest.txt` (a save, read back).
