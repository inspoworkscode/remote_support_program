using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace RemoteSupport.Client;

/// <summary>
/// What a single downloadable .exe carries inside it (2026-09-30): the host, the agent service, the
/// install scripts and the health check toolkit — everything the old zip held beside the tray app —
/// packed by tools/Pack-ClientExe.ps1 as the embedded resource <c>client-payload.zip</c>. They were
/// SIGNED before they were packed, and this app is signed after, so what is unpacked here is
/// byte-for-byte what was signed (docs/CODE-SIGNING.md).
///
/// A build made any other way — Visual Studio, dotnet run, an older ship — has none of it; the app
/// then works as it always did, from the files beside it, and the Install choice says it cannot.
/// </summary>
public static class ClientPayload
{
    public const string PayloadResource = "client-payload.zip";
    public const string DefaultsResource = "client-defaults.json";

    private static Stream? Open(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    public static bool Present { get { using var s = Open(PayloadResource); return s != null; } }

    /// <summary>The server this build was made for, used only when the download did not say where it came from.</summary>
    public static string DefaultServer()
    {
        try
        {
            using var s = Open(DefaultsResource);
            if (s == null) return "";
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("server", out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Unpacks the payload into <paramref name="dir"/>. Only <paramref name="only"/>, when given —
    /// "Get help now" needs the host and nothing else. A file already there from this same build is
    /// left alone, so a second run does not rewrite a hundred megabytes. Returns how many files it wrote.
    /// </summary>
    public static int ExtractTo(string dir, params string[] only)
    {
        using var s = Open(PayloadResource) ?? throw new InvalidOperationException("This copy of the app carries nothing to install.");
        using var zip = new ZipArchive(s, ZipArchiveMode.Read);
        Directory.CreateDirectory(dir);
        var full = Path.GetFullPath(dir);
        int n = 0;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            if (only.Length > 0 && !only.Contains(e.FullName, StringComparer.OrdinalIgnoreCase)) continue;
            var target = Path.GetFullPath(Path.Combine(full, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
            // An entry may never land outside the folder it was asked for.
            if (!target.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            // ExtractToFile stamps the entry's own time on the file, so size AND time together say "this build's".
            if (File.Exists(target) && new FileInfo(target) is { } fi && fi.Length == e.Length
                && Math.Abs((fi.LastWriteTimeUtc - e.LastWriteTime.UtcDateTime).TotalSeconds) < 2) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            n++;
        }
        return n;
    }
}
