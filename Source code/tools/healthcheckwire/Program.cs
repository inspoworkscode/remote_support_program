using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// The monthly health check, per customer with a per-machine override — against a REAL hub.
//
// Clint, 2026-09-28: not every customer should get the health check, only SLA clients and the
// ones chosen. So the default install is without it, a tick on the customer turns it on for every
// machine of theirs, and a right-click on one machine overrides that. The AGENT SERVICE on the
// machine is told the state wanted and carries it out, because only a SYSTEM service can register a
// SYSTEM scheduled task.
//
// What has to be true, and what this checks:
//   * the rule itself — company switch, then the machine's override, then the customer's tick;
//   * the agent is told on connect, and told again the moment any of the three changes, from the
//     portal as well as from the console;
//   * a reinstalled machine that arrives with a new id is NEVER told "off" before the hub has
//     recognised it — "off" removes, and a flap would remove a paying customer's maintenance;
//   * only the agent may say what is installed;
//   * the endpoint the monthly run asks says, for THIS machine, whether it is still wanted — the
//     run removes its own schedule on a false, and before this nothing was ever said at all.
//
// It starts a PLATFORM OF ITS OWN — its own port, its own empty database in the temp folder, its
// own tenant — and touches nothing that is already running.
//
//   dotnet run -c Release --project tools/healthcheckwire/healthcheckwire.csproj
// ---------------------------------------------------------------------------------------------

internal static class HealthCheckWire
{
    private static int _pass, _fail;
    private const string Slug = "hcwire";
    private const string Pw = "Harness-Pw-1";
    private const string Admin = "admin@testing.local";
    private const string TechEmail = "tech@testing.local";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static async Task<int> Main()
    {
        TheRule();

        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-hcwire-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

        Console.WriteLine();
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server on 8090 and its database are not touched)");

        string tenantKey;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Health Wire Co", Slug);
            var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            tenantKey = line["Tenant key:".Length..].Trim();
            await RunPlatform(proj, dataDir, port, "approve", Slug);
            await RunPlatform(proj, dataDir, port, "add-tech", Slug, Admin, Pw, "Ada Admin", "--admin");
            await RunPlatform(proj, dataDir, port, "add-tech", Slug, TechEmail, Pw, "Tess Tech");
        }
        catch (Exception ex) { Console.WriteLine("Could not start the platform: " + ex.Message); return 1; }

        using var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var portal = await PortalClientAsync(port, Admin);

            var zenith = await CreateClientAsync(portal, "Zenith Ltd");
            var acme = await CreateClientAsync(portal, "Acme Ltd");
            if (zenith == 0 || acme == 0) { Console.WriteLine($"Could not set up customers (zenith={zenith}, acme={acme})."); return 1; }

            var tech = await SignInAsync(http, port, TechEmail);
            if (tech == null) { Console.WriteLine("The technician could not sign in."); return 1; }
            await using var _t = tech;

            // ---- one machine: tray app AND agent, like a real install -----------------------
            Console.WriteLine();
            Console.WriteLine("== a machine connects, with the company switch still off ==");
            var m1 = NewMachine("HC-ZENITH-PC");
            await using var m1Client = await ConnectAsync(port, tenantKey, m1, DeviceRole.Client);
            var m1Agent = await ConnectAsync(port, tenantKey, m1, DeviceRole.Agent);
            var first = await m1Agent.NextWanted(8000);
            Check("the agent is told what is wanted as soon as it connects", first != null,
                  first == null ? "nothing in eight seconds - a machine switched off while it was changed would never catch up" : $"on={first.On}");
            Check("and with the company switch off, the answer is OFF", first is { On: false });

            var linked = await tech.Act(new DeviceLinkMsg { DeviceId = m1.DeviceId, ClientId = zenith }, m1.DeviceId);
            Check("the machine is linked to Zenith", linked is { ClientId: > 0 }, linked == null ? "no broadcast" : linked.ClientName);
            var afterLink = await m1Agent.NextWanted(3000);
            Check("linking to a customer with no tick leaves it off", afterLink == null || !afterLink.On,
                  afterLink == null ? "nothing sent, or off" : $"on={afterLink.On}");

            // ---- the settings form itself (2026-10-21) ------------------------------------------
            // Clint switched it on twice and it was off at every sign-in: the address box was
            // type="email", which the BROWSER refuses for two addresses without posting anything.
            Console.WriteLine();
            Console.WriteLine("== the settings form: the browser never refuses it, the server says why ==");
            var form0 = await portal.GetStringAsync("/portal/healthcheck");
            Check("the form is novalidate, so the browser cannot block Save on its own",
                  System.Text.RegularExpressions.Regex.IsMatch(form0, "<form method=\"post\" class=\"form card\"[^>]*novalidate"));
            Check("...and the report address box is not type=\"email\" (which refuses two addresses)",
                  !form0.Contains("name=\"healthCheckEmailTo\" type=\"email\""));
            var refused = await PostCompanyAsync(portal, true, "reports@testing.local; not-an-address") ?? "";
            Check("a bad address is refused by the server, naming it", refused.Contains("&quot;not-an-address&quot; does not look like an email address")
                  && !refused.Contains("Saved."), refused.Length == 0 ? "no page" : "");
            Check("...and the refused page keeps the tick and what was typed",
                  SwitchTicked(refused) && refused.Contains("value=\"reports@testing.local; not-an-address\""));
            Check("...but nothing was saved: a fresh visit shows it off", !SwitchTicked(await portal.GetStringAsync("/portal/healthcheck")));
            var two = await PostCompanyAsync(portal, true, "reports@testing.local; office@testing.local") ?? "";
            Check("two addresses save", two.Contains("Saved."));
            var after = await portal.GetStringAsync("/portal/healthcheck");
            Check("...and a fresh visit shows it ON with both addresses",
                  SwitchTicked(after) && after.Contains("value=\"reports@testing.local; office@testing.local\""));
            // Clint's actual fault, the same day: right mail settings, the switch unticked, and "Saved." read as "on".
            var offSaved = await PostCompanyAsync(portal, false, "reports@testing.local") ?? "";
            Check("saved with the switch off, the page says plainly it is still OFF",
                  offSaved.Contains("Saved, but the monthly check is still OFF") && offSaved.Contains("alert-bad") && !SwitchTicked(offSaved));
            Check("...and the switch says it installs nothing by itself",
                  offSaved.Contains("Switch the monthly check on for my company") && offSaved.Contains("This alone installs nothing"));

            // ---- the company switch, from the portal -----------------------------------------
            Console.WriteLine();
            Console.WriteLine("== the company switches the health check on (portal, Health check page) ==");
            tech.Clear();
            var ok = await SetCompanyAsync(portal, true);
            Check("the settings page accepted it", ok);
            var d = await tech.WaitFor(m1.DeviceId, x => x.HealthCheckAvailable, 5000);
            Check("every console hears the company switch at once", d is { HealthCheckAvailable: true },
                  d == null ? "no broadcast carrying it" : "");
            Check("...but Zenith is not ticked, so this machine still does not want it", d is { HealthCheckWanted: false });

            // ---- the customer's tick ---------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== Zenith is ticked on its client page ==");
            m1Agent.Clear(); tech.Clear();
            await PostClientAsync(portal, zenith, "HealthCheck", new() { ["on"] = "true" });
            var on = await m1Agent.NextWanted(5000);
            Check("the agent is told ON", on is { On: true }, on == null ? "nothing arrived" : $"on={on.On}");
            d = await tech.WaitFor(m1.DeviceId, x => x.HealthCheckWanted, 5000);
            Check("the console shows the customer's tick and what it comes to",
                  d is { ClientHealthCheck: true, HealthCheckWanted: true, HealthCheckMode: HealthCheckModes.AsClient });
            Check("...and nothing claims it is INSTALLED before the machine says so", d is { HealthCheckInstalled: null },
                  d == null ? "" : $"installed={d.HealthCheckInstalled?.ToString() ?? "unknown"}");

            // The machine answers.
            tech.Clear();
            m1Agent.Conn.Send(new HealthCheckStatusMsg { Installed = true, Note = "installed" });
            d = await tech.WaitFor(m1.DeviceId, x => x.HealthCheckInstalled == true, 5000);
            Check("what the agent reports reaches the console", d is { HealthCheckInstalled: true },
                  d == null ? "no broadcast" : HealthCheckModes.Describe(d.HealthCheckAvailable, d.HealthCheckWanted, d.HealthCheckInstalled, d.HealthCheckNote, d.AgentOnline));
            var page = await portal.GetStringAsync($"/portal/clients/{zenith}");
            Check("the portal's client page says so from the record", page.Contains("On - installed on the machine"),
                  "the database is the portal's copy");
            Check("...with the tick box shown ticked", page.Contains("Monthly health check") && System.Text.RegularExpressions.Regex.IsMatch(page, "type=\"checkbox\" checked=\"checked\" onchange"));

            // ---- the override, from the console's right-click --------------------------------
            Console.WriteLine();
            Console.WriteLine("== one machine set OFF from the console, while its customer stays ticked ==");
            m1Agent.Clear();
            d = await tech.Act(new DeviceHealthCheckMsg { DeviceId = m1.DeviceId, Mode = HealthCheckModes.Off }, m1.DeviceId,
                               x => x.HealthCheckMode == HealthCheckModes.Off);
            var off = await m1Agent.NextWanted(5000);
            Check("the agent is told OFF", off is { On: false }, off == null ? "nothing arrived" : $"on={off.On}");
            Check("the console shows the override, and the customer still ticked", d is { HealthCheckMode: HealthCheckModes.Off, ClientHealthCheck: true, HealthCheckWanted: false });

            m1Agent.Clear();
            d = await tech.Act(new DeviceHealthCheckMsg { DeviceId = m1.DeviceId, Mode = HealthCheckModes.AsClient }, m1.DeviceId,
                               x => x.HealthCheckMode == HealthCheckModes.AsClient);
            on = await m1Agent.NextWanted(5000);
            Check("set back to 'as the customer', the agent is told ON again", on is { On: true });

            // ---- following the customer means following a NEW customer too ------------------
            Console.WriteLine();
            Console.WriteLine("== the machine is moved to Acme, which has no health check ==");
            m1Agent.Clear();
            await tech.Act(new DeviceLinkMsg { DeviceId = m1.DeviceId, ClientId = acme }, m1.DeviceId, x => x.ClientId == acme);
            off = await m1Agent.NextWanted(5000);
            Check("it goes with its customer: the agent is told OFF", off is { On: false }, off == null ? "nothing arrived" : $"on={off.On}");

            // ---- an unlinked machine switched on by itself ---------------------------------
            Console.WriteLine();
            Console.WriteLine("== a machine nobody has linked, switched ON for itself ==");
            var m2 = NewMachine("HC-LOOSE-PC");
            var m2Agent = await ConnectAsync(port, tenantKey, m2, DeviceRole.Agent);
            var m2First = await m2Agent.NextWanted(8000);
            Check("an unlinked machine starts OFF - it has no customer to follow", m2First is { On: false });
            m2Agent.Clear();
            await tech.Act(new DeviceHealthCheckMsg { DeviceId = m2.DeviceId, Mode = HealthCheckModes.On }, m2.DeviceId,
                           x => x.HealthCheckMode == HealthCheckModes.On);
            var m2On = await m2Agent.NextWanted(5000);
            Check("the override works without a customer", m2On is { On: true });

            // ---- a machine that was away catches up ----------------------------------------
            Console.WriteLine();
            Console.WriteLine("== the agent drops, the setting changes, the agent comes back ==");
            await m2Agent.Conn.CloseAsync("probe away");
            await Task.Delay(500);
            await tech.Act(new DeviceHealthCheckMsg { DeviceId = m2.DeviceId, Mode = HealthCheckModes.Off }, m2.DeviceId,
                           x => x.HealthCheckMode == HealthCheckModes.Off);
            m2Agent = await ConnectAsync(port, tenantKey, m2, DeviceRole.Agent);
            var back = await m2Agent.NextWanted(8000);
            Check("it is told the CURRENT answer when it comes back", back is { On: false },
                  back == null ? "nothing arrived" : $"on={back.On}");
            await tech.Act(new DeviceHealthCheckMsg { DeviceId = m2.DeviceId, Mode = HealthCheckModes.On }, m2.DeviceId,
                           x => x.HealthCheckMode == HealthCheckModes.On);

            // ---- a reinstall with a new id must never be told "off" first ------------------
            Console.WriteLine();
            Console.WriteLine("== the same machine reinstalled: new device id, same hardware ==");
            var hw = "hc-wire-hardware-" + Guid.NewGuid().ToString("N")[..8];
            var m3 = NewMachine("HC-REINSTALL-PC", hw);
            var m3Agent = await ConnectAsync(port, tenantKey, m3, DeviceRole.Agent);
            await m3Agent.NextWanted(8000);
            await tech.Act(new DeviceHealthCheckMsg { DeviceId = m3.DeviceId, Mode = HealthCheckModes.On }, m3.DeviceId,
                           x => x.HealthCheckMode == HealthCheckModes.On);
            await m3Agent.Conn.CloseAsync("reinstalling");
            await Task.Delay(500);
            var m3b = NewMachine("HC-REINSTALL-PC", hw);   // a reinstall issues a new id and key
            var m3bAgent = await ConnectAsync(port, tenantKey, m3b, DeviceRole.Agent);
            await Task.Delay(2500);
            var told = m3bAgent.AllWanted();
            Check("the reinstalled machine is told something", told.Count > 0);
            Check("and it is NEVER told 'off' - not even for a moment before it is recognised",
                  told.Count > 0 && told.All(w => w.On),
                  "sequence: " + string.Join(", ", told.Select(w => w.On ? "on" : "OFF")));

            // ---- only the agent may say what is installed ----------------------------------
            Console.WriteLine();
            Console.WriteLine("== a tray app claiming the health check is installed ==");
            var m4 = NewMachine("HC-LIAR-PC");
            await using var m4Client = await ConnectAsync(port, tenantKey, m4, DeviceRole.Client);
            await Task.Delay(800);
            tech.Clear();
            m4Client.Conn.Send(new HealthCheckStatusMsg { Installed = true, Note = "installed" });
            var liar = await tech.WaitFor(m4.DeviceId, x => x.HealthCheckInstalled == true, 2000);
            Check("is ignored - only the agent can see a SYSTEM scheduled task", liar == null);

            // ---- the question the monthly run asks ------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== what the monthly run is told (/api/healthcheck/context) ==");
            var ctx1 = await ContextAsync(http, m1);   // at Acme, as the customer: not wanted
            var ctx2 = await ContextAsync(http, m2);   // on for itself: wanted
            Check("it carries 'wanted' for THIS machine", ctx1 != null && ctx1.Value.TryGetProperty("wanted", out _),
                  ctx1 == null ? "no answer" : ctx1.Value.GetRawText());
            Check("a machine whose customer has none is told false - the run then removes its own schedule",
                  ctx1 != null && ctx1.Value.GetProperty("wanted").GetBoolean() == false);
            Check("a machine switched on for itself is told true",
                  ctx2 != null && ctx2.Value.GetProperty("wanted").GetBoolean() == true);
            var bad = await http.GetAsync($"/api/healthcheck/context?deviceId={m1.DeviceId}&deviceKey=wrong");
            Check("a wrong device key gets nothing", (int)bad.StatusCode == 401, $"HTTP {(int)bad.StatusCode}");

            // To LOOK at it: HCWIRE_HOLD=1 stops here with the platform up and the machines still
            // connected, Zenith ticked and one of its machines reporting installed.
            if (Environment.GetEnvironmentVariable("HCWIRE_HOLD") == "1")
            {
                await tech.Act(new DeviceLinkMsg { DeviceId = m1.DeviceId, ClientId = zenith }, m1.DeviceId, x => x.ClientId == zenith);
                await tech.Act(new DeviceLinkMsg { DeviceId = m2.DeviceId, ClientId = zenith }, m2.DeviceId, x => x.ClientId == zenith);
                Console.WriteLine();
                Console.WriteLine($"HOLDING: http://127.0.0.1:{port}/portal/clients/{zenith}  as {Admin} / {Pw}. Stop this process to finish.");
                await Task.Delay(Timeout.Infinite);
            }

            // ---- the company switch off removes it everywhere -------------------------------
            Console.WriteLine();
            Console.WriteLine("== the company switches it off ==");
            m2Agent.Clear(); m3bAgent.Clear();
            await SetCompanyAsync(portal, false);
            var m2Off = await m2Agent.NextWanted(5000);
            var m3Off = await m3bAgent.NextWanted(5000);
            Check("a machine switched ON for itself is told OFF", m2Off is { On: false }, m2Off == null ? "nothing arrived" : "");
            Check("and so is every other one", m3Off is { On: false }, m3Off == null ? "nothing arrived" : "");
            var ctx3 = await ContextAsync(http, m2);
            Check("and the monthly run is told false too", ctx3 != null && ctx3.Value.GetProperty("wanted").GetBoolean() == false);

            await m1Agent.DisposeAsync(); await m2Agent.DisposeAsync(); await m3bAgent.DisposeAsync();
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

    // ---- the rule, asked directly --------------------------------------------------------------

    private static void TheRule()
    {
        Console.WriteLine("== the rule (HealthCheckModes.Wanted), every combination ==");
        foreach (var company in new[] { false, true })
        foreach (var mode in new[] { HealthCheckModes.AsClient, HealthCheckModes.On, HealthCheckModes.Off })
        foreach (var ticked in new[] { false, true })
        {
            bool expect = company && (mode == HealthCheckModes.On || (mode == HealthCheckModes.AsClient && ticked));
            bool got = HealthCheckModes.Wanted(company, mode, ticked);
            Check($"company {(company ? "on " : "off")}, machine {Name(mode),-9}, customer {(ticked ? "ticked  " : "unticked")} -> {(expect ? "wanted" : "not wanted")}", got == expect);
        }
        Check("an unrecognised mode is read as 'as the customer', never as on",
              HealthCheckModes.Wanted(true, 7, false) == false && HealthCheckModes.Normalise(7) == HealthCheckModes.AsClient);

        Console.WriteLine();
        Console.WriteLine("== the words (HealthCheckModes.Describe) - decision AND what the machine said ==");
        Check("wanted, never reported: does NOT say installed",
              !HealthCheckModes.Describe(true, true, null, "", true).Contains("installed on"),
              HealthCheckModes.Describe(true, true, null, "", true));
        Check("wanted, no agent connected: says the service has to connect",
              HealthCheckModes.Describe(true, true, null, "", false).Contains("background service"),
              HealthCheckModes.Describe(true, true, null, "", false));
        Check("wanted and reported: installed",
              HealthCheckModes.Describe(true, true, true, "installed", true) == "On - installed on the machine");
        Check("wanted and failed: the machine's own reason",
              HealthCheckModes.Describe(true, true, false, "could not install it (exit 1)", true).Contains("exit 1"));
        Check("not wanted but still installed: says it is waiting to be removed",
              HealthCheckModes.Describe(true, false, true, "installed", true).Contains("remove"),
              HealthCheckModes.Describe(true, false, true, "installed", true));
        Check("somebody else's health check is named as left alone",
              HealthCheckModes.Describe(true, false, false, "a health check not set up by this support app is on the machine, and was left alone", true).Contains("left alone"));
        Check("company off: says it is the company's switch",
              HealthCheckModes.Describe(false, false, null, "", true).Contains("your company"));
        Check("the 'as the customer' choice names the customer and what it comes to",
              HealthCheckModes.AsClientLabel(3, "Zenith Ltd", true) == "As Zenith Ltd (on)");
        Check("an unlinked machine's choice says there is nobody to follow, not a blank",
              HealthCheckModes.AsClientLabel(0, "", false) == "As its customer (none linked, so off)");
    }

    private static string Name(int mode) => mode switch { HealthCheckModes.On => "on", HealthCheckModes.Off => "off", _ => "as client" };

    // ---- machines on the wire ----------------------------------------------------------------

    private sealed record Machine(string DeviceId, string DeviceKey, string Name, string HardwareId);
    private static Machine NewMachine(string name, string hardwareId = "") =>
        new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), name, hardwareId);

    private sealed class Device : IAsyncDisposable
    {
        public WsConnection Conn = null!;
        private readonly List<HealthCheckWantedMsg> _wanted = new();
        private int _read;
        public void Add(HealthCheckWantedMsg m) { lock (_wanted) _wanted.Add(m); }
        public void Clear() { lock (_wanted) _read = _wanted.Count; }
        public List<HealthCheckWantedMsg> AllWanted() { lock (_wanted) return _wanted.ToList(); }
        /// <summary>The next message not yet read, or null. Later ones win if several are waiting.</summary>
        public async Task<HealthCheckWantedMsg?> NextWanted(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                bool any; lock (_wanted) any = _wanted.Count > _read;
                if (any)
                {
                    // A moment for anything sent straight after it; the latest is the answer.
                    await Task.Delay(200);
                    lock (_wanted) { _read = _wanted.Count; return _wanted[^1]; }
                }
                await Task.Delay(50);
            }
            return null;
        }
        public ValueTask DisposeAsync() => Conn.DisposeAsync();
    }

    private static async Task<Device> ConnectAsync(int port, string tenantKey, Machine m, DeviceRole role)
    {
        var dev = new Device();
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/device"), CancellationToken.None);
        dev.Conn = new WsConnection(cws);
        dev.Conn.OnMessage += msg => { if (msg is HealthCheckWantedMsg w) dev.Add(w); };
        _ = dev.Conn.RunAsync(CancellationToken.None);
        dev.Conn.Send(new HelloMsg
        {
            Role = role, DeviceId = m.DeviceId, DeviceKey = m.DeviceKey, MachineName = m.Name,
            UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey,
            HardwareId = m.HardwareId
        });
        // Long enough for the (deliberately slowed) hub to record the machine before anything is
        // done to it. Messages that arrive meanwhile are still collected.
        await Task.Delay(2200);
        return dev;
    }

    private static async Task<JsonElement?> ContextAsync(HttpClient http, Machine m)
    {
        var r = await http.GetAsync($"/api/healthcheck/context?deviceId={m.DeviceId}&deviceKey={m.DeviceKey}");
        if (!r.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ---- a technician socket -----------------------------------------------------------------

    private sealed class Tech : IAsyncDisposable
    {
        public WsConnection Conn = null!;
        private readonly List<DeviceInfo> _updates = new();
        public void Add(DeviceInfo d) { lock (_updates) _updates.Add(d); }
        public void Clear() { lock (_updates) _updates.Clear(); }
        public ValueTask DisposeAsync() => Conn.DisposeAsync();

        public async Task<DeviceInfo?> WaitFor(string deviceId, Func<DeviceInfo, bool> ok, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                lock (_updates)
                {
                    var hit = _updates.LastOrDefault(x => x.DeviceId == deviceId && ok(x));
                    if (hit != null) return hit;
                }
                await Task.Delay(50);
            }
            return null;
        }

        public async Task<DeviceInfo?> Act(Msg m, string deviceId, Func<DeviceInfo, bool>? ok = null)
        {
            Clear();
            Conn.Send(m);
            return await WaitFor(deviceId, ok ?? (_ => true), 5000);
        }
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
        t.Conn.OnMessage += m => { if (m is DeviceUpdateMsg du) t.Add(du.Device); };
        _ = t.Conn.RunAsync(CancellationToken.None);
        await Task.Delay(400);
        return t;
    }

    // ---- the portal, over HTTP with a real cookie ---------------------------------------------

    private static async Task<HttpClient> PortalClientAsync(int port, string email)
    {
        var jar = new System.Net.CookieContainer();
        var h = new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true };
        var c = new HttpClient(h) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var page = await c.GetStringAsync("/login");
        await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["Password"] = Pw, ["__RequestVerificationToken"] = Token(page) }));
        return c;
    }

    private static string Token(string page) =>
        Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";

    private static async Task<bool> SetCompanyAsync(HttpClient c, bool on)
    {
        var body = await PostCompanyAsync(c, on, "reports@testing.local");
        return body != null && body.Contains("Saved.");
    }

    /// <summary>The page as it comes back from a save, or null for a refused request.</summary>
    private static async Task<string?> PostCompanyAsync(HttpClient c, bool on, string emailTo)
    {
        var page = await c.GetStringAsync("/portal/healthcheck");
        var form = new Dictionary<string, string>
        {
            ["healthCheckEmailTo"] = emailTo, ["healthCheckDayOfMonth"] = "0",
            ["healthCheckRunMaintenance"] = "true", ["smtpHost"] = "smtp.testing.local", ["smtpPort"] = "587",
            ["smtpUseSsl"] = "true", ["__RequestVerificationToken"] = Token(page)
        };
        if (on) form["healthCheckEnabled"] = "true";
        var r = await c.PostAsync("/portal/healthcheck", new FormUrlEncodedContent(form));
        return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
    }

    private static bool SwitchTicked(string page) =>
        System.Text.RegularExpressions.Regex.IsMatch(page, "name=\"healthCheckEnabled\" value=\"true\" checked=\"checked\"");

    private static async Task PostClientAsync(HttpClient c, int clientId, string handler, Dictionary<string, string> fields)
    {
        var page = await c.GetStringAsync($"/portal/clients/{clientId}");
        fields["id"] = clientId.ToString();
        fields["__RequestVerificationToken"] = Token(page);
        await c.PostAsync($"/portal/clients/{clientId}?handler={handler}", new FormUrlEncodedContent(fields));
    }

    private static async Task<int> CreateClientAsync(HttpClient c, string name)
    {
        try
        {
            var page = await c.GetStringAsync("/portal/clients");
            await c.PostAsync("/portal/clients?handler=Add", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["name"] = name, ["__RequestVerificationToken"] = Token(page) }));
            // Its id, which the console's messages name it by, from the type-ahead: the page's own links name a client by
            // its number in the company now, never its id (HANDOVER §159).
            var found = System.Text.Json.JsonDocument.Parse(await c.GetStringAsync("/api/portal/clients?term=" + Uri.EscapeDataString(name))).RootElement;
            return found.EnumerateArray().Where(x => x.GetProperty("name").GetString() == name).Select(x => x.GetProperty("id").GetInt32()).DefaultIfEmpty(0).Max();
        }
        catch { return 0; }
    }

    private static string? Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        var j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? null : s[i..j];
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
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
        psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        // A slow database, on purpose: the reinstall check below is only a check if the hub has a
        // moment in which a connected machine is not yet recognised. See PersistDeviceAsync.
        psi.Environment["RSP_Testing__PersistDeviceDelayMs"] = "1500";
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
