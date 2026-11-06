// =====================================================================================
// guestwebdemo - a platform of its own to LOOK at outside help's browser viewer (roadmap 3.9, step 4).
//
// One company with outside help on and two machines, each with a code made for it:
//
//   REAL-PC  hosted by the REAL RemoteSupport.Host.exe on THIS PC — so the browser decodes the real
//            thing (this PC's own screen; H.264 if this PC's encoder runs, JPEG tiles if not).
//            Only LOOK at it: input sent to it moves this PC's real mouse.
//   FAKE-PC  hosted by this tool: a painted 1280x720 picture as JPEG tiles, a pointer, and every
//            mouse and key it is sent printed as "INPUT ..." — so input can be tested safely.
//
// When a visit ends, a new code is made for that machine and printed. Nothing touches the live server.
//
//   dotnet run -c Debug --project tools/guestwebdemo/guestwebdemo.csproj -- <port>
// =====================================================================================

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8198;
var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var hostExe = Path.Combine(root, "src", "RemoteSupport.Host", "bin", "Debug", "net10.0-windows", "RemoteSupport.Host.exe");
var dataDir = Path.Combine(Path.GetTempPath(), "rs-guestwebdemo-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
void Say(string s) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); Console.Out.Flush(); }

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
var tenants = new TenantService(new Factory(options), new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("guestwebdemo"));
var (a, _) = await tenants.CreateAsync("Anywhere Demo IT", "demo-it", 7);
await tenants.UpdateBrandingAsync(a!.Id, t => t.OutsidersAllowed = true);

var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
foreach (var arg in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
    psi.ArgumentList.Add(arg);
psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
HarnessDb.Use(psi.Environment, dataDir);
psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
var server = Process.Start(psi)!;
server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
for (int i = 0; i < 150; i++)
{
    try { using var c = new System.Net.Sockets.TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; }
    catch { await Task.Delay(400); }
}
Say($"platform up: http://127.0.0.1:{port}/guest/web   (data {dataDir})");

var hosts = new List<Process>();
AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var h in hosts) try { h.Kill(true); } catch { } try { server.Kill(true); } catch { } };

async Task<WsConnection> Open(string url)
{
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri(url), CancellationToken.None);
    var ws = new WsConnection(cws);
    _ = ws.RunAsync(CancellationToken.None);
    return ws;
}

async Task Machine(string name, bool real)
{
    var id = new DeviceIdentity { DeviceId = name.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = Guid.NewGuid().ToString("N") };
    var tray = await Open($"ws://127.0.0.1:{port}/ws/device");
    tray.OnMessage += async m =>
    {
        try
        {
            if (m is OutsiderCodeMsg { Code.Length: > 0 } c) Say($"CODE {name} {OutsiderAccess.Format(c.Code)}  (for {c.For}, until {c.ExpiresUtc?.ToLocalTime():HH:mm})");
            else if (m is OutsiderCodeMsg { Error.Length: > 0 } ce) Say($"CODE {name} refused: {ce.Error}");
            else if (m is StartSessionMsg { HostToken.Length: > 0 } s)
            {
                Say($"VISIT {name}: '{s.TechnicianName}' — ask={s.AskConsent}");
                if (real)
                {
                    var hp = Process.Start(new ProcessStartInfo(hostExe)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        Arguments = $"--server ws://127.0.0.1:{port} --session {s.SessionId} --token {s.HostToken} --consent auto --tech \"{s.TechnicianName}\""
                    });
                    if (hp != null) hosts.Add(hp);
                    Say($"VISIT {name}: the real host started (pid {hp?.Id})");
                }
                else await FakeHost(name, s);
            }
            else if (m is SessionEndedMsg se) { Say($"ENDED {name}: {se.Reason}"); await Task.Delay(500); tray.Send(new OutsiderCodeRequestMsg { For = "The demo" }); }
        }
        catch (Exception ex) { Say($"{name}: {ex.Message}"); }
    };
    tray.Send(new HelloMsg
    {
        Role = DeviceRole.Client, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = name, UserName = "demo",
        Os = "demo", Version = "1.0", TenantKey = a.TenantKey, Can = new List<string> { DeviceCan.AlwaysAsk }
    });
    await Task.Delay(1500);
    tray.Send(new OutsiderCodeRequestMsg { For = "The demo" });
}

async Task FakeHost(string name, StartSessionMsg s)
{
    var host = await Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}&consent=asking&secs=60");
    // End-to-end encryption (2026-10-07): sealed exactly as the real host seals, so the browser's half (e2e.js)
    // can be looked at. "E2E" lines say what happened; the 10-second line counts frames opened and clear.
    var seal = new SessionSeal(s.SessionId);
    var k = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    seal.UseKey(k, 0);
    host.Seal = seal;
    Seals.Add(seal);
    var eph = E2eHandshake.NewEphemeral();
    var answers = new Dictionary<string, E2eKeyMsg>();
    uint next = 0;
    host.OnMessage += m =>
    {
        switch (m)
        {
            case E2eModeMsg em:
                seal.Sealing = em.Sealed; seal.RefuseClearContent = em.Sealed;
                Say($"E2E {name}: the hub says {(em.Sealed ? "SEAL" : "CLEAR")}");
                break;
            case E2eHelloMsg h:
                if (!answers.TryGetValue(h.Pub, out var ans) && (ans = E2eHandshake.Answer(s.SessionId, eph, k, h.Pub, ++next, null)) != null) answers[h.Pub] = ans;
                if (ans != null) { host.Send(ans); Say($"E2E {name}: viewer {ans.SenderId} given the session key"); }
                break;
            case SubscribeMsg sub:
                Say($"INPUT subscribe monitor {sub.Monitor} on={sub.On} codecs=[{string.Join(",", sub.Codecs)}]");
                if (sub.On) { host.SendBinary(Picture(name)); host.Send(new CursorMsg { Monitor = 0, X = 640, Y = 360, Visible = true, ShapeId = 0 }); }
                break;
            case MouseMsg mm: if (mm.Action != "move") Say($"INPUT mouse {mm.Action} {mm.X},{mm.Y} button {mm.Button} delta {mm.Delta}"); else Moves++; break;
            case KeyMsg km: Say($"INPUT key vk {km.Vk} (0x{km.Vk:X2}) {(km.Down ? "down" : "up")}{(km.Extended ? " extended" : "")}"); break;
            case ParticipantsMsg pm: Say($"PARTICIPANTS {string.Join(", ", pm.Technicians)}"); break;
            default: Say($"INPUT {m.GetType().Name}"); break;
        }
    };
    await Task.Delay(1500);   // as if the person read the prompt
    host.Send(new ConsentGrantedMsg());
    host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 1280, Height = 720, Primary = true, Name = "FAKE" } } });
    host.Send(new E2eReadyMsg());
    Say($"VISIT {name}: allowed");
}

await Machine("REAL-PC", real: true);
await Machine("FAKE-PC", real: false);
var t0 = Task.Run(async () => { while (true) { await Task.Delay(10000); if (Moves > 0) { Say($"INPUT {Moves} mouse moves"); Moves = 0; } foreach (var se in Seals.ToList()) Say($"E2E frames opened {se.Opened}, clear content {se.ClearContent}, refused {se.Refused}"); } });
await server.WaitForExitAsync();
return 0;

// A painted screen as ONE JPEG tile: [1][monitor][count] then [x][y][w][h][len][jpeg].
static byte[] Picture(string name)
{
    using var bmp = new Bitmap(1280, 720);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), Color.FromArgb(30, 64, 175), Color.FromArgb(14, 116, 144), 30f);
        g.FillRectangle(bg, 0, 0, 1280, 720);
        using var grid = new Pen(Color.FromArgb(60, 255, 255, 255));
        for (int x = 0; x < 1280; x += 80) g.DrawLine(grid, x, 0, x, 720);
        for (int y = 0; y < 720; y += 80) g.DrawLine(grid, 0, y, 1280, y);
        using var big = new Font("Segoe UI", 54, FontStyle.Bold);
        using var small = new Font("Segoe UI", 22);
        g.DrawString(name, big, Brushes.White, 60, 220);
        g.DrawString("The stand-in host: every click and key is printed by guestwebdemo.", small, Brushes.White, 64, 320);
        g.FillEllipse(Brushes.Gold, 640 - 10, 360 - 10, 20, 20);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Jpeg);
    var jpeg = ms.ToArray();
    var buf = new byte[1 + 4 + 4 + 20 + jpeg.Length];
    buf[0] = BinKind.Tiles;
    BitConverter.TryWriteBytes(buf.AsSpan(1), 0);
    BitConverter.TryWriteBytes(buf.AsSpan(5), 1);
    int o = 9;
    foreach (var v in new[] { 0, 0, 1280, 720, jpeg.Length }) { BitConverter.TryWriteBytes(buf.AsSpan(o), v); o += 4; }
    jpeg.CopyTo(buf, o);
    return buf;
}

partial class Program { public static int Moves; public static readonly List<SessionSeal> Seals = new(); }

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
