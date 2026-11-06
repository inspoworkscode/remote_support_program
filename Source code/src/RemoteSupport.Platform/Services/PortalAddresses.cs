using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Every portal address that names one thing, and the way back from the old ones (2026-10-23, HANDOVER §159).
///
/// <para>Database ids come from one sequence for every company, so <c>/portal/tickets/129</c> for a company's ticket #2
/// let the gap between two of its own tickets show the whole platform's volume (the outside review's F8). Every page now
/// names a ticket, a client, an article or a recurring task by its number WITHIN the company, and a stored file by a
/// random token on its row — in a shape of its own, because <c>/portal/tickets/2</c> already meant id 2:</para>
/// <code>
///   /portal/t/{n}   a ticket          /portal/p/{token}  a photo           /portal/kb?c={n}        one client's articles
///   /portal/c/{n}   a client          /portal/f/{token}  a comment's file  /portal/kb/edit?t={n}   written up from a ticket
///   /portal/a/{n}   an article        /portal/i/{token}  an article's picture   /portal/kb/edit?c={n}   new, for a client
///   /portal/a/{n}/edit                /portal/v/{token}  a recording       /portal/tickets?c={n}   one client's tickets
///   /portal/r/{n}   a recurring task
/// </code>
/// <para><b>Every lookup takes the company in the same query</b>, and a number or token of another company is the same
/// answer as one that does not exist. The old id addresses keep working, for that company's own people, by
/// <see cref="FromOldAsync"/> — one translator for the old pages, the old file routes and the console's hand-off.</para>
/// <para>Out of scope on purpose: the ids in <c>/api/portal/…</c> JSON and in the console's protocol (§159).</para>
/// </summary>
public static class PortalAddresses
{
    public static string Ticket(int number) => "/portal/t/" + number.ToString(CultureInfo.InvariantCulture);
    public static string Client(int number) => "/portal/c/" + number.ToString(CultureInfo.InvariantCulture);
    public static string Article(int number) => "/portal/a/" + number.ToString(CultureInfo.InvariantCulture);
    public static string ArticleEdit(int number) => Article(number) + "/edit";
    public static string Recurring(int number) => "/portal/r/" + number.ToString(CultureInfo.InvariantCulture);
    public static string Photo(string token) => "/portal/p/" + token;
    public static string Attachment(string token) => "/portal/f/" + token;
    public static string KbImage(string token) => "/portal/i/" + token;
    public static string Recording(string token) => "/portal/v/" + token;

    // ---- the one lookup per kind: (company, number) → id, 0 when it is not this company's ----

    public static Task<int> TicketIdAsync(PlatformDb db, int tenantId, int number) =>
        db.Tickets.Where(t => t.TenantId == tenantId && t.Number == number).Select(t => t.Id).FirstOrDefaultAsync();
    public static Task<int> ClientIdAsync(PlatformDb db, int tenantId, int number) =>
        db.Clients.Where(c => c.TenantId == tenantId && c.Number == number).Select(c => c.Id).FirstOrDefaultAsync();
    public static Task<int> ArticleIdAsync(PlatformDb db, int tenantId, int number) =>
        db.KbArticles.Where(a => a.TenantId == tenantId && a.Number == number).Select(a => a.Id).FirstOrDefaultAsync();
    public static Task<int> RecurringIdAsync(PlatformDb db, int tenantId, int number) =>
        db.RecurringTasks.Where(r => r.TenantId == tenantId && r.Number == number).Select(r => r.Id).FirstOrDefaultAsync();

    // ---- a stored file: (company, token) → id, 0 when it is not this company's ----

    public static async Task<long> PhotoIdAsync(PlatformDb db, int tenantId, string token) => !FileToken.Looks(token) ? 0
        : await db.TicketPhotos.Where(p => p.TenantId == tenantId && p.Token == token).Select(p => p.Id).FirstOrDefaultAsync();
    public static async Task<long> AttachmentIdAsync(PlatformDb db, int tenantId, string token) => !FileToken.Looks(token) ? 0
        : await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.Token == token).Select(a => a.Id).FirstOrDefaultAsync();
    public static async Task<long> RecordingIdAsync(PlatformDb db, int tenantId, string token) => !FileToken.Looks(token) ? 0
        : await db.SessionRecordings.Where(r => r.TenantId == tenantId && r.Token == token).Select(r => r.Id).FirstOrDefaultAsync();

    // ---- and back: (company, id) → number, 0 when it is not this company's ----

    public static Task<int> TicketNumberAsync(PlatformDb db, int tenantId, int id) =>
        db.Tickets.Where(t => t.TenantId == tenantId && t.Id == id).Select(t => t.Number).FirstOrDefaultAsync();
    public static Task<int> ClientNumberAsync(PlatformDb db, int tenantId, int id) =>
        db.Clients.Where(c => c.TenantId == tenantId && c.Id == id).Select(c => c.Number).FirstOrDefaultAsync();
    public static Task<int> ArticleNumberAsync(PlatformDb db, int tenantId, int id) =>
        db.KbArticles.Where(a => a.TenantId == tenantId && a.Id == id).Select(a => a.Number).FirstOrDefaultAsync();
    public static Task<int> RecurringNumberAsync(PlatformDb db, int tenantId, int id) =>
        db.RecurringTasks.Where(r => r.TenantId == tenantId && r.Id == id).Select(r => r.Number).FirstOrDefaultAsync();

    /// <summary>
    /// The client an address names by number, where 0 means "none" (the knowledge base's General section): 0 stays 0,
    /// and a number that is not one of this company's clients is −1 — never quietly General.
    /// </summary>
    public static async Task<int> ClientIdOrNoneAsync(PlatformDb db, int tenantId, int number) =>
        number == 0 ? 0 : await ClientIdAsync(db, tenantId, number) is var id and > 0 ? id : -1;

    /// <summary>The address of a client's articles, or General's (client 0).</summary>
    public static string KbFor(int clientNumber) => "/portal/kb?c=" + clientNumber.ToString(CultureInfo.InvariantCulture);

    // ---- the old addresses ----

    private static readonly Regex OldPath = new(
        @"^/portal/(?<kind>tickets|clients|kb/edit|kb/image|kb|recurring|photo|attachment|recordings)/(?<id>[0-9]{1,18})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The address an old one (a database id in it) means now, for this company — or null when it names nothing of this
    /// company's, which every caller answers exactly as it answers a number that does not exist. The query is kept,
    /// except where it named something by id itself (<c>?client=</c>, <c>?ticket=</c>), which is translated too. An
    /// address that was never an old shape comes back unchanged.
    /// </summary>
    public static async Task<string?> FromOldAsync(PlatformDb db, int tenantId, string pathAndQuery)
    {
        var at = pathAndQuery.IndexOf('?');
        var path = (at < 0 ? pathAndQuery : pathAndQuery[..at]).TrimEnd('/');
        var query = at < 0 ? "" : pathAndQuery[at..];

        var m = OldPath.Match(path);
        if (m.Success)
        {
            if (!long.TryParse(m.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
            int small = id <= int.MaxValue ? (int)id : 0;
            string? to = m.Groups["kind"].Value.ToLowerInvariant() switch
            {
                "tickets" => await TicketNumberAsync(db, tenantId, small) is > 0 and var n ? Ticket(n) : null,
                "clients" => await ClientNumberAsync(db, tenantId, small) is > 0 and var n ? Client(n) : null,
                "kb" => await ArticleNumberAsync(db, tenantId, small) is > 0 and var n ? Article(n) : null,
                "kb/edit" => await ArticleNumberAsync(db, tenantId, small) is > 0 and var n ? ArticleEdit(n) : null,
                "recurring" => await RecurringNumberAsync(db, tenantId, small) is > 0 and var n ? Recurring(n) : null,
                "photo" => await db.TicketPhotos.Where(p => p.TenantId == tenantId && p.Id == id).Select(p => p.Token).FirstOrDefaultAsync() is { Length: > 0 } t ? Photo(t) : null,
                "attachment" => await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.Id == id).Select(a => a.Token).FirstOrDefaultAsync() is { Length: > 0 } t ? Attachment(t) : null,
                "kb/image" => await db.KbImages.Where(i => i.TenantId == tenantId && i.Id == id).Select(i => i.Token).FirstOrDefaultAsync() is { Length: > 0 } t ? KbImage(t) : null,
                "recordings" => await db.SessionRecordings.Where(r => r.TenantId == tenantId && r.Id == id).Select(r => r.Token).FirstOrDefaultAsync() is { Length: > 0 } t ? Recording(t) : null,
                _ => null
            };
            if (to == null) return null;
            path = to;
        }
        var q = await QueryFromOldAsync(db, tenantId, query);
        return q == null ? null : path + q;
    }

    /// <summary>
    /// <c>?client={id}</c> becomes <c>?c={number}</c> (0 stays 0, the knowledge base's General) and <c>?ticket={id}</c>
    /// becomes <c>?t={number}</c>; anything else in the query is kept as it was. Null when an id is not this company's.
    /// </summary>
    public static async Task<string?> QueryFromOldAsync(PlatformDb db, int tenantId, string query)
    {
        if (query.Length <= 1) return query;
        var parts = new List<string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            if (key.Equals("client", StringComparison.OrdinalIgnoreCase) || key.Equals("ticket", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var oldId)) return null;
                bool client = key.Equals("client", StringComparison.OrdinalIgnoreCase);
                int n = client && oldId == 0 ? 0
                    : client ? await ClientNumberAsync(db, tenantId, oldId) : await TicketNumberAsync(db, tenantId, oldId);
                if (n == 0 && !(client && oldId == 0)) return null;
                parts.Add((client ? "c=" : "t=") + n.ToString(CultureInfo.InvariantCulture));
            }
            else parts.Add(part);
        }
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    /// <summary>Whether a query still names something by id the old way, so a page should send it on to the new one.</summary>
    public static bool HasOldQuery(Microsoft.AspNetCore.Http.IQueryCollection q) => q.ContainsKey("client") || q.ContainsKey("ticket");
}
