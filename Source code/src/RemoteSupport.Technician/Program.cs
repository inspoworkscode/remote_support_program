using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // tools/consoleexecheck drives the REAL published .exe through this: the whole first run of a
        // downloaded copy, no window and no question, its answer written to a file.
        if (args.Length == 2 && args[0] == "--set-up") return SetUpForHarness(args[1]);

        // Auto-update (2026-10-16, stage E): the NEW console putting itself in place of the old one, then gone. No window.
        if (args.Length >= 1 && args[0] == ConsoleUpdate.InstallSwitch) return ConsoleUpdate.Install(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // The GUEST console (3.9, step 3): this same .exe from /guest, for somebody with no account. It
        // belongs to no company, so it is asked about BEFORE the downloaded-copy set-up, which would
        // otherwise try to work out whose console it is and ask for a company code.
        if (GuestForm.GuestServer(args, Environment.ProcessPath) is { } guestServer)
        {
            Application.Run(new GuestForm(guestServer));
            return 0;
        }

        // A DOWNLOADED console (2026-10-01): the one .exe every company's technicians get, with no
        // branding beside it. It sets itself up for this Windows user and starts that copy.
        if (ConsoleDownload.IsDownloadedCopy && Environment.ProcessPath is { } downloaded)
            return ConsoleDownload.Run(downloaded);

        Trace.Write($"console build {File.GetLastWriteTime(Environment.ProcessPath ?? ""):yyyy-MM-dd HH:mm} — trace file: {Trace.Path_}");

        var brand = Branding.Load();
        // Before the first window: the company's logo on every window, not only the main one.
        if (!BrandIcon.UseForEveryWindow(brand)) Trace.Write("icon: the company's icon is not the default for this console's windows");
        // Auto-update (2026-10-16, stage E): an installed console takes its company's newer release before anybody signs in.
        if (ConsoleUpdate.AtStart(brand)) return 0;
        using var login = new LoginForm(brand);
        // This build reached its sign-in window: the copy that just installed it (if one did) may stop watching.
        ConsoleUpdate.SayStarted();
        if (login.ShowDialog() != DialogResult.OK || login.Link == null) return 0;

        // Stage 3.6.9: the technician's OWN light or dark, chosen in the portal and stored against
        // their account, wins over the company's TechnicianTheme. Applied here — after sign-in,
        // before the first window is built — because it is only known once we know who signed in,
        // and every form takes its Theme at construction.
        //
        // RSP_THEME still beats both. It is a developer's override and somebody who has set it is
        // asking to see one palette regardless of whose account they used.
        //
        // The login dialog above is drawn in the COMPANY's theme, unavoidably: nobody has said who
        // they are yet. That is one window, once, and the alternative is asking for a name before
        // drawing anything.
        if (Theme.Override is null)
        {
            var mine = login.Link.Appearance;
            if (mine == "light") Theme.Override = ThemeMode.Light;
            else if (mine == "dark") Theme.Override = ThemeMode.Dark;
        }

        var dashboard = new DashboardForm(brand, login.Link);
        Application.Run(dashboard);

        // Exit or Sign out: the server forgets this console's token, so signing in on another
        // computer next is not "somewhere else" and asks no extra code (one sign-in at a time).
        // Signed out BY the server, the token is already gone and this changes nothing.
        try { login.Link.LogoutAsync().Wait(TimeSpan.FromSeconds(4)); } catch { }

        // Sign out: start again at the sign-in window, in a fresh process — see DashboardForm.SignOut.
        if (dashboard.SignedOut && Environment.ProcessPath is { } exe)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }); }
            catch (Exception ex) { Trace.Write("sign out: could not start the sign-in window again: " + ex.Message); }
            // And make sure THIS one is gone: a session's worker thread still running would keep
            // the old process — and its sockets, still signed in — alive beside the sign-in window.
            Environment.Exit(0);
        }
        return 0;
    }

    private static int SetUpForHarness(string outFile)
    {
        var exe = Environment.ProcessPath ?? "";
        var id = ConsoleDownload.Identify(exe, () => null);
        var (installed, error) = id == null ? (null, "could not tell whose console this is") : ConsoleDownload.SetUpAsync(id, exe).GetAwaiter().GetResult();
        File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            slug = id?.Slug, server = id?.Server, how = id?.How, exe = installed, error,
            defaultServer = ConsoleDownload.DefaultServer(), home = ConsoleDownload.Home
        }));
        return installed != null ? 0 : 1;
    }
}
