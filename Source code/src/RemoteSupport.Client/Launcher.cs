using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.Client;

/// <summary>
/// The first thing a DOWNLOADED copy of the app does (2026-09-30). An installed copy, or one run
/// from an unzipped folder, has branding\brand.json beside it and never comes here. A downloaded one
/// is the same signed file every company's customers get, so it works out whose it is
/// (<see cref="DownloadIdentity"/>), fetches that company's branding from the server into a folder of
/// its own, and then either runs now — the old "just this once" — or installs, which runs the same
/// Install-Client.ps1 the zip always had, from a folder unpacked out of this .exe.
/// </summary>
public static class Launcher
{
    /// <summary>
    /// %LOCALAPPDATA%\RemoteSupport\download — per person, no administrator needed. RSP_DOWNLOAD_CACHE
    /// moves it, so tools/oneexecheck never touches this PC's real one.
    /// </summary>
    public static string CacheRoot { get; set; } =
        Environment.GetEnvironmentVariable("RSP_DOWNLOAD_CACHE") is { Length: > 0 } moved ? moved
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "download");

    /// <summary>A copy with no branding beside it: one that was downloaded rather than installed or unzipped.</summary>
    public static bool IsDownloadedCopy =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, "branding", "brand.json"));

    public sealed record Identity(string Server, string Slug, string How);

    private static string LastPath => Path.Combine(CacheRoot, "last.json");

    /// <summary>
    /// Whose copy this is, and which server to ask. The download's own address first, then the file
    /// name, then the last company this person used, then <paramref name="ask"/>.
    /// </summary>
    public static Identity? Identify(string exePath, Func<string?> ask)
    {
        var (zoneServer, zoneSlug) = DownloadIdentity.FromZoneIdentifier(DownloadIdentity.ReadZoneIdentifier(exePath));
        var fileSlug = DownloadIdentity.SlugFromFileName(exePath);
        var last = ReadLast();
        var fallbackServer = last?.Server is { Length: > 0 } ls ? ls : ClientPayload.DefaultServer();

        if (zoneSlug != null && zoneServer != null) return new Identity(zoneServer, zoneSlug, "the address it was downloaded from");
        if (fileSlug != null && fallbackServer.Length > 0) return new Identity(DownloadIdentity.HttpOrigin(zoneServer ?? fallbackServer), fileSlug, "its file name");
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

    /// <summary>The folder this company's branding lives in: {cache}\{slug}, with branding\ inside.</summary>
    public static string BrandDir(string slug) => Path.Combine(CacheRoot, slug);

    /// <summary>
    /// This company's branding from its server — what the zip's brand.json used to carry — into
    /// <see cref="BrandDir"/>, with its logo. With the server out of reach, the copy from last time
    /// will do. Empty error means there is branding to use.
    /// </summary>
    public static async Task<(Branding? brand, string error)> FetchBrandingAsync(Identity id, HttpClient? http = null)
    {
        var dir = BrandDir(id.Slug);
        var bdir = Path.Combine(dir, "branding");
        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var origin = DownloadIdentity.HttpOrigin(id.Server);
            // The branding carries the company's server address and key; never fetched in the clear
            // from the internet, or anyone on the line could point this machine somewhere else.
            if (SecureTransport.Refusal(origin) is { } insecure) return (null, insecure);
            var res = await http.GetAsync($"{origin}/download/{id.Slug}/brand.json");
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, $"\"{id.Slug}\" is not a company this server supports, or its support has been switched off.");
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync();
            var brand = JsonSerializer.Deserialize<Branding>(json, Json.Options);
            if (brand == null || string.IsNullOrEmpty(brand.TenantKey)) return (null, "The server's answer did not make sense.");
            Directory.CreateDirectory(bdir);
            await File.WriteAllTextAsync(Path.Combine(bdir, "brand.json"), json);
            try
            {
                var logo = await http.GetAsync($"{origin}/logo/{id.Slug}");
                if (logo.IsSuccessStatusCode && !string.IsNullOrEmpty(brand.Logo))
                    await File.WriteAllBytesAsync(Path.Combine(bdir, Path.GetFileName(brand.Logo)), await logo.Content.ReadAsByteArrayAsync());
            }
            catch { /* a missing logo draws the company's name instead */ }
            Directory.CreateDirectory(CacheRoot);
            await File.WriteAllTextAsync(LastPath, JsonSerializer.Serialize(id with { How = "" }));
            return (Branding.LoadFrom(dir), "");
        }
        catch (Exception ex)
        {
            var cached = Branding.LoadFrom(dir);
            if (cached != null) return (cached, "");
            return (null, $"Could not reach {DownloadIdentity.HttpOrigin(id.Server)}. Check the internet connection and try again. ({ex.Message})");
        }
        finally { if (own) http.Dispose(); }
    }

    /// <summary>
    /// "Get help now": the host beside a copy of nothing, in this company's folder, where
    /// <see cref="HostSpawner"/> will find it. Nothing is installed and nothing needs an administrator.
    /// </summary>
    public static void PrepareRunNow(string slug)
    {
        if (!ClientPayload.Present) return;   // a development build: the host is beside the app already
        var bin = Path.Combine(BrandDir(slug), "bin");
        ClientPayload.ExtractTo(bin, "RemoteSupport.Host.exe");
        HostSpawner.Directory = bin;
    }

    /// <summary>The installed copy's path, as Install-Client.ps1 chooses it: Program Files\{company}\Remote Support.</summary>
    public static string InstalledExe(Branding b)
    {
        var company = string.IsNullOrWhiteSpace(b.CompanyName) ? "Remote Support" : b.CompanyName;
        foreach (var c in Path.GetInvalidFileNameChars()) company = company.Replace(c, ' ');
        company = System.Text.RegularExpressions.Regex.Replace(company, @"\s+", " ").Trim();
        if (company.Length == 0) company = "Remote Support";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), company, "Remote Support", "RemoteSupport.Client.exe");
    }

    /// <summary>
    /// The folder the installer runs from — shaped exactly like the old unzipped download: this .exe
    /// as RemoteSupport.Client.exe, everything it carries, and this company's branding\.
    /// </summary>
    public static string StageInstall(string slug, string? into = null)
    {
        var dir = into ?? Path.Combine(Path.GetTempPath(), "RemoteSupport-Install-" + Guid.NewGuid().ToString("N")[..8]);
        ClientPayload.ExtractTo(dir);
        File.Copy(Environment.ProcessPath!, Path.Combine(dir, "RemoteSupport.Client.exe"), overwrite: true);
        var from = Path.Combine(BrandDir(slug), "branding");
        var to = Path.Combine(dir, "branding");
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        return dir;
    }

    /// <summary>
    /// Runs the staged Install-Client.ps1 as administrator — the Windows prompt is the only question
    /// asked — writing what it says to <paramref name="log"/>. The exit code, or -1 when they said no
    /// to the prompt.
    /// </summary>
    public static async Task<int> RunInstallerAsync(string stagedDir, bool unattended, string log, bool free = false)
    {
        var script = Path.Combine(stagedDir, "Install-Client.ps1");
        string Q(string s) => s.Replace("'", "''");
        // Everything the script says goes to the log: an elevated window's output cannot be read back.
        var command = $"try {{ & '{Q(script)}'{(unattended ? " -Unattended" : "")}{(free ? " -Free" : "")} *>&1 | Out-File -FilePath '{Q(log)}' -Encoding utf8; exit 0 }} " +
                      $"catch {{ $_ | Out-File -FilePath '{Q(log)}' -Append -Encoding utf8; exit 1 }}";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = stagedDir
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return 1;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return -1; }   // "No" to the prompt
    }
}
