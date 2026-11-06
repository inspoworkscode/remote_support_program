using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// dashtabs - proves stage 3.6.2: the console's home screen is two tabs of machines listed under
// the customer who owns them.
//
// Two halves, deliberately.
//
//   The RULES are asked of DeviceList directly - which tab a machine lands on, how machines
//   gather under a customer, what order any of it comes out in. That logic lives in its own
//   class precisely so a harness can ask it plainly instead of reaching into a Form.
//
//   The RENDERING is asked of the REAL DashboardForm, opened the way Program.cs opens it,
//   pointed at a port nothing is listening on. Because the rule being right and the list
//   showing it are two different claims: the first version of this passed every rule check
//   while the ListView showed one flat list, since ShowGroups had not been set.
//
// No server, no session, no customer machine.
//
//   dotnet run -c Release --project tools/dashtabs/dashtabs.csproj
// ---------------------------------------------------------------------------------------------

internal static class DashTabs
{
    private static int _pass, _fail;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    /// <summary>
    /// A machine, as the hub describes one.
    ///
    /// `agent` and `trayApp` default to `online` because most machines have both, and every
    /// fixture written before 2026-09-21 assumed it. They are separable now because the fault
    /// Clint reported lives exactly in the gap: a machine whose tray app is connected and whose
    /// AGENT is not is online, is on the Unattended tab, and cannot do unattended access.
    /// </summary>
    private static DeviceInfo Dev(string machine, string client = "", int clientId = 0, string contact = "",
        string user = "", bool online = false, bool unattended = false, bool? agent = null, bool? trayApp = null) => new()
        {
            DeviceId = "dev-" + machine.ToLowerInvariant(),
            MachineName = machine,
            ClientId = clientId,
            ClientName = client,
            ContactId = contact.Length > 0 ? 1 : 0,
            ContactName = contact,
            UserName = user,
            Online = online,
            AgentOnline = agent ?? online,
            ClientOnline = trayApp ?? online,
            UnattendedEnabled = unattended,
            Unattended = unattended && (agent ?? online)
        };

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Zenith has three machines, which is the case grouping exists for. Acme has one. Two
        // machines belong to nobody. One of Zenith's is unattended, so the same customer appears
        // on both tabs - which is real, and is the case a naive "group by tab" would get wrong.
        var devices = new List<DeviceInfo>
        {
            Dev("ZEN-SUE",   "Zenith Motors", 1, contact: "Sue Jones", user: "sue",        online: true),
            Dev("ZEN-BOB",   "Zenith Motors", 1, contact: "Bob Smith", user: "bob"),
            Dev("ZEN-SRV",   "Zenith Motors", 1,                       user: "Administrator", online: true, unattended: true),
            Dev("ACME-1",    "Acme Holdings", 2, contact: "Dave",      user: "dave",       online: true),
            Dev("STRAY-1",   user: "frontdesk", online: true),
            Dev("STRAY-2",   user: "workshop",  unattended: true),
        };

        Console.WriteLine("dashtabs - stage 3.6.2, the console's two device tabs");
        Console.WriteLine();
        Console.WriteLine("== the rules ==");

        // ---------------------------------------------------------------- which tab
        var attended = DeviceList.Build(devices, DeviceTab.Attended);
        var unattended = DeviceList.Build(devices, DeviceTab.Unattended);

        var attendedNames = attended.SelectMany(g => g.Machines).Select(m => m.MachineName).ToList();
        var unattendedNames = unattended.SelectMany(g => g.Machines).Select(m => m.MachineName).ToList();

        Check("a machine without the switch never appears on Unattended",
            !unattendedNames.Contains("ZEN-SUE") && !unattendedNames.Contains("ZEN-BOB"),
            string.Join(", ", unattendedNames));
        Check("a machine without the switch appears on Attended",
            attendedNames.Contains("ZEN-SUE") && attendedNames.Contains("ZEN-BOB"));
        Check("a machine with the switch appears on Unattended",
            unattendedNames.Contains("ZEN-SRV") && unattendedNames.Contains("STRAY-2"));
        Check("a machine with the switch never appears on Attended",
            !attendedNames.Contains("ZEN-SRV") && !attendedNames.Contains("STRAY-2"),
            string.Join(", ", attendedNames));
        Check("every machine is on exactly one tab",
            attendedNames.Count + unattendedNames.Count == devices.Count
            && !attendedNames.Intersect(unattendedNames).Any(),
            $"{attendedNames.Count} + {unattendedNames.Count} of {devices.Count}");

        // The tab is the customer's PERMISSION, not whether their agent happens to be up. An
        // offline machine whose owner allowed access is still an unattended machine; a list that
        // moved rows between tabs as agents came and went would not hold still long enough to
        // click on.
        var offlineButAllowed = Dev("OFFLINE-OK", user: "nobody", online: false, unattended: true);
        Check("an OFFLINE machine with permission is still Unattended",
            DeviceList.Belongs(offlineButAllowed, DeviceTab.Unattended));
        var onlineNoPermission = Dev("ONLINE-NO", user: "somebody", online: true);
        Check("an ONLINE machine without permission is still Attended",
            DeviceList.Belongs(onlineNoPermission, DeviceTab.Attended));

        Check("the tab counts match what the tabs hold",
            DeviceList.CountOn(devices, DeviceTab.Attended) == attendedNames.Count
            && DeviceList.CountOn(devices, DeviceTab.Unattended) == unattendedNames.Count);

        // ------------------------------------------- the machine that says Unattended and is not
        //
        // Clint, 2026-09-21: the switch "switches between attended and unattended on the console,
        // but does not allow for unattended access. the client still has to click on allow."
        //
        // The tab is right and stays right — the permission IS granted. What was missing is that
        // nothing anywhere could tell this machine from one that works. Online is the agent OR the
        // tray app, so the dot is green either way; the tab heading is the permission; and the
        // session starts normally, attended, with the customer asked to accept.
        var deadAgent = Dev("DEAD-AGENT", user: "sue", online: true, unattended: true, agent: false, trayApp: true);
        Check("a machine with permission but no agent is STILL on the Unattended tab",
            DeviceList.Belongs(deadAgent, DeviceTab.Unattended));
        Check("...and is flagged as unable to do it",
            DeviceList.ClaimsUnattendedButCannot(deadAgent));
        Check("...and its Online flag would have said nothing was wrong",
            deadAgent.Online, "which is why the dot could not carry this");

        var healthy = Dev("HEALTHY", user: "bob", online: true, unattended: true);
        Check("a machine with permission AND an agent is not flagged",
            !DeviceList.ClaimsUnattendedButCannot(healthy));

        // A machine that is simply switched off is not claiming anything it cannot do — the red
        // dot already says so, and flagging it would make the warning mean two different things.
        var switchedOff = Dev("OFF-ALLOWED", user: "nobody", online: false, unattended: true);
        Check("a switched-off machine with permission is NOT flagged",
            !DeviceList.ClaimsUnattendedButCannot(switchedOff), "the red dot already says it");

        // Permission off is the customer's choice, not a fault, however its agent is doing.
        var noPermission = Dev("NO-PERMISSION", user: "dave", online: true, agent: false, trayApp: true);
        Check("a machine without permission is never flagged",
            !DeviceList.ClaimsUnattendedButCannot(noPermission));

        Check("the count of not-ready machines is the count of flagged ones",
            DeviceList.CountNotReady(new[] { deadAgent, healthy, switchedOff, noPermission }) == 1,
            $"{DeviceList.CountNotReady(new[] { deadAgent, healthy, switchedOff, noPermission })} of 4");

        // ---------------------------------------------------------------- grouping
        Check("one client with three machines is one group",
            attended.Count(g => g.Name == "Zenith Motors") == 1);
        Check("that group holds the two attended ones",
            attended.First(g => g.Name == "Zenith Motors").Machines.Count == 2,
            $"{attended.First(g => g.Name == "Zenith Motors").Machines.Count}");
        Check("the same client appears on the other tab for its unattended machine",
            unattended.Count(g => g.Name == "Zenith Motors") == 1
            && unattended.First(g => g.Name == "Zenith Motors").Machines.Count == 1);
        Check("machines nobody has linked gather under one heading",
            attended.Count(g => g.Name == DeviceList.Unlinked) == 1);
        Check("the strays come last, below real customers",
            attended.Last().IsUnlinked, attended.Last().Name);
        Check("customers are in alphabetical order",
            attended.Where(g => !g.IsUnlinked).Select(g => g.Name).SequenceEqual(new[] { "Acme Holdings", "Zenith Motors" }),
            string.Join(" | ", attended.Where(g => !g.IsUnlinked).Select(g => g.Name)));
        Check("online machines sort above offline ones within a customer",
            attended.First(g => g.Name == "Zenith Motors").Machines[0].MachineName == "ZEN-SUE");
        Check("a group counts how many of its machines are online",
            attended.First(g => g.Name == "Zenith Motors").OnlineCount == 1);

        // A client whose name never arrived must not silently merge with the strays - that would
        // file one customer's machines under "nobody owns these".
        var nameless = new List<DeviceInfo> { Dev("NONAME", client: "", clientId: 7, user: "x") };
        var namelessGroups = DeviceList.Build(nameless, DeviceTab.Attended);
        Check("a linked client with no name is still its own group, not a stray",
            namelessGroups.Count == 1 && !namelessGroups[0].IsUnlinked && namelessGroups[0].Name == "Client #7",
            namelessGroups[0].Name);

        // ---------------------------------------------------------------- the first column
        Check("the first column shows the contact when there is one",
            devices.First(d => d.MachineName == "ZEN-SUE").OwnerName == "Sue Jones");
        Check("the first column falls back to the client",
            devices.First(d => d.MachineName == "ZEN-SRV").OwnerName == "Zenith Motors");
        Check("the first column falls back to whoever is signed in",
            devices.First(d => d.MachineName == "STRAY-1").OwnerName == "frontdesk");
        Check("the first column is never empty",
            devices.All(d => !string.IsNullOrWhiteSpace(d.OwnerName)));

        // ---------------------------------------------------------------- the real window
        Console.WriteLine();
        Console.WriteLine("== the window ==");

        var brand = Branding.Load();
        // A localhost port nothing is listening on. The link's retry loop fails in the background,
        // which is not what this measures — the window is built and laid out regardless.
        var link = new TechLink("ws://127.0.0.1:59999");
        using var form = new DashboardForm(brand, link);
        form.StartPosition = FormStartPosition.Manual;
        form.WindowState = FormWindowState.Normal;
        form.Location = new Point(0, 0);
        form.Size = new Size(1600, 900);
        form.Show();
        for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }

        var list = Field<ListView>(form, "_devices");
        if (list == null) { Check("found the device list", false); return Done(); }

        Check("the list has no grid lines", !list.GridLines);
        Check("the list shows groups", list.ShowGroups);
        Check("the Status column is gone", !HeaderNames(list).Contains("Status"), string.Join(" | ", HeaderNames(list)));
        Check("the Access column is gone", !HeaderNames(list).Contains("Access"));
        Check("the first column is the indicator and carries no heading",
            list.Columns.Count > 0 && list.Columns[0].Text == "");
        Check("the owner is the first column with words in it",
            list.Columns.Count > 1 && list.Columns[1].Text == "Who", list.Columns.Count > 1 ? list.Columns[1].Text : "-");
        Check("the machine name is second",
            list.Columns.Count > 2 && list.Columns[2].Text == "Machine", list.Columns.Count > 2 ? list.Columns[2].Text : "-");

        // Feed the real form the real devices and look at what it actually built.
        Seed(form, devices);

        Check("the window starts on Attended", Field<ListView>(form, "_devices") != null && list.Groups.Count > 0);
        Check("the list built one group per customer on this tab",
            list.Groups.Count == attended.Count, $"{list.Groups.Count} groups against {attended.Count}");
        Check("the list built a row per machine on this tab",
            list.Items.Count == attendedNames.Count, $"{list.Items.Count} rows against {attendedNames.Count}");
        Check("Zenith's block expands to its two attended machines",
            list.Groups.Cast<ListViewGroup>().FirstOrDefault(g => (g.Tag as string) == "Zenith Motors")?.Items.Count == 2);
        Check("every row is inside a group",
            list.Items.Cast<ListViewItem>().All(i => i.Group != null));
        Check("a row's first cell is blank, because the dot is painted there",
            list.Items.Cast<ListViewItem>().All(i => i.SubItems[0].Text == ""));
        Check("a row's second cell is the owner's name",
            list.Items.Cast<ListViewItem>().Any(i => i.SubItems[1].Text == "Sue Jones"));

        var tabAttended = Field<RoundButton>(form, "_tabAttended");
        var tabUnattended = Field<RoundButton>(form, "_tabUnattended");
        Check("both tabs exist", tabAttended != null && tabUnattended != null);
        Check("the tabs carry their counts",
            tabAttended!.Text.Contains(attendedNames.Count.ToString())
            && tabUnattended!.Text.Contains(unattendedNames.Count.ToString()),
            $"\"{tabAttended.Text}\" / \"{tabUnattended!.Text}\"");
        Check("the showing tab is the accented one",
            tabAttended.Style == BtnStyle.Accent && tabUnattended.Style != BtnStyle.Accent);
        Check("Attended is to the left of Unattended",
            tabAttended.Left < tabUnattended.Left, $"{tabAttended.Left} < {tabUnattended.Left}");

        // §30: no control height is a constant. One line of the button's own font plus padding —
        // on a 150% display this is about 38 px, and a hard-coded 34 would have clipped it.
        int line = TextRenderer.MeasureText("Ag", tabAttended.Font).Height;
        Check("a tab is tall enough for its own font",
            tabAttended.Height >= line, $"{tabAttended.Height} px against a {line} px line of {tabAttended.Font.Name} {tabAttended.Font.SizeInPoints}pt");
        Check("the dot column is wider than the dot it holds",
            list.Columns[0].Width > line / 2, $"{list.Columns[0].Width} px");

        // Switch tabs on the real window and check the list actually changed.
        tabUnattended.PerformClick();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }

        Check("clicking Unattended swaps the list",
            list.Items.Count == unattendedNames.Count, $"{list.Items.Count} rows against {unattendedNames.Count}");
        Check("the unattended list holds only permitted machines",
            list.Items.Cast<ListViewItem>().All(i => i.Tag is DeviceInfo d && d.UnattendedEnabled));
        Check("the accent moved to the tab now showing",
            tabUnattended.Style == BtnStyle.Accent && tabAttended.Style != BtnStyle.Accent);
        Check("Zenith still appears, for its one unattended machine",
            list.Groups.Cast<ListViewGroup>().Any(g => (g.Tag as string) == "Zenith Motors"));

        Shot(form, "unattended");

        tabAttended.PerformClick();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
        Check("clicking back restores the attended list", list.Items.Count == attendedNames.Count);
        Shot(form, "attended");

        // A fold must survive a device update. This is the one that would be found by a customer
        // rather than by a developer: a machine goes offline, the list refreshes, and the block
        // somebody folded away springs open again.
        var collapsed = Invoke(form, "_collapsed") as HashSet<string>;
        collapsed?.Add("Zenith Motors");
        Seed(form, devices);
        var zen = list.Groups.Cast<ListViewGroup>().FirstOrDefault(g => (g.Tag as string) == "Zenith Motors");
        Check("a folded customer stays folded across a refresh",
            zen?.CollapsedState == ListViewGroupCollapsedState.Collapsed, zen?.CollapsedState.ToString() ?? "no group");

        // ------------------------------------- the window, with a machine that cannot deliver
        //
        // Seeded last and on its own, so the counts every check above asserts are left alone.
        Console.WriteLine();
        Console.WriteLine("== a machine that says Unattended and is not ==");

        int widthBefore = tabUnattended.Width;
        Seed(form, new List<DeviceInfo>
        {
            Dev("GOOD-SRV", "Zenith Motors", 1, user: "Administrator", online: true, unattended: true),
            deadAgent,
        });
        tabUnattended.PerformClick();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }

        Check("the Unattended tab says how many are not ready",
            tabUnattended.Text.Contains("1 not ready"), $"\"{tabUnattended.Text}\"");

        // §30 sideways: the warning is useless if the button clips it off. The tab must be wide
        // enough for its OWN text in its OWN font — a 150 px constant is not.
        int need = TextRenderer.MeasureText(tabUnattended.Text, tabUnattended.Font).Width;
        Check("the tab grew wide enough to show it",
            tabUnattended.Width >= need, $"{tabUnattended.Width} px for {need} px of \"{tabUnattended.Text}\"");
        Check("...and it grew rather than being born that wide",
            tabUnattended.Width > widthBefore, $"{widthBefore} -> {tabUnattended.Width} px");

        var deadRow = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[2].Text == "DEAD-AGENT");
        var goodRow = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[2].Text == "GOOD-SRV");
        Check("both machines are still listed under Unattended", deadRow != null && goodRow != null);
        Check("the list shows row tooltips at all", list.ShowItemToolTips);
        // The words matter more than the colour. A technician who sees only an amber dot goes and
        // checks the network; one who reads this asks the customer to restart the machine.
        Check("the machine that cannot deliver says so in words",
            (deadRow?.ToolTipText ?? "").Contains("background service", StringComparison.OrdinalIgnoreCase)
            && (deadRow?.ToolTipText ?? "").Contains("ask", StringComparison.OrdinalIgnoreCase),
            deadRow?.ToolTipText?.Replace("\n", " ") ?? "(none)");
        Check("the machine that can deliver carries no warning",
            string.IsNullOrEmpty(goodRow?.ToolTipText), goodRow?.ToolTipText ?? "(none)");

        Shot(form, "not-ready");

        // And when every machine is healthy the tab goes quiet again — a warning that never
        // clears is one people stop reading.
        Seed(form, new List<DeviceInfo> { Dev("GOOD-SRV", "Zenith Motors", 1, user: "Administrator", online: true, unattended: true) });
        Check("the warning clears when nothing is wrong",
            !tabUnattended.Text.Contains("not ready"), $"\"{tabUnattended.Text}\"");

        // ------------------------------------------------ the monthly health check, 2026-09-28
        //
        // On the same right-click as Link, where Clint asked for it. The real menu is opened on a
        // real row, because its words are filled in as it OPENS — from the machine under the
        // pointer — and a menu checked only as it was built would be checked blank.
        Console.WriteLine();
        Console.WriteLine("== the right-click: Monthly health check ==");
        tabAttended.PerformClick();
        var sla = Dev("SLA-PC", "Zenith Motors", 1, user: "Sue Jones", online: true);
        sla.ClientHealthCheck = true; sla.HealthCheckAvailable = true; sla.HealthCheckWanted = true;
        sla.HealthCheckMode = HealthCheckModes.AsClient; sla.AgentOnline = true;
        var loose = Dev("LOOSE-PC", "", 0, user: "Pat", online: true);
        loose.HealthCheckAvailable = true; loose.HealthCheckMode = HealthCheckModes.Off;
        Seed(form, new List<DeviceInfo> { sla, loose });

        (string now, string asClient, bool[] ticks)? OpenFor(string machine)
        {
            var row = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[2].Text == machine);
            var menu = list.ContextMenuStrip;
            if (row == null || menu == null) return null;
            list.SelectedItems.Clear(); row.Selected = true;
            menu.Show(list, new Point(10, 10));
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
            var hc = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text == "Monthly health check");
            menu.Close();
            if (hc == null) return null;
            var items = hc.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            return (items[0].Text, items[1].Text, items.Skip(1).Select(x => x.Checked).ToArray());
        }

        var slaMenu = OpenFor("SLA-PC");
        Check("the machine menu has Monthly health check", slaMenu != null);
        if (slaMenu is { } s1)
        {
            Check("its top line says where the machine stands, not just the choice",
                s1.now == "On - waiting for the machine to install it", $"\"{s1.now}\"");
            Check("'as the customer' names the customer and what that comes to",
                s1.asClient == "As Zenith Motors (on)", $"\"{s1.asClient}\"");
            Check("the current choice is the one ticked, and only it",
                s1.ticks.SequenceEqual(new[] { true, false, false }), string.Join(",", s1.ticks));
        }
        var looseMenu = OpenFor("LOOSE-PC");
        if (looseMenu is { } s2)
        {
            Check("an unlinked machine says it has nobody to follow",
                s2.asClient == "As its customer (none linked, so off)", $"\"{s2.asClient}\"");
            Check("and its own choice, Off, is the one ticked",
                s2.ticks.SequenceEqual(new[] { false, false, true }), string.Join(",", s2.ticks));
        }
        else Check("the menu opened on the unlinked machine", false);

        // Its words move when the machine reports: the same menu, re-opened after an update.
        sla.HealthCheckInstalled = true; sla.HealthCheckNote = "installed";
        Seed(form, new List<DeviceInfo> { sla, loose });
        var after = OpenFor("SLA-PC");
        Check("after the machine reports, it says installed",
            after?.now == "On - installed on the machine", $"\"{after?.now}\"");

        // ------------------------------------------------ the right-click: Chat, 2026-10-05
        //
        // Clint: chat with a machine from its right-click rather than the dropdown every time. It
        // must point the ONE chat card at the machine under the pointer — not whichever the
        // dropdown already showed — and refuse an offline machine the way Connect does.
        Console.WriteLine();
        Console.WriteLine("== the right-click: Chat ==");
        var offline = Dev("OFF-PC", "Zenith Motors", 1, user: "Max", online: false);
        Seed(form, new List<DeviceInfo> { sla, loose, offline });
        var chatTarget = Field<ComboBox>(form, "_chatTarget");
        ToolStripMenuItem? ChatItemFor(string machine)
        {
            var row = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[2].Text == machine);
            var menu = list.ContextMenuStrip;
            if (row == null || menu == null) return null;
            list.SelectedItems.Clear(); row.Selected = true;
            menu.Show(list, new Point(10, 10));
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
            var item = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text.StartsWith("Chat"));
            return item;
        }
        string Target() => chatTarget?.SelectedItem?.ToString() ?? "(none)";
        // The dropdown starts on the first online machine alphabetically (LOOSE-PC); right-clicking
        // the OTHER one is the test that means something.
        var startedOn = Target();
        var chatSla = ChatItemFor("SLA-PC");
        Check("the machine menu has Chat, enabled for an online machine",
            chatSla is { Enabled: true, Text: "Chat" }, $"\"{chatSla?.Text}\" enabled={chatSla?.Enabled}");
        chatSla?.PerformClick();
        list.ContextMenuStrip?.Close();
        Application.DoEvents();
        Check("Chat points the chat card at the machine right-clicked",
            Target().StartsWith("SLA-PC"), $"was \"{startedOn}\", now \"{Target()}\"");
        Check("and the cursor is in the message box, ready to type",
            Field<InputBox>(form, "_chatInput")?.Box.Focused == true);
        var chatOff = ChatItemFor("OFF-PC");
        list.ContextMenuStrip?.Close();
        Check("an offline machine's Chat is greyed and says why",
            chatOff is { Enabled: false, Text: "Chat  (offline)" }, $"\"{chatOff?.Text}\" enabled={chatOff?.Enabled}");
        // A clicked menu item hands activation around; let it settle and take it back, or the next
        // window this harness shows opens BEHIND whatever else is on the desktop and the screen-read
        // checks below read that window instead (found the first time this ran).
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
        form.Activate();

        // ------------------------------------------------ Processes and Performance, 2026-10-05
        //
        // Clint: right-click, see the machine "before even connecting". Any online machine whose
        // client can send the figures; an older client is SAID, never a blank window. Both items open
        // ONE window per machine, on the page chosen.
        Console.WriteLine();
        Console.WriteLine("== the right-click: Processes and Performance ==");
        sla.CanWatch = true;          // a current client
        loose.CanWatch = false;       // online, an older client
        Seed(form, new List<DeviceInfo> { sla, loose, offline });
        ToolStripMenuItem? ItemFor(string machine, string starts)
        {
            var row = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[2].Text == machine);
            var menu = list.ContextMenuStrip;
            if (row == null || menu == null) return null;
            list.SelectedItems.Clear(); row.Selected = true;
            menu.Show(list, new Point(10, 10));
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
            return menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text.StartsWith(starts));
        }
        var procOn = ItemFor("SLA-PC", "Processes"); list.ContextMenuStrip?.Close();
        var perfOn = ItemFor("SLA-PC", "Performance"); list.ContextMenuStrip?.Close();
        Check("an online machine on a current client offers Processes and Performance",
            procOn is { Enabled: true, Text: "Processes" } && perfOn is { Enabled: true, Text: "Performance" },
            $"\"{procOn?.Text}\" {procOn?.Enabled}, \"{perfOn?.Text}\" {perfOn?.Enabled}");
        var procOld = ItemFor("LOOSE-PC", "Processes"); list.ContextMenuStrip?.Close();
        Check("an older client greys them and says what to do",
            procOld is { Enabled: false, Text: "Processes  (update the support client)" }, $"\"{procOld?.Text}\" {procOld?.Enabled}");
        var procOff = ItemFor("OFF-PC", "Processes"); list.ContextMenuStrip?.Close();
        Check("an offline machine greys them and says offline",
            procOff is { Enabled: false, Text: "Processes  (offline)" }, $"\"{procOff?.Text}\" {procOff?.Enabled}");

        var watchWindows = Invoke(form, "_watchWindows") as System.Collections.IDictionary;
        procOn = ItemFor("SLA-PC", "Processes"); procOn?.PerformClick(); list.ContextMenuStrip?.Close();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
        var watchWin = watchWindows?[sla.DeviceId] as MachineWatchForm;
        Check("Processes opens the machine's window on the Processes page",
            watchWin is { Visible: true, Showing: MachineWatchForm.Page.Processes }, $"{watchWin?.Showing}");
        perfOn = ItemFor("SLA-PC", "Performance"); perfOn?.PerformClick(); list.ContextMenuStrip?.Close();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
        Check("Performance on the same machine brings THAT window to its Performance page — not a second window",
            watchWindows?.Count == 1 && ReferenceEquals(watchWindows[sla.DeviceId], watchWin) && watchWin?.Showing == MachineWatchForm.Page.Performance,
            $"{watchWindows?.Count} window(s), showing {watchWin?.Showing}");
        watchWin?.Close();
        for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
        Check("closing it forgets it, so the next one is a fresh window", watchWindows?.Count == 0, $"{watchWindows?.Count}");
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
        form.Activate();

        // ------------------------------------------------ Wake up, 2026-10-06 (Wake-on-LAN)
        Console.WriteLine();
        Console.WriteLine("== the right-click: Wake up ==");
        var wakeOnline = ItemFor("SLA-PC", "Wake up"); list.ContextMenuStrip?.Close();
        Check("an online machine has no Wake up", wakeOnline is null or { Visible: false }, $"\"{wakeOnline?.Text}\" visible={wakeOnline?.Visible}");
        offline.CanWake = false;
        Seed(form, new List<DeviceInfo> { sla, loose, offline });
        var wakeUnknown = ItemFor("OFF-PC", "Wake up");
        bool unknownShown = wakeUnknown?.Visible == true; list.ContextMenuStrip?.Close();
        Check("an offline machine whose card the server does not know: Wake up greyed, saying why",
            unknownShown && wakeUnknown is { Enabled: false, Text: "Wake up  (its network card is not known yet)" }, $"\"{wakeUnknown?.Text}\" {wakeUnknown?.Enabled}");
        offline.CanWake = true;
        Seed(form, new List<DeviceInfo> { sla, loose, offline });
        var wakeOff = ItemFor("OFF-PC", "Wake up");
        bool offShown = wakeOff?.Visible == true;
        Check("an offline machine the server can wake: Wake up offered", offShown && wakeOff is { Enabled: true, Text: "Wake up" }, $"\"{wakeOff?.Text}\" {wakeOff?.Enabled}");
        var connectAt = list.ContextMenuStrip?.Items.IndexOf(list.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().First(x => x.Text.StartsWith("Connect")));
        Check("...straight under Connect", wakeOff != null && list.ContextMenuStrip?.Items.IndexOf(wakeOff) == connectAt + 1);
        list.ContextMenuStrip?.Close();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
        form.Activate();

        form.Hide();

        // ------------------------------------------------ the light-or-dark button, 2026-09-23
        //
        // The same setting as the portal's switch, from the console. Worded as what it DOES, so on
        // a window painted light it must offer dark and the other way round — a button that names
        // the palette you are already in reads as a label, and Clint looked straight past exactly
        // that in the portal. Asked of a real window in each palette.
        Console.WriteLine();
        Console.WriteLine("== the light-or-dark button ==");
        var overrideBefore = Theme.Override;
        try
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                Theme.Override = mode;
                using var f = new DashboardForm(brand, new TechLink("ws://127.0.0.1:59999"));
                f.StartPosition = FormStartPosition.Manual;
                f.WindowState = FormWindowState.Normal;
                f.Location = new Point(0, 0);
                // 1920, not 1600, since Latest activity (2026-10-26, §164) filled the header: at 1600 Light/Dark now steps out to
                // the tray menu by the header's own rule (the least-used button goes first) — the knowledge base checks below prove
                // that rule at 1366. Here the button itself is looked at, so it must be on the header.
                f.Size = new Size(1920, 900);
                f.Show();
                for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }

                var btn = Field<RoundButton>(f, "_modeBtn");
                var next = Field<RoundButton>(f, "_newTicketBtn");
                var name = mode == ThemeMode.Light ? "light" : "dark";
                if (btn == null || next == null) { Check($"found the button on a {name} window", false); continue; }

                var want = mode == ThemeMode.Light ? "Dark mode" : "Light mode";
                Check($"a {name} window offers \"{want}\"", btn.Text == want, $"\"{btn.Text}\"");
                Check($"it is showing ({name})", btn.Visible && btn.Width > 0, $"left {btn.Left}, header {btn.Parent?.Width} wide, window {f.Width}");
                // §30: wide enough for either wording, measured, so the header does not shuffle
                // when it flips.
                int needed = new[] { "Light mode", "Dark mode" }.Max(s => TextRenderer.MeasureText(s, btn.Font).Width);
                Check($"it is wide enough for either wording ({name})", btn.Width > needed, $"{btn.Width} px for {needed} px");
                Check($"it is tall enough for its own font ({name})",
                    btn.Height >= TextRenderer.MeasureText("Ag", btn.Font).Height, $"{btn.Height} px");
                Check($"it sits left of New ticket without touching it ({name})",
                    btn.Right < next.Left && btn.Top == next.Top, $"{btn.Right} < {next.Left}, top {btn.Top}/{next.Top}");
                Shot(f, "mode-" + name);

                // Sign out, 2026-09-30: "there is also no logout for technicians. closing the console
                // simply minimizes it." A button at the far right of the header, and on the tray menu.
                var so = Field<RoundButton>(f, "_signOutBtn");
                var pill = Field<Control>(f, "_statusPill");
                Check($"there is a Sign out button, showing ({name})", so != null && so.Visible && so.Text == "Sign out");
                if (so != null && pill != null)
                {
                    Check($"it is the right-most thing in the header ({name})",
                        so.Parent != null && so.Right <= so.Parent.Width && so.Right > so.Parent.Width - 60,
                        $"right edge {so.Right} of {so.Parent?.Width}");
                    Check($"the status pill sits left of it without touching ({name})", pill.Right < so.Left, $"{pill.Right} < {so.Left}");
                    Check($"it is wide enough for its words ({name})", so.Width > TextRenderer.MeasureText(so.Text, so.Font).Width, $"{so.Width} px");
                }
                var tray = Field<NotifyIcon>(f, "_tray");
                var trayItems = tray?.ContextMenuStrip?.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList() ?? new List<string?>();
                Check($"the tray menu offers Sign out and Exit ({name})", trayItems.Contains("Sign out") && trayItems.Contains("Exit"),
                    string.Join(" | ", trayItems));

                // The customer headings, 2026-09-30. Technicians: in dark mode the client names
                // were "hardly readable, they are black". They are the ListView's own group
                // headers, which Windows draws in its colour, not ours — so they are now painted
                // by GroupHeaders, in the signature colour of the column headings above them (the portal's --grape).
                // Read off the SCREEN, not DrawToBitmap: that goes through WM_PRINT, which is
                // not the path a technician's eyes take.
                Seed(f, devices);
                var devList = Field<ListView>(f, "_devices");
                if (devList != null)
                {
                    var t = Theme.FromBranding(brand, AppSurface.Technician);
                    var shot = ScreenShot(devList, "headings-" + name);
                    var rect = shot == null ? Rectangle.Empty : HeaderRect(devList, 0);
                    int signaturePx = shot == null ? 0 : CountNear(shot, rect, t.SurfaceAltText);
                    Check($"the first customer heading is painted in the signature colour ({name})",
                        signaturePx > 20, $"{signaturePx} px of {ColorTranslator.ToHtml(t.SurfaceAltText)} in {rect}");
                    // Black text on the dark ground is DARKER than the ground; nothing we paint is.
                    int darkPx = shot == null ? 0 : CountDarkerThan(shot, rect, t.Surface, 12);
                    if (mode == ThemeMode.Dark)
                        Check("nothing in a dark heading is darker than the list behind it", darkPx == 0, $"{darkPx} px");

                    // The chevron is ours but the fold is the list's: a click where the chevron is
                    // DRAWN must fold the customer away. Found by its pixels, clicked by message.
                    if (shot != null && !rect.IsEmpty)
                    {
                        var chevron = FindColour(shot, Rectangle.FromLTRB(rect.Right - 60, rect.Top, rect.Right, rect.Bottom), t.Muted);
                        var lvg = devList.Groups[0];
                        var before = lvg.CollapsedState;
                        if (chevron is { } at)
                        {
                            // A REAL click. A posted WM_LBUTTONDOWN is not one: the list hit-tests
                            // where the cursor actually is, and selected the customer's machines.
                            var was = Cursor.Position;
                            Cursor.Position = devList.PointToScreen(at);
                            mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // left down
                            mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // left up
                            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(20); }
                            Cursor.Position = was;
                        }
                        Check($"a click on the drawn chevron folds the customer ({name})",
                            chevron != null && before == ListViewGroupCollapsedState.Expanded
                            && lvg.CollapsedState == ListViewGroupCollapsedState.Collapsed,
                            $"chevron at {chevron?.ToString() ?? "nowhere"}, {before} -> {lvg.CollapsedState}");
                        using var folded = ScreenShot(devList, "headings-folded-" + name);
                        lvg.CollapsedState = ListViewGroupCollapsedState.Expanded;
                    }
                    shot?.Dispose();
                }
                f.Hide();
            }
        }
        finally { Theme.Override = overrideBefore; }

        // The knowledge base (3.8.5, 2026-10-05): "an option on all user's dashboards". A header
        // button, between New ticket and Office chat. The header was already full, so the question
        // that matters is whether the buttons now run into the company's name — asked at a laptop's
        // 1366 px as well as a desktop's, with the header's words measured the way BrandHeader
        // draws them.
        Console.WriteLine();
        Console.WriteLine("== the knowledge base button ==");
        foreach (var width in new[] { 1920, 1366 })
        {
            using var f = new DashboardForm(brand, new TechLink("ws://127.0.0.1:59999"));
            f.StartPosition = FormStartPosition.Manual;
            f.WindowState = FormWindowState.Normal;
            f.Location = new Point(0, 0);
            f.Size = new Size(width, 800);
            f.Show();
            for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var kb = Field<RoundButton>(f, "_kbBtn");
            var nt = Field<RoundButton>(f, "_newTicketBtn");
            var oc = Field<RoundButton>(f, "_staffChatBtn");
            var mode = Field<RoundButton>(f, "_modeBtn");
            if (kb == null || nt == null || oc == null || mode == null) { Check($"found the header's buttons at {width} px", false); continue; }
            Check($"there is a Knowledge base button, showing ({width} px)", kb.Visible && kb.Text == "Knowledge base");
            Check($"it is wide enough for its words ({width} px)", kb.Width > TextRenderer.MeasureText(kb.Text, kb.Font).Width, $"{kb.Width} px");
            Check($"it sits between New ticket and Office chat without touching either ({width} px)",
                nt.Right < kb.Left && kb.Right < oc.Left && kb.Top == oc.Top, $"{nt.Right} < {kb.Left}..{kb.Right} < {oc.Left}");
            // The words as drawn: their own width, or "…" at the header's limit — and no button may
            // sit on them. Measured independently of the header's own TextRight as well, so a header
            // that set its limit wrongly cannot vouch for itself.
            var header = f.Controls.OfType<BrandHeader>().First();
            int natural = HeaderWordsEnd(f, brand);
            int drawn = Math.Min(natural, header.TextLimit);
            int leftmost = mode.Visible ? mode.Left : nt.Left;
            Check($"no header button sits on the header's words ({width} px)",
                leftmost > drawn && header.TextRight <= leftmost,
                $"left-most button at {leftmost}, words end at {drawn} ({(drawn < natural ? "shortened with …" : "whole")}), header says {header.TextRight}");
            Check($"Knowledge base is on the header whatever the width ({width} px)", kb.Visible && kb.Left > drawn);
            // Latest activity (2026-10-26, HANDOVER §164): beside Tickets, wide enough for "Latest activity (99+)" so a count never shuffles the header.
            var act = Field<RoundButton>(f, "_activityBtn"); var tk = Field<RoundButton>(f, "_ticketsBtn");
            Check($"Latest activity sits beside Tickets, wide enough for its widest words, clear of the header's words ({width} px)",
                act != null && tk != null && act.Visible && act.Right < tk.Left && act.Top == tk.Top && act.Left > drawn
                && act.Width > TextRenderer.MeasureText("Latest activity (99+)", act.Font).Width,
                act == null ? "no button" : $"{act.Left}..{act.Right} beside Tickets at {tk?.Left}, {act.Width} px");
            var trayItems = Field<NotifyIcon>(f, "_tray")?.ContextMenuStrip?.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList() ?? new();
            Check($"Light/Dark is on the tray menu too, so it is never lost ({width} px{(mode.Visible ? "" : ", its button stepped out")})",
                trayItems.Contains(mode.Text), string.Join(" | ", trayItems));
            Shot(f, $"header-kb-{width}");
            f.Hide();
        }

        // ---- the free version (2026-10-06, NEXT-SESSION-FREE.md Stage F) ----------------------------
        // One person, two unattended computers, no tickets: the console draws only that. Fed the REAL
        // state a free company's console gets, then a full one's, through the form's own LoadState.
        Console.WriteLine();
        Console.WriteLine("== the free version ==");
        {
            var f = new DashboardForm(brand, new TechLink("ws://127.0.0.1:59999"))
            { StartPosition = FormStartPosition.Manual, WindowState = FormWindowState.Normal, Location = new Point(0, 0), Size = new Size(1400, 860) };
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var load = f.GetType().GetMethod("LoadState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var two = new List<DeviceInfo>
            {
                new() { DeviceId = "free-a", MachineName = "BRANCH-A", Online = true, AgentOnline = true, UnattendedEnabled = true, Unattended = true, UnattendedPassword = true },
                new() { DeviceId = "free-b", MachineName = "BRANCH-B", Online = false, UnattendedEnabled = true, UnattendedPassword = true }
            };
            void Feed(bool free) { load.Invoke(f, new object[] { new StateMsg { You = "Fran Free", Devices = two, Free = free } }); for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); } }
            Feed(true);

            string[] fullOnly = { "_ticketsBtn", "_activityBtn", "_staffChatBtn", "_kbBtn", "_newTicketBtn", "_webChatBtn" };
            var shownFull = fullOnly.Where(n => Field<Control>(f, n)?.Visible == true).ToList();
            Check("free: no Tickets, Latest activity, Office chat, Knowledge base, New ticket or Website chat", shownFull.Count == 0, string.Join(", ", shownFull));
            Check("free: no customer chat, no incoming calls, no \"Connect with a code\"",
                Field<Control>(f, "_chatCard")?.Visible == false && Field<Control>(f, "_callsCard")?.Visible == false && Field<Control>(f, "_codeBtn")?.Visible == false);
            Check("free: no tabs at all, and the list is its two computers under \"Your computers\"",
                Field<Control>(f, "_tabAttended")?.Visible == false && Field<Control>(f, "_tabUnattended")?.Visible == false
                && Field<ListView>(f, "_devices")!.Items.Count == 2
                && Field<ListView>(f, "_devices")!.Groups.Cast<ListViewGroup>().All(g => g.Header.StartsWith("Your computers")),
                $"{Field<ListView>(f, "_devices")!.Items.Count} row(s)");
            var pill = Field<Control>(f, "_statusPill")!; var modeB = Field<Control>(f, "_modeBtn")!;
            var upB = Field<Control>(f, "_upgradeBtn")!;
            Check("free: the header closes up — Upgrade beside the status, Light/Dark beside it, no gap where the others were",
                upB.Visible && upB.Right <= pill.Left && pill.Left - upB.Right <= 30 && (!modeB.Visible || (modeB.Right <= upB.Left && upB.Left - modeB.Right <= 30)),
                $"Light/Dark {modeB.Left}..{modeB.Right}, Upgrade {upB.Left}..{upB.Right}, status from {pill.Left}");

            // The right-click menu, as it opens on a row.
            var freeList = Field<ListView>(f, "_devices")!;
            freeList.Items[0].Selected = true;
            var menu = freeList.ContextMenuStrip!;
            menu.Show(freeList, new Point(10, 10));
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
            var offered = menu.Items.Cast<ToolStripItem>().Where(i => i.Available && i is not ToolStripSeparator).Select(i => i.Text).ToList();
            menu.Close();
            Check("free: right-click offers Connect and Remove, nothing of the full version",
                offered.Contains("Connect") && offered.Contains("Remove this computer…") && offered.All(t => t is "Connect" or "Remove this computer…" or "Forget saved password"),
                string.Join(" | ", offered));
            Shot(f, "free-console");

            // Connecting on the free version: no reason; the password alone when it is not saved.
            using (var ask = new ReasonDialog(Field<Theme>(f, "_t")!, "BRANCH-A", "", askPassword: true, askReason: false))
            {
                ask.Show();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
                var boxes = ask.Controls.Cast<Control>().SelectMany(Flatten).OfType<InputBox>().Where(b => b.Visible).ToList();
                Check("free: Connect asks the password and no reason", boxes.Count == 1 && ask.Text == "Unattended password", $"{boxes.Count} box(es), \"{ask.Text}\"");
                boxes.FirstOrDefault()?.Box.AppendText("Branch-Office-2026");
                ask.GetType().GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ask, null);
                Check("free: and goes ahead with only the password", ask.DialogResult == DialogResult.OK && ask.Password == "Branch-Office-2026" && ask.Reason == "");
            }

            Feed(false);
            Check("upgraded: no Upgrade button", Field<Control>(f, "_upgradeBtn")?.Visible == false);
            Check("upgraded (a full company's state): every button back", fullOnly.Where(n => n != "_webChatBtn").All(n => Field<Control>(f, n)?.Visible == true)
                && Field<Control>(f, "_chatCard")?.Visible == true && Field<Control>(f, "_tabAttended")?.Visible == true);
            f.Close(); f.Dispose();
        }

        // The name typed into Request Support (2026-10-18): it is the machine's Who until somebody links it, marked as
        // only what they said; and the Link window offers people on file by that name, and the name as a new contact.
        Console.WriteLine();
        Console.WriteLine("== the name they gave at a support request ==");
        {
            var f = new DashboardForm(brand, new TechLink("ws://127.0.0.1:59999"));
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.Size = new Size(1400, 800);
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var asked = Dev("DESKTOP123", user: "frontdesk", online: true);
            asked.RequestedBy = "John Smith"; asked.RequestedByUtc = DateTimeOffset.UtcNow;
            var never = Dev("DESKTOP456", user: "reception", online: true);
            var linkedSince = Dev("ZEN-ASKED", client: "Zenith Motors", clientId: 3, user: "x", online: true);
            linkedSince.RequestedBy = "Old Name";
            Seed(f, new List<DeviceInfo> { asked, never, linkedSince });
            var rows = Field<ListView>(f, "_devices")!.Items.Cast<ListViewItem>().ToList();
            ListViewItem? Row(DeviceInfo d) => rows.FirstOrDefault(i => i.Tag is DeviceInfo x && x.DeviceId == d.DeviceId);
            Check("an unlinked machine's Who is the name they gave, not Windows' sign-in", Row(asked)?.SubItems[1].Text == "John Smith", Row(asked)?.SubItems[1].Text ?? "no row");
            Check("...and its tooltip says that is only what they typed", Row(asked)?.ToolTipText.Contains("name they gave") == true && Row(asked)!.ToolTipText.Contains("not checked"),
                Row(asked)?.ToolTipText ?? "");
            Check("a machine that never asked keeps Windows' sign-in, unmarked", Row(never)?.SubItems[1].Text == "reception" && Row(never)?.ToolTipText.Contains("name they gave") == false);
            Check("a machine linked since shows its customer, not the old name", Row(linkedSince)?.SubItems[1].Text == "Zenith Motors");
            Shot(f, "requested-name");
            f.Close(); f.Dispose();

            var t = Theme.FromBranding(brand, AppSurface.Technician);
            var dlg = new LinkDeviceDialog(t, new TechLink("ws://127.0.0.1:59999"), asked);
            dlg.Show();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var clients = new List<ClientInfo>
            {
                new() { Id = 1, Name = "Acme Ltd", Contacts = { new() { Id = 11, ClientId = 1, Name = "John Smith" }, new() { Id = 12, ClientId = 1, Name = "Mary Jones" } } },
                new() { Id = 2, Name = "Zenith Motors", Contacts = { new() { Id = 21, ClientId = 2, Name = "John" } } },
            };
            dlg.GetType().GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dlg, clients);
            void Call(string method) { dlg.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dlg, null); for (int i = 0; i < 5; i++) Application.DoEvents(); }
            var lv = Field<ListView>(dlg, "_list")!;
            List<string> Texts() => lv.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();
            void Pick(string text) { foreach (ListViewItem i in lv.Items) i.Selected = i.Text == text; }
            Call("Fill");
            var texts = Texts();
            Check("the Link window offers the people on file by that name first, the best fit first",
                texts.Count > 2 && texts[0] == "John Smith — Acme Ltd" && texts[1] == "John — Zenith Motors", string.Join(" | ", texts.Take(4)));
            Check("...then adding the name as a new contact", texts.Count > 2 && texts[2] == "+ Add John Smith as a new contact…", texts.ElementAtOrDefault(2) ?? "");
            Check("...and Save untouched still changes nothing", lv.SelectedItems.Count == 1 && lv.SelectedItems[0].Text == "Not linked to anybody",
                lv.SelectedItems.Count == 1 ? lv.SelectedItems[0].Text : $"{lv.SelectedItems.Count} chosen");
            Check("...and it says what name they gave", Field<Label>(dlg, "_sub")?.Text.Contains("They gave the name John Smith") == true, Field<Label>(dlg, "_sub")?.Text ?? "");
            Pick("+ Add John Smith as a new contact…"); Call("Submit");
            Check("choosing it asks which customer they are at", Field<Label>(dlg, "_head")?.Text == "Which customer is John Smith at?", Field<Label>(dlg, "_head")?.Text ?? "");
            Check("...listing the customers alone, with a way back", Texts().SequenceEqual(new[] { "← Back to everybody", "Acme Ltd", "Zenith Motors" }), string.Join(" | ", Texts()));
            Pick("← Back to everybody"); Call("Submit");
            Check("the way back goes back", Texts().FirstOrDefault() == "John Smith — Acme Ltd" && Field<Label>(dlg, "_head")?.Text == asked.MachineName);
            Pick("+ Add John Smith as a new contact…"); Call("Submit");
            Pick("Zenith Motors"); Call("Submit");
            Check("a customer chosen: Save sends the new contact's name and that customer, no contact id",
                dlg.ClientId == 2 && dlg.ContactId == 0 && dlg.NewContactName == "John Smith" && dlg.DialogResult == DialogResult.OK,
                $"client {dlg.ClientId}, contact {dlg.ContactId}, new \"{dlg.NewContactName}\", {dlg.DialogResult}");
            if (!dlg.IsDisposed) dlg.Dispose();

            // 2026-10-23: the name given at the install or a request, with its email, ADDED when a customer is chosen — a
            // tick, ticked, to say no for a shared PC.
            asked.RequestedByEmail = "john@acme.co.za";
            LinkDeviceDialog Open(DeviceInfo d)
            {
                var x = new LinkDeviceDialog(t, new TechLink("ws://127.0.0.1:59999"), d);
                x.Show();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
                x.GetType().GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(x, clients);
                x.GetType().GetMethod("Fill", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(x, null);
                return x;
            }
            void Choose(LinkDeviceDialog x, string text)
            {
                foreach (ListViewItem i in Field<ListView>(x, "_list")!.Items) i.Selected = i.Text == text;
                x.GetType().GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(x, null);
                for (int i = 0; i < 5; i++) Application.DoEvents();
            }
            var d1 = Open(asked);
            var tick = Field<RoundCheck>(d1, "_addGiven");
            Check("the Link window has the tick, ticked, naming the person and their email",
                tick is { Checked: true, Visible: true } && tick.Text == "Add John Smith (john@acme.co.za) as a contact when I choose a customer", tick?.Text ?? "no tick");
            var tickNeed = tick == null ? Size.Empty : TextRenderer.MeasureText(tick.Text, tick.Font,
                new Size(tick.Width - SystemInformation.MenuCheckSize.Width - 8, 0), TextFormatFlags.WordBreak);
            var listBox = Field<ListView>(d1, "_list")!;
            Check("...all its words show, and it covers nothing", tick != null && tickNeed.Height <= tick.Height && !tick.Bounds.IntersectsWith(listBox.Bounds),
                $"needs {tickNeed.Height}, has {tick?.Height}");
            Check("...and the line above says the email too", Field<Label>(d1, "_sub")?.Text.Contains("They gave the name John Smith (john@acme.co.za).") == true, Field<Label>(d1, "_sub")?.Text ?? "");
            Shot(d1, "link-add-given");
            Choose(d1, "+ Add John Smith as a new contact…");
            Check("...hidden while it asks which customer — that step adds them already", tick?.Visible == false);
            Choose(d1, "← Back to everybody");
            Check("...and back with the way back", tick?.Visible == true);
            Choose(d1, "Acme Ltd");
            Check("a CUSTOMER chosen with the tick on: Save adds the person there", d1.ClientId == 1 && d1.ContactId == 0 && d1.NewContactName == "John Smith"
                && d1.DialogResult == DialogResult.OK, $"client {d1.ClientId}, new \"{d1.NewContactName}\"");
            if (!d1.IsDisposed) d1.Dispose();
            var d2 = Open(asked);
            Field<RoundCheck>(d2, "_addGiven")!.Checked = false;
            Choose(d2, "Acme Ltd");
            Check("...the tick taken off: the customer alone, nobody added", d2.ClientId == 1 && d2.NewContactName == "" && d2.DialogResult == DialogResult.OK,
                $"new \"{d2.NewContactName}\"");
            if (!d2.IsDisposed) d2.Dispose();
            var d3 = Open(asked);
            Choose(d3, "      Mary Jones");
            Check("a PERSON chosen: that person, the tick does not add another", d3.ContactId == 12 && d3.NewContactName == "", $"contact {d3.ContactId}, new \"{d3.NewContactName}\"");
            if (!d3.IsDisposed) d3.Dispose();
            var d4 = Open(never);
            Check("a machine nobody gave a name for: no tick", Field<RoundCheck>(d4, "_addGiven") == null);
            if (!d4.IsDisposed) d4.Dispose();
            var d5 = Open(linkedSince);
            Check("a machine linked to a customer but no person, with a name given: the tick is there", Field<RoundCheck>(d5, "_addGiven") != null);
            if (!d5.IsDisposed) d5.Dispose();
        }

        // Signing out with no session open: the window goes, nothing is asked, and the form says it
        // was a sign-out, which is what Program reads to open the sign-in window again.
        Console.WriteLine();
        Console.WriteLine("== sign out ==");
        {
            var f = new DashboardForm(brand, new TechLink("ws://127.0.0.1:59999"));
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            Check("no session windows are open", DashboardForm.OpenSessionWindows() == 0);
            f.GetType().GetMethod("SignOut", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f, null);
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            Check("Sign out closes the console rather than hiding it in the tray", f.IsDisposed || !f.Visible,
                $"disposed {f.IsDisposed}, visible {(!f.IsDisposed && f.Visible)}");
            Check("...and says it was a sign-out, for Program to start again at the sign-in window", f.SignedOut);
            Check("...and takes its tray icon with it", Field<NotifyIcon>(f, "_tray")?.Visible == false);
        }

        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static List<string> HeaderNames(ListView lv) => lv.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList();

    /// <summary>
    /// Where the header's own words end, by BrandHeader's arithmetic: the logo (at most 220 px, 14 px
    /// after it), then the company's name — drawn only when there is no logo — and the subtitle.
    /// </summary>
    private static int HeaderWordsEnd(DashboardForm f, Branding brand)
    {
        var header = f.Controls.OfType<BrandHeader>().First();
        var t = Theme.FromBranding(brand, AppSurface.Technician);
        int x = 18;
        Image? logo = null;
        try { if (brand.HasLogo) logo = Image.FromFile(brand.LogoPath); } catch { }
        if (logo != null)
        {
            int h = Math.Min(header.Height - 26, 56);
            x += Math.Min((int)(logo.Width * (h / (double)logo.Height)), 220) + 14;
            logo.Dispose();
        }
        var subtitle = f.Controls.OfType<BrandHeader>().First().GetType()
            .GetField("_subtitle", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(header) as string ?? "";
        int words = TextRenderer.MeasureText(subtitle, t.Body).Width;
        if (logo == null)
        {
            using var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold);
            words = Math.Max(words, TextRenderer.MeasureText(brand.CompanyName, titleFont).Width);
        }
        return x + words;
    }

    /// <summary>
    /// Writes a picture of the window beside the harness.
    ///
    /// The numbers above prove the list holds the right rows; only a picture answers "does this
    /// look like something a technician wants to work in", which is the whole point of the stage.
    /// Every layout fault in §30 was caught by somebody looking at one.
    /// </summary>
    private static IEnumerable<Control> Flatten(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(Flatten));

    private static void Shot(Form f, string name)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"        (no picture: {ex.Message})"); }
    }

    /// <summary>
    /// Puts devices into the form's own map and asks it to redraw — the same two steps
    /// OnDeviceUpdate takes when one arrives from the server.
    ///
    /// Reflection, because the map and the refresh are private and should stay private: nothing in
    /// the product has any business writing to them. The RULES this probe cares most about are
    /// asked of DeviceList directly for exactly that reason; this half is here to prove the window
    /// renders them, and rendering is what a Form legitimately keeps to itself.
    /// </summary>
    private static void Seed(DashboardForm form, List<DeviceInfo> devices)
    {
        if (Invoke(form, "_deviceMap") is Dictionary<string, DeviceInfo> map)
        {
            map.Clear();
            foreach (var d in devices) map[d.DeviceId] = d;
        }
        form.GetType().GetMethod("RefreshDevices", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(form, null);
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }

    /// <summary>The list as it is on the screen right now, saved beside the harness.</summary>
    private static Bitmap? ScreenShot(Control c, string name)
    {
        try
        {
            c.FindForm()?.Activate();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var r = c.RectangleToScreen(c.ClientRectangle);
            var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            var dir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture: {path}");
            return bmp;
        }
        catch (Exception ex) { Console.WriteLine($"        (no picture: {ex.Message})"); return null; }
    }

    /// <summary>A group's heading band, in the list's client coordinates, as the list reports it.</summary>
    private static Rectangle HeaderRect(ListView lv, int group) => GroupHeaders.HeaderBounds(lv, group);

    private static int CountNear(Bitmap b, Rectangle r, Color want, int tolerance = 24)
    {
        int n = 0;
        r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                var p = b.GetPixel(x, y);
                if (Math.Abs(p.R - want.R) <= tolerance && Math.Abs(p.G - want.G) <= tolerance && Math.Abs(p.B - want.B) <= tolerance) n++;
            }
        return n;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);

    /// <summary>The middle of the pixels of one colour in a region, or null if there are none.</summary>
    private static Point? FindColour(Bitmap b, Rectangle r, Color want, int tolerance = 30)
    {
        r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
        long sx = 0, sy = 0, n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                var p = b.GetPixel(x, y);
                if (Math.Abs(p.R - want.R) <= tolerance && Math.Abs(p.G - want.G) <= tolerance && Math.Abs(p.B - want.B) <= tolerance)
                { sx += x; sy += y; n++; }
            }
        return n == 0 ? null : new Point((int)(sx / n), (int)(sy / n));
    }

    private static int CountDarkerThan(Bitmap b, Rectangle r, Color ground, int by)
    {
        int n = 0;
        static int Sum(Color c) => c.R + c.G + c.B;
        r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (Sum(b.GetPixel(x, y)) < Sum(ground) - by * 3) n++;
        return n;
    }

    private static object? Invoke(object o, string field) =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o);

    private static T? Field<T>(object o, string field) where T : class => Invoke(o, field) as T;
}
