using System.Collections.Concurrent;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Lets somebody already signed in to the console open the portal as themselves, without typing a
/// password a second time.
///
/// The console holds an authenticated websocket, so the server already knows exactly who is
/// asking. It mints a token here, the console opens a browser at a URL carrying it, and the
/// portal trades it for a sign-in cookie.
///
/// Three properties, and all three matter:
///
///   SINGLE USE. <see cref="Redeem"/> removes the token before it returns it. Two browsers racing
///   the same link means one of them signs in and the other is told to sign in normally.
///
///   SHORT LIVED. A minute. It exists to survive the gap between clicking a button and a browser
///   finishing its start-up, and nothing longer than that.
///
///   IN MEMORY ONLY, like <see cref="AuthService"/>'s console tokens. A restart invalidates every
///   outstanding link, which is the correct behaviour for something with a one-minute life.
///
/// The URL does land in the browser's history — every URL does. What is in it is already spent by
/// the time the page has rendered, which is what makes that acceptable and is the reason it is
/// single-use rather than merely short.
/// </summary>
public sealed class PortalHandoffService
{
    /// <summary>
    /// Long enough for a cold browser to start, short enough that a link copied out of somebody's
    /// shoulder is worthless before they have finished reading it.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private sealed record Pending(TechSession Session, string Path, DateTime ExpiresUtc);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    /// <summary>
    /// Where a hand-off may land. An allow-list rather than a sanitiser: this value arrives from
    /// the console and ends up in a redirect, and "starts with a slash" is not a rule that keeps
    /// out "//evil.example.com", which a browser reads as another host entirely.
    /// </summary>
    private static readonly string[] Allowed =
    {
        "/portal/tickets",
        "/portal/clients",
        "/portal",
    };

    /// <summary>
    /// Whole links, query included, that the console may ask for. The console's Tickets button opens
    /// "waiting on my reply" when somebody has @mentioned the technician (stage 3.7).
    /// </summary>
    private static readonly string[] AllowedExactly =
    {
        "/portal/tickets?filter=asked",
    };

    public const string DefaultPath = "/portal/tickets";

    /// <summary>The requested path if we know it, otherwise the tickets queue.</summary>
    public static string SafePath(string? path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0) return DefaultPath;
        // The few links that keep their query, compared WHOLE — never a prefix, never a pattern.
        foreach (var a in AllowedExactly)
            if (string.Equals(p, a, StringComparison.OrdinalIgnoreCase)) return a;
        // One ticket, from the console's ticket window ("Open in browser" — photos, Request quote).
        // Digits and nothing else after the slash, so it can only ever name a ticket number.
        // It may name one of two places on the ticket, both spelled out whole: the Request quote step
        // (the office's, which needs the mail-client choice the browser holds) and the photographs.
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/tickets/[0-9]{1,9}(\?open=quote-requested|\?tab=photos)?$")) return p;
        // The knowledge base (3.8.5), from the console's header, a ticket and a session window: the
        // whole of it, one client's articles, one article, a new one, or a ticket being written up.
        // Digits only wherever there is a number, spelled out whole like the ticket links above.
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/kb(\?client=[0-9]{1,9}|/[0-9]{1,9}|/edit(\?(ticket|client)=[0-9]{1,9})?)?$")) return p;
        // A recurring task (2026-10-07), from its ticket's "Change in the browser": the list, or one by number.
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/recurring(/[0-9]{1,9})?$")) return p;
        // A picture of the customer's screen from their support request (2026-10-09), from the ticket's "See their
        // screen". Digits only; the photo route itself asks whether this person may see that ticket.
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/photo/[0-9]{1,18}$")) return p;
        // The same places by number within the company, and stored files by their token (2026-10-23, HANDOVER §159) —
        // what an updated console sends. The old shapes above stay for good: a console not yet updated still sends
        // them, and /portal/enter translates them for the signed-in company. Spelled out whole, digits (or 24 hex
        // characters) only, like the rest. A comment's file was never on this list, so the console's file buttons
        // landed on the queue; both its shapes are here now.
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/t/[0-9]{1,9}(\?open=quote-requested|\?tab=photos)?$")) return p;
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/kb(\?c=[0-9]{1,9}|/edit\?(t|c)=[0-9]{1,9})$")) return p;
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/(a|r)/[0-9]{1,9}$")) return p;
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/(p|f)/[0-9a-f]{24}$")) return p;
        if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^/portal/attachment/[0-9]{1,18}$")) return p;
        // Compared whole, case-insensitively, with any query string dropped. A path that is not on
        // the list is replaced rather than refused: the technician asked to see the portal, and
        // landing on the queue is a better answer than an error page.
        var bare = p.Split('?')[0].TrimEnd('/');
        foreach (var a in Allowed)
            if (string.Equals(bare, a, StringComparison.OrdinalIgnoreCase)) return a;
        return DefaultPath;
    }

    /// <summary>Mints a one-minute, one-use token for somebody the console has already authenticated.</summary>
    public string Issue(TechSession session, string? path)
    {
        Sweep();
        var token = Passwords.RandomToken();
        _pending[token] = new Pending(session, SafePath(path), DateTime.UtcNow.Add(Lifetime));
        return token;
    }

    /// <summary>
    /// Trades a token for the session it stands for, and destroys it in the same breath.
    ///
    /// Removing BEFORE checking the expiry is deliberate: an expired token should not survive its
    /// own rejection and sit there to be tried again.
    /// </summary>
    public (TechSession? session, string path) Redeem(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, DefaultPath);
        if (!_pending.TryRemove(token, out var p)) return (null, DefaultPath);
        if (p.ExpiresUtc < DateTime.UtcNow) return (null, DefaultPath);
        return (p.Session, p.Path);
    }

    /// <summary>How many links are outstanding. For diagnostics and for the harness.</summary>
    public int Outstanding { get { Sweep(); return _pending.Count; } }

    private void Sweep()
    {
        if (_pending.IsEmpty) return;
        var now = DateTime.UtcNow;
        foreach (var kv in _pending)
            if (kv.Value.ExpiresUtc < now) _pending.TryRemove(kv.Key, out _);
    }
}
