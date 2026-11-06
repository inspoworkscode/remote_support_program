using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RemoteSupport.Shared;

/// <summary>
/// A stable identifier for the physical machine, independent of our own install.
///
/// Why this exists: DeviceId lives in device.json, which the uninstaller deletes, so an
/// uninstall/reinstall produced a second Device row for the same PC — losing its client link and
/// splitting its history (docs/HANDOVER.md §15). A fingerprint taken from the hardware survives
/// that, and the SMBIOS UUID survives an OS reinstall too.
///
/// Deliberately NOT the machine name: two PCs in the same company are often called the same thing
/// (RECEPTION, SERVER, or any re-imaged fleet), and merging those would fuse two machines into one
/// record — a technician could then connect to a PC believing it was a different one.
///
/// Everything here is best-effort. A machine that cannot report a fingerprint still works exactly
/// as before; it simply does not get reinstall matching.
/// </summary>
public static class MachineFingerprint
{
    private static string? _cached;

    /// <summary>
    /// Hash of whatever stable identifiers this machine will give up, or "" if none.
    /// Hashed rather than raw so we never store a customer's SMBIOS UUID verbatim.
    /// </summary>
    public static string Get()
    {
        if (_cached != null) return _cached;
        var parts = new List<string>();

        var (uuid, guid) = ReadIdentifiers();
        if (IsUsable(uuid)) parts.Add("smbios:" + uuid!.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(guid)) parts.Add("machineguid:" + guid!.Trim());

        if (parts.Count == 0) return _cached = "";
        var joined = string.Join("|", parts).ToLowerInvariant();
        return _cached = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..32];
    }

    /// <summary>
    /// Some OEMs and hypervisors ship a blank or shared UUID. Matching on one of those would merge
    /// every machine of that model into a single record, so they are rejected outright.
    /// </summary>
    private static bool IsUsable(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        var u = uuid.Trim().Trim('{', '}').ToUpperInvariant();
        if (u.Length < 32) return false;
        var stripped = u.Replace("-", "");
        if (stripped.All(c => c == '0') || stripped.All(c => c == 'F')) return false;
        return u is not ("03000200-0400-0500-0006-000700080009"    // seen on several OEM boards
                      or "00000000-0000-0000-0000-000000000000"
                      or "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
    }

    /// <summary>
    /// Both identifiers in a single call. The registry is read through PowerShell rather than
    /// Microsoft.Win32.Registry so this assembly — which everything else references — stays on the
    /// plain net10.0 target with no extra package.
    /// </summary>
    private static (string? uuid, string? guid) ReadIdentifiers()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        // wmic is deprecated and missing on current Windows builds, so ask CIM.
        const string script =
            "$u=''; try { $u=(Get-CimInstance Win32_ComputerSystemProduct).UUID } catch {}; " +
            "$g=''; try { $g=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Cryptography').MachineGuid } catch {}; " +
            "Write-Output $u; Write-Output $g";
        var raw = RunQuiet("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");
        if (raw == null) return (null, null);
        var lines = raw.Split('\n').Select(l => l.Trim()).ToArray();
        return (lines.ElementAtOrDefault(0), lines.ElementAtOrDefault(1));
    }

    /// <summary>Runs a helper with no window and a hard timeout; identity must never hang a login.</summary>
    private static string? RunQuiet(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return null; }
            return output.Trim();
        }
        catch { return null; }
    }
}
