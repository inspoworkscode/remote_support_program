using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Media;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Talking to the customer (3.3; docs/NEXT-SESSION-VOICE.md), the technician's end.
///
/// Tools → Talk to the customer starts it: the technician's microphone (through Windows' echo
/// canceller, so a technician without a headset does not send the customer their own voice back)
/// goes to the customer's speakers, and the customer is asked in their tray app whether to use
/// their microphone. Clint chose an OPEN microphone with a Mute button: the status bar's pill
/// mutes and unmutes with one click, and says which it is the whole time — the same shape as the
/// Recording pill beside it. The Tools line ends the call.
///
/// The player opens FIRST and stays open for the whole call, even while nobody speaks: the echo
/// canceller the microphone runs through delivers nothing while no render stream is open.
/// </summary>
public sealed partial class SessionForm
{
    private ToolStripMenuItem _talkItem = null!;
    private Pill _talkPill = null!;
    private Label _voiceLabel = null!;
    private VoicePlayer? _voicePlayer;
    private EchoCancelledMic? _techMic;
    private volatile bool _muted;
    private bool _talking;
    private uint _voiceSeq;
    private int _voiceFramesIn;

    private void BuildTalkItem()
    {
        _talkItem = new ToolStripMenuItem("Talk to the customer") { Font = _t.Body };
        _talkItem.ToolTipText = "Talk through their speakers; they are asked whether to use their microphone";
        _talkItem.Click += (_, _) => ToggleTalk();
        _toolsMenu.Items.Add(_talkItem);
    }

    /// <summary>The pill and the customer's microphone line, docked left on the status bar after
    /// the Recording pill. Hidden until a call starts.</summary>
    private void AddVoiceToStatus(Panel status)
    {
        _talkPill = new Pill(_t, "● Talking — click to mute", _t.OkBg, _t.OkText)
        {
            Size = new Size(TextRenderer.MeasureText("● Muted — click to unmute", new Font("Segoe UI", 8.5f, FontStyle.Bold)).Width + 28, 22),
            Dock = DockStyle.Left, Visible = false, Cursor = Cursors.Hand,
        };
        _talkPill.Click += (_, _) => ToggleMute();
        _toolTip.SetToolTip(_talkPill, "Your microphone. Click to mute or unmute; Tools → Stop talking ends the call");
        _voiceLabel = new Label
        {
            Dock = DockStyle.Left, Width = 300, AutoEllipsis = true, ForeColor = _t.Muted, Font = _t.Small,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, Visible = false, UseMnemonic = false,
        };
        // The LINE first: a control added later docks further out, so this reads pill, then line —
        // "● Talking — click to mute  they can hear you · …" — left to right. viewonlyprobe caught
        // the other order putting the line at the window's edge, ahead of everything.
        status.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        status.Controls.Add(_voiceLabel);
        status.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        status.Controls.Add(_talkPill);
    }

    private void ToggleTalk()
    {
        if (_talking) { EndTalk("you stopped talking", tell: true); return; }
        if (_dead) return;

        // The player first: it is the render stream the echo canceller needs (see the class note).
        _voicePlayer = new VoicePlayer(Trace.Write);
        if (!_voicePlayer.Start())
        {
            SetVoiceLine("your speakers could not be opened — " + (_voicePlayer.Error ?? "no reason given"), bad: true);
            _voicePlayer = null;
            return;
        }
        _muted = false;
        _techMic = new EchoCancelledMic(pcm =>
        {
            // Muted is not "stop the microphone": the canceller keeps learning the room, and
            // unmuting is instant. The frames simply do not go.
            if (_muted) return;
            _link.SendBinary(Bin.Voice(unchecked(_voiceSeq++), pcm));
        }, Trace.Write);
        if (!_techMic.Start(out var why))
        {
            _techMic = null;
            _voicePlayer.Stop(); _voicePlayer = null;
            SetVoiceLine("your microphone could not be opened — " + why, bad: true);
            return;
        }
        _talking = true;
        _voiceFramesIn = 0;
        _link.Send(new VoiceTalkMsg { On = true });
        _talkItem.Text = "Stop talking";
        ShowTalkPill();
        SetVoiceLine($"asking {_session.MachineName}…");
        Trace.Write($"voice: talking, through \"{_techMic.DeviceName}\"");
    }

    private void ToggleMute()
    {
        if (!_talking) return;
        _muted = !_muted;
        ShowTalkPill();
        Trace.Write(_muted ? "voice: muted" : "voice: unmuted");
    }

    private void ShowTalkPill()
    {
        if (_muted) _talkPill.Set("● Muted — click to unmute", _t.WarnBg, _t.WarnText);
        else _talkPill.Set("● Talking — click to mute", _t.OkBg, _t.OkText);
        _talkPill.Visible = true;
    }

    /// <summary>Ends the call at this end. <paramref name="tell"/>: say so to the host — false when
    /// the link is already gone.</summary>
    private void EndTalk(string why, bool tell)
    {
        if (!_talking && _voicePlayer == null && _techMic == null) return;
        if (tell && _talking) { try { _link.Send(new VoiceTalkMsg { On = false }); } catch { } }
        _talking = false;
        _techMic?.Stop(); _techMic = null;
        _voicePlayer?.Stop(); _voicePlayer = null;
        _talkItem.Text = "Talk to the customer";
        _talkPill.Visible = false;
        SetVoiceLine(why);
        Trace.Write($"voice: ended — {why}; {_voiceFramesIn} frames of the customer's voice arrived");
    }

    /// <summary>The host's account of the call: chiefly, where the customer's microphone stands.</summary>
    private void OnVoiceState(VoiceStateMsg v)
    {
        if (!_talking) return;
        if (!v.Call)
        {
            // The host could not start a call at all — its speakers would not open — or it ended
            // one this console still thinks is on.
            EndTalk(v.Detail.Length > 0 ? "no call: " + v.Detail : "the call ended on their side", tell: false);
            return;
        }
        var who = _session.MachineName;
        switch (v.Mic)
        {
            // Short, because the status bar is shared: the pill says what YOUR end is doing, this says
            // theirs. The whole sentence, with any reason, is the tooltip.
            case VoiceMic.Asking: SetVoiceLine("they can hear you · asking to use their microphone"); break;
            case VoiceMic.On: SetVoiceLine("they can hear you · their microphone is on"); break;
            case VoiceMic.Declined: SetVoiceLine("they can hear you · their microphone is off — use chat"); break;
            case VoiceMic.Unavailable: SetVoiceLine("their microphone could not be used: " + v.Detail, bad: true); break;
            case VoiceMic.Unattended: SetVoiceLine("they can hear you · unattended, so no microphone"); break;
            default: SetVoiceLine($"talking to {who}"); break;
        }
    }

    /// <summary>A frame of the customer's voice.</summary>
    private void OnVoiceFrame(ReadOnlyMemory<byte> frame)
    {
        var player = _voicePlayer;
        if (player == null || frame.Length <= Bin.AudioHeader) return;
        var (_, rate, channels, format, pcm) = Bin.ParseAudio(frame);
        if (rate != VoiceWire.Rate || channels != 1 || format != AudioFormat.Pcm16)
        {
            if (_voiceFramesIn++ == 0) SetVoiceLine($"their voice arrives as {rate} Hz {channels}ch format {format}, which this cannot play", bad: true);
            return;
        }
        _voiceFramesIn++;
        player.Feed(pcm.Span);
    }

    /// <summary>After a reconnect the host may have ended the call (every technician was gone for a
    /// moment), so Talk is said again — a call the technician thinks is on must be on.</summary>
    private void ResumeVoice()
    {
        if (_talking) _link.Send(new VoiceTalkMsg { On = true });
    }

    private void SetVoiceLine(string text, bool bad = false)
    {
        _voiceLabel.Visible = true;
        _voiceLabel.ForeColor = bad ? _t.BadText : _t.Muted;
        _voiceLabel.Text = text;
        // As wide as the words, measured (§30), up to a limit that leaves the rest of the bar its
        // room; past that it ends in "…" and the whole sentence is in the tooltip.
        _voiceLabel.Width = Math.Min(TextRenderer.MeasureText(text, _voiceLabel.Font).Width + 8, 460);
        _toolTip.SetToolTip(_voiceLabel, text);
        Trace.Write("voice: " + text);
    }
}
