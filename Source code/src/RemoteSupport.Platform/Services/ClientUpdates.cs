using System.Net;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Updating the client from a ticket (Clint, 2026-10-09): "If someone posts a comment, there should be
/// an option to update client. The client then gets the original fault as per the ticket description
/// with the comment whoever has posted, but not the entire string of the ticket. If a client replies,
/// it should come back into the same ticket. A lot of comments would be for internal purposes only."
///
/// His answers, the same day: a tick box when posting AND "Email to the client" on a comment already
/// posted; to the ticket's contact by default, changeable to another of the client's people or an
/// address typed in; EVERY email is the original fault plus that one comment, never the comments sent
/// before; from the address set up on Portal → Tickets by email, which signup now asks for too; and a
/// reply flags the people on the ticket until they open it.
///
/// Every rule is here. The address it goes from and the way back in are Tickets by email's
/// (<see cref="TicketIntake"/>): the update carries a Message-ID with the ticket in it, and the reply
/// comes back through the same mailbox and onto the same ticket. So updates are offered only once that
/// is working — an update the client cannot answer would be a dead end.
/// </summary>
public sealed class ClientUpdateService
{
    /// <summary>How much of the original fault goes in the email. A long email ticket's whole thread is not the point.</summary>
    public const int FaultMax = 4000;

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TicketIntake _intake;
    private readonly TenantService _tenants;
    private readonly TicketAttachmentService? _files;

    public ClientUpdateService(IDbContextFactory<PlatformDb> dbf, TicketIntake intake, TenantService tenants, TicketAttachmentService? files = null)
    { _dbf = dbf; _intake = intake; _tenants = tenants; _files = files; }

    /// <summary>An address an update may go to, with who it is, for the box that chooses it.</summary>
    public sealed record Recipient(string Address, string Name)
    {
        public string Label => Name.Length > 0 && !Name.Equals(Address, StringComparison.OrdinalIgnoreCase) ? $"{Name} <{Address}>" : Address;
    }

    /// <summary>
    /// What a ticket offers: whether updates can be sent at all (and why not, in words for this person),
    /// the address ticked by default, and the others to choose from. Typing another is always allowed.
    /// </summary>
    /// <param name="From">The support address it goes from, and the client's reply comes back to.</param>
    public sealed record Offer(bool Ready, string WhyNot, string Default, List<Recipient> Choices, string From = "");

    /// <summary>Tickets by email is on and has an address that can take the client's reply.</summary>
    public static bool Ready(TicketIntakeSettings s) =>
        s.Id != 0 && s.EmailEnabled && TicketIntake.LooksLikeEmail(s.Address) && (TicketIntake.ReadsMailbox(s) || (s.Way == "forward" && s.AddressConfirmed));

    /// <summary>Why updates are not offered yet — with where to fix it for somebody who may, and whom to ask for somebody who may not.</summary>
    public static string WhyNotReady(TenantRole role) =>
        Permissions.CanManageCompany(role)
            ? "To email comments to the client, set up Tickets by email first (Portal → Tickets by email). Updates go out from your support address, and the client's replies come back to this ticket through it."
            : "Emailing comments to the client is not set up yet — ask the owner or a supervisor to set up Tickets by email.";

    /// <summary>
    /// Whether a note may be emailed: a comment somebody typed. Never an automatic line, a step's form,
    /// a Work done entry (that is the invoice's) or the customer's own words.
    /// </summary>
    /// <param name="hasFiles">It carries files or photos (2026-10-13) — a comment may be those alone, with no words.</param>
    public static bool Sendable(TicketNote n, bool hasFiles = false) =>
        !n.IsSystem && !n.FromCustomer && !n.IsWorkDone
        // The damage report is a step's form, and the one step the client is meant to see (2026-10-13) — its comments
        // and the report itself, never the time and the amount written under them.
        && (n.FormTitle.Length == 0 || IsDamageReport(n))
        && (Words(n).Length > 0 || hasFiles);

    /// <summary>Not repairable — Damage report needed (2026-10-13): the step whose report is emailed to the client.</summary>
    public static bool IsDamageReport(TicketNote n) => n.FormTitle == Workflow.DamageReportLabel;

    /// <summary>
    /// What of a note the client reads: a comment's words, or — for the damage report — only the comments typed into it,
    /// cut before the "Time spent:" line the step writes under them (the time and the amount are the invoice's).
    /// </summary>
    public static string Words(TicketNote n) => IsDamageReport(n) ? TicketService.WorkDoneText(n.Body) : n.Body.Trim();

    public async Task<Offer> OfferAsync(int tenantId, Ticket t, TenantRole role)
    {
        var s = await _intake.GetAsync(tenantId);
        if (!Ready(s)) return new Offer(false, WhyNotReady(role), "", new(), s.Address);
        await using var db = await _dbf.CreateDbContextAsync();
        var list = new List<Recipient>();
        void Add(string? address, string? name)
        {
            var a = (address ?? "").Trim().ToLowerInvariant();
            if (!TicketIntake.LooksLikeEmail(a) || list.Any(r => r.Address == a)) return;
            list.Add(new Recipient(a, (name ?? "").Trim()));
        }

        // The default, in this order: who wrote in, the ticket's own contact, a contact of that name at
        // the client, the client's own address, and lastly whoever the last update went to.
        Add(t.RequesterEmail, t.ContactName.Contains('@') ? "" : t.ContactName);
        Contact? own = t.ContactId is int cid ? await db.Contacts.FirstOrDefaultAsync(c => c.Id == cid && c.TenantId == tenantId) : null;
        if (own != null) Add(own.Email, own.Name);
        if (t.ClientId is int client)
        {
            var people = await db.Contacts.Where(c => c.TenantId == tenantId && c.ClientId == client && !c.Archived && c.Email != "")
                .OrderBy(c => c.Name).ToListAsync();
            var named = t.ContactName.Trim().ToLowerInvariant();
            foreach (var c in people.Where(c => named.Length > 0 && c.Name.Trim().ToLowerInvariant() == named)) Add(c.Email, c.Name);
            var firm = await db.Clients.Where(c => c.Id == client && c.TenantId == tenantId).Select(c => new { c.Email, c.Name }).FirstOrDefaultAsync();
            if (firm != null) Add(firm.Email, firm.Name);
            foreach (var c in people) Add(c.Email, c.Name);
        }
        var before = await db.TicketClientUpdates.Where(u => u.TenantId == tenantId && u.TicketId == t.Id && u.Sent)
            .OrderByDescending(u => u.Id).Select(u => new { u.ToAddress, u.ToName }).ToListAsync();
        foreach (var u in before) Add(u.ToAddress, u.ToName);
        // Nobody on file: whoever an update went to last is the best guess there is.
        var fallback = list.Count > 0 ? list[0].Address : "";
        return new Offer(true, "", fallback, list, s.Address);
    }

    /// <summary>
    /// Emails one comment to the client: the comment, who wrote it, and the ticket's original fault —
    /// nothing else from the ticket. A row is kept whether it went or not, and the comment shows it.
    /// </summary>
    /// <returns>Whether it went, and what to tell the person who sent it.</returns>
    /// <summary>
    /// What stops an update going to this address, or empty. Asked BEFORE a comment is posted with the box
    /// ticked, so a mistyped address sends the person back with their words kept, rather than leaving a
    /// comment posted and not emailed. <see cref="SendAsync"/> asks it again.
    /// </summary>
    public async Task<string> CheckAsync(int tenantId, string? to, TenantRole role)
    {
        var address = (to ?? "").Trim();
        if (address.Length == 0) return "Choose who to email it to, or type their address.";
        if (!TicketIntake.LooksLikeEmail(address)) return $"\"{address}\" does not look like an email address.";
        if (!Permissions.CanEmailClient(role)) return Permissions.CanWorkTickets(role) ? "You may not email the client." : Permissions.OwnerOnlyRefusal;
        if (!Ready(await _intake.GetAsync(tenantId))) return WhyNotReady(role);
        return "";
    }

    /// <summary>
    /// <see cref="CheckAsync"/>, and the files picked for the comment (2026-10-13) — so files too large to email
    /// together are refused BEFORE anything is posted, with the words kept, as a bad address is.
    /// </summary>
    public async Task<string> CheckWithFilesAsync(int tenantId, int ticketId, string? to, TenantRole role, IEnumerable<long>? fileIds, string by)
    {
        if (await CheckAsync(tenantId, to, role) is { Length: > 0 } refused) return refused;
        if (_files == null || fileIds == null) return "";
        return TicketAttachmentService.TooLargeToEmail(await _files.LooseAsync(tenantId, ticketId, fileIds, by));
    }

    /// <summary>
    /// Emails the step this person has just recorded — the damage report (2026-10-13), "mailed directly from there to the
    /// client". The newest entry of that title by them on the ticket: the one <c>TicketService.ActAsync</c> just wrote.
    /// </summary>
    public async Task<(bool ok, string words, long noteId)> SendStepAsync(int tenantId, int ticketId, string formTitle, string? to, string by, TenantRole role)
    {
        long noteId;
        await using (var db = await _dbf.CreateDbContextAsync())
            noteId = await db.TicketNotes.Where(n => n.TenantId == tenantId && n.TicketId == ticketId && n.FormTitle == formTitle && n.Author == by)
                .OrderByDescending(n => n.Id).Select(n => n.Id).FirstOrDefaultAsync();
        if (noteId == 0) return (false, "The step is recorded, but it could not be found to email.", 0);
        var (ok, words) = await SendAsync(tenantId, ticketId, noteId, to, by, role);
        return (ok, ok ? words : words.Replace("The comment is saved", "The damage report is saved"), noteId);
    }

    public async Task<(bool ok, string words)> SendAsync(int tenantId, int ticketId, long noteId, string? to, string by, TenantRole role)
    {
        if (await CheckAsync(tenantId, to, role) is { Length: > 0 } refused) return (false, refused);
        var address = to!.Trim().ToLowerInvariant();
        var tenant = await _tenants.ByIdAsync(tenantId);
        var s = await _intake.GetAsync(tenantId);
        if (tenant == null) return (false, "That company is not on file.");

        Ticket? t; TicketNote? note; string name; string inReplyTo;
        var files = new List<(string name, string type, byte[] bytes)>();
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
            if (t == null) return (false, "That ticket is not on file.");
            note = await db.TicketNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.TicketId == ticketId && n.TenantId == tenantId);
            if (note == null) return (false, "That comment is not on this ticket.");
            // Its files and photos go with it, as attachments (2026-10-13) — refused whole if they are too large together.
            var attached = await TicketAttachmentService.OfNoteAsync(db, tenantId, note.Id);
            if (!Sendable(note, attached.Count > 0)) return (false, "Only a comment can be emailed to the client — not an automatic line, a step or the client's own reply.");
            if (TicketAttachmentService.TooLargeToEmail(attached) is { Length: > 0 } tooBig) return (false, tooBig);
            foreach (var a in attached)
            {
                var path = _files?.FilePath(tenantId, a.FileName);
                if (path == null || !File.Exists(path)) return (false, $"{a.OriginalName} is no longer on the server, so it could not go with the email.");
                files.Add((a.OriginalName, a.ContentType, await File.ReadAllBytesAsync(path)));
            }
            name = await NameOfAsync(db, tenantId, t, address);
            inReplyTo = await LastInThreadAsync(db, tenantId, t.Id);
        }

        var logo = Logo(tenant);
        var (result, sent) = await _intake.SendAsCompanyAsync(tenant, s, "update to the client", (fromAddress, withReplyTo, replyTo) =>
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(tenant.CompanyName, fromAddress));
            if (withReplyTo && TicketIntake.LooksLikeEmail(replyTo)) msg.ReplyTo.Add(new MailboxAddress(tenant.CompanyName, replyTo));
            msg.To.Add(new MailboxAddress(name, address));
            msg.Subject = TicketIntake.ReplySubject(t.Number, t.Subject);
            msg.MessageId = TicketIntake.NewMessageId(tenantId, t.Number, fromAddress);
            if (inReplyTo.Length > 0) { msg.InReplyTo = inReplyTo; msg.References.Add(inReplyTo); }
            var body = new BodyBuilder { TextBody = Text(tenant, t, note, name) };
            string? cid = null;
            if (logo is { } l)
            {
                var res = body.LinkedResources.Add("logo" + l.ext, l.bytes, ContentType.Parse(l.type));
                res.ContentId = cid = MimeKit.Utils.MimeUtils.GenerateMessageId();
            }
            body.HtmlBody = Html(tenant, t, note, name, cid);
            foreach (var f in files)
                body.Attachments.Add(f.name, f.bytes, ContentType.Parse(f.type.Length > 0 ? f.type : "application/octet-stream"));
            msg.Body = body.ToMessageBody();
            return msg;
        });

        bool ok = sent != null;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            db.TicketClientUpdates.Add(new TicketClientUpdate
            {
                TenantId = tenantId, TicketId = t.Id, NoteId = note.Id, ToAddress = address, ToName = name, SentBy = by,
                Sent = ok, Result = Clip(result, 400), MessageId = Clip(sent?.MessageId ?? "", 300)
            });
            var row = await db.Tickets.FirstAsync(x => x.Id == t.Id);
            row.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return ok
            ? (true, $"Emailed to {address}." + (result.StartsWith("sent from our mailbox") ? " " + Capital(result) + "." : ""))
            : (false, $"The comment is saved, but it was not emailed to {address}: {result["not sent: ".Length..]}");
    }

    /// <summary>Every update sent from a ticket, by the comment it sent — what each comment's marker says.</summary>
    public async Task<Dictionary<long, List<TicketClientUpdate>>> ForTicketAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.TicketClientUpdates.Where(u => u.TenantId == tenantId && u.TicketId == ticketId).OrderBy(u => u.Id).ToListAsync();
        return rows.GroupBy(u => u.NoteId).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>The marker under a comment: "Emailed to jane@acme.co.za by Sue Tech, 9 Oct 14:02", or why it did not go.</summary>
    public static string Marker(TicketClientUpdate u) =>
        u.Sent
            ? $"Emailed to {u.ToAddress}{(u.SentBy.Length > 0 ? " by " + u.SentBy : "")}, {u.SentUtc.Local():d MMM HH:mm}"
            : $"Not emailed to {u.ToAddress} ({u.SentUtc.Local():d MMM HH:mm}): {(u.Result.StartsWith("not sent: ") ? u.Result["not sent: ".Length..] : u.Result)}";

    // ================================================================ the email

    /// <summary>The fault the ticket was opened for, as the client should see it.</summary>
    public static string Fault(Ticket t)
    {
        var f = (t.Description ?? "").Trim();
        return f.Length <= FaultMax ? f : f[..FaultMax].TrimEnd() + " …";
    }

    /// <summary>
    /// The line that says who wrote and what about, starting with the author's name: "Sue Tech has an update on ticket
    /// #12 — Laptop will not start:". A damage report says it is one (2026-10-13), and a comment of files alone says
    /// they are attached, since there are no words under it to read.
    /// </summary>
    public static string Intro(TicketNote note, Ticket t) =>
        IsDamageReport(note)
            ? $"{note.Author} has sent the damage report for ticket #{t.Number} — {t.Subject}. It is attached." + (Words(note).Length > 0 ? " Comments:" : "")
            : Words(note).Length > 0
                ? $"{note.Author} has an update on ticket #{t.Number} — {t.Subject}:"
                : $"{note.Author} has an update on ticket #{t.Number} — {t.Subject}. The files are attached.";

    public static string Text(Tenant tenant, Ticket t, TicketNote note, string name)
    {
        var fault = Fault(t);
        var lines = new List<string>
        {
            $"Hello {Greeting(name)},", "",
            Intro(note, t), ""
        };
        if (Words(note).Length > 0) lines.AddRange(new[] { Words(note), "" });
        if (fault.Length > 0) lines.AddRange(new[] { "----------", "Your original request:", "", fault, "", "----------" });
        lines.AddRange(new[] { "To reply, simply reply to this email.", "", "Kind regards", tenant.CompanyName });
        return string.Join("\n", lines);
    }

    /// <summary>The same words as <see cref="Text"/>, laid out, every piece of them encoded — nothing typed becomes markup.</summary>
    public static string Html(Tenant tenant, Ticket t, TicketNote note, string name, string? logoCid)
    {
        static string E(string s) => WebUtility.HtmlEncode(s).Replace("\r\n", "\n").Replace("\n", "<br>");
        var accent = string.IsNullOrWhiteSpace(tenant.AccentColor) ? "#2E9BFF" : tenant.AccentColor.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9A-Fa-f]{6}$")) accent = "#2E9BFF";
        var fault = Fault(t);
        var head = logoCid != null
            ? $"<img src=\"cid:{logoCid}\" alt=\"{E(tenant.CompanyName)}\" style=\"max-height:56px;max-width:240px\">"
            : $"<div style=\"font-size:20px;font-weight:700\">{E(tenant.CompanyName)}</div>";
        return "<!doctype html><html><body style=\"margin:0;padding:24px;background:#f4f5f7;font-family:Segoe UI,Arial,sans-serif;color:#1d2433\">"
            + "<div style=\"max-width:640px;margin:0 auto;background:#fff;border-radius:12px;padding:24px\">"
            + $"<div style=\"padding-bottom:14px;border-bottom:3px solid {accent};margin-bottom:18px\">{head}</div>"
            + $"<p style=\"margin:0 0 12px\">Hello {E(Greeting(name))},</p>"
            + $"<p style=\"margin:0 0 8px\"><strong>{E(note.Author)}</strong>{E(Intro(note, t)[note.Author.Length..])}</p>"
            + (Words(note).Length > 0 ? $"<div style=\"border-left:4px solid {accent};padding:10px 14px;background:#f7f9fc;margin:0 0 18px\">{E(Words(note))}</div>" : "")
            + (fault.Length > 0
                ? $"<p style=\"margin:0 0 6px;color:#5b6475;font-size:13px;font-weight:700\">Your original request</p><div style=\"padding:10px 14px;border:1px solid #e3e6ec;border-radius:8px;color:#3b4354;font-size:14px;margin:0 0 18px\">{E(fault)}</div>"
                : "")
            + "<p style=\"margin:0 0 12px\">To reply, simply reply to this email.</p>"
            + $"<p style=\"margin:0\">Kind regards<br>{E(tenant.CompanyName)}</p>"
            + "</div></body></html>";
    }

    private static string Greeting(string name)
    {
        var n = (name ?? "").Trim();
        return n.Length == 0 || n.Contains('@') ? "there" : n;
    }

    /// <summary>Who the address is, for "Hello Jane": the person who wrote in, a contact, the client — or nobody named.</summary>
    private static async Task<string> NameOfAsync(PlatformDb db, int tenantId, Ticket t, string address)
    {
        if (address.Equals(t.RequesterEmail, StringComparison.OrdinalIgnoreCase) && !t.ContactName.Contains('@')) return t.ContactName.Trim();
        var contact = await db.Contacts.Where(c => c.TenantId == tenantId && !c.Archived && c.Email.ToLower() == address)
            .OrderBy(c => c.ClientId == t.ClientId ? 0 : 1).Select(c => c.Name).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(contact)) return contact.Trim();
        var before = await db.TicketClientUpdates.Where(u => u.TenantId == tenantId && u.TicketId == t.Id && u.ToAddress == address && u.ToName != "")
            .Select(u => u.ToName).FirstOrDefaultAsync();
        return before ?? "";
    }

    /// <summary>The last message of this ticket's thread — ours or theirs — so the update lands in the same conversation.</summary>
    private static async Task<string> LastInThreadAsync(PlatformDb db, int tenantId, int ticketId)
    {
        var ours = await db.TicketClientUpdates.Where(u => u.TenantId == tenantId && u.TicketId == ticketId && u.Sent && u.MessageId != "")
            .OrderByDescending(u => u.Id).Select(u => new { u.MessageId, When = u.SentUtc }).FirstOrDefaultAsync();
        var theirs = await db.InboundMail.Where(m => m.TenantId == tenantId && m.TicketId == ticketId && m.MessageId != "" && !m.MessageId.StartsWith("nomid:"))
            .OrderByDescending(m => m.Id).Select(m => new { m.MessageId, When = m.ReceivedUtc }).FirstOrDefaultAsync();
        if (ours == null) return theirs?.MessageId ?? "";
        if (theirs == null) return ours.MessageId;
        return theirs.When > ours.When ? theirs.MessageId : ours.MessageId;
    }

    private (byte[] bytes, string type, string ext)? Logo(Tenant t)
    {
        if (string.IsNullOrEmpty(t.LogoFile)) return null;
        try
        {
            var path = Path.Combine(_tenants.LogoDir, t.LogoFile);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 1_000_000) return null;   // a logo, not a photograph
            var png = t.LogoFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            return (bytes, png ? "image/png" : "image/jpeg", png ? ".png" : ".jpg");
        }
        catch { return null; }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
