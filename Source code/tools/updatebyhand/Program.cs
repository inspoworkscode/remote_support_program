using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// THE ONE UPDATE BY HAND (2026-10-16, auto-update stage F, Shared/UpdateByHand.cs).
//
// The script the console's Scripts tool fills in for a machine installed before auto-update. Run for real here — but
// ONLY with files it must refuse: a passing run would install onto this PC's own support software. So:
//   - it is valid PowerShell, names the company's server and our organisation;
//   - an unsigned download, Microsoft's signed program, and our signed program with one byte changed are each REFUSED
//     before anything is unpacked or touched, with the reason said;
//   - it never fetches from anywhere but the server it was written for.
// The accepting half — a signed release installed through the new agent's --apply-update — is the hardware test.
//
//   dotnet run -c Release --project tools/updatebyhand/updatebyhand.csproj
// ---------------------------------------------------------------------------------------------

internal static class UpdateByHandCheck
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private static int Main()
    {
        var root = FindRepoRoot()!;
        var work = Path.Combine(Path.GetTempPath(), "rs-updatebyhand-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(work);
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteSupport-Update");
        bool hadProgramData = Directory.Exists(programData);
        try
        {
            Console.WriteLine("== The script ==");
            var script = UpdateByHand.Script("http://127.0.0.1:1234");
            var parsed = ParseErrors(script, work);
            Check("it is valid PowerShell (Windows PowerShell's own parser)", parsed.Length == 0, parsed);
            Check("it fetches from the company's own server, and checks for OUR organisation", script.Contains("$server = 'http://127.0.0.1:1234'")
                && script.Contains($"$publisher = '{UpdateTrust.Publisher}'") && script.Contains("C=ZA"));
            Check("it installs through the new agent's --apply-update (which rolls back by itself)", script.Contains("'--apply-update'"));

            Console.WriteLine();
            Console.WriteLine("== Refusals, run for real ==");
            var host = Path.Combine(root, "dist", "platform-linux", "payloads", "client", "RemoteSupport.Host.exe");
            var tampered = Path.Combine(work, "tampered.exe");
            File.Copy(host, tampered);
            var b = File.ReadAllBytes(tampered); b[b.Length / 2] ^= 0xFF; File.WriteAllBytes(tampered, b);
            var cases = new (string what, string file, string expect)[]
            {
                ("an unsigned program", Environment.ProcessPath!, "is not validly signed"),
                ("Microsoft's validly signed program", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), "Microsoft"),
                ("our signed program with one byte changed", tampered, "is not validly signed"),
            };
            foreach (var (what, file, expect) in cases)
            {
                using var server = new Server(File.ReadAllBytes(file));
                var (code, output) = RunScript(UpdateByHand.Script(server.Base), work);
                Check($"{what}: REFUSED, nothing unpacked", code == 1 && output.Contains("REFUSED") && output.Contains(expect)
                    && output.Contains("Nothing was changed") && !Directory.EnumerateDirectories(programData, "files-*").Any(),
                    output.Split('\n').FirstOrDefault(l => l.Contains("REFUSED"))?.Trim() ?? output.Trim());
                Check($"...fetched from the server it was written for, once", server.Requests == 1 && server.Paths.All(p => p == "/update/client.exe"),
                    string.Join(", ", server.Paths));
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
            if (!hadProgramData) try { Directory.Delete(programData, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>What Windows PowerShell 5.1 — the one on every customer machine — says is wrong with the script, or "".</summary>
    private static string ParseErrors(string script, string work)
    {
        var file = Path.Combine(work, "parse-me.ps1");
        File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{file}', [ref]$null, [ref]$e); $e | ForEach-Object {{ $_.Message }}" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    private static (int, string) RunScript(string script, string work)
    {
        var file = Path.Combine(work, "update-this-machine.ps1");
        File.WriteAllText(file, script, new System.Text.UTF8Encoding(true));
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NonInteractive", "-File", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120000);
        return (p.ExitCode, o.Result + e.Result);
    }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _l = new();
        public readonly string Base;
        public int Requests;
        public readonly List<string> Paths = new();
        public Server(byte[] body)
        {
            var port = FreePort();
            Base = $"http://127.0.0.1:{port}";
            _l.Prefixes.Add(Base + "/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _l.GetContextAsync(); } catch { return; }
                    Requests++; lock (Paths) Paths.Add(c.Request.Url!.AbsolutePath);
                    c.Response.ContentLength64 = body.Length;
                    await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
            });
        }
        public void Dispose() { try { _l.Stop(); } catch { } }
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
