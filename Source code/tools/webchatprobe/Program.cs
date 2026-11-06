using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// webchatprobe - the console's Website chat window and its button (stage 3.8.3, 2026-09-30).
//
// webchatcheck proves what the hub SENDS a console. This hands the REAL dashboard and its REAL
// Website chat window those messages, with no server — through TechLink's own message handler,
// exactly where a socket would deliver them — and asks what a technician would see:
//
//   no button until the company has website chat on; then it sits beside Office chat, and counts
//   who is waiting, in red
//   a new visitor pops the window up on them without taking the keyboard
//   once somebody else answers, the visitor's lines no longer pop it — for the one answering, they do
//   a new visitor does not take away the conversation being typed in
//   the one-click links: the app's, and browser support's only while it is on
//   an ended chat cannot be typed in; a refused line says why; nothing runs off the window (§30)
//
//   dotnet run -c Debug --project tools/webchatprobe/webchatprobe.csproj [picture folder]
// ---------------------------------------------------------------------------------------------

internal static class WebChatProbe
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static TechLink _link = null!;
    private static readonly MethodInfo Handle = typeof(TechLink).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Deliver(Msg m) { Handle.Invoke(_link, new object[] { m }); Settle(); }
    private static T F<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private const string Me = "Sue Tech";
    private static long _line = 100;

    private static WebChatInfo Chat(int id, string name, string answeredBy = "", bool ended = false, string endedBy = "") => new()
    {
        Id = id, Name = name, Email = name.Split(' ')[0].ToLower() + "@example.com", Phone = "", Page = "https://anywhereit.example/services",
        AnsweredBy = answeredBy, StartedUtc = DateTime.UtcNow, Ended = ended, EndedBy = endedBy, VisitorHere = true, LastText = "Do you fix Macs?"
    };
    private static WebChatLineInfo Line(int chat, bool visitor, string name, string text) =>
        new() { Id = ++_line, ChatId = chat, FromVisitor = visitor, Name = name, Text = text, SentUtc = DateTime.UtcNow };

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);
        Console.WriteLine("webchatprobe - the console's Website chat window and its button");
        Console.WriteLine();

        var brand = Branding.Load();
        _link = new TechLink("ws://127.0.0.1:59999");   // nothing listens: messages are handed in
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(_link, Me);

        using var dash = new DashboardForm(brand, _link)
        { StartPosition = FormStartPosition.Manual, WindowState = FormWindowState.Normal, Location = new Point(0, 0), Size = new Size(1600, 900) };
        dash.Show(); Settle();
        var btn = F<RoundButton>(dash, "_webChatBtn");
        var office = F<RoundButton>(dash, "_staffChatBtn");
        var newTicket = F<RoundButton>(dash, "_newTicketBtn");
        var chat = F<WebChatForm>(dash, "_webChat");

        // Something else has the keyboard, as it would: a customer's session, say.
        using var other = new Form { Text = "stand-in for a session window", StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), Size = new Size(400, 300) };
        var otherBox = new TextBox { Dock = DockStyle.Top };
        other.Controls.Add(otherBox);
        other.Show(); other.Activate(); otherBox.Focus(); Settle();

        Console.WriteLine("== the button ==");
        Check("no Website chat button before the company has it on", !btn.Visible);
        Deliver(new WebChatListMsg { Enabled = true, AppLink = "https://support.example/get/anywhere-it", HelpLink = "" });
        Check("switched on: the button shows", btn.Visible && btn.Text == "Website chat");
        Check("...beside Office chat, without touching it or New ticket", btn.Right < office.Left && newTicket.Right < btn.Left && btn.Top == office.Top,
            $"{newTicket.Right} < {btn.Left}..{btn.Right} < {office.Left}");
        Check("...wide enough for its widest wording", btn.Width > TextRenderer.MeasureText("Website chat (99)", btn.Font).Width, $"{btn.Width} px");
        Check("the window is still hidden: nobody is waiting", !chat.Visible);

        Console.WriteLine();
        Console.WriteLine("== a visitor arrives ==");
        Deliver(new WebChatUpdateMsg { Chat = Chat(1, "Vic Visitor") });
        Check("it pops up", chat.Visible);
        Check("...on them", F<int>(chat, "_open") == 1 && F<Label>(chat, "_title").Text == "Vic Visitor");
        Check("...without taking the keyboard from what they were doing", otherBox.Focused || !chat.ContainsFocus, $"chat has focus: {chat.ContainsFocus}");
        Check("...saying nobody is answering yet", F<Label>(chat, "_state").Text == "Waiting for somebody to answer");
        Check("...with how to reach them and where they are on the site",
            F<Label>(chat, "_sub").Text.Contains("vic@example.com") && F<Label>(chat, "_sub").Text.Contains("anywhereit.example/services"), F<Label>(chat, "_sub").Text);
        Check("the button counts them, in red", btn.Text == "Website chat (1)" && btn.Style == BtnStyle.Danger);
        Deliver(new WebChatHistoryMsg { ChatId = 1, Lines = new() { Line(1, true, "Vic Visitor", "Do you fix Macs?") } });
        var log = F<RichTextBox>(chat, "_log");
        Check("their question is drawn", log.Text.Contains("Vic Visitor: Do you fix Macs?"), log.Text.Replace("\n", " | "));
        Deliver(new WebChatHistoryMsg { ChatId = 99, Lines = new() { Line(99, true, "Nobody", "stale") } });
        Check("an answer for a chat that is not open is ignored", !log.Text.Contains("stale"));
        var app = F<RoundButton>(chat, "_appBtn"); var help = F<RoundButton>(chat, "_helpBtn"); var end = F<RoundButton>(chat, "_endBtn");
        Check("App link is there, and its words fit", app.Visible && app.Width > TextRenderer.MeasureText(app.Text, app.Font).Width);
        Check("no browser support link while browser support is off", !help.Visible);
        Check("End chat is there", end.Visible && end.Width > TextRenderer.MeasureText(end.Text, end.Font).Width);
        Shot(chat, outDir, "webchat-popped");

        Console.WriteLine();
        Console.WriteLine("== somebody else answers ==");
        Deliver(new WebChatUpdateMsg { Chat = Chat(1, "Vic Visitor", answeredBy: "Tom Tech") });
        Check("it says who", F<Label>(chat, "_state").Text == "Tom Tech is answering");
        Check("the button stops counting them", btn.Text == "Website chat" && btn.Style == BtnStyle.Neutral);
        chat.Hide(); Settle();
        Deliver(new WebChatLineMsg { ChatId = 1, Line = Line(1, true, "Vic Visitor", "It's a MacBook Air.") });
        Check("the visitor's next line does NOT pop it up — it is Tom's", !chat.Visible);

        Console.WriteLine();
        Console.WriteLine("== a chat that is theirs ==");
        Deliver(new WebChatUpdateMsg { Chat = Chat(2, "Wendy Web", answeredBy: Me) });
        Check("a chat they already answer, arriving, does not pop it", !chat.Visible);
        Deliver(new WebChatLineMsg { ChatId = 2, Line = Line(2, true, "Wendy Web", "Are you still there?") });
        Check("the visitor they are answering writes: it pops up", chat.Visible);
        Check("...on that chat", F<int>(chat, "_open") == 2, $"{F<int>(chat, "_open")}");
        Check("...saying they are answering", F<Label>(chat, "_state").Text == "You are answering");
        Deliver(new WebChatHistoryMsg { ChatId = 2, Lines = new() { Line(2, true, "Wendy Web", "Are you still there?") } });

        Console.WriteLine();
        Console.WriteLine("== while typing ==");
        F<InputBox>(chat, "_input").Value = "Yes — one moment";
        Deliver(new WebChatUpdateMsg { Chat = Chat(3, "Newt New") });
        Check("a new visitor does NOT take away the conversation being typed in", F<int>(chat, "_open") == 2 && F<InputBox>(chat, "_input").Value == "Yes — one moment");
        Check("...but is in the list, and counted", F<ListBox>(chat, "_list").Items.Cast<object>().Contains(3) && btn.Text == "Website chat (1)");
        F<InputBox>(chat, "_input").Value = "";

        Console.WriteLine();
        Console.WriteLine("== refused, and ended ==");
        Deliver(new WebChatLineMsg { ChatId = 2, Error = "This chat has ended." });
        var err = F<Label>(chat, "_error");
        Check("a refused line says why, under the conversation", err.Text == "This chat has ended." && err.Height > 0);
        Deliver(new WebChatUpdateMsg { Chat = Chat(2, "Wendy Web", answeredBy: Me, ended: true, endedBy: "visitor") });
        Check("an ended chat says so", F<Label>(chat, "_state").Text == "The visitor ended the chat.");
        Check("...and cannot be typed in, or sent links", !F<InputBox>(chat, "_input").Box.Enabled && !F<Panel>(chat, "_tools").Visible);
        Shot(chat, outDir, "webchat-ended");

        Console.WriteLine();
        Console.WriteLine("== hand over (2026-10-15) ==");
        var hand = F<RoundButton>(chat, "_handBtn");
        var menu = F<ContextMenuStrip>(chat, "_handMenu");
        Deliver(new WebChatListMsg { Enabled = true, MayHandOverAny = false, AppLink = "https://support.example/get/anywhere-it",
            Chats = new() { Chat(4, "Hal Help", answeredBy: "Tom Tech"), Chat(5, "Una Waiting"), Chat(6, "Mia Mine", answeredBy: Me) } });
        chat.ShowFor(4); Settle();
        Check("a chat a colleague is answering: no Hand over for a technician (the server would refuse)", !hand.Visible);
        chat.ShowFor(5); Settle();
        Check("a chat nobody has answered: Hand over is there", hand.Visible && hand.Width > TextRenderer.MeasureText(hand.Text, hand.Font).Width);
        chat.ShowFor(6); Settle();
        Check("their own chat: Hand over is there, beside End chat", hand.Visible && hand.Right <= end.Left && hand.Top == end.Top, $"{hand.Left}-{hand.Right} / {end.Left}");
        hand.PerformClick(); Settle();
        Deliver(new WebChatPeopleMsg { ChatId = 6, Names = new() { "Olive Office", Me, "Tom Tech" } });
        var offered = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
        Check("pressed: the people signed in are offered — never the one who has it", menu.Visible && offered.SequenceEqual(new[] { "Olive Office", "Tom Tech" }), string.Join(", ", offered));
        menu.Close(); Settle();
        chat.ShowFor(5); Settle();
        hand.PerformClick(); Settle();
        Deliver(new WebChatPeopleMsg { ChatId = 5, Names = new() { "Olive Office", Me } });
        offered = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
        Check("one nobody has answered: they may give it to themselves too, marked as them", offered.Contains($"{Me} (you)") && offered.Contains("Olive Office"), string.Join(", ", offered));
        menu.Close(); Settle();
        Deliver(new WebChatPeopleMsg { ChatId = 4, Names = new() { "Olive Office" } });
        Check("an answer for another chat opens no menu", !menu.Visible);
        chat.ShowFor(6); Settle();
        Deliver(new WebChatPeopleMsg { ChatId = 6, Names = new() { Me } });
        Check("nobody else signed in: it says so, under the conversation", F<Label>(chat, "_error").Text == "Nobody else is signed in right now.");
        Deliver(new WebChatHistoryMsg { ChatId = 6, Lines = new() { Line(6, true, "Mia Mine", "Hi"), new WebChatLineInfo { Id = ++_line, ChatId = 6, Note = true, Text = "Tom Tech has taken over this chat.", SentUtc = DateTime.UtcNow } } });
        Check("the hand-over's line is drawn as a note — nobody's name before it", log.Text.Contains("Tom Tech has taken over this chat.") && !log.Text.Contains(": Tom Tech has taken over"),
            log.Text.Replace("\n", " | "));
        Shot(chat, outDir, "webchat-handover");
        Deliver(new WebChatListMsg { Enabled = true, MayHandOverAny = true, AppLink = "https://support.example/get/anywhere-it", Chats = new() { Chat(4, "Hal Help", answeredBy: "Tom Tech") } });
        chat.ShowFor(4); Settle();
        Check("a supervisor or the owner: Hand over on a colleague's chat too", hand.Visible);
        chat.Hide(); Settle();
        Deliver(new WebChatUpdateMsg { Chat = new WebChatInfo { Id = 7, Name = "Gus Given", AnsweredBy = "Tom Tech", VisitorHere = true, StartedUtc = DateTime.UtcNow } });
        Check("a chat a colleague has does not pop up", !chat.Visible);
        var given = Chat(7, "Gus Given", answeredBy: Me); given.HandedOverBy = "Tom Tech";
        Deliver(new WebChatUpdateMsg { Chat = given });
        Check("handed to THEM by Tom: it pops up, on that chat", chat.Visible && F<int>(chat, "_open") == 7);
        Check("...without taking the keyboard", !chat.ContainsFocus || otherBox.Focused);
        Check("...saying they are answering now", F<Label>(chat, "_state").Text == "You are answering");
        chat.Hide(); Settle();
        var took = Chat(8, "Tia Taken", answeredBy: Me); took.HandedOverBy = Me;
        Deliver(new WebChatUpdateMsg { Chat = took });
        Check("taking one themselves is not news to them: nothing pops", !chat.Visible);

        Console.WriteLine();
        Console.WriteLine("== fits (§30) ==");
        Deliver(new WebChatListMsg { Enabled = true, Chats = new() { Chat(3, "Newt New") }, AppLink = "https://support.example/get/anywhere-it", HelpLink = "https://support.example/help/anywhere-it" });
        chat.ShowFor(3); Settle();
        Check("with browser support on, its link is offered too", help.Visible && help.Width > TextRenderer.MeasureText(help.Text, help.Font).Width);
        chat.WindowState = FormWindowState.Normal;
        chat.Size = chat.MinimumSize; Settle();
        var tools = F<Panel>(chat, "_tools");
        Check("at its smallest the four buttons still fit on one row", hand.Visible && app.Right <= help.Left && help.Right <= hand.Left && hand.Right <= end.Left && end.Right <= tools.ClientSize.Width + 1,
            $"{app.Left}-{app.Right}, {help.Left}-{help.Right}, {hand.Left}-{hand.Right}, {end.Left}-{end.Right} in {tools.ClientSize.Width}");
        Check("nothing runs off the right-hand side", AllControls(chat).Where(c => c.Visible && c.Parent != null).All(c => c.Right <= c.Parent!.ClientSize.Width + 1),
            string.Join(", ", AllControls(chat).Where(c => c.Visible && c.Parent != null && c.Right > c.Parent.ClientSize.Width + 1).Select(c => c.GetType().Name)));
        Shot(chat, outDir, "webchat-smallest");

        Deliver(new WebChatListMsg { Enabled = false });
        Check("switched off, with nobody waiting: the button goes", !btn.Visible);
        chat.Hide();
        dash.GetType().GetMethod("ExitForReal", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(dash, null);

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static IEnumerable<Control> AllControls(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in AllControls(x)) yield return y; } }
    private static void Settle() { for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    private static void Shot(Form f, string dir, string name)
    {
        try
        {
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"        (no picture: {ex.Message})"); }
    }
}
