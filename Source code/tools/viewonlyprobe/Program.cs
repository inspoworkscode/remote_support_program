using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// viewonlyprobe - the console's session window for a customer sharing from a web browser
// (stage 3.8.2, 2026-09-30).
//
// websupportcheck proves the server marks the session view only. This builds the REAL session
// window with that flag, and without it, and asks what a technician is offered: no control, no
// System menu, no Files, Scripts, Sound or Listen, a banner saying why — and Chat, Info and
// recording still there. And that a normal session is exactly as it was. It is built in the
// window's constructor, so a fault here would stop a browser caller's window opening at all.
//
//   dotnet run -c Debug --project tools/viewonlyprobe/viewonlyprobe.csproj [picture folder]
// ---------------------------------------------------------------------------------------------

internal static class ViewOnlyProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);
        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);

        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        }
        T F<T>(object o, string name) => (T)typeof(SessionForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

        Console.WriteLine("viewonlyprobe - a session window for a customer sharing from a browser");
        foreach (var viewOnly in new[] { true, false })
        {
            Console.WriteLine();
            Console.WriteLine(viewOnly ? "== view only ==" : "== a normal session, unchanged ==");
            var session = new SessionInfo { SessionId = "probe", DeviceId = viewOnly ? "web-probe" : "probe", MachineName = viewOnly ? "Jane Smith (web browser)" : "PROBE-PC", StartedBy = "Probe", ViewOnly = viewOnly };
            SessionForm? f = null;
            Exception? threw = null;
            try { f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand); }
            catch (Exception ex) { threw = ex; }
            Check("the window opens", f != null, threw?.Message ?? "");
            if (f == null) continue;
            using (f)
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.Size = new Size(1320, 850);
                f.Show();
                for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }

                var view = F<ScreenView>(f, "_view");
                var control = F<CheckBox>(f, "_controlChk");
                var sys = F<RoundButton>(f, "_sysBtn");
                var tools = F<ContextMenuStrip>(f, "_toolsMenu");
                var banner = F<Panel>(f, "_banner");
                var bannerText = F<Label>(f, "_bannerText");
                bool Shown(string text) => tools.Items.Cast<ToolStripItem>().Any(i => i.Text == text && i.Available);
                // A menu that has never dropped reports Available, not Visible, for what it will show.
                if (viewOnly)
                {
                    Check("no control: the screen sends no input", !view.ControlEnabled);
                    Check("...and the Control tick is gone", !control.Visible);
                    Check("no System menu (Ctrl+Alt+Del, restart, privacy screen)", !sys.Visible);
                    Check("no Files, Scripts, Sound or Listen", !Shown("Files") && !Shown("Scripts") && !Shown("Sound") && !Shown("Listen"),
                        string.Join(", ", tools.Items.Cast<ToolStripItem>().Where(i => i.Available && i.Text.Length > 0).Select(i => i.Text)));
                    Check("Chat and Info are still there", Shown("Chat") && Shown("Info"));
                    Check("a banner says why", banner.Visible && bannerText.Text.StartsWith("View only") && bannerText.Text.Contains("web browser"), bannerText.Text);
                    try
                    {
                        using var bmp = new Bitmap(f.Width, f.Height);
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        var path = Path.Combine(outDir, "viewonly.png");
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine($"        picture: {path}");
                    }
                    catch { }
                }
                else
                {
                    Check("control is on", view.ControlEnabled && control.Visible && control.Checked);
                    Check("the System menu is there", sys.Visible);
                    Check("Files, Scripts, Sound and Listen are there", Shown("Files") && Shown("Scripts") && Shown("Sound") && Shown("Listen"));
                    Check("no view-only banner", !banner.Visible || !bannerText.Text.StartsWith("View only"));
                }
                // ---- talking (3.3): offered to BOTH — a browser caller can talk too — and a call's
                // pill and line fit on the status bar beside everything else, at this width.
                Check("Talk to the customer is on the Tools menu", Shown("Talk to the customer"));
                var pill = F<Pill>(f, "_talkPill");
                var line = F<Label>(f, "_voiceLabel");
                typeof(SessionForm).GetMethod("ShowTalkPill", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f, null);
                typeof(SessionForm).GetMethod("SetVoiceLine", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(f, new object[] { "they can hear you · asking to use their microphone", false });
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
                int widest = new[] { "● Talking — click to mute", "● Muted — click to unmute" }.Max(t => TextRenderer.MeasureText(t, pill.Font).Width);
                Check("the talk pill fits its longest text, measured", widest <= pill.Width, $"text {widest} px, pill {pill.Width} px");
                var bar = pill.Parent!;
                var others = bar.Controls.Cast<Control>().Where(c => c.Visible && c != pill && c != line && c is not Panel && c.Width > 0 && c.Dock != DockStyle.Fill).ToList();
                var clash = others.Where(c => c.Bounds.IntersectsWith(pill.Bounds) || c.Bounds.IntersectsWith(line.Bounds)).Select(c => c.GetType().Name + " " + c.Text).ToList();
                Check("the pill and the microphone line overlap nothing on the status bar", clash.Count == 0 && pill.Right <= line.Left, clash.Count > 0 ? string.Join("; ", clash) : $"pill {pill.Bounds}, line {line.Bounds}");
                try
                {
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    var path = Path.Combine(outDir, viewOnly ? "talking-viewonly.png" : "talking.png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"        picture: {path}");
                }
                catch { }
                f.Close();
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{pass} of {pass + fail} checks passed.");
        return fail == 0 ? 0 : 1;
    }
}
