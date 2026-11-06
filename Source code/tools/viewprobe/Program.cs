using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// Black-screen / input probe (docs/HANDOVER.md 6b).
//
// tools/blackscreen proved the tile stream renders correctly when decoded on a plain thread.
// This probe drives the REAL SessionForm — same class, same constructor, opened the same way
// DashboardForm opens it — exercises the viewing modes, screenshots each one, and feeds the
// ScreenView real mouse messages to check that remote control still works in each mode.
//
// Two details that matter:
//   * the pop-out window is closed with WM_SYSCOMMAND/SC_CLOSE — the actual X button.
//     Form.Close() leaves CloseReason at None and takes a different path, which hid a bug once.
//   * a second viewer socket watches the CursorMsg the host announces, so "the console sent
//     input" and "the remote machine acted on it" are measured separately.
//
//   viewprobe <machine> <output-dir> [settleMs]
// ---------------------------------------------------------------------------------------------

internal static class Probe
{
    private const string Http = "http://localhost:8090";
    private const string Ws = "ws://localhost:8090";
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;
    private const int WM_MOUSEMOVE = 0x0200;

    /// <summary>Latest cursor position the host announces, from the observer socket.</summary>
    private static volatile string _remoteCursor = "(none yet)";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static void Main(string[] rawArgs)
    {
        string want = rawArgs.Length > 0 ? rawArgs[0] : "DESKTOP-L2B85US";
        string outDir = rawArgs.Length > 1 ? rawArgs[1] : AppContext.BaseDirectory;
        int settleMs = rawArgs.Length > 2 && int.TryParse(rawArgs[2], out var h) ? h : 6000;
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var brand = Branding.Load();
        // AppSurface.Technician: this probe drives the technician console's own window, so it
        // must be built against the palette that console is drawn with. The parameter became
        // required when tenants got to choose their own appearance (222a8a5) and this harness
        // was left behind, so it has not compiled since.
        var theme = Theme.FromBranding(brand, AppSurface.Technician);

        var (token, session, endSession) = StartSession(want).GetAwaiter().GetResult();
        if (session == null) { Console.WriteLine("could not start a session"); return; }
        Console.WriteLine($"session {session.SessionId} on {session.MachineName}");

        Observe(token, session.SessionId);

        // A hidden host form so the session window is created from inside a running message
        // loop — exactly the situation DashboardForm.OpenSession creates.
        var host = new Form { Opacity = 0, ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        host.Shown += async (_, _) =>
        {
            var f = new SessionForm(Ws, token, session, theme, brand);
            f.Show();                                     // same call DashboardForm makes
            Console.WriteLine("session window shown; waiting for the first frame...");
            await Task.Delay(settleMs);

            Shoot(f, Path.Combine(outDir, "1-docked.png"), "docked");
            await InputTest(f, "docked");

            Invoke(f, "TogglePopOut");
            await Task.Delay(2500);
            var popped = Field<Form>(f, "_popped");
            if (popped != null) Shoot(popped, Path.Combine(outDir, "2-popped-out.png"), "popped out");
            else Console.WriteLine("pop out: no popped window appeared");
            await InputTest(f, "popped out");

            if (popped != null)
            {
                Console.WriteLine("--> the X button on the pop-out window (WM_SYSCOMMAND/SC_CLOSE)");
                PostMessage(popped.Handle, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            }
            await Task.Delay(2500);

            var after = Field<Form>(f, "_popped");
            Console.WriteLine($"    _popped after the X : {(after == null ? "null - popped back in, correct" : $"STILL A WINDOW (visible={after.Visible}, children={after.Controls.Count}) - WRONG")}");
            Console.WriteLine($"    screen card children: {CardChildren(f)}");
            Shoot(f, Path.Combine(outDir, "3-after-x-button.png"), "after the X");
            await InputTest(f, "after pop in");

            Invoke(f, "ToggleFullScreen");
            await Task.Delay(2000);
            Shoot(f, Path.Combine(outDir, "4-full-screen.png"), "full screen");
            await InputTest(f, "full screen");
            Invoke(f, "ToggleFullScreen");
            await Task.Delay(1500);

            // "Leave / End" is just Close().
            Console.WriteLine("--> Leave / End  (f.Close())");
            try { f.Close(); } catch (Exception ex) { Console.WriteLine("    Close() threw: " + ex.Message); }
            await Task.Delay(1500);
            Console.WriteLine(f.IsDisposed
                ? "    session window closed - correct"
                : $"    *** the session window REFUSED to close (Visible={f.Visible}) ***");

            endSession();
            await Task.Delay(700);
            Application.Exit();
        };
        Application.Run(host);

        Console.WriteLine();
        Console.WriteLine($"---- {Trace.Path_} ----");
        try { foreach (var line in File.ReadLines(Trace.Path_).Reverse().Take(80).Reverse()) Console.WriteLine(line); }
        catch (Exception ex) { Console.WriteLine("could not read trace: " + ex.Message); }
    }

    /// <summary>
    /// Feeds the ScreenView a real WM_MOUSEMOVE at the centre of the control and reports what the
    /// host says the remote cursor did. Separates "the console never sent input" from "the host
    /// ignored it" — the console side is in the trace log, the host side is this cursor value.
    /// </summary>
    private static async Task InputTest(Form f, string label)
    {
        var view = Field<Control>(f, "_view");
        if (view == null || !view.IsHandleCreated) { Console.WriteLine($"    input [{label}]: no ScreenView handle"); return; }
        string before = _remoteCursor;
        int x = view.Width / 2, y = view.Height / 2;
        PostMessage(view.Handle, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
        await Task.Delay(1500);
        string parent = view.Parent?.GetType().Name ?? "none";
        string verdict = before != _remoteCursor ? "REMOTE CURSOR MOVED" : "remote cursor did NOT move";
        Console.WriteLine($"    input [{label}]: fed mouse at {x},{y} of {view.Width}x{view.Height} (parent={parent})");
        Console.WriteLine($"                    before={before}  after={_remoteCursor}  -> {verdict}");

        // Keyboard needs focus; the mouse does not. Report it separately, because "keyboard stopped
        // working after pop out" and "mouse stopped working" have completely different causes.
        var owner = view.FindForm();
        Console.WriteLine($"                    focus: view.Focused={view.Focused}, CanFocus={view.CanFocus}, " +
                          $"owner={(owner == null ? "none" : owner.Text)}, owner.ActiveControl={(owner?.ActiveControl?.GetType().Name ?? "null")}, " +
                          $"ActiveForm={(Form.ActiveForm?.Text ?? "null")}");
    }

    /// <summary>Opens an extra viewer socket and records the cursor positions the host announces.</summary>
    private static void Observe(string token, string sessionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var w = new ClientWebSocket();
                await w.ConnectAsync(new Uri($"{Ws}/ws/session?role=viewer&id={sessionId}&token={token}"), default);
                var c = new WsConnection(w);
                c.OnMessage += m => { if (m is CursorMsg cm) _remoteCursor = $"{cm.X},{cm.Y} (mon {cm.Monitor})"; };
                await c.RunAsync();
            }
            catch (Exception ex) { Console.WriteLine("observer socket failed: " + ex.Message); }
        });
    }

    /// <summary>Names of the controls sitting in the screen card — a leftover "popNote" is a symptom.</summary>
    private static string CardChildren(Form f)
    {
        var card = Field<Control>(f, "_screenCard");
        if (card == null) return "(no card)";
        return string.Join(", ", card.Controls.Cast<Control>().Select(c => $"{c.GetType().Name}{(string.IsNullOrEmpty(c.Name) ? "" : "[" + c.Name + "]")}"));
    }

    /// <summary>Screenshot a form and report how much of it is not black, so a black frame is obvious.</summary>
    private static void Shoot(Form f, string path, string label)
    {
        try
        {
            using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
            f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            double sum = 0; int n = 0, bright = 0;
            for (int y = 0; y < bmp.Height; y += 7)
            for (int x = 0; x < bmp.Width; x += 7)
            { var c = bmp.GetPixel(x, y); double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; sum += l; n++; if (l > 28) bright++; }
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine($"{label,-18} mean luminance {sum / Math.Max(1, n):0.0}/255, {100.0 * bright / Math.Max(1, n):0.0}% non-black  -> {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Console.WriteLine($"{label,-18} screenshot failed: {ex.Message}"); }
    }

    private static void Invoke(object target, string method)
    {
        Console.WriteLine($"--> {method}()");
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    }

    private static T? Field<T>(object target, string name) where T : class =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as T;

    private static async Task<(string token, SessionInfo? session, Action end)> StartSession(string want)
    {
        using var hc = new HttpClient();
        var creds = RemoteSupport.Tools.RsCredentials.Load();
        var lr = await hc.PostAsJsonAsync($"{Http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
        var token = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

        var tech = new ClientWebSocket();
        await tech.ConnectAsync(new Uri($"{Ws}/ws/tech?token={token}"), default);
        var tc = new WsConnection(tech);
        var q = new ConcurrentQueue<Msg>();
        tc.OnMessage += m => q.Enqueue(m);
        _ = tc.RunAsync();

        async Task<T?> Wait<T>(int ms, Func<T, bool>? f = null) where T : Msg
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
            return null;
        }

        var st = await Wait<StateMsg>(8000);
        var dev = st?.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (dev == null) return (token, null, () => { });

        tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "viewprobe diagnostic" });
        var ready = await Wait<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
        if (ready == null) return (token, null, () => { });

        return (token, ready.Session, () => tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }));
    }
}
