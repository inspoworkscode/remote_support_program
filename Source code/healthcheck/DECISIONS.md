# Decisions log

Short entries. Why, not what. Future-you will not remember the reasoning.

## 2026-07 — Severity is decided in PowerShell, not by the AI
The model writes tone and narrative only. If the model decided what counts as
Critical, the same machine could produce different severities month to month,
and a client would eventually notice. Deterministic severity also makes the AI
call cheap and short.

## 2026-07 — Antivirus is judged across all registered products, not Defender alone
First real test run produced a false Critical: ESET was running correctly and
Defender had stepped aside as designed, but the script reported the machine as
unprotected. A false alarm on a healthy machine is worse than a missed finding,
because it destroys trust in every other line of the report.

## 2026-07 — Windows 11 eligibility is checked, not just Windows 10 end-of-support
"Windows 10 is out of support" is a dead end for the client. "Eligible for a free
in-place upgrade" versus "cannot be upgraded, here is what is missing" are two
different quotes. This is the most commercially valuable check in the tool.

## 2026-07 — Cloud sync is reported as not being a backup
OneDrive present with no real backup is a Critical, not a pass. Deletion,
ransomware and corruption all sync to the cloud copy. Saying this plainly sells
backup services better than any pitch.

## 2026-07 — No multi-tenancy, no web UI, no ticketing in v1
The previous attempt at this product was a full platform and was abandoned
unfinished. Scope is held small on purpose until there is revenue.

## 2026-07 — Blank-password check downgraded from Critical to Warning
`PasswordRequired` returns False for accounts using a Microsoft account or a
Windows Hello PIN, which is normal and secure. The check fired on every single
test machine, which is the tell. Rewritten as something to verify rather than a
confirmed fault. Same lesson as the ESET false positive: if a Critical fires on
every machine, it is the check that is wrong, not the estate.

## 2026-07 — JSON is written without a byte order mark
PowerShell 5.1's `Out-File -Encoding UTF8` prepends a BOM, which standard JSON
parsers reject. Written with `System.IO.File::WriteAllText` and a
`UTF8Encoding($false)` instead.

## 2026-07 — Removable drives excluded from physical disk reporting
The technician's own USB stick was appearing in client reports as an unexplained
7.5 GB drive.

## 2026-07 — Findings are weighted by whether the machine is a laptop
Unencrypted storage is a footnote on a desktop that never leaves the office and a
real risk on a laptop. Same data, different severity.

## 2026-07 — Report leads with the machine roster, not the fault list
A client with five PCs wants to know which one is the problem before they want
to know what the problem is. The roster also makes the retainer visible: they
can see their whole estate was looked at, not just the machine that broke.

## 2026-07 — "Checked and clear" section is not filler
A report containing only faults reads like a sales pitch. Listing what was
checked and found healthy is what turns it into evidence that the monthly fee
bought something.

## 2026-07 — No AI in version one of the report
The finding titles and recommendations are already written in client language
inside the collector, which costs nothing to run. The API is billed separately
from the Claude subscription. An AI-written opening paragraph can be added once
the product has revenue.

## 2026-07 — Maintenance is a separate script from the health check
The health check is read-only and the client report says so. Cleaning modifies
the machine. Mixing them would make that promise untrue, and would mean a bug in
the cleaner could damage a machine during what the client was told was an
inspection. Two scripts, two jobs, two log files.

## 2026-07 — Disk health percentage is calculated natively, not with CrystalDiskInfo
Windows exposes the raw SMART attribute table through WMI and simply never
interprets it. Each 12-byte attribute carries a normalised value that starts at
100 and falls towards the failure threshold — that number IS the health
percentage. No third-party tool to license, bundle, or get flagged by antivirus.

## 2026-07 — No attempt to replace Malwarebytes
Writing a malware scanner is years of work and a permanently losing race. The
maintenance script triggers whichever scanner is already installed and reports
the outcome. Malwarebytes stays what it is: a diagnostic tool for when infection
is suspected, not a monthly task.

## 2026-07 — Cleaning only touches files older than 7 days
Anything newer may still be in use. Documents, Downloads, Desktop, Pictures,
browser passwords and saved logins are never touched at all. The recycle bin is
only emptied of items older than 30 days, so a user who deleted something by
mistake still has time to notice.

## 2026-07 — Solid state drives get TRIM, not defragment
Defragmenting an SSD causes wear and delivers nothing. The script detects what
each drive physically is and does the right operation. Mechanical drives are
measured first and only defragmented once fragmentation passes 10%, because an
hour of defragmenting to recover nothing is worse than doing nothing.

## 2026-07 — sfc results become client-facing findings
"Windows repaired 3 damaged system files" is exactly the invisible work a
retainer pays for and the client would otherwise never know happened. Repeated
corruption month after month is also an early warning of a failing drive or bad
memory, which is worth surfacing rather than silently fixing.

## 2026-07 — The recycle bin is never emptied
Deleted files still belong to the user, and someone who deleted something by
mistake may not have noticed yet. It is also the one cleanup action a client
would actually see happen, which makes it the most likely to generate a phone
call. The space it recovers is not worth that. Available with -EmptyRecycleBin
if a specific machine genuinely needs it.

## 2026-07 — Maintenance will not run without a system restore point
This is the only script that changes a client machine. If Windows cannot offer
a way back, nothing is touched. System Restore is switched on automatically if
it is off, the restore point is verified rather than assumed, and a failure
skips the whole maintenance run and says so in the client's report. The health
check still runs, because it changes nothing.

Windows normally refuses more than one restore point per 24 hours. That limit
is lifted for the moment it takes to create ours, then put straight back —
otherwise a second run on the same day would silently proceed with no safety net.

Windows Server does not support System Restore at all, so maintenance skips on
servers by design. -NoRestorePoint overrides that where it is genuinely
appropriate, and the report records that it was used.

## 2026-07 — The monthly run day is chosen from the weekday of installation
Monday installs report on the 1st, Tuesday the 2nd, through to Friday the 5th.
Technicians already spread their site visits across the week, so this spreads
the monthly emails across five days without anyone having to decide anything.

The problem it avoids: fifty-odd machines all mailing within the same hour on
the 1st will hit the hourly send limit on shared hosting, and the failures land
on whichever machines happen to be last. Nothing in the tool would look broken —
the reports would simply stop arriving for some clients.

Set DayOfMonth to a number in email-settings.txt to force every machine onto the
same day instead.

## 2026-07 — The report recipient is chosen at install, not left in a file
Two products from one install: on-contract clients send to us, once-off clients
receive their own report and contact us when they want something done.

Asked as a question rather than read from email-settings.txt, because a stale
address left in a text file would silently send one client's report to another
and nobody would notice. The address is validated and read back for confirmation
before anything is installed.

A client-bound email is written for a non-technical reader and carries only the
PDF. Internal emails keep the technical summary and the data files, which the
combined client report is built from.

## 2026-08 — Reports cover exactly one month
The report generator originally read every JSON file in the folder. On a machine
running monthly that would silently blend months together by March — February's
disk space beside August's, and every month's maintenance totals added up. The
numbers would still have looked plausible, which is what makes it dangerous.

Now it reports on the newest month present unless told otherwise, says how many
files it ignored, and takes -Month yyyy-MM to rebuild an older one. -AllMonths
still exists for looking back over time deliberately.

If a machine is checked twice in one month, only the latest run counts. Running
a manual check mid-month should not double a machine up in the report.

The monthly run also deletes anything older than 12 months from the client
machine, so a folder does not grow forever.

## 2026-08 — Every path handed to an external program must be absolute
The PDF step failed silently for a whole round of testing. Edge was fine — the
diagnostic proved it worked in all three headless modes. The report script was
handing it a file:/// URL built from a relative path (.\reports\x.html), which
Edge cannot resolve. It exits cleanly, writes nothing to its error stream, and
produces no PDF. Indistinguishable from a broken Edge.

Anything crossing out of PowerShell into another program gets Resolve-Path with
.ProviderPath first.

## 2026-08 — Edge writes PDFs to a temp folder, never straight to the target
Headless Edge drops privileges into a sandbox. It cannot write into
C:\ProgramData\HealthCheck, which the installer deliberately restricts to SYSTEM
and Administrators. It exits with code 0 having produced nothing — no error, no
file, indistinguishable from success.

Cost several rounds of debugging. Edge now always works inside a fresh temp
folder and the finished PDF is moved into place afterwards. The source HTML is
staged there too, since Edge has to read it as well.

Also worth remembering: a small test page proved nothing, because the failure
depended on the folder, not the file.

## 2026-08 — The trigger start boundary is the next real occurrence
Setting StartBoundary to the install date means Windows sees a start time in the
past and reports no next run time at all. The task sits there looking correct
in every other respect and never fires.

It now calculates the next actual occurrence of the chosen day, rolling to next
month if today's has passed, and then CHECKS that Windows reports a next run
time before declaring success. The install prints that date so the technician
sees when it will first run.

## 2026-08 — Switch parameters go before quoted values on the command line
"Anywhere I.T" ends in a dot. Passed as -Organisation "Anywhere I.T" with -Pdf
following it, PowerShell's parameter binder hands -Pdf an array and the script
dies with a type error. Putting switches first avoids it entirely.

Worth remembering: the organisation name is user-typed and will contain dots,
ampersands, apostrophes and anything else a business puts in its name.

## 2026-08 — Scripts do not print their result object by default
The collector was dumping its entire result to screen, burying the summary a
technician needs to read under two pages of raw data. -PassThru brings it back
for scripting.

## 2026-08 — Switches passed through powershell.exe -File must use -Name:$true
This one cost two rounds. powershell.exe -File binds everything after the script
path POSITIONALLY. A bare switch such as -Pdf does not act as a flag; it
consumes the next argument, and the script dies with "Cannot convert value
System.Object[] to type SwitchParameter".

My first fix - moving switches to the front - did nothing, because position was
never the issue. The form -Pdf:$true is the only reliable way.

Note the earlier decision blaming the trailing dot in "Anywhere I.T" was wrong.
The name was never the problem.

## 2026-08 — Pdf is a [bool] with a default, not a [switch]
Three rounds were lost to one problem: a switch parameter cannot survive the
trip from a batch file into PowerShell.

  -File    binds everything positionally, so a bare -Pdf swallows the next
           argument
  -Pdf:$true  cmd does not expand $true, so it arrives as a literal string
  -Command parses properly, but then a [bool] cannot be passed as a bare flag

Settled shape: Pdf is a [bool] defaulting to $true and is never passed by any
caller. Show stays a [switch] and is only used from -Command, where switches
work normally.

The wider lesson: anything crossing the cmd/PowerShell boundary should be a
value with a default, not a flag.

## 2026-08 — No switch parameter ever crosses the cmd/PowerShell boundary
After several failed attempts at passing switches from the batch file, they were
removed entirely. New-ClientReport.ps1 has no [switch] parameters left; Pdf and
Show are [bool] with defaults and are never passed.

Opening the finished report is now done by the batch file. The script writes the
path it produced to last-report.txt and the batch opens that. Simpler, and this
whole class of failure cannot recur.

## 2026-08 — The switch saga was actually a variable name collision
Six versions were spent on what looked like a parameter-binding problem. The
real cause: a local variable $allMonths and the parameter $AllMonths are the
SAME variable, because PowerShell names are case-insensitive. Assigning an array
of month strings to a [switch] gave "Cannot convert System.Object[] to
SwitchParameter" - which reads exactly like a bad switch being passed in.

Changing the parameter type to [bool] changed the error to "Cannot convert
2026-08 to Int32", which was the clue that finally gave it away.

It only ever fired when -Month was NOT supplied, which is why the scheduled run
always worked and only the interactive options failed.

Two lessons. When an error moves but does not go away, the diagnosis is wrong.
And a parameter name should never be reused as a working variable, whatever the
casing.

## 2026-08 — Always wrap a filtered or sorted result in @() before indexing
Third time this exact trap has bitten. PowerShell returns a scalar, not a
one-element array, when a pipeline produces a single result. Indexing [0] on a
string then returns its first CHARACTER:

  "Clint"   -> "C"        (Affects C, in the findings)
  "2026-08" -> "2"        (Reporting on 2 only)

Any pipeline result that gets indexed must be wrapped: @($things | Sort-Object)[0].
Safe without it only where the source is always a collection, such as -split
output or a Group-Object .Group.

## 2026-08 — Never write a file with Set-Content/Out-File -Encoding UTF8
PowerShell 5.1 prepends a byte order mark. Anything that reads the file back
gets those bytes as part of the content:

  last-report.txt      -> "Windows cannot find '<junk>E:\Reports\...'"
  backup-location.txt  -> the path never matches, so the unplugged-drive
                          check would silently never fire
  the JSON output      -> standard parsers reject the file outright

Use [System.IO.File]::WriteAllText with UTF8Encoding($false), or ASCII where the
content is a plain path. Third time this has caused a bug in this project.

## 2026-08 — powershell.exe -File cannot carry a switch, even from PowerShell
The same trap that bit the batch file also applies when a PowerShell script
calls powershell.exe -File. The $true in -Quick:$true is NOT expanded; it
arrives as the literal string "$true".

Any parameter that has to cross a process boundary is a [bool] with a default,
and the caller uses -Command rather than -File when it must pass a value.
Switches remain fine for in-process calls and splatting.

## 2026-09 — Recommendation text is addressed to the client, not the technician
Fifteen of the fifty-one Recommendation strings were written in vendor voice and
were being shown to the very person they described: "Worth quoting a
replacement", "a managed backup service should be quoted", "Worth quoting a
Microsoft 365 migration". Two more referred to the reader in the third person as
"the user" and "the client".

This was never hidden internal text. New-ClientReport.ps1 renders Recommendation
straight into the client PDF under the heading "What we suggest", and there is
no separate internal report path, so retainer clients were already reading it.

A report that tells the reader what to quote them teaches them that the report
is a price list, which kills the phone call it exists to generate. The
commercial intent is unchanged - every rewritten string still points at exactly
the same piece of billable work. It is simply addressed to the person holding
the page now.

CLAUDE.md was amended at the same time, because it still described the field as
something "a technician can act on and invoice". Left alone, that line would
have got this work reverted as a tidy-up.

Worth remembering: the collector is pure ASCII and has no BOM, so PowerShell 5.1
reads it as ANSI. An em dash or a curly quote pasted into one of these strings
would corrupt the file. Keep replacements ASCII.

## 2026-09 — sfc runs in the health check as /verifyonly, and repairs nothing
The health check is read-only and the client report says so. sfc /verifyonly
compares every protected Windows file against Microsoft's originals and reports
what is damaged without touching anything, so the promise still holds. Repair
stays where it was, in Invoke-ClientMaintenance.ps1, which runs /scannow.

That split is the commercial point, not an accident. The free check finds the
damage; the paid maintenance fixes it. "Some of Windows own files are damaged,
and nothing was changed" is a line that earns a phone call, where the same
finding in a retainer report would only raise the question of why it was left.

It is on by default despite costing roughly ten minutes on a healthy machine and
considerably longer on a sick one. That was a deliberate trade: the finding is
worth more than the speed. The old "three minutes" claim came out of
PC Health Check.bat at the same time, because it was no longer true, and a tool
that claims three minutes and takes fifteen looks broken while it works.

It runs LAST in the collector, so everything quick is already gathered before
the slow check starts. A timeout there costs one finding, not the whole report.

## 2026-09 — Drive optimisation date comes from two sources, and 1999 means never
Neither source is dependable alone. The Windows event log is the more accurate
of the two because it records manual optimisations as well as scheduled ones,
but on the test machine it was completely empty. The scheduled task always
exists, but when it has never run it reports a LastRunTime of 30 November 1999
with result code 267011.

That date is a placeholder, not a date. Printing "last optimised: 30 November
1999" in a client report is the same failure as the Defender 4294967295
signature age - an unexplained number that destroys confidence in everything
around it. The check tests the result code and rejects any year before 2000,
and reports "never" instead.

Found by writing a diagnostic that asked all four possible sources what they
returned, before any of the real check was written. Guessing would have shipped
the 1999 date.

Also worth remembering: defrag.exe /A returns nothing at all without
administrator rights. It is recorded as unknown rather than as zero, so an
unelevated run can never claim a drive is unfragmented when it was never
measured.

## 2026-09 — Temp file measurement mirrors the maintenance cutoff exactly
The health check measures what a maintenance run would recover, using the same
folders and the same seven-day age cutoff as Invoke-ClientMaintenance.ps1.

If the two ever drift apart the client is told they will get 4 GB back and then
sees 1.5 GB recovered, which reads as either incompetence or a lie. Changing the
cutoff on one side means changing it on the other.

Reported as Info and only above 500 MB. Temporary files accumulate on every
working machine, so raising them as a warning would fire across the whole estate
and devalue the genuine findings next to them.

## 2026-09 — OK findings are rendered, in a section that comes last
Get-FindingBlock was called three times, for Critical, Warning and Info. There
was no fourth call, and the machine roster stripped OK findings out explicitly.
Every OK finding the collector produced was therefore collected and thrown away.
"Virus protection is active" appears in three of the four sample files and in
none of the reports ever built from them - reports/example-health-report.html
contains zero occurrences of it.

The styling gave it away: $SeverityLabel['OK'] = 'All good' and
$SeverityClass['OK'] = 'sev-ok' were both already defined. Someone wired up the
appearance of the section and never wired up the section.

This had quietly contradicted the July decision that a "checked and clear"
section is not filler. That decision was made, recorded, and never built.

It now renders as "Checked and found healthy", placed LAST, after "For your
information". Problems first and reassurance afterwards, so the report never
buries what needs attention behind a list of things that are fine. It matters
more in the free report than the retainer one: a stranger reading a page of
nothing but problems concludes the report is a sales pitch, and the clean
results are what make the problems believable.

## 2026-09 — Anything measured gets a row, including checks that could not run
Temporary files were being measured correctly and were completely invisible.
The figure went into the JSON, but New-MachineDetail had no row for it and the
finding only fires above 500 MB, so on any reasonably tidy machine the client
was told nothing at all. Drive housekeeping and the system file check had the
same problem.

If a check is worth running it is worth showing, whether or not it crossed the
threshold that raises a finding. The measurement is the product.

The same row now reports a check that could NOT run - "Windows system files:
not checked on this run, needs administrator rights". This is the counterpart to
the silent-failure trap: BitLocker, defrag /A and sfc all fail closed without
elevation, producing no finding rather than a wrong one, which made an
unelevated report look CLEANER than the machine really was. Saying so on the
page is the difference between an incomplete report and a misleading one.

## 2026-09 — sfc will not run while a restart is pending, so it is skipped
sfc /verifyonly returned in under a minute with "There is a system repair
pending which requires reboot to complete. Restart Windows and run sfc again."
It refuses to run at all in that state.

The health check already detects a pending restart, and that check runs before
this one, so it now cross-references instead of finding out the hard way. Ten
minutes of waiting for a result that cannot arrive is avoided.

Deliberately NO second warning. The pending restart is already reported by the
Updates check, and two warnings about one root cause is the crying wolf this
report exists to avoid. The machine detail row carries it instead: "could not be
checked because the machine is waiting for a restart".

Expect this to be the normal case, not the exception. On the neglected machines
this tool is aimed at, a PC sitting on a pending reboot for weeks is ordinary.

The phrase itself is also matched directly as a fallback, because the registry
check does not detect every kind of pending servicing operation and sfc is the
real authority on whether it can run.

Worth recording how this was found. The hypothesis was that redirecting sfc's
output broke it - its own error message mentions needing "a console session",
which made that look obvious. The diagnostic ran sfc both ways and the two
outputs were identical, so the hypothesis was simply wrong, and a refactor of
working code was avoided. It also cleared Invoke-ClientMaintenance.ps1, which
had been suspected of never really repairing anything.

## 2026-09 — A check whose result cannot be read is never reported as a pass
The first version of the sfc check had a catch-all branch that recorded any
unrecognised output as "Completed", intended for non-English Windows. On the
first real elevated run that branch caught the pending-reboot message, and the
report would have printed "Windows system files: Checked against Microsoft
originals - Completed".

That is a false clean bill of health, and it is worse than the false Critical
this project already guards against. A wrong alarm annoys; a wrong all-clear
means a real fault goes unlooked-at while the client believes it was checked.

The catch-all is now "Unrecognised", it keeps up to 600 characters of the raw
output in the JSON so the cause can be read rather than guessed at, and any
non-clean result finishing in under two minutes is flagged as suspiciously
fast - a verification that quick did not happen, whatever it printed. The report
says "not confirmed either way" and never presents it as healthy.

The general rule: not knowing is a third outcome. It must never be collapsed
into either pass or fail.

## 2026-09 — Cast to [string] before calling a string method
Same family as the @() trap, and it bit inside the sfc diagnostic itself:

  $err = (Get-Content $file -Raw) -replace "`0", ''
  if ($err.Trim()) { ... }   ->  "[System.Object[]] does not contain a
                                  method named 'Trim'"

Get-Content on an empty file returns nothing, and -replace against nothing
yields an empty ARRAY rather than an empty string. The failure only appears when
the file happens to be empty, which is exactly when nobody is testing.

Where @() protects an indexing operation, [string] protects a method call.

## 2026-09 — sfc runs once per monthly run, not twice
Adding sfc /verifyonly to the health check quietly broke the retainer path. The
monthly run does maintenance first, which runs sfc /scannow - verify AND repair -
and then ran the health check, which now verified all over again. With a 45
minute timeout on one and 30 on the other, a single unattended run could sit in
sfc for over an hour doing the same job twice.

Invoke-MonthlyRun now decides: the health check verifies only when maintenance
did not do its own, which means when maintenance was skipped entirely or ran in
Quick mode. On a Windows Server, where maintenance always skips because there is
no System Restore, this is the only system file check that happens at all - so
it must not simply be switched off.

Known gap, accepted: if maintenance starts but aborts because no restore point
could be created, no system file check runs that month. The report says plainly
that maintenance was skipped and why, so nobody is told a check happened that
did not.

The wider lesson: a default that is right for the standalone tool was wrong for
the retainer path. Anything added to the collector needs checking against BOTH
callers, because the free checker and the monthly run share it.

## 2026-09 — Maintenance must never report work it did not do
The same false-pass bug found in the collector existed independently in
Invoke-ClientMaintenance.ps1, and mattered more there. Its catch-all branch
recorded any unrecognised sfc output as:

  "Checked every protected Windows file against Microsoft originals - Completed"

On a machine waiting for a restart, sfc returns "There is a system repair
pending" within seconds. The client was then told, under the heading "What we did
this month", that ten minutes of work had been done which had not been done at
all.

That heading is the invoice justification. A wrong finding elsewhere in the
report is an error; a wrong line there is a claim about what was charged for. It
is the one place in the product where being wrong is not just embarrassing.

Now: the pending-restart flags are checked BEFORE the scan starts, so the run is
not held up; the client is told it will be done after the restart; and
unrecognised output is recorded as "did not report a result we could read" with
result Failed, never as work performed.

Only the servicing and update flags are tested. PendingFileRenameOperations is
true on virtually every machine and testing it would skip the scan permanently.
See the entry on that above.

Worth noting the shape of this: one bug, written twice, in two files, because the
same reasoning error was made in both. Finding it in one place is a reason to
search for it in the other.

## 2026-09 — A measurement that reports zero hides, where a wrong number shows
The Delivery Optimisation cleanup had reported 0 MB freed on every run it had
ever done:

  $size = 0
  $doCache = Get-DeliveryOptimizationPerfSnap    # fetched, never used
  Delete-DeliveryOptimizationCache -Force
  Add-Task ... -FreedMB $size                    # always zero

Someone intended to read the size out of that snapshot and stopped halfway. On
the first machine where it was ever measured the cache held 2.5 GB, so the
maintenance told the client it had recovered 0.13 GB when 2.61 GB had actually
gone - understating the work by twentyfold.

That is the worst direction to be wrong in. Space recovered is the headline
number the paid maintenance is sold on. Overstating it would be dishonest;
understating it makes the product look worthless while working perfectly.

The size has to be read BEFORE the cache is deleted, or it reads zero for a
different and equally invisible reason.

Renamed to $doSizeMB. $size was already used for two other things in the same
file, and a name collision in this project has cost six versions before now.

The general lesson is the one worth keeping. Nothing looked broken. A wrong
non-zero figure gets questioned; a zero reads as "there was nothing to do", which
is a perfectly ordinary thing for a cleanup task to report. It only surfaced
because two independently derived numbers - the task total and the free space
before and after - were compared and disagreed.

So the record now carries UnattributedMB: the free space delta minus what the
tasks claim. Technician-facing only, never shown to a client, and deliberately
noisy - the restore point consumes space and other processes write during a run -
so it is a smell rather than a measurement. Consistently large means another
task is silently reporting nothing. The gap cannot hide in plain sight again.

## 2026-09 — A month with nothing to clean must not read as a month of no work
The maintenance section opened with "We cleared 0 GB of unnecessary files across
1 machine this month." That sentence sits at the top of the part of the report
that justifies the invoice, and it appears precisely when the retainer has been
working: a machine kept clean has nothing left to clear.

So the better the maintenance worked over time, the worse the report read. The
product argued against itself, in the one paragraph where it should not.

It now says the machine had nothing left to clear, that this is what regular
maintenance is for, and that everything below was still checked and tuned - with
the full task list underneath, so the restore point, the caches, the drive
optimisation and the system file check are all still visible as work done.

Same reasoning as the "checked and clear" section decided in July: an absence of
problems is a result and has to be reported as one. A report that can only show
value when something was wrong will eventually stop showing value at all.

Units were fixed at the same time. TotalFreedMB was divided by 1024 and rounded
to two decimals, so a genuine 130 MB clean-up printed as "0.13 GB" - a number the
reader has to decode, and one that looks like nothing. Under a gigabyte it now
says megabytes.

## 2026-09 — One variable cannot escape for both -File and -Command
The organisation and role names were apostrophe-doubled once, at the point they
were typed, and then used for every call. But the two ways of calling PowerShell
need opposite treatment:

  -File     the value sits inside DOUBLE quotes. An apostrophe is harmless and
            doubling it is wrong.
  -Command  the value sits inside SINGLE quotes. O'Brien Plumbing ends the
            string early unless the apostrophe is doubled.

Four of the six call sites used -File, so most of the time the escaped form was
sent. A client called O'Brien Plumbing came out as O''Brien Plumbing on their own
report, in the filename, and in the folder their results collected into.

Nothing errored. That is the part worth remembering - the failure was a wrong
name printed confidently, not a crash, so it would have survived any amount of
testing with clients whose names contain no apostrophe.

Now kept as two variables: %ORG% raw for -File, %ORG_Q% doubled for -Command.
The names differ so that picking the wrong one is visible when reading the line.

Two things this is a reminder of. First, an escaping decision belongs at the call
site, not at the point of input, because only the call site knows which quoting
it lives inside. Second, and more important: this bug had been in START HERE.bat
for months, and was copied into PC Health Check.bat the same day it was found -
by reusing the existing pattern without questioning it. That is the second time
in one day the same fault appeared in two files for the same reason. Finding a
bug in one file is a reason to grep the pattern across the others, not just to
fix the instance in front of you.

## 2026-09 — "Could not run" and "was not asked to run" are different things
Option 2 produced a report that argued with itself:

  Windows system files   Checked every protected Windows file ...  No damage found
  Windows system files   Not checked on this run - not requested.  Not checked

Both lines were true. Maintenance had run sfc /scannow and found nothing wrong;
the health check had then been told not to repeat it, because option 2 and the
monthly run both switch its copy off to avoid ten minutes of duplicated work.

The mistake was in the rule added earlier the same day: that anything measured
gets a row, INCLUDING a check that could not run, so an unchecked machine never
looks like a checked one. That rule is right, but it collapsed two different
situations into one.

  Could not run          no administrator rights, or a restart pending. A
                         genuine inability. The client must be told, or the
                         report overstates the health of the machine.
  Was not asked to run   another part of the toolkit has already done it. An
                         internal implementation detail that means nothing to
                         the reader.

Only 'Not requested' is suppressed. The pending-restart and missing-admin rows
still appear, so the protection is intact.

Worth remembering how this surfaced. Every piece was individually correct: the
maintenance record, the health check record, and both rendering rules. The fault
only existed in the combination, and only became visible to somebody reading the
whole page as a client would. Testing the parts could not have found it.

## 2026-09 — Test the package, not the source tree
On a fresh unzip the toolkit could not find its email settings. The working copy
was assembled in this order:

  line 54   copy email-settings.txt -> WORKDIR     (only if it exists)
  line 71   create email-settings.txt from template

The copy ran fifteen lines before the file it copies was created, so on a first
run it silently did nothing - copy /Y with output redirected to nul reports no
failure. The settings file then appeared on the stick, but never in the working
folder, which is the only place Install-HealthCheck.ps1 looks: $here and its
parent, never back at the stick.

Every run after the first worked, because by then the file existed. That is why
it survived months of use. It could only fail for somebody unzipping a clean
copy - which is every deployment, and every IT company this might one day be
sold to.

The settings copy now happens after the template has been turned into a real
file, and the old position carries a comment saying why it is not done there.

The real lesson is about testing, not ordering. Every fix that day was verified
against a working tree where state had accumulated: a settings file that already
existed, a machine already restarted, a cache already cleared. The zip contents
were checked carefully and the zip was never once extracted somewhere clean and
run. For a deployment package that is the only test that counts.

Anything shipped as a package gets tested by unzipping it into an empty folder
and running it from there, as a stranger would, with no state left over from
building it.

## 2026-09 — A default is fine until the code ships to somebody else's customers

Every branding parameter in this toolkit defaulted to Anywhere IT: the company
name, the email address, the phone number, the tagline, and the logo in
`templates\`. That was correct for years, because the toolkit only ever ran on
Anywhere IT's own machines, and a default that matches the only user is not a
default at all - it is a convenience.

It stopped being correct the day `build.ps1` started staging these scripts into
the client payload, because that payload is what every tenant of the platform
downloads. Nothing about the defaults changed; the audience did.

The name was the obvious half and the smaller one. `Invoke-MonthlyRun.ps1` only
passed `CompanyName` through when it was non-empty, so a tenant with a blank
name in `brand.json` fell through to the report builder's default and got
"Anywhere I.T." printed on their customer's report.

The logo was worse, and needed nobody to do anything wrong. A tenant who had
simply not uploaded one left `BrandLogoPath` empty, `Install-HealthCheck.ps1`
fell back to whatever `logo.png` it could find beside itself, `build.ps1` had
helpfully put Anywhere IT's there, and it was copied into
`C:\ProgramData\HealthCheck` and embedded in every monthly report that
reseller's customers received.

Both fallbacks were written deliberately, with comments explaining that they
kept a standalone install working. They did. They also made "branded with the
wrong company" the failure mode, when "not branded at all" was available and is
obviously preferable. An unbranded report is a cosmetic gap. Another IT
company's name and logo on your customer's report is a commercial one.

So: the defaults are blank, blank renders as no element rather than an empty
one, a platform-managed install never falls back to a toolkit logo, and the
toolkit's logo is no longer staged into the client payload at all.

The general rule, which is not about branding: **a fallback should be checked
against what it produces when it fires, not only against what it prevents.** A
fallback that prevents an empty space by inserting the wrong answer is worse
than the empty space, and it will fire silently, on somebody else's machine,
for a customer nobody here will ever meet.

## 2026-09 — Drive health comes from the maker's thresholds, not our percentages
Our health percentage and CrystalDiskInfo disagreed. A diagnostic dumped the full
SMART table from a Crucial CT240BX500SSD1 beside CrystalDiskInfo, and the column
that settled it was THRESHOLD - which the collector had never read.

A normalised SMART value means nothing on its own. On that drive "Reallocated
sectors" fails at 10, "Lifetime remaining" at 1, and everything else at 0. A
value of 9 against a threshold of 0 is a healthy drive; against a threshold of 10
it is a failing one. We compared the bare number to 50 and 80 as though it were a
percentage, which measured neither.

Crossing the manufacturer's own threshold is now its own Critical. The health
figure comes from the drive's life-remaining attribute in a fixed preference
order (202, 231, 233, 169, 173) rather than the lowest value across a basket of
nine - that rule was ours, not CrystalDiskInfo's, and half the basket were damage
counters rather than life meters. Both tools now read 96% from attribute 202,
"Lifetime remaining", named the same way so a client who checks sees the same
words.

NVMe wear is only trusted on NVMe. The SATA drive reported Wear=0 while SMART
showed 4% used, so on SATA a zero means "not reported". Believing it would have
printed 100% for a worn drive.

We will not match CrystalDiskInfo on every drive; its per-vendor tables are years
of work and IDs above about 170 mean different things on different makes. The
aim is to stop contradicting it, which is achievable.

## 2026-09 — Email settings are checked after they are read
The installer's CHANGE-ME guard ran at the top of the script and the settings file
was read fifty lines later. The menu never passes the password on the command
line, so the guard always saw an empty value and passed. The placeholder was then
installed as the real password - and the round-trip check confirmed "Email
password stored and verified (9 characters)". The report would have failed to
send a month later, with nothing to say why.

Test-Email.ps1 checks for CHANGE-ME in an installed config because this happened.
The symptom was caught downstream and the cause was never looked for. This is the
same shape as the email-settings.txt copy that ran before the file existed, and
the backup verdict reached before the file search: a decision made before the
information it depends on has arrived.

-ValidateOnly was added to make this testable without admin, without creating a
scheduled task and without sending anything. It is also worth running before any
real install.

## 2026-09 — A client install never uses the address in the settings file
Choosing "The client" with no address given used to fall back to EmailTo in
email-settings.txt - our own address - and send the client-worded email to us.
Unreachable from the menu, which will not proceed without an address, but found
by the validation matrix and a direct contradiction of the earlier decision that
the recipient is chosen at install and never left in a file. The file's address
is now read for support installs only.

## 2026-09 — The backup verdict is reached once, after all the evidence
"Backup in place" or "no backup" was decided before the file search that finds
actual backup files had run. A machine whose only backup was image files on an
external drive was told "No evidence of a working backup" - Critical - and then
"A recent backup was found and checked". A false Critical on a healthy machine,
doing exactly what we would advise. Old backup files produced "no actual backup"
beside "the last backup was 40 days ago", contradicting each other.

The verdict is now its own check at the end, and the generic line only speaks
when nothing more specific has been said. A stale backup, an unplugged drive or a
stopped agent is always more useful than "no backup", and saying both reads as
the report arguing with itself. Only a recent backup counts as evidence of a
working one.

## 2026-09 — Installation media and virtual machine disks are not backups
The "backup 158.8 days old" in a client email was D:\sources\boot.wim, the boot
image on a Windows installation USB stick. *.wim matched the backup patterns and
every install stick carries two. Same class of fault as the technician's USB
stick appearing in client reports: the technician's own kit leaking into the
client's result. Anything in a "sources" folder is now ignored.

*.vhdx had the opposite fault. Hyper-V and WSL both use it and running VM disks
are written constantly, so a Hyper-V host with no backup would have read "A recent
backup was found and checked" - a false all-clear on the kind of machine where it
matters most. Only .vhdx inside WindowsImageBackup, where Windows' own backup
puts it, now counts.

Both were found by reading the email a client would receive, not by testing the
check. The finding read as perfectly plausible until the file behind it was
named.

## 2026-09 — A missing health figure is never shown as "Healthy"
With no health percentage, the report's drive row said "Healthy" without looking
at anything else - not the maker's thresholds, not the drive's own failure flag,
not its bad-sector counts. Making mechanical drives stop reporting a percentage
(correctly, to match CrystalDiskInfo) sent every one of them down that branch,
failing ones included. The findings would have called a drive Critical and the
table on the same page would have called it Healthy.

The drive's own verdict now comes first. Where the health table was read and
nothing is past its threshold, the row says "No problems reported", which is a
real result. Where nothing could be read at all, it falls back to Windows' own
verdict and says so, rather than presenting a guess as a measurement.

It was found while writing the integration handover. The sentence "an empty
HealthPercent is not a healthy drive" went into the document, and checking
whether the report obeyed it showed that it did not. Writing a rule down for
someone else is a good way to find out whether your own code follows it.

Also worth keeping: @($null).Count is 1. JSON from before v2.9 has no Breached
field, and counting it naively would have marked every older drive as failing.
Count through a filtering pipeline - @($x | Where-Object { $_ }).Count - whenever
a field might not exist.

## 2026-09 — Two different builds both call themselves v2.9
An exception to the version rule, recorded so it is not mistaken for the rule.

The first v2.9 zips were built, then the integration handover exposed the
"Healthy" drive-row bug and it was fixed. The zips were rebuilt WITHOUT moving
to 2.10, so two different builds carry the same version number - exactly what
the version rule exists to prevent.

Why it was allowed: the first build was under an hour old and had not left
dist\ as far as anyone knew. Bumping for a build nobody had used would have been
noise, and a same-session 2.10 would have confused more than it clarified.

Why it is still a risk: if the first build was copied onto a stick, that stick
shows "Toolkit: v2.9", looks current, and carries a bug that can call a failing
drive Healthy. The version check cannot catch it.

Telling them apart:

  The fixed build's New-ClientReport.ps1 contains "breachedCount".
  The first build's does not.

  Fixed build, from commit adee1f9:
    AnywhereIT-Toolkit-v2.9.zip   SHA256 begins 4035640A58EB501A
    PC-Health-Check-v2.9.zip      SHA256 begins 4D4E1B607DDE28F1

The rule going forward, which makes this the only exception: a rebuild under the
same number is allowed only while the zip has never left dist\. The moment one
has been copied anywhere - a stick, a share, an email - any change at all is a
new version number.

## 2026-09 — When a technician does something odd, check what the screen told him
A technician ran option 3, made a Collected folder by hand, put a finished PDF in
it, and ran option 4 - which said the folder was empty. It looked like a user
mistake. It was the menu's.

Option 3 ended with the installer saying results collect in
C:\ProgramData\HealthCheck, followed immediately by the menu saying "Results saved
to: <stick>\Collected". The second was false - option 3 saves nothing there - and
it was the last line on the screen. He went where he was told, found nothing,
found the report on the machine, and put it where the screen said results belong.

The same closing message also told him to run Uninstall-HealthCheck.bat, which
has never existed, and to "pull those files down through your remote support
tool" without saying where to put them.

Each option now reports where its own results went. Option 3 points at option
4, which finds the machine's results itself. And Collected carries a README
saying it holds .json data files, not reports - because the name alone invited a
PDF, and a PDF cannot be built into anything.

The lesson is broader than the bug. When someone using the tool does something
that makes no sense, the first question is what the tool told them to do. Every
message a technician reads is part of the product, and a wrong one is followed
faithfully.

## 2026-09 — A path with brackets, and a line ending in a digit
Two cmd traps, both met while writing the README above, both silent.

A ")" inside a ( ) block can close the block early. Extract a zip twice and
Windows names the second folder "Toolkit (1)"; this technician named his
"(Maintenance)". Any path from %~dp0 may contain brackets. Keep path-using code
out of ( ) blocks - use goto - or keep every path inside double quotes.

"echo Option 4>>file" does not write "Option 4". cmd reads "4>>" as "redirect
handle 4", so the text goes to the screen and the file silently loses the line.
No echo that is redirected to a file may end in a digit.

Both were caught by running the real lines from a folder with brackets in its
name, not by reading them.

## 2026-09 — The client report shows one machine at a time
Partly reverses how the July "roster first" decision was carried out. The roster
still comes first - a client with five PCs wants to know which is the problem
before what the problem is - but everything after it is now one section per
machine, not one merged list.

The merged layout put one entry per problem, marked "Affects Reception, Office",
and one maintenance list for all machines combined. It kept the report short,
but it hid exactly what a retainer client is paying to see: what was done to
EACH machine. Asked directly - "does it list all three machines independently,
showing what it has done for each?" - the honest answer was no.

Now page one is the summary and the roster, and every machine then starts its
own printed page with its own problems, what was done to it, what was found
healthy, and its measured details. A single-machine report has no page break,
so it has no half-empty first page. The same problem on three machines appears
three times, once under each, and is counted three times in the summary.

The cost is length. The print styles had been tuned to save pages; this spends
some of them on clarity, deliberately.

## 2026-09 — Clients are grouped ignoring capitals, spaces and punctuation
The organisation is typed by hand on every machine, and option 4 matched it
exactly. "Anywhere IT" and "Anywhere I.T" - both already on the toolkit's own
test machine - produced two reports for one client. Grouping now ignores
capitals, spaces and punctuation; the report title uses the most common
spelling, preferring the one with capitals, so a client never sees their own
name in lower case. The technician is told when a name was typed more than one
way, so the habit gets fixed.

The accepted risk: two genuinely different clients whose names differ only by
punctuation would merge. Judged far less likely than inconsistent typing.

The newest month is also now chosen per client. It was chosen across the whole
folder, so a stick used at two clients in different months silently dropped
one - visit one on the 30th and another on the 1st, and the first vanished with
a single grey line. The one-month rule was always meant to stop ONE client's
months blending, not to drop whole clients.

## 2026-09 — Check the rendered page, not just the HTML
Five faults in the v2.11 work were invisible in the HTML and obvious on the
printed page, found only by rendering the PDF to images with Windows' own PDF
engine (Windows.Data.Pdf) and looking:

- Edge's own header and footer on every page, including the temp file path with
  the Windows username. The code asked Edge not to print them, using a flag name
  Chromium has since renamed and silently ignores. Every PDF made on current Edge
  had carried it - the free check's included.
- "running at 34&deg;C" - an entity escaped twice.
- "Drive C: optimised - TRIM" beside "this machine's drives have never been
  tidied" on the same page. Windows may not record our own TRIM where the health
  check looks, so the maintenance record now wins.
- "Nothing was changed on any machine" on reports listing the files maintenance
  had just cleared.
- The same cleared figure printed as "1,76 GB" and "1,8 GB" a few lines apart.

The report is the product - it is the thing the client holds. Anything that
changes it gets rendered and looked at before it ships, the same way the package
gets extracted and run rather than inspected.
