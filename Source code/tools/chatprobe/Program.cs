using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteSupport.Client;
using RemoteSupport.Shared;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// Does the customer's chat window stay where the customer put it?
//
// Stage 3.5.5. Before it, the popup was pinned to the bottom-right corner and RE-PINNED THERE ON
// EVERY MESSAGE. That is a real fault and not a preference: the bottom-right of a Windows desktop
// is where icons live, so the window could sit on top of the very icon the customer had just been
// asked to double-click — and dragging it out of the way worked only until the technician typed
// the next sentence, which put it straight back.
//
// WHY THIS IS WORTH A HARNESS RATHER THAN A LOOK. The failure is not visible in a screenshot. A
// picture of the window in the corner is correct on first open and is the bug on the fifth
// message, and the difference between them is a flag nobody can see. It is also exactly the kind
// of thing that comes back: anything that calls Reposition() unconditionally restores the fault,
// and it would be noticed by a customer on a call rather than here.
//
// It drives the REAL ChatPopup with no server, no session and no customer machine.
//
//   dotnet run -c Release --project tools/chatprobe/chatprobe.csproj [-- <out-dir>]
//
// IT TOUCHES THE REAL windows.json, because the remembered position is the thing being tested and
// a fake one would test nothing. So it takes a copy first and puts it back afterwards, even if it
// fails — the harnesses that took the real clipboard while Clint was using the machine are the
// reason that is not optional (HANDOVER §27).
// ---------------------------------------------------------------------------------------------

internal static class ChatProbe
{
    private const int WmExitSizeMove = 0x0232;
    private const int WmNcHitTest = 0x0084;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static int _pass, _fail;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "chat");
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "RemoteSupport", "windows.json");
        var backup = store + ".chatprobe-backup";
        bool hadStore = File.Exists(store);
        if (hadStore) File.Copy(store, backup, true);

        try { return Run(outDir); }
        finally
        {
            // Put Clint's own window positions back, whatever happened above.
            try
            {
                if (hadStore) { File.Copy(backup, store, true); File.Delete(backup); }
                else if (File.Exists(store)) File.Delete(store);
                Console.WriteLine();
                Console.WriteLine("  (your own remembered window positions were restored)");
            }
            catch (Exception ex) { Console.WriteLine("  (could not restore windows.json: " + ex.Message + ")"); }
        }
    }

    private static int Run(string outDir)
    {
        var brand = Branding.Load();
        // AppSurface.Customer: this is the CUSTOMER's window, so it must be measured against the
        // palette the customer's machine draws it with, not the console's.
        var theme = Theme.FromBranding(brand, AppSurface.Customer);

        // Start from nothing remembered, which is a customer who has never touched it.
        WindowPlacement.Forget("client.chat");

        var wa = Screen.PrimaryScreen!.WorkingArea;

        Console.WriteLine();
        Console.WriteLine("== where it opens when nobody has moved it ==");
        var popup = new ChatPopup(theme, "Anywhere IT") { ReserveBottom = 64 };
        popup.Incoming(new ChatMsg { From = "Clint", Text = "Morning — can you double-click the icon on your desktop?", Ts = DateTimeOffset.Now });
        Settle();

        Check("it opens in the bottom-right corner", popup.Visible
              && popup.Right <= wa.Right && popup.Right > wa.Right - 40
              && popup.Bottom < wa.Bottom, popup.Bounds.ToString());
        Check("and leaves room for the session banner below it",
              popup.Bottom <= wa.Bottom - 64, $"bottom {popup.Bottom} against a working area ending at {wa.Bottom}");

        // Everything in it must actually fit. This window is 170 px wide by design — "narrow
        // enough to sit out of the way" — and a Send button whose bottom half is off the edge of
        // its own window would look like the window being too small rather than a layout fault,
        // so nobody would report it as one. Asked of the numbers because the screenshot is the
        // thing that makes it ambiguous.
        foreach (var c in Leaves(popup).Where(c => c.Visible && c.Width > 4 && c.Height > 4))
        {
            var r = Relative(c, popup);
            Check($"'{Describe(c)}' fits inside the window",
                  r.Right <= popup.ClientSize.Width + 1 && r.Bottom <= popup.ClientSize.Height + 1 && r.X >= -1 && r.Y >= -1,
                  $"{r} against a client area of {popup.ClientSize.Width}x{popup.ClientSize.Height}");
        }

        // And nothing a customer clicks may be covered by anything else. This is toolbarprobe's
        // rule applied to the other end of the product, and it earned its place immediately: the
        // reply box is a one-line TextBox, which takes the height its FONT needs and ignores the
        // height it is docked into, so it hung 7 px over the Send button. The top strip of Send
        // typed into the box instead of sending. A screenshot cannot show that — a text box drawn
        // over a button looks exactly like a text box above one.
        var clickable = Leaves(popup).Where(c => c.Visible && c is ButtonBase or TextBox).ToList();
        for (int i = 0; i < clickable.Count; i++)
            for (int j = i + 1; j < clickable.Count; j++)
            {
                var ra = Relative(clickable[i], popup);
                var rb = Relative(clickable[j], popup);
                if (!ra.IntersectsWith(rb)) continue;
                var over = Rectangle.Intersect(ra, rb);
                Check($"'{Describe(clickable[i])}' is not covered by '{Describe(clickable[j])}'", false,
                      $"{over.Width}x{over.Height} px of overlap — one of them eats the other's clicks");
            }
        Check("nothing the customer clicks is covered by anything else", true,
              $"{clickable.Count} clickable controls");

        Console.WriteLine();
        Console.WriteLine("== what can be dragged, and what deliberately cannot ==");
        var handles = DragHandles(popup);
        Check("the window has drag handles at all", handles.Count >= 2, string.Join(", ", handles.Select(Describe)));
        Check("the header is one of them", handles.Any(c => c is Panel),
              "the company name across the top IS this window's title bar");
        Check("so is the company name itself", handles.Any(c => c is Label),
              "a Label swallows the mouse, so the panel underneath it is not enough on its own");
        // The log must stay selectable. A customer copying a licence key out of what the
        // technician typed is a thing that happens, and a log that moved the window instead
        // would be the kind of small maddening fault nobody reports and everybody remembers.
        Check("the message log is NOT a drag handle", !handles.Any(c => c is RichTextBox),
              "selecting text out of it has to keep working");
        Check("nor is the reply box", !handles.Any(c => c is TextBox), "it has to be typable");

        // The window's own background — the strip of padding around the controls — must drag too,
        // or a grab that lands a few pixels outside the header does nothing at all.
        var hit = SendMessage(popup.Handle, WmNcHitTest, IntPtr.Zero,
                              (IntPtr)((popup.Top + 4) << 16 | (popup.Left + 4) & 0xFFFF));
        Check("the window's own background drags it too", hit == (IntPtr)HtCaption,
              $"WM_NCHITTEST returned {hit} where {HtCaption} is 'title bar'");

        Console.WriteLine();
        Console.WriteLine("== it stays where it is put ==");
        // What a real drag leaves behind: a new position, then WM_EXITSIZEMOVE. Windows runs the
        // drag itself inside a modal loop that cannot be driven from in here, so this is the end
        // of it rather than the whole of it — the drag gesture itself is the manual check below.
        var moved = new Point(wa.Left + 120, wa.Top + 90);
        popup.Location = moved;
        SendMessage(popup.Handle, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
        Settle();
        Check("moving it is recorded", WindowPlacement.Recall("client.chat")?.Location == moved,
              WindowPlacement.Recall("client.chat")?.ToString() ?? "nothing was remembered");

        // THE BUG. Every incoming message called Reposition(), which pinned the window back into
        // the corner — so a customer who moved it out of the way had it dragged back under their
        // cursor once per sentence.
        popup.Incoming(new ChatMsg { From = "Clint", Text = "…the one behind this window.", Ts = DateTimeOffset.Now });
        popup.Outgoing("moved it, thanks");
        Settle();
        Check("a message does NOT drag it back to the corner", popup.Location == moved,
              popup.Location == moved ? "still where the customer put it" : $"it jumped to {popup.Location}");

        Shoot(popup, outDir, "chat-moved");
        popup.Dispose();
        Settle();

        Console.WriteLine();
        Console.WriteLine("== and remembers it next time it opens ==");
        var again = new ChatPopup(theme, "Anywhere IT") { ReserveBottom = 64 };
        again.Incoming(new ChatMsg { From = "Clint", Text = "back again", Ts = DateTimeOffset.Now });
        Settle();
        Check("a new session opens it where the customer left it", again.Location == moved,
              again.Location == moved ? moved.ToString() : $"opened at {again.Location} instead of {moved}");
        Shoot(again, outDir, "chat-remembered");
        again.Dispose();
        Settle();

        Console.WriteLine();
        Console.WriteLine("== a position that is no longer anywhere ==");
        // A laptop undocked, a projector unplugged, a resolution changed. Restoring the position
        // faithfully would open a window the customer cannot see, click or close — and it would
        // be reported as chat having stopped working, which is the most misleading outcome
        // available.
        WindowPlacement.Remember("client.chat", new Rectangle(-9000, -9000, 170, 320));
        Check("a position off every screen is refused", WindowPlacement.Recall("client.chat") == null,
              "so the window opens where it can be seen instead");
        var recovered = new ChatPopup(theme, "Anywhere IT") { ReserveBottom = 64 };
        recovered.Incoming(new ChatMsg { From = "Clint", Text = "are you there?", Ts = DateTimeOffset.Now });
        Settle();
        Check("and the window opens in the corner rather than off-screen",
              WindowPlacement.IsReachable(recovered.Bounds), recovered.Bounds.ToString());
        recovered.Dispose();

        // Only a hand can do this one, and saying so is better than a harness pretending to.
        Console.WriteLine();
        Console.WriteLine("== two things only a person can check ==");
        Console.WriteLine("   1. Drag the popup by the company name across its top. It must move with the");
        Console.WriteLine("      mouse, snap to screen edges like any window, and stay put afterwards.");
        Console.WriteLine("   2. Select a line of text in the message log. It must SELECT, not move the");
        Console.WriteLine("      window — that is why the log is not a drag handle.");

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        Console.WriteLine($"screenshots in {outDir}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>The popup's own list of what a drag may start from, read rather than guessed at:
    /// a probe that re-implemented the rule would agree with itself and prove nothing.</summary>
    private static List<Control> DragHandles(ChatPopup p)
    {
        var prop = typeof(ChatPopup).GetProperty("DragHandles", BindingFlags.Instance | BindingFlags.NonPublic);
        return (prop?.GetValue(p) as IEnumerable<Control>)?.ToList() ?? new List<Control>();
    }

    private static string Describe(Control c) =>
        c.GetType().Name + (string.IsNullOrWhiteSpace(c.Text) ? "" : $" \"{c.Text}\"");

    private static void Settle() { for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    private static IEnumerable<Control> Leaves(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var g in Leaves(c)) yield return g;
        }
    }

    /// <summary>Bounds of a control relative to the window it lives in, whatever it is nested in.</summary>
    private static Rectangle Relative(Control c, Control root)
    {
        var p = new Point(0, 0);
        for (var x = c; x != null && x != root; x = x.Parent) { p.X += x.Left; p.Y += x.Top; }
        return new Rectangle(p, c.Size);
    }

    private static void Shoot(Form f, string outDir, string name)
    {
        try
        {
            using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            var p = Path.Combine(outDir, name + ".png");
            bmp.Save(p, ImageFormat.Png);
            Console.WriteLine($"        screenshot: {p}");
        }
        catch (Exception ex) { Console.WriteLine($"        (could not screenshot {name}: {ex.GetType().Name})"); }
    }
}
