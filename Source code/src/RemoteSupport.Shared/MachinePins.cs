namespace RemoteSupport.Shared;

/// <summary>
/// The machine identity keys a console has seen (2026-10-07, end-to-end encryption, docs/NEXT-SESSION-E2E.md):
/// remembered the first time, and a change NOT accepted silently — Clint's answer 1.
///
/// <para>Pinned by device id AND by machine name + whose it is, because a reinstall (or an impostor) arrives with a
/// NEW device id under a name the technician recognises. A pin either way that disagrees is a change. A pinned
/// machine that answers WITHOUT a signature is a change too: a server pretending to be the host cannot sign, and
/// would otherwise only have to leave the signature out.</para>
///
/// <para>Per Windows user, in <c>%LOCALAPPDATA%\RemoteSupport\machine-keys.json</c>. First contact is believed
/// (TOFU) — the brief says so plainly.</para>
/// </summary>
public sealed class MachinePins
{
    public sealed class Pin
    {
        public string DeviceId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string Owner { get; set; } = "";
        public string IdentityPub { get; set; } = "";
        public DateTimeOffset First { get; set; }
        public DateTimeOffset Last { get; set; }
    }

    public enum Verdict
    {
        /// <summary>Signed by the key on file.</summary>
        Matches,
        /// <summary>Signed, and never seen: remembered from now.</summary>
        FirstTime,
        /// <summary>Signed by a DIFFERENT key than the one on file: hold the session and ask.</summary>
        Changed,
        /// <summary>A machine on file answered without a signature: hold and ask, as for a change.</summary>
        Unsigned,
        /// <summary>Never seen, and no signature (a host running as the person, browser support): encrypted, identity not checked.</summary>
        NoIdentity,
    }

    private readonly string _path;
    private readonly List<Pin> _pins;
    private readonly object _lock = new();

    public MachinePins(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "machine-keys.json");
        try { _pins = File.Exists(_path) ? Json.Deserialize<List<Pin>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch { _pins = new(); }   // a damaged file forgets — every machine is then first contact again, never trusted wrongly
    }

    private IEnumerable<Pin> Candidates(string deviceId, string machine, string owner) =>
        _pins.Where(p => p.DeviceId == deviceId
            || (p.MachineName.Equals(machine, StringComparison.OrdinalIgnoreCase) && p.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What this answer means for this machine, and the pin it disagrees with (for the warning's words).</summary>
    public (Verdict Verdict, Pin? Previous) Check(string deviceId, string machine, string owner, string identityPub)
    {
        lock (_lock)
        {
            var known = Candidates(deviceId, machine ?? "", owner ?? "").ToList();
            if (string.IsNullOrEmpty(identityPub))
                return known.Count > 0 ? (Verdict.Unsigned, known[0]) : (Verdict.NoIdentity, null);
            var other = known.FirstOrDefault(p => p.IdentityPub != identityPub);
            if (other != null) return (Verdict.Changed, other);
            return known.Count > 0 ? (Verdict.Matches, known[0]) : (Verdict.FirstTime, null);
        }
    }

    /// <summary>Believe this key for this machine from now on: on first contact, or when the technician says the change
    /// was expected. Every pin it disagreed with goes.</summary>
    public void Remember(string deviceId, string machine, string owner, string identityPub)
    {
        if (string.IsNullOrEmpty(identityPub)) return;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var keep = Candidates(deviceId, machine ?? "", owner ?? "").FirstOrDefault(p => p.IdentityPub == identityPub && p.DeviceId == deviceId);
            _pins.RemoveAll(p => p != keep && (p.DeviceId == deviceId
                || (p.MachineName.Equals(machine, StringComparison.OrdinalIgnoreCase) && p.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))));
            if (keep != null) { keep.Last = now; keep.MachineName = machine ?? ""; keep.Owner = owner ?? ""; }
            else _pins.Add(new Pin { DeviceId = deviceId, MachineName = machine ?? "", Owner = owner ?? "", IdentityPub = identityPub, First = now, Last = now });
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(_pins));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }   // a pin not saved is asked again next time — the safe direction
    }
}
