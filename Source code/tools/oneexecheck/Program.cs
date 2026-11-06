// =====================================================================================
// oneexecheck - proves the one downloadable .exe (2026-09-30).
//
//   "It would be much better if every download was an exe. it would be way more professional."
//
// Every company's customers download the SAME file, because a file that differed per company could
// not be signed; it works out whose it is from the download's address or its name, fetches that
// company's branding from the server, and offers "get help now" or "install".
//
//   1. the rules, in process: every way a file name and a browser's Zone.Identifier can look
//   2. a REAL packed .exe: tools\Pack-ClientExe.ps1 run on a copy of the download folder, pointed
//      at a platform of this harness's own; the platform serves it named for the company, and its
//      branding; the download page offers it
//   3. that .exe, downloaded and marked the way a browser marks it, driven through its hidden
//      switches: it knows whose it is and why, fetches the branding into a cache of the harness's
//      own (never this PC's), carries the host, agent and scripts BYTE FOR BYTE, and stages the
//      folder Install runs Install-Client.ps1 from — which is NOT run: it would install on this PC
//   4. the launcher window, drawn: both choices, their buttons fit, and a copy that cannot install says so
//
//   dotnet run -c Debug --project tools/oneexecheck/oneexecheck.csproj [picture folder]
// =====================================================================================

using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Client;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.UI;
using RemoteSupport.Tools;

internal static class OneExeCheck
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);
        Console.WriteLine("oneexecheck - the one downloadable .exe");
        Console.WriteLine();
        Rules();
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var work = Path.Combine(Path.GetTempPath(), "rs-oneexe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        Process? server = null;
        try
        {
            server = Wire(root, work).GetAwaiter().GetResult();
            Window(outDir);
        }
        catch (Exception ex) { _fail++; Console.WriteLine("  FAIL  threw: " + ex); }
        finally
        {
            try { if (server is { HasExited: false }) server.Kill(true); } catch { }
            Thread.Sleep(500);
            try { Directory.Delete(work, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // =========================================================== 1. the rules
    private static void Rules()
    {
        Console.WriteLine("== whose copy is it: the file name ==");
        Check("served as <slug>-support.exe", DownloadIdentity.FileNameFor("anywhere-it") == "anywhere-it-support.exe");
        Check("\"anywhere-it-support.exe\" → anywhere-it", DownloadIdentity.SlugFromFileName(@"C:\Users\x\Downloads\anywhere-it-support.exe") == "anywhere-it");
        Check("a browser's second copy, \"anywhere-it-support (2).exe\" → anywhere-it", DownloadIdentity.SlugFromFileName("anywhere-it-support (2).exe") == "anywhere-it");
        Check("in capitals → anywhere-it", DownloadIdentity.SlugFromFileName("Anywhere-IT-Support.EXE") == "anywhere-it");
        Check("\"setup.exe\" says nothing", DownloadIdentity.SlugFromFileName("setup.exe") == null);
        Check("\"RemoteSupport.Client.exe\" says nothing", DownloadIdentity.SlugFromFileName("RemoteSupport.Client.exe") == null);
        Check("\"-support.exe\" alone says nothing", DownloadIdentity.SlugFromFileName("-support.exe") == null);

        Console.WriteLine();
        Console.WriteLine("== whose copy is it: where it was downloaded from ==");
        var edge = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://theremotesupportprogram.com/get/anywhere-it\r\nHostUrl=https://theremotesupportprogram.com/download/anywhere-it\r\n";
        Check("Edge/Chrome's Zone.Identifier → the server and the company",
            DownloadIdentity.FromZoneIdentifier(edge) == ("https://theremotesupportprogram.com", "anywhere-it"));
        Check("a LAN server with a port keeps its port",
            DownloadIdentity.FromZoneIdentifier("[ZoneTransfer]\nZoneId=3\nHostUrl=http://192.168.1.115:8090/download/anywhere-it/\n") == ("http://192.168.1.115:8090", "anywhere-it"));
        Check("a file from somewhere else is told nothing",
            DownloadIdentity.FromZoneIdentifier("[ZoneTransfer]\nZoneId=3\nHostUrl=https://example.com/files/app.exe\n") == (null, null));
        Check("no HostUrl (copied from a USB stick, or an old browser) says nothing", DownloadIdentity.FromZoneIdentifier("[ZoneTransfer]\nZoneId=3\n") == (null, null));
        Check("nothing at all says nothing", DownloadIdentity.FromZoneIdentifier("") == (null, null));
        Check("a server given as ws:// is asked over http://", DownloadIdentity.HttpOrigin("ws://192.168.1.115:8090") == "http://192.168.1.115:8090");
        Check("...and wss:// over https://", DownloadIdentity.HttpOrigin("wss://theremotesupportprogram.com/") == "https://theremotesupportprogram.com");
    }

    // =========================================================== 2 and 3. a real packed .exe
    private static async Task<Process?> Wire(string root, string work)
    {
        var live = Path.Combine(root, @"src\RemoteSupport.Platform\bin\Release\net10.0\payloads\client");
        if (!File.Exists(Path.Combine(live, "RemoteSupport.Host.exe")))
        { Console.WriteLine($"\n  (no staged download folder at {live} — ship once first; skipping the packed .exe)"); return null; }

        var payload = Path.Combine(work, "payload");
        CopyDir(live, payload);
        File.Delete(Path.Combine(payload, PayloadService.PackedMarker));
        var port = FreePort();
        var origin = $"http://127.0.0.1:{port}";

        Console.WriteLine();
        Console.WriteLine($"== packing a real .exe for a platform of its own on port {port} (a minute or two) ==");
        var pack = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "tools", "Pack-ClientExe.ps1")}\" -PayloadDir \"{payload}\" -Server {origin}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var packOut = await pack.StandardOutput.ReadToEndAsync();
        await pack.WaitForExitAsync();
        var exe = Path.Combine(payload, "RemoteSupport.Client.exe");
        Check("Pack-ClientExe.ps1 packs it, and leaves the marker that says so",
            pack.ExitCode == 0 && File.Exists(Path.Combine(payload, PayloadService.PackedMarker)),
            packOut.Split('\n').LastOrDefault(l => l.Contains("MB"))?.Trim() ?? packOut);
        var mb = new FileInfo(exe).Length / 1024.0 / 1024.0;
        var zipMb = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("RemoteSupport.Client.exe")).Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0 + 70;
        Check("one .exe no bigger than the zip it replaces", mb < zipMb, $"{mb:N0} MB against about {zipMb:N0} MB");

        // ---- the platform ----
        var dataDir = Path.Combine(work, "data");
        Directory.CreateDirectory(dataDir);
        var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
        await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
        var factory = new Factory(options);
        var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("oneexecheck"));
        var (acme, _) = await tenants.CreateAsync("Acme IT", "acme-it", 7);
        var (pend, _) = await tenants.CreateAsync("Pending IT", "pending-it", 7);
        await tenants.UpdateBrandingAsync(acme!.Id, t => { t.Status = TenantStatus.Active; t.ProductName = "Acme Remote Support"; });
        await tenants.UpdateBrandingAsync(pend!.Id, t => t.Status = TenantStatus.Pending);

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
            psi.ArgumentList.Add(a);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
        psi.Environment["RSP_Platform__ClientPayloadDir"] = payload;
        psi.Environment["RSP_Platform__PublicUrl"] = origin;
        psi.Environment["RSP_Urls"] = origin;
        psi.Environment["ASPNETCORE_URLS"] = origin;
        var server = Process.Start(psi)!;
        server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
        server.BeginOutputReadLine(); server.BeginErrorReadLine();
        if (!await WaitForPort(port, 90000)) { Check("the platform starts", false); return server; }

        Console.WriteLine();
        Console.WriteLine("== the platform serves it ==");
        using var http = new HttpClient { BaseAddress = new Uri(origin) };
        var res = await http.GetAsync("/download/acme-it");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Check("/download/acme-it is the .exe, named acme-it-support.exe",
            res.IsSuccessStatusCode && res.Content.Headers.ContentDisposition?.FileNameStar is "acme-it-support.exe" or null
            && (res.Content.Headers.ContentDisposition?.FileName?.Trim('"') == "acme-it-support.exe" || res.Content.Headers.ContentDisposition?.FileNameStar == "acme-it-support.exe"),
            res.Content.Headers.ContentDisposition?.ToString() ?? $"{(int)res.StatusCode}");
        Check("...the SAME file every company gets, byte for byte", bytes.Length > 0 && SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(exe))));
        var brandJson = await http.GetStringAsync("/download/acme-it/brand.json");
        var brand = JsonSerializer.Deserialize<Branding>(brandJson, Json.Options)!;
        Check("/download/acme-it/brand.json is the company's branding: name, server, key",
            brand.CompanyName == "Acme IT" && brand.TenantKey.Length > 10 && brand.ServerUrl.Contains($"127.0.0.1:{port}"), $"{brand.CompanyName} / {brand.ServerUrl}");
        Check("an unknown company: 404", (await http.GetAsync("/download/nobody")).StatusCode == HttpStatusCode.NotFound);
        Check("a company not yet approved: 404", (await http.GetAsync("/download/pending-it")).StatusCode == HttpStatusCode.NotFound
            && (await http.GetAsync("/download/pending-it/brand.json")).StatusCode == HttpStatusCode.NotFound);
        Check("anything else under it: 404", (await http.GetAsync("/download/acme-it/other.txt")).StatusCode == HttpStatusCode.NotFound);
        var get = await http.GetStringAsync("/get/acme-it");
        Check("the download page's button is the .exe, with how to use it", get.Contains("href=\"/download/acme-it\"") && get.Contains("Get help now"));
        Check("...the zip only as a small link for IT staff, and no unzip instructions", get.Contains("The zip for IT staff") && !get.Contains("Unzip the file"));
        var marker = Path.Combine(payload, PayloadService.PackedMarker);
        File.Move(marker, marker + ".off");
        var getOld = await http.GetStringAsync("/get/acme-it");
        Check("without a packed .exe the page offers the zip as before, and /download refuses",
            getOld.Contains("/downloads/acme-it/client.zip") && !getOld.Contains("href=\"/download/acme-it\"")
            && (await http.GetAsync("/download/acme-it")).StatusCode == HttpStatusCode.NotFound);
        File.Move(marker + ".off", marker);

        // ---- 3. the downloaded .exe itself ----
        Console.WriteLine();
        Console.WriteLine("== the downloaded .exe works out whose it is ==");
        var dl = Path.Combine(work, "Downloads");
        Directory.CreateDirectory(dl);
        var got = Path.Combine(dl, "acme-it-support.exe");
        await File.WriteAllBytesAsync(got, bytes);
        // What Edge and Chrome write beside a download.
        File.WriteAllText(got + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl={origin}/get/acme-it\r\nHostUrl={origin}/download/acme-it\r\n");
        var cache1 = Path.Combine(work, "cache1");
        var (code, id) = Identify(got, cache1);
        Check("from the address the browser recorded: Acme IT, this server",
            code == 0 && id?.GetProperty("slug").GetString() == "acme-it" && id?.GetProperty("server").GetString() == origin
            && id?.GetProperty("how").GetString() == "the address it was downloaded from", id?.ToString() ?? "");
        Check("...and fetched its branding: the name, the product, the key",
            id?.GetProperty("company").GetString() == "Acme IT" && id?.GetProperty("product").GetString() == "Acme Remote Support" && id?.GetProperty("hasTenantKey").GetBoolean() == true);
        Check("...into the cache it was given, not this PC's own", File.Exists(Path.Combine(cache1, "acme-it", "branding", "brand.json")));
        Check("it carries its payload, and knows the server it was built for",
            id?.GetProperty("payload").GetBoolean() == true && id?.GetProperty("defaultServer").GetString() == origin);

        var renamed = Path.Combine(dl, "acme-it-support (2).exe");
        // Written fresh: File.Copy on Windows DOES carry the Zone.Identifier stream (found by this
        // harness's first run), and this copy must have no recorded address.
        await File.WriteAllBytesAsync(renamed, bytes);
        var (code2, id2) = Identify(renamed, Path.Combine(work, "cache2"));
        Check("a second copy with no recorded address: from its NAME, and the server it was built for",
            code2 == 0 && id2?.GetProperty("slug").GetString() == "acme-it" && id2?.GetProperty("how").GetString() == "its file name", id2?.ToString() ?? "");

        var anon = Path.Combine(dl, "setup.exe");
        await File.WriteAllBytesAsync(anon, bytes);
        var (code3, _) = Identify(anon, Path.Combine(work, "cache3"));
        Check("renamed to setup.exe with nothing to go on, and nobody to ask: it refuses rather than guessing", code3 != 0);
        var (code4, id4) = Identify(anon, cache1);
        Check("...but on a PC where it was used before, it remembers the company", code4 == 0 && id4?.GetProperty("slug").GetString() == "acme-it"
            && id4?.GetProperty("how").GetString() == "the last company used on this PC");

        Console.WriteLine();
        Console.WriteLine("== what it carries ==");
        var unpacked = Path.Combine(work, "unpacked");
        Check("--unpack writes it out", Run(got, "--unpack", unpacked, cache1) == 0);
        bool Same(string rel) => File.Exists(Path.Combine(unpacked, rel))
            && SHA256.HashData(File.ReadAllBytes(Path.Combine(unpacked, rel))).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(payload, rel))));
        Check("the host, byte for byte — so its signature is the one it was given", Same("RemoteSupport.Host.exe"));
        Check("the agent service, byte for byte", Same("RemoteSupport.Agent.exe"));
        Check("Install-Client.ps1 and Uninstall-Client.ps1", Same("Install-Client.ps1") && Same("Uninstall-Client.ps1"));
        var hc = Directory.EnumerateFiles(Path.Combine(payload, "healthcheck")).Select(Path.GetFileName).ToList();
        Check("the health check toolkit", hc.Count > 0 && hc.All(f => Same(Path.Combine("healthcheck", f!))), $"{hc.Count} scripts");
        Check("NOT another copy of itself, and NOT anybody's branding",
            !File.Exists(Path.Combine(unpacked, "RemoteSupport.Client.exe")) && !Directory.Exists(Path.Combine(unpacked, "branding")));

        Console.WriteLine();
        Console.WriteLine("== the folder Install runs from ==");
        var staged = Path.Combine(work, "staged");
        Check("--stage-install builds it", Run(got, "--stage-install", staged, cache1) == 0);
        Check("...shaped like the old unzipped download: the app, the host, the agent, the scripts",
            new[] { "RemoteSupport.Client.exe", "RemoteSupport.Host.exe", "RemoteSupport.Agent.exe", "Install-Client.ps1" }.All(f => File.Exists(Path.Combine(staged, f))));
        Check("...the app in it is THIS .exe, byte for byte", SHA256.HashData(File.ReadAllBytes(Path.Combine(staged, "RemoteSupport.Client.exe"))).SequenceEqual(SHA256.HashData(bytes)));
        var stagedBrand = Branding.LoadFrom(staged);
        Check("...with this company's branding\\brand.json, key included — what Install-Client.ps1 reads",
            stagedBrand is { CompanyName: "Acme IT" } && stagedBrand.TenantKey == brand.TenantKey);
        Check("an INSTALLED copy (branding beside it) is not a downloaded one", !Launcher.IsDownloadedCopy || !File.Exists(Path.Combine(AppContext.BaseDirectory, "branding", "brand.json")));
        return server;
    }

    private static (int code, JsonElement? id) Identify(string exe, string cache)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "oneexe-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var code = Run(exe, "--identify", outFile, cache);
        JsonElement? id = null;
        try { if (File.Exists(outFile)) id = JsonDocument.Parse(File.ReadAllText(outFile)).RootElement.Clone(); } catch { }
        try { File.Delete(outFile); } catch { }
        return (code, id);
    }

    private static int Run(string exe, string sw, string target, string cache)
    {
        var p = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        p.ArgumentList.Add(sw); p.ArgumentList.Add(target);
        p.Environment["RSP_DOWNLOAD_CACHE"] = cache;
        using var proc = Process.Start(p)!;
        if (!proc.WaitForExit(120000)) { try { proc.Kill(); } catch { } return -9; }
        return proc.ExitCode;
    }

    // =========================================================== 4. the window
    private static void Window(string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("== the window a customer sees ==");
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var brand = new Branding { CompanyName = "Anywhere IT", ProductName = "Anywhere IT Remote Support", AccentColor = "#29A8E0", PrimaryColor = "#0E0E12", Logo = "" };
        foreach (var (canInstall, installed, name) in new[] { (true, false, "fresh"), (true, true, "installed"), (false, false, "cannot-install") })
        {
            // An Update starts with who it was installed for (2026-10-23).
            var before = installed ? GivenPerson.Make("Jane", "Smith", "jane@acme.co.za", null) : null;
            using var f = new LauncherForm(brand, "anywhere-it", canInstall, installed, before) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var buttons = All(f).OfType<RoundButton>().ToList();
            var run = buttons.FirstOrDefault(b => b.Text == "Get help now");
            var ins = buttons.FirstOrDefault(b => b.Text is "Install" or "Update");
            if (name == "fresh")
            {
                Check("two choices: Get help now, and Install", run != null && ins?.Text == "Install" && ins.Enabled);
                Check("...both buttons' words fit", buttons.All(b => TextRenderer.MeasureText(b.Text, b.Font).Width < b.Width - 8));
                Check("...on one line at the bottom of two cards of one height",
                    run != null && ins != null && run.Parent!.Height == ins.Parent!.Height && run.Top == ins.Top);
                Check("...and nothing runs off the window", All(f).Where(c => c.Visible && c.Parent != null).All(c => c.Right <= c.Parent!.ClientSize.Width + 1));
                // The tick box's second line once sat under the Install button, and every other check passed.
                var overlaps = f.Controls.Cast<Control>().SelectMany(All).Where(c => c is Card)
                    .SelectMany(card => card.Controls.Cast<Control>().SelectMany((a, i) => card.Controls.Cast<Control>().Skip(i + 1)
                        .Where(b => a.Bounds.IntersectsWith(b.Bounds)).Select(b => $"{a.Text} / {b.Text}"))).ToList();
                Check("...nothing in a card overlaps anything else", overlaps.Count == 0, string.Join("; ", overlaps));
                var tick = All(f).OfType<CheckBox>().SingleOrDefault();
                Check("unattended is a choice, unticked", tick is { Checked: false, Enabled: true });
                // Its words wrapped at its width must fit its height: an AutoSize tick box cut them at "…when I".
                Check("...and all its words show",
                    tick != null && TextRenderer.MeasureText(tick.Text, tick.Font, new Size(tick.Width - SystemInformation.MenuCheckSize.Width - 8, 0), TextFormatFlags.WordBreak).Height <= tick.Height
                    && tick.Right <= tick.Parent!.ClientSize.Width, $"{tick?.Width} × {tick?.Height}");
            }
            var boxes = All(f).OfType<InputBox>().ToList();
            if (name == "fresh")
            {
                // Who the computer is for (2026-10-23): three boxes in the Install card, empty, each with its label, all fitting.
                Check("Install asks who it is for: name, surname and email, empty", boxes.Count == 3 && boxes.All(x => x.Value == "")
                    && new[] { "Name", "Surname", "Email address" }.All(w => All(f).OfType<Label>().Any(l => l.Text == w)), $"{boxes.Count} boxes");
                Check("...in the Install card, and every box inside it", boxes.All(x => x.Parent == ins?.Parent && x.Right <= x.Parent!.ClientSize.Width - 8)
                    && boxes.All(x => x.Bottom <= ins!.Top - 4), string.Join(", ", boxes.Select(x => $"{x.Left}-{x.Right}/{x.Bottom}")));
                Check("...saying why in one line", All(f).OfType<Label>().Any(l => l.Text.StartsWith("Who is this computer for? So Anywhere IT knows who you are")));
            }
            else if (name == "installed")
            {
                Check("already installed: it offers to Update", ins?.Text == "Update" && All(f).OfType<Label>().Any(l => l.Text.Contains("already on this PC")));
                Check("...with who it was installed for filled in", boxes.Select(x => x.Value).SequenceEqual(new[] { "Jane", "Smith", "jane@acme.co.za" }),
                    string.Join(" | ", boxes.Select(x => x.Value)));
            }
            else Check("a copy that cannot install says so, and the button is off", ins is { Enabled: false } && All(f).OfType<Label>().Any(l => l.Text.Contains("cannot install")));
            try
            {
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                var path = Path.Combine(outDir, $"launcher-{name}.png");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"        picture: {path}");
            }
            catch { }
            f.Close();
        }
        Closing(brand);
    }

    // =========================================================== 5. closing it (2026-10-01)
    // Clint: "The exe installer does not close when you click on close or when you click on the X."
    // A good install returned with _busy still set, and OnFormClosing refused every close after it.
    // The installer is a stand-in here — the real one would install on this PC.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void Closing(Branding brand)
    {
        Console.WriteLine();
        Console.WriteLine("== closing the window ==");
        void Pump(int n = 10) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(20); } }
        // The X exactly as Windows sends it, not Form.Close().
        void X(Form f) { SendMessage(f.Handle, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF060 /* SC_CLOSE */, IntPtr.Zero); Pump(); }
        (LauncherForm f, Func<bool> closed, Func<int> calls, RoundButton ins) Open(Func<Task<int>> install, bool fill = true)
        {
            var f = new LauncherForm(brand, "anywhere-it", true, false) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            bool shut = false; int n = 0;
            f.FormClosed += (_, _) => shut = true;
            f.Installer = _ => { n++; return install(); };
            f.Show(); Pump();
            if (fill) Fill(f, "Jane", "Smith", "jane@acme.co.za");
            return (f, () => shut, () => n, All(f).OfType<RoundButton>().First(b => b.Text == "Install"));
        }
        void Fill(LauncherForm f, string first, string surname, string email)
        {
            var boxes = All(f).OfType<InputBox>().ToList();
            boxes[0].Value = first; boxes[1].Value = surname; boxes[2].Value = email;
        }
        string Status(LauncherForm f) => All(f).OfType<Label>().LastOrDefault(l => l.Parent is Panel && l.Parent is not Card && l.Text.Length > 0)?.Text ?? "";

        // Who it is for, required (2026-10-23): checked before anything is staged or Windows asks.
        var (r, _, rCalls, rIns) = Open(() => Task.FromResult(0), fill: false);
        rIns.PerformClick(); Pump();
        Check("Install with the boxes empty: refused, nothing run, and it says what is missing", rCalls() == 0 && Status(r) == "Type your name." && rIns.Text == "Install",
            $"{rCalls()} runs, '{Status(r)}'");
        Fill(r, "Jane", "Smith", "jane at acme");
        rIns.PerformClick(); Pump();
        Check("...an email that is not one: refused, in words", rCalls() == 0 && Status(r) == "That email address does not look right.", Status(r));
        Fill(r, "  Jane ", " Smith", " jane@acme.co.za ");
        rIns.PerformClick(); Pump();
        Check("...all three: it installs, with who it is for kept as one name and the email", rCalls() == 1 && r.Person is { Name: "Jane Smith", Email: "jane@acme.co.za" },
            r.Person == null ? "no person" : $"{r.Person.Name} / {r.Person.Email}");
        r.Dispose();

        var (a, aClosed, _, _) = Open(() => Task.FromResult(0));
        X(a);
        Check("before anything is chosen, the X closes it", aClosed());
        a.Dispose();

        var (b, bClosed, bCalls, bIns) = Open(() => Task.FromResult(0));
        bIns.PerformClick(); Pump();
        Check("after a good install the button reads Close", bIns.Text == "Close" && bIns.Enabled && b.Result == LauncherForm.Outcome.Installed, bIns.Text);
        bIns.PerformClick(); Pump();
        Check("...and pressing it closes the window", bClosed());
        Check("...without running the installer a second time", bCalls() == 1, $"{bCalls()} runs");
        b.Dispose();

        var (c, cClosed, _, cIns) = Open(() => Task.FromResult(0));
        cIns.PerformClick(); Pump();
        X(c);
        Check("after a good install the X closes it", cClosed());
        c.Dispose();

        var (d, dClosed, _, dIns) = Open(() => Task.FromResult(-1));
        dIns.PerformClick(); Pump();
        Check("after the Windows prompt is declined, Install is offered again", dIns.Text == "Install" && dIns.Enabled);
        X(d);
        Check("...and the X closes it", dClosed());
        d.Dispose();

        var running = new TaskCompletionSource<int>();
        var (e, eClosed, _, eIns) = Open(() => running.Task);
        eIns.PerformClick(); Pump();
        X(e);
        Check("WHILE it installs, the X is refused — nobody would be told how it went", !eClosed());
        running.SetResult(0); Pump();
        X(e);
        Check("...and once it has finished, the X closes it", eClosed());
        e.Dispose();
    }

    private static IEnumerable<Control> All(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in All(x)) yield return y; } }

    private static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

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
}

internal sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
