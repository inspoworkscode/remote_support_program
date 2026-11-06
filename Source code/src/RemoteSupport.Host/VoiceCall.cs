using Microsoft.Win32;
using RemoteSupport.Media;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// The customer's end of a voice call (3.3; docs/NEXT-SESSION-VOICE.md).
///
/// A technician presses Talk: their voice comes out of this machine's speakers, and the person at
/// it is asked — through their tray app — whether to use their microphone. Clint's answers,
/// 2026-09-30, are the rules here: the technician's voice needs no permission; the MICROPHONE opens
/// only on the person's own "yes", every session; never in an unattended session (the hub enforces
/// that, because only the hub knows for certain which kind this is).
///
/// THE ORDER MATTERS. The player opens first and stays open, playing silence whenever nobody is
/// talking, for the whole call: Windows' echo canceller — which the microphone runs through —
/// delivers nothing at all while no render stream is open (micprobe, 2026-09-30). Close the player
/// between sentences and the customer's microphone goes dead with it.
///
/// Control messages arrive on the socket's receive thread; opening a device takes a moment, so the
/// work is done off it, one change at a time.
/// </summary>
public sealed class VoiceCall : IDisposable
{
    private readonly Action<Msg> _send;
    private readonly Func<byte[], bool> _sendBinary;
    private readonly object _lock = new();
    private readonly List<string> _talkers = new();
    private VoicePlayer? _player;
    private EchoCancelledMic? _mic;
    private string _micState = VoiceMic.Off, _detail = "";
    private uint _seq;
    private int _framesIn;
    /// <summary>Frames of the technician's voice received this call — for the trace, and for voicewire.</summary>
    public int FramesIn => _framesIn;
    /// <summary>Packets the player has actually played (voicewire checks the voice reached the speakers).</summary>
    public int PacketsPlayed => _player?.PacketsPlayed ?? _lastPlayed;
    private int _lastPlayed;

    /// <summary>The call started or ended. Listen leaves the host's own sound out of what it
    /// captures while a call is on, or the technician would hear their own voice come back.</summary>
    public event Action<bool>? CallChanged;

    public bool InCall { get { lock (_lock) return _talkers.Count > 0; } }

    public VoiceCall(Action<Msg> send, Func<byte[], bool> sendBinary)
    {
        _send = send;
        _sendBinary = sendBinary;
    }

    /// <summary>A technician's Talk went on or off.</summary>
    public void Talk(VoiceTalkMsg m) => Run(() =>
    {
        var who = string.IsNullOrWhiteSpace(m.Technician) ? "Your technician" : m.Technician.Trim();
        bool started = false, ended = false;
        lock (_lock)
        {
            if (m.On)
            {
                if (!_talkers.Contains(who)) _talkers.Add(who);
                if (_player == null)
                {
                    var player = new VoicePlayer(HostTrace.Write);
                    if (!player.Start())
                    {
                        // No speakers, no call: nothing the technician says could be heard, and the
                        // echo canceller would have no render stream to run against either.
                        _talkers.Clear();
                        _micState = VoiceMic.Off;
                        _detail = "this computer's speakers could not be opened — " + (player.Error ?? "no reason given");
                        HostTrace.Write("voice: " + _detail);
                    }
                    else
                    {
                        _player = player;
                        _micState = VoiceMic.Asking;
                        _detail = "";
                        _framesIn = 0;
                        started = true;
                        HostTrace.Write($"voice: call started by {who}; asking the person at the machine about their microphone");
                    }
                }
            }
            else
            {
                _talkers.Remove(who);
                if (_talkers.Count == 0 && _player != null) { EndLocked("the technician stopped talking"); ended = true; }
            }
        }
        if (started) CallChanged?.Invoke(true);
        if (ended) CallChanged?.Invoke(false);
        Report();
    });

    /// <summary>The person at the machine answered, from their tray app: use my microphone, or stop.</summary>
    public void MicAnswer(bool allow) => Run(() =>
    {
        lock (_lock)
        {
            if (_talkers.Count == 0) { HostTrace.Write("voice: a microphone answer arrived with no call on; ignored"); return; }
            if (!allow)
            {
                StopMicLocked();
                _micState = VoiceMic.Declined;
                _detail = "";
                HostTrace.Write("voice: the person at the machine said no to (or turned off) their microphone");
            }
            else if (_mic == null)
            {
                // Windows' own answer comes first. A person who switched their microphone off in
                // Windows has answered already; clicking "yes" in our banner is not them changing
                // that, and it is not ours to override even if SYSTEM could.
                var refused = MicPrivacy.Refused();
                if (refused.Length > 0)
                {
                    _micState = VoiceMic.Unavailable;
                    _detail = refused;
                    HostTrace.Write("voice: " + refused);
                }
                else
                {
                    var mic = new EchoCancelledMic(pcm => _sendBinary(Bin.Voice(unchecked(_seq++), pcm)), HostTrace.Write);
                    if (mic.Start(out var why))
                    {
                        _mic = mic;
                        _micState = VoiceMic.On;
                        _detail = mic.DeviceName;
                    }
                    else
                    {
                        _micState = VoiceMic.Unavailable;
                        _detail = why;
                        HostTrace.Write("voice: the microphone would not open — " + why);
                    }
                }
            }
        }
        Report();
    });

    /// <summary>The server's list of technicians in the session. A console that vanished without
    /// saying "stop talking" is taken out of the call here, or its call would hold this machine's
    /// microphone open with nobody listening — the same shape as the clipboard leak of §40.</summary>
    public void Participants(IReadOnlyCollection<string> technicians) => Run(() =>
    {
        bool ended = false;
        lock (_lock)
        {
            int before = _talkers.Count;
            _talkers.RemoveAll(t => !technicians.Contains(t));
            if (_talkers.Count == before) return;
            if (_talkers.Count == 0 && _player != null) { EndLocked("every technician in the call has left the session"); ended = true; }
        }
        if (ended) CallChanged?.Invoke(false);
        Report();
    });

    /// <summary>A frame of a technician's voice, from the wire.</summary>
    public void Frame(ReadOnlyMemory<byte> frame)
    {
        var player = _player;
        if (player == null || frame.Length <= Bin.AudioHeader) return;
        var (_, rate, channels, format, pcm) = Bin.ParseAudio(frame);
        // Refused out loud, once, rather than played as noise.
        if (rate != VoiceWire.Rate || channels != 1 || format != AudioFormat.Pcm16)
        {
            if (_framesIn++ == 0) HostTrace.Write($"voice: refusing frames of {rate} Hz {channels} ch format {format}");
            return;
        }
        if (_framesIn++ == 0) HostTrace.Write("voice: the technician's voice is arriving");
        player.Feed(pcm.Span);
    }

    /// <summary>The session is over. Everything closes, synchronously: the process may be about to go.</summary>
    public void End()
    {
        bool ended;
        lock (_lock) { ended = _player != null; _talkers.Clear(); if (ended) EndLocked("the session ended"); }
        if (ended) CallChanged?.Invoke(false);
    }

    public void Dispose() => End();

    private void EndLocked(string why)
    {
        StopMicLocked();
        var p = _player; _player = null;
        p?.Stop();
        _lastPlayed = p?.PacketsPlayed ?? _lastPlayed;
        _micState = VoiceMic.Off;
        _detail = "";
        HostTrace.Write($"voice: call ended — {why}; {_framesIn} frames of the technician's voice arrived");
    }

    private void StopMicLocked()
    {
        var m = _mic; _mic = null;
        m?.Stop();
    }

    private void Report()
    {
        VoiceStateMsg s;
        lock (_lock)
            s = new VoiceStateMsg
            {
                Call = _talkers.Count > 0,
                Technicians = string.Join(" and ", _talkers),
                Mic = _talkers.Count > 0 ? _micState : VoiceMic.Off,
                Detail = _detail,
            };
        try { _send(s); } catch { }
    }

    // One change at a time, in the order they arrived, off the socket's receive thread.
    private Task _tail = Task.CompletedTask;
    private readonly object _tailLock = new();
    private void Run(Action a)
    {
        lock (_tailLock)
            _tail = _tail.ContinueWith(_ =>
            {
                try { a(); }
                catch (Exception ex) { HostTrace.Write("voice: " + ex.GetType().Name + ": " + ex.Message); }
            }, TaskScheduler.Default);
    }
}

/// <summary>
/// Windows' own microphone privacy switches, read the way Windows stores them. micprobe prints the
/// same values. "Deny" anywhere that applies to us means the person has answered already.
/// </summary>
internal static class MicPrivacy
{
    private const string Consent = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Empty when nothing refuses; otherwise a sentence for the technician and the customer.</summary>
    public static string Refused()
    {
        try
        {
            using (var pol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy"))
                if (pol?.GetValue("LetAppsAccessMicrophone") is int p && p == 2)
                    return "a policy on this computer forbids apps from using the microphone";

            using (var k = Registry.LocalMachine.OpenSubKey(Consent))
                if (IsDeny(k))
                    return "microphone access is switched off for this whole computer in Windows' privacy settings";

            // The signed-in person's registry, not SYSTEM's: HKEY_USERS\<their SID>. (A host run as
            // the person, which only happens in development, reads its own.)
            string prefix = "";
            RegistryKey root = Registry.CurrentUser;
            if (UserContext.IsSystem)
            {
                var sid = UserContext.InteractiveSid;
                if (sid.Length == 0) return "";
                root = Registry.Users;
                prefix = sid + @"\";
            }
            using (var k = root.OpenSubKey(prefix + Consent))
                if (IsDeny(k)) return "microphone access is switched off in Windows' privacy settings on this computer";
            using (var k = root.OpenSubKey(prefix + Consent + @"\NonPackaged"))
                if (IsDeny(k)) return "\"Let desktop apps access your microphone\" is switched off in Windows' privacy settings";
        }
        catch (Exception ex) { HostTrace.Write("voice: could not read the microphone privacy settings — " + ex.Message); }
        return "";
    }

    private static bool IsDeny(RegistryKey? k) =>
        string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
}
