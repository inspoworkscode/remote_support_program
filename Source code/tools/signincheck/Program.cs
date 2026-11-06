using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// signincheck - the console remembers the last username, so a technician types only the password.
//
// Technicians, 2026-09-30: "Everytime a technician has to sign in, they have to put their username
// and password." Asked of the REAL LoginForm, signing in to a PLATFORM OF ITS OWN (own port, own
// database in the temp folder, own tenant) — the live server on 8090 is not touched — and with
// LastSignIn pointed at a file of its own, so Clint's remembered name is not touched either.
//
//   dotnet run -c Release --project tools/signincheck/signincheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class SignInCheck
{
    private static int _pass, _fail;
    private const string Slug = "signintest";
    private const string User = "tech@testing.local";
    private const string Pw = "Harness-Pw-1";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        int code = 1;
        var ctx = new ApplicationContext();
        var starter = new System.Windows.Forms.Timer { Interval = 50 };
        starter.Tick += async (_, _) =>
        {
            starter.Stop();
            try { code = await Run(); }
            catch (Exception ex) { Console.WriteLine("signincheck FAILED: " + ex); }
            ctx.ExitThread();
        };
        starter.Start();
        Application.Run(ctx);
        return code;
    }

    private static async Task<int> Run()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-signincheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        LastSignIn.FilePath = Path.Combine(dataDir, "signin.json");
        TrustedSignIn.FilePath = Path.Combine(dataDir, "trust.json");   // never this PC's own
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server on 8090, its database and this PC's remembered name are not touched)");
        Console.WriteLine();

        var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Sign In Test Co", Slug);
        if (!created.Contains("Tenant key:", StringComparison.Ordinal)) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
        await RunPlatform(proj, dataDir, port, "approve", Slug);
        await RunPlatform(proj, dataDir, port, "add-tech", Slug, User, Pw, "Tess Tech");

        using var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var brand = new Branding { CompanyName = "Sign In Test Co", ServerUrl = $"ws://127.0.0.1:{port}" };

            Console.WriteLine("== the first sign-in on this PC ==");
            using (var f = Open(brand))
            {
                Check("with nothing remembered, the username is blank", Field<InputBox>(f, "_user")!.Value == "");
                Check("...and the cursor starts in it", Focused(f) == Field<InputBox>(f, "_user")!.Box, FocusName(f));

                // A wrong password must not be remembered — and nor must the name it was typed with,
                // or a typo would be what comes back tomorrow.
                Field<InputBox>(f, "_user")!.Value = User;
                Field<InputBox>(f, "_pass")!.Value = "wrong-password";
                await SignIn(f);
                Check("a sign-in that fails is refused", f.DialogResult != DialogResult.OK, Field<Label>(f, "_msg")!.Text);
                Check("...and remembers nothing", LastSignIn.For(brand.ServerUrl) == "" && !File.Exists(LastSignIn.FilePath),
                      $"\"{LastSignIn.For(brand.ServerUrl)}\"");

                Field<InputBox>(f, "_pass")!.Value = Pw;
                await SignIn(f);
                Check("the right password signs in", f.DialogResult == DialogResult.OK, Field<Label>(f, "_msg")!.Text);
                Check("...and the username is remembered", LastSignIn.For(brand.ServerUrl) == User, $"\"{LastSignIn.For(brand.ServerUrl)}\"");
            }

            var onDisk = File.Exists(LastSignIn.FilePath) ? File.ReadAllText(LastSignIn.FilePath) : "";
            Check("the password is not in the file", onDisk.Length > 0 && !onDisk.Contains(Pw, StringComparison.Ordinal)
                  && !onDisk.Contains("wrong-password", StringComparison.Ordinal), onDisk.Replace("\r", "").Replace("\n", " "));

            Console.WriteLine();
            Console.WriteLine("== the next sign-in ==");
            using (var f = Open(brand))
            {
                Check("the username is already filled in", Field<InputBox>(f, "_user")!.Value == User, $"\"{Field<InputBox>(f, "_user")!.Value}\"");
                Check("the password is empty", Field<InputBox>(f, "_pass")!.Value == "");
                Check("...and the cursor starts in the password", Focused(f) == Field<InputBox>(f, "_pass")!.Box, FocusName(f));
                Field<InputBox>(f, "_pass")!.Value = Pw;
                await SignIn(f);
                Check("typing only the password signs in", f.DialogResult == DialogResult.OK, Field<Label>(f, "_msg")!.Text);
            }

            Console.WriteLine();
            Console.WriteLine("== another server ==");
            using (var f = Open(new Branding { CompanyName = "Elsewhere", ServerUrl = "ws://127.0.0.1:1" }))
                Check("a different server is not offered this server's name", Field<InputBox>(f, "_user")!.Value == "",
                      $"\"{Field<InputBox>(f, "_user")!.Value}\"");

            // The code once a week (2026-10-02): what the server gives after a code, kept for the
            // next sign-in. The server's half — issuing, refusing a forged or stale one, the stamp —
            // is securitycheck's; this is the console's keeping of it.
            Console.WriteLine();
            Console.WriteLine("== the week's trust, as the console keeps it ==");
            Check("with a two-factor-off server nothing was given, so nothing is kept", !File.Exists(TrustedSignIn.FilePath));
            const string Token = "CfDJ8-pretend-trust-token_0123456789";
            TrustedSignIn.Remember(brand.ServerUrl, User, Token);
            Check("kept, it comes back for the same person on the same server", TrustedSignIn.For(brand.ServerUrl, User) == Token);
            Check("...typed in another case it is the same person", TrustedSignIn.For(brand.ServerUrl, User.ToUpperInvariant()) == Token);
            Check("...never for somebody else on this PC", TrustedSignIn.For(brand.ServerUrl, "someone.else@testing.local") == "");
            Check("...nor on another server", TrustedSignIn.For("ws://127.0.0.1:1", User) == "");
            var sealedText = File.ReadAllText(TrustedSignIn.FilePath);
            Check("the file never holds it as it is — Windows seals it to this user", !sealedText.Contains(Token, StringComparison.Ordinal) && sealedText.Length > 0);
            File.WriteAllText(TrustedSignIn.FilePath, sealedText.Replace("\"A", "\"B"));
            Check("a damaged file is no trust, not a crash", TrustedSignIn.For(brand.ServerUrl, User) == "");
            TrustedSignIn.Remember(brand.ServerUrl, User, Token);
            TrustedSignIn.Remember(brand.ServerUrl, User, "");
            Check("an empty one forgets it", TrustedSignIn.For(brand.ServerUrl, User) == "");

            Console.WriteLine();
            Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }
    }

    private static LoginForm Open(Branding brand)
    {
        var f = new LoginForm(brand);
        f.Show();
        f.Activate();
        for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
        return f;
    }

    /// <summary>Presses Sign In the way the button does, and waits for the answer.</summary>
    private static async Task SignIn(LoginForm f)
    {
        var m = typeof(LoginForm).GetMethod("DoLogin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)m.Invoke(f, null)!;
        for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }

    private static Control? Focused(Form f)
    {
        Control? c = f.ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c;
    }

    private static string FocusName(Form f) =>
        Focused(f) is { } c ? $"focus on {(c.Parent is InputBox ib && ib == Field<InputBox>(f, "_pass") ? "password" : c.Parent is InputBox ? "username" : c.GetType().Name)}" : "nothing focused";

    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    // ---- running a platform of our own (as devicelink does) ---------------------------------

    private static Process StartPlatform(string proj, string dataDir, int port)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> RunPlatform(string proj, string dataDir, int port, params string[] args)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private static ProcessStartInfo Psi(string proj, string dataDir, int port)
    {
        var psi = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        // Debug, as devicelink: the Release output is usually locked by the server Clint has running.
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
        psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        return psi;
    }

    private static async Task<bool> WaitForPort(int port, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
            catch { await Task.Delay(400); }
        }
        return false;
    }

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
