using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// @mentions (stage 3.7, step 4). Clint: "if more details are needed one can simply post in a
/// comment section @so and so what is going on here and it flags that person to add to the
/// comments". Staff only; a badge in the console (and in the portal, for the office administrator,
/// who has no console); **the flag stays until they reply on that ticket** — answer 18. There is no
/// dismiss button on purpose: the flag is a question, and the answer is a reply.
/// </summary>
public sealed partial class TicketService
{
    /// <summary>
    /// Who a comment mentions, from the people who could be mentioned. "@Sue Jones" by full display
    /// name; "@Sue" by first name only while exactly one person has it. Longest names are tried first,
    /// so "@Sue Jones" is never read as "@Sue" followed by the word Jones. Case does not matter.
    /// </summary>
    public static List<string> MentionsIn(string body, IEnumerable<string> people)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(body) || !body.Contains('@')) return found;
        var names = people.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var firsts = names.GroupBy(n => n.Split(' ')[0], StringComparer.OrdinalIgnoreCase)
                          .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        for (int i = body.IndexOf('@'); i >= 0; i = body.IndexOf('@', i + 1))
        {
            // An email address is not a mention: "sales@supplier.test".
            if (i > 0 && char.IsLetterOrDigit(body[i - 1])) continue;
            var rest = body[(i + 1)..];
            var full = names.Where(n => rest.StartsWith(n, StringComparison.OrdinalIgnoreCase) && Ends(rest, n.Length))
                            .OrderByDescending(n => n.Length).FirstOrDefault();
            if (full == null)
            {
                var word = new string(rest.TakeWhile(char.IsLetterOrDigit).ToArray());
                if (word.Length > 0) firsts.TryGetValue(word, out full);
            }
            if (full != null && !found.Contains(full)) found.Add(full);
        }
        return found;

        static bool Ends(string s, int at) => at >= s.Length || !char.IsLetterOrDigit(s[at]);
    }

    /// <summary>
    /// Who the @ list offers first (2026-10-21): everybody assigned, then whoever did the work if that is
    /// somebody else — those of them who can be mentioned. Everybody else follows them in the list.
    /// </summary>
    public static List<string> OnTicket(string? assignedTo, string? workedBy, IEnumerable<string> mentionable)
    {
        var can = mentionable.ToHashSet();
        return Assignees.Parse(assignedTo).Append((workedBy ?? "").Trim()).Where(can.Contains).Distinct().ToList();
    }

    /// <summary>The people who may be mentioned: every enabled member of the company.</summary>
    private static async Task<List<string>> MentionablesAsync(PlatformDb db, int tenantId) =>
        await db.Technicians.Where(t => t.TenantId == tenantId && !t.Disabled).Select(t => t.DisplayName).ToListAsync();

    /// <summary>
    /// What happens around a person's own comment or Work done entry: anyone it mentions is flagged,
    /// and any flag waiting for the author on this ticket is answered by it. Called by both.
    /// </summary>
    /// <returns>Everybody whose waiting count this changes, for <see cref="MentionsChanged"/>.</returns>
    private static async Task<List<string>> ApplyMentionsAsync(PlatformDb db, int tenantId, Ticket t, TicketNote note)
    {
        var touched = new List<string>();
        var answered = await db.TicketMentions.Where(m => m.TicketId == t.Id && m.Person == note.Author && m.AnsweredUtc == null).ToListAsync();
        foreach (var open in answered) open.AnsweredUtc = DateTime.UtcNow;
        if (answered.Count > 0) touched.Add(note.Author);

        foreach (var who in MentionsIn(note.Body, await MentionablesAsync(db, tenantId)))
        {
            if (who == note.Author) continue;   // mentioning yourself asks nobody anything
            db.TicketMentions.Add(new TicketMention { TenantId = tenantId, TicketId = t.Id, Note = note, Person = who, MentionedBy = note.Author });
            touched.Add(who);
        }
        return touched;
    }

    /// <summary>
    /// Raised AFTER the change is saved, with the company and the person whose waiting count moved.
    /// The hub listens and pushes the new count to that person's open consoles, so the badge moves
    /// the moment somebody asks — the service knows nothing about sockets.
    /// </summary>
    public event Action<int, string>? MentionsChanged;

    private void RaiseMentionsChanged(int tenantId, IEnumerable<string> people)
    {
        foreach (var p in people.Distinct())
            try { MentionsChanged?.Invoke(tenantId, p); } catch { /* a listener's fault must not undo a saved comment */ }
    }

    /// <summary>
    /// A customer's emailed reply on a ticket (2026-10-09), shown as theirs. Clint: a reply comes back
    /// into the same ticket — and it must not be missed, so everybody on the ticket is flagged, the way
    /// an @mention flags somebody, and the person who last emailed the client is flagged too (they are
    /// waiting for the answer). Unlike a mention, a flag for a customer's reply clears when that person
    /// OPENS the ticket (<see cref="SeenCustomerRepliesAsync"/>) — there is nobody to reply to here — or
    /// when they comment on it. An "@Jason" in the customer's words flags nobody.
    /// </summary>
    public async Task<TicketNote?> AddCustomerReplyAsync(int tenantId, int ticketId, string author, string body, string flaggedAs)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        List<string> touched;
        TicketNote note;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
            if (t == null) return null;
            note = new TicketNote { TenantId = tenantId, TicketId = ticketId, Author = author, Body = body.Trim(), FromCustomer = true, Stage = t.Stage };
            db.TicketNotes.Add(note);
            var lastSender = await db.TicketClientUpdates.Where(u => u.TenantId == tenantId && u.TicketId == ticketId && u.Sent)
                .OrderByDescending(u => u.Id).Select(u => u.SentBy).FirstOrDefaultAsync();
            var enabled = await MentionablesAsync(db, tenantId);
            touched = Assignees.Parse(t.AssignedTo).Append(lastSender ?? "")
                .Where(p => p.Length > 0 && enabled.Contains(p)).Distinct().ToList();
            foreach (var who in touched)
                db.TicketMentions.Add(new TicketMention { TenantId = tenantId, TicketId = t.Id, Note = note, Person = who, MentionedBy = flaggedAs });
            t.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        RaiseMentionsChanged(tenantId, touched);
        return note;
    }

    /// <summary>
    /// The customer's replies this person was flagged for on this ticket are seen: they opened it. Asked
    /// AFTER the page or the console has read the flags, so the reply is still pointed out on that view.
    /// An @mention from a colleague is NOT cleared by looking — that one is answered by replying.
    /// </summary>
    public async Task SeenCustomerRepliesAsync(int tenantId, int ticketId, string person)
    {
        if (string.IsNullOrEmpty(person)) return;
        await using var db = await _dbf.CreateDbContextAsync();
        var open = await db.TicketMentions.Where(m => m.TenantId == tenantId && m.TicketId == ticketId && m.Person == person
                                                   && m.AnsweredUtc == null && m.Note.FromCustomer).ToListAsync();
        if (open.Count == 0) return;
        foreach (var m in open) m.AnsweredUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        RaiseMentionsChanged(tenantId, new[] { person });
    }

    /// <summary>The flags waiting on one person — the console's badge and the portal's.</summary>
    public async Task<List<TicketMention>> OpenMentionsAsync(int tenantId, string person)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketMentions.Include(m => m.Ticket)
            .Where(m => m.TenantId == tenantId && m.Person == person && m.AnsweredUtc == null)
            .OrderBy(m => m.CreatedUtc).ToListAsync();
    }
}
