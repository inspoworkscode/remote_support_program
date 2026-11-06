using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteSupport.Shared;

/// <summary>
/// One network card on a machine, as it reports itself in its hello (Wake-on-LAN, roadmap 1.8,
/// 2026-10-06): the MAC address a wake-up is addressed to, whether it is a cable or Wi-Fi, and the
/// IPv4 networks it is on right now (<c>192.168.1.0/24</c>). Kept by the server, so it is still known
/// when the machine is switched off — which is the only time anybody needs it.
/// </summary>
public sealed class NetworkCard
{
    /// <summary>Twelve hex digits, upper case, no separators.</summary>
    public string Mac { get; set; } = "";
    public bool Wired { get; set; }
    /// <summary>The networks this card is on now, as <c>a.b.c.d/len</c>; empty when it is not connected.</summary>
    public List<string> Networks { get; set; } = new();
}

/// <summary>
/// Wake-on-LAN (roadmap 1.8). A switched-off PC can only be woken by something on its own network, so
/// the server asks ANOTHER machine there to send the wake-up — Clint's choice, 2026-10-06: "via another
/// of their PCs", no router setup at the customer.
///
/// <para>The wake-up is the "magic packet": six bytes of FF and the MAC sixteen times, sent as UDP to
/// the broadcast address. The machine sending it only ever broadcasts on ITS OWN networks — the server
/// hands it MAC addresses and nothing else, never an address to send to — so a wake request can never
/// be turned into traffic aimed anywhere else.</para>
///
/// <para>Whether the woken machine actually starts is up to it: Wake-on-LAN has to be on in its BIOS
/// and its network card's settings, and over Wi-Fi it almost never works. The console says so.</para>
/// </summary>
public static class WakeOnLan
{
    /// <summary>The ports a wake-up is sent to: 9 (discard), the usual one, and 7, which some cards want.</summary>
    public static readonly int[] Ports = { 9, 7 };

    /// <summary>
    /// The physical network cards on this machine — cable and Wi-Fi, whether connected or not (a laptop
    /// on Wi-Fi is woken through its cable card). Virtual adapters (Hyper-V, VPNs, VMware, Bluetooth)
    /// and locally administered MACs are left out: nothing can wake a machine through them.
    /// </summary>
    public static List<NetworkCard> Cards()
    {
        var list = new List<NetworkCard>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Wireless80211)) continue;
                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                if (new[] { "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "wan miniport", "bluetooth", "loopback", "wi-fi direct", "tunnel" }
                    .Any(text.Contains)) continue;
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6 || bytes.All(b => b == 0) || (bytes[0] & 0x02) != 0) continue;
                var card = new NetworkCard { Mac = Convert.ToHexString(bytes), Wired = nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 };
                if (nic.OperationalStatus == OperationalStatus.Up)
                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is > 0 and < 32)
                            card.Networks.Add(NetworkOf(u.Address, u.PrefixLength));
                if (list.All(c => c.Mac != card.Mac)) list.Add(card);
            }
        }
        catch { }
        return list;
    }

    /// <summary><c>192.168.1.37</c> and 24 → <c>192.168.1.0/24</c>.</summary>
    public static string NetworkOf(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint mask = prefix <= 0 ? 0 : uint.MaxValue << (32 - prefix);
        v &= mask;
        return $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}/{prefix}";
    }

    /// <summary>A MAC as the server keeps it — twelve hex digits — or null for anything else.</summary>
    public static string? NormaliseMac(string? mac)
    {
        if (mac == null) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 && hex != "000000000000" && hex != "FFFFFFFFFFFF" ? hex : null;
    }

    /// <summary>The magic packet for one MAC: FF×6, then the MAC sixteen times — 102 bytes.</summary>
    public static byte[] MagicPacket(string mac)
    {
        var m = Convert.FromHexString(NormaliseMac(mac) ?? throw new ArgumentException("not a MAC address", nameof(mac)));
        var p = new byte[102];
        for (int i = 0; i < 6; i++) p[i] = 0xFF;
        for (int r = 0; r < 16; r++) Buffer.BlockCopy(m, 0, p, 6 + r * 6, 6);
        return p;
    }

    /// <summary>
    /// Sends the wake-up for every MAC given, out of EVERY network this machine is on: to the limited
    /// broadcast (255.255.255.255) and to that network's own broadcast address, on ports 9 and 7,
    /// from a socket bound to that network's address — a broadcast from an unbound socket leaves by
    /// one card only, which is the wrong one on any machine with two. Returns how many packets went
    /// out, or throws with the reason when none could.
    /// </summary>
    public static int Send(IEnumerable<string> macs, Action<IPEndPoint, IPEndPoint, byte[]>? sender = null)
    {
        var packets = macs.Select(NormaliseMac).Where(m => m != null).Distinct().Select(m => MagicPacket(m!)).ToList();
        if (packets.Count == 0) throw new InvalidOperationException("no network card address to wake");
        sender ??= SendUdp;
        int sent = 0; string? last = null;
        foreach (var (local, broadcast) in LocalNetworks())
            foreach (var to in new[] { IPAddress.Broadcast, broadcast }.Distinct())
                foreach (var port in Ports)
                    foreach (var p in packets)
                    {
                        try { sender(new IPEndPoint(local, 0), new IPEndPoint(to, port), p); sent++; }
                        catch (Exception ex) { last = ex.Message; }
                    }
        if (sent == 0) throw new InvalidOperationException(last ?? "this machine is not on any network it could send from");
        return sent;
    }

    /// <summary>This machine's IPv4 addresses that are up, with each one's broadcast address.</summary>
    public static List<(IPAddress Local, IPAddress Broadcast)> LocalNetworks()
    {
        var list = new List<(IPAddress, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.PrefixLength is <= 0 or >= 31) continue;
                var b = u.Address.GetAddressBytes();
                uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                uint bc = v | ~(uint.MaxValue << (32 - u.PrefixLength));
                list.Add((u.Address, new IPAddress(new[] { (byte)(bc >> 24), (byte)(bc >> 16), (byte)(bc >> 8), (byte)bc })));
            }
        }
        return list;
    }

    private static void SendUdp(IPEndPoint from, IPEndPoint to, byte[] packet)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        s.Bind(from);
        s.SendTo(packet, to);
    }

    /// <summary>Whether two machines share a network: any <c>a.b.c.d/len</c> in common.</summary>
    public static bool ShareANetwork(IEnumerable<string> a, IEnumerable<string> b) => a.Intersect(b).Any();
}
