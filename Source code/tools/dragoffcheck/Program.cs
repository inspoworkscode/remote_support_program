using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// dragoffcheck - roadmap 3.5.2: dragging files OFF the picture of the customer's screen.
//
// The REAL SessionForm and ScreenView, with no server: the link fails to connect in the
// background and the window is built regardless (as tools/toolbarprobe does). The mouse is played
// into the picture as synthetic events, the customer machine's answer to "what is selected?" is
// handed to the window's own message handler, and everything the window would send to their
// screen is recorded - so what is asserted is what their machine would actually be told to do.
//
// Clint's rule, 2026-09-18: the files selected in the folder window in front of them, and only
// that; anything else is said plainly. And a drag on THEIR screen stays theirs unless there are
// files to take - overshooting the edge while moving a window must not be cancelled on a guess.
//
// The button cannot really be held down without taking the mouse from whoever is at this laptop,
// so ScreenView.ButtonsNow is answered by this harness instead of by Windows.
//
//   dotnet run -c Debug --project tools/dragoffcheck/dragoffcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class DragOffCheck
{
    private static int pass, fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static bool held;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.WriteLine("dragoffcheck - dragging files off the picture (3.5.2)");
        Console.WriteLine();

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        var session = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "PROBE-PC", StartedBy = "Probe", Unattended = true };
        ScreenView.ButtonsNow = () => held ? MouseButtons.Left : MouseButtons.None;

        using var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.Size = new Size(1320, 850);
        f.Show();
        Pump(600);

        var view = Field<ScreenView>(f, "_view")!;
        view.Reset(1920, 1080);
        var sent = new List<Msg>();
        view.OnInput += m => sent.Add(m);
        var mid = new Point(view.Width / 2, view.Height / 2);
        var outside = new Point(-30, mid.Y);

        void Ready() { view.Stale = false; view.ControlEnabled = true; sent.Clear(); }
        string Status() => Field<Label>(f, "_xferLabel")?.Text ?? "";
        string Asked() => (string)Field<object>(f, "_selectionAsked")!;
        void Answer(params string[] paths) => Call(f, "HandleMsg", new object[] { new SelectionMsg
        {
            RequestId = Asked(), Where = "explorer", Folder = @"C:\Users\Them\Documents",
            Paths = paths.ToList(),
            Why = paths.Length > 0 ? "" : "nothing is selected in \"Documents\" — click the files there first, then drag"
        } });
        void Down(Point p) { held = true; Call(view, "OnMouseDown", new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) }); }
        void Move(Point p) => Call(view, "OnMouseMove", new object[] { new MouseEventArgs(held ? MouseButtons.Left : MouseButtons.None, 0, p.X, p.Y, 0) });
        void Up(Point p) { held = false; Call(view, "OnMouseUp", new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) }); }
        bool SentEscape() => sent.OfType<KeyMsg>().Any(k => k.Vk == (int)Keys.Escape);

        // ---- a click is a click ----
        Console.WriteLine("== a click ==");
        Ready();
        var before = Asked();
        Down(mid); Up(mid);
        Check("a click on the picture asks their machine nothing", Asked() == before);
        Check("...and goes to their screen as a click", sent.OfType<MouseMsg>().Count(m => m.Action is "down" or "up") == 2);

        // ---- a drag that stays on the picture ----
        Console.WriteLine();
        Console.WriteLine("== a drag that stays on the picture ==");
        Ready();
        Down(mid); Move(mid with { X = mid.X + 40 }); Move(mid with { X = mid.X + 80 });
        var askedInside = Asked();
        Check("starting to drag asks what is selected over there", askedInside.Length > 0 && askedInside != before);
        Up(mid with { X = mid.X + 80 });
        Check("...but it stays their drag: moves and the release go to their screen, nothing is cancelled",
            !SentEscape() && sent.OfType<MouseMsg>().Count(m => m.Action == "move") >= 2 && sent.OfType<MouseMsg>().Any(m => m.Action == "up"));

        // ---- off the picture with files selected ----
        Console.WriteLine();
        Console.WriteLine("== off the picture, with files selected over there ==");
        Ready();
        Down(mid); Move(mid with { X = mid.X - 40 });
        Answer(@"C:\Users\Them\Documents\report.pdf", @"C:\Users\Them\Documents\Logs");
        Move(mid with { X = 20 });
        var lastInside = sent.OfType<MouseMsg>().Last(m => m.Action == "move");
        sent.Clear();
        Move(outside);
        var keys = sent.OfType<KeyMsg>().ToList();
        var ups = sent.OfType<MouseMsg>().Where(m => m.Action == "up").ToList();
        Check("their drag is cancelled with Escape, down then up",
            keys.Count == 2 && keys.All(k => k.Vk == (int)Keys.Escape) && keys[0].Down && !keys[1].Down, $"{keys.Count} key(s)");
        Check("...then their button is released where it last was, with the left button",
            ups.Count == 1 && ups[0].X == lastInside.X && ups[0].Y == lastInside.Y && ups[0].Button == 0,
            ups.Count == 1 ? $"{ups[0].X},{ups[0].Y} (last move {lastInside.X},{lastInside.Y})" : $"{ups.Count} ups");
        // The fetch itself asks Windows about the button, which really is up under this harness, so
        // it takes the "let go early" branch: still fetching, onto the clipboard when they land.
        Check("the drag is taken over here: what is selected is fetched from their machine", Status().Contains("fetching") && Status().Contains("PROBE-PC"), Status());
        sent.Clear();
        Move(outside with { X = -60 }); Move(outside with { X = -90 }); Move(mid);
        Check("nothing more of that gesture goes to their screen, even back over the picture", sent.Count == 0, $"{sent.Count} message(s)");
        held = false;
        Move(mid with { Y = mid.Y + 10 });
        Check("once the button is up the pointer is theirs again, with no MouseUp needed",
            sent.OfType<MouseMsg>().Any(m => m.Action == "move"));

        // ---- off the picture with nothing selected ----
        Console.WriteLine();
        Console.WriteLine("== off the picture, nothing selected (moving a window, say) ==");
        Ready();
        Down(mid); Move(mid with { X = mid.X - 40 });
        Answer();
        Move(outside);
        Check("their drag is NOT cancelled", !SentEscape());
        Check("...the status bar says why nothing came, in their machine's words",
            Status().StartsWith("Nothing to drag here") && Status().Contains("nothing is selected"), Status());
        sent.Clear();
        Move(mid);
        Check("...and the drag goes on being theirs when the pointer comes back", sent.OfType<MouseMsg>().Any(m => m.Action == "move"));
        Up(mid);
        Check("...ending with their own release", sent.OfType<MouseMsg>().Any(m => m.Action == "up"));

        // ---- no answer ----
        Console.WriteLine();
        Console.WriteLine("== their machine does not answer ==");
        Ready();
        Down(mid); Move(mid with { X = mid.X - 40 });
        var t0 = DateTime.UtcNow;
        Move(outside);
        var waited = (DateTime.UtcNow - t0).TotalSeconds;
        Check("it waits a moment, not for ever", waited is > 2 and < 5, $"{waited:0.0} s");
        Check("...leaves their drag alone", !SentEscape());
        Check("...and says what to do instead", Status().Contains("did not say what is selected") && Status().Contains("Files"), Status());
        Up(mid);

        // ---- text dropped onto the picture ----
        Console.WriteLine();
        Console.WriteLine("== text dropped onto the picture ==");
        var t = new DataObject(); t.SetData(DataFormats.UnicodeText, "café 42");
        Check("text is found in a drop, accents and all", SessionForm.DroppedText(t) == "café 42", SessionForm.DroppedText(t) ?? "null");
        var both = new DataObject(); both.SetData(DataFormats.UnicodeText, "x"); both.SetData(DataFormats.FileDrop, new[] { @"C:\a.txt" });
        Check("files win when a drop carries both", SessionForm.DroppedText(both) == null);
        Check("an empty drop has no text", SessionForm.DroppedText(new DataObject()) == null);

        // ---- our own drag passing back over the picture ----
        Console.WriteLine();
        Console.WriteLine("== our own drag passing back over the picture ==");
        var files = new DataObject(); files.SetData(DataFormats.FileDrop, new[] { @"C:\a.txt" });
        SetField(f, "_dragOutBusy", true);
        var e1 = new DragEventArgs(files, 0, 10, 10, DragDropEffects.Copy, DragDropEffects.None);
        Call(view, "OnDragEnter", new object[] { e1 });
        SetField(f, "_dragOutBusy", false);
        var e2 = new DragEventArgs(files, 0, 10, 10, DragDropEffects.Copy, DragDropEffects.None);
        Call(view, "OnDragEnter", new object[] { e2 });
        Check("files being dragged OUT are not accepted back onto their desktop", e1.Effect == DragDropEffects.None, e1.Effect.ToString());
        Check("...while files dragged IN from here still are", e2.Effect == DragDropEffects.Copy, e2.Effect.ToString());

        f.Close();
        Console.WriteLine();
        Console.WriteLine($"{pass} of {pass + fail} checks passed.");
        return fail == 0 ? 0 : 1;
    }

    private static void Pump(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); } }

    private static void Call(object o, string method, object[] args)
    {
        for (var ty = o.GetType(); ty != null; ty = ty.BaseType)
        {
            var m = ty.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (m != null) { m.Invoke(o, args); return; }
        }
        throw new MissingMethodException(o.GetType().Name, method);
    }
    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;
    private static void SetField(object o, string name, object v) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, v);
}
