using System.Net;
using System.Net.Sockets;

namespace RemoteSupport.Shared;

/// <summary>
/// The one rule for when an app may talk to the server WITHOUT encryption: only inside a private
/// network. Anywhere else it must be https:// or wss://, and is refused otherwise.
///
/// Why it exists (2026-10-01, security before go-live): every app turned "http://" into "ws://"
/// and connected without a word. The live server is behind TLS, but one wrong PublicUrl, or a
/// brand.json edited by hand, would have sent passwords, screens and keystrokes across the
/// internet in the clear, and nothing anywhere would have said so. Development stays as it is —
/// the office server is 192.168.1.115, which is private — so the rule costs nothing on the LAN.
///
/// Certificates are checked by Windows in the usual way. Nothing in this product turns that off,
/// and nothing should: a connection that accepts any certificate is not encrypted to anyone in
/// particular.
/// </summary>
public static class SecureTransport
{
    /// <summary>
    /// Null when this address may be used; otherwise the sentence to show and log. Encrypted
    /// schemes (https, wss) always pass. Plain ones pass only for a private address.
    /// </summary>
    public static string? Refusal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "No server address is set.";
        if (!System.Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return $"'{url}' is not a server address.";
        if (u.Scheme is "https" or "wss") return null;
        if (u.Scheme is not ("http" or "ws")) return $"'{u.Scheme}' is not a way to reach the server.";
        if (IsPrivateHost(u.Host)) return null;
        return $"Refusing to connect to {u.Host} without encryption. The server address must start with https:// " +
               "(or wss://). Please reinstall from your provider's download page.";
    }

    /// <summary>Loopback, the private ranges, link-local, and names that cannot be on the internet.</summary>
    public static bool IsPrivateHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local")
            || host.EndsWith(".lan") || host.EndsWith(".test") || host.EndsWith(".internal")
            || host.EndsWith(".home.arpa"))
            return true;
        if (!IPAddress.TryParse(host, out var ip)) return !host.Contains('.');   // a bare machine name
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // carrier-grade NAT, e.g. Tailscale
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
    }

    /// <summary>Throws with <see cref="Refusal"/>'s sentence; for the places that connect inside a retry loop that logs.</summary>
    public static void Require(string url)
    {
        var why = Refusal(url);
        if (why != null) throw new InsecureServerException(why);
    }
}

public sealed class InsecureServerException : Exception
{
    public InsecureServerException(string message) : base(message) { }
}
