using Microsoft.AspNetCore.Http;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The Basic (once free) version's portal (2026-10-06, docs/NEXT-SESSION-FREE.md Stage F): My account, Downloads, its
/// Machines, Upgrade, and since 2026-10-25 Billing — and nothing else. ONE allow-list, asked by a middleware in front of every portal page
/// and portal API (Program.cs), so no page can forget the check and a page added next year is refused to a
/// free company until somebody decides otherwise. The menu (<c>_Layout</c>) draws the same list; hiding a
/// link is the convenience, this is the rule.
/// </summary>
public static class FreePortal
{
    /// <summary>Where a free company lands, and where anything refused sends them.</summary>
    public const string Home = "/portal/machines";

    private static readonly string[] Pages =
    {
        Home, "/portal/downloads", "/portal/account", "/portal/upgrade", "/portal/appearance", "/portal/enter",
        // The Basic version pays R50 a month by card since 2026-10-25: its card, payments, cancelling and the terms it accepted.
        "/portal/billing"
    };

    /// <summary>Whether a free company may open this address. Only /portal and /api/portal are asked about.</summary>
    public static bool Allowed(PathString path) =>
        Pages.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the free rule is asked about this address at all.</summary>
    public static bool Governs(PathString path) =>
        path.StartsWithSegments("/portal", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/portal", StringComparison.OrdinalIgnoreCase);
}
