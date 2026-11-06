using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteSupport.Shared;

/// <summary>Stable per-machine identity, shared by the client app and the unattended agent.</summary>
public sealed class DeviceIdentity
{
    public string DeviceId { get; set; } = "";
    public string DeviceKey { get; set; } = "";

    public static string MachineDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteSupport");
    public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport");

    public static DeviceIdentity LoadOrCreate()
    {
        foreach (var dir in new[] { MachineDir, UserDir })
        {
            var p = Path.Combine(dir, "device.json");
            if (File.Exists(p))
            {
                try { var d = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(p), Json.Options); if (d != null && d.DeviceId.Length > 0) return d; } catch { }
            }
        }
        var id = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        foreach (var dir in new[] { MachineDir, UserDir })
        {
            try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "device.json"), JsonSerializer.Serialize(id, Json.Options)); return id; } catch { }
        }
        return id;
    }
}
