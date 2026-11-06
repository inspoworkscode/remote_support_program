using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// Can EVERYBODY say which customer a machine belongs to, from the console?
//
// Clint, 2026-09-20: "devices should be an option that everybody has, otherwise it's only the
// owner/supervisor that can link them to a client or contact. This should also be a console
// feature."
//
// Until a machine is linked, its tickets never reach a client page. So an unlinked machine is a
// hole in the records, and the person who notices it is whoever is looking at the list — which
// is a technician in the console, not the owner in a browser. Two things had to change: the
// portal's Devices page stopped being admin-only, and the hub learned DeviceLinkMsg.
//
// THIS TALKS TO A REAL HUB, and that is the whole point of it. §33 of the handover is the reason:
// `unattendedcheck` drove the tray app against a stand-in server, said so in its own output, and
// the bug lived precisely in the half it admitted to not covering. A permission that is only
// asserted against `Permissions.CanLinkDevices` in-process would prove that the RULE says yes
// while saying nothing about whether the SOCKET honours it.
//
// It starts a PLATFORM OF ITS OWN — its own port, its own empty database in the temp folder, its
// own tenant — and touches nothing that is already running. The live server on 8090, its database
// and Clint's real machines are not involved and are not modified.
//
//   dotnet run -c Release --project tools/devicelink/devicelink.csproj
// ---------------------------------------------------------------------------------------------

internal static class DeviceLinkCheck
{
    private static int _pass, _fail;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private const string Slug = "linktest";
    private const string Pw = "Harness-Pw-1";

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }

        var dataDir = Path.Combine(Path.GetTempPath(), "rs-devicelink-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server on 8090 and its database are not touched)");
        Console.WriteLine();

        string tenantKey;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Link Test Co", Slug);
            var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            tenantKey = line["Tenant key:".Length..].Trim();
            await RunPlatform(proj, dataDir, port, "approve", Slug);

            // One account of each role, because the claim being tested is about all three.
            await RunPlatform(proj, dataDir, port, "add-tech", Slug, "owner@testing.local", Pw, "Olive Owner", "--admin");
            await RunPlatform(proj, dataDir, port, "add-tech", Slug, "office@testing.local", Pw, "Ossie Office", "--billing");
            await RunPlatform(proj, dataDir, port, "add-tech", Slug, "tech@testing.local", Pw, "Tess Tech");
        }
        catch (Exception ex) { Console.WriteLine("Could not start the platform: " + ex.Message); return 1; }

        using var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }

            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // A customer with two people at it, and a second customer, so "a person at the wrong
            // company" is a case that can actually be constructed.
            var zenith = await CreateClientAsync(http, port, "owner@testing.local", "Zenith Ltd");
            var acme = await CreateClientAsync(http, port, "owner@testing.local", "Acme Ltd");
            var bob = await CreateContactAsync(http, port, "owner@testing.local", zenith, "Bob at Zenith");
            if (zenith == 0 || acme == 0 || bob == 0)
            {
                Console.WriteLine($"Could not set up customers (zenith={zenith}, acme={acme}, bob={bob}).");
                return 1;
            }

            // ---- every role may link a machine ------------------------------------------------
            //
            // The CONSOLE loop is the owner and the technician only, and that is not an oversight:
            // /api/login refuses TenantRole.Billing outright — "the console is for technicians" —
            // because the office administrator's seat has no remote access at all (docs/PRICING.md:
            // seven seats, one of them an office administrator who cannot connect to a machine).
            // Their way in is the portal, which is checked at the bottom. Asserted below rather
            // than assumed, because a harness that silently skips a role proves nothing about it.
            Console.WriteLine("== every role that has a console can say whose a machine is ==");
            foreach (var (who, email) in new[]
                     { ("the owner", "owner@testing.local"),
                       ("a technician", "tech@testing.local") })
            {
                var machine = await ConnectMachineAsync(port, tenantKey, $"LINK-{who.Split(' ')[^1].ToUpper()}");
                await using var dev = machine.conn;
                var tech = await SignInAsync(http, port, email);
                if (tech == null) { Check($"{who} could sign in", false, "no token"); continue; }
                await using var ws = tech;

                var seen = await LinkAndWaitAsync(ws, machine.deviceId, zenith, 0);
                Check($"{who} can link a machine to a company",
                      seen is { ClientId: > 0 } && seen.ClientName == "Zenith Ltd",
                      Describe(seen));

                seen = await LinkAndWaitAsync(ws, machine.deviceId, 0, bob);
                Check($"{who} can link a machine to a person",
                      seen is { ContactId: > 0 } && seen.ContactName == "Bob at Zenith",
                      Describe(seen));

                // A person implies their company. The console sends only the contact, so if the
                // server did not fill the client in, the machine would vanish from every group.
                Check($"...and the person's company comes with them", seen is { ClientId: > 0 },
                      seen == null ? "" : $"clientId={seen.ClientId} clientName='{seen.ClientName}'");

                seen = await LinkAndWaitAsync(ws, machine.deviceId, 0, 0);
                Check($"{who} can unlink a machine",
                      seen is { ClientId: 0, ContactId: 0 }, Describe(seen));
            }

            // The one role with no console. Checked explicitly, so that the day somebody widens
            // /api/login this harness says so rather than quietly testing two roles out of three.
            Console.WriteLine();
            Console.WriteLine("== the office administrator has no console, by design ==");
            {
                var refused = await SignInAsync(http, port, "office@testing.local");
                Check("the office administrator is refused a console sign-in", refused == null,
                      refused == null ? "their seat has no remote access — docs/PRICING.md" : "they got a token");
                if (refused != null) await refused.DisposeAsync();
            }

            // ---- the rules that keep the two halves agreeing ----------------------------------
            Console.WriteLine();
            Console.WriteLine("== the rules a wrong answer has to meet ==");
            {
                var machine = await ConnectMachineAsync(port, tenantKey, "LINK-RULES");
                await using var dev = machine.conn;
                var ws = await SignInAsync(http, port, "tech@testing.local");
                if (ws == null) { Check("a technician could sign in", false); return Finish(); }
                await using var _ = ws;

                // Link it properly first, so a refusal can be told apart from nothing happening.
                var ok = await LinkAndWaitAsync(ws, machine.deviceId, zenith, 0);
                Check("the machine starts out at Zenith", ok is { ClientId: > 0 }, Describe(ok));

                // Bob is at Zenith, not Acme. ContactService refuses this, and the hub must not
                // route around it — a machine that says it belongs to Acme AND to Bob at Zenith is
                // a machine whose tickets file in two places.
                var (after, err) = await LinkExpectingRefusalAsync(ws, machine.deviceId, acme, bob);
                Check("a person at a different company is refused", err.Length > 0, err.Length > 0 ? err : "no error came back");
                Check("...and the machine was left where it was", after == null || after.ClientName == "Zenith Ltd",
                      after == null ? "nothing was broadcast, which is correct" : Describe(after));

                var (_, err2) = await LinkExpectingRefusalAsync(ws, "not-a-real-device-id", zenith, 0);
                Check("a machine that is not on file is refused", err2.Length > 0, err2.Length > 0 ? err2 : "no error came back");
            }

            // ---- the browser half -------------------------------------------------------------
            //
            // The page policy is the rule; the nav link is convenience. Both are checked, because
            // a page somebody may open and cannot find is not a feature they have.
            Console.WriteLine();
            Console.WriteLine("== the portal's Devices page, for each role ==");
            foreach (var (who, email) in new[]
                     { ("the owner", "owner@testing.local"),
                       ("the office administrator", "office@testing.local"),
                       ("a technician", "tech@testing.local") })
            {
                var (status, body) = await GetPortalPageAsync(port, email, "/portal/devices");
                // The page's own heading, not the status code. A refused page redirects to the
                // sign-in form and that redirect is followed, so it arrives as a cheerful 200 —
                // this check passed against the OLD admin-only policy until it asked for the
                // heading instead, which is exactly the kind of green that proves nothing.
                var got = body.Contains("<h1>Customer machines</h1>", StringComparison.Ordinal);
                Check($"{who} may open /portal/devices", got, got ? $"HTTP {status}" : $"HTTP {status}, and not the Devices page");
                Check($"...and the nav offers it to {who}", got && body.Contains("href=\"/portal/devices\"", StringComparison.Ordinal),
                      got ? "" : "the page did not load, so the nav on it proves nothing");
            }

            // Customers first, the machines nobody has linked LAST — the console's order
            // (technicians, 2026-09-30; the page used to put them first). By now LINK-RULES is at
            // Zenith and the other two were unlinked, so the page has one of each.
            {
                var (_, body) = await GetPortalPageAsync(port, "owner@testing.local", "/portal/devices");
                int customer = body.IndexOf(">Zenith Ltd</a>", StringComparison.Ordinal);
                int strays = body.IndexOf("<span>Not linked to a client</span>", StringComparison.Ordinal);
                Check("the portal lists customers first and the unlinked machines last",
                      customer >= 0 && strays > customer, $"Zenith at {customer}, not linked at {strays}");
            }

            return Finish();
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }
    }

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static string Describe(DeviceInfo? d) =>
        d == null ? "nothing was broadcast in five seconds"
                  : $"clientId={d.ClientId} '{d.ClientName}', contactId={d.ContactId} '{d.ContactName}'";

    // ---- a technician socket -------------------------------------------------------------------

    private sealed class Tech : IAsyncDisposable
    {
        public WsConnection Conn = null!;
        public readonly List<DeviceInfo> Updates = new();
        public readonly List<string> Errors = new();
        public ValueTask DisposeAsync() => Conn.DisposeAsync();
        public void Send(Msg m) => Conn.Send(m);
        public void Clear() { lock (Updates) Updates.Clear(); lock (Errors) Errors.Clear(); }
    }

    private static async Task<Tech?> SignInAsync(HttpClient http, int port, string email)
    {
        var resp = await http.PostAsJsonAsync("/api/login", new { username = email, password = Pw }, Json.Options);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("token", out var tok)) return null;

        var t = new Tech();
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={tok.GetString()}"), CancellationToken.None);
        t.Conn = new WsConnection(cws);
        t.Conn.OnMessage += m =>
        {
            if (m is DeviceUpdateMsg du) lock (t.Updates) t.Updates.Add(du.Device);
            else if (m is ErrorMsg e) lock (t.Errors) t.Errors.Add(e.Message);
        };
        _ = t.Conn.RunAsync(CancellationToken.None);
        await Task.Delay(400);   // let the hub finish its opening state burst
        return t;
    }

    /// <summary>Links, then waits for the broadcast that says it happened.</summary>
    private static async Task<DeviceInfo?> LinkAndWaitAsync(Tech t, string deviceId, int clientId, int contactId)
    {
        t.Clear();
        t.Send(new DeviceLinkMsg { DeviceId = deviceId, ClientId = clientId, ContactId = contactId });
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            lock (t.Updates)
            {
                var hit = t.Updates.LastOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>Links expecting to be told no. Returns whatever was broadcast, and the complaint.</summary>
    private static async Task<(DeviceInfo? after, string error)> LinkExpectingRefusalAsync(
        Tech t, string deviceId, int clientId, int contactId)
    {
        t.Clear();
        t.Send(new DeviceLinkMsg { DeviceId = deviceId, ClientId = clientId, ContactId = contactId });
        await Task.Delay(1500);
        lock (t.Errors)
            lock (t.Updates)
                return (t.Updates.LastOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)),
                        t.Errors.FirstOrDefault() ?? "");
    }

    // ---- a machine on the wire -------------------------------------------------------------------

    private static async Task<(string deviceId, WsConnection conn)> ConnectMachineAsync(int port, string tenantKey, string machine)
    {
        var id = Guid.NewGuid().ToString("N");
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/device"), CancellationToken.None);
        var conn = new WsConnection(cws);
        _ = conn.RunAsync(CancellationToken.None);
        conn.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = id, DeviceKey = Guid.NewGuid().ToString("N"),
            MachineName = machine, UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey
        });
        await Task.Delay(600);   // let the hub persist it before anything tries to link it
        return (id, conn);
    }

    // ---- the portal, over HTTP with a real cookie ---------------------------------------------

    private static async Task<HttpClient> PortalClientAsync(int port, string email)
    {
        var jar = new System.Net.CookieContainer();
        var h = new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true };
        var c = new HttpClient(h) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        // The portal's own sign-in form, antiforgery token and all.
        var page = await c.GetStringAsync("/login");
        var token = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
                    ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
        await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = Pw, ["__RequestVerificationToken"] = token
        }));
        return c;
    }

    private static async Task<(int status, string body)> GetPortalPageAsync(int port, string email, string path)
    {
        try
        {
            var c = await PortalClientAsync(port, email);
            var r = await c.GetAsync(path);
            var body = await r.Content.ReadAsStringAsync();
            // A redirect back to the sign-in page means refused, however cheerful the status code.
            if (body.Contains("name=\"Password\"", StringComparison.Ordinal) && !body.Contains("Machines", StringComparison.Ordinal))
                return (403, body);
            return ((int)r.StatusCode, body);
        }
        catch (Exception ex) { return (0, ex.Message); }
    }

    private static string? Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        var j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? null : s[i..j];
    }

    /// <summary>Makes a customer through the portal, as the owner, and returns its id.</summary>
    private static async Task<int> CreateClientAsync(HttpClient _, int port, string email, string name)
    {
        try
        {
            var c = await PortalClientAsync(port, email);
            var page = await c.GetStringAsync("/portal/clients");
            var token = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
                        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
            await c.PostAsync("/portal/clients?handler=Add", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = name, ["__RequestVerificationToken"] = token
            }));
            // Its id, which the console's messages name it by, from the type-ahead: the page's own links name a client by
            // its number in the company now, never its id (HANDOVER §159).
            var found = System.Text.Json.JsonDocument.Parse(await c.GetStringAsync("/api/portal/clients?term=" + Uri.EscapeDataString(name))).RootElement;
            return found.EnumerateArray().Where(x => x.GetProperty("name").GetString() == name).Select(x => x.GetProperty("id").GetInt32()).DefaultIfEmpty(0).Max();
        }
        catch { return 0; }
    }

    private static async Task<int> CreateContactAsync(HttpClient _, int port, string email, int clientId, string name)
    {
        try
        {
            var c = await PortalClientAsync(port, email);
            var page = await c.GetStringAsync($"/portal/clients/{clientId}");
            var token = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
                        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
            await c.PostAsync($"/portal/clients/{clientId}?handler=AddContact",
                new FormUrlEncodedContent(new Dictionary<string, string>
                { ["id"] = clientId.ToString(), ["name"] = name, ["__RequestVerificationToken"] = token }));
            var after = await c.GetStringAsync($"/portal/clients/{clientId}");
            var marker = "name=\"contactId\" value=\"";
            var ids = new List<int>();
            int at = 0;
            while ((at = after.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                at += marker.Length;
                var digits = new string(after[at..].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var id)) ids.Add(id);
            }
            return ids.Count == 0 ? 0 : ids.Max();
        }
        catch { return 0; }
    }

    // ---- running a platform of our own ------------------------------------------------------

    private static Process StartPlatform(string proj, string dataDir, int port)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { };
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

    private static ProcessStartInfo Psi(string proj, string dataDir, int port)
    {
        var psi = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        // Debug, deliberately: the Release output is usually locked by the server Clint has
        // running, and a harness that cannot run while the product is running does not get run.
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
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
