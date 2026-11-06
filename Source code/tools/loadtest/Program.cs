using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// loadtest - how much ONE server carries before it bends (2026-10-07).
//
// Somebody told Clint the system was "not enterprise ready" and SQLite would be a problem. Before
// anything is changed for that, this measures it: thousands of pretend customer machines, pretend
// technicians signing in, and pretend remote sessions pushing real-sized pictures, against a
// THROWAWAY copy of the platform, raised step by step while it watches what slows down first.
//
// The pretend apps speak the real protocol (Shared), as the wire harnesses do: a machine says hello
// on /ws/device and pings every 15 s like DeviceLink; a technician signs in on /api/login and opens
// /ws/tech; a session is ConnectDeviceMsg -> the machine's host on /ws/session -> the viewer
// subscribes -> the host streams H.264-sized frames the server relays without reading.
//
//   loadtest local                                  this PC: builds a throwaway platform, seeds it,
//                                                   runs a small test against it, says if it worked
//   loadtest run --server https://... --seed seed.txt [options]     the real run (deploy/loadtest)
//
// Options for run (and local):
//   --machines N      pretend machines per company (default 20)
//   --agents          each machine also connects its agent service, as a real install does (2 sockets)
//   --techs N         technicians signed in per company (default 1, at most what the seed has)
//   --sessions N      remote sessions open at once, in total (default 0)
//   --sessions-each N remote sessions open at once PER COMPANY (instead of --sessions; 2026-10-12)
//   --storm           after the hold, wait for the server to be RESTARTED (Restart-LoadServer.ps1, or by
//                     itself in local mode) and measure everybody coming back: machines every 3 s as
//                     DeviceLink does, consoles every 3 s as TechLink does and, refused because a restart
//                     forgets every sign-in, signing in again; then the sessions again
//   --storm-wait S    seconds to wait for that restart (default 1800)
//   --resign S        a technician takes up to S seconds to sign in again after a restart (default 60)
//   --kbps N          picture per session in KB/s (default 200: a scrolling screen, as measured)
//   --fps N           frames per second (default 10)
//   --ramp S          seconds to bring the machines up, and again the sessions (default 120)
//   --hold S          seconds to hold at full load (default 300)
//   --companies N     use only the first N companies of the seed
//   --part I/N        this load server's share when several run at once: 1/2 and 2/2
//   --out FILE        the 5-second readings as CSV (default loadtest-<time>.csv)
//   --insecure        accept any TLS certificate (a test server without a real one)
// ---------------------------------------------------------------------------------------------

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0 || args[0] is not ("local" or "run"))
{
    Console.WriteLine("loadtest local                     a small test against a throwaway platform on this PC");
    Console.WriteLine("loadtest run --server URL --seed FILE [--machines N] [--agents] [--techs N] [--sessions N | --sessions-each N]");
    Console.WriteLine("             [--kbps N] [--fps N] [--ramp S] [--hold S] [--companies N] [--part I/N] [--out FILE] [--insecure]");
    Console.WriteLine("             [--storm] [--storm-wait S] [--resign S]");
    return 1;
}
var opt = new Options(args.Skip(1).ToArray());
return args[0] == "local" ? await Local.RunAsync(opt) : await Runner.RunAsync(opt, null);

// =============================================================================================
sealed class Options
{
    private readonly Dictionary<string, string> _v = new();
    private readonly HashSet<string> _flags = new();
    public Options(string[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var k = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) _v[k] = a[++i]; else _flags.Add(k);
        }
    }
    public string? Str(string k) => _v.TryGetValue(k, out var s) ? s : null;
    public int Int(string k, int d) => _v.TryGetValue(k, out var s) && int.TryParse(s, out var n) ? n : d;
    public bool Flag(string k) => _flags.Contains(k);
    public void Default(string k, string v) { if (!_v.ContainsKey(k)) _v[k] = v; }
}

sealed record Company(string Slug, string Key, List<string> Emails);

sealed class Seed
{
    public string Password = "";
    public List<Company> Companies = new();
    public static Seed Read(string file)
    {
        var s = new Seed();
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var p = line.Split('\t');
            if (p[0] == "password") s.Password = p[1];
            else if (p.Length >= 3) s.Companies.Add(new Company(p[0], p[1], p[2].Split(',').ToList()));
        }
        if (s.Password.Length == 0 || s.Companies.Count == 0) throw new InvalidDataException($"{file} is not a seed file from seed-loadtest");
        return s;
    }
}

// =============================================================================================
/// <summary>Counters and timings, read by the reporter every 5 s.</summary>
sealed class Stats
{
    public long MachinesUp, MachineConnectFails, MachineDrops, AgentsUp;
    public long TechsUp, LoginFails, TechDrops;
    public long HostsUp, SessionsUp, SessionFails, SessionsLost;
    public long BytesSent, BytesReceived, FramesSent, FramesSkipped;
    public long HealthFails;
    /// <summary>Consoles told their sign-in had ended (a restart), and consoles back after a drop.</summary>
    public long SignedOut, TechReconnects;
    public readonly Timings Connect = new(), Login = new(), State = new(), SessionStart = new(), FirstFrame = new(), Health = new(), DbRead = new();
    public readonly ConcurrentDictionary<string, int> Errors = new();
    public volatile bool Stopping;
    /// <summary>Set before the test ends its own sessions, so the viewers the server then closes are not counted as lost.</summary>
    public volatile bool Ending;
    public void Error(string what) { if (!Stopping) Errors.AddOrUpdate(what.Length > 120 ? what[..120] : what, 1, (_, n) => n + 1); }
}

sealed class Timings
{
    private readonly List<double> _window = new(), _all = new();
    public void Add(double ms) { lock (_all) { _window.Add(ms); _all.Add(ms); } }
    public (int n, double p50, double p95) TakeWindow() { lock (_all) { var r = Pct(_window); _window.Clear(); return r; } }
    public (int n, double p50, double p95) Overall() { lock (_all) return Pct(_all); }
    public (int n, double p50, double p95) Since(int from) { lock (_all) return Pct(_all.Skip(from).ToList()); }
    public int Count { get { lock (_all) return _all.Count; } }
    private static (int, double, double) Pct(List<double> l)
    {
        if (l.Count == 0) return (0, 0, 0);
        var s = l.OrderBy(x => x).ToArray();
        return (s.Length, s[(int)(s.Length * 0.50)], s[Math.Min(s.Length - 1, (int)(s.Length * 0.95))]);
    }
}

// =============================================================================================
/// <summary>
/// One WebSocket, as light as it can be made: a load server holds tens of thousands of these, and
/// WsConnection's 64 KB receive buffer each would make the LOAD server the thing that runs out.
/// Text is decoded into messages; binary is only counted.
/// </summary>
sealed class Link
{
    // Every link opens its OWN connection, as each real machine (its own program) does. Until 2026-10-12 they all
    // shared one HttpMessageInvoker: on xneelo, after the platform was killed and Caddy had answered thousands of
    // 502s, one load box's shared pool stopped opening connections for the rest of the run (6,272 machines never
    // back; its separate /health probe answered in 2 ms throughout) — the TOOL jammed, not the server.
    private static bool _insecure;
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    public Action<Msg>? OnMsg;
    public Action<int>? OnBinary;
    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static void Configure(bool insecure) => _insecure = insecure;

    /// <param name="refusals">Keep the server's answer, so a 401 throws <see cref="UnauthorizedAccessException"/>:
    /// a console told its sign-in has ended, as TechLink tells it apart (only consoles ask; it costs memory).</param>
    public static async Task<Link> OpenAsync(string url, int buffer, bool refusals = false)
    {
        var l = new Link();
        l._ws.Options.SetBuffer(buffer, buffer);
        l._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (refusals) l._ws.Options.CollectHttpResponseDetails = true;
        if (_insecure) l._ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await l._ws.ConnectAsync(new Uri(url), cts.Token); }
        catch (Exception) when (refusals && l._ws.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        { throw new UnauthorizedAccessException("the server refused the sign-in (401)"); }
        return l;
    }

    public async Task<bool> SendAsync(Msg m)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize<Msg>(m));
        await _send.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, default); return true; }
        catch { return false; }
        finally { _send.Release(); }
    }

    /// <summary>False when a frame is still being written: the caller skips this one, as the real
    /// host does when its queue is deep (WsConnection.DropFramesAboveBytes) — the newest frame waits for nothing.</summary>
    public bool TrySendBinary(byte[] frame, out Task sent)
    {
        sent = Task.CompletedTask;
        if (!_send.Wait(0)) return false;
        sent = SendBinaryHeld(frame);
        return true;
    }
    private async Task SendBinaryHeld(byte[] frame)
    {
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, default); }
        catch { }
        finally { _send.Release(); }
    }

    /// <summary>Runs until the socket closes.</summary>
    public async Task RunAsync(int buffer)
    {
        var buf = new byte[buffer];
        MemoryStream? text = null;
        int binLen = 0;
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(buf, default);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    binLen += r.Count;
                    if (r.EndOfMessage) { OnBinary?.Invoke(binLen); binLen = 0; }
                    continue;
                }
                if (r.EndOfMessage && text == null) { Dispatch(Encoding.UTF8.GetString(buf, 0, r.Count)); continue; }
                text ??= new MemoryStream();
                text.Write(buf, 0, r.Count);
                if (r.EndOfMessage) { Dispatch(Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length)); text = null; }
            }
        }
        catch { }
    }

    private void Dispatch(string json)
    {
        Msg? m = null;
        try { m = Json.Deserialize<Msg>(json); } catch { }
        if (m != null) OnMsg?.Invoke(m);
    }

    public void Abort() { try { _ws.Abort(); } catch { } }
}

// =============================================================================================
/// <summary>A pretend customer machine: its tray app (and agent, with --agents), and its host when a
/// session starts. If the server drops it, it tries again every 3 s, as DeviceLink does (RetryMs) — the
/// tool used to wait 5–15 s, which made a restart look gentler than the real one is.</summary>
sealed class Machine
{
    private readonly Runner _r;
    private readonly Company _c;
    private readonly string _id, _key, _name;
    public volatile bool Online, InSession;
    public string Id => _id;

    public Machine(Runner r, Company c, int n)
    {
        _r = r; _c = c;
        _id = $"{c.Slug}-pc{n:0000}"; _key = "k-" + _id; _name = $"LOAD-PC-{n:0000}";
    }

    public async Task KeepConnectedAsync(DeviceRole role)
    {
        bool first = true;
        while (!_r.S.Stopping)
        {
            if (!first) await Task.Delay(3000);
            first = false;
            Link link;
            var sw = Stopwatch.StartNew();
            try { link = await Link.OpenAsync(_r.Ws + "/ws/device", 4096); }
            catch (Exception ex) { Interlocked.Increment(ref _r.S.MachineConnectFails); _r.S.Error("machine connect: " + ex.GetBaseException().Message); continue; }
            _r.S.Connect.Add(sw.Elapsed.TotalMilliseconds);
            link.OnMsg = m =>
            {
                if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = HostAsync(s);
                else if (m is ErrorMsg e) _r.S.Error("machine told: " + e.Message);
            };
            var run = link.RunAsync(4096);
            await link.SendAsync(new HelloMsg
            {
                Role = role, DeviceId = _id, DeviceKey = _key, MachineName = _name, UserName = "LoadUser",
                Os = "Windows 11 (load test)", Version = "0.0-loadtest", TenantKey = _c.Key,
                Spec = new SystemSpec { Cpu = "Pretend", Cores = 4, RamMb = 8192, DiskTotalGb = 256, DiskFreeGb = 100, Os = "Windows 11 (load test)" },
                // As DeviceLink sends them on every connect, so the server stores what it stores for a real one.
                HardwareId = "hw-" + _id, Cards = new List<NetworkCard> { new() { Mac = _r.MacFor(_id), Wired = true, Networks = new() { "192.168.1.20/24" } } },
            });
            if (role == DeviceRole.Client) { Online = true; Interlocked.Increment(ref _r.S.MachinesUp); }
            else Interlocked.Increment(ref _r.S.AgentsUp);
            // DeviceLink's own rhythm: a ping every 15 s.
            var ping = Task.Run(async () => { while (link.IsOpen && !_r.S.Stopping) { await Task.Delay(15000); if (link.IsOpen) await link.SendAsync(new PingMsg()); } });
            await run;
            if (role == DeviceRole.Client) { Online = false; Interlocked.Decrement(ref _r.S.MachinesUp); }
            else Interlocked.Decrement(ref _r.S.AgentsUp);
            if (!_r.S.Stopping) { Interlocked.Increment(ref _r.S.MachineDrops); _r.S.Error($"machine dropped by the server ({role})"); }
            if (_r.S.Stopping) break;
        }
    }

    private async Task HostAsync(StartSessionMsg s)
    {
        Link host;
        try { host = await Link.OpenAsync($"{_r.Ws}/ws/session?role=host&id={Uri.EscapeDataString(s.SessionId)}&token={Uri.EscapeDataString(s.HostToken)}", 4096); }
        catch (Exception ex) { _r.S.Error("host connect: " + ex.GetBaseException().Message); return; }
        Interlocked.Increment(ref _r.S.HostsUp);
        CancellationTokenSource? streaming = null;
        host.OnMsg = m =>
        {
            if (m is SubscribeMsg sub)
            {
                if (sub.On && streaming == null) { streaming = new CancellationTokenSource(); _ = StreamAsync(host, streaming.Token); }
                else if (!sub.On) { streaming?.Cancel(); streaming = null; }
            }
            else if (m is SessionEndedMsg) { streaming?.Cancel(); host.Abort(); }
        };
        var run = host.RunAsync(4096);
        await host.SendAsync(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 1920, Height = 1080, Primary = true, Name = "Load" } } });
        await run;
        streaming?.Cancel();
        Interlocked.Decrement(ref _r.S.HostsUp);
    }

    /// <summary>The picture: fps frames a second of kbps/fps bytes each, a key frame first and every 5 s.</summary>
    private async Task StreamAsync(Link host, CancellationToken ct)
    {
        var every = TimeSpan.FromMilliseconds(1000.0 / _r.Fps);
        var next = DateTime.UtcNow;
        long n = 0;
        while (!ct.IsCancellationRequested && host.IsOpen)
        {
            var frame = n % (_r.Fps * 5) == 0 ? _r.KeyFrame : _r.Frame;
            if (host.TrySendBinary(frame, out _)) { Interlocked.Add(ref _r.S.BytesSent, frame.Length); Interlocked.Increment(ref _r.S.FramesSent); }
            else Interlocked.Increment(ref _r.S.FramesSkipped);
            n++;
            next += every;
            var wait = next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) { try { await Task.Delay(wait, ct); } catch { break; } }
            else if (wait < -every * 10) next = DateTime.UtcNow; // far behind: do not try to catch up in a burst
        }
    }
}

// =============================================================================================
/// <summary>A pretend technician's console: signs in, opens /ws/tech, and starts sessions one at a time.
/// Like TechLink it keeps the console connected, trying again every 3 s; a 401 means the server has forgotten
/// the sign-in (console tokens live in its memory — AuthService — so a restart forgets every one), and the
/// person, told so, signs in again a little later.</summary>
sealed class Tech
{
    private readonly Runner _r;
    private readonly string _email;
    private string _token = "";
    private volatile Link? _link;
    private readonly SemaphoreSlim _one = new(1, 1);
    private TaskCompletionSource<(SessionInfo? s, string err)>? _waiting;
    private string _waitingFor = "";
    public volatile bool Up;
    public Tech(Runner r, string email) { _r = r; _email = email; }

    /// <summary>Signs in and opens the console; true once its machine list has arrived. It then stays connected.</summary>
    public async Task<bool> SignInAsync()
    {
        if (!await LoginAsync()) return false;
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => KeepConnectedAsync(opened));
        return await opened.Task;
    }

    private async Task<bool> LoginAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var hc = new HttpClient(new SocketsHttpHandler { SslOptions = Runner.Ssl }, true) { Timeout = TimeSpan.FromSeconds(60) };
            var resp = await hc.PostAsJsonAsync($"{_r.Http}/api/login", new { email = _email, password = _r.Seed.Password });
            var body = await resp.Content.ReadAsStringAsync();
            _r.S.Login.Add(sw.Elapsed.TotalMilliseconds);
            if (!resp.IsSuccessStatusCode) { Interlocked.Increment(ref _r.S.LoginFails); _r.S.Error($"sign-in refused ({(int)resp.StatusCode}): {body}"); return false; }
            var doc = JsonDocument.Parse(body).RootElement;
            if (!doc.TryGetProperty("token", out var t) || t.GetString() is not { Length: > 0 } tok)
            {
                Interlocked.Increment(ref _r.S.LoginFails);
                _r.S.Error("sign-in gave no token (is two-factor still on? the test server needs RSP_Security__RequireTwoFactor=false): " + body);
                return false;
            }
            _token = tok;
        }
        catch (Exception ex) { Interlocked.Increment(ref _r.S.LoginFails); _r.S.Error("sign-in: " + ex.GetBaseException().Message); return false; }
        return true;
    }

    private async Task KeepConnectedAsync(TaskCompletionSource<bool> firstOpen)
    {
        bool first = true;
        int wait = 0;
        while (!_r.S.Stopping)
        {
            if (wait > 0) await Task.Delay(wait);
            wait = 3000; // TechLink's own rhythm
            var sw = Stopwatch.StartNew();
            Link link;
            try { link = await Link.OpenAsync($"{_r.Ws}/ws/tech?token={Uri.EscapeDataString(_token)}", 16384, refusals: true); }
            catch (Exception ex) when (first)
            { Interlocked.Increment(ref _r.S.LoginFails); _r.S.Error("console connect: " + ex.GetBaseException().Message); firstOpen.TrySetResult(false); return; }
            catch (UnauthorizedAccessException)
            {
                // "Your sign-in has ended — the server was restarted… Please sign in again." The person reads it and does.
                Interlocked.Increment(ref _r.S.SignedOut);
                await Task.Delay(Random.Shared.Next(2000, Math.Max(3000, _r.ResignMs)));
                if (await LoginAsync()) wait = 0;
                continue;
            }
            catch (Exception ex) { _r.S.Error("console connect: " + ex.GetBaseException().Message); continue; }

            var state = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.OnMsg = m =>
            {
                if (m is StateMsg) state.TrySetResult(true);
                else if (m is SessionReadyMsg ready && ready.Session.DeviceId == _waitingFor) _waiting?.TrySetResult((ready.Session, ""));
                else if (m is ErrorMsg e) { if (_waiting != null) _waiting.TrySetResult((null, e.Message)); else _r.S.Error("console told: " + e.Message); }
            };
            var run = link.RunAsync(16384);
            _link = link; Up = true;
            Interlocked.Increment(ref _r.S.TechsUp);
            var got = await Task.WhenAny(state.Task, Task.Delay(30000)) == state.Task;
            if (!got) _r.S.Error("console never received its machine list in 30 s");
            if (first) { if (got) _r.S.State.Add(sw.Elapsed.TotalMilliseconds); firstOpen.TrySetResult(got); }
            else Interlocked.Increment(ref _r.S.TechReconnects);
            first = false;
            _ = Task.Run(async () => { while (link.IsOpen && !_r.S.Stopping) { await Task.Delay(15000); if (link.IsOpen) await link.SendAsync(new PingMsg()); } });
            await run;
            Up = false;
            Interlocked.Decrement(ref _r.S.TechsUp);
            if (!_r.S.Stopping && !_r.S.Ending) { Interlocked.Increment(ref _r.S.TechDrops); _r.S.Error("console dropped by the server"); }
        }
    }

    /// <summary>ConnectDeviceMsg → SessionReadyMsg → the viewer subscribes and counts what arrives.</summary>
    public async Task<bool> StartSessionAsync(Machine m)
    {
        // Read once: after a restart the console's link is replaced, and may be gone between a check and a send.
        var link = _link;
        if (link == null || !link.IsOpen) { Interlocked.Increment(ref _r.S.SessionFails); _r.S.Error("session not started: the console was not connected"); return false; }
        await _one.WaitAsync();
        SessionInfo? s; string err;
        var sw = Stopwatch.StartNew();
        try
        {
            _waitingFor = m.Id;
            _waiting = new TaskCompletionSource<(SessionInfo?, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await link.SendAsync(new ConnectDeviceMsg { DeviceId = m.Id, Reason = "load test" });
            var done = await Task.WhenAny(_waiting.Task, Task.Delay(30000));
            (s, err) = done == _waiting.Task ? _waiting.Task.Result : (null, "no answer in 30 s");
            _waiting = null; _waitingFor = "";
        }
        finally { _one.Release(); }
        if (s == null) { Interlocked.Increment(ref _r.S.SessionFails); _r.S.Error("session refused: " + err); return false; }
        _r.S.SessionStart.Add(sw.Elapsed.TotalMilliseconds);

        Link viewer;
        try { viewer = await Link.OpenAsync($"{_r.Ws}/ws/session?role=viewer&id={Uri.EscapeDataString(s.SessionId)}&token={Uri.EscapeDataString(_token)}", 65536); }
        catch (Exception ex) { Interlocked.Increment(ref _r.S.SessionFails); _r.S.Error("viewer connect: " + ex.GetBaseException().Message); return false; }
        var first = Stopwatch.StartNew();
        bool seen = false;
        viewer.OnBinary = n =>
        {
            Interlocked.Add(ref _r.S.BytesReceived, n);
            if (!seen) { seen = true; _r.S.FirstFrame.Add(first.Elapsed.TotalMilliseconds); }
        };
        // As the real console does (SessionForm): the first request for the picture is sent when the customer's screen
        // list arrives — from the host, or replayed by the server to a viewer that came after it. Asked straight away,
        // before the host has connected, the request has nobody to go to and is lost: 27 of 300 sessions showed nothing
        // the first time this ran (2026-10-08), which was this tool, not the server.
        int subscribed = 0;
        viewer.OnMsg = msg =>
        {
            if (msg is MonitorsMsg && Interlocked.Exchange(ref subscribed, 1) == 0)
            {
                first.Restart();
                _ = viewer.SendAsync(new SubscribeMsg { Monitor = 0, On = true, Codecs = new List<string> { VideoCodec.H264 } });
            }
        };
        var run = viewer.RunAsync(65536);
        m.InSession = true;
        Interlocked.Increment(ref _r.S.SessionsUp);
        _ = Task.Run(async () =>
        {
            await run;
            Interlocked.Decrement(ref _r.S.SessionsUp);
            m.InSession = false;
            if (!_r.S.Stopping && !_r.S.Ending) { Interlocked.Increment(ref _r.S.SessionsLost); _r.S.Error("a session's viewer was dropped"); }
        });
        _r.Viewers.Add((viewer, s.SessionId, this));
        return true;
    }

    public Task EndAsync(string sessionId) => _link?.SendAsync(new EndSessionMsg { SessionId = sessionId }) ?? Task.CompletedTask;
    public void Abort() => _link?.Abort();
}

// =============================================================================================
sealed class Runner
{
    public static SslClientAuthenticationOptions Ssl = new();
    public readonly Stats S = new();
    public string Http = "", Ws = "";
    public Seed Seed = null!;
    public int Fps, Kbps;
    public byte[] Frame = Array.Empty<byte>(), KeyFrame = Array.Empty<byte>();
    public readonly ConcurrentBag<(Link viewer, string session, Tech tech)> Viewers = new();

    /// <summary>What the server's own process looks like, when the harness can see it (local mode).</summary>
    public Func<string>? ServerReading;
    /// <summary>Local mode only: restarts the throwaway platform for --storm. On xneelo, Restart-LoadServer.ps1 does.</summary>
    public Func<Task>? RestartServer;
    /// <summary>The most a technician takes to sign in again after a restart (--resign).</summary>
    public int ResignMs = 60000;

    public static async Task<int> RunAsync(Options o, Func<string>? serverReading, Func<Task>? restartServer = null)
    {
        var r = new Runner { ServerReading = serverReading, RestartServer = restartServer };
        return await r.GoAsync(o);
    }

    /// <summary>A MAC address the hub will keep (twelve hex digits), the same for a machine every time it connects.</summary>
    public string MacFor(string id) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12];

    /// <summary>This load box's free memory and total (MB), on Linux; (-1, -1) elsewhere. A load box short of
    /// memory is the load box's limit, not the server's.</summary>
    public static (double free, double total) BoxMemory()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return (-1, -1);
            double free = -1, total = -1;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                if (p[0] == "MemAvailable:") free = double.Parse(p[1]) / 1024;
                else if (p[0] == "MemTotal:") total = double.Parse(p[1]) / 1024;
            }
            return (free, total);
        }
        catch { return (-1, -1); }
    }

    private static byte[] MakeFrame(int bytes, bool key)
    {
        // [4][monitor:i32][flags:u8][width:i32][height:i32][annex-B H.264] — BinKind.Video's layout. The server relays
        // it unread; only its size matters here.
        var f = new byte[Math.Max(14, bytes)];
        Random.Shared.NextBytes(f);
        f[0] = BinKind.Video;
        BitConverter.GetBytes(0).CopyTo(f, 1);
        f[5] = key ? VideoFlags.KeyFrame : (byte)0;
        BitConverter.GetBytes(1920).CopyTo(f, 6);
        BitConverter.GetBytes(1080).CopyTo(f, 10);
        return f;
    }

    private async Task<int> GoAsync(Options o)
    {
        Http = (o.Str("server") ?? throw new ArgumentException("--server is required")).TrimEnd('/');
        Ws = Http.StartsWith("https://") ? "wss://" + Http[8..] : Http.StartsWith("http://") ? "ws://" + Http[7..] : throw new ArgumentException("--server must start with https:// or http://");
        Seed = Seed.Read(o.Str("seed") ?? throw new ArgumentException("--seed is required"));
        bool insecure = o.Flag("insecure");
        Link.Configure(insecure);
        if (insecure) Ssl = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true };

        int perCompany = o.Int("machines", 20), techsPer = o.Int("techs", 1), sessions = o.Int("sessions", 0);
        int ramp = o.Int("ramp", 120), hold = o.Int("hold", 300);
        bool agents = o.Flag("agents"), storm = o.Flag("storm");
        ResignMs = Math.Max(3, o.Int("resign", 60)) * 1000;
        Fps = Math.Max(1, o.Int("fps", 10)); Kbps = Math.Max(1, o.Int("kbps", 200));
        Frame = MakeFrame(Kbps * 1024 / Fps, false); KeyFrame = MakeFrame(Kbps * 1024 / Fps, true);

        int wanted = o.Int("companies", Seed.Companies.Count);
        // Asking for more than the seed holds used to run fewer, silently — and a step would then claim a load it never put on.
        if (wanted > Seed.Companies.Count) { Console.WriteLine($"REFUSED: --companies {wanted}, but the seed holds only {Seed.Companies.Count}. Seed more (Prepare-LoadServer.ps1 -Companies)."); return 2; }
        var companies = Seed.Companies.Take(wanted).ToList();
        var short_ = companies.FirstOrDefault(c => c.Emails.Count < techsPer);
        if (short_ != null) { Console.WriteLine($"REFUSED: --techs {techsPer}, but {short_.Slug} has only {short_.Emails.Count} people in the seed."); return 2; }
        var part = (o.Str("part") ?? "1/1").Split('/');
        int pi = int.Parse(part[0]), pn = int.Parse(part[1]);
        companies = companies.Where((_, i) => i % pn == pi - 1).ToList();
        if (companies.Count == 0) { Console.WriteLine("No companies left for this part."); return 2; }
        if (o.Str("sessions-each") != null) sessions = o.Int("sessions-each", 0) * companies.Count;

        var machines = companies.SelectMany(c => Enumerable.Range(1, perCompany).Select(n => (c, m: new Machine(this, c, n)))).ToList();
        var techs = companies.Select(c => (c, list: c.Emails.Take(techsPer).Select(e => new Tech(this, e)).ToList())).ToList();
        int techCount = techs.Sum(t => t.list.Count);
        sessions = Math.Min(sessions, machines.Count);

        var outFile = o.Str("out") ?? $"loadtest-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        Console.WriteLine($"loadtest against {Http}  (part {pi}/{pn})");
        Console.WriteLine($"  {companies.Count} companies, {machines.Count} machines{(agents ? " + their agents" : "")}, {techCount} technicians, {sessions} sessions at {Kbps} KB/s, {Fps} fps");
        Console.WriteLine($"  ramp {ramp} s per stage, hold {hold} s{(storm ? ", then a RESTART STORM" : "")}; readings every 5 s to {outFile}");
        Console.WriteLine();

        var started = DateTime.UtcNow;
        var stage = "machines";
        using var csv = new StreamWriter(outFile) { AutoFlush = true };
        csv.WriteLine("seconds,stage,machines_up,agents_up,machine_connect_fails,machine_drops,techs_up,login_fails,sessions_up,session_fails,sessions_lost,sent_kbs,received_kbs,expected_kbs,frames_skipped,connect_p95_ms,login_p95_ms,state_p95_ms,session_start_p95_ms,first_frame_p95_ms,health_p50_ms,health_p95_ms,health_fails,db_read_p95_ms,loadbox_cpu_pct,loadbox_mem_mb,loadbox_free_mb,signed_out,server");
        var reports = new List<Reading>();
        var reporter = Task.Run(async () =>
        {
            long lastSent = 0, lastRecv = 0;
            var proc = Process.GetCurrentProcess();
            var lastCpu = proc.TotalProcessorTime; var lastAt = DateTime.UtcNow;
            while (!S.Stopping)
            {
                await Task.Delay(5000);
                var now = DateTime.UtcNow; proc.Refresh();
                double secs = (now - lastAt).TotalSeconds;
                double cpu = (proc.TotalProcessorTime - lastCpu).TotalSeconds / secs / Environment.ProcessorCount * 100;
                lastCpu = proc.TotalProcessorTime; lastAt = now;
                long sent = Interlocked.Read(ref S.BytesSent), recv = Interlocked.Read(ref S.BytesReceived);
                var rd = new Reading
                {
                    Seconds = (now - started).TotalSeconds, Stage = stage,
                    MachinesUp = S.MachinesUp, AgentsUp = S.AgentsUp, TechsUp = S.TechsUp, SessionsUp = S.SessionsUp,
                    SentKbs = (sent - lastSent) / 1024.0 / secs, ReceivedKbs = (recv - lastRecv) / 1024.0 / secs,
                    ExpectedKbs = S.HostsUp > 0 ? Math.Min(S.HostsUp, S.SessionsUp) * (double)Kbps : 0,
                    Connect = S.Connect.TakeWindow(), Login = S.Login.TakeWindow(), State = S.State.TakeWindow(),
                    SessionStart = S.SessionStart.TakeWindow(), FirstFrame = S.FirstFrame.TakeWindow(),
                    Health = S.Health.TakeWindow(), DbRead = S.DbRead.TakeWindow(),
                    Cpu = cpu, MemMb = proc.WorkingSet64 / 1048576.0, Server = ServerReading?.Invoke() ?? "",
                };
                (rd.BoxFreeMb, rd.BoxTotalMb) = BoxMemory();
                lastSent = sent; lastRecv = recv;
                lock (reports) reports.Add(rd);
                Console.WriteLine($"{rd.Seconds,5:0}s {stage,-9} machines {rd.MachinesUp,6}/{machines.Count}  techs {rd.TechsUp,4}/{techCount}  sessions {rd.SessionsUp,4}/{sessions}  " +
                    $"video {rd.ReceivedKbs / 1024,6:0.0}/{rd.ExpectedKbs / 1024:0.0} MB/s  health p95 {rd.Health.p95,5:0} ms  db p95 {rd.DbRead.p95,5:0} ms  " +
                    $"fails {S.MachineConnectFails}/{S.LoginFails}/{S.SessionFails} drops {S.MachineDrops}  box cpu {cpu:0}%{(rd.BoxFreeMb >= 0 ? $" free {rd.BoxFreeMb:0} MB" : "")}" +
                    $"{(rd.Server.Length > 0 ? "  server " + rd.Server : "")}");
                csv.WriteLine(string.Join(",", rd.Seconds.ToString("0"), stage, rd.MachinesUp, rd.AgentsUp, S.MachineConnectFails, S.MachineDrops, rd.TechsUp, S.LoginFails,
                    rd.SessionsUp, S.SessionFails, S.SessionsLost, rd.SentKbs.ToString("0"), rd.ReceivedKbs.ToString("0"), rd.ExpectedKbs.ToString("0"), S.FramesSkipped,
                    rd.Connect.p95.ToString("0"), rd.Login.p95.ToString("0"), rd.State.p95.ToString("0"), rd.SessionStart.p95.ToString("0"), rd.FirstFrame.p95.ToString("0"),
                    rd.Health.p50.ToString("0"), rd.Health.p95.ToString("0"), S.HealthFails, rd.DbRead.p95.ToString("0"), cpu.ToString("0"), rd.MemMb.ToString("0"),
                    rd.BoxFreeMb.ToString("0"), S.SignedOut, "\"" + rd.Server + "\""));
            }
        });

        // The server's pulse throughout: /health (no database) and /api/branding (one database read), every 2 s.
        var probe = Task.Run(async () =>
        {
            using var hc = new HttpClient(new SocketsHttpHandler { SslOptions = Ssl }) { Timeout = TimeSpan.FromSeconds(30) };
            var key = Uri.EscapeDataString(companies[0].Key);
            while (!S.Stopping)
            {
                var sw = Stopwatch.StartNew();
                try { var h = await hc.GetAsync($"{Http}/health"); if (h.IsSuccessStatusCode) S.Health.Add(sw.Elapsed.TotalMilliseconds); else Interlocked.Increment(ref S.HealthFails); }
                catch { Interlocked.Increment(ref S.HealthFails); }
                sw.Restart();
                try { var b = await hc.GetAsync($"{Http}/api/branding?key={key}"); if (b.IsSuccessStatusCode) S.DbRead.Add(sw.Elapsed.TotalMilliseconds); }
                catch { }
                await Task.Delay(2000);
            }
        });

        // ---- stage 1: the machines, spread evenly over the ramp ----
        var running = new List<Task>();
        void Start(int i)
        {
            running.Add(machines[i].m.KeepConnectedAsync(DeviceRole.Client));
            if (agents) running.Add(machines[i].m.KeepConnectedAsync(DeviceRole.Agent));
        }
        var order = Enumerable.Range(0, machines.Count).ToList();
        // --warm: one machine per company first, so every company's hub is already loaded when the rush comes —
        // to tell a stall caused by loading hubs (BUGS #11) from one caused by the rush itself.
        if (o.Flag("warm"))
        {
            var firsts = order.Where(i => i % perCompany == 0).ToList();
            Console.WriteLine($"  (warming: one machine in each of {firsts.Count} companies first)");
            await Paced(firsts.Count, 10, k => Start(firsts[k]));
            await WaitUntil(() => S.MachinesUp >= firsts.Count, 60);
            order = order.Where(i => i % perCompany != 0).ToList();
        }
        await Paced(order.Count, ramp, k => Start(order[k]));
        await WaitUntil(() => S.MachinesUp + S.MachineConnectFails >= machines.Count, 60);

        // ---- stage 2: the technicians sign in (a Monday morning) ----
        stage = "sign-in";
        var signIns = new List<Task<bool>>();
        var allTechs = techs.SelectMany(t => t.list).ToList();
        await Paced(allTechs.Count, Math.Max(10, Math.Min(ramp, 120)), i => signIns.Add(allTechs[i].SignInAsync()));
        await Task.WhenAll(signIns);

        // ---- stage 3: the sessions, each company's technicians taking its machines in turn ----
        var plan = new List<(Tech t, Machine m)>();
        if (sessions > 0)
        {
            stage = "sessions";
            var queues = techs.Select(t => (t.list, ms: machines.Where(x => x.c == t.c).Select(x => x.m).ToList(), next: 0)).ToList();
            for (int round = 0; plan.Count < sessions && round < perCompany; round++)
                foreach (var q in queues)
                {
                    if (plan.Count >= sessions) break;
                    if (round < q.ms.Count && q.list.Count > 0) plan.Add((q.list[round % q.list.Count], q.ms[round]));
                }
            await StartSessions(plan, ramp);
        }

        // ---- stage 4: hold at full load ----
        stage = "hold";
        int holdFrom; lock (reports) holdFrom = reports.Count;
        await Task.Delay(TimeSpan.FromSeconds(hold));
        List<Reading> held; lock (reports) held = reports.Skip(holdFrom).ToList();
        // The hold's own summary first, from the counters as they stand — the storm's drops are not the hold's.
        int code = Summary(machines.Count, techCount, sessions, held);

        // ---- stage 5 (--storm): the server restarted under all of it, as after every deploy ----
        if (storm)
        {
            stage = "storm";
            int stormCode = await StormAsync(o, machines.Count, agents ? machines.Count : 0, techCount, plan, reports);
            code = Math.Max(code, stormCode);
        }

        // ---- the end: sessions ended properly (so the server writes their tickets), then everything closed ----
        stage = "stopping";
        S.Ending = true;
        foreach (var v in Viewers) await v.tech.EndAsync(v.session);
        await Task.Delay(3000);
        S.Stopping = true;
        foreach (var v in Viewers) v.viewer.Abort();
        foreach (var t in allTechs) t.Abort();
        await Task.WhenAny(reporter, Task.Delay(6000));
        return code;
    }

    private async Task StartSessions(List<(Tech t, Machine m)> plan, int seconds)
    {
        var starts = new List<Task<bool>>();
        await Paced(plan.Count, seconds, i => { if (plan[i].m.Online) starts.Add(plan[i].t.StartSessionAsync(plan[i].m)); else { Interlocked.Increment(ref S.SessionFails); S.Error("session skipped: its machine was not online"); } });
        await Task.WhenAll(starts);
    }

    /// <summary>
    /// The restart storm (2026-10-12). Waits for the server to go down — Restart-LoadServer.ps1 on xneelo, or the
    /// local platform restarted by this tool — then times everybody coming back the way the real apps do: every
    /// machine and agent every 3 s (DeviceLink), every console every 3 s and, refused because the restart forgot its
    /// sign-in, its technician signing in again (TechLink); then each technician opens their session again. A
    /// restart is what every deploy is, so this is what the margin in companies-per-cell is for.
    /// </summary>
    private async Task<int> StormAsync(Options o, int machines, int agents, int techCount, List<(Tech t, Machine m)> plan, List<Reading> reports)
    {
        Console.WriteLine();
        Console.WriteLine("####################################################################################");
        Console.WriteLine(RestartServer != null
            ? "##  STORM: restarting the platform in 10 s"
            : "##  READY FOR THE RESTART - run Restart-LoadServer.ps1 now (one window, any time)");
        Console.WriteLine("####################################################################################");
        Console.WriteLine();
        if (RestartServer != null) _ = Task.Run(async () => { await Task.Delay(10000); await RestartServer(); });

        // Down: most machines gone at once. A few dropping is not a restart.
        var waitUntil = DateTime.UtcNow.AddSeconds(o.Int("storm-wait", 1800));
        while (DateTime.UtcNow < waitUntil && S.MachinesUp >= machines / 2) await Task.Delay(250);
        if (S.MachinesUp >= machines / 2)
        {
            Console.WriteLine("STORM: the server was never restarted while this waited - nothing measured.");
            return 3;
        }
        var down = Stopwatch.StartNew();
        long failsAt = S.MachineConnectFails, healthFailsAt = S.HealthFails, signedOutAt = S.SignedOut;
        int connectFrom = S.Connect.Count, loginFrom = S.Login.Count, healthFrom = S.Health.Count;
        Viewers.Clear(); // every session went with the server
        Console.WriteLine($"STORM: the server went down - {S.MachinesUp} of {machines} machines still connected; timing the way back");

        double? machines99 = null, machinesAll = null, agentsAll = agents == 0 ? 0 : null, consolesAll = techCount == 0 ? 0 : null;
        var giveUp = TimeSpan.FromSeconds(Math.Max(300, ResignMs / 1000 * 3));
        while (down.Elapsed < giveUp && (machinesAll == null || agentsAll == null || consolesAll == null))
        {
            var t = down.Elapsed.TotalSeconds;
            if (machines99 == null && S.MachinesUp >= machines * 0.99) machines99 = t;
            if (machinesAll == null && S.MachinesUp >= machines) machinesAll = t;
            if (agentsAll == null && S.AgentsUp >= agents) agentsAll = t;
            if (consolesAll == null && S.TechsUp >= techCount) consolesAll = t;
            await Task.Delay(250);
        }
        long refused = S.MachineConnectFails - failsAt, signedOut = S.SignedOut - signedOutAt;
        var reconnect = S.Connect.Since(connectFrom);
        var relogin = S.Login.Since(loginFrom);
        var health = S.Health.Since(healthFrom);

        // The technicians open their sessions again, as they would.
        int sessFailsAt = (int)S.SessionFails, startFrom = S.SessionStart.Count, firstFrom = S.FirstFrame.Count;
        if (plan.Count > 0)
        {
            Console.WriteLine($"STORM: everybody is back - the technicians open their {plan.Count} sessions again");
            await StartSessions(plan, Math.Max(10, Math.Min(60, plan.Count / 5)));
        }
        int stormHold = Math.Max(30, o.Int("storm-hold", 120));
        int from; lock (reports) from = reports.Count;
        await Task.Delay(TimeSpan.FromSeconds(stormHold));
        List<Reading> after; lock (reports) after = reports.Skip(from).ToList();
        int sessFails = (int)S.SessionFails - sessFailsAt;
        double recv = after.Count > 0 ? after.Average(h => h.ReceivedKbs) : 0, expect = after.Count > 0 ? after.Average(h => h.ExpectedKbs) : 0;

        string T(double? s) => s == null ? "NOT within " + giveUp.TotalSeconds.ToString("0") + " s" : $"{s:0} s";
        string P((int n, double p50, double p95) t) => t.n == 0 ? "none measured" : $"median {t.p50:0} ms, 95% within {t.p95:0} ms ({t.n})";
        Console.WriteLine();
        Console.WriteLine("==================================================================== restart storm");
        Console.WriteLine($"Machines back      : 99% after {T(machines99)}, all {machines} after {T(machinesAll)} (from the moment it went down)");
        if (agents > 0) Console.WriteLine($"Agents back        : all {agents} after {T(agentsAll)}");
        Console.WriteLine($"Consoles back      : all {techCount} after {T(consolesAll)}; {signedOut} told their sign-in had ended, signing in again {P(relogin)}");
        Console.WriteLine($"Reconnects         : {P(reconnect)}; {refused} attempts refused while it was down or busy");
        Console.WriteLine($"Server /health     : {P(health)} through the storm; {S.HealthFails - healthFailsAt} failed");
        if (plan.Count > 0)
        {
            Console.WriteLine($"Sessions again     : {plan.Count - sessFails} of {plan.Count} opened; start {P(S.SessionStart.Since(startFrom))}");
            Console.WriteLine($"First picture      : {P(S.FirstFrame.Since(firstFrom))}");
            Console.WriteLine($"Picture relayed    : {recv / 1024:0.0} MB/s of {expect / 1024:0.0} MB/s expected over {stormHold} s after ({(expect > 0 ? recv / expect * 100 : 100):0}%)");
        }
        var bent = new List<string>();
        // A healthy restart: the platform is up in seconds and every machine is back within a minute or two of
        // 3-second retries. The technicians are given their reading-and-typing time on top.
        if (machinesAll == null || machinesAll > 120) bent.Add($"Machines took {T(machinesAll)} to all come back, where well under two minutes is healthy.");
        if (agentsAll == null || agentsAll > 120) bent.Add($"Agents took {T(agentsAll)} to all come back.");
        if (consolesAll == null || consolesAll > ResignMs / 1000 + 120) bent.Add($"Consoles took {T(consolesAll)} to all come back.");
        if (sessFails > 0) bent.Add($"{sessFails} sessions could not be opened again.");
        if (expect > 0 && recv < expect * 0.9) bent.Add($"Pictures were not getting through in full after it ({recv / expect * 100:0}%).");
        if (bent.Count == 0) Console.WriteLine("STORM VERDICT: everybody came back promptly after the restart.");
        else { Console.WriteLine("STORM VERDICT: the restart hurt:"); foreach (var b in bent) Console.WriteLine("  - " + b); }
        return bent.Count == 0 ? 0 : 3;
    }

    private int Summary(int machines, int techs, int sessions, List<Reading> held)
    {
        Console.WriteLine();
        Console.WriteLine("==================================================================== summary");
        string P((int n, double p50, double p95) t) => t.n == 0 ? "none measured" : $"median {t.p50:0} ms, 95% within {t.p95:0} ms ({t.n})";
        long minUp = held.Count > 0 ? held.Min(h => h.MachinesUp) : S.MachinesUp;
        double recv = held.Count > 0 ? held.Average(h => h.ReceivedKbs) : 0, expect = held.Count > 0 ? held.Average(h => h.ExpectedKbs) : 0;
        double boxCpu = held.Count > 0 ? held.Max(h => h.Cpu) : 0;
        var health = (n: 0, p50: 0.0, p95: 0.0);
        if (held.Count > 0) { var hs = held.Where(h => h.Health.n > 0).ToList(); if (hs.Count > 0) health = (hs.Sum(h => h.Health.n), hs.Average(h => h.Health.p50), hs.Max(h => h.Health.p95)); }
        Console.WriteLine($"Machines connected : {minUp} of {machines} held through the whole hold; {S.MachineConnectFails} failed to connect, {S.MachineDrops} dropped by the server");
        Console.WriteLine($"Machine connect    : {P(S.Connect.Overall())}");
        Console.WriteLine($"Technician sign-in : {P(S.Login.Overall())}; {S.LoginFails} failed");
        Console.WriteLine($"Machine list       : {P(S.State.Overall())} from opening the console");
        Console.WriteLine($"Session start      : {P(S.SessionStart.Overall())}; {S.SessionFails} failed, {S.SessionsLost} lost during the run");
        Console.WriteLine($"First picture      : {P(S.FirstFrame.Overall())}");
        Console.WriteLine($"Picture relayed    : {recv / 1024:0.0} MB/s of {expect / 1024:0.0} MB/s expected during the hold ({(expect > 0 ? recv / expect * 100 : 100):0}%)");
        Console.WriteLine($"Server /health     : during the hold, median {health.p50:0} ms, worst 95% {health.p95:0} ms; {S.HealthFails} failed");
        Console.WriteLine($"One database read  : {P(S.DbRead.Overall())}");
        var mem = held.Where(h => h.BoxFreeMb >= 0).ToList();
        double boxFree = mem.Count > 0 ? mem.Min(h => h.BoxFreeMb) : -1, boxTotal = mem.Count > 0 ? mem.Max(h => h.BoxTotalMb) : -1;
        Console.WriteLine($"This load server   : busiest {boxCpu:0}% processor, {(held.Count > 0 ? held.Max(h => h.MemMb) : 0):0} MB" +
            (boxFree >= 0 ? $"; the box's free memory went down to {boxFree:0} of {boxTotal:0} MB" : ""));
        if (!S.Errors.IsEmpty)
        {
            Console.WriteLine("What went wrong, most often first:");
            foreach (var e in S.Errors.OrderByDescending(e => e.Value).Take(12)) Console.WriteLine($"  {e.Value,6} x {e.Key}");
        }
        Console.WriteLine();
        var verdict = new List<string>();
        if (boxCpu > 85) verdict.Add("THIS LOAD SERVER WAS THE LIMIT (over 85% busy), not necessarily the platform — split the run across more load servers (--part).");
        if (boxFree >= 0 && boxFree < boxTotal * 0.10) verdict.Add($"THIS LOAD SERVER WAS THE LIMIT (memory: {boxFree:0} MB free of {boxTotal:0}), not necessarily the platform — a bigger flavor or another load server.");
        // The box's own ports to one address (EADDRNOTAVAIL), not the server's: a third load server, not a finding.
        if (S.Errors.Keys.Any(k => k.Contains("Cannot assign requested address") || k.Contains("Address already in use")))
            verdict.Add("THIS LOAD SERVER WAS THE LIMIT (it ran out of its own ports to the server) — add another load server.");
        // A 502 is Caddy saying it could not reach the platform behind it: the platform down, or Caddy out of
        // ports to 127.0.0.1:8090 (about 28,000 by default) — the server watch's "upstream" column says which.
        if (S.Errors.Keys.Any(k => k.Contains("'502'")))
            verdict.Add("Caddy answered 502 (could not reach the platform behind it) — read the server watch's upstream column against its port range.");
        if (minUp < machines) verdict.Add($"Not every machine stayed connected ({minUp} of {machines}).");
        // The rush counts too, not only the calm after it: a machine kept waiting seconds to say hello is what a
        // restart looks like to every customer (BUGS #11 passed the hold "comfortably" for exactly this reason).
        var connect = S.Connect.Overall();
        if (connect.n > 0 && connect.p95 > 2000) verdict.Add($"Machines waited to connect: 95% within {connect.p95 / 1000:0.0} s, where a few milliseconds is normal.");
        if (S.MachineDrops > machines / 100) verdict.Add($"The server dropped {S.MachineDrops} machine connections, which then had to come back.");
        if (S.SessionFails > 0 || S.SessionsLost > 0) verdict.Add($"Sessions failed or were lost ({S.SessionFails} + {S.SessionsLost}).");
        if (expect > 0 && recv < expect * 0.9) verdict.Add($"Pictures were not getting through in full ({recv / expect * 100:0}%) — bandwidth or processor at the server.");
        if (health.p95 > 1000) verdict.Add($"The server was slow to answer at all (/health worst {health.p95:0} ms).");
        if (verdict.Count == 0) Console.WriteLine("VERDICT: the server carried this load comfortably. Try the next step up.");
        else { Console.WriteLine("VERDICT: the server bent at this load:"); foreach (var v in verdict) Console.WriteLine("  - " + v); }
        return verdict.Count == 0 ? 0 : 3;
    }

    /// <summary>Calls start(i) for i in 0..count, spread evenly over seconds.</summary>
    private static async Task Paced(int count, int seconds, Action<int> start)
    {
        var t0 = DateTime.UtcNow;
        for (int i = 0; i < count; i++)
        {
            var due = t0 + TimeSpan.FromSeconds(seconds * (double)i / Math.Max(1, count));
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.FromMilliseconds(1)) await Task.Delay(wait);
            start(i);
        }
    }

    private static async Task WaitUntil(Func<bool> f, int seconds)
    { var e = DateTime.UtcNow.AddSeconds(seconds); while (DateTime.UtcNow < e && !f()) await Task.Delay(250); }
}

sealed class Reading
{
    public double Seconds, SentKbs, ReceivedKbs, ExpectedKbs, Cpu, MemMb, BoxFreeMb = -1, BoxTotalMb = -1;
    public string Stage = "", Server = "";
    public long MachinesUp, AgentsUp, TechsUp, SessionsUp;
    public (int n, double p50, double p95) Connect, Login, State, SessionStart, FirstFrame, Health, DbRead;
}

// =============================================================================================
/// <summary>
/// `loadtest local`: the whole thing on this PC, against a throwaway platform on port 8131 — the
/// proof the tool works before anybody pays for a server. The live server is never touched.
/// </summary>
static class Local
{
    const int Port = 8131;

    public static async Task<int> RunAsync(Options o)
    {
        string http = $"http://127.0.0.1:{Port}";
        var repo = FindRepoRoot();
        var work = Path.Combine(Path.GetTempPath(), "rs-loadtest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var dataDir = Path.Combine(work, "data"); Directory.CreateDirectory(dataDir);
        var serverDir = Path.Combine(work, "server");
        int companies = o.Int("companies", 10), people = Math.Max(2, o.Int("techs", 1));
        Console.WriteLine($"building the platform into {serverDir}");
        var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}{Path.DirectorySeparatorChar}\"", repo, 600000);
        if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
        var exe = Path.Combine(serverDir, OperatingSystem.IsWindows() ? "RemoteSupport.Platform.exe" : "RemoteSupport.Platform");
        var env = new Dictionary<string, string>
        {
            ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
            ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
        };
        HarnessDb.Use(env, dataDir);
        // --env NAME=VALUE: one more setting for the throwaway platform, to measure what it changes.
        if (o.Str("env") is { } extra && extra.Split('=', 2) is [var k, var v]) { env[k] = v; Console.WriteLine($"  (platform setting {k}={v})"); }
        var seedFile = Path.Combine(work, "seed.txt");
        var s = Run(exe, $"seed-loadtest {companies} {people} \"{seedFile}\"", serverDir, 600000, env);
        Console.WriteLine(s.output.Trim());
        if (s.code != 0) { Console.WriteLine("seeding failed"); return 2; }
        // The guard: a second seed on the same database must be refused.
        var again = Run(exe, $"seed-loadtest 1 1 \"{seedFile}.again\"", serverDir, 120000, env);
        bool guarded = again.code != 0 && again.output.Contains("REFUSED");
        Console.WriteLine(guarded ? "  (a second seed on the same database is refused, as it must be)" : "  !! a second seed was NOT refused: " + again.output);
        // --wal: the database switched to write-ahead logging before the platform opens it (the setting is kept in
        // the file), so readers stop waiting for writers — to measure whether that alone ends the stall.
        if (o.Flag("wal"))
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=WAL;";
            Console.WriteLine($"  (database journal mode: {cmd.ExecuteScalar()})");
        }

        var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var log = new ConcurrentQueue<string>();
        Process StartServer()
        {
            var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) log.Enqueue(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) log.Enqueue(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }
        async Task<bool> UpAsync()
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (int i = 0; i < 60; i++) { try { if ((await hc.GetAsync($"{http}/health")).IsSuccessStatusCode) return true; } catch { } await Task.Delay(500); }
            return false;
        }
        var server = StartServer();
        if (!await UpAsync()) { Console.WriteLine("the platform did not come up"); foreach (var l in log.TakeLast(30)) Console.WriteLine(l); server.Kill(true); return 2; }
        // On a database line the platform prints at start-up: which one this run is on (RSP_TEST_DB=postgres for PostgreSQL).
        foreach (var l in log.Where(l => l.Contains("PostgreSQL") || l.Contains("SQLite")).Take(2)) Console.WriteLine("  platform: " + l.Trim());

        // The server's own processor and memory, which only a harness on the same machine can read directly.
        var lastCpu = server.TotalProcessorTime; var lastAt = DateTime.UtcNow;
        string Reading()
        {
            try
            {
                server.Refresh();
                var now = DateTime.UtcNow;
                var cpu = (server.TotalProcessorTime - lastCpu).TotalSeconds / (now - lastAt).TotalSeconds / Environment.ProcessorCount * 100;
                lastCpu = server.TotalProcessorTime; lastAt = now;
                return $"cpu {cpu:0}% mem {server.WorkingSet64 / 1048576} MB threads {server.Threads.Count}";
            }
            catch { return ""; }
        }

        o.Default("server", http); o.Default("seed", seedFile);
        o.Default("machines", "20"); o.Default("sessions", "10"); o.Default("ramp", "15"); o.Default("hold", "30");
        o.Default("kbps", "200"); o.Default("out", Path.Combine(work, "readings.csv"));
        // --storm: the platform stopped and started again under the load, as systemctl restart does on xneelo.
        async Task Restart()
        {
            var at = DateTime.Now;
            try { server.Kill(true); server.WaitForExit(10000); } catch { }
            server = StartServer();
            lastCpu = TimeSpan.Zero; lastAt = DateTime.UtcNow;
            Console.WriteLine($"  (local platform restarted at {at:HH:mm:ss}; {(await UpAsync() ? "answering again" : "NOT answering")} after {(DateTime.Now - at).TotalSeconds:0.0} s)");
        }
        int code;
        try { code = await Runner.RunAsync(o, Reading, o.Flag("storm") ? Restart : null); }
        finally { try { server.Kill(true); server.WaitForExit(5000); } catch { } }
        var errors = log.Where(l => l.Contains("fail:") || l.Contains("Exception")).Take(10).ToList();
        if (errors.Count > 0) { Console.WriteLine("The server logged:"); foreach (var l in errors) Console.WriteLine("  " + l); }
        Console.WriteLine($"server, database, seed and readings kept at {work}");
        return guarded ? code : 4;
    }

    static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
    {
        var p0 = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (env != null) foreach (var kv in env) p0.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(p0)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd() + err.Result;
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
}
