# Automatic delivery by email

Each machine emails its results the moment its monthly check finishes. No server,
no cloud account, nothing to maintain.

## Before you start

**Create a dedicated mailbox.** Something like `healthcheck@anywhereit.co.za`.

This account's password is stored on every client machine. It is encrypted with
the Windows machine key so it is not sitting in plain text, but an administrator
on that machine could still recover it. So:

- Never use your real mail account
- Give the account no access to anything except sending
- If your provider supports app passwords, use one
- If it leaks, you change one password and reinstall. No other damage.

## Installing with email

```
.\Install-HealthCheck.ps1 `
    -Organisation "Riverside Guest House" `
    -ClientName   "Riverside Guest House" `
    -EmailTo      "reports@anywhereit.co.za" `
    -SmtpServer   "smtp.yourprovider.co.za" `
    -SmtpPort     587 `
    -SmtpUser     "healthcheck@anywhereit.co.za" `
    -SmtpPassword "the-app-password"
```

Common settings:

| Provider   | Server                  | Port |
|------------|-------------------------|------|
| Gmail      | smtp.gmail.com          | 587  |
| Microsoft  | smtp.office365.com      | 587  |
| Most hosts | mail.yourdomain.co.za   | 587  |

Add `-NoSsl` only if your mail server genuinely does not support encryption.
Almost none do these days.

## What arrives

One email per machine per month. Subject line:

```
[Riverside Guest House] Riverside Guest House - RECEPTION-PC - 2 urgent, 3 to plan
```

The body lists the headline issues. The full results file is attached.

Filter these into a folder in your mailbox. At month end, save the attachments
into your `Collected` folder and run `Make-Reports.bat`.

## If email fails

Nothing is lost. The results file is always written to the machine first, and
email is attempted afterwards. A failure writes the reason to `last-send.txt`
in the install folder, and the check carries on.

## Why reports are not emailed to clients automatically

Because a wrong report is worse than a late one. The machines send raw data to
you; you generate the reports and glance at them before sending. A report telling
a client their backup failed when it did not is how you lose a client.
