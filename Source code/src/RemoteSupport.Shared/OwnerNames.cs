namespace RemoteSupport.Shared;

/// <summary>
/// Whose machine a device list says it is — the ONE rule, for the console (<see cref="DeviceInfo.OwnerName"/>) and the
/// server (ContactService.OwnerName) alike.
///
/// <para>The person if we know them, else the company, else the name they gave when they asked for help, else whoever is
/// signed in to Windows there. The third is new (2026-10-18). Clint: technicians were not linking machines straight away,
/// so the list filled with DESKTOP-names and Windows sign-ins nobody recognised. A support request has always asked for a
/// name; now the machine keeps it. It is what the person TYPED, never checked, so the lists mark it, and it gives way the
/// moment anybody links the machine — to a client or a person.</para>
/// </summary>
public static class OwnerNames
{
    /// <summary>The longest name kept from a request. A name, not an essay — the box is free text.</summary>
    public const int MaxLength = 100;

    public static string For(string contactName, string clientName, string requestedBy, string userName)
    {
        if (!string.IsNullOrWhiteSpace(contactName)) return contactName.Trim();
        if (!string.IsNullOrWhiteSpace(clientName)) return clientName.Trim();
        if (!string.IsNullOrWhiteSpace(requestedBy)) return requestedBy.Trim();
        if (!string.IsNullOrWhiteSpace(userName)) return userName.Trim();
        return "—";
    }

    /// <summary>The name shown is only what somebody typed at a support request: nobody has linked the machine.</summary>
    public static bool IsRequested(string contactName, string clientName, string requestedBy) =>
        string.IsNullOrWhiteSpace(contactName) && string.IsNullOrWhiteSpace(clientName) && !string.IsNullOrWhiteSpace(requestedBy);

    /// <summary>What is kept of a typed name: spaces tidied, at most <see cref="MaxLength"/> characters. Empty stays empty —
    /// an empty box never wipes a name given before.</summary>
    public static string Clean(string? typed)
    {
        var words = (typed ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var s = string.Join(' ', words);
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() : s;
    }

    /// <summary>
    /// How well a contact's name fits the name somebody gave: 2 the same (case and spacing aside), 1 every word of the
    /// shorter is a word of the longer ("John" and "John Smith", "Smith John"), 0 not at all. Only ever a SUGGESTION —
    /// two John Smiths at two companies are both offered and a person picks.
    /// </summary>
    public static int Likeness(string given, string candidate)
    {
        var a = Words(given); var b = Words(candidate);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.SequenceEqual(b)) return 2;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.All(w => longer.Contains(w)) ? 1 : 0;
    }

    private static string[] Words(string? s) =>
        (s ?? "").ToLowerInvariant().Split(new[] { ' ', '\t', ',', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
}
