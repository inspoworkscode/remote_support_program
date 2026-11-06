using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// "Copy on one office machine, paste on the other" — two session windows at once (2026-10-15).
//
// Clint: two sessions side by side, one on each monitor, works great; can copy and paste work
// BETWEEN them? There is no link between the two machines: a copy on A lands on THIS PC's
// clipboard (text at once, files once fetched), and the clipboard follows the technician into
// B's window when they click into it. Nothing proved that with two windows open at once —
// pullcheck and pastecheck each drive ONE window. This drives two REAL SessionForms, each pointed
// at its own loopback socket playing a customer's machine, and checks both ways:
//
//   1. text copied on A is put on B's clipboard when B's window comes to the front — and back
//   2. files copied on A are fetched, then sent to B as a paste — and back
//   3. neither machine is sent its own copy back
//   4. clicking into B while A's files are STILL ARRIVING: B says so, sends nothing, and the
//      files go to B once they land (the gap SessionForm.Between.cs closes)
//   5. a set too big to fetch unasked: B says to click the offer in A's window
//   6. A's window closed mid-fetch: B says nothing came
//
// It uses the machine's real clipboard, so it takes the same named mutex as the other clipboard
// harnesses and SKIPS rather than failing when it cannot win it. Leave the keyboard and mouse
// alone while it runs — it tries to bring B's window to the front.
//
//   dotnet run -c Release --project tools/twosessions/twosessions.csproj
// ---------------------------------------------------------------------------------------------

internal static class TwoSessions
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
        Console.WriteLine("twosessions — copy on one customer's machine, paste on another, with both windows open");
        Console.WriteLine();

        using var clipboardTurn = new Mutex(false, @"Global\RemoteSupport-clipboard-harness");
        bool gotTurn = false;
        try { gotTurn = clipboardTurn.WaitOne(TimeSpan.FromMinutes(3)); }
        catch (AbandonedMutexException) { gotTurn = true; }
        if (!gotTurn) { Console.WriteLine("another clipboard harness has been running for three minutes; not starting a second one."); return 2; }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var scratch = Path.Combine(Path.GetTempPath(), "twosessions-" + Guid.NewGuid().ToString("N")[..8]);
        var hostA = new FakeHost("TWOSESS-A", Path.Combine(scratch, "a"));
        var hostB = new FakeHost("TWOSESS-B", Path.Combine(scratch, "b"));

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        SessionInfo Info(FakeHost h) => new() { SessionId = h.Name, DeviceId = h.Name, MachineName = h.Name, StartedBy = "Probe", Unattended = true };

        int rc = 1;
        var fA = new SessionForm($"ws://127.0.0.1:{hostA.Port}", "a-token", Info(hostA), theme, brand);
        var fB = new SessionForm($"ws://127.0.0.1:{hostB.Port}", "b-token", Info(hostB), theme, brand);
        try
        {
            fA.StartPosition = FormStartPosition.Manual; fA.Location = new Point(0, 0); fA.Size = new Size(900, 600);
            fB.StartPosition = FormStartPosition.Manual; fB.Location = new Point(120, 120); fB.Size = new Size(900, 600);
            fA.Show(); fB.Show();
            Pump(400);
            bool both = hostA.Connected.Wait(10000) & hostB.Connected.Wait(10000);
            Check("both session windows' sockets connected", both, $"A on {hostA.Port}, B on {hostB.Port}");
            if (!both) goto done;
            Pump(500);

            if (!ClearClipboard(out var clearWhy)) { Console.WriteLine($"  SKIP  this machine's clipboard is in use — {clearWhy}"); rc = 2; goto done; }

            // ---- 1. text ---------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("TEXT, A TO B AND BACK");
            var textA = "copied on A " + Guid.NewGuid().ToString("N")[..10] + " é";
            hostA.Far!.Send(new ClipboardMsg { Text = textA, Auto = true });
            Check("copied on A, it is on this PC's clipboard", WaitFor(() => ReadText() == textA, 8000), Describe(ReadText()));
            Raise(fB, "OnActivated");
            var clipB = Dequeue(hostB.Clips, 6000, c => c.Text == textA);
            Check("clicking into B's window puts it on B's clipboard", clipB != null, clipB != null ? $"{textA.Length} characters" : "B was sent nothing");
            Pump(500);
            Check("and A is not sent its own text back", !hostA.Clips.Any(c => c.Text == textA), "");

            var textB = "copied on B " + Guid.NewGuid().ToString("N")[..10];
            hostB.Far!.Send(new ClipboardMsg { Text = textB, Auto = true });
            Check("copied on B, it is on this PC's clipboard", WaitFor(() => ReadText() == textB, 8000), Describe(ReadText()));
            Raise(fA, "OnActivated");
            var clipA = Dequeue(hostA.Clips, 6000, c => c.Text == textB);
            Check("clicking into A's window puts it on A's clipboard", clipA != null, clipA != null ? $"{textB.Length} characters" : "A was sent nothing");

            // ---- 2. files ----------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("FILES, A TO B");
            var filesA = hostA.MakeFiles("report", 3);
            hostA.Far.Send(new ClipboardMsg { Files = filesA, Bytes = Size(filesA), Auto = true });
            var pullA = Dequeue(hostA.Pulls, 10000);
            Check("the console fetches what was copied on A", pullA is { Paths.Count: 3 }, pullA == null ? "never asked" : $"{pullA.Paths.Count} path(s)");
            Check("and A's files land on this PC's clipboard", WaitFor(() => SameNames(ReadFiles(), filesA), 15000), Names(ReadFiles()));
            var offerB = PasteInto(fB, hostB, filesA);
            Check("clicking into B's window sends them to B, staged as a paste", offerB is { Entries.Count: 3 } && offerB.TargetDir == RemotePaths.PasteStaging,
                offerB == null ? "B was offered nothing" : $"{offerB.Entries.Count} file(s), {offerB.TotalBytes:N0} bytes, into {offerB.TargetDir}");
            var pasteB = Dequeue(hostB.Clips, 8000, c => c.Files is { Count: 3 });
            Check("...and B's clipboard is pointed at all three, so Ctrl+V there pastes them", pasteB != null,
                pasteB != null ? Names(pasteB.Files!) : "no ClipboardMsg with the files");
            Check("...byte for byte", offerB != null && hostB.ReceivedMatch(filesA), hostB.LastReceivedSummary);
            Raise(fA, "OnActivated");
            Pump(1500);
            Check("and A is not sent its own files back", hostA.Offers.IsEmpty, $"{hostA.Offers.Count} offer(s) to A");

            Console.WriteLine();
            Console.WriteLine("FILES, B TO A");
            var filesB = hostB.MakeFiles("invoice", 2);
            hostB.Far.Send(new ClipboardMsg { Files = filesB, Bytes = Size(filesB), Auto = true });
            Check("copied on B, they land on this PC's clipboard", WaitFor(() => SameNames(ReadFiles(), filesB), 15000), Names(ReadFiles()));
            var offerA = PasteInto(fA, hostA, filesB);
            Check("clicking into A's window sends them to A", offerA is { Entries.Count: 2 }, offerA == null ? "A was offered nothing" : $"{offerA.Entries.Count} file(s)");
            Check("...byte for byte", offerA != null && hostA.ReceivedMatch(filesB), hostA.LastReceivedSummary);
            var pasteA = Dequeue(hostA.Clips, 8000, c => c.Files is { Count: 2 });
            Check("...and A's clipboard follows", pasteA != null, pasteA != null ? Names(pasteA.Files!) : "no ClipboardMsg with the files");

            // ---- 4. still arriving -------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("CLICKING INTO B WHILE A's FILES ARE STILL ARRIVING");
            var slow = hostA.MakeFiles("slow", 2);
            hostA.Gate.Reset();
            while (hostB.Offers.TryDequeue(out _)) { }
            while (hostB.Clips.TryDequeue(out _)) { }
            hostA.Far.Send(new ClipboardMsg { Files = slow, Bytes = Size(slow), Auto = true });
            var pullSlow = Dequeue(hostA.Pulls, 10000);
            Check("(A's files are being fetched, and held up)", pullSlow != null, "");
            bool front = BringToFront(fB);
            Raise(fB, "OnActivated");
            Pump(800);
            var status = StatusOf(fB);
            Check("B's window says A's files are still arriving, and not to paste yet",
                status.Contains("still arriving") && status.Contains("TWOSESS-A") && status.Contains("don't paste"), Describe(status));
            Check("...and B is sent nothing meanwhile — no stale clipboard, no half a paste", hostB.Offers.IsEmpty && hostB.Clips.IsEmpty,
                $"{hostB.Offers.Count} offer(s), {hostB.Clips.Count} clipboard message(s)");
            hostA.Gate.Set();
            Check("(A's files land on this PC's clipboard)", WaitFor(() => SameNames(ReadFiles(), slow), 15000), Names(ReadFiles()));
            front = front && Form.ActiveForm == fB;
            if (front)
            {
                // B is still in front: the paste that was waiting goes now, with no further click.
                var offerSlow = Dequeue(hostB.Offers, 10000);
                Check("B still in front: the files go to B the moment they land, with no further click", offerSlow is { Entries.Count: 2 },
                    offerSlow == null ? "B was offered nothing" : $"{offerSlow.Entries.Count} file(s)");
                if (offerSlow != null) { hostB.Finish(offerSlow, slow); Check("...byte for byte", hostB.ReceivedMatch(slow), hostB.LastReceivedSummary); }
            }
            else
            {
                // Windows would not give this harness the foreground (it never asked for it by a click), so B is not
                // the window in front: it must say they have arrived, and a click back into it sends them.
                Console.WriteLine("        (Windows kept the foreground from this harness, so B is not the window in front — checking that path instead)");
                Pump(600);
                var arrived = StatusOf(fB);
                Check("B not in front: its window says they have arrived and to click back into it", arrived.Contains("have arrived"), Describe(arrived));
                var offerSlow = PasteInto(fB, hostB, slow);
                Check("...and clicking back sends them to B", offerSlow is { Entries.Count: 2 }, offerSlow == null ? "B was offered nothing" : $"{offerSlow.Entries.Count} file(s)");
            }

            // ---- 5. a big set waiting for a click ----------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("A's COPY IS TOO BIG TO FETCH UNASKED");
            while (hostB.Offers.TryDequeue(out _)) { }
            while (hostA.Pulls.TryDequeue(out _)) { }
            var big = hostA.MakeFiles("big", 1);
            hostA.Far.Send(new ClipboardMsg { Files = big, Bytes = FileSetLimits.AutoFetchBytes + 1, Auto = true });
            Pump(800);
            Raise(fB, "OnActivated");
            Pump(600);
            var bigStatus = StatusOf(fB);
            Check("B's window says to click the offer in A's window", bigStatus.Contains("click the offer in TWOSESS-A"), Describe(bigStatus));
            Check("...and nothing was fetched or sent", hostA.Pulls.IsEmpty && hostB.Offers.IsEmpty, "");

            // ---- 6. A closed mid-fetch ---------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("A's WINDOW IS CLOSED WHILE ITS FILES ARE ON THEIR WAY");
            var lost = hostA.MakeFiles("lost", 1);
            hostA.Gate.Reset();
            hostA.Far.Send(new ClipboardMsg { Files = lost, Bytes = Size(lost), Auto = true });
            Check("(A's files are being fetched, and held up)", Dequeue(hostA.Pulls, 10000) != null, "");
            Raise(fB, "OnActivated");
            Pump(500);
            Check("(B is waiting for them)", StatusOf(fB).Contains("still arriving"), Describe(StatusOf(fB)));
            fA.Close();
            Pump(800);
            var lostStatus = StatusOf(fB);
            Check("B's window says they did not arrive and nothing was sent", lostStatus.Contains("did not arrive"), Describe(lostStatus));
            hostA.Gate.Set();

            // ---- 7. straight across (2026-10-15) -----------------------------------------------------
            // Everything above ran against hosts that cannot copy straight across — an older app — so it is also the
            // proof that those still go through this PC exactly as before (Clint's answer 6).
            fB.Close();
            Pump(500);
            StraightAcross(theme, brand, scratch);

            rc = _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { if (!fA.IsDisposed) fA.Close(); } catch { }
            try { if (!fB.IsDisposed) fB.Close(); } catch { }
        }

    done:
        hostA.Stop(); hostB.Stop();
        try { Directory.Delete(scratch, true); } catch { }
        // What the console fetched went where a real fetch goes: Downloads\Remote Support\<machine> <time>.
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Remote Support");
        try { foreach (var d in Directory.GetDirectories(downloads, "TWOSESS-*")) Directory.Delete(d, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(new string('-', 70));
        if (rc == 2) { Console.WriteLine("skipped; nothing was proved or disproved."); return 2; }
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        if (_fail == 0) Console.WriteLine("copy on one machine, paste on the other, both ways — and an early paste is told to wait.");
        return rc;
    }

    /// <summary>
    /// Two windows onto NEW hosts (end-to-end encrypted, able to copy straight across): A's copy is held for B, not
    /// fetched here; clicking into B asks the server, and on its yes hands each machine the key, sealed; the endings,
    /// the refusal that goes through this PC unasked, the failure that OFFERS it, the 50 MB offer, "bring them to this
    /// PC", and a server that never answers. The fakes play the hub's half (TenantHub.Bridge.cs is bridgewire's).
    /// </summary>
    private static void StraightAcross(Theme theme, Branding brand, string scratch)
    {
        Console.WriteLine();
        Console.WriteLine("STRAIGHT ACROSS: A's COPY GOES FROM A's MACHINE TO B's, NOT THROUGH THIS PC");
        var hC = new FakeHost("STRAIGHT-A", Path.Combine(scratch, "sa"), bridging: true);
        var hD = new FakeHost("STRAIGHT-B", Path.Combine(scratch, "sb"), bridging: true);
        SessionInfo Info(FakeHost h) => new() { SessionId = h.Name, DeviceId = h.Name, MachineName = h.Name, StartedBy = "Probe", Unattended = true };
        var fC = new SessionForm($"ws://127.0.0.1:{hC.Port}", "c-token", Info(hC), theme, brand);
        var fD = new SessionForm($"ws://127.0.0.1:{hD.Port}", "d-token", Info(hD), theme, brand);
        try
        {
            fC.StartPosition = FormStartPosition.Manual; fC.Location = new Point(0, 0); fC.Size = new Size(900, 600);
            fD.StartPosition = FormStartPosition.Manual; fD.Location = new Point(120, 120); fD.Size = new Size(900, 600);
            fC.Show(); fD.Show();
            bool ready = hC.Connected.Wait(10000) & hD.Connected.Wait(10000)
                && WaitFor(() => CanBridge(fC) && CanBridge(fD), 10000);
            Check("both windows are end-to-end encrypted onto hosts that can copy straight across", ready, $"C {CanBridge(fC)}, D {CanBridge(fD)}");
            if (!ready) return;
            ClearClipboard(out _);

            // -- held, not fetched --
            var files = hC.MakeFiles("plan", 3);
            hC.Far!.Send(new ClipboardMsg { Files = files, Bytes = Size(files), Auto = true });
            Pump(1200);
            var held = StatusOf(fC);
            Check("copied on A, the files are NOT fetched to this PC", hC.Pulls.IsEmpty && ReadFiles().Count == 0, $"{hC.Pulls.Count} fetch(es)");
            Check("...A's window says to click into B's window and paste, or bring them here",
                held.Contains("click into STRAIGHT-B's window") && held.Contains("bring them to this PC"), Describe(held));

            // -- clicking into B: the ask, then the keys, sealed --
            Raise(fD, "OnActivated");
            var ask = Dequeue(hD.Asks, 6000);
            Check("clicking into B's window asks the server to copy straight from A", ask != null && ask.FromSessionId == "STRAIGHT-A" && ask.Items == 3,
                ask == null ? "no ask" : $"{ask.Items} item(s), {ask.Bytes:N0} bytes from {ask.FromSessionId}");
            if (ask == null) return;
            Pump(600);
            Check("...and hands out no key before the server says yes", hC.Keys.IsEmpty && hD.Keys.IsEmpty);
            hD.Far!.Send(ask);   // the server's yes is the ask sent back
            var keyD = Dequeue(hD.Keys, 6000);
            var keyC = Dequeue(hC.Keys, 6000);
            Check("on the yes, each machine is given the key — and it arrived SEALED (these hosts refuse clear content)",
                keyD is { Send: false } && keyC is { Send: true } && keyC.Key == keyD.Key && keyC.BridgeId == ask.BridgeId && Convert.FromBase64String(keyC.Key).Length == 32,
                keyD == null || keyC == null ? $"D {keyD != null}, C {keyC != null}" : "one key, 32 bytes, for both");
            Check("...A's machine is told exactly what was copied", keyC != null && keyC.Paths.SequenceEqual(files));
            var coming = StatusOf(fD);
            Check("B's window says they are coming straight here, not to paste yet", coming.Contains("coming straight here") && coming.Contains("don't paste"), Describe(coming));

            hD.Far.Send(new BridgeProgressMsg { BridgeId = ask.BridgeId, BytesDone = 10, TotalBytes = 20, FilesDone = 1, FilesTotal = 3 });
            Pump(300);
            Check("progress shows on B's window", StatusOf(fD).Contains("1/3 files"), Describe(StatusOf(fD)));
            var done = new BridgeEndMsg { BridgeId = ask.BridgeId, Ok = true, Files = 3, Bytes = Size(files) };
            hD.Far.Send(done); hC.Far.Send(done);
            Pump(500);
            Check("landed: B's window says paste it now", StatusOf(fD).Contains("paste it now"), Describe(StatusOf(fD)));
            Check("...and A's says it was sent straight to B", StatusOf(fC).Contains("straight to STRAIGHT-B"), Describe(StatusOf(fC)));
            Check("nothing went through this PC: no fetch from A, no offer to B, nothing on this clipboard",
                hC.Pulls.IsEmpty && hD.Offers.IsEmpty && ReadFiles().Count == 0);
            Raise(fD, "OnActivated");
            Pump(800);
            Check("clicking into B again is an alt-tab, not a second copy", hD.Asks.IsEmpty);

            // -- refused: through this PC, unasked --
            Console.WriteLine();
            Console.WriteLine("STRAIGHT ACROSS REFUSED: THROUGH THIS PC, UNASKED");
            var files2 = hC.MakeFiles("refused", 2);
            hC.Far.Send(new ClipboardMsg { Files = files2, Bytes = Size(files2), Auto = true });
            Pump(900);
            Raise(fD, "OnActivated");
            var ask2 = Dequeue(hD.Asks, 6000);
            if (ask2 != null) hD.Far.Send(new BridgeEndMsg { BridgeId = ask2.BridgeId, Refused = true, Why = "one of the two machines is running an older app" });
            var pull2 = Dequeue(hC.Pulls, 8000);
            Check("refused: A's files are fetched to this PC instead, unasked", ask2 != null && pull2 is { Paths.Count: 2 }, pull2 == null ? "no fetch" : $"{pull2.Paths.Count} path(s)");
            Check("...landing on this PC's clipboard", WaitFor(() => SameNames(ReadFiles(), files2), 15000), Names(ReadFiles()));
            var offer2 = Form.ActiveForm == fD ? Dequeue(hD.Offers, 8000) : PasteInto(fD, hD, files2);
            if (offer2 != null && Form.ActiveForm == fD) hD.Finish(offer2, files2);
            Check("...and sent to B the old way", offer2 is { Entries.Count: 2 }, offer2 == null ? "B was offered nothing" : $"{offer2.Entries.Count} file(s)");

            // -- failed half-way: offered, never sent unasked --
            Console.WriteLine();
            Console.WriteLine("STRAIGHT ACROSS FAILED HALF-WAY: OFFERED, NOT SENT UNASKED");
            while (hD.Offers.TryDequeue(out _)) { }
            var files3 = hC.MakeFiles("halfway", 1);
            hC.Far.Send(new ClipboardMsg { Files = files3, Bytes = Size(files3), Auto = true });
            Pump(900);
            Raise(fD, "OnActivated");
            var ask3 = Dequeue(hD.Asks, 6000);
            if (ask3 != null)
            {
                hD.Far.Send(ask3);
                Dequeue(hD.Keys, 4000); Dequeue(hC.Keys, 4000);
                var broke = new BridgeEndMsg { BridgeId = ask3.BridgeId, Why = "nothing arrived from the other computer for 30 seconds" };
                hD.Far.Send(broke); hC.Far.Send(broke);
            }
            Pump(800);
            var failed = StatusOf(fD);
            Check("B's window says it did not finish, and offers this PC", failed.Contains("did not finish") && failed.Contains("through this PC instead"), Describe(failed));
            Check("...and fetches nothing until asked", hC.Pulls.IsEmpty);
            Invoke(fD, "TakePendingOffer");
            var pull3 = Dequeue(hC.Pulls, 6000);
            Check("clicking the offer sends it through this PC", pull3 is { Paths.Count: 1 });
            Check("...landing on this PC's clipboard", WaitFor(() => SameNames(ReadFiles(), files3), 15000), Names(ReadFiles()));
            Pump(500);
            if (Form.ActiveForm == fD) { var o3 = Dequeue(hD.Offers, 6000); if (o3 != null) hD.Finish(o3, files3); }
            else { var o3 = PasteInto(fD, hD, files3); }

            // -- the 50 MB rule (Clint's answer 4) --
            Console.WriteLine();
            Console.WriteLine("OVER 50 MB: A CLICK TO SEND IT STRAIGHT ACROSS");
            while (hD.Offers.TryDequeue(out _)) { }
            var files4 = hC.MakeFiles("bigone", 1);
            hC.Far.Send(new ClipboardMsg { Files = files4, Bytes = FileSetLimits.AutoFetchBytes + 1, Auto = true });
            Pump(900);
            Raise(fD, "OnActivated");
            Pump(700);
            var bigOffer = StatusOf(fD);
            Check("B's window offers to send it straight across, and asks nothing yet",
                bigOffer.Contains("click here to send it straight to STRAIGHT-B") && hD.Asks.IsEmpty, Describe(bigOffer));
            Invoke(fD, "TakePendingOffer");
            var ask4 = Dequeue(hD.Asks, 6000);
            Check("...the click asks the server", ask4 != null && ask4.Bytes == FileSetLimits.AutoFetchBytes + 1);
            if (ask4 != null) { var gone = new BridgeEndMsg { BridgeId = ask4.BridgeId, Why = "cancelled by the harness" }; hD.Far.Send(gone); hC.Far.Send(gone); }
            Pump(500);

            // -- "bring them to this PC" (Clint's answer 1) --
            Console.WriteLine();
            Console.WriteLine("BRING THEM TO THIS PC");
            var files5 = hC.MakeFiles("mine", 2);
            hC.Far.Send(new ClipboardMsg { Files = files5, Bytes = Size(files5), Auto = true });
            Pump(900);
            Invoke(fC, "TakePendingOffer");
            var pull5 = Dequeue(hC.Pulls, 6000);
            Check("the click on A's status bar fetches them to this PC", pull5 is { Paths.Count: 2 });
            Check("...onto this PC's clipboard", WaitFor(() => SameNames(ReadFiles(), files5), 15000), Names(ReadFiles()));

            // -- a server that never answers (an older one) --
            Console.WriteLine();
            Console.WriteLine("A SERVER THAT NEVER ANSWERS");
            while (hC.Pulls.TryDequeue(out _)) { }
            while (hD.Offers.TryDequeue(out _)) { }
            var files6 = hC.MakeFiles("silent", 1);
            hC.Far.Send(new ClipboardMsg { Files = files6, Bytes = Size(files6), Auto = true });
            Pump(900);
            Raise(fD, "OnActivated");
            var ask6 = Dequeue(hD.Asks, 6000);
            Pump(3000);
            Check("(asked, and no answer yet)", ask6 != null && hC.Pulls.IsEmpty);
            var pull6 = Dequeue(hC.Pulls, 12000);
            Check("after ten seconds with no answer, it goes through this PC", pull6 is { Paths.Count: 1 }, pull6 == null ? "no fetch" : "");
        }
        finally
        {
            try { if (!fC.IsDisposed) fC.Close(); } catch { }
            try { if (!fD.IsDisposed) fD.Close(); } catch { }
            Pump(300);
            hC.Stop(); hD.Stop();
        }
    }

    private static bool CanBridge(SessionForm f) =>
        typeof(SessionForm).GetProperty("CanBridge", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(f) is true;

    private static void Invoke(SessionForm f, string method) =>
        typeof(SessionForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f, null);

    /// <summary>Clicks into a window and, when it offers files, plays the far end through to the paste.</summary>
    private static FileSetOfferMsg? PasteInto(SessionForm f, FakeHost h, List<string> expected)
    {
        Raise(f, "OnActivated");
        var offer = Dequeue(h.Offers, 10000);
        if (offer != null) h.Finish(offer, expected);
        return offer;
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static bool BringToFront(Form f)
    {
        for (int i = 0; i < 3; i++)
        {
            try { f.TopMost = true; f.Activate(); f.TopMost = false; } catch { }
            Pump(400);
            if (Form.ActiveForm == f) return true;
        }
        return false;
    }

    private static string StatusOf(SessionForm f)
    {
        var fi = typeof(SessionForm).GetField("_xferLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        return (fi?.GetValue(f) as Control) is { Visible: true } l ? l.Text : "";
    }

    private static long Size(IEnumerable<string> paths) => paths.Sum(p => new FileInfo(p).Length);
    private static bool SameNames(List<string> got, List<string> want) =>
        got.Count == want.Count && want.All(w => got.Any(g => Path.GetFileName(g).Equals(Path.GetFileName(w), StringComparison.OrdinalIgnoreCase)));
    private static string Names(IEnumerable<string> paths) { var l = paths.Select(Path.GetFileName).ToList(); return l.Count == 0 ? "(nothing)" : string.Join(", ", l); }

    private static void Raise(Form f, string method)
    {
        var mi = typeof(Form).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        mi!.Invoke(f, new object[] { EventArgs.Empty });
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

    private static T? Dequeue<T>(ConcurrentQueue<T> q, int ms, Func<T, bool>? want = null) where T : class
    {
        var end = Environment.TickCount64 + ms;
        var putBack = new List<T>();
        try
        {
            while (Environment.TickCount64 < end)
            {
                while (q.TryDequeue(out var item))
                {
                    if (want == null || want(item)) return item;
                    putBack.Add(item);
                }
                Pump(50);
            }
            return null;
        }
        finally { foreach (var x in putBack) q.Enqueue(x); }
    }

    private static string? ReadText()
    {
        for (int i = 0; i < 4; i++) { try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { Thread.Sleep(80); } }
        return null;
    }

    private static List<string> ReadFiles()
    {
        for (int i = 0; i < 4; i++) { try { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : new(); } catch { Thread.Sleep(80); } }
        return new();
    }

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

    private static string Describe(string? s) => string.IsNullOrEmpty(s) ? "(nothing)" : $"\"{(s.Length > 110 ? s[..110] + "…" : s)}\"";
}

/// <summary>
/// One customer's machine, played by a loopback socket: it serves the files its customer "copied" when the console
/// asks for them (as a host does), and receives a paste the console sends it, answering where it landed.
/// </summary>
internal sealed class FakeHost
{
    public readonly string Name, Dir;
    public readonly int Port;
    public readonly ManualResetEventSlim Connected = new(false);
    /// <summary>Held shut to keep a fetch "still arriving".</summary>
    public readonly ManualResetEventSlim Gate = new(true);
    public WsConnection? Far;
    public readonly ConcurrentQueue<FileSetPullMsg> Pulls = new();
    public readonly ConcurrentQueue<FileSetOfferMsg> Offers = new();
    public readonly ConcurrentQueue<ClipboardMsg> Clips = new();
    private readonly ConcurrentQueue<FileSetAcceptMsg> _accepts = new();
    private readonly HttpListener _listener = new();
    private readonly object _rxLock = new();
    private readonly List<byte> _received = new();
    private long _expected;
    public string LastReceivedSummary { get; private set; } = "nothing received";

    /// <summary>
    /// Plays a NEW host (2026-10-15): end-to-end encrypted, as the real host is (it answers the console's hello with the
    /// session key, unsigned, and seals what it sends), and says it can copy straight across. It also plays the hub's half
    /// of a copy straight across: the console's ask and its keys are kept here for the harness to answer.
    /// </summary>
    public readonly bool Bridging;
    public readonly ConcurrentQueue<BridgeAskMsg> Asks = new();
    public readonly ConcurrentQueue<BridgeKeyMsg> Keys = new();
    public readonly ConcurrentQueue<BridgeEndMsg> Ends = new();

    public FakeHost(string name, string dir, bool bridging = false)
    {
        Name = name; Dir = dir; Bridging = bridging;
        Directory.CreateDirectory(dir);
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private int _made;
    public List<string> MakeFiles(string stem, int n)
    {
        var list = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var p = Path.Combine(Dir, $"{stem} {++_made} on {Name}.txt");
            File.WriteAllText(p, $"{Name} {stem} {_made} " + new string((char)('a' + _made % 26), 1500 + 700 * i));
            list.Add(p);
        }
        return list;
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
            var wsc = await ctx.AcceptWebSocketAsync(null);
            var conn = new WsConnection(wsc.WebSocket);
            SessionSeal? seal = null;
            byte[] sessionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var eph = E2eHandshake.NewEphemeral();
            if (Bridging) { seal = new SessionSeal(Name); seal.UseKey(sessionKey, 0); conn.Seal = seal; }
            // Handlers before RunAsync, always (HANDOVER §10).
            conn.OnMessage += m =>
            {
                switch (m)
                {
                    case E2eHelloMsg h when seal != null:
                        conn.Send(E2eHandshake.Answer(Name, eph, sessionKey, h.Pub, 1, null)!);
                        // From now on everything is sealed, and clear content refused — only the server could send it.
                        seal.Sealing = true; seal.RefuseClearContent = true;
                        break;
                    case BridgeAskMsg a: Asks.Enqueue(a); break;
                    case BridgeKeyMsg k: Keys.Enqueue(k); break;
                    case BridgeEndMsg e: Ends.Enqueue(e); break;
                    case FileSetPullMsg p: Pulls.Enqueue(p); _ = Task.Run(() => Serve(conn, p)); break;
                    case FileSetAcceptMsg a: _accepts.Enqueue(a); break;
                    case FileSetOfferMsg o:
                        lock (_rxLock) { _received.Clear(); _expected = o.TotalBytes; }
                        Offers.Enqueue(o);
                        conn.Send(new FileSetAcceptMsg { SetId = o.SetId, Accept = true, TargetDir = o.TargetDir ?? @"C:\Users\Someone\Downloads" });
                        break;
                    case ClipboardMsg c: Clips.Enqueue(c); break;
                }
            };
            conn.OnBinary += b =>
            {
                // Whole-tree chunks: the payload after the frame header, collected in arrival order (one stream).
                try { var (_, _, data) = Bin.ParseFileChunk(b); lock (_rxLock) _received.AddRange(data.ToArray()); } catch { }
            };
            Far = conn;
            Connected.Set();
            _ = conn.RunAsync();
            if (Bridging) conn.Send(new SysInfoMsg { Info = new() { ["Machine"] = Name, [HostCan.BridgeKey] = HostCan.Yes } });
        }
    }

    private async Task Serve(WsConnection conn, FileSetPullMsg p)
    {
        try
        {
            Gate.Wait(30000);
            var plan = FileSetSender.Plan(p.Paths);
            conn.Send(new FileSetOfferMsg { SetId = p.SetId, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label });
            var end = Environment.TickCount64 + 15000;
            FileSetAcceptMsg? acc = null;
            while (Environment.TickCount64 < end && acc == null)
            {
                foreach (var x in _accepts) if (x.SetId == p.SetId) { acc = x; break; }
                if (acc == null) await Task.Delay(25);
            }
            if (acc is not { Accept: true }) return;
            await FileSetSender.SendAsync(p.SetId, plan, bin => conn.SendBinary(bin), null, CancellationToken.None);
        }
        catch (Exception ex) { Console.WriteLine($"  ({Name} failed to serve a fetch: {ex.Message})"); }
    }

    /// <summary>Waits for a paste's bytes, then says where it landed, as a host does — which is what makes the console
    /// point this machine's clipboard at them.</summary>
    public void Finish(FileSetOfferMsg offer, List<string> expected)
    {
        var end = Environment.TickCount64 + 10000;
        while (Environment.TickCount64 < end) { lock (_rxLock) if (_received.Count >= _expected) break; Application.DoEvents(); Thread.Sleep(10); }
        var saved = expected.Select(e => Path.Combine(RemotePaths.PasteStaging, Path.GetFileName(e))).ToList();
        Far!.Send(new FileSetDoneMsg { SetId = offer.SetId, Ok = true, SavedDir = RemotePaths.PasteStaging, SavedPaths = saved });
    }

    /// <summary>The bytes received for the last paste are exactly these files' bytes, in some order.</summary>
    public bool ReceivedMatch(List<string> files)
    {
        byte[] got; lock (_rxLock) got = _received.ToArray();
        var want = files.Select(File.ReadAllBytes).ToList();
        long total = want.Sum(w => (long)w.Length);
        LastReceivedSummary = $"{got.Length:N0} of {total:N0} bytes";
        if (got.Length != total) return false;
        // Every file's bytes appear whole in what arrived.
        var hay = Convert.ToHexString(got);
        return want.All(w => hay.Contains(Convert.ToHexString(w), StringComparison.Ordinal));
    }

    public void Stop() { try { _listener.Stop(); } catch { } }
}
