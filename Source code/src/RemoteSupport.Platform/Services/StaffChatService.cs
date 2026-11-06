using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Office chat (stage 3.8.1, 2026-09-30). Clint: "everybody at the office is able to chat with each
/// other so that when you chat and do either the @ or select the person it pops up a chat window".
/// His answers: one to one, and one group — Everyone; @mentions in ticket comments stay ticket
/// comments; the browser shows a notification.
///
/// <para>The rules live here and nowhere else — who may be written to, what a conversation is, what
/// is unread — and the portal (over HTTP and a server-sent event stream) and the console (over the
/// hub) both ask it. <see cref="Sent"/> and <see cref="ReadChanged"/> are how either hears of a
/// change the other made.</para>
///
/// <para>"Staff" is every enabled person of the company, whatever their role: the office
/// administrator, who has no console, is exactly who most needs to reach a technician. Customers
/// are never part of it — their chat is <see cref="ChatLine"/>, per machine, and separate.</para>
/// </summary>
public sealed class StaffChatService
{
    public const int MaxLength = 4000;
    private readonly IDbContextFactory<PlatformDb> _dbf;

    public StaffChatService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    /// <summary>A message was saved: (tenant, message). Everyone it involves should hear of it.</summary>
    public event Action<int, StaffMessage>? Sent;
    /// <summary>What this person has read changed: (tenant, person). Their unread counts move.</summary>
    public event Action<int, string>? ReadChanged;

    /// <summary>Everybody at the company who can chat, by display name, in name order.</summary>
    public async Task<List<string>> PeopleAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        // Sorted AFTER Distinct and in memory: a DISTINCT in SQL is free to drop an ORDER BY before it.
        var names = await db.Technicians.Where(t => t.TenantId == tenantId && !t.Disabled)
            .Select(t => t.DisplayName).Distinct().ToListAsync();
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Say something. <paramref name="to"/> is a person's display name, or empty for Everyone.
    /// Empty error means it was saved and <see cref="Sent"/> has been raised.
    /// </summary>
    public async Task<(StaffMessage? message, string error)> SendAsync(int tenantId, string from, string? to, string? text)
    {
        to = (to ?? "").Trim();
        text = (text ?? "").Trim();
        if (from.Length == 0) return (null, "Sign in again.");
        if (text.Length == 0) return (null, "Type a message first.");
        if (text.Length > MaxLength) return (null, $"That is longer than {MaxLength} characters. Send it in parts.");
        var people = await PeopleAsync(tenantId);
        if (!people.Contains(from)) return (null, "You are not one of this company's people.");
        if (to.Length > 0)
        {
            if (to == from) return (null, "That is you.");
            // Matched without regard to case, stored as the company spells it.
            var match = people.FirstOrDefault(p => string.Equals(p, to, StringComparison.OrdinalIgnoreCase));
            if (match == null) return (null, $"Nobody called \"{to}\" works here.");
            to = match;
        }

        await using var db = await _dbf.CreateDbContextAsync();
        var m = new StaffMessage { TenantId = tenantId, From = from, To = to, Text = text, SentUtc = DateTime.UtcNow };
        db.StaffMessages.Add(m);
        await db.SaveChangesAsync();
        // Your own message is read by you.
        await MarkReadCoreAsync(db, tenantId, from, to, m.Id);
        try { Sent?.Invoke(tenantId, m); } catch { /* a listener's fault must not undo a sent message */ }
        return (m, "");
    }

    /// <summary>Whether this person is part of this message: to them, from them, or to Everyone.</summary>
    public static bool Involves(StaffMessage m, string me) => m.To.Length == 0 || m.To == me || m.From == me;

    /// <summary>The conversation this message belongs to, from this person's side: the other person, or "".</summary>
    public static string ThreadOf(StaffMessage m, string me) => m.To.Length == 0 ? "" : m.From == me ? m.To : m.From;

    /// <summary>
    /// An Everyone message that names this person with @ — "@Sue Tech", in any case, not followed by
    /// more of a name ("@Sue Technician" is not Sue Tech). Pops their window open; the rest of
    /// Everyone only counts as unread.
    /// </summary>
    public static bool Mentions(string text, string person) =>
        person.Length > 0 && Regex.IsMatch(text, "@" + Regex.Escape(person) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);

    /// <summary>
    /// One conversation, oldest first: with a person (both directions) or Everyone. The newest
    /// <paramref name="take"/>, or those before <paramref name="beforeId"/> for scrolling back.
    /// </summary>
    public async Task<List<StaffMessage>> HistoryAsync(int tenantId, string me, string? with, long? beforeId = null, int take = 100)
    {
        with = (with ?? "").Trim();
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.StaffMessages.Where(m => m.TenantId == tenantId);
        q = with.Length == 0
            ? q.Where(m => m.To == "")
            : q.Where(m => (m.From == me && m.To == with) || (m.From == with && m.To == me));
        if (beforeId.HasValue) q = q.Where(m => m.Id < beforeId.Value);
        var rows = await q.OrderByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        rows.Reverse();
        return rows;
    }

    /// <summary>Everything in this conversation up to <paramref name="upToId"/> has been seen.</summary>
    public async Task MarkReadAsync(int tenantId, string me, string? with, long upToId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (await MarkReadCoreAsync(db, tenantId, me, (with ?? "").Trim(), upToId))
            try { ReadChanged?.Invoke(tenantId, me); } catch { }
    }

    private static async Task<bool> MarkReadCoreAsync(PlatformDb db, int tenantId, string me, string with, long upToId)
    {
        var r = await db.StaffChatReads.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Person == me && x.With == with);
        if (r == null) db.StaffChatReads.Add(r = new StaffChatRead { TenantId = tenantId, Person = me, With = with });
        // Only ever forward: a window drawn a minute ago must not un-read what was read since.
        if (upToId <= r.LastReadId) return false;
        r.LastReadId = upToId;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Every conversation this person has, Everyone first and then the rest by their latest
    /// message — each with what is unread and the last thing said — plus everybody they could start
    /// one with. Unread is somebody ELSE's message after the last one they have seen.
    /// </summary>
    public async Task<StaffChatOverviewMsg> OverviewAsync(int tenantId, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var people = (await PeopleAsync(tenantId)).Where(p => p != me).ToList();
        var reads = await db.StaffChatReads.Where(r => r.TenantId == tenantId && r.Person == me)
            .ToDictionaryAsync(r => r.With, r => r.LastReadId);
        long ReadUpTo(string with) => reads.TryGetValue(with, out var id) ? id : 0;

        var threads = new List<StaffChatThreadInfo>();
        long everyoneRead = ReadUpTo("");
        var lastEveryone = await db.StaffMessages.Where(m => m.TenantId == tenantId && m.To == "")
            .OrderByDescending(m => m.Id).FirstOrDefaultAsync();
        threads.Add(new StaffChatThreadInfo
        {
            With = "",
            Unread = await db.StaffMessages.CountAsync(m => m.TenantId == tenantId && m.To == "" && m.From != me && m.Id > everyoneRead),
            Last = lastEveryone == null ? null : Info(lastEveryone)
        });

        // Direct messages to or from me, newest first, one pass: small companies, small tables.
        var mine = await db.StaffMessages.Where(m => m.TenantId == tenantId && m.To != "" && (m.To == me || m.From == me))
            .OrderByDescending(m => m.Id).Take(5000).ToListAsync();
        foreach (var g in mine.GroupBy(m => ThreadOf(m, me)))
        {
            long read = ReadUpTo(g.Key);
            threads.Add(new StaffChatThreadInfo
            {
                With = g.Key,
                Unread = g.Count(m => m.From == g.Key && m.Id > read),
                Last = Info(g.First())
            });
        }
        return new StaffChatOverviewMsg
        {
            People = people,
            Threads = threads,
            TotalUnread = threads.Sum(t => t.Unread)
        };
    }

    public static StaffChatInfo Info(StaffMessage m) =>
        new() { Id = m.Id, From = m.From, To = m.To, Text = m.Text, SentUtc = DateTime.SpecifyKind(m.SentUtc, DateTimeKind.Utc) };
}
