using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace RemoteSupport.Platform.Services;

/// <summary>A mail server and the login for it. Security is "ssl", "starttls" or "none".</summary>
public sealed record MailServer(string Host, int Port, string Security, string User, string Password)
{
    /// <summary>
    /// Microsoft's token instead of a password (Sign in with Microsoft, 2026-10-19, §151): the login is SASL XOAUTH2
    /// as <see cref="User"/> — for reading, the MAILBOX (a shared one may be named here); for sending, the account
    /// that signed in. Null for an ordinary password login.
    /// </summary>
    public string? AccessToken { get; init; }
    /// <summary>With <see cref="AccessToken"/>: the Microsoft account that signed in, for the words when it is refused.</summary>
    public string SignedInAs { get; init; } = "";

    /// <summary>
    /// Plain (no encryption) only to this machine — the harnesses' own fake servers, which is the one
    /// place a mail server can be on the platform's own loopback. Anywhere else it is SSL or STARTTLS:
    /// a company's mail password must never cross the internet in the clear, whatever a form says.
    /// </summary>
    public SecureSocketOptions Socket =>
        MailWire.IsLoopback(Host) ? SecureSocketOptions.None
        : Security == "ssl" ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTls;
}

/// <summary>One email as the intake reads it: who, to whom, what, and whether a machine sent it.</summary>
public sealed class IncomingMail
{
    public string MessageId { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    /// <summary>A single Reply-To, when there is one. A website's form robot writes FROM itself
    /// with the customer here — <c>TicketIntake.RequesterOf</c> decides which one is the person.</summary>
    public string ReplyToAddress { get; set; } = "";
    public string ReplyToName { get; set; } = "";
    /// <summary>Every address it says it was for: To, Cc and the delivery headers a forward leaves.</summary>
    public List<string> Recipients { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Text { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public List<string> References { get; set; } = new();
    /// <summary>Why a machine sent it (an out-of-office, a bounce, a newsletter), or empty.</summary>
    public string Automatic { get; set; } = "";
    /// <summary>It asked not to be answered automatically (X-Auto-Response-Suppress).</summary>
    public bool NoAutoReply { get; set; }
    public bool Spam { get; set; }
    public List<string> Attachments { get; set; } = new();
    public DateTime? Date { get; set; }
}

/// <summary>What one look at a mailbox found.</summary>
public sealed class MailboxRead
{
    public long UidValidity { get; set; }
    /// <summary>The highest message number now looked at.</summary>
    public long LastUid { get; set; }
    /// <summary>The first look at a mailbox only notes where it is up to: mail already there is NOT
    /// made into tickets — switching it on must not turn five years of inbox into five thousand tickets.</summary>
    public bool Baseline { get; set; }
    public int Count { get; set; }
    public List<IncomingMail> Mails { get; set; } = new();
    /// <summary>Messages too large to fetch, by their envelope only.</summary>
    public List<IncomingMail> TooLarge { get; set; } = new();
}

/// <summary>
/// Everything that talks to a mail server, and the only file that knows MailKit (MIT, the one
/// library this needed — reading mail from the internet is a parser for hostile input, and this one
/// has had twenty years of it). Reads are READ-ONLY: the folder is opened with EXAMINE, so nothing
/// is ever marked read, moved or deleted in a company's mailbox. Whatever goes wrong comes back as
/// words a company can act on (<see cref="Explain"/>), never a stack trace.
/// </summary>
public static class MailWire
{
    /// <summary>Larger than this is not fetched: it is logged, and the company reads it in their mail.</summary>
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int TextMax = 20_000;

    public static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    // ---------------------------------------------------------------- reading

    /// <summary>Signs in and opens the folder, and says how many messages it holds. For the Test button.</summary>
    public static async Task<int> TestReadAsync(MailServer s, string folder, CancellationToken ct)
    {
        using var imap = await ConnectImapAsync(s, ct);
        var f = await OpenAsync(imap, folder, ct);
        var n = f.Count;
        await imap.DisconnectAsync(true, ct);
        return n;
    }

    /// <summary>
    /// The messages that arrived after <paramref name="afterUid"/>, oldest first, at most
    /// <paramref name="max"/>. A first look (or a folder whose UIDVALIDITY changed, which renumbers
    /// everything) fetches nothing and only notes where the folder is up to.
    /// </summary>
    public static async Task<MailboxRead> ReadNewAsync(MailServer s, string folder, long knownValidity, long afterUid, int max, CancellationToken ct)
    {
        using var imap = await ConnectImapAsync(s, ct);
        var f = await OpenAsync(imap, folder, ct);
        var read = new MailboxRead { UidValidity = f.UidValidity, Count = f.Count, LastUid = afterUid };

        // Never looked at (no UIDVALIDITY kept), or renumbered. NOT "afterUid is 0": that is an
        // EMPTY mailbox, and treating it as never-looked-at re-took the starting point on every look
        // — so the first message a brand-new mailbox ever received fell behind it and was lost.
        // Found by mailintakecheck, on the forwarding mailbox, which starts empty.
        if (knownValidity == 0 || knownValidity != f.UidValidity)
        {
            read.Baseline = true;
            read.LastUid = await HighestUidAsync(f, ct);
            await imap.DisconnectAsync(true, ct);
            return read;
        }

        // "UID n:*" always answers with the newest message even when it is older than n (RFC 3501),
        // so the numbers are filtered again here rather than trusted.
        var found = await f.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)Math.Min(afterUid + 1, uint.MaxValue)), UniqueId.MaxValue)), ct);
        var uids = found.Where(u => u.Id > afterUid).OrderBy(u => u.Id).Take(max).ToList();
        if (uids.Count > 0)
        {
            var sizes = await f.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Envelope, ct);
            foreach (var m in sizes.OrderBy(x => x.UniqueId.Id))
            {
                ct.ThrowIfCancellationRequested();
                if (m.Size is uint size && size > MaxBytes)
                    read.TooLarge.Add(FromEnvelope(m.Envelope));
                else
                    read.Mails.Add(Read(await f.GetMessageAsync(m.UniqueId, ct)));
                read.LastUid = Math.Max(read.LastUid, m.UniqueId.Id);
            }
            // A message that vanished between the search and the fetch is simply passed over.
            read.LastUid = Math.Max(read.LastUid, uids.Max(u => u.Id));
        }
        await imap.DisconnectAsync(true, ct);
        return read;
    }

    private static async Task<long> HighestUidAsync(IMailFolder f, CancellationToken ct)
    {
        if (f.UidNext is { } next && next.Id > 0) return next.Id - 1;
        if (f.Count == 0) return 0;
        var last = await f.FetchAsync(f.Count - 1, f.Count - 1, MessageSummaryItems.UniqueId, ct);
        return last.Count > 0 ? last[0].UniqueId.Id : 0;
    }

    private static async Task<ImapClient> ConnectImapAsync(MailServer s, CancellationToken ct)
    {
        var imap = new ImapClient { Timeout = 30_000 };
        try
        {
            await imap.ConnectAsync(s.Host, s.Port, s.Socket, ct);
            await SignInAsync(imap, s, ct);
            return imap;
        }
        catch { imap.Dispose(); throw; }
    }

    private static async Task<IMailFolder> OpenAsync(ImapClient imap, string folder, CancellationToken ct)
    {
        var f = string.IsNullOrWhiteSpace(folder) || folder.Trim().Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? imap.Inbox : await imap.GetFolderAsync(folder.Trim(), ct);
        // EXAMINE: read only. Nothing in their mailbox is marked read, moved or deleted, ever.
        await f.OpenAsync(FolderAccess.ReadOnly, ct);
        return f;
    }

    // ---------------------------------------------------------------- what a message says

    private static readonly string[] DeliveryHeaders =
        { "Delivered-To", "X-Original-To", "Envelope-To", "X-Envelope-To", "X-Forwarded-To", "X-Forwarded-For", "Resent-To", "X-Rcpt-To", "X-Delivered-To" };
    private static readonly Regex AnAddress = new(@"[A-Za-z0-9._%+'\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    /// <summary>Reads a parsed message into what the intake needs. Public so a harness can feed one.</summary>
    public static IncomingMail Read(MimeMessage m)
    {
        var from = m.From.Mailboxes.FirstOrDefault();
        var replyTo = m.ReplyTo.Mailboxes.ToList();
        var mail = new IncomingMail
        {
            MessageId = m.MessageId ?? "",
            FromAddress = (from?.Address ?? "").Trim().ToLowerInvariant(),
            FromName = (from?.Name ?? "").Trim(),
            ReplyToAddress = replyTo.Count == 1 ? replyTo[0].Address.Trim().ToLowerInvariant() : "",
            ReplyToName = replyTo.Count == 1 ? (replyTo[0].Name ?? "").Trim() : "",
            Subject = (m.Subject ?? "").Trim(),
            InReplyTo = m.InReplyTo ?? "",
            References = m.References.ToList(),
            Date = m.Date == DateTimeOffset.MinValue ? null : m.Date.UtcDateTime
        };
        foreach (var a in m.To.Mailboxes.Concat(m.Cc.Mailboxes)) mail.Recipients.Add(a.Address.Trim().ToLowerInvariant());
        foreach (var h in DeliveryHeaders)
            foreach (var v in m.Headers.Where(x => x.Field.Equals(h, StringComparison.OrdinalIgnoreCase)))
                foreach (Match a in AnAddress.Matches(v.Value)) mail.Recipients.Add(a.Value.ToLowerInvariant());
        mail.Recipients = mail.Recipients.Distinct().ToList();

        string H(string name) => (m.Headers[name] ?? "").Trim();
        var auto = H("Auto-Submitted").ToLowerInvariant();
        var precedence = H("Precedence").ToLowerInvariant();
        var local = mail.FromAddress.Split('@')[0];
        var subject = mail.Subject.ToLowerInvariant();
        mail.Automatic =
            auto.Length > 0 && auto != "no" ? "it is an automatic reply (an out-of-office, or similar)"
            : H("X-Autoreply").Length > 0 || H("X-Autorespond").Length > 0 ? "it is an automatic reply (an out-of-office, or similar)"
            : local is "mailer-daemon" or "postmaster" || m.Body is MultipartReport ? "it is a delivery failure notice (a bounce)"
            : precedence is "bulk" or "junk" or "list" || H("List-Id").Length > 0 || H("List-Unsubscribe").Length > 0
                ? "it is a newsletter or mailing list, not a person writing to you"
            : subject.StartsWith("automatic reply:") || subject.StartsWith("out of office") || subject.StartsWith("auto:")
                || subject.StartsWith("undeliverable:") || subject.StartsWith("delivery status notification")
                ? "it is an automatic reply (an out-of-office, or similar)"
            : "";
        var suppress = H("X-Auto-Response-Suppress").ToLowerInvariant();
        mail.NoAutoReply = suppress.Contains("all") || suppress.Contains("autoreply");
        mail.Spam = H("X-Spam-Flag").Equals("yes", StringComparison.OrdinalIgnoreCase)
                 || H("X-Spam-Status").StartsWith("yes", StringComparison.OrdinalIgnoreCase)
                 || H("X-Spam").Equals("yes", StringComparison.OrdinalIgnoreCase)
                 || mail.Subject.StartsWith("***SPAM***", StringComparison.OrdinalIgnoreCase)
                 || mail.Subject.StartsWith("[SPAM]", StringComparison.OrdinalIgnoreCase);

        var text = m.TextBody;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(m.HtmlBody)) text = HtmlToText(m.HtmlBody);
        mail.Text = Tidy(text ?? "");
        foreach (var a in m.Attachments)
            mail.Attachments.Add(a is MimePart p ? (p.FileName ?? "attachment") : a is MessagePart ? "an attached email" : "attachment");
        return mail;
    }

    private static IncomingMail FromEnvelope(Envelope? e)
    {
        var from = e?.From.Mailboxes.FirstOrDefault();
        return new IncomingMail
        {
            MessageId = e?.MessageId ?? "",
            FromAddress = (from?.Address ?? "").ToLowerInvariant(),
            FromName = from?.Name ?? "",
            Subject = e?.Subject ?? ""
        };
    }

    /// <summary>Line endings made one kind, runs of blank lines made one, and a sensible length.</summary>
    public static string Tidy(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, "[ \t]+\n", "\n");
        s = Regex.Replace(s, "\n{3,}", "\n\n").Trim();
        return s.Length <= TextMax ? s : s[..TextMax] + "\n\n[… the rest is in the email]";
    }

    /// <summary>HTML mail as text: blocks become lines, tags go, entities are decoded.</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|tr|li|h[1-6]|blockquote|table)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        // &nbsp; decodes to a non-breaking space, which no search for an ordinary space ever finds.
        return WebUtility.HtmlDecode(s).Replace(' ', ' ');
    }

    // ---------------------------------------------------------------- sending

    public static async Task SendAsync(MailServer s, MimeMessage message, CancellationToken ct)
    {
        using var smtp = new SmtpClient { Timeout = 60_000 };
        await smtp.ConnectAsync(s.Host, s.Port, s.Socket, ct);
        if (!string.IsNullOrWhiteSpace(s.User)) await SignInAsync(smtp, s, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    /// <summary>Signs in to the sending server without sending anything. For the Test button.</summary>
    public static async Task TestSendAsync(MailServer s, CancellationToken ct)
    {
        using var smtp = new SmtpClient { Timeout = 30_000 };
        await smtp.ConnectAsync(s.Host, s.Port, s.Socket, ct);
        if (!string.IsNullOrWhiteSpace(s.User)) await SignInAsync(smtp, s, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    /// <summary>A password, or Microsoft's token by XOAUTH2 — the one difference between the two ways in.</summary>
    private static Task SignInAsync(MailService client, MailServer s, CancellationToken ct) =>
        s.AccessToken != null
            ? client.AuthenticateAsync(new SaslMechanismOAuth2(s.User, s.AccessToken), ct)
            : client.AuthenticateAsync(s.User, s.Password, ct);

    // ---------------------------------------------------------------- what went wrong, in words

    /// <summary>
    /// A failure as a company can act on it. <c>login</c> is true when the server refused the
    /// username or password — checking then STOPS rather than retrying every minute, because a
    /// cPanel server blocks an address that keeps failing to sign in (cPHulk), and that address is
    /// ours: one company's wrong password would lock out every company on the same host.
    /// </summary>
    public static (string words, bool login) Explain(Exception ex, MailServer s, bool sending, string address = "")
    {
        var host = s.Host;
        var what = sending ? "sending" : "reading";
        var google = IsGoogle(host) || IsGoogle(Domain(address)) || IsGoogle(Domain(s.User));
        var microsoft = IsMicrosoft(host) || IsMicrosoft(Domain(address)) || IsMicrosoft(Domain(s.User));
        var e = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;

        // Signed in with Microsoft: the token was good (Microsoft gave it a moment ago), so a refusal here is the MAILBOX
        // saying no — IMAP or SMTP AUTH switched off for it, or a shared mailbox this account may not open or send as.
        if (s.AccessToken != null)
        {
            var who = s.SignedInAs.Length > 0 ? s.SignedInAs : s.User;
            var shared = !who.Equals(s.User, StringComparison.OrdinalIgnoreCase);
            if (e is SmtpCommandException sad && sad.Message.Contains("SendAsDenied", StringComparison.OrdinalIgnoreCase))
                return ($"Microsoft would not let {who} send as {address}. Give {who} \"Send as\" on that mailbox (Exchange admin centre → "
                      + $"Mailboxes → {address} → Delegation → Send as), or sign in as {address} itself.", false);
            if (e is AuthenticationException or SmtpCommandException { StatusCode: SmtpStatusCode.AuthenticationInvalidCredentials or SmtpStatusCode.AuthenticationRequired })
                return sending
                    ? ($"Microsoft accepted the sign-in, but would not let {who} send by SMTP. SMTP AUTH is switched off — for the mailbox "
                     + $"(Microsoft 365 admin centre → Users → Active users → {who} → Mail → Manage email apps → tick Authenticated SMTP) or for "
                     + "the whole organisation (Exchange admin centre → Settings → Mail flow → untick \"Turn off SMTP AUTH protocol for your organization\").", true)
                    : ($"Microsoft accepted the sign-in, but would not let {who} read {s.User}. Most often IMAP is switched off for that mailbox: "
                     + $"Microsoft 365 admin centre → Users → Active users → {s.User} → Mail → Manage email apps → tick IMAP."
                     + (shared ? $" As {s.User} is not the account that signed in, {who} also needs Full Access to it (Exchange admin centre → Mailboxes → {s.User} → Delegation → Read and manage)." : "")
                     + " Checking has stopped until you save the form again.", true);
        }

        switch (e)
        {
            case AuthenticationException:
            case SmtpCommandException { StatusCode: SmtpStatusCode.AuthenticationInvalidCredentials or SmtpStatusCode.AuthenticationRequired }:
                if (google)
                    return ("Google refused the password. Google does not accept your normal password here: turn on 2-Step Verification for the account, "
                          + "then make an App password (Google Account → Security → App passwords) and paste that in. In Google Workspace, "
                          + "your administrator may have switched app passwords off — if so, use forwarding instead. Checking has stopped until you save the form again.", true);
                if (microsoft)
                    return ("Microsoft refused the password. Microsoft 365 and Outlook.com no longer accept a password from a program like this one — "
                          + "choose \"Microsoft 365 — sign in with Microsoft\" above instead, or forwarding. Checking has stopped until you save the form again.", true);
                return ($"The {(sending ? "sending" : "mail")} server refused the username or password. On cPanel and most hosting the username is the "
                      + "FULL email address (support@yourcompany.co.za), and the password is that mailbox's own password, not your hosting login. "
                      + "Checking has stopped until you save the form again, so that the server does not block us for too many failed sign-ins.", true);

            case SslHandshakeException ssl:
            {
                var names = CertificateNames(ssl.ServerCertificate);
                if (names.Count > 0 && !names.Any(n => NameMatches(n, host)))
                {
                    var plain = names.FirstOrDefault(n => !n.StartsWith("*"));
                    return ($"The server's security certificate is for {string.Join(", ", names.Take(3))}, not {host}. On shared hosting the mail server is "
                          + "often reached by the hosting server's own name — "
                          + (plain != null ? $"try {plain} as the server instead." : "ask your host which name to use for mail."), false);
                }
                return ($"{host} did not complete a secure connection on port {s.Port}. "
                      + (s.Security == "ssl" ? $"Port {s.Port} may expect STARTTLS rather than SSL — " : $"Port {s.Port} may expect SSL rather than STARTTLS — ")
                      + (sending ? "for sending, SSL is usually port 465 and STARTTLS port 587." : "for reading, SSL is usually port 993 and STARTTLS port 143."), false);
            }

            case SocketException se when se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain:
                return ($"There is no server called {host}. Check the spelling — on cPanel hosting it is usually mail.yourdomain (mail.yourcompany.co.za).", false);
            case SocketException se when se.SocketErrorCode is SocketError.ConnectionRefused:
                return ($"{host} refused a connection on port {s.Port}. "
                      + (sending ? "Sending is usually port 465 (SSL) or 587 (STARTTLS)." : "Reading is usually port 993 (SSL) or 143 (STARTTLS)."), false);
            case SocketException or TimeoutException or OperationCanceledException:
                return ($"{host} did not answer on port {s.Port}. Check the server name and port"
                      + (sending ? " — some hosts block sending from outside their own network; if so, choose 'from our mailbox' for the auto-reply." : "."), false);

            case FolderNotFoundException fnf:
                return ($"There is no folder called \"{fnf.FolderName}\" in that mailbox. Leave it as INBOX unless your mail goes somewhere else.", false);

            case SmtpCommandException sc when sc.ErrorCode is SmtpErrorCode.SenderNotAccepted:
                return ($"The server would not send from {address}. The auto-reply must come from the mailbox you signed in as, or an address it may send for.", false);
            case SmtpCommandException sc when sc.ErrorCode is SmtpErrorCode.RecipientNotAccepted:
                return ($"The server would not deliver to that address: {Short(sc.Message)}", false);

            case ImapCommandException ic when google && ic.Response == ImapCommandResponse.No:
                return ("Google refused: IMAP may be switched off for this mailbox (Gmail settings → Forwarding and POP/IMAP → Enable IMAP).", false);
        }
        return ($"Something went wrong {what} mail with {host}: {Short(e.Message)}", false);
    }

    public static bool IsGoogle(string host) =>
        host.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("googlemail.com", StringComparison.OrdinalIgnoreCase);
    public static bool IsMicrosoft(string host) =>
        new[] { "office365.com", "outlook.com", "hotmail.com", "live.com", "onmicrosoft.com" }.Any(d => host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

    public static string Domain(string address)
    {
        var at = (address ?? "").LastIndexOf('@');
        return at < 0 ? "" : address![(at + 1)..].Trim().ToLowerInvariant();
    }

    private static string Short(string s) { s = s.Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length <= 200 ? s : s[..200] + "…"; }

    private static List<string> CertificateNames(X509Certificate? cert)
    {
        var names = new List<string>();
        if (cert == null) return names;
        try
        {
            var c2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
            foreach (var ext in c2.Extensions)
                if (ext is X509SubjectAlternativeNameExtension san) names.AddRange(san.EnumerateDnsNames());
            var cn = c2.GetNameInfo(X509NameType.DnsName, false);
            if (!string.IsNullOrEmpty(cn) && !names.Contains(cn)) names.Insert(0, cn);
        }
        catch { /* no names: the plain message will do */ }
        return names;
    }

    private static bool NameMatches(string certName, string host) =>
        certName.Equals(host, StringComparison.OrdinalIgnoreCase)
        || (certName.StartsWith("*.") && host.EndsWith(certName[1..], StringComparison.OrdinalIgnoreCase)
            && host.Count(c => c == '.') == certName.Count(c => c == '.'));
}
