using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// consoleexecheck - the technician console as one downloadable .exe (2026-10-01).
//
// Clint: "the technician console still downloads as a zip, that should be an exe as well."
//
//   1. the rules: the console's file name and download address, never mistaken for the customers'
//   2. a REAL single-file console, published the way build.ps1 and ship-build.ps1 publish it (with
//      the server it falls back on embedded), served by a PLATFORM OF ITS OWN to a signed-in
//      technician as <company>-console.exe — and the zip again without the marker
//   3. that .exe, downloaded and marked the way a browser marks it, driven through --set-up with its
//      home and its shortcuts moved into the harness's own folder (never this PC's desktop): whose it
//      is and why, the branding fetched, itself copied beside it WITHOUT the downloaded mark, the
//      shortcuts pointing at it, an update over it, and the refusal while it is open
//   4. the copy it set up STARTS AS A CONSOLE: the sign-in window, in the company's name
//
//   dotnet run -c Debug --project tools/consoleexecheck/consoleexecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class ConsoleExeCheck
{
    private static int _pass, _fail;
    private const string Slug = "consoleexe", Pw = "Harness-Pw-1", Company = "Console Exe Co";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("consoleexecheck - the technician console as one .exe");
        Console.WriteLine();
        Rules();

        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var work = Path.Combine(Path.GetTempPath(), "rs-consoleexe-" + Guid.NewGuid().ToString("N")[..8]);
        var dataDir = Path.Combine(work, "data");
        var payload = Path.Combine(work, "techpayload");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(payload);
        int port = FreePort();
        var origin = $"http://127.0.0.1:{port}";
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Process? server = null;
        try
        {
            Console.WriteLine();
            Console.WriteLine($"== publishing a real console for {origin} (a minute or two) ==");
            var exe = await PublishConsole(root, work, origin);
            if (exe == null) { Console.WriteLine("Publishing the console failed."); return 1; }
            Check("the console publishes as one file, with the server embedded", File.Exists(exe), $"{new FileInfo(exe).Length / 1048576} MB");
            File.Copy(exe, Path.Combine(payload, "RemoteSupport.Technician.exe"));
            // As the build scripts leave it: the console, the build's branding (the zip's), and the marker.
            Directory.CreateDirectory(Path.Combine(payload, "branding"));
            File.WriteAllText(Path.Combine(payload, "branding", "brand.json"), "{\"companyName\":\"The build's own\"}");
            var marker = Path.Combine(payload, "console-exe.ready");
            File.WriteAllText(marker, "harness");

            var created = await RunPlatform(proj, dataDir, port, payload, "create-tenant", Company, Slug);
            if (!created.Contains("Tenant key:", StringComparison.Ordinal)) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            await RunPlatform(proj, dataDir, port, payload, "approve", Slug);
            // A logo, so the set-up has something to put on its shortcuts (2026-10-07).
            await RunPlatform(proj, dataDir, port, payload, "set-logo", Slug, Path.Combine(root, "branding", "Logo.jpg"));
            await RunPlatform(proj, dataDir, port, payload, "add-tech", Slug, "tech@testing.local", Pw, "Tess Tech");
            server = StartPlatform(proj, dataDir, port, payload);
            if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

            var downloaded = await Serving(port, exe, marker);
            if (downloaded != null) SettingUp(work, downloaded, origin);
        }
        catch (Exception ex) { _fail++; Console.WriteLine("  FAIL  threw: " + ex); }
        finally
        {
            try { if (server is { HasExited: false }) server.Kill(true); } catch { }
            await Task.Delay(500);
            try { Directory.Delete(work, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // =========================================================== 1. the rules
    private static void Rules()
    {
        Console.WriteLine("== the rules ==");
        Check("served as <slug>-console.exe", DownloadIdentity.ConsoleFileNameFor("anywhere-it") == "anywhere-it-console.exe");
        Check("\"anywhere-it-console.exe\" → anywhere-it", DownloadIdentity.SlugFromConsoleFileName(@"C:\Users\x\Downloads\anywhere-it-console.exe") == "anywhere-it");
        Check("a browser's second copy, \"Anywhere-IT-console (2).exe\" → anywhere-it", DownloadIdentity.SlugFromConsoleFileName("Anywhere-IT-console (2).exe") == "anywhere-it");
        Check("the customers' download is never taken for a console", DownloadIdentity.SlugFromConsoleFileName("anywhere-it-support.exe") == null);
        Check("...nor a console for the customers' download", DownloadIdentity.SlugFromFileName("anywhere-it-console.exe") == null);
        var zone = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://theremotesupportprogram.com/tech/anywhere-it\r\nHostUrl=https://theremotesupportprogram.com/downloads/anywhere-it/technician.exe\r\n";
        Check("the address a browser records gives the company and the server",
            DownloadIdentity.ConsoleFromZoneIdentifier(zone) == ("https://theremotesupportprogram.com", "anywhere-it"));
        Check("...and that address is not the customers'", DownloadIdentity.FromZoneIdentifier(zone) == (null, null));
        Check("the customers' address is not a console's",
            DownloadIdentity.ConsoleFromZoneIdentifier("HostUrl=https://theremotesupportprogram.com/download/anywhere-it") == (null, null));
        Check("the zip's address is not the .exe's",
            DownloadIdentity.ConsoleFromZoneIdentifier("HostUrl=https://theremotesupportprogram.com/downloads/anywhere-it/technician.zip") == (null, null));
    }

    // =========================================================== 2. serving it
    private static async Task<string?> Serving(int port, string exe, string marker)
    {
        Console.WriteLine();
        Console.WriteLine("== the platform serving it ==");
        var exePath = $"/downloads/{Slug}/technician.exe";
        var zipPath = $"/downloads/{Slug}/technician.zip";
        var anon = Client(port);
        var r = await anon.GetAsync(exePath);
        Check("the .exe is refused without signing in", r.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)r.StatusCode}");

        var c = Client(port);
        var page = await SignInOnPage(c, Slug, "tech@testing.local", Pw);
        Check("signed in, the page offers the .exe, not the zip", page.Contains(exePath, StringComparison.Ordinal) && !page.Contains(zipPath, StringComparison.Ordinal));
        Check("...and says it sets itself up, with no unzipping", page.Contains("sets itself up", StringComparison.Ordinal) && !page.Contains("Unzip", StringComparison.Ordinal));
        r = await c.GetAsync(exePath);
        var bytes = r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync() : Array.Empty<byte>();
        var name = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "";
        Check("a technician gets it, named for the company", r.IsSuccessStatusCode && name == $"{Slug}-console.exe", $"HTTP {(int)r.StatusCode}, \"{name}\"");
        Check("...byte for byte the published console — the same file for every company", bytes.AsSpan().SequenceEqual(File.ReadAllBytes(exe)), $"{bytes.Length} bytes");

        File.Move(marker, marker + ".off");
        page = await c.GetStringAsync($"/tech/{Slug}");
        r = await c.GetAsync(exePath);
        Check("an older console (no marker): the page offers the zip again, and no .exe is served",
            page.Contains(zipPath, StringComparison.Ordinal) && !page.Contains(exePath, StringComparison.Ordinal) && r.StatusCode == HttpStatusCode.NotFound, $"HTTP {(int)r.StatusCode}");
        File.Move(marker + ".off", marker);
        return bytes.Length > 0 ? Convert.ToBase64String(bytes) : null;
    }

    // =========================================================== 3. setting itself up
    private static void SettingUp(string work, string downloadedB64, string origin)
    {
        Console.WriteLine();
        Console.WriteLine("== the downloaded .exe setting itself up ==");
        var bytes = Convert.FromBase64String(downloadedB64);
        var downloads = Path.Combine(work, "Downloads");
        Directory.CreateDirectory(downloads);

        // As a browser leaves it: the name it was served under, and the address beside it.
        var dl = Path.Combine(downloads, $"{Slug}-console.exe");
        File.WriteAllBytes(dl, bytes);
        File.WriteAllText(dl + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl={origin}/tech/{Slug}\r\nHostUrl={origin}/downloads/{Slug}/technician.exe\r\n");

        var home = Path.Combine(work, "home");
        var links = Path.Combine(work, "shortcuts");
        var (code, res) = SetUp(dl, home, links);
        var installed = Path.Combine(home, Slug, "RemoteSupport.Technician.exe");
        Check("it knows whose it is, from the address it was downloaded from",
            code == 0 && Str(res, "slug") == Slug && Str(res, "how") == "the address it was downloaded from" && Str(res, "server") == origin, Json(res));
        Check("it put itself in its own folder, byte for byte", File.Exists(installed) && File.ReadAllBytes(installed).AsSpan().SequenceEqual(bytes));
        Check("...with THIS company's branding beside it — the server, the name",
            File.Exists(Path.Combine(home, Slug, "branding", "brand.json"))
            && Brand(home, "companyName") == Company && Brand(home, "serverUrl").Contains($"127.0.0.1:{origin.Split(':')[^1]}"), Brand(home, "serverUrl"));
        Check("...and WITHOUT the downloaded-from-the-internet mark, or Windows warns at every start",
            !File.Exists(installed + ":Zone.Identifier") && File.Exists(dl + ":Zone.Identifier"));
        var lnks = Directory.Exists(links) ? Directory.GetFiles(links, "*.lnk") : Array.Empty<string>();
        Check("a Start menu and a desktop shortcut, pointing at it",
            lnks.Length == 1 && string.Equals(LinkTarget(lnks[0]), installed, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(lnks[0]) == $"{Company} technician console.lnk",
            string.Join(", ", lnks.Select(l => $"{Path.GetFileName(l)} → {LinkTarget(l)}")));
        // The .exe is the same file for every company and has no icon of its own; a shortcut pointing
        // its icon at the .exe showed Windows' generic one — and a pinned taskbar button with it.
        var linkIcon = lnks.Length == 1 ? LinkIcon(lnks[0]) : "";
        var ico = Path.Combine(home, Slug, "console.ico");
        Check("...carrying the company's logo as its icon, not the .exe's blank one",
            linkIcon.StartsWith(ico, StringComparison.OrdinalIgnoreCase) && File.Exists(ico) && new FileInfo(ico).Length > 2000, linkIcon);

        // Opened again: the same file is nothing to do, and it still says yes.
        (code, res) = SetUp(dl, home, links);
        Check("opening the same download again is fine", code == 0, Json(res));

        // A second copy with no address recorded: its NAME, and the server it was built for.
        var named = Path.Combine(downloads, $"{Slug}-console (2).exe");
        File.WriteAllBytes(named, bytes);
        (code, res) = SetUp(named, Path.Combine(work, "home2"), links);
        Check("with no address recorded: from its name, and the server it was built for",
            code == 0 && Str(res, "how") == "its file name" && Str(res, "server") == origin && Str(res, "defaultServer") == origin, Json(res));

        // Renamed, no address — the last company used on this PC.
        var renamed = Path.Combine(downloads, "console.exe");
        File.WriteAllBytes(renamed, bytes);
        (code, res) = SetUp(renamed, home, links);
        Check("renamed, with nothing to go on: the last company used here", code == 0 && Str(res, "how") == "the last company used on this PC", Json(res));
        (code, res) = SetUp(renamed, Path.Combine(work, "home3"), links);
        Check("...and with no last company either, it refuses rather than guessing", code != 0 && Str(res, "exe") == "", Json(res));

        // The customers' address beside a console is not taken as a console's.
        var wrong = Path.Combine(downloads, "odd.exe");
        File.WriteAllBytes(wrong, bytes);
        File.WriteAllText(wrong + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={origin}/download/{Slug}\r\n");
        (code, res) = SetUp(wrong, Path.Combine(work, "home4"), links);
        Check("a file from the customers' link is never set up as a console", code != 0, Json(res));

        // Updating: an older console in place, held open as a running one holds it.
        File.WriteAllBytes(installed, new byte[] { 1, 2, 3 });
        using (var held = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            (code, res) = SetUp(dl, home, links);
            Check("while the old console is open, it says to close it first — and changes nothing",
                code != 0 && Str(res, "error").Contains("Exit", StringComparison.Ordinal) && new FileInfo(installed).Length == 3, Str(res, "error"));
        }
        (code, res) = SetUp(dl, home, links);
        Check("once it is closed, the download replaces it: that is updating",
            code == 0 && File.ReadAllBytes(installed).AsSpan().SequenceEqual(bytes), Json(res));

        // 4. The copy it set up is a console, not another set-up.
        StartsAsConsole(installed, links);
        DoubleClicked(dl, Path.Combine(work, "home5"), links);
    }

    /// <summary>What a technician does: open the download, no switches. It sets up, starts the copy it set up, and goes.</summary>
    private static void DoubleClicked(string dl, string home, string links)
    {
        var psi = new ProcessStartInfo(dl) { UseShellExecute = false };
        psi.Environment["RSP_CONSOLE_HOME"] = home;
        psi.Environment["RSP_CONSOLE_SHORTCUTS"] = links;
        using var p = Process.Start(psi)!;
        bool exited = p.WaitForExit(120000);
        var installed = Path.Combine(home, Slug, "RemoteSupport.Technician.exe");
        Check("opened like any download, it sets itself up and gets out of the way", exited && p.ExitCode == 0 && File.Exists(installed),
            exited ? $"exit {p.ExitCode}" : "still running");
        Process? started = null;
        string title = "";
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && title.Length == 0)
        {
            foreach (var q in Process.GetProcessesByName("RemoteSupport.Technician"))
            {
                string path = "";
                try { path = q.MainModule?.FileName ?? ""; } catch { }
                if (!string.Equals(path, installed, StringComparison.OrdinalIgnoreCase)) { q.Dispose(); continue; }
                started = q; q.Refresh(); title = q.MainWindowTitle;
                break;
            }
            if (title.Length == 0) Thread.Sleep(300);
        }
        Check("...and the console it started is the set-up copy, at its sign-in window", title == $"{Company} — Technician", $"\"{title}\"");
        try { started?.Kill(true); } catch { }
    }

    private static void StartsAsConsole(string installed, string links)
    {
        Console.WriteLine();
        Console.WriteLine("== the console it set up ==");
        var psi = new ProcessStartInfo(installed) { UseShellExecute = false };
        psi.Environment["RSP_CONSOLE_SHORTCUTS"] = links;
        using var p = Process.Start(psi)!;
        string title = "";
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !p.HasExited)
        {
            p.Refresh();
            title = p.MainWindowTitle;
            if (title.Length > 0) break;
            Thread.Sleep(300);
        }
        Check("started from where it was put, it opens the sign-in window in the company's name",
            !p.HasExited && title == $"{Company} — Technician", $"\"{title}\"");
        try { p.Kill(true); } catch { }
    }

    private static (int code, JsonElement res) SetUp(string exe, string home, string links)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "consoleexe-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--set-up"); psi.ArgumentList.Add(outFile);
        psi.Environment["RSP_CONSOLE_HOME"] = home;
        psi.Environment["RSP_CONSOLE_SHORTCUTS"] = links;
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return (-9, default); }
        JsonElement res = default;
        try { if (File.Exists(outFile)) res = JsonDocument.Parse(File.ReadAllText(outFile)).RootElement.Clone(); } catch { }
        try { File.Delete(outFile); } catch { }
        return (p.ExitCode, res);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string Json(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "(no answer)" : e.GetRawText();
    private static string Brand(string home, string name)
    {
        try { return Str(JsonDocument.Parse(File.ReadAllText(Path.Combine(home, Slug, "branding", "brand.json"))).RootElement, name); }
        catch { return ""; }
    }

    private static string LinkTarget(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            return (string)shell.CreateShortcut(lnk).TargetPath;
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    private static string LinkIcon(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            return (string)shell.CreateShortcut(lnk).IconLocation;
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    // ---- publishing a console the way the build scripts do ----------------------------------
    private static async Task<string?> PublishConsole(string root, string work, string server)
    {
        var defaults = Path.Combine(work, "console-defaults.json");
        File.WriteAllText(defaults, JsonSerializer.Serialize(new { server }));
        var outDir = Path.Combine(work, "published");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "publish", Path.Combine(root, "src", "RemoteSupport.Technician", "RemoteSupport.Technician.csproj"),
                     "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
                     "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none",
                     $"-p:ConsoleDefaults={defaults}", "-o", outDir, "--nologo", "-v", "quiet" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) { Console.WriteLine(outp + await err); return null; }
        var exe = Path.Combine(outDir, "RemoteSupport.Technician.exe");
        // A publish copies the project's branding\ beside it; the DOWNLOAD is the .exe alone.
        return File.Exists(exe) ? exe : null;
    }

    // ---- a platform of its own (as techdownload) ----------------------------------------------
    private static HttpClient Client(int port) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    private static async Task<string> SignInOnPage(HttpClient c, string slug, string email, string password)
    {
        var page = await c.GetStringAsync($"/tech/{slug}");
        var r = await c.PostAsync($"/tech/{slug}", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = Token(page) }));
        return await r.Content.ReadAsStringAsync();
    }

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

    private static Process StartPlatform(string proj, string dataDir, int port, string payload)
    {
        var psi = Psi(proj, dataDir, port, payload);
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
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
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        psi.Environment["RSP_Platform__TechnicianPayloadDir"] = payload;
        psi.Environment["RSP_Platform__PublicUrl"] = $"http://127.0.0.1:{port}";   // the branding names THIS platform
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
