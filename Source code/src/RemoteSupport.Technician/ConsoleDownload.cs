using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// What a DOWNLOADED console does first (2026-10-01). Clint: "the technician console still
/// downloads as a zip, that should be an exe as well."
///
/// <para>The download is this same .exe for every company — a file that differed per company could
/// not be signed (docs/CODE-SIGNING.md, "One .exe for every company") — so it has no branding\
/// beside it. It learns whose it is the way the customers' .exe does (<see cref="DownloadIdentity"/>:
/// the address the browser recorded, then its name, then the last company, then it asks), fetches
/// that company's branding, and puts itself where it will stay: the zip's four steps (unblock,
/// unzip somewhere that stays, run, pin), done for them.</para>
///
/// <para>Into <c>%LOCALAPPDATA%\Programs\RemoteSupport\Console\{slug}</c>, for this Windows user only,
/// so no administrator is ever asked. A Start menu and a desktop shortcut. Then the installed copy is
/// started and this one exits. Running a newer download does the same over it: that is updating.</para>
/// </summary>
public static class ConsoleDownload
{
    public const string DefaultsResource = "console-defaults.json";

    /// <summary>Where consoles are set up, one folder per company. RSP_CONSOLE_HOME moves it, for tools/consoleexecheck.</summary>
    public static string Home { get; set; } =
        Environment.GetEnvironmentVariable("RSP_CONSOLE_HOME") is { Length: > 0 } moved ? moved
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RemoteSupport", "Console");

    /// <summary>
    /// Where the shortcuts go: the Start menu and the desktop. RSP_CONSOLE_SHORTCUTS puts both in one
    /// folder of the harness's own, so a test never touches this PC's desktop.
    /// </summary>
    public static IEnumerable<string> ShortcutFolders()
    {
        if (Environment.GetEnvironmentVariable("RSP_CONSOLE_SHORTCUTS") is { Length: > 0 } moved) { yield return moved; yield break; }
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>A copy with no branding beside it: one that was downloaded, not set up or unzipped.</summary>
    public static bool IsDownloadedCopy =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, "branding", "brand.json"));

    public sealed record Identity(string Server, string Slug, string How);

    private static string LastPath => Path.Combine(Home, "last.json");

    /// <summary>The server this build was made for, used only when the download did not say where it came from.</summary>
    public static string DefaultServer()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultsResource);
            if (s == null) return "";
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("server", out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>Whose console this is. The download's own address first, then its name, then the last company, then <paramref name="ask"/>.</summary>
    public static Identity? Identify(string exePath, Func<string?> ask)
    {
        var (zoneServer, zoneSlug) = DownloadIdentity.ConsoleFromZoneIdentifier(DownloadIdentity.ReadZoneIdentifier(exePath));
        var fileSlug = DownloadIdentity.SlugFromConsoleFileName(exePath);
        var last = ReadLast();
        var fallbackServer = last?.Server is { Length: > 0 } ls ? ls : DefaultServer();

        if (zoneSlug != null && zoneServer != null) return new Identity(zoneServer, zoneSlug, "the address it was downloaded from");
        if (fileSlug != null && fallbackServer.Length > 0) return new Identity(DownloadIdentity.HttpOrigin(fallbackServer), fileSlug, "its file name");
        if (last != null) return last with { How = "the last company used on this PC" };
        var typed = (ask() ?? "").Trim().ToLowerInvariant();
        if (!DownloadIdentity.IsSlug(typed) || fallbackServer.Length == 0) return null;
        return new Identity(DownloadIdentity.HttpOrigin(fallbackServer), typed, "the code typed in");
    }

    private static Identity? ReadLast()
    {
        try
        {
            if (!File.Exists(LastPath)) return null;
            var id = JsonSerializer.Deserialize<Identity>(File.ReadAllText(LastPath));
            return id != null && DownloadIdentity.IsSlug(id.Slug) && id.Server.Length > 0 ? id : null;
        }
        catch { return null; }
    }

    /// <summary>The folder a company's console is set up in: the .exe, and branding\ beside it.</summary>
    public static string InstallDir(string slug) => Path.Combine(Home, slug);
    public static string InstalledExe(string slug) => Path.Combine(InstallDir(slug), "RemoteSupport.Technician.exe");

    /// <summary>
    /// Fetches the company's branding into the console's own folder and puts this .exe beside it, with
    /// its shortcuts. The installed .exe's path, or an error worth showing. Nothing is half-done on a
    /// refusal: the branding is written only once the server has answered.
    /// </summary>
    public static async Task<(string? exe, string error)> SetUpAsync(Identity id, string thisExe, HttpClient? http = null)
    {
        var dir = InstallDir(id.Slug);
        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Branding? brand;
        try
        {
            var origin = DownloadIdentity.HttpOrigin(id.Server);
            // The branding carries the company's server address; never fetched in the clear from the
            // internet, or anyone on the line could point this console at a server of their own.
            if (SecureTransport.Refusal(origin) is { } insecure) return (null, insecure);
            var res = await http.GetAsync($"{origin}/download/{id.Slug}/brand.json");
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, $"\"{id.Slug}\" is not a company this server supports, or its account is not active.");
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync();
            brand = JsonSerializer.Deserialize<Branding>(json, Json.Options);
            if (brand == null || string.IsNullOrEmpty(brand.ServerUrl)) return (null, "The server's answer did not make sense.");
            var bdir = Path.Combine(dir, "branding");
            Directory.CreateDirectory(bdir);
            await File.WriteAllTextAsync(Path.Combine(bdir, "brand.json"), json);
            try
            {
                var logo = await http.GetAsync($"{origin}/logo/{id.Slug}");
                if (logo.IsSuccessStatusCode && !string.IsNullOrEmpty(brand.Logo))
                    await File.WriteAllBytesAsync(Path.Combine(bdir, Path.GetFileName(brand.Logo)), await logo.Content.ReadAsByteArrayAsync());
            }
            catch { /* no logo: the company's name is drawn instead */ }
        }
        catch (Exception ex)
        {
            // Offline, with a console already set up for them: that copy will do.
            brand = Branding.LoadFrom(dir);
            if (brand == null || !File.Exists(InstalledExe(id.Slug)))
                return (null, $"Could not reach {DownloadIdentity.HttpOrigin(id.Server)}. Check the internet connection and try again. ({ex.Message})");
        }
        finally { if (own) http.Dispose(); }

        var exe = InstalledExe(id.Slug);
        if (!string.Equals(Path.GetFullPath(thisExe), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) && !SameFile(thisExe, exe))
        {
            try { File.Copy(thisExe, exe, overwrite: true); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)   // sharing or lock violation: it is running
            {
                return (null, "The console is open, so it cannot be updated. Close it first — right-click its icon by the clock and choose Exit — then open this file again.");
            }
            catch (Exception ex) { return (null, $"The console could not be put in {dir}: {ex.Message}"); }
        }
        // Copying carries the browser's "downloaded from the internet" mark with it, and Windows
        // would warn about the publisher at every start. It is ours, set up on purpose; the mark stays
        // on the downloaded file, where it belongs.
        try { File.Delete(exe + ":Zone.Identifier"); } catch { }

        // The shortcuts carry the company's logo. The .exe has no icon of its own (it is the same file
        // for every company), and a pinned taskbar button is drawn from the shortcut, not the window.
        var icon = exe + ",0";
        try
        {
            var ico = Path.Combine(dir, "console.ico");
            if (Branding.LoadFrom(dir) is { } setUp && RemoteSupport.UI.BrandIcon.WriteIcoFile(setUp, ico)) icon = ico + ",0";
        }
        catch (Exception ex) { Trace.Write($"set up: no icon for the shortcuts: {ex.Message}"); }

        var name = $"{brand.CompanyName} technician console".Trim();
        foreach (var folder in ShortcutFolders())
        {
            try { Directory.CreateDirectory(folder); Shortcut(Path.Combine(folder, SafeName(name) + ".lnk"), exe, name, icon); }
            catch (Exception ex) { Trace.Write($"set up: no shortcut in {folder}: {ex.Message}"); }
        }
        try
        {
            Directory.CreateDirectory(Home);
            await File.WriteAllTextAsync(LastPath, JsonSerializer.Serialize(id with { How = "" }));
        }
        catch { }
        return (exe, "");
    }

    /// <summary>Byte for byte the same build: nothing to copy, and no "close it first" for running the same download twice.</summary>
    private static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (!fb.Exists || fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
            return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
        }
        catch { return false; }
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A .lnk through the shell's own object, the way an installer makes one.</summary>
    private static void Shortcut(string lnk, string target, string description, string icon)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = target;
            s.WorkingDirectory = Path.GetDirectoryName(target);
            s.IconLocation = icon;
            s.Description = description;
            s.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(s);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>
    /// The whole first run, with a window only for a question or a problem: whose it is, set up,
    /// start the installed copy. Returns the process exit code.
    /// </summary>
    public static int Run(string thisExe)
    {
        var id = Identify(thisExe, () => RemoteSupport.UI.AskCompanyDialog.Ask(
            "Which company's console is this? Type its code — the last part of the link you downloaded it from, such as anywhere-it."));
        if (id == null)
        {
            MessageBox.Show("This console could not tell which company it is for. Download it again from your company's technician link.",
                "Technician console", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        var (exe, error) = SetUpAsync(id, thisExe).GetAwaiter().GetResult();
        if (exe == null)
        {
            MessageBox.Show(error, "Technician console", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) }); }
        catch (Exception ex)
        {
            MessageBox.Show($"The console is set up in {Path.GetDirectoryName(exe)} but could not be started: {ex.Message}",
                "Technician console", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        return 0;
    }
}
