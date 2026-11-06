using System.Runtime.InteropServices;

namespace RemoteSupport.Shared;

/// <summary>
/// Reads the handful of hardware facts a technician wants at a glance: processor, memory and disk.
/// Windows-only in practice; returns whatever it can and never throws.
/// </summary>
public static class SystemSpecCollector
{
    public static SystemSpec Collect()
    {
        var spec = new SystemSpec { Cores = Environment.ProcessorCount };

        try { spec.Cpu = ReadCpuName(); } catch { }
        if (string.IsNullOrWhiteSpace(spec.Cpu))
            spec.Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";

        try { spec.Os = ReadOsName(); } catch { }
        if (string.IsNullOrWhiteSpace(spec.Os)) spec.Os = Environment.OSVersion.VersionString;

        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem)) spec.RamMb = (int)(mem.ullTotalPhys / (1024 * 1024));
        }
        catch { }

        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (!string.IsNullOrEmpty(root))
            {
                var d = new DriveInfo(root);
                if (d.IsReady)
                {
                    spec.DiskTotalGb = (int)(d.TotalSize / 1_073_741_824L);
                    spec.DiskFreeGb = (int)(d.AvailableFreeSpace / 1_073_741_824L);
                }
            }
        }
        catch { }

        return spec;
    }

    /// <summary>The friendly processor name (e.g. "Intel(R) Core(TM) i7-8565U CPU @ 1.80GHz").</summary>
    private static string ReadCpuName() =>
        ReadRegString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");

    /// <summary>
    /// Edition plus feature update, e.g. "Windows 11 Pro 24H2 (26100)". Windows 11 still reports
    /// itself as "Windows 10" in the registry, so the build number decides.
    /// </summary>
    private static string ReadOsName()
    {
        if (!OperatingSystem.IsWindows()) return "";
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = ReadRegString(key, "ProductName");
        if (string.IsNullOrWhiteSpace(product)) return "";
        var display = ReadRegString(key, "DisplayVersion");
        if (string.IsNullOrWhiteSpace(display)) display = ReadRegString(key, "ReleaseId");
        var buildText = ReadRegString(key, "CurrentBuildNumber");
        int.TryParse(buildText, out var build);
        if (build >= 22000 && product.Contains("Windows 10")) product = product.Replace("Windows 10", "Windows 11");
        var parts = new List<string> { product.Trim() };
        if (!string.IsNullOrWhiteSpace(display)) parts.Add(display.Trim());
        var text = string.Join(' ', parts);
        return build > 0 ? $"{text} ({build})" : text;
    }

    private static string ReadRegString(string subKey, string name)
    {
        if (!OperatingSystem.IsWindows()) return "";
        if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, subKey, 0, KEY_READ, out var h) != 0) return "";
        try
        {
            var buf = new byte[512];
            int len = buf.Length;
            if (RegQueryValueEx(h, name, 0, out _, buf, ref len) != 0) return "";
            return System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, len - 2)).Trim('\0', ' ');
        }
        finally { RegCloseKey(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    private const int KEY_READ = 0x20019;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegOpenKeyEx(IntPtr key, string subKey, int options, int sam, out IntPtr result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegQueryValueEx(IntPtr key, string name, int reserved, out int type, byte[] data, ref int dataLen);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr key);
}
