using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using RemoteSupport.Client;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// The unattended access switch, driven end to end against a stand-in server.
//
// Added 2026-09-20. Unattended access used to be decided once, at install time, by whoever held
// the mouse; now the person at the machine turns it on and off, and the server stores and
// enforces the answer. This drives the REAL tray window against a real WebSocket server that
// speaks the real protocol, and checks both directions of that conversation.
//
// WHY THIS IS WORTH A HARNESS. It is the one setting in the product that decides who can get
// into somebody's computer, and every one of its failure modes is silent:
//
//   * a tick that is never sent looks exactly like one that was;
//   * a box that does not follow the SERVER will eventually show the opposite of the truth, and
//     the customer will believe the box;
//   * "turning it on asks first" is one `if` away from not being true any more, and nobody would
//     notice until a customer said they had never agreed to anything.
//
// The confirmation is a modal dialog, which a test process cannot dismiss — it blocks the very
// message loop the test is pumping. MainForm.ConfirmUnattended exists for that and only that,
// and this is the only thing that sets it.
//
// No platform server, no customer machine, no database.
//
//   dotnet run -c Release --project tools/unattendedcheck/unattendedcheck.csproj [-- <out-dir>]
// ---------------------------------------------------------------------------------------------

internal static class UnattendedCheck
{
    private static int _pass, _fail;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static readonly List<Msg> FromClient = new();
    private static WsConnection? _server;

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "unattended");
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        int port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                    var wsc = await ctx.AcceptWebSocketAsync(null);
                    var conn = new WsConnection(wsc.WebSocket);
                    _server = conn;
                    conn.OnMessage += m => { lock (FromClient) FromClient.Add(m); };
                    _ = conn.RunAsync(CancellationToken.None);
                }
                catch { break; }
            }
        });

        var brand = Branding.Load();
        brand.ServerUrl = $"http://127.0.0.1:{port}";
        brand.CompanyName = string.IsNullOrWhiteSpace(brand.CompanyName) ? "Anywhere IT" : brand.CompanyName;

        var form = new MainForm(brand) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        // Stand in for the confirmation dialog. Starts as "the customer says yes".
        bool confirmAnswer = true, confirmAsked = false;
        SetField(form, "ConfirmUnattended", (Func<bool>)(() => { confirmAsked = true; return confirmAnswer; }));
        form.Show();
        Settle();

        var box = Field<CheckBox>(form, "_unattendedBox");
        var note = Field<Label>(form, "_unattendedNote");
        var item = Field<ToolStripMenuItem>(form, "_unattendedItem");

        Console.WriteLine();
        Console.WriteLine("== before the server has said anything ==");
        Check("there is an unattended switch", box != null, box?.Text ?? "no checkbox found");
        Check("it is on the TRAY MENU", item != null, item?.Text ?? "no menu item found");
        // Clint, 2026-09-20: "there should only ever be the unattended check box in the tray,
        // not as part of the request." The window is what somebody opens when they want help
        // now; a permission set once and left for months does not belong on it. The checkbox
        // object still exists unparented, because it is where the confirm/send/put-it-back
        // logic lives and the menu item drives it — one copy of that decision, not two.
        Check("and NOT on the request window", box is { Parent: null },
              box?.Parent == null ? "the window is for asking for help, not for settings"
                                  : "it is parented to " + box.Parent.GetType().Name);
        Check("it starts OFF", box is { Checked: false }, "installing a support client is not agreeing to be watched");
        Check("and cannot be used before the server has answered", box is { Enabled: false },
              "a tick that cannot be sent anywhere is worse than no tick at all");
        // A greyed menu item shows no tooltip, so while it is unusable the reason has to be in
        // the item's own text. Clint met the version without this on 2026-09-20: a grey line
        // and no way to tell which of three reasons it was.
        Check("and a greyed item says WHY, in its own text", item != null && item.Text.Contains("—", StringComparison.Ordinal),
              item?.Text ?? "");

        if (!WaitFor(() => _server != null, 5000)) { Console.WriteLine("  the client never connected — giving up"); return 1; }
        Settle();
        Check("the tray app connected to the server", _server != null);
        Check("and said hello as a CLIENT", Sent<HelloMsg>() is { Role: DeviceRole.Client },
              "the agent is the thing being permitted; it must not be the thing permitting");

        Console.WriteLine();
        Console.WriteLine("== the server says: agent present, not allowed ==");
        _server!.Send(new UnattendedStateMsg { On = false, AgentOnline = true });
        Settle();
        Check("the switch becomes usable", box is { Enabled: true });
        Check("and the menu item goes back to plain wording", item != null && !item.Text.Contains("—", StringComparison.Ordinal),
              item?.Text ?? "");
        Check("it is off", box is { Checked: false });
        Check("and says what that means, in words a customer can act on",
              note != null && note.Text.Contains("only connect when you ask", StringComparison.OrdinalIgnoreCase),
              note?.Text ?? "");
        Shoot(form, outDir, "unattended-off");

        Console.WriteLine();
        Console.WriteLine("== turning it on asks first ==");
        confirmAnswer = false; confirmAsked = false;
        ClearSent();
        box!.Checked = true;
        Settle();
        Check("ticking it asks the person to confirm", confirmAsked,
              "this is the setting that lets somebody into their computer while they are not there");
        Check("saying no leaves it off", !box.Checked, "and the tick goes back where it was");
        Check("and nothing is sent to the server", Sent<SetUnattendedMsg>() == null,
              "a refused confirmation must not change anything anywhere");

        Console.WriteLine();
        Console.WriteLine("== saying yes sends it, and the SERVER decides ==");
        confirmAnswer = true; confirmAsked = false;
        ClearSent();
        box.Checked = true;
        Settle();
        var sent = Sent<SetUnattendedMsg>();
        Check("confirming sends the request", sent is { On: true }, sent == null ? "nothing arrived" : "on = " + sent.On);
        // Until the server answers, the box is only showing what was asked for. The answer is
        // what makes it true — and the answer is allowed to disagree.
        _server.Send(new UnattendedStateMsg { On = true, AgentOnline = true });
        Settle();
        Check("the server's answer is what the box ends up showing", box.Checked);
        Check("the tray item agrees with the window", item is { Checked: true },
              "two copies of one fact drift; these are set from one place");
        Check("and the explanation names the company that can now get in",
              note != null && note.Text.Contains(brand.CompanyName, StringComparison.OrdinalIgnoreCase)
                           && note.Text.Contains("nobody is at this computer", StringComparison.OrdinalIgnoreCase),
              note?.Text ?? "");
        // The words have to survive the move to the menu. A ticked "Allow unattended access"
        // does not, on its own, tell anybody what they agreed to, so the sentence that used to
        // sit under the checkbox is the menu item's tooltip now — and it has to be the CURRENT
        // sentence, which the first version of this got wrong by setting it before updating it.
        Check("the menu item carries the explanation, and it is up to date",
              item != null && item.ToolTipText != null
              && item.ToolTipText.Contains(brand.CompanyName, StringComparison.OrdinalIgnoreCase),
              item?.ToolTipText ?? "no tooltip");

        Shoot(form, outDir, "unattended-on");

        Console.WriteLine();
        Console.WriteLine("== and it is written into the machine's own record ==");
        var log = Field<RichTextBox>(form, "_chatLog");
        Check("switching it on is logged where the customer can read it",
              log != null && log.Text.Contains("Unattended access is ON", StringComparison.Ordinal),
              "a permission that changes silently is one nobody can account for afterwards");

        Console.WriteLine();
        Console.WriteLine("== turning it off asks nothing ==");
        confirmAsked = false;
        ClearSent();
        box.Checked = false;
        Settle();
        Check("unticking does not ask for confirmation", !confirmAsked,
              "withdrawing permission is always allowed to be easy");
        var off = Sent<SetUnattendedMsg>();
        Check("and the request goes out", off is { On: false }, off == null ? "nothing arrived" : "on = " + off.On);
        _server.Send(new UnattendedStateMsg { On = false, AgentOnline = true });
        Settle();
        Check("the server confirms and the box follows", !box.Checked);
        Check("and that is logged too",
              log != null && log.Text.Contains("Unattended access is off", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine("== the server changing its mind on its own ==");
        // Somebody else, or a later version of the console, turning it off while this machine is
        // running. The box must follow the server rather than its own last click.
        _server.Send(new UnattendedStateMsg { On = true, AgentOnline = true });
        Settle();
        Check("a change the customer did not make still reaches the box", box.Checked,
              "the box shows what the server holds, never what it last clicked");
        Check("without asking them to confirm somebody else's decision", !confirmAsked);

        Console.WriteLine();
        Console.WriteLine("== a machine with no agent service ==");
        _server.Send(new UnattendedStateMsg { On = false, AgentOnline = false });
        Settle();
        bool installedHere = AgentServiceInstalledHere();
        if (installedHere)
            Console.WriteLine("        (this machine HAS the agent service installed, so the 'no service'"
                            + " wording cannot be exercised here — it is checked on a machine without it)");
        else
        {
            Check("the switch is disabled when there is nothing to switch on", box is { Enabled: false });
            Check("and says so, rather than leaving a dead tick box with no explanation",
                  note != null && note.Text.Contains("without the background service", StringComparison.OrdinalIgnoreCase),
                  note?.Text ?? "");
            Shoot(form, outDir, "unattended-no-service");
        }

        Console.WriteLine();
        Console.WriteLine("== losing the server ==");
        _server.Send(new UnattendedStateMsg { On = true, AgentOnline = true });
        Settle();
        try { listener.Stop(); } catch { }
        try { _server.CloseAsync("probe done").GetAwaiter().GetResult(); } catch { }
        if (!WaitFor(() => box.Enabled == false, 8000)) { /* reported by the check below */ }
        Settle();
        Check("the switch is disabled while there is no server to tell", !box.Enabled,
              "changing it would otherwise look like it had worked");

        form.Close();
        Settle();

        // ---------------------------------------------------------------------------------------
        // The service is installed and DEAD — the state Clint reported on 2026-09-21.
        //
        // This one cannot be driven through the window at all, and that is the point. Reaching it
        // needs a machine where the agent service is installed but not connected, and no machine
        // here has the service installed: the section above literally skips itself on a machine
        // that does. So the most important state the switch can be in was the one state no test
        // could visit, which is §30's lesson ("two of them invisible on this machine") arriving a
        // third time.
        //
        // UnattendedWording is the same decision the form makes, asked as a function.
        Console.WriteLine();
        Console.WriteLine("== the service is installed and NOT connected ==");
        const string co = "Anywhere IT";

        var dead = UnattendedWording.For(serviceInstalled: true, agentOnline: false, serverKnows: true,
                                         connected: true, on: true, companyName: co);
        Check("the switch still works — permission is theirs to withdraw whatever the service is doing",
              dead.Usable);
        Check("it does NOT claim technicians can now connect",
              !dead.Note.Contains("can connect when nobody is at this computer", StringComparison.OrdinalIgnoreCase),
              dead.Note);
        Check("it says the background service is not running",
              dead.Note.Contains("background service", StringComparison.OrdinalIgnoreCase)
              && dead.Note.Contains("not running", StringComparison.OrdinalIgnoreCase), dead.Note);
        Check("and warns that they will still be asked first",
              dead.Note.Contains("still have to ask you first", StringComparison.OrdinalIgnoreCase),
              "the customer was told they would not be — that is the surprise being removed");
        Check("the menu item says it too, without needing a hover",
              dead.MenuText.Contains("background service is not running", StringComparison.OrdinalIgnoreCase),
              dead.MenuText);

        var working = UnattendedWording.For(serviceInstalled: true, agentOnline: true, serverKnows: true,
                                            connected: true, on: true, companyName: co);
        Check("a machine whose agent IS connected still gets the plain promise",
              working.Note.Contains($"Technicians at {co} can connect when nobody is at this computer"),
              working.Note);
        Check("and its menu item carries no warning", working.MenuText == "Allow unattended access", working.MenuText);

        // Off, with a dead service. Nothing is being promised, so nothing is being broken — but a
        // customer about to tick the box should not have to tick it to find out it will not work.
        var offDead = UnattendedWording.For(serviceInstalled: true, agentOnline: false, serverKnows: true,
                                            connected: true, on: false, companyName: co);
        Check("with the box OFF and a dead service, ticking it is not offered as if it would work",
              offDead.Note.Contains("not running", StringComparison.OrdinalIgnoreCase), offDead.Note);

        // The three states that were already right must stay right — this is the fourth being
        // added, not the others being rewritten.
        var noService = UnattendedWording.For(serviceInstalled: false, agentOnline: false, serverKnows: true,
                                              connected: true, on: false, companyName: co);
        Check("no service at all still disables the switch and says why",
              !noService.Usable && noService.Note.Contains("without the background service", StringComparison.OrdinalIgnoreCase),
              noService.Note);
        var noServer = UnattendedWording.For(serviceInstalled: true, agentOnline: false, serverKnows: false,
                                             connected: false, on: false, companyName: co);
        Check("no server still disables the switch and says why",
              !noServer.Usable && noServer.Note.Contains("Waiting for the support server", StringComparison.OrdinalIgnoreCase),
              noServer.Note);

        Console.WriteLine();
        Console.WriteLine("== what this harness does NOT cover ==");
        Console.WriteLine("   The HUB's half, which is where the permission is actually enforced. Three");
        Console.WriteLine("   things to check against a real server, and the first two are the security:");
        Console.WriteLine("     - a technician connecting to a machine with unattended access OFF is");
        Console.WriteLine("       refused, and told to ask the person at it to tick the box;");
        Console.WriteLine("     - the same machine still answers a call the customer makes themselves;");
        Console.WriteLine("     - the device list flips between Attended and Unattended, live, when the");
        Console.WriteLine("       box is ticked — no refresh, no reconnect.");

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        Console.WriteLine($"screenshots in {outDir}");
        return _fail == 0 ? 0 : 1;
    }

    private static string Name(Control c) =>
        string.IsNullOrWhiteSpace(c.Text) ? c.GetType().Name : (c.Text.Length > 30 ? c.Text.Substring(0, 30) : c.Text);

    private static bool AgentServiceInstalledHere()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\RemoteSupportAgent");
            return k != null;
        }
        catch { return false; }
    }

    private static T? Sent<T>() where T : Msg
    {
        lock (FromClient) return FromClient.OfType<T>().LastOrDefault();
    }

    private static void ClearSent() { lock (FromClient) FromClient.Clear(); }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            Application.DoEvents(); Thread.Sleep(25);
        }
        return cond();
    }

    private static void Settle() { for (int i = 0; i < 24; i++) { Application.DoEvents(); Thread.Sleep(25); } }

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

    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    private static void SetField(object o, string name, object value) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(o, value);
}
