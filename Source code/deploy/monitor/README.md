# Knowing when the website is down — from the office PC

Until this, nothing told us the live server had stopped except a customer. The office PC that copies the
backups (`deploy\backup`) now also checks `https://theremotesupportprogram.com/health` every 2 minutes and
emails Clint. HANDOVER section 170.

**What you get, and only this:**

- **"theremotesupportprogram.com is DOWN"** — once, when it has not answered two checks in a row (about 2–4
  minutes; a deploy's few seconds never send one). It says what the check saw and the two commands to look with.
- **"… is back up (down about N minutes)"** — once, when it answers again.
- **"… its certificate runs out in N days"** — once a day, only if the HTTPS certificate has under 14 days left
  (Caddy renews at 30, so it has been failing for two weeks).
- **"… no backup copied for N hours"** — once a day, only if the nightly copy to the share has not arrived for
  36 hours.

When the office's own internet is down it cannot tell, so it says nothing (and writes *office offline* in its
log). **While the office PC is off or asleep, nothing is watching** — keep it on and set never to sleep.

The emails are sent through a **theremotesupportprogram.com mailbox** on xneelo's shared hosting, which stays up
when our server is down. Its password is kept on the office PC in `%USERPROFILE%\.rsp-watch\mail.xml`, readable
only by that Windows account on that PC.

## Setting it up: one command, about five minutes

On the **office PC**, signed in as the same account that copies the backups, copy this `deploy\monitor` folder
over (through the share is fine), open PowerShell in it and run:

    powershell -ExecutionPolicy Bypass -File Install-SiteWatch.ps1

1. A Windows box asks for **the mailbox to send from**: its full address as the user name (for example the one on
   Owner → Mail) and its password. A test email goes to clint@anywhereit.co.za straight away — *"The website check
   is set up"*. If it does not arrive, nothing else is set up; run it again.
2. It checks the site once and prints the result.
3. A second Windows box asks for **this Windows account's password**, so the check runs while nobody is signed in.

It ends with *Done. Every 2 minutes this PC checks …*.

To send the alerts somewhere else: `-To someone@example.com`. To stop: `-Remove`.

## Is it working?

`%USERPROFILE%\.rsp-watch\watch-log.txt` on the office PC has one line per check — *up (180 ms)*, *NOT
answering*, *office offline*. Task Scheduler shows **Remote Support - watch the website**.

## Proved how

`tools\Test-SiteWatch.ps1` on any PC, sending nothing (every email goes to a folder): 24 checks — the real site up,
one miss and no email, two misses and ONE email, none while it stays down, none while the office is offline, ONE
"back up", the certificate and the backup once a day, an email that cannot go is tried again, the test email.
**The real send through the mailbox is proved by the installer's own test email.**
