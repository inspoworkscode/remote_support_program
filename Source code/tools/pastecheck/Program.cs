using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// "Copy a file here, right-click Paste on the customer's desktop" — the console's half of it,
// with no server, no host and no customer machine.
//
// WHY THIS EXISTS. On 2026-09-18 that gesture did nothing and Paste stayed greyed out on the far
// side, while every harness we had passed. They passed honestly: clipmirror proves the HOST can
// put a file list on a clipboard the shell will paste from, clipcheck proves the wire carries
// text and file lists both ways, and toolbarprobe proves the window lays out. Not one of them
// touches SessionForm's transfer code, which is where the whole of this gesture lives. The
// console half had no coverage at all, and it had none the day it was written.
//
// So this stands a WebSocket server up on a loopback port, points the REAL SessionForm at it
// through the REAL SessionLink, and plays the part of the customer's machine: it accepts the
// offer, swallows the chunks, reports the paths it "saved" them at, and then asserts on the one
// message that decides whether Paste is greyed out — a ClipboardMsg carrying those paths.
//
// The order is the point. Their clipboard can only be pointed at the files AFTER the files exist
// on their disk, so the console has to remember, across a network round trip, that this
// particular transfer was a paste rather than an ordinary send. That memory is the thing being
// tested, and a race in it is invisible from anywhere else.
//
//   dotnet run -c Release --project tools/pastecheck/pastecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class PasteCheck
{
    private static int _pass, _fail;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("pastecheck — copy here, paste there, from the console's side only");
        Console.WriteLine();

        // One clipboard per machine, so one clipboard harness at a time. See clipmirror for the
        // two runs on 2026-09-18 that failed confidently for reasons that were not the code.
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

        // ---- the customer's machine, played by a loopback socket --------------------------------
        int port = FreePort();
        var offers = new ConcurrentQueue<FileSetOfferMsg>();
        var clips = new ConcurrentQueue<ClipboardMsg>();
        var connected = new ManualResetEventSlim(false);
        var lastOffer = (FileSetOfferMsg?)null;
        var raceMode = false;
        long racedAlready = 0;
        long bytesReceived = 0;
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

                // Handlers before RunAsync, always: the console announces itself within
                // milliseconds and a late handler misses it for good (HANDOVER §10).
                conn.OnMessage += m =>
                {
                    if (m is FileSetOfferMsg o)
                    {
                        offers.Enqueue(o); lastOffer = o;
                        // Accept at once, the way a real host does.
                        conn.Send(new FileSetAcceptMsg { SetId = o.SetId, Accept = true, TargetDir = @"C:\Users\Someone\Downloads" });
                    }
                    if (m is ClipboardMsg cb) clips.Enqueue(cb);
                };
                // In race mode the customer's machine answers from inside the receive path, the
                // instant the last byte lands — no scheduling, no delay, nothing that gives the
                // console's own queued bookkeeping a chance to run first. That is what a real host
                // on a fast local network does with a small file.
                conn.OnBinary += b =>
                {
                    var total = Interlocked.Add(ref bytesReceived, b.Length);
                    if (!raceMode) return;
                    var o = lastOffer;
                    if (o == null || total < o.TotalBytes) return;
                    if (Interlocked.Exchange(ref racedAlready, 1) == 1) return;
                    conn.Send(new FileSetDoneMsg
                    {
                        SetId = o.SetId, Ok = true,
                        SavedDir = @"C:\Users\Someone\AppData\Local\Temp\RemoteSupport\paste\raced",
                        SavedPaths = new List<string> { @"C:\Users\Someone\AppData\Local\Temp\RemoteSupport\paste\raced\tiny.txt" }
                    });
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
            SessionId = "paste", DeviceId = "paste", MachineName = "PASTE-PC",
            StartedBy = "Probe", Unattended = true
        };

        // A real file, on this machine, of a size that is comfortably under the threshold so the
        // path under test is the one that sends without asking.
        // THREE files, not one. Reported on 2026-09-18: selecting several and copying them put
        // only one on the far machine. A harness that copies a single file cannot tell a set that
        // loses its tail from one that does not, and this one could not until now.
        var dir = Path.Combine(Path.GetTempPath(), "pastecheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "note for the customer.txt");
        File.WriteAllText(file, new string('x', 4096));
        var file2 = Path.Combine(dir, "second.txt");
        File.WriteAllText(file2, new string('y', 2048));
        var file3 = Path.Combine(dir, "thìrd é.txt");
        File.WriteAllText(file3, new string('z', 1024));
        var copied = new[] { file, file2, file3 };

        int rc = 1;
        using (var f = new SessionForm($"ws://127.0.0.1:{port}", "paste-token", session, theme, brand))
        {
            f.Show();
            Pump(300);

            if (!connected.Wait(10000)) { Check("the console's viewer socket connected", false, "nothing arrived in 10s"); goto done; }
            Check("the console's viewer socket connected", true, $"on 127.0.0.1:{port}");
            Pump(400);

            // ---- the gesture ---------------------------------------------------------------------
            // Put the file on this machine's clipboard exactly as Explorer's Copy does, then give
            // the window the activation that a technician gives it by clicking back into it.
            Console.WriteLine();
            Console.WriteLine("COPY HERE, THEN CLICK BACK INTO THE SESSION");
            if (!TakeClipboard(copied, out var clipWhy))
            {
                // NOT a failure of the console. This harness uses the machine's real clipboard,
                // and anything else running here can take it away between the write and the read
                // — measured on 2026-09-18, when a screenshot landed on it mid-run and three
                // checks failed for a reason that had nothing to do with the code under test.
                // A harness that reports false failures is worse than no harness, so it says what
                // happened and stops instead of blaming the console.
                Console.WriteLine($"  SKIP  this machine's clipboard is in use — {clipWhy}");
                Console.WriteLine("        nothing was proved or disproved; close whatever is copying and run it again.");
                rc = 2;
                goto done;
            }
            Check("three files could be put on this machine's clipboard", true, string.Join(", ", copied.Select(Path.GetFileName)));

            // OnActivated rather than Activate(): a harness cannot rely on the window manager
            // giving it focus, and what is under test is what the console does WHEN activated,
            // not whether Windows chose to activate it.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Raise(f, "OnActivated");
            sw.Stop();

            // The freeze of 2026-09-18, as a check rather than a memory. Measuring a copied
            // selection means touching every file in it, and doing that on the UI thread wedged
            // the console the moment somebody copied a large folder or anything on a slow share —
            // the window stopped pumping and clicking it on the taskbar just beeped.
            //
            // Asserting on a DURATION would not catch it: a handful of local files walk in
            // milliseconds and would pass either way. So assert on the STRUCTURE instead — when
            // the activation handler returns, the walk must not have finished, because it must
            // not have happened here. An offer already queued at this point means it was measured
            // on the UI thread and the freeze is back.
            Check("activation returns before the copy has been measured, not after",
                !offersHasAny(offers) && sw.ElapsedMilliseconds < 1000,
                offersHasAny(offers)
                    ? "the offer was already sent when OnActivated returned — the tree was walked ON THE UI THREAD"
                    : $"returned in {sw.ElapsedMilliseconds} ms with the walk still running");

            Pump(1500);

            var offer = Dequeue(offers, 8000, () => Pump(100));
            Check("copying files and coming back sends them to the customer", offer != null,
                offer != null ? $"{offer.Label}, {offer.TotalBytes:N0} bytes"
                              : "no FileSetOfferMsg — the console never noticed the copy");
            if (offer == null) goto done;

            // A PASTE must be staged, not delivered. Reported 2026-09-18: three files pasted onto
            // the customer's desktop arrived on the desktop AND stayed in their Downloads, because
            // a paste was staged in the folder a plain send uses. The bytes have to land somewhere
            // before a clipboard can point at them, and that somewhere must be temp — otherwise
            // every paste leaves litter in a folder the customer actually looks at.
            Check("a paste is staged under their temp, not delivered to their Downloads",
                offer.TargetDir == RemotePaths.PasteStaging,
                offer.TargetDir == null
                    ? "TargetDir was null, which means their Downloads — every paste would leave a copy there"
                    : $"TargetDir = {offer.TargetDir}");

            // The offer is what the customer's machine is told to expect. If three files were
            // copied and this names one, everything downstream is already wrong and the far end
            // has no way to know it.
            var tops = offer.Entries.Select(e => e.Path.Replace('\\', '/').Split('/')[0])
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Check("all three are in the offer, not just the first", tops.Count == 3,
                $"{tops.Count} of 3: {string.Join(" | ", tops)}");
            Check("the offer's byte count covers all three", offer.TotalBytes == 4096 + 2048 + 1024,
                $"{offer.TotalBytes:N0} bytes, expected {4096 + 2048 + 1024:N0}");

            // Let the bytes flow, the way they would over a real link.
            var waited = 0;
            while (Interlocked.Read(ref bytesReceived) < offer.TotalBytes && waited < 8000) { Pump(100); waited += 100; }
            Check("the bytes arrived", Interlocked.Read(ref bytesReceived) >= offer.TotalBytes,
                $"{Interlocked.Read(ref bytesReceived):N0} of {offer.TotalBytes:N0}");

            // ---- the part that decides whether Paste is greyed out --------------------------------
            // The customer's machine says where the files landed. Only now can their clipboard be
            // pointed at them, and only the console knows this transfer was a paste.
            Console.WriteLine();
            Console.WriteLine("THE CUSTOMER'S MACHINE CONFIRMS, AND THEIR CLIPBOARD SHOULD FOLLOW");
            var savedPaths = copied.Select(p => @"C:\Users\Someone\Downloads\" + Path.GetFileName(p)).ToList();
            far!.Send(new FileSetDoneMsg
            {
                SetId = offer.SetId, Ok = true,
                SavedDir = @"C:\Users\Someone\Downloads",
                SavedPaths = savedPaths
            });

            var cb = Dequeue(clips, 8000, () => Pump(100), c => c.Files is { Count: > 0 });
            Check("their clipboard is pointed at the files that just landed", cb != null,
                cb != null ? $"{cb.Files!.Count} path(s)"
                           : "no ClipboardMsg with a file list — THIS is 'Paste greyed out' on their desktop");

            if (cb != null)
                Check("and it names ALL of them, so Ctrl+V pastes three files and not one",
                    cb.Files!.Count == 3 && savedPaths.All(s => cb.Files.Contains(s)),
                    $"{cb.Files.Count} of 3: {string.Join(" | ", cb.Files.Select(Path.GetFileName))}");

            // ---- copying the SAME files again ------------------------------------------------
            // Explorer leaves the selection on the clipboard after a copy, so the console has to
            // refuse to re-send it on every alt-tab — and it does. But that memory was set before
            // the transfer was known to have worked, so a send that failed poisoned that exact
            // selection for the rest of the session: copying the same file again, which is what
            // anybody does when something did not arrive, then did nothing and said nothing.
            Console.WriteLine();
            Console.WriteLine("COPYING THE SAME FILES AGAIN");

            while (offers.TryDequeue(out _)) { }
            Raise(f, "OnActivated");
            Pump(1200);
            Check("the same selection is not sent twice on its own", offers.IsEmpty,
                offers.IsEmpty ? "nothing re-sent, which is what alt-tabbing must not do"
                               : "it sent the same files again — every alt-tab would be a transfer");

            // COPYING THE SAME FILE AGAIN, with no failure in between.
            //
            // This is what a technician does when something did not arrive, and until 2026-09-18
            // it was refused for the rest of the session. The guard compared PATHS, and Explorer
            // copying the same file twice produces the identical path list — so the second copy,
            // and every copy after it, did nothing. Clint's trace said so in its own words:
            // "the same 1 item(s) are still on the clipboard and were already sent". Testing with
            // one file, which is what anybody does, meant copy and paste worked exactly once.
            //
            // A real Ctrl+C bumps the clipboard's sequence number even when the content is
            // identical, which is the difference between a copy and an alt-tab.
            while (offers.TryDequeue(out _)) { }
            if (!TakeClipboard(copied, out var againWhy))
            {
                Console.WriteLine($"  SKIP  could not take the clipboard for this one - {againWhy}");
                rc = _fail == 0 ? 2 : 1;
                goto done;
            }
            Raise(f, "OnActivated");
            Pump(2500);
            var recopied = Dequeue(offers, 6000, () => Pump(100));
            Check("copying the SAME files again really does send them again", recopied != null,
                recopied != null ? $"{recopied.Entries.Count} entries, on a fresh copy of an identical selection"
                                 : "refused — a technician retrying the same file gets nothing, for the rest of the session");

            // Now a failure, and then the same selection again.
            var retrySet = Guid.NewGuid().ToString("N");
            far.Send(new FileSetDoneMsg { SetId = retrySet, Ok = false, Error = "the customer's disk is full" });
            Pump(400);
            far.Send(new FileSetAcceptMsg { SetId = retrySet, Accept = false, Error = "refused for the test" });
            Pump(600);

            while (offers.TryDequeue(out _)) { }
            Raise(f, "OnActivated");
            Pump(2000);
            var again = Dequeue(offers, 6000, () => Pump(100));
            Check("after a failure, copying the same files again does resend them", again != null,
                again != null ? $"{again.Label}, {again.TotalBytes:N0} bytes"
                              : "the selection stayed poisoned — a retry of the same file does nothing for the rest of the session");

            // ---- files arriving FROM the customer must not be sent straight back --------------
            // Fetching files off their machine ends by putting them on THIS machine's clipboard,
            // so Ctrl+V works here. That is a clipboard change, and the mirror comes straight
            // back round and pushes those same files to the machine they just came from — a
            // second full transfer, for nobody. Text has been guarded against this from the
            // start; files never were, because until 3.5.1 they only moved on a button press.
            Console.WriteLine();
            Console.WriteLine("FILES THAT CAME FROM THEM MUST NOT GO BACK TO THEM");

            var fromThem = Path.Combine(dir, "came-from-them.txt");
            File.WriteAllText(fromThem, "this arrived from the customer");

            // Exactly what the fetch path does at the end: remember them, then put them on the
            // clipboard. Driven through the form's own method so the harness cannot get it right
            // in a way the console does not.
            // The clipboard FIRST, then the note — the order the fetch path uses, and the order
            // that matters now that the note records the clipboard's sequence number. Remembering
            // before the write captures the state before it, and the write then looks like a
            // fresh copy that ought to be sent onward.
            if (!TakeClipboard(new[] { fromThem }, out var backWhy))
            {
                Console.WriteLine($"  SKIP  could not take the clipboard for this one - {backWhy}");
                rc = _fail == 0 ? 2 : 1;
                goto done;
            }
            Invoke(f, "RememberAsAlreadySent", new object[] { new List<string> { fromThem } });

            while (offers.TryDequeue(out _)) { }
            Raise(f, "OnActivated");
            Pump(2000);
            Check("files fetched from the customer are not pushed back at them", offers.IsEmpty,
                offers.IsEmpty ? "nothing went back"
                               : "they were sent back to the machine they came from — a full transfer for nobody");

            // ---- the freeze, reproduced here instead of on a customer's machine ---------------
            // This is the check that should have existed before any of the freeze "fixes" went
            // out. The console wedged three times on 2026-09-18 and each diagnosis was made from
            // the outside, by reading code and reasoning about it; two of the three were wrong.
            // The working agreement says measure first, and this is what measuring looks like:
            // give the console a selection big enough to hurt and watch whether its message loop
            // keeps turning.
            //
            // Responsiveness is measured the way Windows measures it. A window that does not pump
            // is a window that will not restore from the taskbar and beeps when clicked, so the
            // test is simply whether messages are still being dispatched while the work happens.
            Console.WriteLine();
            Console.WriteLine("A BIG SELECTION MUST NOT WEDGE THE WINDOW");

            var big = Path.Combine(dir, "big");
            Directory.CreateDirectory(big);
            // Wide and deep: a walk touches every one of these, and this is the shape of a real
            // folder somebody copies without thinking about it.
            for (int d = 0; d < 20; d++)
            {
                var sub = Path.Combine(big, "folder " + d);
                Directory.CreateDirectory(sub);
                for (int i = 0; i < 120; i++) File.WriteAllText(Path.Combine(sub, $"file {i}.txt"), "x");
            }

            if (!TakeClipboard(new[] { big }, out var bigWhy))
            {
                Console.WriteLine($"  SKIP  could not take the clipboard for this one - {bigWhy}");
                rc = _fail == 0 ? 2 : 1;
                goto done;
            }

            // Beat the message loop and count how long any single dispatch takes. A pump that is
            // never blocked shows small gaps; a wedged one shows one enormous gap and nothing else.
            long worstGapMs = 0;
            var beats = 0;
            var lastBeat = System.Diagnostics.Stopwatch.StartNew();
            using (var beat = new System.Windows.Forms.Timer { Interval = 50 })
            {
                beat.Tick += (_, _) =>
                {
                    beats++;
                    worstGapMs = Math.Max(worstGapMs, lastBeat.ElapsedMilliseconds);
                    lastBeat.Restart();
                };
                beat.Start();
                Raise(f, "OnActivated");
                Pump(6000);
            }

            Check("the message loop kept turning while 2,400 files were measured",
                worstGapMs < 1500 && beats > 20,
                $"worst gap between dispatches {worstGapMs} ms over {beats} of them — a wedged window shows one huge gap");

            var bigOffer = Dequeue(offers, 10000, () => Pump(100));
            Check("and the big selection was still sent", bigOffer != null,
                bigOffer != null ? $"{bigOffer.Entries.Count} entries" : "no offer within 10s");

            // ---- the confirmation that arrives too fast --------------------------------------
            // THE bug of 2026-09-18, and the one every other check in this file walked straight
            // past because it answers at human speed.
            //
            // The console has to remember, across a network round trip, that a transfer was a
            // paste rather than an ordinary send. It used to record that in a callback queued
            // AFTER the last chunk went out — and a small file is written and confirmed by the
            // far machine before that callback runs. The handler then found no marker, decided it
            // had been a plain send, and never told the host to point the clipboard at the files.
            //
            // The host's own trace showed it precisely: three transfers each followed by
            // "clipboard: put 1 file(s) on the customer's clipboard", and a fourth and smaller
            // one followed by nothing. Smaller files failed MORE often, which is the wrong way
            // round for anything anybody would guess at.
            //
            // So this answers at machine speed: the confirmation goes out from the binary handler
            // itself, the instant the last byte lands, which is the tightest the race can be.
            Console.WriteLine();
            Console.WriteLine("A CONFIRMATION THAT BEATS OUR OWN BOOKKEEPING");

            var tiny = Path.Combine(dir, "tiny.txt");
            File.WriteAllText(tiny, "673 bytes would be luxurious");

            if (!TakeClipboard(new[] { tiny }, out var tinyWhy))
            {
                Console.WriteLine($"  SKIP  could not take the clipboard for this one - {tinyWhy}");
                rc = _fail == 0 ? 2 : 1;
                goto done;
            }

            // Answer from inside the receive path rather than from the test body: no scheduling,
            // no Pump, nothing that would hand the console's queued callback a chance to run.
            raceMode = true;
            while (offers.TryDequeue(out _)) { }
            while (clips.TryDequeue(out _)) { }
            Raise(f, "OnActivated");
            Pump(4000);

            var raced = Dequeue(clips, 6000, () => Pump(100), c => c.Files is { Count: > 0 });
            Check("a paste still points their clipboard at the files when the answer comes back instantly",
                raced != null,
                raced != null
                    ? $"{raced.Files!.Count} path(s), even though the confirmation beat the send callback"
                    : "no ClipboardMsg — the console forgot this was a paste, and nothing on their machine can be pasted");

            rc = _fail == 0 ? 0 : 1;
        }

    done:
        try { listener.Stop(); } catch { }
        try { Directory.Delete(dir, true); } catch { }
        try { Clipboard.Clear(); } catch { }

        Console.WriteLine();
        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        Console.WriteLine(_fail == 0
            ? "the console sends a copied file and then points their clipboard at where it landed."
            : "copy-here-paste-there is NOT proven from the console's side — see the failures above.");
        return rc;
    }

    /// <summary>
    /// Runs the message loop for a while. Everything under test is on the UI thread, so a plain
    /// Thread.Sleep would stop the very thing being measured — the form would never process the
    /// socket callbacks it is being asked about.
    /// </summary>
    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static T? Dequeue<T>(ConcurrentQueue<T> q, int ms, Action pump, Func<T, bool>? want = null) where T : class
    {
        var end = Environment.TickCount64 + ms;
        var putBack = new List<T>();
        while (Environment.TickCount64 < end)
        {
            while (q.TryDequeue(out var item))
            {
                if (want == null || want(item)) { foreach (var b in putBack) q.Enqueue(b); return item; }
                putBack.Add(item);
            }
            pump();
        }
        foreach (var b in putBack) q.Enqueue(b);
        return null;
    }

    /// <summary>Calls a protected override on the form, because a harness cannot make Windows
    /// hand it the focus and the focus is not what is being tested.</summary>
    private static void Raise(Form f, string method)
    {
        var mi = typeof(Form).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        mi!.Invoke(f, new object[] { EventArgs.Empty });
    }

    private static bool offersHasAny(ConcurrentQueue<FileSetOfferMsg> q) => !q.IsEmpty;

    /// <summary>
    /// Puts these files on the clipboard and confirms they are still there before going on.
    ///
    /// Setting it is not enough. The clipboard is one shared resource for the whole machine, and
    /// anything else running here — a screenshot tool, a browser, a person — can replace it
    /// between the write and the read. When that happened on 2026-09-18 this harness reported
    /// three confident failures about files it had never managed to copy.
    /// </summary>
    private static bool TakeClipboard(IReadOnlyList<string> paths, out string why)
    {
        why = "";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var col = new System.Collections.Specialized.StringCollection();
                foreach (var p in paths) col.Add(p);
                Clipboard.SetFileDropList(col);
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; Thread.Sleep(250); continue; }

            Thread.Sleep(150);
            try
            {
                var back = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (back.Count == paths.Count && paths.All(p => back.Contains(p))) return true;
                why = back.Count == 0
                    ? "it came back empty"
                    : $"something else replaced it with {back.Count} other item(s), e.g. {Path.GetFileName(back[0])}";
            }
            catch (Exception ex) { why = "could not read it back: " + ex.GetType().Name; }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Calls a private method on the form, so the harness drives the console's own code
    /// rather than a harness-shaped imitation of it.</summary>
    private static void Invoke(Form f, string method, object[] args)
    {
        var mi = f.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        mi!.Invoke(f, args);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
