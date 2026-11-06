using System.Diagnostics;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// ONLY OUR SIGNED, NEWER FILES INSTALL (2026-10-16, auto-update stage B, Shared/UpdateTrust.cs).
//
// The server is not trusted to say what is safe; the signature is. With REAL files, no test mode:
//   - our signed program and our signed script from dist\ pass;
//   - a Microsoft-signed program (dotnet.exe — valid, trusted, not us) is refused for its signer;
//   - a certificate made on this PC with OUR organisation and country in it is refused (its chain leads nowhere
//     trusted) — the name alone is worth nothing;
//   - one byte changed in our signed program, or one character in our signed script, is refused;
//   - an unsigned program is refused;
//   - our signed program that is not a released build (1.0.0.0), or not newer than what is installed, or not the
//     version it was offered as, is refused — when dist\ holds a STAMPED signed build (build.ps1 after 2026-10-16) the
//     positive case is proved too; before that it is said, not passed.
//
//   dotnet run -c Release --project tools/updatetrust/updatetrust.csproj
//   (needs dist\platform-linux\payloads\client from a SIGNED build — Publish-Platform.ps1 or build.ps1 -Platform)
// ---------------------------------------------------------------------------------------------

internal static class UpdateTrustCheck
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private static int Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var payload = Path.Combine(root, "dist", "platform-linux", "payloads", "client");
        var host = Path.Combine(payload, "RemoteSupport.Host.exe");
        var script = Path.Combine(payload, "healthcheck", "Install-HealthCheck.ps1");
        if (!File.Exists(host) || !File.Exists(script)) { Console.WriteLine($"No signed build in {payload}. Build one first (Publish-Platform.ps1 or build.ps1 -Platform)."); return 1; }

        var work = Path.Combine(Path.GetTempPath(), "rs-updatetrust-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(work);
        try
        {
            Console.WriteLine("== Our own files ==");
            var h = UpdateTrust.Signature(host);
            Check("our signed program passes", h.Ok, h.Why);
            var s = UpdateTrust.Signature(script);
            Check("our signed script passes (a signature in a comment block, checked the same way)", s.Ok, s.Why);

            Console.WriteLine();
            Console.WriteLine("== Everybody else's ==");
            var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            var ms = UpdateTrust.Signature(dotnet);
            Check("a program validly signed by Microsoft is refused — signed, trusted, not us", !ms.Ok && ms.Why.Contains("Microsoft Corporation"), ms.Why);

            var unsigned = Path.Combine(work, "unsigned.exe");
            File.Copy(Environment.ProcessPath!, unsigned);
            var un = UpdateTrust.Signature(unsigned);
            Check("an unsigned program is refused", !un.Ok && un.Why.Contains("not signed"), un.Why);

            var tampered = Path.Combine(work, "RemoteSupport.Host.exe");
            File.Copy(host, tampered);
            var bytes = File.ReadAllBytes(tampered);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);
            var tp = UpdateTrust.Signature(tampered);
            Check("our signed program with ONE byte changed is refused", !tp.Ok && tp.Why.Contains("changed"), tp.Why);

            var tscript = Path.Combine(work, "Install-HealthCheck.ps1");
            File.WriteAllText(tscript, File.ReadAllText(script).Replace("Health", "Hea1th"));
            var ts = UpdateTrust.Signature(tscript);
            Check("our signed script with one word changed is refused", !ts.Ok, ts.Why);

            var impostor = Path.Combine(work, "impostor.exe");
            File.Copy(Environment.ProcessPath!, impostor);
            var (signed, how) = SignWithHomeMadeCertificate(impostor);
            if (!signed) Check("a certificate made here with OUR name is refused", false, "could not make one to try: " + how);
            else
            {
                var im = UpdateTrust.Signature(impostor);
                Check("a certificate made on this PC with OUR organisation and country in it is refused — the name alone is worth nothing",
                    !im.Ok && !im.Why.Contains("signed by us"), im.Why);
            }

            Console.WriteLine();
            Console.WriteLine("== The version ==");
            var version = UpdateTrust.FileVersion(host);
            if (!AppVersion.IsStamped(version))
            {
                var a = UpdateTrust.Accept(host, "1.0.0.0");
                Check("our signed program that is not a released build (1.0.0.0) is refused", !a.Ok && a.Why.Contains("not a released build"), a.Why);
                Console.WriteLine($"  NOTE  dist\\ holds a build from before version numbers ({version}): the newer / older / offered-as cases");
                Console.WriteLine("        are proved once a signed build is made with build.ps1 — run this again then.");
            }
            else
            {
                var older = UpdateTrust.Accept(host, "2026.1.1.0");
                Check($"our signed {version} replaces an older install", older.Ok, older.Why);
                var same = UpdateTrust.Accept(host, version);
                Check("...but not the same version again", !same.Ok && same.Why.Contains("not newer"), same.Why);
                var newer = UpdateTrust.Accept(host, "2099.1.1.0");
                Check("...and never a NEWER install — no going back by the server", !newer.Ok && newer.Why.Contains("not newer"), newer.Why);
                var offered = UpdateTrust.Accept(host, "2026.1.1.0", "2099.1.1.0");
                Check("...and not when it is not the version it was offered as", !offered.Ok && offered.Why.Contains("not the"), offered.Why);
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Signs <paramref name="file"/> with a certificate made here and now, carrying our organisation and country, then
    /// deletes the certificate — what anybody could do in a minute. It is in this user's store for the few seconds it takes.
    /// </summary>
    private static (bool, string) SignWithHomeMadeCertificate(string file)
    {
        var ps = $@"
$c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=The Remote Support Program (Pty) Ltd, O={UpdateTrust.Publisher}, C={UpdateTrust.PublisherCountry}' -CertStoreLocation Cert:\CurrentUser\My
try {{ $r = Set-AuthenticodeSignature -FilePath '{file.Replace("'", "''")}' -Certificate $c; $r.Status }}
finally {{ Remove-Item ('Cert:\CurrentUser\My\' + $c.Thumbprint) -Force }}";
        var psi = new ProcessStartInfo("powershell") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        // UnknownError is what PowerShell reports for a self-made (untrusted) certificate — the signature is still written.
        var hasSig = new FileInfo(file).Length > 0 && (o.Contains("Valid") || o.Contains("UnknownError") || o.Contains("NotTrusted"));
        return (hasSig, (o + e).Trim());
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
