using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// controlcheck — the control dashboard's servers (2026-10-13, docs/NEXT-SESSION-CONTROL.md stage B, HANDOVER §133).
//
// Clint: "an easy to read dashboard that shows all the servers and their specs ... how many tenants on each, how
// many users per tenant and ... how many of those are technicians ... when we are nearing the recommended capacity",
// and "a sign in once into the servers that the dashboard remembers". TWO real platforms: za1, the dashboard, and
// za2, a second server it watches through za2's server key. Readings are pinned (Testing:Readings) so the laptop's
// own memory cannot turn a server amber; capacity is driven by the Servers page's own figure instead.
//
//   THE KEY        za2's /api/control answers only its key; wrong keys are refused, then the address is held off.
//   ADDING         a wrong key, and plain http to a public address, are refused at the door; the right key adds it.
//   THE DASHBOARD  a card per server with its specs and five readings; every company with people, technicians
//                  (ticked Technician) and machines, on whichever server it is.
//   ACTING THERE   approving and suspending a company on za2 from za1: done ON za2 (its machines disconnected, its
//                  owners emailed), logged on za1 by name with za2's name; a Manager may, a Manager may not add a server.
//   CAPACITY       amber at 70%, red at 85%; each emailed ONCE to Full admins and Owners after holding two sweeps;
//                  back to OK is logged; a server that stops answering is shown and emailed too.
//
//   dotnet run -c Release --project tools/controlcheck/controlcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class ControlCheck
{
    private static int _pass, _fail;
    private static readonly FakeSmtp Mail = new();
    private static string _proj = "";
    private static readonly string AdminKey = Hex(24), KeyZa1 = Hex(32), KeyZa2 = Hex(32);
    private const string Clint = "clint@ctl.test", Mandy = "mandy@ctl.test", Pw = "AdminChose!2026";
    private static Server _za1 = null!, _za2 = null!;
    private static string _base = "";

    private sealed class Server
    {
        public string Name = "", DataDir = "", Base = "", Key = "";
        public int Port;
        public Process? Proc;
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Mail.Start();
        var stamp = DateTime.Now.ToString("HHmmss");
        _za1 = new Server { Name = "za1", DataDir = Path.Combine(Path.GetTempPath(), $"rs-controlcheck-za1-{stamp}"), Port = FreePort(), Key = KeyZa1 };
        _za2 = new Server { Name = "za2", DataDir = Path.Combine(Path.GetTempPath(), $"rs-controlcheck-za2-{stamp}"), Port = FreePort(), Key = KeyZa2 };
        foreach (var s in new[] { _za1, _za2 }) { Directory.CreateDirectory(s.DataDir); s.Base = $"http://127.0.0.1:{s.Port}"; }
        _base = _za1.Base;
        Console.WriteLine("controlcheck — the control dashboard's servers");
        Console.WriteLine($"za1 (the dashboard) on {_za1.Port}, za2 on {_za2.Port}, a mail server on {Mail.Port}");

        string southKey;
        try
        {
            var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var buildOut = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }
            foreach (var s in new[] { _za1, _za2 })
            {
                var mailSet = await Cli(s, "platform-mail", "set", "--host", "127.0.0.1", "--port", Mail.Port.ToString(), "--nossl",
                    "--from", $"noreply@{s.Name}.test", "--from-name", "The Remote Support Program");
                if (!mailSet.Contains("Saved")) { Console.WriteLine($"Could not set {s.Name}'s mailbox:\n" + mailSet); return 1; }
            }
            await Cli(_za1, "create-tenant", "North Co", "north-co", "10");
            await Cli(_za1, "set-brand", "north-co", "status", "Active");
            await Cli(_za1, "add-tech", "north-co", "nora@north.test", "Bootstrap!2026", "Nora North", "--owner", "--technician");
            var made = await Cli(_za2, "create-tenant", "South Co", "south-co", "10");
            southKey = Regex.Match(made, @"Tenant key:\s*(\S+)").Groups[1].Value;
            await Cli(_za2, "set-brand", "south-co", "status", "Active");
            await Cli(_za2, "add-tech", "south-co", "sam@south.test", "Bootstrap!2026", "Sam South", "--owner", "--admin");
            await Cli(_za2, "add-tech", "south-co", "tia@south.test", "Bootstrap!2026", "Tia Tech", "--technician");
            await Cli(_za2, "add-tech", "south-co", "bo@south.test", "Bootstrap!2026", "Bo Books", "--billing");
            await Cli(_za2, "create-tenant", "Pending Co", "pending-co", "10");
            await Cli(_za2, "set-brand", "pending-co", "status", "Pending");
            await Cli(_za2, "add-tech", "pending-co", "pat@pending.test", "Bootstrap!2026", "Pat Pending", "--owner", "--admin");
            if (southKey.Length == 0) { Console.WriteLine("Could not make South Co:\n" + made); return 1; }
        }
        catch (Exception ex) { Console.WriteLine("Could not run the platform's command line: " + ex.Message); return 1; }

        _za1.Proc = StartPlatform(_za1); _za2.Proc = StartPlatform(_za2);
        try
        {
            if (!await WaitForPort(_za1.Port, 90000) || !await WaitForPort(_za2.Port, 90000)) { Console.WriteLine("A platform never started listening."); return 1; }

            // ---- an Owner and a Manager on za1 ---------------------------------------------------
            var key = NewBrowser(_za1);
            var step = await Post(key, "/owner/login", await Html(key, "/owner/login"), ("Key", AdminKey));
            var keySecret = Regex.Match(step, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            await PostFollow(key, "/owner/login", step, ("UseKey", "true"), ("Pending", Field(step, "Pending")), ("Code", Totp.Now(keySecret)));
            var clint = await AddAndSetUp(key, "Clint Owner", Clint, 4);
            var mandy = clint == null ? null : await AddAndSetUp(clint, "Mandy Manager", Mandy, 2);
            if (clint == null || mandy == null) { Console.WriteLine("Could not set up the admins on za1."); return Done(); }

            // two machines of South Co's, online on za2
            var m1 = await FakeMachine.ConnectAsync(_za2, southKey, "south-pc-1");
            var m2 = await FakeMachine.ConnectAsync(_za2, southKey, "south-pc-2");

            // ---- the key ----------------------------------------------------------------------------
            Head("za2's door: its server key and nothing else");
            using var raw = new HttpClient { BaseAddress = new Uri(_za2.Base), Timeout = TimeSpan.FromSeconds(30) };
            Check("no key: refused", (await raw.GetAsync("/api/control/status")).StatusCode == HttpStatusCode.Unauthorized);
            Check("the right key: its report", (await Status(raw, KeyZa2)).status == HttpStatusCode.OK);
            Check("a wrong key: refused", (await Status(raw, KeyZa1)).status == HttpStatusCode.Unauthorized);
            Check("an admin's password is not a key", (await Status(raw, Pw)).status == HttpStatusCode.Unauthorized);
            var (_, report) = await Status(raw, KeyZa2);
            Check("the report has za2's own name and its companies", report.Contains("\"name\":\"za2\"") && report.Contains("South Co") && report.Contains("Pending Co"));
            Check("and never a password, key or secret", !Regex.IsMatch(report, "(?i)passw|tenantkey|totp|smtp|salt"));

            // ---- the dashboard, one server --------------------------------------------------------
            Head("the dashboard with one server");
            var dash = await Html(clint, "/owner");
            Check("signing in lands on the dashboard", dash.Contains("<h1>Dashboard</h1>"));
            Check("one card, this server, with its specs", Cards(dash).Count == 1 && Card(dash, "za1").Contains("cores") && Card(dash, "za1").Contains("GB memory"));
            Check("five readings on it", Regex.Matches(Card(dash, "za1"), "data-measure=").Count == 5);
            Check("pinned readings, so it is OK", Card(dash, "za1").Contains("data-level=\"Green\""), Level(dash, "za1"));

            // ---- adding za2 -----------------------------------------------------------------------
            Head("adding za2");
            var servers = await Html(clint, "/owner/servers");
            var wrongKey = await PostWithToken(clint, "/owner/servers?handler=Add", servers, ("name", "za2"), ("address", _za2.Base), ("key", KeyZa1));
            Check("a wrong key is refused at the door", wrongKey.Contains("Not added") && wrongKey.Contains("refused the server key"), Snip(wrongKey, "alert-bad"));
            var bare = await PostWithToken(clint, "/owner/servers?handler=Add", servers, ("name", "x"), ("address", "http://8.8.8.8"), ("key", KeyZa2));
            Check("plain http to a public address is refused", bare.Contains("without encryption"), Snip(bare, "alert-bad"));
            var mAdd = await PostWithToken(mandy, "/owner/servers?handler=Add", await Html(mandy, "/owner/servers"), ("name", "za2"), ("address", _za2.Base), ("key", KeyZa2));
            Check("a Manager cannot add a server", mAdd.Contains("Only a Full admin or an Owner can add a server"));
            var added = await PostWithToken(clint, "/owner/servers?handler=Add", servers, ("name", ""), ("address", _za2.Base), ("key", KeyZa2));
            Check("the right key adds it, under the name it gives itself", added.Contains("Server added") && added.Contains("data-server=\"za2\""), Snip(added, "alert"));
            var again = await PostWithToken(clint, "/owner/servers?handler=Add", servers, ("name", "za2 again"), ("address", _za2.Base), ("key", KeyZa2));
            Check("and not twice", again.Contains("already on the dashboard"));

            // ---- the dashboard, two servers -------------------------------------------------------
            Head("the dashboard with two servers");
            dash = await Html(clint, "/owner");
            Check("two cards", Cards(dash).Count == 2, string.Join(", ", Cards(dash)));
            var za2Card = Card(dash, "za2");
            Check("za2's card: South Africa, its specs, PostgreSQL or SQLite", za2Card.Contains("South Africa") && za2Card.Contains("cores") && Regex.IsMatch(za2Card, "SQLite|PostgreSQL"));
            Check("za2's companies: one using it (Pending Co is not)", Count(za2Card, "companies") == "1", Count(za2Card, "companies"));
            Check("its people: three at South Co (Pending Co's one is not using it either — counted, but…)", Count(za2Card, "people") == "4", Count(za2Card, "people"));
            Check("its technicians: those ticked Technician", Count(za2Card, "technicians") == "1", Count(za2Card, "technicians"));
            Check("its machines", Count(za2Card, "machines") == "2", Count(za2Card, "machines"));
            var south = HeadRow(dash, "south-co");
            Check("South Co in the company table: 3 people, 1 technician, 2 console users, 2 machines, 2 online",
                Regex.IsMatch(south, "<td>3</td><td>1</td><td>2</td><td>2</td><td>2</td>"), Regex.Replace(south, "\\s+", " "));
            Check("and it says which server", south.Contains("<td>za2</td>"));
            Check("North Co too, on za1", HeadRow(dash, "north-co").Contains("<td>za1</td>"));
            Check("the totals count both servers", Regex.IsMatch(dash, "<div class=\"n\">2</div><div class=\"l\">Companies using it \\(3 in all\\)"));
            Check("the pending signup is flagged", dash.Contains("1 signup waiting for approval"));

            // ---- capacity --------------------------------------------------------------------------
            Head("capacity: amber, red, back, each told once");
            // South Co plans 3 per console user and 2 per machine: 3×2 + 2×2 = 10 connections.
            var sid = ServerId(await Html(clint, "/owner/servers"), "za2");
            var before = Mail.All.LastOrDefault();
            await Save(clint, sid, "za2", true, 13);   // 10 of 13 = 77%: amber
            dash = await Html(clint, "/owner");
            Check("10 planned of 13: amber, and the card says why", Level(dash, "za2") == "Amber" && Card(dash, "za2").Contains("Getting full (planned)"), Level(dash, "za2"));
            Check("with the advice: a bigger size, or a new server for South Africa", Card(dash, "za2").Contains("a bigger size for this server, or a new server for South Africa"));
            var amber = await Mail.WaitFor(Clint, "za2 is nearing capacity", before, seconds: 15);
            Check("the Owner is emailed once it has held two sweeps", amber != null, amber?.Subject ?? "nothing");
            Check("and the Manager is not", Mail.All.SkipWhile(m => m != before).All(m => !m.To.Contains(Mandy) || !m.Subject.Contains("za2")));
            await Task.Delay(6000);
            Check("once — not again every sweep", Mail.All.SkipWhile(m => m != before).Count(m => m.Subject.Contains("za2 is nearing capacity")) == 1);
            await Save(clint, sid, "za2", true, 11);   // 10 of 11 = 91%: red
            var red = await Mail.WaitFor(Clint, "za2 is at capacity", amber, seconds: 15);
            Check("red is news again: emailed", red != null && red.Body.Contains("New companies should go elsewhere"), red?.Subject ?? "nothing");
            await Save(clint, sid, "za2", true, 0);    // from its memory: tens of thousands
            await Task.Delay(7000);
            var log = WebUtility.HtmlDecode(await Html(clint, "/owner/log"));
            Check("back to OK is logged", log.Contains("Server za2 is back to OK"));
            Check("the emails are logged", log.Contains("Server za2: Nearing capacity — emailed " + Clint) && log.Contains("Server za2: At capacity — emailed"));
            Check("and the changes to the server, by name", log.Contains("Clint Owner") && log.Contains("Server za2: capacity 13 connections"));
            await Save(clint, sid, "za2", false, 0);
            Check("not taking new companies: the card says so", Card(await Html(clint, "/owner"), "za2").Contains("NOT taking new companies"));

            // ---- acting on za2 from za1 ---------------------------------------------------------------
            Head("approving and suspending a company on za2, from za1");
            var appr = await Html(mandy, "/owner/approvals");
            Check("Approvals on za1 lists za2's signup, with za2's name", Regex.IsMatch(appr, "data-slug=\"pending-co\"[\\s\\S]*?<td>za2</td>"));
            var pendingId = Regex.Match(Section(appr, "data-slug=\"pending-co\""), "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;
            var approved = await PostWithToken(mandy, "/owner/approvals?handler=Act", appr, ("server", sid), ("id", pendingId), ("act", "approve"));
            Check("a Manager approves it", approved.Contains("za2: Pending Co approved"), Snip(approved, "alert"));
            Check("and it is approved ON za2", (await Cli(_za2, "list-tenants")).Split('\n').Any(l => l.Contains("pending-co") && l.Contains("Trial")));
            var tenants = await Html(mandy, "/owner/tenants");
            var southId = Regex.Match(Section(tenants, "data-slug=\"south-co\""), "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;
            Check("All companies groups them by server", tenants.Contains("data-server=\"za1\"") && tenants.Contains("data-server=\"za2\""));
            before = Mail.All.LastOrDefault();
            var susp = await PostWithToken(mandy, "/owner/tenants?handler=Act", tenants, ("server", sid), ("id", southId), ("act", "status"), ("status", "Suspended"));
            Check("a Manager suspends South Co on za2", susp.Contains("za2: South Co is now Suspended"), Snip(susp, "alert"));
            Check("its machines on za2 are disconnected at once", await Closed(m1) && await Closed(m2));
            Check("its owner is emailed — by za2", (await Mail.WaitFor("sam@south.test", "suspended", before)) is { } n && n.From.Contains("za2"));
            log = WebUtility.HtmlDecode(await Html(clint, "/owner/log"));
            Check("za1's log has it by name, with za2's name", Regex.IsMatch(log, "<td>Mandy Manager</td><td>Status → Suspended[^<]*</td><td>South Co</td><td class=\"muted\">za2</td>"));
            var restore = await PostWithToken(mandy, "/owner/tenants?handler=Act", await Html(mandy, "/owner/tenants"), ("server", sid), ("id", southId), ("act", "status"), ("status", "Active"));
            Check("and made Active again", restore.Contains("za2: South Co is now Active"));

            // ---- za2 goes away -------------------------------------------------------------------------
            Head("a server that stops answering");
            before = Mail.All.LastOrDefault();
            try { _za2.Proc!.Kill(entireProcessTree: true); } catch { }
            await Task.Delay(1500);
            dash = await Html(clint, "/owner");
            Check("its card says Not answering, and why", Level(dash, "za2") == "Unreachable" && Card(dash, "za2").Contains("Not answering"), Level(dash, "za2"));
            Check("the rest of the dashboard still works", Card(dash, "za1").Contains("data-measure"));
            var down = await Mail.WaitFor(Clint, "za2 is not answering", before, seconds: 20);
            Check("and the Owner is emailed", down != null, down?.Subject ?? "nothing");
            var rm = await PostWithToken(clint, "/owner/servers?handler=Remove", await Html(clint, "/owner/servers"), ("id", sid));
            Check("it can be taken off the dashboard", rm.Contains("Taken off the dashboard") && !rm.Contains("data-server=\"za2\""));
            var localId = ServerId(await Html(clint, "/owner/servers"), "za1");
            var rmLocal = await PostWithToken(clint, "/owner/servers?handler=Remove", await Html(clint, "/owner/servers"), ("id", localId));
            Check("but never this server", rmLocal.Contains("cannot be removed"));
        }
        catch (Exception ex) { Check("ran to the end", false, ex.ToString()); }
        finally
        {
            foreach (var s in new[] { _za1, _za2 }) try { s.Proc?.Kill(entireProcessTree: true); } catch { }
            Mail.Dispose();
        }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"controlcheck: {_pass} of {_pass + _fail} passed");
        return _fail == 0 ? 0 : 1;
    }

    // ---- reading the dashboard ------------------------------------------------------------------

    private static List<string> Cards(string html) => Regex.Matches(html, "class=\"card server-card\" data-server=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

    private static string Card(string html, string name)
    {
        var i = html.IndexOf($"class=\"card server-card\" data-server=\"{name}\"", StringComparison.Ordinal);
        if (i < 0) return "";
        var next = html.IndexOf("class=\"card server-card\"", i + 10, StringComparison.Ordinal);
        var end = html.IndexOf("<h2>Companies</h2>", i, StringComparison.Ordinal);
        var stop = new[] { next, end }.Where(x => x > 0).DefaultIfEmpty(html.Length).Min();
        return WebUtility.HtmlDecode(html[i..stop]);
    }

    private static string Level(string html, string name) => Regex.Match(Card(html, name), "data-level=\"([A-Za-z]+)\"").Groups[1].Value;
    private static string Count(string card, string label) => Regex.Match(card, $"<b>([0-9,]+)</b><span>{label}</span>").Groups[1].Value;

    private static string HeadRow(string html, string slug)
    {
        var t = html.IndexOf("id=\"headcount\"", StringComparison.Ordinal);
        var i = t < 0 ? -1 : html.IndexOf($"data-slug=\"{slug}\"", t, StringComparison.Ordinal);
        if (i < 0) return "";
        return html[i..html.IndexOf("</tr>", i, StringComparison.Ordinal)];
    }

    private static string ServerId(string serversPage, string name)
    {
        var i = serversPage.IndexOf($"data-server=\"{name}\"", StringComparison.Ordinal);
        return i < 0 ? "0" : Regex.Match(serversPage[i..], "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;
    }

    private static async Task Save(HttpClient c, string id, string name, bool takingNew, int capacity)
    {
        var page = await Html(c, "/owner/servers");
        var f = new List<(string, string)> { ("id", id), ("name", name), ("capacity", capacity.ToString()) };
        if (takingNew) f.Add(("takingNew", "true"));
        var r = await PostWithToken(c, "/owner/servers?handler=Save", page, f.ToArray());
        if (!r.Contains("Saved.")) Console.WriteLine("        save failed: " + Snip(r, "alert"));
    }

    private static async Task<(HttpStatusCode status, string body)> Status(HttpClient raw, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/control/status");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var res = await raw.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<bool> Closed(FakeMachine m) => await Task.WhenAny(m.Closed.Task, Task.Delay(5000)) == m.Closed.Task;

    // ---- admins (as admincheck) -------------------------------------------------------------------

    private static async Task<HttpClient?> AddAndSetUp(HttpClient by, string name, string email, int level)
    {
        var before = Mail.All.LastOrDefault();
        var r = await PortalPost(by, "/owner/admins", "Add", ("name", name), ("email", email), ("level", level.ToString()));
        if (!r.Contains("a set-up link has gone")) { Console.WriteLine("        add failed: " + Snip(r, "alert")); return null; }
        var m = await Mail.WaitFor(email, "set up your admin account", before);
        if (m == null) return null;
        var link = Regex.Match(m.Body, @"http://127\.0\.0\.1:\d+/owner/setup\?t=[0-9A-F]+").Value;
        var rel = new Uri(link).PathAndQuery; var t = link[(link.IndexOf("?t=", StringComparison.Ordinal) + 3)..];
        var b = NewBrowser(_za1);
        var chosen = await Post(b, rel, await Html(b, rel), ("T", t), ("Password", Pw), ("Again", Pw));
        var secret = Regex.Match(chosen, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
        var landed = await PostFollow(b, rel, chosen, ("T", t), ("Pending", Field(chosen, "Pending")), ("Code", Totp.Now(secret)));
        return landed == "/owner" ? b : null;
    }

    // ---- a pretend machine -------------------------------------------------------------------------

    private sealed class FakeMachine
    {
        public bool Accepted;
        public string Error = "";
        public readonly TaskCompletionSource<bool> Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<FakeMachine> ConnectAsync(Server s, string tenantKey, string id)
        {
            var f = new FakeMachine();
            var raw = new ClientWebSocket();
            await raw.ConnectAsync(new Uri($"ws://127.0.0.1:{s.Port}/ws/device"), default);
            var c = new WsConnection(raw);
            var stated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.OnMessage += m => { if (m is ErrorMsg e) { f.Error = e.Message; stated.TrySetResult(false); } else stated.TrySetResult(true); };
            c.OnClosed += _ => { f.Closed.TrySetResult(true); stated.TrySetResult(false); };
            _ = c.RunAsync();
            c.Send(new HelloMsg
            {
                Role = DeviceRole.Client, DeviceId = id, DeviceKey = id + "-key", MachineName = id.ToUpperInvariant(), UserName = "FakeUser",
                Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = tenantKey,
                Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
            });
            f.Accepted = await Task.WhenAny(stated.Task, Task.Delay(5000)) == stated.Task && stated.Task.Result;
            return f;
        }
    }

    // ---- the platforms -----------------------------------------------------------------------------

    private static Process StartPlatform(Server s)
    {
        var psi = Psi(s);
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> Cli(Server s, params string[] args)
    {
        var psi = Psi(s);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private static ProcessStartInfo Psi(Server s)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = s.DataDir;
        HarnessDb.Use(psi.Environment, s.DataDir);
        psi.Environment["RSP_Platform__PublicUrl"] = s.Base;
        psi.Environment["RSP_Platform__ServerName"] = s.Name;
        psi.Environment["RSP_Platform__Region"] = "ZA";
        psi.Environment["RSP_Platform__ServerKey"] = s.Key;
        psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
        psi.Environment["RSP_Platform__ControlSweepSeconds"] = "2";
        // Pinned, so this laptop's own memory and processor cannot turn a server amber (ServerReadings).
        psi.Environment["RSP_Testing__Readings__MemoryUsedPercent"] = "30";
        psi.Environment["RSP_Testing__Readings__CpuPercent"] = "5";
        psi.Environment["RSP_Testing__Readings__DiskUsedPercent"] = "40";
        psi.Environment["RSP_Urls"] = s.Base;
        psi.Environment["ASPNETCORE_URLS"] = s.Base;
        return psi;
    }

    private static HttpClient NewBrowser(Server s) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(s.Base), Timeout = TimeSpan.FromSeconds(60) };

    private static async Task<string> PostWithToken(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    private static string Hex(int bytes) => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));

    // ---- from invitecheck: a mail server, forms, small things --------------------------------------
    private static int _port; private static string _dataDir = "";
    private sealed class FakeSmtp : IDisposable
    {
        private TcpListener? _l;
        private readonly List<Message> _mails = new();
        public int Port { get; private set; }
        public int Count { get { lock (_mails) return _mails.Count; } }
        public List<Message> All { get { lock (_mails) return _mails.ToList(); } }

        public void Start()
        {
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
        }

        private async Task Handle(TcpClient c)
        {
            using (c)
            {
                var s = c.GetStream();
                var r = new StreamReader(s, Encoding.UTF8);
                var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                await w.WriteLineAsync("220 invitecheck ESMTP");
                var to = new List<string>();
                while (await r.ReadLineAsync() is { } line)
                {
                    var cmd = line.ToUpperInvariant();
                    if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await w.WriteLineAsync("250 invitecheck");
                    else if (cmd.StartsWith("RCPT TO:")) { to.Add(line[8..].Trim().Trim('<', '>').ToLowerInvariant()); await w.WriteLineAsync("250 OK"); }
                    else if (cmd == "DATA")
                    {
                        await w.WriteLineAsync("354 go ahead");
                        var sb = new StringBuilder();
                        while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                        lock (_mails) _mails.Add(new Message(to.ToList(), sb.ToString()));
                        to.Clear();
                        await w.WriteLineAsync("250 queued");
                    }
                    else if (cmd == "QUIT") { await w.WriteLineAsync("221 bye"); break; }
                    else await w.WriteLineAsync("250 OK");
                }
            }
        }

        public async Task<Message?> WaitFor(string to, string subjectPart, Message? after = null, string? fromBy = null, int seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                List<Message> all; lock (_mails) all = _mails.ToList();
                var start = after == null ? 0 : all.IndexOf(after) + 1;
                var m = all.Skip(start).LastOrDefault(x => x.To.Contains(to) && x.Subject.Contains(subjectPart) && (fromBy == null || x.Body.Contains(fromBy)));
                if (m != null) return m;
                await Task.Delay(100);
            }
            return null;
        }

        public void Dispose() { try { _l?.Stop(); } catch { } }
    }

    /// <summary>One message, read as a mail program would: unfolded headers, decoded body.</summary>
    private sealed class Message
    {
        public List<string> To { get; }
        public string From = "", FromName = "", ReplyTo = "", Subject = "", Body = "";
        public Message(List<string> to, string raw)
        {
            To = to;
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = Regex.Replace(split < 0 ? raw : raw[..split], "\r\n[ \t]+", " ");
            var body = split < 0 ? "" : raw[(split + 4)..];
            string H(string n) => Regex.Match(head, $"^{n}:\\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            From = Words(H("From")); ReplyTo = Words(H("Reply-To")); Subject = Words(H("Subject"));
            FromName = Regex.Match(From, "^\"?([^\"<]+?)\"?\\s*<").Groups[1].Value.Trim();
            var cte = H("Content-Transfer-Encoding").ToLowerInvariant();
            Body = cte == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, "\\s", "")))
                 : cte == "quoted-printable" ? QuotedPrintable(body) : body;
            Body = Body.Replace("\r\n", "\n");
        }


        private static string QuotedPrintable(string s)
        {
            s = s.Replace("=\r\n", "");
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>RFC 2047 encoded words (=?utf-8?B?…?= and =?utf-8?Q?…?=) back to text.</summary>
        private static string Words(string v) => Regex.Replace(v, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
            m.Groups[2].Value.ToUpperInvariant() == "B"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[3].Value))
                : QuotedPrintable(m.Groups[3].Value.Replace('_', ' ')));
    }

    // ---- the browser ----------------------------------------------------------------------

    private static HttpClient NewBrowser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<string> Html(HttpClient c, string path) => await (await c.GetAsync(path)).Content.ReadAsStringAsync();

    private static async Task<string> Post(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return await r.Content.ReadAsStringAsync();
    }

    private static async Task<string> PostFollow(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return r.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
    }

    private static async Task<string> PortalPost(HttpClient c, string page, string handler, params (string k, string v)[] fields)
    {
        var html = await Html(c, page);
        var r = await c.PostAsync($"{page}?handler={handler}", Form(fields.Append(("__RequestVerificationToken", Token(html))).ToArray()));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpClient client, string landed)> PortalSignIn(string email, string password, string code)
    {
        var c = NewBrowser();
        var form = await Html(c, "/login");
        var step = await Post(c, "/login", form, ("Email", email), ("Password", password));
        var landed = await PostFollow(c, "/login", step, ("Mode", Field(step, "Mode")), ("Pending", Field(step, "Pending")), ("Code", code));
        return (c, landed);
    }

    private static async Task<string> Forgot(string email)
    {
        var c = NewBrowser();
        var page = await Html(c, "/forgot");
        return WebUtility.HtmlDecode(await Post(c, "/forgot", page, ("Email", email)));
    }

    private static string Answer(string html) => Regex.Match(html, "<div class=\"alert alert-ok\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;

    private static async Task<List<string>> EnrolByApi(HttpClient api, string email, string password)
    {
        var a = await Api(api, "/api/login", new { email, password });
        var b = await Api(api, "/api/login/enrol", new { pending = a.Str("pending"), code = Totp.Now(a.Str("secret")) });
        return b.Arr("recoveryCodes");
    }

    private sealed record ApiAnswer(HttpStatusCode status, JsonElement doc)
    {
        public string Str(string n) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        public List<string> Arr(string n) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
    }

    private static async Task<ApiAnswer> Api(HttpClient http, string path, object body)
    {
        var r = await http.PostAsJsonAsync(path, body);
        JsonElement doc; try { doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement; } catch { doc = default; }
        return new ApiAnswer(r.StatusCode, doc);
    }

    private sealed class ConsoleSocket
    {
        public string status = "";
        public readonly TaskCompletionSource<bool> state = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> signedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<ConsoleSocket> OpenConsole(string token)
    {
        var s = new ConsoleSocket();
        var cws = new ClientWebSocket();
        cws.Options.CollectHttpResponseDetails = true;
        try
        {
            await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws/tech?token={token}"), CancellationToken.None);
            s.status = "open";
            var conn = new WsConnection(cws);
            conn.OnMessage += m => { if (m is StateMsg) s.state.TrySetResult(true); if (m is SignedOutMsg so) s.signedOut.TrySetResult(so.Reason); };
            _ = conn.RunAsync(CancellationToken.None);
        }
        catch { s.status = ((int)cws.HttpStatusCode).ToString(); }
        return s;
    }

    // ---- small things ----------------------------------------------------------------------

    private static string Rel(string link) => link.Length == 0 ? "/account/setup" : new Uri(link).PathAndQuery;
    private static string Token(string linkOrPage) =>
        linkOrPage.Contains("/account/setup?t=") && !linkOrPage.Contains("<")
            ? linkOrPage[(linkOrPage.IndexOf("?t=", StringComparison.Ordinal) + 3)..]
            : Regex.Match(linkOrPage, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    private static string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    private static string Section(string html, string heading) { var i = html.IndexOf(heading, StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 3000)]; }
    private static string Row(string html, string email) { var i = html.IndexOf("<td>" + email + "</td>", StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 4000)]; }
    private static string Snip(string html, string near) { var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase); return i < 0 ? "" : Regex.Replace(html.Substring(Math.Max(0, i - 60), Math.Min(200, html.Length - Math.Max(0, i - 60))), "\\s+", " "); }
    private static FormUrlEncodedContent Form(params (string k, string v)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

    private static Process StartPlatform()
    {
        var psi = Psi();
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> Cli(params string[] args)
    {
        var psi = Psi();
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    // Two-factor is NOT turned off: this is how a live server behaves.
    private static ProcessStartInfo Psi()
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Platform__PublicUrl"] = _base;
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
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
