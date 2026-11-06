using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Media;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// sessionextras - the privacy screen (1.7) and session recording (1.6), through the server.
//
// Its own platform on port 8096 against a throwaway database, a pretend customer machine (tray
// app and host), and technicians on real sockets. The host's half of the privacy screen and the
// recorder itself are proved on this laptop by tools/privacyprobe and tools/recordcheck; this is
// everything between them: what the ticket says, who is told, who may upload, watch and delete.
//
//   dotnet run -c Debug --project tools/sessionextras/sessionextras.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8096;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";
int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("sessionextras - privacy screen and recording, through the server");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-sessionextras-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
Log("building the platform");
var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
if (b.code != 0) { Console.WriteLine(b.output); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var env = new Dictionary<string, string> { ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http, ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development" };
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[] {
    "create-tenant \"Session Extras\" sx 5", "approve sx",
    "add-tech sx sam@local.test localtest \"Sam Super\" --admin",
    "add-tech sx tina@local.test localtest \"Tina Tech\" --technician" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var server = Process.Start(psi)!;
server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
using var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
bool up = false;
for (int i = 0; i < 60 && !up; i++) { try { up = (await hc.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { await Task.Delay(500); } }
if (!up) { Console.WriteLine("the platform did not come up"); server.Kill(true); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { server.Kill(true); } catch { } }
Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    async Task<List<string>> Notes(int ticket) { await using var db = new PlatformDb(options); return await db.TicketNotes.Where(n => n.TicketId == ticket).OrderBy(n => n.Id).Select(n => n.Body).ToListAsync(); }
    async Task<bool> Note(int ticket, Func<string, bool> f, int ms = 5000)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if ((await Notes(ticket)).Any(f)) return true; await Task.Delay(150); } return false; }

    var sam = await Login("sam@local.test");
    var tina = await Login("tina@local.test");
    var (samConn, samQ) = await TechSocket(sam);

    var machine = new FakeMachine(ws, "sx-device", "sx-key", "SX-PC", tenantKey);
    await machine.ConnectAsync();
    await Task.Delay(700);
    samConn.Send(new ConnectDeviceMsg { DeviceId = "sx-device", Reason = "sessionextras" });
    var ready = await Wait<SessionReadyMsg>(samQ, 15000, s => s.Session.DeviceId == "sx-device");
    if (ready == null || !await machine.WaitForHostAsync(10000)) throw new Exception("no session");
    var sessionId = ready.Session.SessionId;
    int ticket = 0;
    for (int i = 0; i < 40 && ticket == 0; i++) { await using var db = new PlatformDb(options); ticket = await db.Sessions.Where(s => s.SessionId == sessionId).Select(s => s.TicketId ?? 0).FirstOrDefaultAsync(); if (ticket == 0) await Task.Delay(150); }
    Log($"session {sessionId}, ticket #{ticket}");
    var viewer = await Viewer(sessionId, sam);

    // ---- the privacy screen ----
    Console.WriteLine();
    Console.WriteLine("== the privacy screen ==");
    viewer.conn.Send(new PrivacyScreenMsg { On = true });
    Check("the technician's request reaches the host", await WaitFor(() => machine.HostGot.OfType<PrivacyScreenMsg>().Any(p => p.On), 4000));
    machine.HostSend(new PrivacyStateMsg { On = true, Monitors = 2 });
    Check("the host's answer reaches the technician", await WaitFor(() => viewer.q.OfType<PrivacyStateMsg>().Any(p => p.On && p.Monitors == 2), 4000));
    Check("...and the ticket says their screen was blacked out", await Note(ticket, n => n.Contains("Privacy screen on") && n.Contains("SX-PC")));
    var late = await Viewer(sessionId, sam);
    Check("a technician who joins later is told it is on", await WaitFor(() => late.q.OfType<PrivacyStateMsg>().Any(p => p.On), 4000));
    machine.HostSend(new PrivacyStateMsg { On = false });
    Check("off is written on the ticket too", await Note(ticket, n => n == "Privacy screen off."));
    machine.HostSend(new PrivacyStateMsg { On = false, Error = "this version of Windows cannot hide a window from screen capture" });
    Check("a refusal is written with its reason", await Note(ticket, n => n.Contains("could not be turned on") && n.Contains("cannot hide a window")));

    // ---- recording: disclosure ----
    Console.WriteLine();
    Console.WriteLine("== recording: who is told ==");
    viewer.conn.Send(new RecordingStateMsg { On = true, By = "Somebody Else" });
    Check("the person at the machine is told, in their tray app", await WaitFor(() => machine.TrayGot.OfType<RecordingStateMsg>().Any(r => r.On), 4000));
    Check("...named as who really pressed Record, not what the console claimed",
        machine.TrayGot.OfType<RecordingStateMsg>().FirstOrDefault(r => r.On)?.By == "Sam Super", machine.TrayGot.OfType<RecordingStateMsg>().FirstOrDefault()?.By ?? "");
    Check("...and the ticket says so", await Note(ticket, n => n == "Sam Super started recording the session."));
    viewer.conn.Send(new RecordingStateMsg { On = false });
    Check("stopping is told and written as well", await WaitFor(() => machine.TrayGot.OfType<RecordingStateMsg>().Any(r => !r.On), 4000) && await Note(ticket, n => n == "Sam Super stopped recording."));
    Check("the HOST is never sent it — it has nothing to do with recording", !machine.HostGot.OfType<RecordingStateMsg>().Any());

    // ---- recording: the upload ----
    Console.WriteLine();
    Console.WriteLine("== recording: the upload ==");
    var mp4 = Path.Combine(work, "real.mp4");
    using (var rec = ScreenRecorder.Create(mp4, 320, 240, 8))
    using (var bmp = new Bitmap(320, 240))
    {
        for (int i = 0; i < 16; i++) { using (var g = Graphics.FromImage(bmp)) g.Clear(i % 2 == 0 ? Color.Teal : Color.Orange); rec.AddFrame(bmp, TimeSpan.FromSeconds(i / 8.0), TimeSpan.FromSeconds(1 / 8.0)); }
        rec.Finish();
    }
    var (st1, body1) = await Upload(sam, sessionId, File.ReadAllBytes(mp4), 2);
    Check("a real recording is accepted", st1 == HttpStatusCode.OK, $"{(int)st1} {body1}");
    long recId = st1 == HttpStatusCode.OK ? JsonDocument.Parse(body1).RootElement.GetProperty("id").GetInt64() : 0;
    await using (var db = new PlatformDb(options))
    {
        var row = await db.SessionRecordings.FirstOrDefaultAsync(r => r.Id == recId);
        Check("...attached to the session's ticket", row?.TicketId == ticket, $"ticket {row?.TicketId}");
        Check("...and on disk, byte for byte", row != null && File.ReadAllBytes(Path.Combine(dataDir, "recordings", row.TenantId.ToString(), row.FileName)).SequenceEqual(File.ReadAllBytes(mp4)));
    }
    Check("the ticket says it was saved and where to watch it", await Note(ticket, n => n.StartsWith("Recording saved: 2 s") && n.Contains("Recordings")));

    var (st2, body2) = await Upload(sam, sessionId, System.Text.Encoding.UTF8.GetBytes("not a video at all, just words"), 1);
    Check("something that is not an MP4 is refused", st2 == HttpStatusCode.BadRequest && body2.Contains("not an MP4"), $"{(int)st2} {body2}");
    var (st3, _) = await Upload("", sessionId, File.ReadAllBytes(mp4), 1);
    Check("no token, no upload", st3 == HttpStatusCode.Unauthorized, ((int)st3).ToString());
    var (st4, body4) = await Upload(tina, sessionId, File.ReadAllBytes(mp4), 1);
    Check("a technician who was not in the session cannot add to its ticket", st4 == HttpStatusCode.BadRequest && body4.Contains("was in that session"), $"{(int)st4} {body4}");

    // 40 MB: past Kestrel's 30 MB default, which a real session passes in four minutes.
    var big = new byte[40 * 1024 * 1024];
    File.ReadAllBytes(mp4).CopyTo(big, 0);
    var (st5, body5) = await Upload(sam, sessionId, big, 300);
    Check("a 40 MB recording is accepted - the 30 MB default is lifted for this one endpoint", st5 == HttpStatusCode.OK, $"{(int)st5} {body5}");

    // ---- remote printing, both directions (roadmap 1.8, 2026-10-06) ----
    Console.WriteLine();
    Console.WriteLine("== remote printing ==");
    viewer.conn.Send(new RemotePrintMsg { Technician = "Sam Super", On = true });
    Check("Print on my printer reaches the host", await WaitFor(() => machine.HostGot.OfType<RemotePrintMsg>().Any(p => p.On && p.Technician == "Sam Super"), 4000));
    machine.HostSend(new RemotePrintStateMsg { Technician = "Sam Super", On = true, Printer = "Sam Super's printer (SX)" });
    Check("the host's answer — the printer's name on their machine — reaches the technician",
        await WaitFor(() => viewer.q.OfType<RemotePrintStateMsg>().Any(p => p.On && p.Printer == "Sam Super's printer (SX)"), 4000));
    machine.HostSend(new RemotePrintJobMsg { JobId = "j1", Technician = "Sam Super", Document = "Invoice-1042", Path = @"C:\x\job-1.pdf", Bytes = 5000 });
    Check("a finished print job is announced to the technicians", await WaitFor(() => viewer.q.OfType<RemotePrintJobMsg>().Any(j => j.JobId == "j1"), 4000));
    machine.HostSend(new FileActivityMsg { Kind = "print-out", FromMachine = true, Label = "Invoice-1042" });
    Check("the ticket says what they printed went to the technician's printer",
        await Note(ticket, n => n.Contains("\u201CInvoice-1042\u201D was printed on SX-PC to") && n.Contains("printer")), string.Join(" | ", (await Notes(ticket)).TakeLast(2)));
    Check("...and the person at the machine is told, in their tray app", await WaitFor(() => machine.TrayGot.OfType<FileActivityMsg>().Any(a => a.Kind == "print-out"), 4000));
    viewer.conn.Send(new PrintFileMsg { JobId = "p1", Technician = "Sam Super", Path = @"C:\x\print\in\a\letter.pdf", Name = "letter.pdf" });
    Check("Print a file on their printer reaches the host", await WaitFor(() => machine.HostGot.OfType<PrintFileMsg>().Any(p => p.JobId == "p1"), 4000));
    machine.HostSend(new PrintFileResultMsg { JobId = "p1", Ok = true, Printer = "HP LaserJet M404", Pages = 2 });
    Check("...and how it went reaches the technician", await WaitFor(() => viewer.q.OfType<PrintFileResultMsg>().Any(p => p.JobId == "p1" && p.Ok), 4000));
    machine.HostSend(new FileActivityMsg { Kind = "print-in", FromMachine = false, Label = "letter.pdf", Printer = "HP LaserJet M404", FileCount = 2 });
    Check("the ticket says the technician printed it on their printer, which, and how many pages",
        await Note(ticket, n => n.Contains("printed \u201Cletter.pdf\u201D on SX-PC's printer, HP LaserJet M404 (2 pages)")), string.Join(" | ", (await Notes(ticket)).TakeLast(1)));
    machine.HostSend(new FileActivityMsg { Kind = "print-in", FromMachine = false, Label = "budget.xlsx", Error = "nothing on this computer prints .xlsx files — send it as a PDF" });
    Check("a print that did not work is written with its reason", await Note(ticket, n => n.Contains("tried to print \u201Cbudget.xlsx\u201D") && n.Contains("send it as a PDF")));

    // ---- after the session has ended ----
    samConn.Send(new EndSessionMsg { SessionId = sessionId });
    await Task.Delay(4000);
    var (st6, _) = await Upload(sam, sessionId, File.ReadAllBytes(mp4), 2);
    Check("an upload that arrives after the session ended still finds its ticket", st6 == HttpStatusCode.OK, ((int)st6).ToString());

    // ---- watching and deleting ----
    Console.WriteLine();
    Console.WriteLine("== watching and deleting ==");
    var samWeb = await Portal(sam);
    var page = await samWeb.GetStringAsync($"{http}/portal/tickets/{ticket}");
    // Named by its token, never its id (HANDOVER §159); the id address below still plays, by sending the browser on.
    string recToken; await using (var db = new PlatformDb(options)) recToken = await db.SessionRecordings.Where(r => r.Id == recId).Select(r => r.Token).FirstAsync();
    Check("the ticket page has a Recordings card with a player", page.Contains("<h3>Recordings") && page.Contains($"src=\"/portal/v/{recToken}\""),
        $"{page.Length} chars; heading={page.Contains("Recordings")}; title={System.Text.RegularExpressions.Regex.Match(page, "<h1[^>]*>(.*?)</h1>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value.Trim()}");
    var full = await samWeb.GetAsync($"{http}/portal/recordings/{recId}");
    Check("it plays: served as video/mp4", full.StatusCode == HttpStatusCode.OK && full.Content.Headers.ContentType?.MediaType == "video/mp4", $"{(int)full.StatusCode} {full.Content.Headers.ContentType}");
    var range = new HttpRequestMessage(HttpMethod.Get, $"{http}/portal/recordings/{recId}");
    range.Headers.Range = new RangeHeaderValue(0, 99);
    var part = await samWeb.SendAsync(range);
    Check("...and a player can seek in it (a range request gets part of the file)", part.StatusCode == HttpStatusCode.PartialContent && (await part.Content.ReadAsByteArrayAsync()).Length == 100, ((int)part.StatusCode).ToString());
    var tinaWeb = await Portal(tina);
    var tinaGet = await tinaWeb.GetAsync($"{http}/portal/recordings/{recId}");
    Check("a technician who may not see that ticket may not watch its recording", tinaGet.StatusCode == HttpStatusCode.NotFound, ((int)tinaGet.StatusCode).ToString());

    var tinaDel = await tinaWeb.PostAsync($"{http}/portal/recordings/{recId}/delete?ticket={ticket}", new StringContent(""));
    await using (var db = new PlatformDb(options))
        Check("a technician cannot delete one", await db.SessionRecordings.AnyAsync(r => r.Id == recId));
    string file; await using (var db = new PlatformDb(options)) { var r = await db.SessionRecordings.FirstAsync(x => x.Id == recId); file = Path.Combine(dataDir, "recordings", r.TenantId.ToString(), r.FileName); }
    await samWeb.PostAsync($"{http}/portal/recordings/{recId}/delete?ticket={ticket}", new StringContent(""));
    await using (var db = new PlatformDb(options))
        Check("a supervisor can, and the file goes with the row", !await db.SessionRecordings.AnyAsync(r => r.Id == recId) && !File.Exists(file));
}

async Task<string> Login(string email)
{
    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
    return JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
}

async Task<(WsConnection conn, ConcurrentQueue<Msg> q)> TechSocket(string token)
{
    var raw = new ClientWebSocket();
    await raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={token}"), default);
    var c = new WsConnection(raw); var q = new ConcurrentQueue<Msg>();
    c.OnMessage += m => q.Enqueue(m); _ = c.RunAsync();
    await Task.Delay(500);
    return (c, q);
}

async Task<(WsConnection conn, ConcurrentQueue<Msg> q)> Viewer(string sessionId, string token)
{
    var raw = new ClientWebSocket();
    await raw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={token}"), default);
    var c = new WsConnection(raw); var q = new ConcurrentQueue<Msg>();
    c.OnMessage += m => q.Enqueue(m); _ = c.RunAsync();
    await Task.Delay(500);
    return (c, q);
}

async Task<(HttpStatusCode, string)> Upload(string token, string sessionId, byte[] bytes, int seconds)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{http}/api/recordings?session={sessionId}&seconds={seconds}") { Content = new ByteArrayContent(bytes) };
    if (token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var resp = await hc.SendAsync(req);
    return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
}

/// <summary>A browser signed in as this person, through the console's own one-use hand-off link.</summary>
async Task<HttpClient> Portal(string token)
{
    var (c, q) = await TechSocket(token);
    c.Send(new PortalLinkRequestMsg { Path = "/portal/tickets" });
    var link = await Wait<PortalLinkMsg>(q, 8000);
    var web = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true });
    await web.GetAsync($"{http}{link!.Url}");
    return web;
}

async Task<bool> WaitFor(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (f()) return true; await Task.Delay(50); } return false; }

async Task<T?> Wait<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); } return null; }

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var p0 = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) p0.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(p0)!;
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

/// <summary>A pretend customer machine: its tray app (device connection) and its host.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _id, _key, _name, _tenantKey;
    private WsConnection? _host;
    private ClientWebSocket? _hostRaw;
    public readonly ConcurrentQueue<Msg> HostGot = new(), TrayGot = new();
    public FakeMachine(string ws, string id, string key, string name, string tenantKey) { _ws = ws; _id = id; _key = key; _name = name; _tenantKey = tenantKey; }

    public async Task ConnectAsync()
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        var c = new WsConnection(raw);
        c.OnMessage += m => { TrayGot.Enqueue(m); if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = HostAsync(s); };
        _ = c.RunAsync();
        c.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = _id, DeviceKey = _key, MachineName = _name, UserName = "FakeUser", Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(400);
    }
    public async Task<bool> WaitForHostAsync(int ms) { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (_hostRaw?.State == WebSocketState.Open) return true; await Task.Delay(50); } return false; }
    public void HostSend(Msg m) => _host?.Send(m);
    private async Task HostAsync(StartSessionMsg s)
    {
        _hostRaw = new ClientWebSocket();
        await _hostRaw.ConnectAsync(new Uri($"{_ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
        _host = new WsConnection(_hostRaw);
        _host.OnMessage += m => { HostGot.Enqueue(m); if (m is SessionEndedMsg) _ = _host.CloseAsync("ended"); };
        _ = _host.RunAsync();
        _host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
    }
}
