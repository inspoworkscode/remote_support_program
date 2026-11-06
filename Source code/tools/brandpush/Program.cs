using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// brandpush - branding reaches installed machines (2026-10-07, roadmap 4.6, the settings half).
//
// Clint: "At the moment, a company that changes its logo or colours only sees it on new downloads.
// The hub should send the current brand.json when a machine connects."
//
// Part 1, on disk: what BrandRefresh keeps on a machine and how Branding.Load reads it back — the
// installed brand.json never touched (the uninstaller finds the shortcuts by it), the server address and
// key never changed, nothing in a message choosing a path, a logo a running app holds open not stopping
// the next one.
//
// Part 2, on the wire: its own platform on port 8098 against a throwaway database (the live server is
// never touched). A machine with another look is sent the company's; one with the same look is sent
// nothing; an older build is sent nothing; saving the Branding page in the portal reaches every connected
// machine at once, as ONE message although the page saves twice; another company's machines hear nothing;
// a 1.9 MB logo arrives whole; the command line reaches a machine at its next connect.
//
//   dotnet run -c Debug --project tools/brandpush/brandpush.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8098;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("brandpush - branding reaches installed machines");
Console.WriteLine();

var work = Path.Combine(Path.GetTempPath(), "rs-brandpush-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);

// A 1x1 PNG, and variants of it: bytes after IEND are ignored by every reader, so each is a different file.
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");
byte[] Variant(int n, int size = 0) { var b = png.Concat(Encoding.ASCII.GetBytes("variant-" + n)).ToList(); while (b.Count < size) b.Add((byte)(b.Count % 251)); return b.ToArray(); }

try { DiskChecks(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }

Console.WriteLine();
try { if (await WireChecksAsync() is int code and > 0) return code; }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"kept at {work}");
return fail == 0 ? 0 : 1;

// ============================================================================================
void DiskChecks()
{
    Console.WriteLine("== on the machine: kept beside the installed branding, read back over it ==");
    var install = Path.Combine(work, "install");
    Directory.CreateDirectory(Path.Combine(install, "branding"));
    var installed = new Branding
    {
        CompanyName = "Installed Co", ProductName = "Installed Product", PrimaryColor = "#111111", AccentColor = "#222222",
        ServerUrl = "wss://installed.example", TenantKey = "installed-key", Logo = "logo.png", ConsentTimeoutSeconds = 45,
        HealthCheckEnabled = true, Free = false
    };
    var brandJson = Path.Combine(install, "branding", "brand.json");
    File.WriteAllText(brandJson, Json.Serialize(installed));
    File.WriteAllBytes(Path.Combine(install, "branding", "logo.png"), Variant(0));
    var brandBefore = File.ReadAllBytes(brandJson);

    var b0 = Branding.LoadFrom(install)!;
    Check("before anything is sent, it is the installed look, with no fingerprint", b0.CompanyName == "Installed Co" && b0.LiveHash == "", $"{b0.CompanyName} / '{b0.LiveHash}'");

    Check("nothing that decides where a machine connects, or whose it is, can travel",
        !typeof(BrandLook).GetProperties().Any(p => p.Name is "ServerUrl" or "TenantKey" or "Free" || p.Name.StartsWith("HealthCheck")),
        string.Join(", ", typeof(BrandLook).GetProperties().Select(p => p.Name)));

    var server = new Branding
    {
        CompanyName = "New Co", ProductName = "New Product", PrimaryColor = "#0A7B83", AccentColor = "#F2A900", WelcomeText = "Hello again",
        ClientTheme = "dark", RequireConsent = true, UnattendedRequireConsent = true, ConsentTimeoutSeconds = 90, Logo = "logo.png",
        ServerUrl = "wss://somewhere-else.example", TenantKey = "another-key"
    };
    var m1 = BrandRefresh.Message(server, Variant(1));
    Check("the fingerprint is the look and the logo, and the same every time",
        m1.Hash == BrandRefresh.Hash(BrandRefresh.LookOf(server), Variant(1)) && m1.Hash != BrandRefresh.Message(server, Variant(2)).Hash);
    Check("kept", BrandRefresh.Save(install, m1, out var e1), e1);

    var b1 = Branding.LoadFrom(install)!;
    Check("read back: the new name, colours, words and consent settings",
        b1.CompanyName == "New Co" && b1.ProductName == "New Product" && b1.PrimaryColor == "#0A7B83" && b1.AccentColor == "#F2A900"
        && b1.WelcomeText == "Hello again" && b1.ClientTheme == "dark" && b1.UnattendedRequireConsent && b1.ConsentTimeoutSeconds == 90,
        $"{b1.CompanyName}, {b1.PrimaryColor}, {b1.ClientTheme}, {b1.ConsentTimeoutSeconds}s");
    Check("...and the new logo, from branding\\live",
        b1.HasLogo && b1.LogoPath.StartsWith(BrandRefresh.LiveDir(install)) && File.ReadAllBytes(b1.LogoPath).SequenceEqual(Variant(1)), b1.LogoPath);
    Check("the server address and the company key are still the INSTALLED ones",
        b1.ServerUrl == "wss://installed.example" && b1.TenantKey == "installed-key" && b1.HealthCheckEnabled, $"{b1.ServerUrl} / {b1.TenantKey}");
    Check("the installed brand.json is untouched, byte for byte (the uninstaller finds the shortcuts by its names)",
        File.ReadAllBytes(brandJson).SequenceEqual(brandBefore));
    Check("its fingerprint is said in the next hello", b1.LiveHash == m1.Hash && BrandRefresh.HashOn(install) == m1.Hash);

    // ---- what a message cannot do ----
    Console.WriteLine();
    Console.WriteLine("== what a message cannot do ==");
    var bad = new BrandLook { CompanyName = "Bad Co", ProductName = "Bad", PrimaryColor = "red; background:url(x)", AccentColor = "#12345", Logo = "..\\..\\evil.exe", ConsentTimeoutSeconds = 0 };
    var mBad = new BrandingMsg { Look = bad, Logo = Variant(3), Hash = BrandRefresh.Hash(bad, Variant(3)) };
    Check("a hostile look is still kept — its words are only words", BrandRefresh.Save(install, mBad, out var eb), eb);
    var bb = Branding.LoadFrom(install)!;
    var outside = Directory.EnumerateFiles(work, "evil*", SearchOption.AllDirectories).ToList();
    Check("a logo name with a path in it writes nowhere but branding\\live, under our own name",
        outside.Count == 0 && Path.GetDirectoryName(bb.LogoPath) == BrandRefresh.LiveDir(install) && Path.GetFileName(bb.LogoPath).StartsWith("logo-") && bb.LogoPath.EndsWith(".png"),
        bb.LogoPath + (outside.Count > 0 ? " — and " + string.Join(", ", outside) : ""));
    Check("colours that are not colours leave the last good ones", bb.PrimaryColor == "#111111" && bb.AccentColor == "#222222", $"{bb.PrimaryColor} / {bb.AccentColor}");
    Check("a consent timeout of 0 becomes the portal's least, 10 seconds", bb.ConsentTimeoutSeconds == 10, bb.ConsentTimeoutSeconds + "s");
    Check("a message with no proper fingerprint is refused",
        !BrandRefresh.Save(install, new BrandingMsg { Look = bad, Hash = "not-a-hash" }, out var eh) && BrandRefresh.HashOn(install) == mBad.Hash, eh);
    var huge = new byte[BrandRefresh.MaxLogoBytes + 1];
    Check("a logo over 4 MB is refused, and what was kept stays",
        !BrandRefresh.Save(install, new BrandingMsg { Look = bad, Logo = huge, Hash = BrandRefresh.Hash(bad, huge) }, out var ez) && BrandRefresh.HashOn(install) == mBad.Hash, ez);

    // ---- a running app holds its logo open ----
    Console.WriteLine();
    Console.WriteLine("== a logo held open by a running app ==");
    BrandRefresh.Save(install, m1, out _);
    var held = Branding.LoadFrom(install)!.LogoPath;
    var m2 = BrandRefresh.Message(server, Variant(2));
    bool saved2;
    // As Image.FromFile holds it: open, others may read, nobody may replace or delete it.
    using (var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        saved2 = BrandRefresh.Save(install, m2, out var e2);
        Check("the next look is kept while the old logo is held open", saved2, e2);
        var b2 = Branding.LoadFrom(install)!;
        Check("...under a new name, and read back", b2.LogoPath != held && File.ReadAllBytes(b2.LogoPath).SequenceEqual(Variant(2)), Path.GetFileName(b2.LogoPath));
        Check("...the held one left where it is for now", File.Exists(held));
    }
    BrandRefresh.Save(install, BrandRefresh.Message(server, Variant(4)), out _);
    Check("once let go, an old logo is cleared away by the next look", !File.Exists(held) && Directory.EnumerateFiles(BrandRefresh.LiveDir(install), "logo-*").Count() == 1,
        string.Join(", ", Directory.EnumerateFiles(BrandRefresh.LiveDir(install)).Select(Path.GetFileName)));

    // ---- a look whose logo has gone is not that look ----
    File.Delete(Branding.LoadFrom(install)!.LogoPath);
    var b3 = Branding.LoadFrom(install)!;
    Check("a kept look whose logo has gone is ignored: the installed look, and no fingerprint, so the server sends it again",
        b3.CompanyName == "Installed Co" && b3.LiveHash == "" && BrandRefresh.HashOn(install) == "" && b3.LogoPath == Path.Combine(install, "branding", "logo.png"),
        $"{b3.CompanyName} / '{b3.LiveHash}'");

    // ---- a company with no logo ----
    var noLogo = BrandRefresh.Message(server, null);
    BrandRefresh.Save(install, noLogo, out _);
    var b4 = Branding.LoadFrom(install)!;
    Check("a company with no logo shows none (its name is drawn instead)", !b4.HasLogo && b4.CompanyName == "New Co" && b4.LiveHash == noLogo.Hash);

    // ---- the agent holds its branding for life ----
    var agentHeld = Branding.LoadFrom(install)!;
    BrandRefresh.Save(install, BrandRefresh.Message(new Branding { CompanyName = "Later Co", UnattendedRequireConsent = false, Logo = "logo.png" }, Variant(5)), out _);
    BrandRefresh.Reapply(agentHeld);
    Check("Reapply moves branding already held (the agent's) to what is kept now",
        agentHeld.CompanyName == "Later Co" && !agentHeld.UnattendedRequireConsent && agentHeld.ServerUrl == "wss://installed.example", agentHeld.CompanyName);
}

// ============================================================================================
async Task<int> WireChecksAsync()
{
    var repo = FindRepoRoot();
    var dataDir = Path.Combine(work, "data");
    Directory.CreateDirectory(dataDir);
    var serverDir = Path.Combine(work, "server");
    Log($"building the platform into {serverDir}");
    var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
    if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
    var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
    var env = new Dictionary<string, string>
    {
        ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
        ["RSP_Testing__BrandingStaleMs"] = "1500",
        ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
    };
    HarnessDb.Use(env, dataDir);
    var firstLogo = Path.Combine(work, "first-logo.png");
    File.WriteAllBytes(firstLogo, Variant(10));
    string keyA = "", keyB = "";
    foreach (var (cmd, isA) in new[]
    {
        ("create-tenant \"Brand Push\" brand-push 5", true), ("approve brand-push", true),
        ("add-tech brand-push boss@local.test brand-push-2026 \"Brand Boss\" --admin", true),
        ($"set-logo brand-push \"{firstLogo}\"", true),
        ("create-tenant \"Other Company\" other-co 5", false), ("approve other-co", false)
    })
    {
        var r = Run(exe, cmd, serverDir, 60000, env);
        if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
        var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
        if (line != null) { if (isA) keyA = line["Tenant key:".Length..].Trim(); else keyB = line["Tenant key:".Length..].Trim(); }
    }
    if (keyA.Length == 0 || keyB.Length == 0) { Console.WriteLine("could not read the tenant keys"); return 2; }

    var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    var serverLog = new ConcurrentQueue<string>();
    using var server = Process.Start(psi)!;
    server.OutputDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    try
    {
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        {
            bool up = false;
            for (int i = 0; i < 60 && !up; i++) { try { up = (await probe.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { } if (!up) await Task.Delay(500); }
            if (!up) { Console.WriteLine("the platform did not come up"); return 2; }
        }

        Console.WriteLine("== a machine connecting ==");
        var agent = await RawDevice.OpenAsync(ws, keyA, "bp-1", DeviceRole.Agent, canBranding: true, brandHash: "");
        var first = await agent.WaitBranding(1, 5000);
        Check("a machine with the installed look is sent the company's", first.Count == 1, $"{first.Count} message(s)");
        var m = first.FirstOrDefault().msg;
        Check("...its name and its logo, byte for byte", m?.Look.CompanyName == "Brand Push" && m.Logo != null && m.Logo.SequenceEqual(Variant(10)),
            m == null ? "" : $"{m.Look.CompanyName}, logo {m.Logo?.Length ?? 0} bytes");
        Check("...with a fingerprint the machine can check", m != null && m.Hash == BrandRefresh.Hash(m.Look, m.Logo));
        var raw = first.FirstOrDefault().raw ?? "";
        Check("...and neither the server address nor the company key anywhere in it",
            raw.Length > 0 && !raw.Contains(keyA) && !raw.Contains("serverUrl", StringComparison.OrdinalIgnoreCase) && !raw.Contains("tenantKey", StringComparison.OrdinalIgnoreCase),
            $"{raw.Length} characters");
        agent.Dispose();

        var same = await RawDevice.OpenAsync(ws, keyA, "bp-1", DeviceRole.Agent, canBranding: true, brandHash: m?.Hash ?? "");
        Check("connecting again with that look: nothing is sent", (await same.WaitBranding(1, 2500)).Count == 0);
        var old = await RawDevice.OpenAsync(ws, keyA, "bp-old", DeviceRole.Agent, canBranding: false, brandHash: "");
        Check("an older build, which does not say it can take one, is sent nothing", (await old.WaitBranding(1, 2500)).Count == 0);
        var tray = await RawDevice.OpenAsync(ws, keyA, "bp-1", DeviceRole.Client, canBranding: true, brandHash: m?.Hash ?? "");
        var other = await RawDevice.OpenAsync(ws, keyB, "bp-other", DeviceRole.Agent, canBranding: true, brandHash: "");
        var otherFirst = await other.WaitBranding(1, 4000);
        Check("(another company's machine is sent ITS company's look)", otherFirst.Count == 1 && otherFirst[0].msg.Look.CompanyName == "Other Company",
            otherFirst.FirstOrDefault().msg?.Look.CompanyName ?? "nothing");

        // ---- the real thing on the machine's side ----
        Console.WriteLine();
        Console.WriteLine("== the real DeviceLink and BrandRefresh, as the agent runs them ==");
        var install = Path.Combine(work, "wire-install");
        Directory.CreateDirectory(Path.Combine(install, "branding"));
        File.WriteAllText(Path.Combine(install, "branding", "brand.json"), Json.Serialize(new Branding { CompanyName = "Old Name", ServerUrl = ws, TenantKey = keyA }));
        var id = new DeviceIdentity { DeviceId = "bp-link", DeviceKey = "bp-link-key" };
        var link = new DeviceLink(ws, id, DeviceRole.Agent, keyA);
        link.Can.Add(DeviceCan.Branding);
        link.BrandHash = Branding.LoadFrom(install)!.LiveHash;
        var kept = new TaskCompletionSource<string>();
        link.OnBranding += bm => { if (BrandRefresh.Save(install, bm, out var why)) { link.BrandHash = bm.Hash; kept.TrySetResult(bm.Hash); } else kept.TrySetResult("refused: " + why); };
        using var linkCts = new CancellationTokenSource();
        _ = link.RunAsync(linkCts.Token);
        var keptHash = await Task.WhenAny(kept.Task, Task.Delay(6000)) == kept.Task ? kept.Task.Result : "nothing arrived";
        var onDisk = Branding.LoadFrom(install)!;
        Check("it arrives, is kept, and Branding.Load reads the company's look", keptHash == m?.Hash && onDisk.CompanyName == "Brand Push" && onDisk.HasLogo, $"{keptHash[..Math.Min(12, keptHash.Length)]}…, {onDisk.CompanyName}");
        Check("...still pointed at the same server with the same key", onDisk.ServerUrl == ws && onDisk.TenantKey == keyA);
        // As the agent and tray app start: BrandHash from what Branding.Load read, said in the REAL hello.
        var link2 = new DeviceLink(ws, new DeviceIdentity { DeviceId = "bp-link", DeviceKey = "bp-link-key" }, DeviceRole.Client, keyA);
        link2.Can.Add(DeviceCan.Branding);
        link2.BrandHash = Branding.LoadFrom(install)!.LiveHash;
        int link2Got = 0;
        link2.OnBranding += _ => Interlocked.Increment(ref link2Got);
        using var link2Cts = new CancellationTokenSource();
        _ = link2.RunAsync(link2Cts.Token);
        await Task.Delay(3000);
        Check("a starting app whose kept look is the company's says so in its hello, and is sent nothing", link2Got == 0 && link2.Connected,
            $"{link2Got} message(s), said {link2.BrandHash[..Math.Min(12, link2.BrandHash.Length)]}…");
        link2Cts.Cancel();

        // ---- the portal ----
        Console.WriteLine();
        Console.WriteLine("== saving the Branding page in the portal ==");
        var bigLogo = Variant(11, 1_900_000);
        var portal = await Portal("boss@local.test", "brand-push-2026");
        var page = await portal.GetStringAsync("/portal/branding");
        Check("(signed in to the Branding page)", page.Contains("name=\"CompanyName\""), Heading(page));
        same.Clear(); tray.Clear(); old.Clear(); other.Clear();
        var form = new MultipartFormDataContent
        {
            { new StringContent("Brand Push Renamed"), "CompanyName" }, { new StringContent("Push Support"), "ProductName" },
            { new StringContent("We have moved offices"), "WelcomeText" }, { new StringContent("help@push.test"), "SupportEmail" },
            { new StringContent("021 555 0100"), "SupportPhone" }, { new StringContent("#0A7B83"), "PrimaryColor" }, { new StringContent("#F2A900"), "AccentColor" },
            { new StringContent("light"), "TechnicianTheme" }, { new StringContent("dark"), "ClientTheme" },
            { new StringContent("true"), "RequireConsent" }, { new StringContent("60"), "ConsentTimeoutSeconds" },
            { new StringContent(Token(FormOf(page, "enctype=\"multipart/form-data\""))), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(bigLogo); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "Logo", "new-logo.png");
        var saved = await portal.PostAsync("/portal/branding", form);
        var savedPage = WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync());
        Check("(the page says it saved)", savedPage.Contains("Branding saved."), Between(savedPage, "class=\"err", "<") ?? "");

        var pushed = await same.WaitBranding(2, 6000);
        Check("a connected machine is sent the new look at once — ONE message, though the page saves the words and the logo apart",
            pushed.Count == 1, $"{pushed.Count} message(s)");
        var p = pushed.FirstOrDefault().msg;
        Check("...the new name, colours, words and light/dark", p != null && p.Look.CompanyName == "Brand Push Renamed" && p.Look.PrimaryColor.Equals("#0A7B83", StringComparison.OrdinalIgnoreCase)
            && p.Look.WelcomeText == "We have moved offices" && p.Look.ClientTheme == "dark", p == null ? "" : $"{p.Look.CompanyName}, {p.Look.PrimaryColor}, {p.Look.ClientTheme}");
        Check("...and the new 1.9 MB logo, whole", p?.Logo != null && p.Logo.SequenceEqual(bigLogo), $"{p?.Logo?.Length ?? 0} bytes");
        Check("the tray app on the same machine is sent it too", (await tray.WaitBranding(1, 3000)).Count == 1);
        Check("the older build is still sent nothing", old.Brandings().Count == 0);
        Check("ANOTHER company's machine hears nothing of it", other.Brandings().Count == 0);
        Check("the machine with the real DeviceLink keeps the new look too",
            await WaitAsync(() => Branding.LoadFrom(install)!.CompanyName == "Brand Push Renamed" && File.ReadAllBytes(Branding.LoadFrom(install)!.LogoPath).SequenceEqual(bigLogo), 6000),
            Branding.LoadFrom(install)!.CompanyName);

        same.Clear();
        var again = await portal.GetStringAsync("/portal/branding");
        var noChange = new MultipartFormDataContent
        {
            { new StringContent("Brand Push Renamed"), "CompanyName" }, { new StringContent("Push Support"), "ProductName" },
            { new StringContent("We have moved offices"), "WelcomeText" }, { new StringContent("help@push.test"), "SupportEmail" },
            { new StringContent("021 555 0100"), "SupportPhone" }, { new StringContent("#0A7B83"), "PrimaryColor" }, { new StringContent("#F2A900"), "AccentColor" },
            { new StringContent("light"), "TechnicianTheme" }, { new StringContent("dark"), "ClientTheme" },
            { new StringContent("true"), "RequireConsent" }, { new StringContent("60"), "ConsentTimeoutSeconds" },
            { new StringContent(Token(FormOf(again, "enctype=\"multipart/form-data\""))), "__RequestVerificationToken" }
        };
        await portal.PostAsync("/portal/branding", noChange);
        var resent = await same.WaitBranding(1, 4000);
        Check("saving again with nothing a machine sees changed sends nothing", resent.Count == 0, $"{resent.Count} message(s)");

        // ---- the command line: a separate process ----
        Console.WriteLine();
        Console.WriteLine("== the command line (a separate process the server never hears from) ==");
        same.Clear();
        var cli = Run(exe, "set-brand brand-push company \"Changed From The CLI\"", serverDir, 60000, env);
        Check("(set-brand ran)", cli.code == 0, cli.output.Trim());
        Check("a machine already connected is not told (said in the handover — it is told at its next connect)", (await same.WaitBranding(1, 2500)).Count == 0);
        await Task.Delay(1600);   // RSP_Testing__BrandingStaleMs
        var late = await RawDevice.OpenAsync(ws, keyA, "bp-late", DeviceRole.Agent, canBranding: true, brandHash: p?.Hash ?? "");
        var lateGot = await late.WaitBranding(1, 4000);
        Check("a machine connecting afterwards is sent it", lateGot.Count == 1 && lateGot[0].msg.Look.CompanyName == "Changed From The CLI",
            lateGot.FirstOrDefault().msg?.Look.CompanyName ?? "nothing");

        linkCts.Cancel();
        foreach (var d in new[] { same, old, tray, other, late }) d.Dispose();
    }
    finally
    {
        try { if (!server.HasExited) server.Kill(true); server.WaitForExit(5000); } catch { }
        File.WriteAllLines(Path.Combine(work, "server.log"), serverLog);
    }
    return 0;
}

async Task<HttpClient> Portal(string email, string password)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(http) };
    var page = await c.GetStringAsync("/login");
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
    { ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = Token(page) }));
    return c;
}

static string Token(string page) =>
    Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
    ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
static string FormOf(string page, string marker)
{
    var at = page.IndexOf(marker, StringComparison.Ordinal);
    if (at < 0) return "";
    var start = page.LastIndexOf("<form", at, StringComparison.Ordinal);
    var end = page.IndexOf("</form>", at, StringComparison.Ordinal);
    return start < 0 || end < 0 ? "" : page[start..end];
}
static string Heading(string page) => Between(page, "<h1", "</h1>") ?? "(no heading)";
static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}

static async Task<bool> WaitAsync(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { try { if (f()) return true; } catch { } await Task.Delay(150); } return false; }

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}

/// <summary>One machine connection, keeping every text frame exactly as it came off the wire.</summary>
sealed class RawDevice : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentQueue<string> _frames = new();

    public static async Task<RawDevice> OpenAsync(string ws, string tenantKey, string deviceId, DeviceRole role, bool canBranding, string brandHash)
    {
        var d = new RawDevice();
        await d._ws.ConnectAsync(new Uri($"{ws}/ws/device"), default);
        _ = d.ReadAsync();
        var hello = new HelloMsg
        {
            Role = role, DeviceId = deviceId, DeviceKey = deviceId + "-key", MachineName = deviceId.ToUpperInvariant(), UserName = "Pretend",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = tenantKey, BrandHash = brandHash,
            Can = canBranding ? new List<string> { DeviceCan.Branding } : new List<string>()
        };
        await d._ws.SendAsync(Encoding.UTF8.GetBytes(Json.Serialize<Msg>(hello)), WebSocketMessageType.Text, true, default);
        return d;
    }

    private async Task ReadAsync()
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do { r = await _ws.ReceiveAsync(buf, default); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Text) _frames.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch { }
    }

    public List<(BrandingMsg msg, string raw)> Brandings() =>
        _frames.Where(f => f.Contains("\"t\":\"branding\"")).Select(f => ((BrandingMsg)Json.Deserialize<Msg>(f)!, f)).ToList();

    /// <summary>Waits the whole time given (a second message is what some checks look for), or until <paramref name="enough"/> arrive.</summary>
    public async Task<List<(BrandingMsg msg, string raw)>> WaitBranding(int enough, int ms)
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e && Brandings().Count < enough) await Task.Delay(100);
        return Brandings();
    }

    public void Clear() { while (_frames.TryDequeue(out _)) { } }
    public void Dispose() { try { _ws.Abort(); } catch { } }
}
