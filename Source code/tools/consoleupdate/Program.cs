using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// THE CONSOLE UPDATES ITSELF (2026-10-16, auto-update stage E, Technician/ConsoleUpdate.cs).
//
// Two REAL consoles, published the way build.ps1 publishes them and stamped N and N+1; a platform BUILT as a release
// that hands out N+1; N installed the way a technician's download installs itself. Then:
//   - N, started, asks its server before the sign-in window, downloads N+1, and — N+1 being unsigned here — REFUSES it
//     before running it: N stays, the version is remembered, and the sign-in window opens as always;
//   - started again it does not even download it;
//   - N+1's --install-update (what N starts once N+1 IS signed): puts itself in N's place, N renamed aside and kept until
//     N+1 reaches its sign-in window — then it is installed, and N's copy is tidied away at the next start.
// The signed half — N accepting a signed N+1 — and a new console that never reaches its sign-in window (and is put back)
// are the hardware test (TEST-3.6.md DE).
//
//   dotnet run -c Release --project tools/consoleupdate/consoleupdate.csproj
// ---------------------------------------------------------------------------------------------

internal static class ConsoleUpdateCheck
{
    private const string VN = "2026.10.16.1600", VNext = "2026.10.16.1700";
    private const string Company = "Console Update Co";
    private static int _pass, _fail, _port;
    private static string _root = "", _work = "", _base = "", _slug = "";
    private static readonly List<Process> _started = new();

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main()
    {
        _root = FindRepoRoot() ?? "";
        if (_root.Length == 0) { Console.WriteLine("Could not find the repository root."); return 1; }
        _work = Path.Combine(Path.GetTempPath(), "rs-consoleupdate-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_work);
        _port = FreePort(); _base = $"http://127.0.0.1:{_port}";
        var payload = Path.Combine(_work, "payloads", "technician");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(Path.Combine(_work, "payloads", "client"));
        Process? server = null;
        try
        {
            Head($"Two real consoles, {VN} and {VNext} (a few minutes)");
            var n = await Publish(VN);
            var next = await Publish(VNext);
            Check("both published, each stamped", n != null && next != null && UpdateTrust.FileVersion(n) == VN && UpdateTrust.FileVersion(next!) == VNext,
                $"{(n == null ? "-" : UpdateTrust.FileVersion(n))} / {(next == null ? "-" : UpdateTrust.FileVersion(next))}");
            if (n == null || next == null) return 1;
            File.Copy(next, Path.Combine(payload, "RemoteSupport.Technician.exe"));
            File.WriteAllText(Path.Combine(payload, "console-exe.ready"), "consoleupdate");

            var created = await RunPlatform("create-tenant", Company);
            _slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
            using (var c = HarnessDb.Connect($"Data Source={Path.Combine(_work, "platform.db")};Pooling=False"))
            {
                c.Open(); using var cmd = c.CreateCommand();
                cmd.CommandText = HarnessDb.Portable($"UPDATE Tenants SET NotBilled = TRUE WHERE Slug = '{_slug}'");
                cmd.ExecuteNonQuery();
            }
            server = StartPlatform();
            if (!await WaitForPort(_port, 180000)) { Check("the platform starts", false); return 1; }
            using (var hc = new HttpClient())
            {
                var ask = await hc.GetAsync($"{_base}/update/console.json?slug={_slug}");
                Check($"the server offers {VNext} to this company's consoles", ask.StatusCode == HttpStatusCode.OK && (await ask.Content.ReadAsStringAsync()).Contains(VNext));
            }

            Head($"{VN}, installed, starting");
            var home = Path.Combine(_work, "home");
            var links = Path.Combine(_work, "shortcuts");
            var dl = Path.Combine(_work, "Downloads", $"{_slug}-console.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(dl)!);
            File.Copy(n, dl);
            File.WriteAllText(dl + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl={_base}/tech/{_slug}\r\nHostUrl={_base}/downloads/{_slug}/technician.exe\r\n");
            var setUp = Run(dl, home, links, "--set-up", Path.Combine(_work, "setup.json"));
            setUp.WaitForExit(120000);
            var installed = Path.Combine(home, _slug, "RemoteSupport.Technician.exe");
            Check($"{VN} set itself up, as a technician's download does", setUp.ExitCode == 0 && File.Exists(installed) && UpdateTrust.FileVersion(installed) == VN);

            var traceFrom = TraceLength();
            var first = Run(installed, home, links);
            var title = await WindowTitle(first, 90);
            var downloaded = Path.Combine(home, _slug, "update", VNext, "RemoteSupport.Technician.exe");
            Check($"before the sign-in window it asked, and downloaded {VNext} beside itself", File.Exists(downloaded)
                && File.ReadAllBytes(downloaded).AsSpan().SequenceEqual(File.ReadAllBytes(next)));
            var trace = TraceSince(traceFrom);
            Check($"...and REFUSED it — not signed by us — without running it", trace.Contains($"update {VNext}: REFUSED") && trace.Contains("not signed"),
                Regex.Match(trace, $"update {Regex.Escape(VNext)}: [^\\r\\n]*").Value);
            Check($"{VN} stays, and its sign-in window opens as always", UpdateTrust.FileVersion(installed) == VN && title == $"{Company} — Technician", $"\"{title}\"");
            var state = Path.Combine(home, _slug, "update", "state.json");
            Check("...the refused version remembered", File.Exists(state) && File.ReadAllText(state).Contains(VNext));
            Kill(first);

            if (File.Exists(downloaded)) File.Delete(downloaded);
            var again = Run(installed, home, links);
            title = await WindowTitle(again, 60);
            Check("started again: not even downloaded — it gave up on that version", title == $"{Company} — Technician" && !File.Exists(downloaded), $"\"{title}\"");
            Kill(again);

            Head($"{VNext} putting itself in place (what {VN} starts once it IS signed)");
            var fresh = Path.Combine(_work, "fresh", "RemoteSupport.Technician.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
            File.Copy(next, fresh);
            var nHash = Sha(installed);
            var installer = Run(fresh, home, links, "--install-update", installed, "999999");
            // It starts the new console and waits for it to reach its sign-in window.
            Process? newConsole = null;
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until && newConsole == null)
            {
                newConsole = Process.GetProcessesByName("RemoteSupport.Technician").FirstOrDefault(p => Safe(() => string.Equals(p.MainModule?.FileName, installed, StringComparison.OrdinalIgnoreCase)));
                await Task.Delay(300);
            }
            Check($"{VNext} is in {VN}'s place and started from there", newConsole != null && UpdateTrust.FileVersion(installed) == VNext);
            Check($"...{VN} kept aside, byte for byte, while it is not yet known to start", File.Exists(installed + ".old") && Sha(installed + ".old") == nHash);
            installer.WaitForExit(90000);
            Check("...it reached its sign-in window: installed (the installing copy says so and leaves)", installer.HasExited && installer.ExitCode == 0,
                installer.HasExited ? $"exit {installer.ExitCode}" : "still waiting");
            if (newConsole != null)
            {
                _started.Add(newConsole);
                title = await WindowTitle(newConsole, 30);
                Check("...and the new console's sign-in window is up", title == $"{Company} — Technician", $"\"{title}\"");
                Check($"...{VN}'s copy left alone while it was being installed (a rollback would need it)", File.Exists(installed + ".old"));
                Kill(newConsole);
            }
            var third = Run(installed, home, links);
            title = await WindowTitle(third, 60);
            Check($"at its next start {VNext} tidies {VN}'s copy away", title == $"{Company} — Technician" && !File.Exists(installed + ".old") && UpdateTrust.FileVersion(installed) == VNext);
            Kill(third);
        }
        finally
        {
            foreach (var p in _started) Kill(p);
            try { if (server != null && !server.HasExited) server.Kill(true); } catch { }
            await Task.Delay(500);
            try { Directory.Delete(_work, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the consoles

    private static Process Run(string exe, string home, string links, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["RSP_CONSOLE_HOME"] = home;
        psi.Environment["RSP_CONSOLE_SHORTCUTS"] = links;
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); p.WaitForExit(5000); } catch { } Thread.Sleep(300); }

    private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

    /// <summary>The title of the process's main window once it has one — the sign-in window, here.</summary>
    private static async Task<string> WindowTitle(Process p, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !p.HasExited)
        {
            p.Refresh();
            // The sign-in window — not the few seconds of "Updating…" that may come first.
            if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.EndsWith("— Technician")) return p.MainWindowTitle;
            await Task.Delay(300);
        }
        p.Refresh();
        return p.HasExited ? $"(exited {p.ExitCode})" : $"(no sign-in window; showing \"{p.MainWindowTitle}\")";
    }

    private static async Task<string?> Publish(string version)
    {
        var (args, _) = await Ps($". '{Path.Combine(_root, "tools", "BuildVersion.ps1")}'; (VersionArgs '{version}') -join ' '");
        var defaults = Path.Combine(_work, $"console-defaults-{version}.json");
        File.WriteAllText(defaults, $"{{\"server\":\"{_base}\"}}");
        var outDir = Path.Combine(_work, "published-" + version);
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "publish", Path.Combine(_root, "src", "RemoteSupport.Technician", "RemoteSupport.Technician.csproj"),
                     "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
                     "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none",
                     $"-p:ConsoleDefaults={defaults}", "-o", outDir, "--nologo", "-v", "quiet" }
                     .Concat(args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(a => a.StartsWith("-p:"))))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) { Console.WriteLine(outp + await err); return null; }
        var exe = Path.Combine(outDir, "RemoteSupport.Technician.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string Sha(string f) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));

    private static string TracePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheRemoteSupportProgram", "technician-trace.log");
    private static long TraceLength() => File.Exists(TracePath) ? new FileInfo(TracePath).Length : 0;
    private static string TraceSince(long from)
    {
        if (!File.Exists(TracePath)) return "";
        using var s = new FileStream(TracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (from > s.Length) from = 0;
        s.Seek(from, SeekOrigin.Begin);
        return new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }

    // ------------------------------------------------------------------ the platform

    private static Process StartPlatform()
    {
        var psi = Psi();
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> RunPlatform(params string[] args)
    {
        var psi = Psi();
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private static ProcessStartInfo Psi()
    {
        var (args, _) = Ps($". '{Path.Combine(_root, "tools", "BuildVersion.ps1")}'; (VersionArgs '{VNext}') -join ' '").GetAwaiter().GetResult();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Versioned");
        foreach (var a in args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(a => a.StartsWith("-p:")))
            psi.ArgumentList.Add("--property:" + a["-p:".Length..]);
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(Path.Combine(_root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"));
        psi.Environment["RSP_Platform__DataDirectory"] = _work;
        HarnessDb.Use(psi.Environment, _work);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
        psi.Environment["RSP_Platform__PublicUrl"] = _base;
        psi.Environment["RSP_Platform__ClientPayloadDir"] = Path.Combine(_work, "payloads", "client");
        psi.Environment["RSP_Platform__TechnicianPayloadDir"] = Path.Combine(_work, "payloads", "technician");
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        return psi;
    }

    private static async Task<(string Out, string Err)> Ps(string script)
    {
        var psi = new ProcessStartInfo("powershell") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e);
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
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
