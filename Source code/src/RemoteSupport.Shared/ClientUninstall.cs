namespace RemoteSupport.Shared;

/// <summary>
/// Running Uninstall-Client.ps1 from an installed copy (2026-10-06): the agent when its machine is removed from
/// the company's list, and the tray app's Uninstall… for a person who no longer wants it. ONE way, so the two
/// cannot drift. The script deletes the folder it would otherwise run from, so it is copied out first, with
/// the branding it reads to find the shortcuts it made.
/// </summary>
public static class ClientUninstall
{
    /// <summary>
    /// Copies the script and its branding from <paramref name="installDir"/> into a folder of its own under
    /// <paramref name="temp"/>, and says how to run it. Null when there is no script (an install made before
    /// 2026-10-06 did not keep it).
    /// </summary>
    public static (string? script, string args) Command(string installDir, string temp)
    {
        var source = Path.Combine(installDir, "Uninstall-Client.ps1");
        if (!File.Exists(source)) return (null, "");
        var dir = Path.Combine(temp, "rs-uninstall-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "branding"));
        var script = Path.Combine(dir, "Uninstall-Client.ps1");
        File.Copy(source, script);
        var brand = Path.Combine(installDir, "branding", "brand.json");
        if (File.Exists(brand)) File.Copy(brand, Path.Combine(dir, "branding", "brand.json"));
        var install = installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (script, $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -InstallDir \"{install}\"");
    }
}
