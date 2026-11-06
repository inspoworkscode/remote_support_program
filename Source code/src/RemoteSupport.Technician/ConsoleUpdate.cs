using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// The console keeps itself current (2026-10-16, auto-update stage E, docs/NEXT-SESSION-AUTOUPDATE.md).
///
/// <para><b>At start, before anybody signs in</b> (<see cref="AtStart"/>): an INSTALLED console — one set up in
/// <c>%LOCALAPPDATA%\Programs\RemoteSupport\Console\{company}</c> by <see cref="ConsoleDownload"/> — asks its server
/// <c>/update/console.json?slug=</c> what its company may take. Newer → downloaded beside it, checked with
/// <see cref="UpdateTrust.Accept"/> (ours, newer, the version offered), and the NEW copy is started with
/// <c>--install-update</c>: it waits for this one to close, puts itself in its place (the old one renamed aside), and starts
/// there. If that started copy does not reach its sign-in window within a minute, the old one goes back and that version
/// is never taken again. A few seconds of "Updating…" for the technician; nothing asked.</para>
///
/// <para><b>While running</b>: the hub's state names the console release (<see cref="StateMsg.ConsoleUpdate"/>); a newer
/// one shows "Update ready" in the header, which restarts the console the way Sign out does — and the start updates.</para>
///
/// <para>A console run from anywhere else — a developer's build, <c>demo\technician</c>, a guest copy — never updates
/// itself: it is not installed, and replacing it is nobody's business but whoever put it there.</para>
/// </summary>
internal static class ConsoleUpdate
{
    public const string InstallSwitch = "--install-update";
    /// <summary>Written by a console that reached its sign-in window; the installing copy waits for it.</summary>
    private const string StartedMarker = "started-";
    /// <summary>Written by the installing copy while it waits, so the new console does not tidy away what a rollback needs.</summary>
    private const string InstallingMarker = "installing";

    /// <summary>The company folder this console is installed in, or null when it is not an installed copy.</summary>
    public static string? InstalledDir()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var home = Path.GetFullPath(ConsoleDownload.Home).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(dir), home, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(Path.GetFileName(exe), "RemoteSupport.Technician.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(Path.Combine(dir, "branding", "brand.json")) ? dir : null;
    }

    private static string UpdateDir(string installDir) => Path.Combine(installDir, "update");

    /// <summary>Whether <paramref name="r"/> is newer than this console, for the header. Never for a console that is not installed.</summary>
    public static bool Offers(UpdateRelease? r) =>
        r != null && InstalledDir() != null && AppVersion.IsStamped(r.Version) && AppVersion.Compare(r.Version, AppVersion.Current) > 0
        && !GaveUp(InstalledDir()!, r.Version);

    /// <summary>
    /// The check at start. True when an update is being installed and this process must exit now; false to carry on as
    /// it is — no update, not installed, offline, refused.
    /// </summary>
    public static bool AtStart(Branding brand)
    {
        var dir = InstalledDir();
        if (dir == null) return false;
        Tidy(dir);
        UpdateRelease? r;
        string origin;
        try
        {
            origin = DownloadIdentity.HttpOrigin(brand.ServerUrl);
            if (SecureTransport.Refusal(origin) != null) return false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = http.GetAsync($"{origin}/update/console.json?slug={Uri.EscapeDataString(Path.GetFileName(dir))}").GetAwaiter().GetResult();
            if (res.StatusCode != HttpStatusCode.OK) return false;
            r = JsonSerializer.Deserialize<UpdateRelease>(res.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Json.Options);
        }
        catch (Exception ex) { Trace.Write("update: could not ask the server: " + ex.Message); return false; }
        if (!Offers(r)) return false;

        string? file = null, problem = null;
        using (var wait = new UpdatingForm(brand, r!.Version))
        {
            wait.Show();
            Application.DoEvents();
            var t = Task.Run(() => Download(origin, r, dir));
            while (!t.IsCompleted) { Application.DoEvents(); Thread.Sleep(50); }
            (file, problem) = t.Result;
        }
        if (file == null) { Trace.Write($"update {r.Version}: {problem}"); return false; }

        var verdict = UpdateTrust.Accept(file, AppVersion.Current, r.Version);
        if (!verdict.Ok)
        {
            Trace.Write($"update {r.Version}: {(verdict.TryLater ? "not now" : "REFUSED")} — {verdict.Why}");
            // A check that could not be made now (revocation offline) is tried again at the next start; a refusal is for ever.
            if (!verdict.TryLater) Remember(dir, r.Version);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(file, $"{InstallSwitch} \"{Path.Combine(dir, "RemoteSupport.Technician.exe")}\" {Environment.ProcessId}")
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file)! });
            Trace.Write($"update {r.Version}: checked; the new console is putting itself in place");
            return true;
        }
        catch (Exception ex) { Trace.Write($"update {r.Version}: could not start the new console: {ex.Message}"); return false; }
    }

    /// <summary>The release, beside the installed console. A file already there and whole is used as it is.</summary>
    private static (string? file, string problem) Download(string origin, UpdateRelease r, string dir)
    {
        try
        {
            if (!r.Path.StartsWith('/') || r.Path.StartsWith("//") || r.Path.Contains(':')) return (null, "the offer named another server");
            var folder = Path.Combine(UpdateDir(dir), r.Version);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "RemoteSupport.Technician.exe");
            if (!(File.Exists(file) && new FileInfo(file).Length == r.Size && Sha(file) == r.Sha256))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using (var from = http.GetStreamAsync(origin + r.Path).GetAwaiter().GetResult())
                using (var to = File.Create(file))
                    from.CopyTo(to);
                if (new FileInfo(file).Length != r.Size || Sha(file) != r.Sha256) { File.Delete(file); return (null, "the download was not whole"); }
            }
            return (file, "");
        }
        catch (Exception ex) { return (null, "the download failed: " + ex.Message); }
    }

    private static string Sha(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>
    /// <c>--install-update &lt;installed exe&gt; &lt;old pid&gt;</c>: run by the NEW console from its download folder. Waits for
    /// the old one to close, puts itself in its place, starts it, and waits for it to reach its sign-in window — or puts
    /// the old one back.
    /// </summary>
    public static int Install(string[] args)
    {
        if (args.Length < 3) return 2;
        var target = args[1];
        var dir = Path.GetDirectoryName(target)!;
        if (int.TryParse(args[2], out var pid))
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(30_000); } catch { /* already gone */ }
        var me = Environment.ProcessPath!;
        var old_ = target + ".old";
        var marker = Path.Combine(UpdateDir(dir), StartedMarker + AppVersion.Current);
        // While this is here the new console's tidy-up leaves *.old alone: it is what a rollback puts back.
        var installing = Path.Combine(UpdateDir(dir), InstallingMarker);
        try { Directory.CreateDirectory(UpdateDir(dir)); File.WriteAllText(installing, AppVersion.Current); } catch { }
        try
        {
            try { File.Delete(marker); } catch { }
            if (File.Exists(old_)) File.Delete(old_);
            if (File.Exists(target)) File.Move(target, old_);
            File.Copy(me, target);
            try { File.Delete(target + ":Zone.Identifier"); } catch { }
        }
        catch (Exception ex)
        {
            Trace.Write("update: could not put the new console in place: " + ex.Message);
            PutBack(target, old_);
            try { File.Delete(installing); } catch { }
            Start(target);
            return 1;
        }
        var started = Start(target);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (started && DateTime.UtcNow < until && !File.Exists(marker)) Thread.Sleep(250);
        if (started && File.Exists(marker))
        {
            Trace.Write($"update {AppVersion.Current}: installed");
            try { File.Delete(installing); } catch { }
            return 0;
        }
        // It never reached its sign-in window: the old one back, and this version never again.
        Trace.Write($"update {AppVersion.Current}: the new console did not start — putting the old one back");
        foreach (var p in Process.GetProcessesByName("RemoteSupport.Technician"))
            try { if (string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase) && p.Id != Environment.ProcessId) p.Kill(); } catch { }
        Thread.Sleep(500);
        PutBack(target, old_);
        Remember(dir, AppVersion.Current);
        try { File.Delete(installing); } catch { }
        Start(target);
        return 1;
    }

    /// <summary>Said by an installed console once its sign-in window is up: the copy that installed it can go.</summary>
    public static void SayStarted()
    {
        var dir = InstalledDir();
        if (dir == null) return;
        try { Directory.CreateDirectory(UpdateDir(dir)); File.WriteAllText(Path.Combine(UpdateDir(dir), StartedMarker + AppVersion.Current), DateTime.UtcNow.ToString("o")); }
        catch { }
    }

    private static void PutBack(string target, string old)
    {
        try
        {
            if (!File.Exists(old)) return;
            if (File.Exists(target)) File.Move(target, target + ".bad", overwrite: true);
            File.Move(old, target);
        }
        catch (Exception ex) { Trace.Write("update: could not put the old console back: " + ex.Message); }
    }

    private static bool Start(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }); return true; }
        catch (Exception ex) { Trace.Write("update: could not start " + exe + ": " + ex.Message); return false; }
    }

    /// <summary>Old copies and downloads from earlier updates — best effort, at every start of an installed console.</summary>
    private static void Tidy(string dir)
    {
        // An install under way (its copy is waiting for this console to reach its sign-in window): leave its rollback alone.
        var installing = Path.Combine(UpdateDir(dir), InstallingMarker);
        if (File.Exists(installing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(installing) < TimeSpan.FromMinutes(5)) return;
        try { File.Delete(installing); } catch { }
        foreach (var f in new[] { "RemoteSupport.Technician.exe.old", "RemoteSupport.Technician.exe.bad" })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        try
        {
            foreach (var d in Directory.Exists(UpdateDir(dir)) ? Directory.GetDirectories(UpdateDir(dir)) : Array.Empty<string>())
                if (AppVersion.Compare(Path.GetFileName(d), AppVersion.Current) <= 0) try { Directory.Delete(d, true); } catch { }
            foreach (var f in Directory.Exists(UpdateDir(dir)) ? Directory.GetFiles(UpdateDir(dir), StartedMarker + "*") : Array.Empty<string>())
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private sealed class State { public List<string> GaveUp { get; set; } = new(); }

    private static bool GaveUp(string dir, string version)
    {
        try
        {
            var path = Path.Combine(UpdateDir(dir), UpdateRunner.StateFile);
            return File.Exists(path) && (JsonSerializer.Deserialize<State>(File.ReadAllText(path))?.GaveUp.Contains(version) ?? false);
        }
        catch { return false; }
    }

    private static void Remember(string dir, string version) => UpdateRunner.RememberIn(UpdateDir(dir), version);

    /// <summary>The few seconds of "Updating…" while the new console downloads: the company's look, no buttons.</summary>
    private sealed class UpdatingForm : Form
    {
        public UpdatingForm(Branding brand, string version)
        {
            var t = Theme.FromBranding(brand, AppSurface.Technician);
            Text = brand.ProductName;
            FormBorderStyle = FormBorderStyle.FixedDialog; ControlBox = false; ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen; BackColor = t.Surface;
            Icon = BrandIcon.Current;
            var words = $"Updating the console to version {version}…";
            var size = TextRenderer.MeasureText(words, t.Body);
            ClientSize = new Size(size.Width + 64, size.Height + 56);
            Controls.Add(new Label { Text = words, Font = t.Body, ForeColor = t.Ink, BackColor = Color.Transparent, AutoSize = true, Location = new Point(32, 28) });
        }
    }
}
