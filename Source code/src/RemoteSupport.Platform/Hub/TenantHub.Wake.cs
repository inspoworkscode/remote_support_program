using System.Net;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Wake-on-LAN (roadmap 1.8, 2026-10-06). Right-click an offline machine in the console → Wake up.
/// A switched-off PC can only be woken from its own network, so the server asks ANOTHER machine there
/// to send the wake-up — Clint's choice: "via another of their PCs", nothing to set up on the
/// customer's router.
///
/// <para>The rules, and why:</para>
/// <list type="bullet">
/// <item><b>Same place</b>: the sender must have connected from the same internet address as the
/// sleeping machine last did AND share one of its networks (<c>192.168.1.0/24</c>). Either alone is
/// not enough — every office has a 192.168.1.0/24, and a public address can be shared by a building.</item>
/// <item><b>Same customer</b>, when the machine is linked to one — Clint's words were "another of
/// THEIR PCs". An unlinked machine may be woken by any of the company's machines at that place.</item>
/// <item><b>The sender is handed MAC addresses only</b>, never an address to send to; it broadcasts on
/// its own networks (<see cref="WakeOnLan.Send"/>). A wake request cannot become traffic aimed anywhere.</item>
/// <item>Any technician on the console may ask. <b>Nothing is logged and no ticket is made</b> — a
/// wake-up changes nothing on any machine but whether it is on, like pressing its power button.</item>
/// </list>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>deviceId -> its network cards and the address it last connected from. Guarded by <c>_lock</c>.</summary>
    private readonly Dictionary<string, (List<NetworkCard> Cards, string PublicAddress)> _net = new();

    /// <summary>A wake-up handed to a machine and not answered yet: who asked, and for which machine.</summary>
    private readonly Dictionary<string, (TechConn Asker, string DeviceId, string Target, string Sender, DeviceConn From)> _wakes = new();

    /// <summary>How long a sender has to say it sent the wake-up before the technician is told it did not answer.</summary>
    private const int WakeAnswerMs = 15000;

    /// <summary>An address as one string whatever way it arrived: IPv4 mapped into IPv6 is plain IPv4.</summary>
    internal static string PlainAddress(IPAddress? ip) =>
        ip == null ? "" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    /// <summary>What a machine says about its networks, kept for when it is switched off. Call under <c>_lock</c>.</summary>
    private void RememberNetwork(string deviceId, List<NetworkCard>? cards, string publicAddress)
    {
        var had = _net.TryGetValue(deviceId, out var was) ? was : (new List<NetworkCard>(), "");
        var keep = cards == null ? had.Item1 : Clean(cards);
        _net[deviceId] = (keep, publicAddress.Length > 0 ? publicAddress : had.Item2);
        if (_devices.TryGetValue(deviceId, out var info)) RecomputeCanWake(info);
    }

    /// <summary>The stored cards, or none when nothing was stored or it cannot be read.</summary>
    private static List<NetworkCard>? ReadCards(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Json.Deserialize<List<NetworkCard>>(json); } catch { return null; }
    }

    /// <summary>Only what a wake-up can use: well-formed MACs, well-formed networks, no repeats.</summary>
    private static List<NetworkCard> Clean(IEnumerable<NetworkCard> cards) => cards
        .Select(c => new NetworkCard
        {
            Mac = WakeOnLan.NormaliseMac(c.Mac) ?? "",
            Wired = c.Wired,
            Networks = (c.Networks ?? new()).Where(n => n.Count(ch => ch == '/') == 1 && n.Length <= 18).Distinct().Take(8).ToList()
        })
        .Where(c => c.Mac.Length == 12).GroupBy(c => c.Mac).Select(g => g.First()).Take(8).ToList();

    /// <summary>Whether the server knows enough to try. Call under <c>_lock</c>.</summary>
    private void RecomputeCanWake(DeviceInfo info) =>
        info.CanWake = _net.TryGetValue(info.DeviceId, out var n) && n.Cards.Count > 0 && n.PublicAddress.Length > 0;

    private void WakeMachine(TechConn tc, WakeRequestMsg m)
    {
        string? refusal = null; DeviceConn? sender = null; string target = m.DeviceId, senderName = "";
        List<string> macs = new();
        var requestId = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            if (!_devices.TryGetValue(m.DeviceId, out var info)) refusal = "That machine is not known.";
            else
            {
                target = info.MachineName;
                if (info.Online) refusal = $"{target} is already on.";
                else if (!_net.TryGetValue(m.DeviceId, out var net) || net.Cards.Count == 0 || net.PublicAddress.Length == 0)
                    refusal = $"{target} has never told the server its network card, so it cannot be woken from here. " +
                              "It needs the current support client, and to have been switched on once since it was installed.";
                else
                {
                    macs = net.Cards.Select(c => c.Mac).ToList();
                    var theirNetworks = net.Cards.SelectMany(c => c.Networks).ToList();
                    sender = _deviceConns
                        .Where(d => d.CanWake && d.DeviceId != m.DeviceId && !_closedByUser.Contains(d.DeviceId)
                                    && _net.TryGetValue(d.DeviceId, out var dn) && dn.PublicAddress == net.PublicAddress
                                    && WakeOnLan.ShareANetwork(dn.Cards.SelectMany(c => c.Networks), theirNetworks)
                                    && _devices.TryGetValue(d.DeviceId, out var di)
                                    && (info.ClientId == 0 || di.ClientId == info.ClientId))
                        // The agent first: it is up whether or not anybody is logged on.
                        .OrderBy(d => d.Role == DeviceRole.Agent ? 0 : 1)
                        .FirstOrDefault();
                    if (sender == null)
                    {
                        var whose = info.ClientId != 0 && info.ClientName.Length > 0 ? $"{info.ClientName}'s" : "the company's";
                        refusal = $"None of {whose} other machines on {target}'s network is switched on with the support app, " +
                                  "so nothing there can wake it. Wake-on-LAN needs one machine at that place to be on.";
                    }
                    else
                    {
                        senderName = _devices.TryGetValue(sender.DeviceId, out var si) ? si.MachineName : sender.DeviceId;
                        _wakes[requestId] = (tc, m.DeviceId, target, senderName, sender);
                    }
                }
            }
        }
        if (refusal != null) { tc.Ws.Send(new WakeResultMsg { DeviceId = m.DeviceId, Sent = false, Message = refusal }); return; }

        sender!.Ws.Send(new WakeSendMsg { RequestId = requestId, ForMachine = target, Macs = macs });
        _log.LogInformation("[{Tenant}] {Who} asked {Sender} to wake {Target}", TenantName, tc.Who.DisplayName, senderName, target);
        _ = Task.Delay(WakeAnswerMs).ContinueWith(_ =>
        {
            (TechConn Asker, string DeviceId, string Target, string Sender, DeviceConn From) w;
            lock (_lock) { if (!_wakes.Remove(requestId, out w)) return; }
            w.Asker.Ws.Send(new WakeResultMsg { DeviceId = w.DeviceId, Sent = false,
                Message = $"{w.Sender} was asked to wake {w.Target} but did not answer. Try again in a moment." });
        });
    }

    /// <summary>The sending machine's answer, to the technician who asked — and nobody else.</summary>
    private void WakeAnswered(DeviceConn dc, WakeSentMsg m)
    {
        (TechConn Asker, string DeviceId, string Target, string Sender, DeviceConn From) w;
        lock (_lock)
        {
            // Only an answer to a request THIS machine was given; anything else is dropped.
            if (!_wakes.TryGetValue(m.RequestId, out w) || w.From != dc) return;
            _wakes.Remove(m.RequestId);
        }
        w.Asker.Ws.Send(new WakeResultMsg
        {
            DeviceId = w.DeviceId,
            Sent = m.Sent,
            Message = m.Sent
                ? $"The wake-up was sent from {w.Sender}. {w.Target} should turn green within a couple of minutes. " +
                  "If it does not, Wake-on-LAN is probably switched off in its BIOS or network card settings — or it is on Wi-Fi only, which seldom wakes."
                : $"{w.Sender} could not send the wake-up: {m.Error}"
        });
    }

    /// <summary>A console has gone: its unanswered wake requests go with it.</summary>
    private void ForgetWakes(TechConn tc)
    {
        lock (_lock) foreach (var id in _wakes.Where(kv => kv.Value.Asker == tc).Select(kv => kv.Key).ToList()) _wakes.Remove(id);
    }
}
