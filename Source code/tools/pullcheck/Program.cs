using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// "The customer copies; it must end up on MY clipboard" — the console's half, with no server, no
// host and no customer machine.
//
// WHY THIS EXISTS. §26 named this gap the day the mirror was built and it stayed open:
//
//     "clipcheck is a viewer, not the console. It proves the host speaks and what it says. It
//      does NOT exercise SessionForm: the automatic fetch onto the technician's own clipboard
//      after a customer copies files has only ever run locally."
//
// pastecheck covers the opposite direction — we copy, they paste. Nothing covered this one, and
// on 2026-09-21 Clint reported exactly it: pasting INTO the customer's machine works now, and
// copying FROM it does not.
//
// So this stands a WebSocket server up on a loopback port, points the REAL SessionForm at it
// through the REAL SessionLink, and plays the customer's machine end to end:
//
//   1. the customer copies TEXT     -> ClipboardMsg{Text}  -> it must land on this clipboard
//   2. the customer copies FILES    -> ClipboardMsg{Files} -> the console must ask for them
//      (FileSetPullMsg), and this end answers with a real offer and real bytes, and the fetched
//      paths must land on this clipboard, ready for Ctrl+V
//   3. a set too big to move on a gesture must NOT be fetched unasked
//   4. with the technician's OWN files on this clipboard, a big set from them must still take
//      those away — or Ctrl+V here pastes the technician's files as if they were the customer's
//   5. the same for a small set, looked at WHILE it is being fetched, and then once it lands
//      (added 2026-09-29; steps 1-3 empty the clipboard first and so cannot see this)
//
// It uses the machine's real clipboard, so it takes the same named mutex as clipmirror and
// pastecheck and SKIPS rather than failing when it cannot win it (§27: a harness that cries wolf
// costs more than one that does not exist).
//
//   dotnet run -c Release --project tools/pullcheck/pullcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class PullCheck
{
    private static int _pass, _fail;
    private static readonly ManualResetEventSlim _serveGate = new(true);

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("pullcheck — the customer copies; it must arrive on the technician's clipboard");
        Console.WriteLine();

        using var clipboardTurn = new Mutex(false, @"Global\RemoteSupport-clipboard-harness");
        bool gotTurn = false;
        try { gotTurn = clipboardTurn.WaitOne(TimeSpan.FromMinutes(3)); }
        catch (AbandonedMutexException) { gotTurn = true; }
        if (!gotTurn)
        {
            Console.WriteLine("another clipboard harness has been running for three minutes; not starting a second one.");
            return 2;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---- the files the customer "has" -------------------------------------------------------
        // Real files on this machine, standing in for files on theirs. The console asks for them
        // by path and this end serves them, which is exactly what a host does.
        var theirs = Path.Combine(Path.GetTempPath(), "pullcheck-theirs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(theirs);
        var a = Path.Combine(theirs, "from the customer.txt");
        var b = Path.Combine(theirs, "second one.txt");
        var c = Path.Combine(theirs, "thìrd é.txt");
        File.WriteAllText(a, new string('a', 4096));
        File.WriteAllText(b, new string('b', 2048));
        File.WriteAllText(c, new string('c', 1024));
        var theirPaths = new List<string> { a, b, c };

        int port = FreePort();
        var pulls = new ConcurrentQueue<FileSetPullMsg>();
        var accepts = new ConcurrentQueue<FileSetAcceptMsg>();
        var dones = new ConcurrentQueue<FileSetDoneMsg>();
        var connected = new ManualResetEventSlim(false);
        WsConnection? far = null;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                var wsc = await ctx.AcceptWebSocketAsync(null);
                var conn = new WsConnection(wsc.WebSocket);

                // Handlers before RunAsync, always (§10).
                conn.OnMessage += m =>
                {
                    if (m is FileSetPullMsg p)
                    {
                        pulls.Enqueue(p);
                        // Answer the way a real host does: plan the tree on THIS side, offer it
                        // under the SetId the console chose, then stream it once accepted.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Held shut by step 5, so the moment between "they copied" and
                                // "it landed" can be looked at rather than raced.
                                _serveGate.Wait(20000);
                                var plan = FileSetSender.Plan(p.Paths);
                                conn.Send(new FileSetOfferMsg
                                {
                                    SetId = p.SetId, Entries = plan.Entries, TotalBytes = plan.TotalBytes,
                                    Label = plan.Label, TargetDir = null
                                });
                                // Wait for the console to accept before sending a byte.
                                var end = Environment.TickCount64 + 15000;
                                FileSetAcceptMsg? acc = null;
                                while (Environment.TickCount64 < end && acc == null)
                                {
                                    foreach (var x in accepts) if (x.SetId == p.SetId) { acc = x; break; }
                                    if (acc == null) await Task.Delay(25);
                                }
                                if (acc is not { Accept: true }) return;
                                await FileSetSender.SendAsync(p.SetId, plan, bin => conn.SendBinary(bin), null, CancellationToken.None);
                            }
                            catch (Exception ex) { Console.WriteLine("  (the fake host failed to serve the pull: " + ex.Message + ")"); }
                        });
                    }
                    if (m is FileSetAcceptMsg fa) accepts.Enqueue(fa);
                    if (m is FileSetDoneMsg fd) dones.Enqueue(fd);
                };
                far = conn;
                connected.Set();
                _ = conn.RunAsync();
            }
        });

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        var session = new SessionInfo
        {
            SessionId = "pull", DeviceId = "pull", MachineName = "PULL-PC",
            StartedBy = "Probe", Unattended = true
        };

        int rc = 1;
        using (var f = new SessionForm($"ws://127.0.0.1:{port}", "pull-token", session, theme, brand))
        {
            f.Show();
            Pump(300);

            if (!connected.Wait(10000)) { Check("the console's viewer socket connected", false, "nothing arrived in 10s"); goto done; }
            Check("the console's viewer socket connected", true, $"on 127.0.0.1:{port}");
            Pump(400);

            // ---- 1. text ------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("THE CUSTOMER COPIES TEXT");
            if (!ClearClipboard(out var clearWhy))
            {
                Console.WriteLine($"  SKIP  this machine's clipboard is in use — {clearWhy}");
                rc = 2; goto done;
            }
            var text = "pullcheck " + Guid.NewGuid().ToString("N") + "\twith a tab and é";
            far!.Send(new ClipboardMsg { Text = text, Auto = true });
            var gotText = WaitFor(() => ReadText() == text, 8000);
            Check("their text lands on the technician's clipboard", gotText,
                gotText ? $"{text.Length} characters, tab and non-ASCII included"
                        : $"after 8s the clipboard held: {Describe(ReadText())}");

            // ---- 2. files -------------------------------------------------------------------------
            // The one that matters, and the one nothing has ever tested.
            Console.WriteLine();
            Console.WriteLine("THE CUSTOMER COPIES FILES");
            ClearClipboard(out _);
            long bytes = theirPaths.Sum(p => new FileInfo(p).Length);
            far.Send(new ClipboardMsg { Files = theirPaths, Bytes = bytes, Auto = true });

            var pull = Dequeue(pulls, 10000, () => Pump(50));
            Check("the console asks the customer's machine for the files it was told about", pull != null,
                pull != null ? $"{pull.Paths.Count} path(s)" : "no FileSetPullMsg in 10s — the console never asked");
            if (pull == null) goto done;
            Check("it asks for all three, not just the first", pull.Paths.Count == 3,
                string.Join(", ", pull.Paths.Select(Path.GetFileName)));

            var done1 = Dequeue(dones, 30000, () => Pump(50), d => d.SetId == pull.SetId);
            Check("the transfer completes", done1 is { Ok: true },
                done1 == null ? "no FileSetDoneMsg in 30s" : done1.Ok ? $"{done1.SavedPaths.Count} file(s) into {done1.SavedDir}" : "failed: " + done1.Error);

            // THE CHECK THIS HARNESS EXISTS FOR.
            Pump(1200);
            var onClip = ReadFiles();
            bool landed = onClip.Count == 3 && theirPaths.All(p => onClip.Any(q => Path.GetFileName(q).Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)));
            Check("the fetched files are on the technician's clipboard, ready for Ctrl+V", landed,
                landed ? string.Join(", ", onClip.Select(Path.GetFileName))
                       : onClip.Count == 0
                            ? "NOTHING is on the clipboard — this is 'copy from the customer does nothing'"
                            : $"{onClip.Count} item(s): {string.Join(", ", onClip.Select(Path.GetFileName))}");
            if (landed)
            {
                bool allExist = onClip.All(File.Exists);
                Check("and every one of them exists on this machine", allExist,
                    allExist ? "all three readable" : "the clipboard points at files that are not there");
            }

            // ---- 3. too big to move on a gesture --------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("A SET TOO BIG TO MOVE WITHOUT ASKING");
            ClearClipboard(out _);
            while (pulls.TryDequeue(out _)) { }
            far.Send(new ClipboardMsg { Files = theirPaths, Bytes = FileSetLimits.AutoFetchBytes + 1, Auto = true });
            var unwanted = Dequeue(pulls, 4000, () => Pump(50));
            Check("a set over the threshold is offered, not fetched behind the technician's back", unwanted == null,
                unwanted == null ? $"nothing fetched for {FileSetLimits.Human(FileSetLimits.AutoFetchBytes + 1)}"
                                 : "the console fetched it anyway");

            // ---- 4 and 5. the technician's OWN copy is on the clipboard when theirs arrives -------
            // Clint, 2026-09-29: folders copied across worked, then copying different folders back
            // "pastes the original files". Every step above empties the clipboard first, which is
            // exactly the state in which that cannot happen. Here the technician's own files are
            // there, as they are in real use.
            var mine = Path.Combine(Path.GetTempPath(), "pullcheck-mine-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(mine);
            var mineFile = Path.Combine(mine, "the technician's own.txt");
            File.WriteAllText(mineFile, "mine");
            bool StillMine() => ReadFiles().Any(p => p.Equals(mineFile, StringComparison.OrdinalIgnoreCase));
            bool PutMine()
            {
                for (int i = 0; i < 5; i++)
                {
                    try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { mineFile }); Pump(1500); if (StillMine()) return true; }
                    catch { Pump(200); }
                }
                return false;
            }

            Console.WriteLine();
            Console.WriteLine("THEY COPY A BIG SET WHILE THE TECHNICIAN'S OWN FILES ARE ON THIS CLIPBOARD");
            if (!PutMine()) { Check("the technician's own file could be put on the clipboard", false, "the clipboard would not take it"); goto done; }
            while (pulls.TryDequeue(out _)) { }
            far.Send(new ClipboardMsg { Files = theirPaths, Bytes = FileSetLimits.AutoFetchBytes + 1, Auto = true });
            Pump(1500);
            bool staleBig = StillMine();
            Check("a paste here no longer gives the technician's own files while theirs waits to be clicked", !staleBig,
                staleBig ? "Ctrl+V would paste 'the technician's own.txt' — the old files, not what they copied"
                         : ReadFiles().Count == 0 ? "the clipboard is empty until the offer is taken" : "the clipboard holds: " + string.Join(", ", ReadFiles().Select(Path.GetFileName)));

            Console.WriteLine();
            Console.WriteLine("THEY COPY A SMALL SET WHILE THE TECHNICIAN'S OWN FILES ARE ON THIS CLIPBOARD");
            if (!PutMine()) { Check("the technician's own file could be put on the clipboard again", false, "the clipboard would not take it"); goto done; }
            while (pulls.TryDequeue(out _)) { }
            _serveGate.Reset();
            far.Send(new ClipboardMsg { Files = theirPaths, Bytes = bytes, Auto = true });
            var pull5 = Dequeue(pulls, 10000, () => Pump(50));
            Pump(500);
            bool staleSmall = StillMine();
            Check("while the fetch is still on its way, a paste does not give the old files", pull5 != null && !staleSmall,
                pull5 == null ? "the console never asked for them" : staleSmall ? "Ctrl+V during the fetch would paste the technician's own file" : "nothing to paste until they land");
            _serveGate.Set();
            if (pull5 != null)
            {
                Dequeue(dones, 30000, () => Pump(50), d => d.SetId == pull5.SetId);
                Pump(1200);
                var after = ReadFiles();
                bool theirsNow = after.Count == 3 && !StillMine();
                Check("and once it lands, the clipboard holds THEIR files", theirsNow,
                    string.Join(", ", after.Select(Path.GetFileName)));
            }
            try { Directory.Delete(mine, true); } catch { }

            rc = _fail == 0 ? 0 : 1;

        done:
            try { f.Hide(); } catch { }
        }

        try { Directory.Delete(theirs, true); } catch { }
        try { listener.Stop(); } catch { }

        Console.WriteLine();
        Console.WriteLine(new string('-', 70));
        if (rc == 2) { Console.WriteLine("skipped; nothing was proved or disproved."); return 2; }
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        if (_fail == 0) Console.WriteLine("the customer copies and it arrives here, text and files, ready to paste.");
        return rc;
    }

    // ---- helpers, the same shapes pastecheck uses -----------------------------------------------

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Pump(50); }
        return cond();
    }

    private static T? Dequeue<T>(ConcurrentQueue<T> q, int ms, Action pump, Func<T, bool>? want = null) where T : class
    {
        var end = Environment.TickCount64 + ms;
        var putBack = new List<T>();
        while (Environment.TickCount64 < end)
        {
            while (q.TryDequeue(out var item))
            {
                if (want == null || want(item)) { foreach (var x in putBack) q.Enqueue(x); return item; }
                putBack.Add(item);
            }
            pump();
        }
        foreach (var x in putBack) q.Enqueue(x);
        return null;
    }

    private static string? ReadText()
    {
        for (int i = 0; i < 4; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { Thread.Sleep(80); }
        }
        return null;
    }

    private static List<string> ReadFiles()
    {
        for (int i = 0; i < 4; i++)
        {
            try { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>(); }
            catch { Thread.Sleep(80); }
        }
        return new List<string>();
    }

    /// <summary>
    /// Empties the clipboard so a stale value cannot be mistaken for an arrival. If the machine's
    /// clipboard cannot be taken at all, say so and skip rather than blaming the console (§27).
    /// </summary>
    private static bool ClearClipboard(out string why)
    {
        why = "";
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.Clear(); Thread.Sleep(120); if (ReadText() == null && ReadFiles().Count == 0) return true; why = "something put it straight back"; }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
            Thread.Sleep(200);
        }
        return false;
    }

    private static string Describe(string? s) => s == null ? "(nothing)" : $"\"{(s.Length > 40 ? s[..40] + "…" : s)}\"";
}
