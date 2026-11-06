# The nightly copy of the server's backups, to the office

The live server makes an encrypted backup every night at 02:30, on its own disk. These scripts copy
it every night at 04:00 to `\\192.168.1.33\anywhereit\Backups\RemoteSupport`, so losing the server no
longer means losing the backups. HANDOVER section 120.

- The copies stay **encrypted** (AES-256). The passphrase is NOT copied, and must never be put beside them.
- The office PC gets a key of its own that the server accepts **only from the office's fixed address**,
  and that can only **list** and **fetch** backups: no shell, no other file, nothing deleted.
- Each copy is checked byte for byte (SHA-256) against the server's own before it is kept. 60 days are kept.
- `pull-log.txt` on the share says what every night did; `LAST-OK.txt` says when the last good copy was.

## Setting it up: three steps, about ten minutes

**Before you start:** copy this `deploy\backup` folder onto the office PC, for example through the share.

**Step 1, on the office PC**, signed in as the account that opens the share. Open PowerShell in the
copied folder and run:

    powershell -ExecutionPolicy Bypass -File Install-BackupPull.ps1

It ends with *Done. The public half is on the share*.

**Step 2, on your laptop, IN THE OFFICE** (the server learns the office's address from this connection):

    powershell -ExecutionPolicy Bypass -File deploy\backup\Enable-OffsiteBackup.ps1

It asks for your key's passphrase three times, and asks you to confirm the office's address (type `y`).
It ends with *Done. The server lets that key in from ... only*.

**Step 3, back on the office PC:**

    powershell -ExecutionPolicy Bypass -File Install-BackupPull.ps1 -Finish

It checks the server is the real one, copies a backup straight away, then a Windows box asks for that
account's own password (so the copy can run at night with nobody signed in). It ends with
*Done. Every night at 04:00 ...*.

## The passphrase, into your password manager (once)

Without it the copies cannot be opened. On your laptop, this puts it on the clipboard without showing it:

    ssh ubuntu@154.65.110.136 "sudo cat /etc/rsp-backup-key" | clip

Paste it into the password manager as "Remote Support backup passphrase", then copy something else to
clear the clipboard.

## Checking it

Open `\\192.168.1.33\anywhereit\Backups\RemoteSupport\LAST-OK.txt` the next morning. A `WARNING` line in
`pull-log.txt` means the server's own nightly backup has stopped; a `FAILED` line means the copy did not
happen, and says why.
