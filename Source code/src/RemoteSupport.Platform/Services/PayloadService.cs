using System.IO.Compression;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Builds a tenant-branded download from the generic app payloads: the client (or technician) files
/// plus that tenant's brand.json and logo. Phase 3 replaces the zip with a signed installer that fetches
/// the same branding at install time.
/// </summary>
public sealed class PayloadService
{
    private readonly string _clientDir, _techDir;
    private readonly TenantService _tenants;

    public PayloadService(IConfiguration cfg, TenantService tenants, string baseDir)
    {
        _tenants = tenants;
        _clientDir = Resolve(cfg["Platform:ClientPayloadDir"] ?? "payloads/client", baseDir);
        _techDir = Resolve(cfg["Platform:TechnicianPayloadDir"] ?? "payloads/technician", baseDir);
    }

    private static string Resolve(string p, string baseDir) => Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);

    public bool ClientAvailable => Directory.Exists(_clientDir) && Directory.EnumerateFiles(_clientDir).Any();

    // ---- the one downloadable .exe (2026-09-30) ----
    // tools/Pack-ClientExe.ps1 writes a client that carries the host, the agent and the install
    // scripts inside it, and leaves this file beside it to say so. Only then is the .exe offered on
    // its own: an ordinary RemoteSupport.Client.exe could "get help now" but never install.

    public const string PackedMarker = "client-exe.packed";
    public string ClientExePath => Path.Combine(_clientDir, "RemoteSupport.Client.exe");
    public bool ClientExeAvailable => File.Exists(Path.Combine(_clientDir, PackedMarker)) && File.Exists(ClientExePath);

    /// <summary>What the zip's branding\brand.json holds — what a downloaded .exe fetches instead.</summary>
    public string BrandJson(Tenant t) => Json.Serialize(_tenants.ToBranding(t));
    public bool TechnicianAvailable => Directory.Exists(_techDir) && Directory.EnumerateFiles(_techDir).Any();

    // ---- the console as one .exe too (2026-10-01) ----
    // build.ps1 and tools/ship-build.ps1 leave this beside a console that can set itself up from a
    // download (ConsoleDownload). An older console with no branding beside it would start pointed at
    // nobody, so without the marker the zip is still what is offered.
    public const string ConsoleMarker = "console-exe.ready";
    public string TechnicianExePath => Path.Combine(_techDir, "RemoteSupport.Technician.exe");
    public bool TechnicianExeAvailable => File.Exists(Path.Combine(_techDir, ConsoleMarker)) && File.Exists(TechnicianExePath);

    /// <summary>
    /// Whether the client payload carries the installer scripts as well as the executables. A payload
    /// staged by a build.ps1 older than 2026-09-11 has only the three exes, so the download can offer
    /// nothing but the portable tray app. The download page asks this rather than assuming, because
    /// telling a customer to double-click a file that is not in their zip is worse than not offering it.
    /// </summary>
    public bool ClientHasInstaller =>
        ClientAvailable && File.Exists(Path.Combine(_clientDir, "Install-Client-Unattended.bat"));

    public Task<byte[]> BuildClientZipAsync(Tenant t) => BuildAsync(_clientDir, t);
    public Task<byte[]> BuildTechnicianZipAsync(Tenant t) => BuildAsync(_techDir, t);

    private async Task<byte[]> BuildAsync(string dir, Tenant t)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                if (rel.StartsWith("branding/")) continue; // replaced below
                zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
            }
            var brand = _tenants.ToBranding(t);
            var e = zip.CreateEntry("branding/brand.json");
            await using (var s = e.Open()) await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Json.Serialize(brand)));
            if (!string.IsNullOrEmpty(t.LogoFile))
            {
                var logoPath = Path.Combine(_tenants.LogoDir, t.LogoFile);
                if (File.Exists(logoPath)) zip.CreateEntryFromFile(logoPath, "branding/" + brand.Logo, CompressionLevel.Optimal);
            }
        }
        return ms.ToArray();
    }
}
