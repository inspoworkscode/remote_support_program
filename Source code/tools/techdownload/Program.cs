using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// techdownload - the technicians' download link, /tech/{slug}.
//
// Clint, 2026-09-30: "There should be a technician download link, much like the client download
// link so that we dont have to run around with usb sticks every time." The console could already
// be downloaded, but only from inside the portal and only by a supervisor or the owner.
//
// Over HTTP with a real cookie, against a PLATFORM OF ITS OWN (own port, own database in the temp
// folder, two companies of its own, a stand-in console in its own payload folder). The live server
// on 8090 is not touched.
//
//   dotnet run -c Release --project tools/techdownload/techdownload.csproj
// ---------------------------------------------------------------------------------------------

internal static class TechDownload
{
    private static int _pass, _fail;
    private const string Slug = "techdl", Other = "othertechdl", Pw = "Harness-Pw-1";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-techdownload-" + DateTime.Now.ToString("HHmmss"));
        var payload = Path.Combine(dataDir, "techpayload");
        Directory.CreateDirectory(payload);
        // A stand-in console: the page and the route only care that there is something to zip.
        File.WriteAllText(Path.Combine(payload, "RemoteSupport.Technician.exe"), "stand-in console");
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server on 8090 and its database are not touched)");
        Console.WriteLine();

        foreach (var (slug, name) in new[] { (Slug, "Tech Download Co"), (Other, "Other Co") })
        {
            var created = await RunPlatform(proj, dataDir, port, payload, "create-tenant", name, slug);
            if (!created.Contains("Tenant key:", StringComparison.Ordinal)) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            await RunPlatform(proj, dataDir, port, payload, "approve", slug);
        }
        await RunPlatform(proj, dataDir, port, payload, "add-tech", Slug, "tech@testing.local", Pw, "Tess Tech");
        await RunPlatform(proj, dataDir, port, payload, "add-tech", Slug, "sup@testing.local", Pw, "Sam Supervisor", "--admin");
        await RunPlatform(proj, dataDir, port, payload, "add-tech", Slug, "office@testing.local", Pw, "Ossie Office", "--billing");
        await RunPlatform(proj, dataDir, port, payload, "add-tech", Other, "elsewhere@testing.local", Pw, "Ed Elsewhere");

        using var server = StartPlatform(proj, dataDir, port, payload);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var zipPath = $"/downloads/{Slug}/technician.zip";

            Console.WriteLine("== somebody with the link and no account ==");
            {
                var c = Client(port);
                var page = await c.GetStringAsync($"/tech/{Slug}");
                Check("the link opens a page for the company", page.Contains("Tech Download Co technician console", StringComparison.Ordinal));
                Check("...asking them to sign in", page.Contains("Sign in to download", StringComparison.Ordinal));
                Check("...with no download on it", !page.Contains(zipPath, StringComparison.Ordinal));
                var z = await c.GetAsync(zipPath);
                Check("the zip itself is refused without signing in", z.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)z.StatusCode}");
                var none = await c.GetStringAsync("/tech/no-such-company");
                Check("a company that does not exist has no page", none.Contains("Page not found", StringComparison.Ordinal));

                var wrong = await SignInOnPage(c, Slug, "tech@testing.local", "not-the-password");
                Check("a wrong password stays on the sign-in form, with a reason",
                      wrong.Contains("Sign in to download", StringComparison.Ordinal) && wrong.Contains("alert-bad", StringComparison.Ordinal));
                z = await c.GetAsync(zipPath);
                Check("...and still gets no zip", z.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)z.StatusCode}");
            }

            Console.WriteLine();
            Console.WriteLine("== a technician ==");
            {
                var c = Client(port);
                var page = await SignInOnPage(c, Slug, "tech@testing.local", Pw);
                Check("signing in on the page shows the download", page.Contains(zipPath, StringComparison.Ordinal)
                      && page.Contains("Download the technician console", StringComparison.Ordinal));
                Check("...and says who they are", page.Contains("signed in as Tess Tech", StringComparison.Ordinal));
                var z = await c.GetAsync(zipPath);
                var bytes = z.IsSuccessStatusCode ? await z.Content.ReadAsByteArrayAsync() : Array.Empty<byte>();
                Check("a technician gets the zip — the thing that was missing", z.IsSuccessStatusCode, $"HTTP {(int)z.StatusCode}, {bytes.Length} bytes");
                var names = new List<string>();
                string brand = "";
                try
                {
                    using var zip = new ZipArchive(new MemoryStream(bytes));
                    names = zip.Entries.Select(e => e.FullName).ToList();
                    var b = zip.GetEntry("branding/brand.json");
                    if (b != null) { using var r = new StreamReader(b.Open()); brand = r.ReadToEnd(); }
                }
                catch { }
                Check("...holding the console and THIS company's branding",
                      names.Contains("RemoteSupport.Technician.exe") && brand.Contains("Tech Download Co", StringComparison.Ordinal),
                      string.Join(", ", names));

                // Coming back to the link later: already signed in, straight to the button.
                var again = await c.GetStringAsync($"/tech/{Slug}");
                Check("coming back to the link goes straight to the download", again.Contains(zipPath, StringComparison.Ordinal)
                      && !again.Contains("Sign in to download", StringComparison.Ordinal));

                // "not you?" signs out and comes back to this page, not the home page.
                var token = Token(again);
                var r2 = await c.PostAsync("/logout", Form(("back", $"/tech/{Slug}"), ("__RequestVerificationToken", token)));
                var after = await r2.Content.ReadAsStringAsync();
                Check("\"not you?\" signs out back to the same page",
                      r2.RequestMessage?.RequestUri?.AbsolutePath == $"/tech/{Slug}" && after.Contains("Sign in to download", StringComparison.Ordinal),
                      r2.RequestMessage?.RequestUri?.AbsolutePath ?? "");
            }

            Console.WriteLine();
            Console.WriteLine("== everybody else ==");
            {
                var c = Client(port);
                await SignInOnPage(c, Slug, "sup@testing.local", Pw);
                var z = await c.GetAsync(zipPath);
                Check("a supervisor still gets the zip", z.IsSuccessStatusCode, $"HTTP {(int)z.StatusCode}");
                var downloads = await c.GetStringAsync("/portal/downloads");
                Check("the portal's Downloads page gives the technicians' link to copy",
                      downloads.Contains($"/tech/{Slug}</code>", StringComparison.Ordinal));
                Check("...and draws no stray Razor on the way", !downloads.Contains("else {", StringComparison.Ordinal));
            }
            {
                var c = Client(port);
                var page = await SignInOnPage(c, Slug, "office@testing.local", Pw);
                Check("an office administrator is told the console is not for them",
                      page.Contains("office administrator", StringComparison.Ordinal) && !page.Contains(zipPath, StringComparison.Ordinal));
                var z = await c.GetAsync(zipPath);
                Check("...and cannot fetch the zip directly", z.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)z.StatusCode}");
            }
            {
                var c = Client(port);
                var page = await SignInOnPage(c, Slug, "elsewhere@testing.local", Pw);
                Check("another company's technician is refused on this link",
                      page.Contains("belongs to a different company", StringComparison.Ordinal) && !page.Contains(zipPath, StringComparison.Ordinal));
                var z = await c.GetAsync(zipPath);
                Check("...and cannot fetch this company's zip", z.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)z.StatusCode}");
            }
            {
                // A technician signed in to their OWN company, trying another company's zip by URL.
                var c = Client(port);
                await SignInOnPage(c, Other, "elsewhere@testing.local", Pw);
                var own = await c.GetAsync($"/downloads/{Other}/technician.zip");
                var theirs = await c.GetAsync(zipPath);
                Check("a technician gets their own company's zip, never another's",
                      own.IsSuccessStatusCode && theirs.StatusCode == HttpStatusCode.Unauthorized,
                      $"own HTTP {(int)own.StatusCode}, other HTTP {(int)theirs.StatusCode}");
                var token = Token(await c.GetStringAsync($"/tech/{Other}"));
                var r = await c.PostAsync("/logout", Form(("back", "https://example.com/tech/x"), ("__RequestVerificationToken", token)));
                Check("sign-out never sends anybody off the site", r.RequestMessage?.RequestUri?.Host == "127.0.0.1",
                      r.RequestMessage?.RequestUri?.ToString() ?? "");
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

    private static HttpClient Client(int port) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    /// <summary>The page's own sign-in form, antiforgery token and all. Returns the page it lands on.</summary>
    private static async Task<string> SignInOnPage(HttpClient c, string slug, string email, string password)
    {
        var page = await c.GetStringAsync($"/tech/{slug}");
        var r = await c.PostAsync($"/tech/{slug}", Form(("Email", email), ("Password", password), ("__RequestVerificationToken", Token(page))));
        return await r.Content.ReadAsStringAsync();
    }

    private static FormUrlEncodedContent Form(params (string k, string v)[] f) =>
        new(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

    private static string Token(string page) =>
        Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";

    private static string? Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        var j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? null : s[i..j];
    }

    // ---- running a platform of our own (as devicelink does, plus a payload folder) ----------

    private static Process StartPlatform(string proj, string dataDir, int port, string payload)
    {
        var psi = Psi(proj, dataDir, port, payload);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> RunPlatform(string proj, string dataDir, int port, string payload, params string[] args)
    {
        var psi = Psi(proj, dataDir, port, payload);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private static ProcessStartInfo Psi(string proj, string dataDir, int port, string payload)
    {
        var psi = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        // Debug, as devicelink: the Release output is usually locked by the server Clint has running.
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
        psi.Environment["RSP_Platform__TechnicianPayloadDir"] = payload;
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
