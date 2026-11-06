using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// sitecheck — the public website against the website report (2026-10-14, docs/NEXT-SESSION-PAYFAST.md stage E).
//
// The report on Anywhere IT's own site asked for: zero code errors, under 3 seconds, several pages rather than one, menus that
// say what we do, and the SEO basics. This holds the real site to that, on a real platform:
//
//   VALID       every page parsed by an HTML5 parser that reports every parse error (the W3C checker's own rules for
//               tokenising and tree building) — and none may have one; no duplicate ids; every image has alt text.
//   FOUND       one h1; a title and a description of a sensible length, each different from every other page's; the
//               canonical address and Open Graph; html lang; a sitemap with every page; robots.txt saying "stay out"
//               until go-live and naming the sitemap after.
//   FAST        nothing loaded from another website; the page and its stylesheet small; answered quickly.
//   LINKED      every link on every page leads somewhere that answers; terms, refunds and privacy from the home page
//               (PayFast's list); the menu says what we do.
//   TRUE        none of what was never built or no longer true: the AI assistant, time-window access, made-up testimonials,
//               "VAT excluded", seven seats.
//   HIDDEN      every page of the site is behind the holding page in deploy/go-live.sh.
//
//   dotnet run -c Release --project tools/sitecheck/sitecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class SiteCheck
{
    private static int _pass, _fail;
    private static string _proj = "", _dataDir = "", _base = "", _root = "";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _root = FindRepoRoot() ?? "";
        if (_root.Length == 0) { Console.WriteLine("Could not find the repository root."); return 1; }
        _proj = Path.Combine(_root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        _dataDir = Path.Combine(Path.GetTempPath(), $"rs-sitecheck-{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(_dataDir);
        int port = FreePort();
        _base = $"http://127.0.0.1:{port}";
        Console.WriteLine("sitecheck — the public website");

        // The page list, from the source (this harness does not reference the platform).
        var sitePages = File.ReadAllText(Path.Combine(_root, "src", "RemoteSupport.Platform", "Services", "SitePages.cs"));
        var list = Regex.Match(sitePages, @"Public\s*=\s*\{([^}]*)\}").Groups[1].Value;
        var pages = Regex.Matches(list, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var all = pages.Append("/terms/ZA").ToList();
        Console.WriteLine($"{pages.Count} pages in SitePages.Public, and the terms");

        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var buildOut = await build.StandardOutput.ReadToEndAsync();
        await build.WaitForExitAsync();
        if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }

        // ---- hidden until go-live ----------------------------------------------------------------------------------------
        Head("hidden: every page behind the holding page");
        var golive = File.ReadAllText(Path.Combine(_root, "deploy", "go-live.sh"));
        var holding = Regex.Match(golive, @"@holding \{\s*path ([^\n]+)").Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var missing = pages.Where(p => !holding.Contains(p) || (p != "/" && !holding.Contains(p + "/"))).ToList();
        Check("deploy/go-live.sh's holding matcher covers every public page, with and without the slash", missing.Count == 0, missing.Count == 0 ? $"{pages.Count} pages" : "missing: " + string.Join(" ", missing));
        Check("…and still lets a preview browser through", golive.Contains("not header Cookie *rsp_preview=*"));

        var platform = StartPlatform(signupsOn: false);
        try
        {
            if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
            await c.GetAsync("/");   // warm

            var html = new Dictionary<string, string>();
            var times = new Dictionary<string, double>();
            foreach (var p in all)
            {
                var sw = Stopwatch.StartNew();
                using var r = await c.GetAsync(p);
                var body = await r.Content.ReadAsStringAsync();
                sw.Stop();
                if (r.StatusCode != HttpStatusCode.OK) { Check($"{p} answers", false, ((int)r.StatusCode).ToString()); continue; }
                html[p] = body; times[p] = sw.Elapsed.TotalMilliseconds;
            }
            Check("every page answers", html.Count == all.Count, $"{html.Count} of {all.Count}");

            // ---- valid ---------------------------------------------------------------------------------------------------
            Head("valid: no HTML errors");
            foreach (var (p, body) in html)
            {
                var errors = new List<string>();
                var parser = new HtmlParser(new HtmlParserOptions { IsStrictMode = false });
                parser.Error += (_, e) => { if (e is AngleSharp.Html.Dom.Events.HtmlErrorEvent he) errors.Add($"line {he.Position.Line}: {he.Message}"); };
                var doc = parser.ParseDocument(body);
                var ids = doc.All.Where(e => e.Id is { Length: > 0 }).GroupBy(e => e.Id!).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                var noAlt = doc.QuerySelectorAll("img").Count(i => !i.HasAttribute("alt"));
                Check($"{p}: no parse errors, no duplicate ids, alt on every image",
                    errors.Count == 0 && ids.Count == 0 && noAlt == 0,
                    errors.Count + ids.Count + noAlt == 0 ? "" : string.Join("; ", errors.Take(3).Concat(ids.Select(i => "duplicate id " + i)).Append(noAlt > 0 ? $"{noAlt} images without alt" : "").Where(s => s.Length > 0)));
            }

            // ---- found ---------------------------------------------------------------------------------------------------
            Head("found: what a search engine reads");
            var titles = new Dictionary<string, string>();
            var descs = new Dictionary<string, string>();
            foreach (var (p, body) in html)
            {
                var doc = new HtmlParser().ParseDocument(body);
                var title = doc.Title ?? "";
                var desc = doc.QuerySelector("meta[name=description]")?.GetAttribute("content") ?? "";
                var canonical = doc.QuerySelector("link[rel=canonical]")?.GetAttribute("href") ?? "";
                titles[p] = title; descs[p] = desc;
                var h1 = doc.QuerySelectorAll("h1").Length;
                bool og = doc.QuerySelector("meta[property='og:title']") != null && doc.QuerySelector("meta[property='og:description']") != null;
                Check($"{p}: one h1, a title (≤ 70, as a search result shows it), a description (70–160), canonical {(_base + p)}, Open Graph, lang",
                    h1 == 1 && title.Length is > 10 and <= 70 && desc.Length is >= 70 and <= 160 && canonical == _base + p && og && doc.DocumentElement.GetAttribute("lang") == "en",
                    $"h1 {h1}, title {title.Length}, description {desc.Length}{(canonical == _base + p ? "" : ", canonical " + canonical)}");
            }
            Check("every title is different", titles.Values.Distinct().Count() == titles.Count);
            Check("every description is different", descs.Values.Distinct().Count() == descs.Count);
            var home = new HtmlParser().ParseDocument(html.GetValueOrDefault("/", ""));
            var ld = home.QuerySelector("script[type='application/ld+json']")?.TextContent ?? "";
            bool ldOk;
            try { using var j = System.Text.Json.JsonDocument.Parse(ld); ldOk = ld.Contains("\"Organization\"") && ld.Contains("\"SoftwareApplication\"") && ld.Contains("\"ZAR\""); }
            catch { ldOk = false; }
            Check("the home page says who we are and what we sell, for search engines (schema.org JSON that parses)", ldOk);
            var robots = await (await c.GetAsync("/robots.txt")).Content.ReadAsStringAsync();
            Check("robots.txt: stay out, until go-live", robots.Contains("Disallow: /\n") && !robots.Contains("Sitemap:"), robots.Replace("\n", " | "));
            var sitemap = await (await c.GetAsync("/sitemap.xml")).Content.ReadAsStringAsync();
            Check("sitemap.xml lists every page", pages.All(p => sitemap.Contains($"<loc>{_base}{p}</loc>")) && sitemap.Contains("/terms/ZA"));

            // ---- fast ----------------------------------------------------------------------------------------------------
            Head("fast");
            var css = await c.GetByteArrayAsync("/site.css");
            foreach (var (p, body) in html)
            {
                var doc = new HtmlParser().ParseDocument(body);
                var external = doc.QuerySelectorAll("script[src], link[href], img[src], iframe[src]")
                    .Select(e => e.GetAttribute("src") ?? e.GetAttribute("href") ?? "")
                    .Where(u => u.StartsWith("http") || u.StartsWith("//")).Where(u => !u.StartsWith(_base)).ToList();
                var bytes = Encoding.UTF8.GetByteCount(body) + css.Length;
                Check($"{p}: nothing from another website, page and stylesheet {bytes / 1024} KB (≤ 150), answered in {times[p]:0} ms (≤ 500)",
                    external.Count == 0 && bytes <= 150 * 1024 && times[p] <= 500, external.Count > 0 ? string.Join(" ", external) : "");
            }

            // ---- seen: the screenshots and the light/dark switch (2026-10-14) ----------------------------------------------
            Head("seen: screenshots in light and dark, and the switch");
            var shotSrc = Regex.Matches(sitePages, @"new\(""([\w-]+)"", ""(png|jpg)"", (\d+), (\d+),")
                .Select(m => (name: m.Groups[1].Value, ext: m.Groups[2].Value, w: int.Parse(m.Groups[3].Value), h: int.Parse(m.Groups[4].Value))).ToList();
            var shotDir = Path.Combine(_root, "src", "RemoteSupport.Platform", "wwwroot", "shots");
            foreach (var s in shotSrc)
                foreach (var theme in new[] { "light", "dark" })
                {
                    var file = Path.Combine(shotDir, $"{s.name}-{theme}.{s.ext}");
                    var size = File.Exists(file) ? ImageSize(File.ReadAllBytes(file)) : (0, 0);
                    long kb = File.Exists(file) ? new FileInfo(file).Length / 1024 : 0;
                    using var r = await c.GetAsync($"/shots/{s.name}-{theme}.{s.ext}");
                    Check($"/shots/{s.name}-{theme}.{s.ext}: there, {s.w}×{s.h} as SitePages.Shots says, ≤ 250 KB, served as a picture",
                        size == (s.w, s.h) && kb is > 0 and <= 250 && r.StatusCode == HttpStatusCode.OK && (r.Content.Headers.ContentType?.MediaType ?? "").StartsWith("image/"),
                        $"{size.Item1}×{size.Item2}, {kb} KB, {(int)r.StatusCode} {r.Content.Headers.ContentType?.MediaType}");
                }
            var used = new HashSet<string>();
            foreach (var (p, body) in html)
            {
                var doc = new HtmlParser().ParseDocument(body);
                var imgs = doc.QuerySelectorAll("img[src^='/shots/']").ToList();
                foreach (var i in imgs) used.Add(Regex.Replace(i.GetAttribute("src")!, @"^/shots/([\w-]+)-(light|dark)\.\w+$", "$1"));
                // Each picture as a pair inside its link, dark then light, both sized (no jump as it loads) and lazy (the hidden one never loads).
                var frames = doc.QuerySelectorAll("a[data-shot]").ToList();
                bool paired = frames.All(a =>
                {
                    var two = a.QuerySelectorAll("img").ToList();
                    return two.Count == 2 && two[0].ClassName == "shot-dark" && two[1].ClassName == "shot-light"
                        && two[0].GetAttribute("src")!.Replace("-dark.", "-light.") == two[1].GetAttribute("src")
                        && two.All(i => i.HasAttribute("width") && i.HasAttribute("height") && i.GetAttribute("loading") == "lazy" && (i.GetAttribute("alt") ?? "").Length > 20);
                });
                if (imgs.Count > 0)
                    Check($"{p}: {frames.Count} screenshots, each a dark and a light picture, sized, lazy, described", paired && imgs.Count == frames.Count * 2);
                // The switch, and the page's light or dark chosen BEFORE the stylesheet, so it is painted right the first time.
                bool switchOk = doc.QuerySelector("button#siteTheme.theme-toggle") != null;
                int pick = body.IndexOf("localStorage.getItem('rsp-theme')", StringComparison.Ordinal), sheet = body.IndexOf("/site.css", StringComparison.Ordinal);
                Check($"{p}: the light/dark switch, and the page's choice made before the stylesheet loads", switchOk && pick > 0 && pick < sheet);
            }
            var unused = shotSrc.Select(s => s.name).Where(n => !used.Contains(n)).ToList();
            Check("every screenshot in SitePages.Shots is on a page, and the home page has the tour", unused.Count == 0 && html.GetValueOrDefault("/", "").Contains("id=\"see-it\""),
                unused.Count == 0 ? $"{shotSrc.Count} screenshots" : "unused: " + string.Join(", ", unused));
            var cssText = Encoding.UTF8.GetString(css);
            Check("the stylesheet shows only the picture of the page's own light or dark",
                cssText.Contains(":root:not([data-theme=\"light\"]) .shot-light") && cssText.Contains(":root[data-theme=\"light\"] .shot-dark"));

            // ---- linked --------------------------------------------------------------------------------------------------
            Head("linked");
            var links = new HashSet<string>();
            foreach (var body in html.Values)
                foreach (var a in new HtmlParser().ParseDocument(body).QuerySelectorAll("a[href]"))
                {
                    var h = a.GetAttribute("href")!;
                    if (h.StartsWith("/") && !h.StartsWith("//")) links.Add(h.Split('#')[0].Length == 0 ? "/" : h.Split('#')[0]);
                }
            var broken = new List<string>();
            foreach (var l in links)
            {
                using var r = await c.GetAsync(l);
                if ((int)r.StatusCode >= 400) broken.Add($"{l} {(int)r.StatusCode}");
            }
            Check($"every link on every page leads somewhere ({links.Count} addresses)", broken.Count == 0, string.Join(", ", broken));
            var homeLinks = home.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")).ToList();
            Check("from the home page: terms and refunds, privacy (PayFast's list)", homeLinks.Contains("/terms/ZA") && homeLinks.Contains("/privacy"));
            var menu = home.QuerySelectorAll("#topnav a").Select(a => a.TextContent.Trim()).ToList();
            Check("the menu says what we do", menu.Contains("Remote support") && menu.Contains("Job cards") && menu.Contains("Pricing") && !menu.Contains("Home"), string.Join(" · ", menu));

            // ---- true ----------------------------------------------------------------------------------------------------
            Head("true: nothing never built, nothing no longer so");
            var text = string.Join("\n", html.Values.Select(b => WebUtility.HtmlDecode(Regex.Replace(b, "<[^>]+>", " "))));
            foreach (var (bad, why) in new[]
                     {
                         ("AI assistant", "never built, and a POPIA problem (Clint, 2026-10-13)"), ("Time-window", "never built"),
                         ("Illustrative testimonials", "made-up quotes"), ("Managed service provider, Cape Town", "a made-up quote"),
                         ("VAT excluded", "no VAT is charged"), ("seven seats", "five since 2026-10-08"), ("Coming soon", "everything listed is built"), ("up to ten", "no fixed ten since 2026-10-22"), ("no extra seats", "more on request since 2026-10-25"), ("and no more", "more on request since 2026-10-25"),
                         ("No credit card required", "the card is taken at signup"),
                     })
                Check($"no \"{bad}\" — {why}", !text.Contains(bad, StringComparison.OrdinalIgnoreCase));
            var pricing = WebUtility.HtmlDecode(html.GetValueOrDefault("/pricing", ""));
            Check("pricing: R500 for five, more on request at R300, made for small businesses, the Basic version, card, no VAT, 14 days",
                pricing.Contains("R500") && pricing.Contains("Five people; more on request") && pricing.Contains("id=\"moreOnRequest\"") && pricing.Contains("R300</span> a month") && pricing.Contains("affordable for small businesses") && pricing.Contains("id=\"free\"") && pricing.Contains("No VAT is charged") && pricing.Contains("nothing is charged for 14 days"));
            // The Basic version (2026-10-25): no longer free — R50 a month, three computers — and a page for remote workers.
            Check("pricing: the Basic Version for Remote Workers at R50 a month, 3 computers and more at R10 up to 20, attended or unattended, linking its page",
                pricing.Contains("The Basic Version for Remote Workers") && pricing.Contains("R50 <small>/ month") && pricing.Contains("3 computers included, and more at <span id=\"extraMachine\">R10</span> a month each — up to 20")
                && pricing.Contains("or attended — the person at the computer allows you in") && pricing.Contains("href=\"/remote-workers\""));
            // The country switch changes EVERY figure (2026-10-27, Clint: "they all say R50 for the basic version" — only the full
            // version's price had moved). Each region's figures are written into the page from Pricing.cs; the switch fills these ids.
            Check("pricing: the country switch has each region's own Basic, extra-computer and extra-seat price, and fills them in",
                pricing.Contains("{\"pkg\":\"R500\",\"basic\":\"R50\",\"machine\":\"R10\",\"seat\":\"R300\"}")
                && pricing.Contains("{\"pkg\":\"$49\",\"basic\":\"$5\",\"machine\":\"$1\",\"seat\":\"$20\"}")
                && pricing.Contains("{\"pkg\":\"£49\",\"basic\":\"£5\",\"machine\":\"£1\",\"seat\":\"£20\"}")
                && pricing.Contains("{\"pkg\":\"€49\",\"basic\":\"€5\",\"machine\":\"€1\",\"seat\":\"€20\"}")
                && pricing.Contains("id=\"basicPrice\"") && pricing.Contains("id=\"extraSeat\"")
                && pricing.Contains("getElementById('basicPrice')") && pricing.Contains("getElementById('extraMachine')") && pricing.Contains("getElementById('extraSeat')"));
            // The Free version is back beside it (2026-10-25 evening, §163): one computer, nothing to pay — named as "the Free Version".
            Check("pricing: the Free Version — one computer, unattended, nothing to pay", pricing.Contains("id=\"freeVersion\"") && pricing.Contains("The Free Version")
                && pricing.Contains("nothing to pay") && pricing.Contains("<strong>one</strong> computer"));
            Check("the Free version is always called by its name — never a lower-case \"the free version\" left from before (a question as people type it, \"Is there a free version?\", is fine)",
                !Regex.IsMatch(text, "\\bthe free version\\b") && text.Contains("Free Version"));
            var rw = WebUtility.HtmlDecode(html.GetValueOrDefault("/remote-workers", ""));
            Check("/remote-workers: working from home and the office computer in its heading, keywords, R50, the FAQ for search engines, the trial",
                rw.Contains("<h1>Work from home on your office computer</h1>") && Regex.IsMatch(rw, "<meta name=\"keywords\" content=\"[^\"]*work from home[^\"]*access office computer from home")
                && rw.Contains("R50 a month") && rw.Contains("\"FAQPage\"") && rw.Contains("/signup?region=ZA&plan=basic"));
            Check("/remote-workers: attended too, and the Free version for one computer — in the page and in the FAQ for search engines",
                rw.Contains("Attended too") && rw.Contains("Is there a free version?") && rw.Contains("\"name\": \"Is there a free version?\"") && rw.Contains("/signup?region=ZA&plan=free"));
        }
        finally { try { platform.Kill(true); } catch { } }

        // ---- after go-live --------------------------------------------------------------------------------------------------
        Head("after go-live: search engines welcome");
        port = FreePort(); _base = $"http://127.0.0.1:{port}";
        platform = StartPlatform(signupsOn: true);
        try
        {
            if (!await WaitForPort(port, 90000)) { Check("the platform started", false); return Done(); }
            using var c = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
            var robots = await (await c.GetAsync("/robots.txt")).Content.ReadAsStringAsync();
            Check("robots.txt keeps the portal, admin and API out, and names the sitemap",
                !robots.Contains("Disallow: /\n") && robots.Contains("Disallow: /portal") && robots.Contains("Disallow: /owner") && robots.Contains($"Sitemap: {_base}/sitemap.xml"), robots.Replace("\n", " | "));
            var home = await (await c.GetAsync("/")).Content.ReadAsStringAsync();
            Check("the home page offers the trial", home.Contains("href=\"/signup\""));
        }
        finally { try { platform.Kill(true); } catch { } }
        return Done();
    }

    /// <summary>A PNG's or JPEG's width and height, from its own header; (0, 0) for anything else.</summary>
    private static (int, int) ImageSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == (byte)'P')
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length && b[i] == 0xFF)
            {
                byte marker = b[i + 1];
                int len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + len;
            }
        }
        return (0, 0);
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"sitecheck: {_pass} of {_pass + _fail} passed");
        return _fail == 0 ? 0 : 1;
    }

    private static Process StartPlatform(bool signupsOn)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Platform__PublicUrl"] = _base;
        psi.Environment["RSP_Platform__SignupsEnabled"] = signupsOn ? "true" : "false";
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<bool> WaitForPort(int port, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
            catch { await Task.Delay(400); }
        }
        return false;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
