using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.Technician;

namespace RemoteSupport.Tools.TrayProbe;

// ---------------------------------------------------------------------------------------------
// Proves that the technician console closes to the tray, comes back, and can still actually be
// exited — by driving the REAL DashboardForm and pressing the REAL X button.
//
// WHY THIS IS WORTH A HARNESS. A window that hides instead of closing has two failure modes and
// they are opposites:
//
//   * it does not hide at all, so an accidental click still kills the console and stops calls
//     ringing — the thing this feature exists to prevent;
//   * it hides from EVERYTHING, including Windows shutting down and its own Exit menu, so the
//     console cannot be closed at all and has to be killed from Task Manager.
//
// And the X button is not Form.Close(). The X sends WM_SYSCOMMAND/SC_CLOSE and produces
// CloseReason.UserClosing; Close() leaves it None. Testing with Close() would exercise the one
// path the handler deliberately does NOT intercept, pass, and prove nothing whatsoever. That
// exact mistake hid a real bug in the session window's pop-out once already (HANDOVER §7).
//
//   trayprobe
//
// It logs in for real, so the server must be running. It opens and closes a console window; it
// touches no customer machine and starts no session.
internal static class Program
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static int _failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "pass" : "FAIL")}  {what}{(detail.Length > 0 ? "   — " + detail : "")}");
    }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        int exit = 1;
        var ctx = new ApplicationContext();
        var starter = new System.Windows.Forms.Timer { Interval = 50 };
        starter.Tick += async (_, _) =>
        {
            starter.Stop();
            try { exit = await Run(); }
            catch (Exception ex) { Console.WriteLine("trayprobe FAILED: " + ex); exit = 1; }
            ctx.ExitThread();
        };
        starter.Start();
        Application.Run(ctx);
        return exit;
    }

    private static async Task<int> Run()
    {
        var brand = Branding.Load();
        var link = new TechLink(brand.ServerUrl);
        var creds = RsCredentials.Load();
        var step = await link.LoginAsync(creds.Email, creds.Password);
        if (step.Next == "code" && creds.Code() is { } code) step = await link.CodeAsync(step.Pending, code);   // two-factor (HANDOVER §72)
        var (ok, why) = (step.Ok, step.Error.Length > 0 ? step.Error : step.Next);
        if (!ok) { Console.WriteLine($"login failed: {why} — is the Platform server running?"); return 2; }
        Console.WriteLine($"logged in as {link.DisplayName}");

        var cts = new CancellationTokenSource();
        _ = link.RunAsync(cts.Token);

        var form = new DashboardForm(brand, link);
        form.Show();
        await Settle(form);

        Console.WriteLine();
        Console.WriteLine("=== closing to the tray ===");
        Check("the console is open to begin with", form.Visible);
        Check("a tray icon exists and is visible", TrayVisible(form));

        // The REAL X button. Not Close().
        SendMessage(form.Handle, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
        await Settle(form);

        Check("the X button did NOT destroy the window", !form.IsDisposed,
              form.IsDisposed ? "an accidental click would end the session and stop calls ringing" : "");
        Check("the window is hidden", !form.IsDisposed && !form.Visible);
        Check("the tray icon is still there to click", !form.IsDisposed && TrayVisible(form));

        Console.WriteLine();
        Console.WriteLine("=== coming back from the tray ===");
        Invoke(form, "ShowWindow");
        await Settle(form);
        Check("the console reopens", !form.IsDisposed && form.Visible);
        Check("and not as a minimised box", !form.IsDisposed && form.WindowState != FormWindowState.Minimized,
              form.IsDisposed ? "" : "state is " + form.WindowState);

        Console.WriteLine();
        Console.WriteLine("=== a message arriving while hidden ===");
        // Hide it again and fire the alert path. This cannot assert that a balloon appeared —
        // Windows owns that — but it can prove the path does not throw with the window hidden,
        // which is the only way this would silently stop notifying anybody.
        SendMessage(form.Handle, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
        await Settle(form);
        bool alerted = true;
        try { Invoke(form, "TrayAlert", "trayprobe", "a customer would be asking for help", ToolTipIcon.Info); }
        catch (Exception ex) { alerted = false; Console.WriteLine("        " + ex.GetType().Name + ": " + ex.Message); }
        Check("alerting from the tray works while hidden", alerted);

        Console.WriteLine();
        Console.WriteLine("=== leaving for real ===");
        Invoke(form, "ExitForReal");
        await Settle(form);
        Check("Exit really closes it", form.IsDisposed,
              form.IsDisposed ? "" : "the console cannot be closed except by Task Manager");

        cts.Cancel();
        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "PASS: the console hides on X, comes back, alerts while hidden, and can still be exited."
            : $"FAIL: {_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Pumps messages for a moment: hiding, showing and closing all happen on the loop.</summary>
    private static async Task Settle(Form form)
    {
        for (int i = 0; i < 12; i++) { Application.DoEvents(); await Task.Delay(60); }
    }

    private static bool TrayVisible(Form form)
    {
        var f = typeof(DashboardForm).GetField("_tray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return f?.GetValue(form) is NotifyIcon n && n.Visible;
    }

    /// <summary>
    /// Calls one of the form's private methods. Reflection rather than making them public: these
    /// are the console's own business, and a probe is not a reason to widen an API.
    /// </summary>
    private static void Invoke(Form form, string method, params object[] args)
    {
        var m = typeof(DashboardForm).GetMethod(method,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (m == null) throw new MissingMethodException($"DashboardForm.{method} is gone — this probe is out of date");
        m.Invoke(form, args);
    }
}
