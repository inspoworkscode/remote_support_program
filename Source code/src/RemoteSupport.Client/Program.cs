using System.Windows.Forms;
using RemoteSupport.Shared;

namespace RemoteSupport.Client;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // --write-icon <path>: render this tenant's logo as an .ico file and exit. The installer
        // asks for it so the desktop and Start menu shortcuts can carry the customer's branding;
        // the shell reads that file when it draws the icon, long after this process is gone, so
        // the tray app's in-memory icon cannot serve it.
        //
        // Handled FIRST, before the single-instance mutex and before anything touches WinForms.
        // Two reasons, and both have bitten this codebase. Taking the mutex would make this fail
        // silently whenever the tray app happened to be running — which during an upgrade is
        // exactly when the installer calls it. And an installer runs ELEVATED: a child that went
        // on to show a window would inherit that token and become the elevated tray app whose
        // UIPI rank silently discarded every injected keystroke (§4 of the handover). This path
        // writes a file and returns, and never reaches Application.Run.
        int flag = Array.IndexOf(args, "--write-icon");
        if (flag >= 0)
        {
            if (flag + 1 >= args.Length) { Environment.ExitCode = 2; return; }
            var ok = RemoteSupport.UI.BrandIcon.WriteIcoFile(Branding.Load(), args[flag + 1]);
            // An exit code, because a WinExe has no console to write a reason to and the installer
            // needs to tell "no logo to render" from "it worked".
            Environment.ExitCode = ok ? 0 : 1;
            return;
        }

        // tools/oneexecheck's view of a downloaded copy, with no window: what it works out, what it
        // carries, and the folder Install would run from. Like --write-icon, handled before anything
        // else and never reaching Application.Run.
        if (args.Length >= 2 && args[0] is "--identify" or "--unpack" or "--stage-install")
        {
            Environment.ExitCode = Diagnose(args[0], args[1]);
            return;
        }

        // A DOWNLOADED copy (2026-09-30): the one signed .exe every company's customers get, with no
        // branding beside it. It works out whose it is, fetches their branding, and asks "get help
        // now, or install?" — see Launcher. An installed or unzipped copy never comes here.
        //
        // The single-instance mutex is only LOOKED AT here, not taken: an install ends by starting
        // the installed tray app, and a mutex held by this window would make it quietly exit.
        Branding? downloaded = null;
        if (Launcher.IsDownloadedCopy)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (Mutex.TryOpenExisting("RemoteSupport.Client.SingleInstance", out var running))
            {
                running.Dispose();
                MessageBox.Show("Remote support is already running on this PC. Look for its icon by the clock.", "Remote support",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = Launcher.Identify(Environment.ProcessPath ?? "", () => RemoteSupport.UI.AskCompanyDialog.Ask());
            if (id == null)
            {
                MessageBox.Show("This support app could not tell which IT company it is from. Download it again from their link, or ask them for their code.",
                    "Remote support", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var (fetched, error) = Launcher.FetchBrandingAsync(id).GetAwaiter().GetResult();
            if (fetched == null)
            {
                MessageBox.Show(error, "Remote support", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var installedExe = Launcher.InstalledExe(fetched);
            // The free version (2026-10-06): add this computer, with its password — no "Get help now",
            // because nobody at a free computer is ever asked anything. See FreeInstallForm.
            if (fetched.Free)
            {
                using var add = new FreeInstallForm(fetched, id.Slug, id.Server, File.Exists(installedExe));
                add.ShowDialog();
                return;
            }
            // An Update starts with who it was installed for (2026-10-23): the same details keep their id, so the server
            // does not put that name back over a later support request's.
            using (var choose = new LauncherForm(fetched, id.Slug, ClientPayload.Present,
                       File.Exists(installedExe) && !string.Equals(installedExe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                       GivenPerson.Load(Path.GetDirectoryName(installedExe)!)))
            {
                choose.ShowDialog();
                if (choose.Result != LauncherForm.Outcome.RunNow) return;
            }
            Launcher.PrepareRunNow(id.Slug);
            downloaded = fetched;
        }

        // --after <pid>: this app restarting itself to show the company's new look (MainForm.RestartForBranding,
        // 2026-10-07). The old one is still closing and still holds the single-instance mutex; wait for it, or this
        // copy would find it taken and quietly leave nothing in the tray.
        int after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var oldPid))
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); old.WaitForExit(15000); } catch { /* already gone */ }

        // single instance
        using var mutex = new Mutex(true, "RemoteSupport.Client.SingleInstance", out var isNew);
        if (!isNew) return;

        if (downloaded == null)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }

        var brand = downloaded ?? Branding.Load();
        // An INSTALLED copy on a free computer (2026-10-06) has no tray app to be: nothing of ours stays
        // on its screen. Opened by hand, it says how this computer is reached and how to change its
        // password; at logon (a leftover Run entry) it says nothing at all.
        if (brand.Free)
        {
            if (!args.Contains("--startup") && !args.Contains("--tray"))
                MessageBox.Show(brand.Basic
                    ? $"This computer can be reached from {brand.CompanyName}'s console — with its unattended password, or, without one, "
                      + "by asking whoever is here. To change that, run the program you downloaded again."
                    : $"This computer can be reached from {brand.CompanyName}'s console, with its unattended password. "
                      + "To change the password, run the program you downloaded again.", brand.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var form = new MainForm(brand);
        bool startHidden = args.Contains("--tray") || args.Contains("--startup");
        if (startHidden)
        {
            // Realise the window handle (so tray/link callbacks can BeginInvoke) then hide.
            form.WindowState = FormWindowState.Minimized;
            form.ShowInTaskbar = false;
            form.Show();
            form.Hide();
        }
        Application.Run(startHidden ? new ApplicationContextHidden(form) : new ApplicationContextVisible(form));
    }

    /// <summary>
    /// The hidden switches tools/oneexecheck drives the REAL packed .exe with:
    /// <c>--identify out.json</c> (whose it is, from where, and its branding fetched),
    /// <c>--unpack dir</c> (what it carries) and <c>--stage-install dir</c> (the folder Install runs
    /// Install-Client.ps1 from). No window, no question: a copy that cannot tell whose it is fails.
    /// </summary>
    private static int Diagnose(string what, string target)
    {
        try
        {
            switch (what)
            {
                case "--unpack":
                    if (!ClientPayload.Present) return 1;
                    ClientPayload.ExtractTo(target);
                    return 0;
                case "--identify":
                case "--stage-install":
                {
                    var id = Launcher.Identify(Environment.ProcessPath ?? "", () => null);
                    var (brand, error) = id == null ? (null, "could not tell whose copy this is") : Launcher.FetchBrandingAsync(id).GetAwaiter().GetResult();
                    if (what == "--stage-install")
                    {
                        if (id == null || brand == null) return 1;
                        Launcher.StageInstall(id.Slug, target);
                        return 0;
                    }
                    File.WriteAllText(target, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        slug = id?.Slug, server = id?.Server, how = id?.How, error,
                        company = brand?.CompanyName, product = brand?.ProductName, serverUrl = brand?.ServerUrl,
                        hasTenantKey = !string.IsNullOrEmpty(brand?.TenantKey), payload = ClientPayload.Present,
                        defaultServer = ClientPayload.DefaultServer(), cache = Launcher.CacheRoot
                    }));
                    return brand != null ? 0 : 1;
                }
            }
        }
        catch (Exception ex) { try { File.WriteAllText(target + ".error.txt", ex.ToString()); } catch { } }
        return 1;
    }
}

internal sealed class ApplicationContextVisible : ApplicationContext
{
    public ApplicationContextVisible(Form f) { f.Show(); MainForm = f; f.FormClosed += (_, _) => ExitThread(); }
}
internal sealed class ApplicationContextHidden : ApplicationContext
{
    public ApplicationContextHidden(Form f) { MainForm = f; f.FormClosed += (_, _) => ExitThread(); }
}
