using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The mailbox the PRODUCT writes from, as opposed to a tenant's own mailbox, which is for
/// writing to that tenant's customers. See PlatformSettings for why the two are separate.
///
/// Everything here degrades quietly and says so rather than throwing: a trial notice that cannot
/// be delivered must never take the server down or stop the portal banner, which is the half that
/// works with no configuration at all.
/// </summary>
public sealed class PlatformMailService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly IDataProtector _protector;
    private readonly ILogger<PlatformMailService> _log;

    public PlatformMailService(IDbContextFactory<PlatformDb> dbf, IDataProtectionProvider dp, ILogger<PlatformMailService> log)
    {
        _dbf = dbf; _log = log;
        // A DIFFERENT purpose string from the tenant one. Purposes are part of the key
        // derivation, so this is what stops a platform password being decryptable by anything
        // holding a tenant protector, and vice versa.
        _protector = dp.CreateProtector("RemoteSupport.PlatformSmtpPassword.v1");
    }

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    /// <summary>Null when it cannot be read — a key ring that has moved, usually. Never throws.</summary>
    public string? Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try { return _protector.Unprotect(cipher); }
        catch (Exception ex) { _log.LogWarning(ex, "the platform mail password could not be decrypted — data/keys has probably moved (handover section 10)"); return null; }
    }

    /// <summary>The single settings row, created on first use so callers never deal with null.</summary>
    public async Task<PlatformSettings> GetAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.PlatformSettings.FirstOrDefaultAsync();
        if (s != null) return s;
        s = new PlatformSettings { Id = 1 };
        db.PlatformSettings.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    public async Task SaveAsync(Action<PlatformSettings> edit)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.PlatformSettings.FirstOrDefaultAsync();
        if (s == null) { s = new PlatformSettings { Id = 1 }; db.PlatformSettings.Add(s); }
        edit(s);
        s.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Our mailbox as a <see cref="MailServer"/>, for mail that must carry headers of its own — the
    /// auto-reply to an emailed ticket (2026-10-05) says it is automatic and which ticket it is.
    /// </summary>
    public async Task<(MailServer? server, string from, string why)> SenderAsync()
    {
        var settings = await GetAsync();
        if (!settings.MailConfigured) return (null, "", "no platform mailbox is configured (Owner → Mail)");
        var password = Unprotect(settings.SmtpPasswordEnc);
        if (password == null) return (null, "", "the stored mail password could not be decrypted");
        var port = settings.SmtpPort > 0 ? settings.SmtpPort : 587;
        var security = !settings.SmtpUseSsl ? "none" : port == 465 ? "ssl" : "starttls";
        var from = string.IsNullOrWhiteSpace(settings.SmtpFrom) ? settings.SmtpUser : settings.SmtpFrom;
        return (new MailServer(settings.SmtpHost, port, security, settings.SmtpUser, password), from.Trim(), "");
    }

    /// <summary>
    /// Sends, and returns "sent" or the reason it did not. The reason is a returned string rather
    /// than an exception because every caller wants to record it and carry on — a notice that
    /// failed must still be visible, and must not be marked as sent.
    /// </summary>
    public async Task<string> SendAsync(IEnumerable<string> to, string subject, string body) =>
        await SendAsync(to, subject, body, null, null, copyOwner: true);

    /// <summary>
    /// The same, written on a COMPANY's behalf (2026-10-01): an invitation or a reset link reads as
    /// coming from "Anywhere IT", from our mailbox, and a reply goes to the company's own support
    /// address. <paramref name="copyOwner"/> false leaves out the owner's copy (NoticeCopyTo) — a
    /// password link is for one person's eyes, not a copy in somebody else's inbox.
    /// <paramref name="cc"/> is a copy the recipient SEES (2026-10-23: the technician helping somebody set up a free
    /// account, chosen by a platform admin) — never a hidden one.
    /// </summary>
    public async Task<string> SendAsync(IEnumerable<string> to, string subject, string body, string? fromName, string? replyTo, bool copyOwner,
        IEnumerable<string>? cc = null)
    {
        var settings = await GetAsync();
        if (!settings.MailConfigured) return "no platform mailbox is configured (Owner → Mail)";

        var password = Unprotect(settings.SmtpPasswordEnc);
        if (password == null) return "the stored mail password could not be decrypted";

        var recipients = to.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Count == 0) return "no recipients";

        try
        {
            var fromAddress = string.IsNullOrWhiteSpace(settings.SmtpFrom) ? settings.SmtpUser : settings.SmtpFrom;
            var name = !string.IsNullOrWhiteSpace(fromName) ? fromName : settings.SmtpFromName;
            using var msg = new MailMessage
            {
                From = string.IsNullOrWhiteSpace(name) ? new MailAddress(fromAddress) : new MailAddress(fromAddress, name),
                Subject = subject,
                Body = body
            };
            foreach (var r in recipients) msg.To.Add(r);
            foreach (var c in (cc ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase).Where(a => !recipients.Contains(a, StringComparer.OrdinalIgnoreCase)))
                msg.CC.Add(c);
            if (!string.IsNullOrWhiteSpace(replyTo))
                try { msg.ReplyToList.Add(replyTo.Trim()); } catch (FormatException) { /* a bad support address must not stop the mail */ }
            if (copyOwner)
                foreach (var c in settings.NoticeCopyTo.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    msg.Bcc.Add(c);

            using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort > 0 ? settings.SmtpPort : 587)
            {
                EnableSsl = settings.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 60_000
            };
            if (!string.IsNullOrWhiteSpace(settings.SmtpUser))
            {
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new NetworkCredential(settings.SmtpUser, password);
            }
            await smtp.SendMailAsync(msg);
            _log.LogInformation("platform mail sent to {To}: {Subject}", string.Join(", ", recipients), subject);
            return "sent";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "platform mail failed: {Subject}", subject);
            return ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
        }
    }
}
