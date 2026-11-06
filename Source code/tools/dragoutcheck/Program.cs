using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// The console's half of moving files, on THIS machine alone. No server, no session, no customer
// machine, about four seconds.
//
// The wire was already proven — treecheck 19 of 19 both ways against real hardware, clipcheck
// 7 of 7, filesetcheck 20 checks on the engine. What had never been checked by anything is what
// the CONSOLE does, and that is where every remaining fault of 2026-09-15 turned out to live.
// Three things are asked here:
//
//   1. DRAG OUT. A real drag is a Windows shell interaction rather than a message, so there was
//      no obvious way to test it and it had never been built. What CAN be asserted is what we
//      hand the shell: that the files exist in the staging folder BEFORE the data object is
//      built, that the CF_HDROP names exactly those files and nothing outside that folder, and —
//      the part that is not self-marking — that Windows actually delivers them. CHECK 4 performs
//      a REAL DoDragDrop with REAL mouse input into a REAL drop target and compares SHA-256 of
//      what arrives. That is the difference between "we built a file list" and "the drop worked".
//
//   2. THE FILES TAB opening on something. It used to open blank, which is indistinguishable
//      from a broken transfer and was reported as one for a fortnight (docs/BUGS.md #5). This
//      runs the REAL SessionForm against a REAL websocket that this harness answers as if it
//      were the customer's machine, so the assertion is on the message that actually left the
//      console — not on a flag the console set itself, which would agree with the bug.
//
//   3. %DESKTOP% resolving. One place, on arrival (docs/BUGS.md #6). Asserted against the real
//      HostPaths the host uses.
//
//   dotnet run -c Release --project tools/dragoutcheck/dragoutcheck.csproj
//   dotnet run -c Release --project tools/dragoutcheck/dragoutcheck.csproj -- --keep
//
// It moves the mouse pointer for about a second during CHECK 4. That is the point of CHECK 4.
// ---------------------------------------------------------------------------------------------

internal static class DragOutCheck
{
    private static int _pass, _fail;
    private static string _outDir = Path.Combine(AppContext.BaseDirectory, "dragout");

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool keep = args.Contains("--keep");
        var outArg = Array.IndexOf(args, "--out");
        if (outArg >= 0 && outArg + 1 < args.Length) _outDir = args[outArg + 1];
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine();
        Console.WriteLine("== CHECK 1: the staging folder ==");
        var stage1 = StagingChecks();

        Console.WriteLine();
        Console.WriteLine("== CHECK 2: what goes into the CF_HDROP, and what must not ==");
        HdropChecks(stage1);

        Console.WriteLine();
        Console.WriteLine("== CHECK 3: the ordering — bytes before paths ==");
        OrderingChecks();

        Console.WriteLine();
        Console.WriteLine("== CHECK 4: a real drag, into a real drop target ==");
        RealDragCheck();

        Console.WriteLine();
        Console.WriteLine("== CHECK 5: the Files tab asks the machine when it opens ==");
        FilesTabCheck();

        Console.WriteLine();
        Console.WriteLine("== CHECK 6: %DESKTOP% resolves once, on arrival ==");
        SentinelChecks();

        if (!keep)
        {
            try { if (Directory.Exists(DragOut.StagingRoot)) Directory.Delete(DragOut.StagingRoot, true); } catch { }
        }
        else Console.WriteLine($"\n  kept: {DragOut.StagingRoot}");

        Console.WriteLine();
        Console.WriteLine($"  {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 1: the staging folder
    private static string StagingChecks()
    {
        var a = DragOut.NewStagingDir("DESKTOP-OPHQIDH");
        var b = DragOut.NewStagingDir("DESKTOP-OPHQIDH");
        Check("a staging folder is created", Directory.Exists(a), a);
        // Two drags in a row must not mix: the second would otherwise pick up the first's files
        // and drop more than was grabbed, which is the kind of fault nobody reports because it
        // looks like they mis-clicked.
        Check("each drag gets its own folder", a != b && Directory.Exists(b));
        Check("it is under the temp folder, not Downloads",
              a.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), DragOut.StagingRoot);

        // A machine name is not a file name. ":" and "\" in one would put the staging folder
        // somewhere else entirely, and "somewhere else entirely" is what everything below guards.
        var odd = DragOut.NewStagingDir(@"C:\bad<name>");
        Check("a hostile machine name cannot escape the staging root",
              Path.GetFullPath(odd).StartsWith(Path.GetFullPath(DragOut.StagingRoot), StringComparison.OrdinalIgnoreCase),
              Path.GetFileName(odd));

        // The sweep runs at session start and must take yesterday's folders without taking one
        // a drop might still be copying out of.
        Directory.SetCreationTime(a, DateTime.Now.AddDays(-2));
        DragOut.SweepOlderThan(TimeSpan.FromHours(12));
        Check("the sweep removes an old folder", !Directory.Exists(a));
        Check("the sweep leaves a fresh one alone", Directory.Exists(b));
        return b;
    }

    // ------------------------------------------------------------------ 2: the file list
    private static void HdropChecks(string stage)
    {
        var one = Path.Combine(stage, "error.png");
        var two = Path.Combine(stage, "a folder");
        File.WriteAllBytes(one, new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(two);
        File.WriteAllText(Path.Combine(two, "inside.txt"), "x");

        var data = DragOut.Build(stage, new[] { one, two }, out var why);
        Check("a data object is built for real staged items", data != null, why);
        if (data != null)
        {
            var got = data.GetFileDropList().Cast<string>().ToList();
            Check("the CF_HDROP names exactly what was staged",
                  got.Count == 2 && got.Contains(one) && got.Contains(two), string.Join(" | ", got));
            Check("every path in it exists on disk",
                  got.All(p => File.Exists(p) || Directory.Exists(p)));
            // Without this some targets offer a MOVE, and a move empties the staging folder — so
            // the same drag done twice would fail the second time, which reads as intermittent.
            var eff = data.GetData("Preferred DropEffect") as MemoryStream;
            var val = eff != null ? BitConverter.ToInt32(eff.ToArray(), 0) : -1;
            Check("it asks for a COPY, not a move", val == 1, "Preferred DropEffect = " + val);
        }

        // The names in a file set came off ANOTHER machine. A drop hands this list straight to
        // Explorer, which would copy whatever is named, so the same rule the receiving side
        // learned in filesetcheck applies here: validate before you hand it on.
        foreach (var hostile in new[]
                 {
                     Path.Combine(stage, @"..\..\escaped.txt"),
                     @"C:\Windows\System32\config\SAM",
                     @"\\attacker\share\x.txt"
                 })
        {
            var refused = DragOut.Build(stage, new[] { hostile }, out var w);
            Check($"refuses a path outside the staging folder: {Short(hostile)}", refused == null, w);
        }

        // One bad entry condemns the set — the rule the host already follows. A drag that
        // silently dropped two of the three things grabbed would be worse than one that failed.
        var mixed = DragOut.Build(stage, new[] { one, @"C:\Windows\notepad.exe" }, out var w2);
        Check("one bad entry condemns the whole drag", mixed == null, w2);

        var empty = DragOut.Build(stage, Array.Empty<string>(), out var w3);
        Check("nothing fetched means no drag at all", empty == null, w3);
    }

    // ------------------------------------------------------------------ 3: the ordering
    private static void OrderingChecks()
    {
        // This is the whole reason drag-out was hard. DoDragDrop hands the far end PATHS; if the
        // bytes have not landed, Explorer copies from a path to nothing and the drop fails with
        // no error worth reading. So a data object must be refusable on exactly that ground.
        var stage = DragOut.NewStagingDir("ordering");
        var promised = Path.Combine(stage, "not-here-yet.bin");

        var tooEarly = DragOut.Build(stage, new[] { promised }, out var why);
        Check("a drag is refused while the file is still in flight", tooEarly == null, why);

        File.WriteAllBytes(promised, RandomNumberGenerator.GetBytes(4096));
        var nowOk = DragOut.Build(stage, new[] { promised }, out var why2);
        Check("and allowed the moment the bytes have landed", nowOk != null, why2);

        // A folder that arrived empty is still something somebody grabbed, and an empty folder
        // carries meaning — filesetcheck makes the same point about the engine.
        var emptyDir = Path.Combine(stage, "empty");
        Directory.CreateDirectory(emptyDir);
        Check("an empty folder can be dragged", DragOut.Build(stage, new[] { emptyDir }, out _) != null);
    }

    // ------------------------------------------------------------------ 4: the real thing
    //
    // Everything above asserts on what we built. This asserts on what WINDOWS DELIVERED, which is
    // the only part that cannot be satisfied by agreeing with ourselves.
    private static void RealDragCheck()
    {
        var stage = DragOut.NewStagingDir("realdrag");
        var file = Path.Combine(stage, "dragged.bin");
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        File.WriteAllBytes(file, bytes);

        var data = DragOut.Build(stage, new[] { file }, out var why);
        if (data == null) { Check("a data object to drag", false, why); return; }

        List<string>? dropped = null;
        Exception? boom = null;
        DragDropEffects effect = DragDropEffects.None;

        using var form = new Form
        {
            Text = "dragoutcheck", StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80), ClientSize = new Size(560, 220), TopMost = true
        };
        var source = new Panel { Bounds = new Rectangle(20, 20, 200, 180), BackColor = Color.Gainsboro };
        var target = new Panel { Bounds = new Rectangle(320, 20, 200, 180), BackColor = Color.LightSteelBlue, AllowDrop = true };
        target.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        target.DragOver += (_, e) => e.Effect = DragDropEffects.Copy;
        target.DragDrop += (_, e) =>
            dropped = (e.Data?.GetData(DataFormats.FileDrop) as string[])?.ToList() ?? new List<string>();
        form.Controls.Add(source); form.Controls.Add(target);
        form.Show();
        Pump(15);

        var from = source.PointToScreen(new Point(source.Width / 2, source.Height / 2));
        var to = target.PointToScreen(new Point(target.Width / 2, target.Height / 2));

        // The button goes down first and DoDragDrop is called with it still down, which is what a
        // real drag is. The movement and the release come from another thread, because
        // DoDragDrop runs its own modal loop on this one and will not return until the gesture
        // ends.
        Cursor.Position = from;
        Pump(5);
        Mouse(MOUSEEVENTF_LEFTDOWN);

        var mover = new Thread(() =>
        {
            Thread.Sleep(120);
            for (int i = 1; i <= 12; i++)
            {
                Cursor.Position = new Point(from.X + (to.X - from.X) * i / 12, from.Y + (to.Y - from.Y) * i / 12);
                Thread.Sleep(25);
            }
            Thread.Sleep(120);
            Mouse(MOUSEEVENTF_LEFTUP);
        }) { IsBackground = true };
        mover.Start();

        try { effect = source.DoDragDrop(data, DragDropEffects.Copy); }
        catch (Exception ex) { boom = ex; }
        finally { Mouse(MOUSEEVENTF_LEFTUP); }   // never leave the button stuck down
        Pump(20);

        if (boom != null) { Check("a real DoDragDrop into a real drop target", false, boom.Message); }
        else
        {
            Check("Windows carried the drag to the drop target", dropped != null,
                  $"DoDragDrop returned {effect}");
            if (dropped != null)
            {
                Check("the drop target received exactly the staged file",
                      dropped.Count == 1 && string.Equals(dropped[0], file, StringComparison.OrdinalIgnoreCase),
                      string.Join(" | ", dropped));
                // By SHA-256, not by name. A path that arrives correctly and points at the wrong
                // bytes is the failure a name comparison cannot see.
                var ok = dropped.Count == 1 && File.Exists(dropped[0]) &&
                         Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dropped[0]))) ==
                         Convert.ToHexString(SHA256.HashData(bytes));
                Check("and the bytes behind it are the bytes that were staged", ok);
                Check("the drop reported a copy", effect == DragDropEffects.Copy, effect.ToString());
            }
        }
        form.Close();
        Pump(5);
    }

    // ------------------------------------------------------------------ 5: the Files tab
    //
    // The REAL SessionForm, talking to a REAL websocket that this harness answers as if it were
    // the customer's machine. Asserting on the console's own bookkeeping would be no test at all:
    // the bug being guarded against is the console believing it asked when it never did.
    private static void FilesTabCheck()
    {
        int port = FreePort();
        var asked = new List<FileListRequestMsg>();
        var gate = new ManualResetEventSlim();
        WsConnection? server = null;
        bool answeredWithError = false;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var ctx = await listener.GetContextAsync();
                    if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                    var wsc = await ctx.AcceptWebSocketAsync(null);
                    var conn = new WsConnection(wsc.WebSocket);
                    server = conn;
                    conn.OnMessage += m =>
                    {
                        if (m is not FileListRequestMsg fl) return;
                        lock (asked) asked.Add(fl);
                        gate.Set();
                        // Answer the FIRST request the way a machine at the logon screen would —
                        // with the error the real host produces — so the fallback is exercised for
                        // real rather than reasoned about.
                        if (!answeredWithError)
                        {
                            answeredWithError = true;
                            conn.Send(new FileListMsg
                            {
                                Path = fl.Path,
                                Error = @"Could not find a part of the path 'C:\Users\Nobody\Desktop'"
                            });
                        }
                        else conn.Send(new FileListMsg
                        {
                            Path = "",
                            Entries = new List<FileEntry> { new() { Name = @"C:\", IsDir = true } }
                        });
                    };
                    // Every handler attached BEFORE the pump: the trap of §10, and it applies to a
                    // harness exactly as it does to the product.
                    _ = conn.RunAsync();
                }
            }
            catch { }
        });

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        var session = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "PROBE-PC", StartedBy = "Probe", Unattended = true };
        using var f = new SessionForm($"ws://127.0.0.1:{port}", "probe-token", session, theme, brand);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1320, 850);
        f.Show();

        // Wait for the viewer socket to come up before touching the tab: a message sent into a
        // socket that is not open yet is dropped with a trace line and nothing else, which would
        // make this check fail for a reason that has nothing to do with the Files tab.
        for (int i = 0; i < 100 && server == null; i++) Pump(2);
        Check("the console's viewer socket connected", server != null);

        // Nothing should have been asked before the Files window is opened.
        Pump(10);
        Check("nothing is asked before the Files window is opened", asked.Count == 0);

        Invoke(f, "OpenTool", 1);
        gate.Wait(5000);
        Pump(40);

        lock (asked)
        {
            Check("opening the Files window asks the machine for a listing", asked.Count >= 1,
                  asked.Count == 0 ? "nothing arrived" : "first: " + asked[0].Path);
            if (asked.Count >= 1)
                Check("and asks for the customer's DESKTOP by sentinel, not a guessed path",
                      asked[0].Path == RemotePaths.Desktop, asked[0].Path);
            // The fallback: their desktop would not list, so the drives were asked for instead.
            // Without this the tab opens on a red error line, which is the fault it was built to
            // remove wearing a different hat.
            Check("a desktop that will not list falls back to the drives",
                  asked.Count >= 2 && asked[1].Path == "",
                  asked.Count >= 2 ? $"second: '{asked[1].Path}'" : "there was no second request");
        }

        // Once per session, not once per click — the Sound tab's pattern. Selecting away and back
        // must not re-ask, or every glance at the tab costs the customer's machine a listing and
        // throws away wherever the technician had browsed to.
        int before;
        lock (asked) before = asked.Count;
        Invoke(f, "OpenTool", 0);
        Invoke(f, "OpenTool", 1);
        Pump(30);
        lock (asked)
            Check("it asks once per session, not on every click", asked.Count == before,
                  $"{asked.Count - before} extra request(s)");

        // Finally, answer with a desktop that looks like a real one and photograph the tab. The
        // numbers above say the message left; a picture is the only thing that says what the
        // technician is actually looking at, and it is the only reason the Listen button's
        // overlap was caught before it shipped (§18).
        server?.Send(new FileListMsg
        {
            Path = @"C:\Users\Lenovo\Desktop",
            Entries = new List<FileEntry>
            {
                new() { Name = "Invoices", IsDir = true, Modified = DateTime.Now.AddDays(-9) },
                new() { Name = "error at startup.png", Size = 184_320, Modified = DateTime.Now.AddHours(-2) },
                new() { Name = "sage backup.zip", Size = 48_233_984, Modified = DateTime.Now.AddDays(-1) },
                new() { Name = "Shortcut to accounts.lnk", Size = 1_284, Modified = DateTime.Now.AddDays(-40) },
            }
        });
        Pump(30);
        try
        {
            Directory.CreateDirectory(_outDir);
            // The Files page is its own WINDOW since stage 3.5.4, so photographing the session
            // window would photograph the remote desktop and nothing else. Found by its title
            // rather than by reflecting into SessionForm''s privates: if the window a technician
            // sees is not among the application''s open forms, that is itself the fault.
            var shot = Path.Combine(_outDir, "files-tab.png");
            var filesWin = Application.OpenForms.Cast<Form>()
                                      .FirstOrDefault(w => w.Text.StartsWith("Files", StringComparison.Ordinal));
            Check("the Files page is a window of its own, and it is open", filesWin != null,
                  filesWin == null ? "no window titled Files" : filesWin.Text + " " + filesWin.Bounds);
            var target = filesWin ?? f;
            using var bmp = new Bitmap(target.Width, target.Height);
            target.DrawToBitmap(bmp, new Rectangle(0, 0, target.Width, target.Height));
            bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  shot  {shot}");
        }
        catch (Exception ex) { Console.WriteLine("  (no screenshot: " + ex.Message + ")"); }

        f.Close();
        Pump(5);
        try { listener.Stop(); } catch { }
    }

    // ------------------------------------------------------------------ 6: the sentinel
    private static void SentinelChecks()
    {
        // Deliberately NOT compared against UserContext, which is where the answer comes from:
        // a check that asks the code what it thinks and then agrees passes just as happily when
        // the code is wrong. These are the properties the path has to have, stated separately.
        var desktop = RemoteSupport.Host.HostPaths.Resolve(RemotePaths.Desktop) ?? "";
        var downloads = RemoteSupport.Host.HostPaths.Resolve(RemotePaths.Downloads) ?? "";

        // The bug itself: an unresolved sentinel reaching FileTransfer is treated as a RELATIVE
        // path and resolved against the host's working directory, which is Program Files. The
        // point of resolving on arrival is that nothing below can be handed one.
        Check("%DESKTOP% resolves to an absolute path — a relative one is BUGS #6 exactly",
              Path.IsPathFullyQualified(desktop), desktop);
        Check("it names a Desktop folder", desktop.EndsWith(@"\Desktop", StringComparison.OrdinalIgnoreCase));
        Check("it exists on this machine", Directory.Exists(desktop));
        // The reason the sentinel exists at all: the host runs as SYSTEM, whose own profile is
        // C:\Windows\System32\config\systemprofile — not where anybody expects their desktop.
        Check("it is not SYSTEM's profile",
              desktop.IndexOf(@"config\systemprofile", StringComparison.OrdinalIgnoreCase) < 0);
        Check("%DOWNLOADS% resolves too",
              Path.IsPathFullyQualified(downloads) && downloads.EndsWith(@"\Downloads", StringComparison.OrdinalIgnoreCase),
              downloads);
        Check("it is case-insensitive, like every other path on Windows",
              RemoteSupport.Host.HostPaths.Resolve("%desktop%") == desktop);

        // %PASTE% is where a copy waits for the customer to paste it. Three things have to be
        // true of it and none of them is obvious from the name.
        var paste1 = RemoteSupport.Host.HostPaths.Resolve(RemotePaths.PasteStaging)!;
        var paste2 = RemoteSupport.Host.HostPaths.Resolve(RemotePaths.PasteStaging)!;
        Check("%PASTE% resolves under the user's temp, not into a folder they look at",
              Path.IsPathFullyQualified(paste1)
              && paste1.Contains(@"\Temp\RemoteSupport\paste\", StringComparison.OrdinalIgnoreCase),
              paste1);
        // Their profile's temp, not SYSTEM's: Explorer reads these paths AS THE USER when the
        // paste happens, and a staging folder under SYSTEM's profile is one they cannot open.
        var profile = desktop[..^@"\Desktop".Length];
        Check("and under the interactive user's profile rather than SYSTEM's",
              paste1.StartsWith(profile, StringComparison.OrdinalIgnoreCase),
              "the same profile %DESKTOP% resolves under: " + profile);
        // A folder of its own per paste. One shared folder would mean two pastes in a row mixing
        // their files, or the second being collision-renamed around the first.
        Check("and a different folder every time, so two pastes cannot mix", paste1 != paste2,
              Path.GetFileName(paste1) + " then " + Path.GetFileName(paste2));

        // And everything that is not a sentinel must come through untouched, including the three
        // spellings FileTransfer.List reads as "the drives".
        foreach (var plain in new[] { @"C:\Users\Lenovo\Desktop", "", @"\", "/" })
            Check($"a real path is left alone: '{plain}'", RemoteSupport.Host.HostPaths.Resolve(plain) == plain);

        // An unknown name must not be quietly turned into a relative path either.
        Check("an unknown sentinel is not invented", RemoteSupport.Host.HostPaths.Resolve("%MUSIC%") == "%MUSIC%");
    }

    // ------------------------------------------------------------------ plumbing
    private static void Invoke(object target, string method, params object[] a) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, a);

    private static void Pump(int ticks)
    {
        for (int i = 0; i < ticks; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string Short(string s) => s.Length <= 42 ? s : "…" + s[^41..];

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    private static void Mouse(uint flags)
    {
        var inp = new INPUT { type = 0 };
        inp.mi.dwFlags = flags;
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }

    // Explicit layout, and the size is the thing to get right: a struct whose declared size is
    // wrong is how the PROPVARIANT bug in §18 wrote eight bytes past a stack slot and produced a
    // fault that moved when you looked at it.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
