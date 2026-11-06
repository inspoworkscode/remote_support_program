using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.Technician;

namespace RemoteSupport.Tools.DashProbe;

// ---------------------------------------------------------------------------------------------
// Drives the REAL DashboardForm and measures where the device list actually ends up, maximised
// and restored, on whichever monitor it is put on.
//
// "The devices disappear when I maximise the window" has several completely different causes that
// look identical from the outside: the list could be empty, or full but sized to nothing, or
// sized correctly but pushed off the bottom of its parent, or hidden behind another card. Only
// the numbers separate them, so this prints the bounds of every control in the chain from the
// form down to the ListView, in both window states, and screenshots each one.
//
// It logs in for real with the stored portal credentials, so the list has real devices in it.
//
//   dashprobe <out-dir>
//
// NOTE: an explicit class, not top-level statements. With top-level statements a "static void
// Main" in the same file is compiled as a LOCAL FUNCTION THAT NOTHING CALLS — the generated entry
// point runs the top-level code, which was nothing at all. The first version of this exited 0
// with no output and no files, and looked for all the world like a silent crash.
internal static class Program
{
    // Written as it happens, not buffered to the end: a probe that dies half way through must
    // still leave behind the half it managed (HANDOVER 17, the PDF failure hidden by Out-Null).
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "dashprobe.txt");

    private static void Log(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(LogFile, s + Environment.NewLine); } catch { }
    }

    [STAThread]
    private static void Main(string[] argv)
    {
        try { File.Delete(LogFile); } catch { }
        Log($"dashprobe starting, pid {Environment.ProcessId}, log {LogFile}");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // The same DPI mode the console itself uses. Measuring a form set up differently from the
        // one Clint is looking at would be worse than not measuring at all.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // A real message loop, on the UI thread. Without one the awaits never resume and nothing
        // is ever laid out.
        var ctx = new ApplicationContext();
        var starter = new System.Windows.Forms.Timer { Interval = 50 };
        starter.Tick += async (_, _) =>
        {
            starter.Stop();
            try { await Run(argv); }
            catch (Exception ex) { Log("dashprobe FAILED: " + ex); }
            ctx.ExitThread();
        };
        starter.Start();
        Application.Run(ctx);
        Log("dashprobe finished");
    }

    private static async Task Run(string[] argv)
    {
        var outDir = argv.Length > 0 ? argv[0] : Path.Combine(Path.GetTempPath(), "dashprobe-out");
        Directory.CreateDirectory(outDir);

        Log("=== monitors ===");
        foreach (var s in Screen.AllScreens)
            Log($"  {(s.Primary ? "primary  " : "secondary")}  bounds={s.Bounds}  working={s.WorkingArea}  {s.DeviceName}");
        Log("");

        var brand = Branding.Load();
        Log($"branding server: {brand.ServerUrl}");

        var link = new TechLink(brand.ServerUrl);
        var creds = RsCredentials.Load();
        var step = await link.LoginAsync(creds.Email, creds.Password);
        if (step.Next == "code" && creds.Code() is { } code) step = await link.CodeAsync(step.Pending, code);   // two-factor (HANDOVER §72)
        var (ok, why) = (step.Ok, step.Error.Length > 0 ? step.Error : step.Next);
        if (!ok) { Log("login failed: " + why); return; }
        Log("logged in as " + link.DisplayName);

        var cts = new CancellationTokenSource();
        _ = link.RunAsync(cts.Token);

        int devicesSeen = 0;
        link.OnState += s => devicesSeen = s.Devices.Count;

        var form = new DashboardForm(brand, link);
        form.Show();

        for (int i = 0; i < 80 && devicesSeen == 0; i++) { Application.DoEvents(); await Task.Delay(100); }
        Log($"devices delivered by the server: {devicesSeen}");
        Log("");

        Measure(form, devicesSeen, outDir, "1 maximised as opened");

        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(1280, 820);
        Measure(form, devicesSeen, outDir, "2 restored down");

        form.WindowState = FormWindowState.Maximized;
        Measure(form, devicesSeen, outDir, "3 maximised again");

        var current = Screen.FromControl(form);
        var other = Screen.AllScreens.FirstOrDefault(s => s.DeviceName != current.DeviceName);
        if (other != null)
        {
            form.WindowState = FormWindowState.Normal;
            form.Location = new Point(other.WorkingArea.Left + 60, other.WorkingArea.Top + 60);
            Measure(form, devicesSeen, outDir, "4 other monitor restored");
            form.WindowState = FormWindowState.Maximized;
            Measure(form, devicesSeen, outDir, "5 other monitor maximised");
        }
        else Log("(only one monitor attached, so the second-monitor case cannot be reproduced here)");

        cts.Cancel();
        form.Close();
    }

    /// <summary>Finds the devices ListView by its columns rather than by name, since it is private.</summary>
    private static ListView? FindDevices(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ListView lv && lv.Columns.Count > 4 && lv.Columns[0].Text == "Machine") return lv;
            var found = FindDevices(c);
            if (found != null) return found;
        }
        return null;
    }

    private static void Measure(Form form, int devicesSeen, string outDir, string label)
    {
        Application.DoEvents();
        Thread.Sleep(500);
        Application.DoEvents();

        Log($"--- {label} ---");
        Log($"  form: state={form.WindowState} bounds={form.Bounds} client={form.ClientSize} dpi={form.DeviceDpi}");
        var screen = Screen.FromControl(form);
        Log($"  monitor: {screen.DeviceName} {(screen.Primary ? "(primary)" : "(secondary)")} working={screen.WorkingArea}");

        var lv = FindDevices(form);
        if (lv == null) { Log("  THE DEVICE LIST CONTROL IS NOT IN THE FORM AT ALL"); Log(""); return; }

        // From the form down to the ListView, so the first place the size goes wrong is visible.
        var chain = new List<Control>();
        for (Control? c = lv; c != null && c != form; c = c.Parent) chain.Add(c);
        chain.Reverse();
        foreach (var c in chain)
            Log($"    {c.GetType().Name,-13} dock={c.Dock,-6} bounds={c.Bounds} vis={c.Visible} " +
                $"client={c.ClientSize}" + (c.Parent != null ? $" parentClient={c.Parent.ClientSize}" : ""));

        Log($"  ListView: items={lv.Items.Count} view={lv.View} vis={lv.Visible} {lv.Width}x{lv.Height}");

        var onForm = form.RectangleToClient(lv.RectangleToScreen(lv.ClientRectangle));
        Log($"  ListView in form coords: {onForm}   form client: {form.ClientRectangle}");

        if (lv.Height <= 0 || lv.Width <= 0) Log("  -> THE LIST HAS NO SIZE. The layout gave it nothing.");
        else if (onForm.Top >= form.ClientRectangle.Bottom || onForm.Bottom <= 0) Log("  -> THE LIST IS OFF THE VISIBLE AREA.");
        else if (lv.Items.Count == 0 && devicesSeen > 0) Log("  -> THE LIST IS EMPTY although the server sent devices.");
        else Log($"  -> present, sized, {lv.Items.Count} row(s).");

        try
        {
            using var bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
            form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            var png = Path.Combine(outDir, label.Replace(' ', '-') + ".png");
            bmp.Save(png, ImageFormat.Png);
            Log($"  screenshot: {png}");
        }
        catch (Exception ex) { Log("  screenshot failed: " + ex.Message); }
        Log("");
    }
}
