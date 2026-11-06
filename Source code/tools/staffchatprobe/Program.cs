using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// staffchatprobe - the console's office chat window (stage 3.8.1, 2026-09-30).
//
// chatcheck proves what the hub SENDS a console. This hands the REAL window those messages, with no
// server — through TechLink's own message handler, exactly where a socket would deliver them — and
// asks what a technician would see:
//
//   it starts hidden, and the Office chat button's count is the total unread
//   a message TO them pops it up on that conversation, without taking the keyboard
//   an Everyone message that does not name them does NOT pop it up; one that names them does
//   while they are typing to one person, a message from another does not steal the conversation
//   a refused send says why; an answer for a conversation they have left is ignored
//   Send fits its text, and nothing runs off the window (§30)
//
//   dotnet run -c Debug --project tools/staffchatprobe/staffchatprobe.csproj [picture folder]
// ---------------------------------------------------------------------------------------------

internal static class StaffChatProbe
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
    private static long _id = 100;
    private static StaffChatInfo Msg(string from, string to, string text) => new() { Id = ++_id, From = from, To = to, Text = text, SentUtc = DateTime.UtcNow };

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);
        Console.WriteLine("staffchatprobe - the console's office chat window");
        Console.WriteLine();

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        _link = new TechLink("ws://127.0.0.1:59999");   // nothing listens: messages are handed in
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(_link, "Sue Tech");

        // Something else has the keyboard, as it would: the dashboard, or a customer's session.
        using var other = new Form { Text = "stand-in for the dashboard", StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), Size = new Size(400, 300) };
        var otherBox = new TextBox { Dock = DockStyle.Top };
        other.Controls.Add(otherBox);
        other.Show(); other.Activate(); otherBox.Focus(); Settle();

        using var chat = new StaffChatForm(_link, theme);
        int unread = -1;
        chat.UnreadChanged += n => unread = n;
        Settle();

        Console.WriteLine("== hidden until needed ==");
        Check("it starts hidden", !chat.Visible);
        Deliver(new StaffChatOverviewMsg
        {
            People = new() { "Bob Tech", "Oscar Office", "Sam Supervisor" },
            Threads = new()
            {
                new() { With = "", Unread = 2, Last = Msg("Bob Tech", "", "Morning all") },
                new() { With = "Sam Supervisor", Unread = 1, Last = Msg("Sam Supervisor", "Sue Tech", "Acme quote?") }
            },
            TotalUnread = 3
        });
        var list = F<ListBox>(chat, "_list");
        Check("the dashboard's button is told the total unread", unread == 3, unread.ToString());
        Check("the list is Everyone first, then whoever spoke last, then by name",
            list.Items.Cast<string>().SequenceEqual(new[] { "", "Sam Supervisor", "Bob Tech", "Oscar Office" }),
            string.Join(", ", list.Items.Cast<string>().Select(s => s.Length == 0 ? "Everyone" : s)));
        Deliver(new StaffChatMsg { Message = Msg("Bob Tech", "", "Coffee's on") });
        Check("an Everyone message that does not name them does NOT pop it up", !chat.Visible);

        Console.WriteLine();
        Console.WriteLine("== a message to them ==");
        Deliver(new StaffChatMsg { Message = Msg("Oscar Office", "Sue Tech", "Customer at the counter for you") });
        Check("a message TO them pops it up", chat.Visible);
        Check("...on that conversation", F<string>(chat, "_with") == "Oscar Office", F<string>(chat, "_with"));
        Check("...without taking the keyboard from what they were doing", otherBox.Focused || !chat.ContainsFocus, $"chat has focus: {chat.ContainsFocus}");
        Deliver(new StaffChatHistoryMsg { With = "Oscar Office", Messages = new() { Msg("Oscar Office", "Sue Tech", "Customer at the counter for you") } });
        var log = F<RichTextBox>(chat, "_log");
        Check("...and the conversation is drawn", log.Text.Contains("Oscar Office: Customer at the counter for you"), log.Text.Replace("\n", " | "));
        Deliver(new StaffChatHistoryMsg { With = "Bob Tech", Messages = new() { Msg("Bob Tech", "Sue Tech", "stale") } });
        Check("an answer for a conversation they are not in is ignored", !log.Text.Contains("stale"));
        Shot(chat, outDir, "staffchat-popped");

        Console.WriteLine();
        Console.WriteLine("== they are typing ==");
        var input = F<InputBox>(chat, "_input");
        input.Value = "On my way";
        Deliver(new StaffChatMsg { Message = Msg("Sam Supervisor", "Sue Tech", "Also, the Acme quote?") });
        Check("a message from somebody else does NOT take away the conversation they are typing in",
            F<string>(chat, "_with") == "Oscar Office" && input.Value == "On my way", F<string>(chat, "_with"));
        input.Value = "";
        Deliver(new StaffChatMsg { Message = Msg("Oscar Office", "Sue Tech", "Thanks!") });
        Check("a message in the open conversation is added to it", log.Text.Contains("Thanks!"));

        Console.WriteLine();
        Console.WriteLine("== the @ ==");
        chat.Hide(); Settle();
        Deliver(new StaffChatMsg { Message = Msg("Bob Tech", "", "@Sue Tech the Dell is ready"), MentionsMe = true });
        Check("an Everyone message that names them pops it up, on Everyone", chat.Visible && F<string>(chat, "_with") == "");
        Check("...titled Everyone", F<Label>(chat, "_title").Text == "Everyone");

        Console.WriteLine();
        Console.WriteLine("== a refusal ==");
        Deliver(new StaffChatMsg { Error = "Nobody called \"Nobody\" works here." });
        var err = F<Label>(chat, "_error");
        Check("a refused send says why, under the conversation", err.Text.Contains("Nobody") && err.Height > 0);
        Deliver(new StaffChatMsg { Message = Msg("Sue Tech", "", "Found it, thanks") });
        Check("...and the next message they send clears it", err.Text.Length == 0 && err.Height == 0);

        Console.WriteLine();
        Console.WriteLine("== fit (§30) ==");
        var send = FindButton(chat, "Send");
        Check("Send is there, and its text fits", send != null && TextRenderer.MeasureText("Send", send.Font).Width < send.Width, $"{send?.Width}");
        Check("nothing runs off the right-hand side", AllControls(chat).Where(c => c.Visible && c.Parent != null).All(c => c.Right <= c.Parent!.ClientSize.Width + 1),
            string.Join(", ", AllControls(chat).Where(c => c.Visible && c.Parent != null && c.Right > c.Parent.ClientSize.Width + 1).Select(c => c.GetType().Name)));
        chat.Size = chat.MinimumSize; Settle();
        Check("at its smallest the input row still holds the box and Send", send != null && F<InputBox>(chat, "_input").Width > 100, $"{F<InputBox>(chat, "_input").Width}");
        Shot(chat, outDir, "staffchat-smallest");

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static IEnumerable<Control> AllControls(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in AllControls(x)) yield return y; } }
    private static RoundButton? FindButton(Control c, string text) => AllControls(c).OfType<RoundButton>().FirstOrDefault(b => b.Text == text);
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
