using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// PASSWORDS AT 600,000 ROUNDS (2026-10-07, roadmap 4.4, HANDOVER §72's "left for soon after" item 2; §111).
//
// Every new password is PBKDF2-SHA256 at 600,000 rounds, the rounds written in the salt ("600000$…").
// Every hash made before — a bare base64 salt, 120,000 rounds — still verifies, and is made again at
// the person's next sign-in, the one moment the server holds the password. Their security stamp does
// NOT move (that would sign them out everywhere for signing in). A wrong password upgrades nothing.
//
//   1. Passwords on its own: the format, the old formula worked out HERE independently, a bad salt;
//   2. a real sign-in on a platform of its own, against an account rewritten to the old format by SQL.
//
//   dotnet run -c Release --project tools/hashcheck/hashcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class HashCheck
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private const string Pw = "HashCheck!2026";

    /// <summary>The 120,000-round formula as it was written before 2026-10-07 — a copy on purpose, so the old
    /// hashes are checked against what they WERE, not against whatever Passwords says today.</summary>
    private static (string salt, string hash) OldFormat(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(h));
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("== 1. Passwords on its own ==");
        var (salt, hash) = Passwords.Create(Pw);
        Check("a new password says its rounds: 600000$…", salt.StartsWith("600000$") && Passwords.RoundsOf(salt) == 600_000, salt[..Math.Min(12, salt.Length)]);
        Check("it verifies, and a wrong one does not", Passwords.Verify(Pw, salt, hash) && !Passwords.Verify(Pw + "x", salt, hash));
        Check("it needs no upgrade", !Passwords.NeedsUpgrade(salt));
        var sw = Stopwatch.StartNew(); Passwords.Verify(Pw, salt, hash); sw.Stop();
        Console.WriteLine($"        (one verify at 600,000 rounds took {sw.ElapsedMilliseconds} ms here)");

        var (oldSalt, oldHash) = OldFormat(Pw);
        Check("an OLD hash (bare salt, 120,000 rounds) still verifies", Passwords.Verify(Pw, oldSalt, oldHash));
        Check("and a wrong password against it does not", !Passwords.Verify("nope-nope", oldSalt, oldHash));
        Check("it is read as 120,000 rounds and needs an upgrade", Passwords.RoundsOf(oldSalt) == 120_000 && Passwords.NeedsUpgrade(oldSalt));
        Check("a salt that is not one verifies nothing, and throws nothing",
            !Passwords.Verify(Pw, "abc$!!!", oldHash) && !Passwords.Verify(Pw, "!!!", oldHash) && !Passwords.Verify(Pw, "", "") && !Passwords.Verify(Pw, "12$" + oldSalt, oldHash));
        Check("an empty salt (no password set) needs no upgrade", !Passwords.NeedsUpgrade(""));

        // ---- 2. a real sign-in ----------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== 2. a real sign-in upgrades an old hash ==");
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-hashcheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Hash Company");
            var slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "old@example.com", Pw, "Olive Old", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }
        Check("(an account made today is at 600,000 rounds)", Text(dataDir, "SELECT Salt FROM Technicians WHERE Email = 'old@example.com'").StartsWith("600000$"));
        // As it would have been written before today.
        var (s1, h1) = OldFormat(Pw);
        Exec(dataDir, $"UPDATE Technicians SET Salt = '{s1}', Hash = '{h1}' WHERE Email = 'old@example.com'");
        var stamp = Text(dataDir, "SELECT SecurityStamp FROM Technicians WHERE Email = 'old@example.com'");

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            Check("a WRONG password is refused, and upgrades nothing",
                await Login(port, "old@example.com", "wrong-password-1") == null
                && Text(dataDir, "SELECT Salt FROM Technicians WHERE Email = 'old@example.com'") == s1);
            Check("the right one signs in against the old hash", await Login(port, "old@example.com", Pw) != null);
            var after = Text(dataDir, "SELECT Salt FROM Technicians WHERE Email = 'old@example.com'");
            Check("and the stored hash is now 600,000 rounds", after.StartsWith("600000$"), after[..Math.Min(12, after.Length)]);
            Check("the security stamp did NOT move (nobody signed out for signing in)",
                Text(dataDir, "SELECT SecurityStamp FROM Technicians WHERE Email = 'old@example.com'") == stamp);
            Check("the same password signs in again, against the new hash", await Login(port, "old@example.com", Pw) != null);
            Check("and a wrong one still does not", await Login(port, "old@example.com", Pw + "x") == null);
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the database, directly

    private static string Text(string dataDir, string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static void Exec(string dataDir, string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ plumbing (as removecheck)

    private static async Task<string?> Login(int port, string email, string password)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password });
        if (!lr.IsSuccessStatusCode) return null;
        var body = await lr.Content.ReadAsStringAsync();
        return body.Contains("\"token\"") ? body : null;
    }

    private static Process StartPlatform(string proj, string dataDir, int port)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
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
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        psi.Environment["RSP_Platform__PublicUrl"] = $"http://127.0.0.1:{port}";
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
