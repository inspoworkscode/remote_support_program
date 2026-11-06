using System.Security.Cryptography;
using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.Server;

public sealed class TechnicianRecord
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Disabled { get; set; }
}

/// <summary>Technician accounts in data/technicians.json (PBKDF2 hashed). Hard cap on active accounts.</summary>
public sealed class TechnicianStore
{
    private readonly string _path;
    private readonly int _max;
    private readonly object _lock = new();
    private List<TechnicianRecord> _techs = new();

    public TechnicianStore(string dataDir, int max)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "technicians.json");
        _max = max;
        if (File.Exists(_path))
            _techs = JsonSerializer.Deserialize<List<TechnicianRecord>>(File.ReadAllText(_path), Json.Options) ?? new();
    }

    public IReadOnlyList<TechnicianRecord> All { get { lock (_lock) return _techs.ToList(); } }

    public (bool ok, string message) Add(string username, string password, string? displayName = null)
    {
        username = username.Trim().ToLowerInvariant();
        if (username.Length < 2 || password.Length < 6) return (false, "Username must be 2+ chars and password 6+ chars.");
        lock (_lock)
        {
            if (_techs.Any(t => t.Username == username)) return (false, "Technician already exists.");
            if (_techs.Count(t => !t.Disabled) >= _max) return (false, $"Maximum of {_max} technicians reached.");
            var salt = RandomNumberGenerator.GetBytes(16);
            _techs.Add(new TechnicianRecord
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName!,
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(Derive(password, salt))
            });
            Save();
        }
        return (true, "Added.");
    }

    public bool Remove(string username)
    {
        lock (_lock)
        {
            var n = _techs.RemoveAll(t => t.Username == username.Trim().ToLowerInvariant());
            if (n > 0) Save();
            return n > 0;
        }
    }

    public (bool ok, string message) SetPassword(string username, string password)
    {
        if (password.Length < 6) return (false, "Password must be 6+ chars.");
        lock (_lock)
        {
            var t = _techs.FirstOrDefault(x => x.Username == username.Trim().ToLowerInvariant());
            if (t == null) return (false, "Not found.");
            var salt = RandomNumberGenerator.GetBytes(16);
            t.Salt = Convert.ToBase64String(salt);
            t.Hash = Convert.ToBase64String(Derive(password, salt));
            Save();
        }
        return (true, "Password updated.");
    }

    public TechnicianRecord? Verify(string username, string password)
    {
        TechnicianRecord? t;
        lock (_lock) t = _techs.FirstOrDefault(x => x.Username == username.Trim().ToLowerInvariant() && !x.Disabled);
        if (t == null) return null;
        var h = Derive(password, Convert.FromBase64String(t.Salt));
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(t.Hash)) ? t : null;
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_techs, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
}
