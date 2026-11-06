using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// requestwire - a support request with a subject, a description and a picture of the screen (2026-10-09).
//
// Clint: "a screenshot option when a client requests support would be a great feature. We should also
// change the request support to include a subject line and then the description as currently the
// description, if long creates a mess of the tickets."
//
// Pretend tray apps send RequestCallMsg to a real platform, as the new one does (subject, description,
// a real JPEG) and as an old one does (one box of words, no picture). What the consoles are rung with,
// what the ticket says, and where the picture ends up are read back — the picture byte for byte.
//
// Its own platform on port 8153; the live server is never touched.
//
//   dotnet run -c Release --project tools/requestwire/requestwire.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8153;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("requestwire - a subject, a description and a picture of the screen");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-requestwire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
    ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[] { "create-tenant \"Request Wire\" request-wire 5", "approve request-wire", "add-tech request-wire sue@local.test localtest \"Sue Tech\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var server = Process.Start(psi)!;
server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
bool up = false;
using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
    for (int i = 0; i < 60 && !up; i++) { try { up = (await probe.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { } if (!up) await Task.Delay(500); }
void Kill() { try { if (!server.HasExited) server.Kill(true); server.WaitForExit(5000); } catch { } }
if (!up) { Console.WriteLine("the platform did not come up"); Kill(); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    var sue = await Tech.OpenAsync(http, ws, "sue@local.test");

    // ================================================================ the new tray app
    Console.WriteLine("== the new tray app: subject, description, picture ==");
    var pc = new FakeMachine(ws, "req-pc", "REQ-PC", tenantKey);
    await pc.ConnectAsync();
    var jpeg = Picture("Sage: licence server not found");
    pc.Send(new RequestCallMsg { ContactName = "Thandi", Issue = "Sage will not open", Details = "It says the licence server cannot be found.\nSince this morning.", Screenshot = jpeg });
    var ring = await sue.WaitFor<RingMsg>(r => r.Call.DeviceId == pc.Id, 5000);
    Check("the consoles are rung with the SUBJECT as the call's words", ring?.Call.Issue == "Sage will not open", ring?.Call.Issue ?? "no ring");
    Check("...and the description beside it", ring?.Call.Details == "It says the licence server cannot be found.\nSince this morning.", ring?.Call.Details ?? "");
    Check("...and told a picture came with it", ring?.Call.HasScreenshot == true);
    var ringSize = ring == null ? 0 : Json.Serialize((Msg)ring).Length;
    Check($"...but NOT sent the picture — every console is rung, and it is {jpeg.Length / 1024} KB", ring != null && ringSize < 2000, $"the ring is {ringSize} bytes");

    var t1 = await TicketForAsync(pc.Id);
    Check("the ticket's subject is the subject", t1?.Subject == "Sage will not open", t1?.Subject ?? "no ticket");
    Check("...and its description what they wrote under it", t1?.Description == "It says the licence server cannot be found.\nSince this morning.", t1?.Description ?? "");
    var shots = t1 == null ? new() : await ShotsAsync(t1.Id);
    Check("the picture is kept on the ticket, as a picture of their screen", shots.Count == 1, $"{shots.Count}");
    if (shots.Count == 1)
    {
        var file = Directory.GetFiles(dataDir, shots[0].FileName, SearchOption.AllDirectories).FirstOrDefault();
        Check("...byte for byte what was sent", file != null && File.ReadAllBytes(file).SequenceEqual(jpeg), file ?? "no file on disk");
    }
    var detail = t1 == null ? null : await sue.OpenTicketAsync(t1.Id);
    Check("the console's ticket window is told which picture to offer", detail != null && shots.Count == 1 && detail.Screenshots.SequenceEqual(new[] { shots[0].Id }),
        detail == null ? "no detail" : string.Join(",", detail.Screenshots));
    Check("...and the hand-off may open it in the browser", RemoteSupport.Platform.Services.PortalHandoffService.SafePath($"/portal/photo/{shots.FirstOrDefault()?.Id}") == $"/portal/photo/{shots.FirstOrDefault()?.Id}");
    Check("...but nothing else that looks like it", RemoteSupport.Platform.Services.PortalHandoffService.SafePath("/portal/photo/1/../../admin") == "/portal/tickets");

    Console.WriteLine();
    Console.WriteLine("== asking again inside the thirty minutes ==");
    pc.Send(new RequestCallMsg { ContactName = "Thandi", Issue = "And now Outlook", Details = "", Screenshot = Picture("Outlook") });
    await sue.WaitFor<RingMsg>(r => r.Call.DeviceId == pc.Id && r.Call.Issue == "And now Outlook", 5000);
    await Task.Delay(800);
    Check("the same ticket — no second one", await TicketCountAsync(pc.Id) == 1, $"{await TicketCountAsync(pc.Id)}");
    Check("...its story says what they asked this time", await NoteAsync(t1!.Id, "Customer requested support again: And now Outlook"));
    Check("...and the second picture is on it too", (await ShotsAsync(t1.Id)).Count == 2, $"{(await ShotsAsync(t1.Id)).Count}");

    // ================================================================ an older tray app
    Console.WriteLine();
    Console.WriteLine("== an older tray app: one box of words, no picture ==");
    var old = new FakeMachine(ws, "old-pc", "OLD-PC", tenantKey);
    await old.ConnectAsync();
    const string Essay = "Every morning since the update the whole machine freezes for about ten minutes when I open the accounts package\n"
                       + "and then the printer stops working until I restart it.";
    old.Send(new RequestCallMsg { ContactName = "Bob", Issue = Essay });
    var ring2 = await sue.WaitFor<RingMsg>(r => r.Call.DeviceId == old.Id, 5000);
    var t2 = await TicketForAsync(old.Id);
    Check("its ticket's subject is the first line, cut to 80 and saying so — not the whole paragraph",
        t2 != null && t2.Subject.Length <= SupportRequest.SubjectMax && t2.Subject.EndsWith("…") && !t2.Subject.Contains("printer"), t2?.Subject ?? "");
    Check("...and nothing is lost: all of it is the description", t2?.Description == Essay);
    Check("the alert gets the short subject too", ring2 != null && ring2.Call.Issue == t2?.Subject, ring2?.Call.Issue ?? "");
    Check("no picture, and none claimed", ring2?.Call.HasScreenshot == false && t2 != null && (await ShotsAsync(t2.Id)).Count == 0);

    // ================================================================ something that is not a picture
    Console.WriteLine();
    Console.WriteLine("== a picture that is not a picture ==");
    var odd = new FakeMachine(ws, "odd-pc", "ODD-PC", tenantKey);
    await odd.ConnectAsync();
    odd.Send(new RequestCallMsg { ContactName = "Ann", Issue = "Screen flickers", Screenshot = System.Text.Encoding.UTF8.GetBytes("MZ this is not an image at all") });
    await sue.WaitFor<RingMsg>(r => r.Call.DeviceId == odd.Id, 5000);
    var t3 = await TicketForAsync(odd.Id);
    await Task.Delay(800);
    Check("it is not kept", t3 != null && (await ShotsAsync(t3.Id)).Count == 0);
    Check("...and the ticket SAYS one came and was refused, rather than losing it in silence",
        t3 != null && await NoteAsync(t3.Id, "could not be kept"));

    sue.Dispose(); pc.Drop(); old.Drop(); odd.Drop();
}

/// <summary>A real JPEG with words on it, the size of a small screen.</summary>
static byte[] Picture(string words)
{
    using var bmp = new Bitmap(1280, 720);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.SteelBlue);
        using var f = new Font("Segoe UI", 40);
        g.DrawString(words, f, Brushes.White, 40, 300);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Jpeg);
    return ms.ToArray();
}

PlatformDb Db() => new(new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options);

async Task<Ticket?> TicketForAsync(string deviceId)
{
    for (int i = 0; i < 40; i++)
    {
        await using var db = Db();
        var t = await db.Tickets.Where(x => x.DeviceId == deviceId).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (t != null) { await Task.Delay(300); await using var db2 = Db(); return await db2.Tickets.FirstAsync(x => x.Id == t.Id); }
        await Task.Delay(150);
    }
    return null;
}

async Task<List<TicketPhoto>> ShotsAsync(int ticketId)
{
    await using var db = Db();
    return await db.TicketPhotos.Where(p => p.TicketId == ticketId && p.Kind == PhotoKind.Screenshot).OrderBy(p => p.Id).ToListAsync();
}

async Task<int> TicketCountAsync(string deviceId) { await using var db = Db(); return await db.Tickets.CountAsync(t => t.DeviceId == deviceId); }

async Task<bool> NoteAsync(int ticketId, string contains)
{
    for (int i = 0; i < 20; i++)
    {
        await using var db = Db();
        if (await db.TicketNotes.AnyAsync(n => n.TicketId == ticketId && n.Body.Contains(contains))) return true;
        await Task.Delay(150);
    }
    return false;
}

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

/// <summary>A technician's console connection.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    public readonly ConcurrentQueue<Msg> Seen = new();

    public static async Task<Tech> OpenAsync(string http, string ws, string email)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech();
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m => t.Seen.Enqueue(m);
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms) where T : Msg
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in Seen) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }

    public async Task<TicketDetail?> OpenTicketAsync(int id)
    {
        var mark = Seen.Count;
        _c.Send(new TicketOpenMsg { TicketId = id });
        var e = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < e)
        {
            foreach (var m in Seen.Skip(mark)) if (m is TicketDetailMsg d && d.Detail.Ticket.Id == id) return d.Detail;
            await Task.Delay(50);
        }
        return null;
    }

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>A machine's tray app connection.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _tenantKey;
    public readonly string Id, Name;
    private ClientWebSocket? _raw;
    private WsConnection? _c;

    public FakeMachine(string ws, string id, string name, string tenantKey) { _ws = ws; Id = id; Name = name; _tenantKey = tenantKey; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = Id, DeviceKey = Id + "-key", MachineName = Name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(500);
    }

    public void Send(Msg m) => _c?.Send(m);
    public void Drop() { try { _raw?.Abort(); } catch { } }
}
