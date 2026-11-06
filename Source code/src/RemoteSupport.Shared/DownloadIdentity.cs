using System.Text.RegularExpressions;

namespace RemoteSupport.Shared;

/// <summary>
/// Which company a downloaded copy of this app belongs to (2026-09-30: "it would be much better if
/// every download was an exe"). Every company's customers download the SAME signed file, because a
/// file that differed per company could not be signed (docs/CODE-SIGNING.md, "One .exe for every
/// company"), so the company has to be learned from something that is not inside it. In order:
///
/// <list type="number">
/// <item>The address it was downloaded from. A browser writes it beside the file, in the
/// Zone.Identifier stream — <c>HostUrl=https://…/download/anywhere-it</c> — which gives the company
/// AND the server, and survives the file being renamed.</item>
/// <item>Its file name: served as <c>anywhere-it-support.exe</c>; a browser's " (1)" for a second
/// copy is allowed for.</item>
/// <item>Neither: the person is asked for their IT company's code.</item>
/// </list>
///
/// Pure, so tools/oneexecheck can ask it directly.
/// </summary>
public static class DownloadIdentity
{
    /// <summary>A company's short name as it appears in its links: lower case, digits and hyphens.</summary>
    public static bool IsSlug(string? s) => !string.IsNullOrEmpty(s) && Regex.IsMatch(s, "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$");

    /// <summary>The name the download is served under. The slug is already safe in a file name.</summary>
    public static string FileNameFor(string slug) => $"{slug}-support.exe";

    /// <summary>"anywhere-it-support.exe", "anywhere-it-support (2).exe", any case → "anywhere-it".</summary>
    public static string? SlugFromFileName(string? path) => SlugFromFileName(path, "support");

    // ---- the technician console (2026-10-01: "the technician console still downloads as a zip,
    // that should be an exe as well"). The same rules with its own link and its own name, so a
    // console is never taken for a customer's download, or the other way round.

    /// <summary>The name the console is served under: anywhere-it-console.exe.</summary>
    public static string ConsoleFileNameFor(string slug) => $"{slug}-console.exe";

    /// <summary>"anywhere-it-console.exe", "anywhere-it-console (2).exe" → "anywhere-it".</summary>
    public static string? SlugFromConsoleFileName(string? path) => SlugFromFileName(path, "console");

    // ---- the GUEST console (2026-10-06, roadmap 3.9): the same .exe again, from /guest, for somebody with no
    // account. It belongs to no company, so it learns only which SERVER it is from, and that it is a guest.

    /// <summary>The name the guest console is served under.</summary>
    public const string GuestFileName = "remote-support-guest.exe";

    /// <summary>"remote-support-guest.exe", "remote-support-guest (2).exe", any case.</summary>
    public static bool IsGuestFileName(string? path) =>
        Regex.IsMatch(Path.GetFileName(path ?? ""), @"^remote-support-guest(?:\s*\(\d+\))?\.exe$", RegexOptions.IgnoreCase);

    /// <summary>The server a guest console was downloaded from — HostUrl ending /guest/console.exe — or null.</summary>
    public static string? GuestServerFromZoneIdentifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(line[8..].Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
            return Regex.IsMatch(u.AbsolutePath, @"^/guest/console\.exe$", RegexOptions.IgnoreCase) ? u.GetLeftPart(UriPartial.Authority) : null;
        }
        return null;
    }

    /// <summary>The console's own link, /downloads/{slug}/technician.exe, as <see cref="FromZoneIdentifier(string?)"/>.</summary>
    public static (string? server, string? slug) ConsoleFromZoneIdentifier(string? text) =>
        FromZoneIdentifier(text, @"^/downloads/(?<slug>[a-z0-9-]+)/technician\.exe$");

    private static string? SlugFromFileName(string? path, string suffix)
    {
        var name = Path.GetFileName(path ?? "");
        var m = Regex.Match(name, @"^(?<slug>[A-Za-z0-9][A-Za-z0-9-]*?)-" + suffix + @"(?:\s*\(\d+\))?\.exe$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var slug = m.Groups["slug"].Value.ToLowerInvariant();
        return IsSlug(slug) ? slug : null;
    }

    /// <summary>
    /// The Zone.Identifier text a browser writes: <c>[ZoneTransfer] ZoneId=3 ReferrerUrl=…
    /// HostUrl=…</c>. Returns the server (scheme, host and port) and the company, when HostUrl is one
    /// of our download links; nothing otherwise.
    /// </summary>
    public static (string? server, string? slug) FromZoneIdentifier(string? text) =>
        FromZoneIdentifier(text, "^/download/(?<slug>[a-z0-9-]+)/?$");

    private static (string? server, string? slug) FromZoneIdentifier(string? text, string pathPattern)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(line[8..].Trim(), UriKind.Absolute, out var u)) return (null, null);
            if (u.Scheme is not ("http" or "https")) return (null, null);
            var server = u.GetLeftPart(UriPartial.Authority);
            // Our download link and nothing else: a file that came from somewhere that is not ours is
            // not told it belongs to anybody.
            var m = Regex.Match(u.AbsolutePath, pathPattern, RegexOptions.IgnoreCase);
            if (!m.Success) return (null, null);
            var slug = m.Groups["slug"].Value.ToLowerInvariant();
            return IsSlug(slug) ? (server, slug) : (null, null);
        }
        return (null, null);
    }

    /// <summary>The Zone.Identifier beside a file, or empty. It is an NTFS stream; anywhere else there is none.</summary>
    public static string ReadZoneIdentifier(string path)
    {
        try { return File.ReadAllText(path + ":Zone.Identifier"); }
        catch { return ""; }
    }

    /// <summary>A server address in whatever form it was given, as the http(s) origin the app asks.</summary>
    public static string HttpOrigin(string server)
    {
        var s = (server ?? "").Trim().TrimEnd('/');
        if (s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s[6..];
        else if (s.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) s = "http://" + s[5..];
        return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : s;
    }
}
