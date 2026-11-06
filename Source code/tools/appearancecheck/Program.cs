using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// The console's light-or-dark switch, 2026-09-23.
//
// Until today a technician could only choose light or dark in the portal, and the console picked
// it up at sign-in. The console now has its own button, writing the SAME setting on the person
// through POST /api/appearance. Three things are tested, all against a real platform:
//
//   THE ENDPOINT. Saves on a real bearer token and on nothing else, and stores only "light",
//   "dark" or "" whatever is sent.
//
//   THE ROUND TRIP. What the button saves is what the next console sign-in is handed — which is
//   the only way the console ever changes palette.
//
//   THE HAND-OFF. The console's Tickets button signs the browser in with the session the console
//   signed in with, and that session carries the appearance AS IT WAS AT SIGN-IN. Before this,
//   flip the switch anywhere and then press Tickets, and the portal came up in the palette you had
//   just left. /portal/enter now reads it fresh; this proves it, both ways round.
//
// It starts a PLATFORM OF ITS OWN — its own port, its own empty database in the temp folder. The
// live server on 8090 and its database are not involved.
//
//   dotnet run -c Release --project tools/appearancecheck/appearancecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class AppearanceCheck
{
    private static int _pass, _fail;
    private const string Email = "mode@example.com", Password = "ModeProbe!2026";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }

        var dataDir = Path.Combine(Path.GetTempPath(), "rs-appearancecheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var baseUrl = $"http://127.0.0.1:{port}";

        Console.WriteLine("appearancecheck — the console's light-or-dark switch");
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server on 8090 and its database are not touched)");

        string slug = "mode-test-co";
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Mode Test Co");
            if (!created.Contains("Tenant key:")) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            var m = System.Text.RegularExpressions.Regex.Match(created, @"\(slug ([^,\)]+)");
            if (m.Success) slug = m.Groups[1].Value.Trim();
            var added = await RunPlatform(proj, dataDir, port, "add-tech", slug, Email, Password, "Mode Probe");
            if (!added.Contains("Added")) { Console.WriteLine("Could not add a technician:\n" + added); return 1; }
        }
        catch (Exception ex) { Console.WriteLine("Could not start the platform: " + ex.Message); return 1; }

        using var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            // ---- a technician who has never chosen -------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== a technician who has never chosen ==");
            var (token, first) = await Login(http, baseUrl);
            Check("the console signs in", token.Length > 0);
            Check("and is told to follow the company", first == "", $"appearance='{first}'");

            // ---- who may save it -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== who may save it ==");
            var none = await Save(http, baseUrl, null, "dark");
            Check("no token is refused", none.status == HttpStatusCode.Unauthorized, $"{(int)none.status}");
            var bogus = await Save(http, baseUrl, "not-a-real-token", "dark");
            Check("a token nobody issued is refused", bogus.status == HttpStatusCode.Unauthorized, $"{(int)bogus.status}");
            var (t2, stillFirst) = await Login(http, baseUrl); token = t2;   // each console sign-in closes the one before (one at a time, HANDOVER §82)
            Check("and neither changed anything", stillFirst == "", $"appearance='{stillFirst}'");

            // ---- the switch ------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== the console's switch ==");
            var dark = await Save(http, baseUrl, token, "dark");
            Check("saving dark is accepted", dark.status == HttpStatusCode.OK, $"{(int)dark.status}");
            Check("and the server says what it stored", dark.stored == "dark", $"'{dark.stored}'");
            var (t3, afterDark) = await Login(http, baseUrl); token = t3;
            Check("the NEXT console sign-in is handed dark", afterDark == "dark", $"appearance='{afterDark}'");

            var shout = await Save(http, baseUrl, token, "  LIGHT ");
            Check("case and spaces do not matter", shout.stored == "light", $"'{shout.stored}'");

            var junk = await Save(http, baseUrl, token, "purple");
            Check("anything else is stored as 'follow the company'", junk.stored == "", $"'{junk.stored}'");
            var (t4, afterJunk) = await Login(http, baseUrl); token = t4;
            Check("and signs in that way", afterJunk == "", $"appearance='{afterJunk}'");

            // ---- the hand-off ----------------------------------------------------------------
            //
            // The console's socket is opened with the LATEST token, whose session says "follow the
            // company". The switch is then flipped, and Tickets pressed. The portal has to come up
            // in what was just chosen, not in what the session remembers.
            Console.WriteLine();
            Console.WriteLine("== the Tickets button after a switch ==");
            await using var ws = await OpenConsoleSocket(port, token);
            Check("the console's socket opens", ws.conn != null);
            if (ws.conn != null)
            {
                await Save(http, baseUrl, token, "light");
                var light = await PortalThemeViaHandoff(ws, baseUrl);
                Check("switched to light, the portal opens light", light.theme == "light", light.detail);

                await Save(http, baseUrl, token, "dark");
                var darkAgain = await PortalThemeViaHandoff(ws, baseUrl);
                Check("switched to dark, the portal opens dark", darkAgain.theme == "dark", darkAgain.detail);
            }

            Console.WriteLine();
            Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }
    }

    private static async Task<(string token, string appearance)> Login(HttpClient http, string baseUrl)
    {
        var r = await http.PostAsJsonAsync($"{baseUrl}/api/login", new { email = Email, password = Password });
        if (!r.IsSuccessStatusCode) return ("", "(login failed)");
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return (doc.GetProperty("token").GetString() ?? "",
                doc.TryGetProperty("appearance", out var a) ? a.GetString() ?? "" : "(missing)");
    }

    private static async Task<(HttpStatusCode status, string stored)> Save(HttpClient http, string baseUrl, string? token, string theme)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/appearance")
        { Content = JsonContent.Create(new { theme }) };
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await http.SendAsync(req);
        if (!r.IsSuccessStatusCode) return (r.StatusCode, "");
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return (r.StatusCode, doc.GetProperty("appearance").GetString() ?? "");
    }

    private sealed class ConsoleSocket : IAsyncDisposable
    {
        public WsConnection? conn;
        public readonly List<PortalLinkMsg> links = new();
        public async ValueTask DisposeAsync() { if (conn != null) try { await conn.CloseAsync("done"); } catch { } }
    }

    private static async Task<ConsoleSocket> OpenConsoleSocket(int port, string token)
    {
        var s = new ConsoleSocket();
        try
        {
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={token}"), CancellationToken.None);
            s.conn = new WsConnection(cws);
            s.conn.OnMessage += m => { if (m is PortalLinkMsg p) lock (s.links) s.links.Add(p); };
            _ = s.conn.RunAsync(CancellationToken.None);
            await Task.Delay(500);
        }
        catch { s.conn = null; }
        return s;
    }

    /// <summary>
    /// Presses Tickets: asks for a hand-off link, spends it in a fresh browser, and reads which
    /// palette the page it lands on is painted in. The page's own heading is asserted too — a
    /// refused page redirects to the sign-in form, and that form has a data-theme of its own
    /// (HANDOVER §34: never trust a page you have not identified).
    /// </summary>
    private static async Task<(string theme, string detail)> PortalThemeViaHandoff(ConsoleSocket s, string baseUrl)
    {
        lock (s.links) s.links.Clear();
        s.conn!.Send(new PortalLinkRequestMsg { Path = "/portal/tickets" });
        PortalLinkMsg? link = null;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (link == null && DateTime.UtcNow < until)
        {
            lock (s.links) link = s.links.FirstOrDefault();
            if (link == null) await Task.Delay(50);
        }
        if (link == null) return ("", "no hand-off link arrived");
        if (link.Error.Length > 0) return ("", "the hub refused a link: " + link.Error);
        var rel = link.Url;
        if (!rel.Contains("/portal/enter")) return ("", $"the link was not a hand-off: '{rel}'");

        var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
        { Timeout = TimeSpan.FromSeconds(20) };
        var r = await browser.GetAsync(baseUrl + (rel.StartsWith('/') ? rel : "/" + rel));
        var html = await r.Content.ReadAsStringAsync();
        var landed = r.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
        var m = System.Text.RegularExpressions.Regex.Match(html, "<html[^>]*data-theme=\"([^\"]*)\"");
        var theme = m.Success ? m.Groups[1].Value : "(none)";
        if (landed.StartsWith("/login")) return ("", $"landed on the sign-in form, not the portal (data-theme='{theme}')");
        if (!html.Contains("Tickets", StringComparison.OrdinalIgnoreCase)) return ("", $"landed on {landed}, which is not the queue");
        return (theme, $"landed on {landed}, data-theme='{theme}'");
    }

    private static Process StartPlatform(string proj, string dataDir, int port)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> RunPlatform(string proj, string dataDir, int port, params string[] args)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    // Debug, deliberately, as in unattendedwire: the Release output is usually locked by the server
    // Clint has running.
    private static ProcessStartInfo Psi(string proj, string dataDir, int port)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
        psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        return psi;
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
