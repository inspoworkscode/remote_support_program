namespace RemoteSupport.Platform.Services;

/// <summary>
/// The public website's pages (2026-10-14, stage E — the website report): ONE list, for the sitemap, for robots.txt, and for
/// <c>tools/sitecheck</c>, which also proves every one of them is behind the holding page in <c>deploy/go-live.sh</c> until
/// go-live — a page missing there would be public on the live server the moment it is deployed.
/// </summary>
public static class SitePages
{
    /// <summary>The pages a search engine should find, home first.</summary>
    public static readonly string[] Public =
    {
        "/", "/remote-support", "/ticketing", "/health-checks", "/knowledge-base", "/website-chat", "/security",
        "/features", "/pricing", "/remote-workers", "/about", "/support", "/privacy",
    };

    /// <summary>
    /// The screenshots (2026-10-14, Clint: "add some screenshots so that people can get a good look and feel"). Each is in
    /// <c>wwwroot/shots</c> twice, <c>{Name}-light</c> and <c>{Name}-dark</c>, made from the real windows by
    /// <c>tools/siteshots</c> with a made-up company and made-up customers; the page shows the one that matches its light
    /// or dark. The sizes are the files' own, so a picture keeps its place while it loads — remake the pictures and
    /// these change with them (<c>tools/sitecheck</c> compares).
    /// </summary>
    public static class Shots
    {
        public static readonly SiteShot Console = new("console", "png", 1860, 1060,
            "The technician's console: customers' computers listed under each customer, online or not, with chat beside them");
        public static readonly SiteShot Session = new("session", "jpg", 1720, 1060,
            "A remote session: the customer's screen in the console, with the toolbar above and who is connected below");
        public static readonly SiteShot Tickets = new("tickets", "png", 1860, 1080,
            "A workshop ticket: the queue on the left, and the job's story step by step on the right with the next steps as buttons");
        public static readonly SiteShot Performance = new("performance", "png", 860, 660,
            "A computer's processor, memory, disk and network, live, without connecting to it");
        public static readonly SiteShot Request = new("request", "png", 520, 742,
            "The customer's Request support window: their name, what is wrong, and a picture of their screen");
        public static readonly SiteShot Allow = new("allow", "png", 540, 500,
            "The prompt on the customer's screen asking them to allow the connection, with the IT company's name and logo");
        public static readonly SiteShot CustomerApp = new("customer-app", "png", 438, 604,
            "The customer's support app with the IT company's name, a Request Support button and chat");

        public static readonly SiteShot[] All = { Console, Session, Tickets, Performance, Request, Allow, CustomerApp };
    }

    /// <summary>The site is open to the world: signups on (go-live). Until then robots.txt asks search engines to stay out.</summary>
    public static bool Open(IConfiguration cfg) => !bool.TryParse(cfg["Platform:SignupsEnabled"], out var on) || on;

    public static string Robots(IConfiguration cfg)
    {
        var url = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        if (!Open(cfg)) return "# Not open yet.\nUser-agent: *\nDisallow: /\n";
        return "User-agent: *\nDisallow: /portal\nDisallow: /owner\nDisallow: /api/\nDisallow: /signup/paid\nDisallow: /help/\nDisallow: /guest\n"
             + $"\nSitemap: {url}/sitemap.xml\n";
    }

    /// <summary>
    /// /.well-known/security.txt (RFC 9116): who to tell. Expires is a year from the moment it is asked, so it is never
    /// out of date, and the address is the one the site already shows.
    /// </summary>
    public static string SecurityTxt(IConfiguration cfg, DateTime? now = null)
    {
        var url = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        var expires = (now ?? DateTime.UtcNow).Date.AddYears(1);
        return "Contact: mailto:queries@theremotesupportprogram.com\n"
             + $"Expires: {expires:yyyy-MM-dd}T00:00:00Z\n"
             + "Preferred-Languages: en\n"
             + (url.Length > 0 ? $"Canonical: {url}/.well-known/security.txt\n" : "");
    }

    public static string Sitemap(IConfiguration cfg)
    {
        var url = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        var b = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        foreach (var p in Public)
            b.Append("  <url><loc>").Append(System.Security.SecurityElement.Escape(url + p)).Append("</loc></url>\n");
        b.Append("  <url><loc>").Append(System.Security.SecurityElement.Escape(url + "/terms/ZA")).Append("</loc></url>\n");
        return b.Append("</urlset>\n").ToString();
    }
}

/// <summary>One screenshot on the public site, in both of its files. See <see cref="SitePages.Shots"/>.</summary>
public sealed record SiteShot(string Name, string Ext, int Width, int Height, string Alt)
{
    public string File(string theme) => $"/shots/{Name}-{theme}.{Ext}";
}
