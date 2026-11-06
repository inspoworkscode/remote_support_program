using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Marks a portal page whose handlers are ticket WORK, refused to a login that does none — an owner-only login
/// (<see cref="Permissions.CanWorkTickets"/>, Clint 2026-10-15: it oversees and comments, it does not work tickets).
///
/// <para><b>An allow-list, on purpose.</b> Every handler on a marked page is refused unless it is named here: a
/// new button added to the ticket page next month is refused to an owner-only login until somebody decides it is
/// not ticket work, rather than open until somebody remembers. "Get" is the page itself (the plain GET); any
/// other name is a handler (<c>OnPostComment</c> → "Comment", <c>OnGetEml</c> → "Eml").</para>
///
/// <para>The services refuse the same people on their own (ActAsync, EditAsync, the go-back step, work done,
/// emailing the client) — this is the second lock, and the one that covers what takes no role at all
/// (opening a ticket, quote lines, linking).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TicketWorkAttribute : Attribute
{
    private readonly HashSet<string> _allowed;

    /// <param name="allowed">The handlers an owner-only login may still run on this page.</param>
    public TicketWorkAttribute(params string[] allowed) =>
        _allowed = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this handler on this page is refused to somebody with <paramref name="role"/>.</summary>
    public static bool Refuses(Type page, string httpMethod, string? handlerName, TenantRole role)
    {
        if (Permissions.CanWorkTickets(role)) return false;
        if (page.GetCustomAttributes(typeof(TicketWorkAttribute), false) is not [TicketWorkAttribute mark]) return false;
        var name = string.IsNullOrEmpty(handlerName) ? (httpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "Get" : "Post") : handlerName;
        return !mark._allowed.Contains(name);
    }
}
