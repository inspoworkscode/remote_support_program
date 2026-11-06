using Microsoft.Extensions.Configuration;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The chat bubble on OUR OWN website (2026-10-27). <c>Platform:SiteChatSlug</c> names the company whose people answer
/// it — Anywhere IT, the same chat as on its own site — but on theremotesupportprogram.com it must say The Remote
/// Support Program in our colour: a prospect there is talking to us, not to one of our customers. Clint: "say The Remote
/// Support Program, answered by the Anywhere I.T people as now."
/// Only the layout's own script tag asks for it (<c>?site=1</c>), and it is honoured only for that one company, so no
/// other company's bubble can wear our name. The chat itself is that company's in every other way: the same people
/// rung, the same hours, the same window, and a message out of hours becomes its ticket.
/// </summary>
public static class SiteChat
{
    public const string Name = "The Remote Support Program";
    /// <summary>The public site's own filled colour (site.css <c>--fill</c>), which carries white text.</summary>
    public const string Fill = "#6d28d9";

    /// <summary>Whose chat our site shows; null when unset (no bubble).</summary>
    public static string? Slug(IConfiguration cfg) => cfg["Platform:SiteChatSlug"] is { Length: > 0 } s ? s.Trim() : null;

    /// <summary>Is this request the bubble on our own site, for the company that answers it?</summary>
    public static bool Ours(IConfiguration cfg, string slug, string? site) =>
        site == "1" && Slug(cfg) is { } s && string.Equals(s, slug, StringComparison.OrdinalIgnoreCase);
}
