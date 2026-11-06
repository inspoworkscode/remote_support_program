using System.Windows.Forms;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// Tools → Get outside help… (2026-10-22, HANDOVER §155): the technician brings somebody from outside into THIS
/// session with a code. Everything goes through the dashboard's signed-in socket (<see cref="SendToServer"/>), never
/// the session socket — that one is sealed end to end and the hub could not read the ask. The dashboard hands the
/// server's answers back here by session id.
/// </summary>
public sealed partial class SessionForm
{
    /// <summary>The session this window is showing now — it changes when a restart brings a new one.</summary>
    public string SessionId => _session.SessionId;

    /// <summary>Set by the dashboard: sends on its signed-in socket. Null (the guest console) means no outside help.</summary>
    public Action<Msg>? SendToServer { get; set; }
    /// <summary>The server's web address, for the links in the message. Set by the dashboard.</summary>
    public string ServerBase { get; set; } = "";
    /// <summary>The company's name, for the message. Set by the dashboard.</summary>
    public string CompanyName { get; set; } = "";

    private ToolStripMenuItem _outsideItem = null!;
    private OutsideHelpForm? _outsideForm;

    private void BuildOutsideHelpItem()
    {
        var line = new ToolStripSeparator();
        _toolsMenu.Items.Add(line);
        _outsideItem = new ToolStripMenuItem("Get outside help…") { Font = _t.Body };
        _outsideItem.ToolTipText = "A code for somebody outside your company to join this session — you let them in";
        _outsideItem.Click += (_, _) => OpenOutsideHelp();
        _toolsMenu.Items.Add(_outsideItem);
        // Not for a visitor's window, a browser session, the guest console or the free version (no SendToServer).
        _toolsMenu.Opening += (_, _) => line.Visible = _outsideItem.Visible = SendToServer != null && !_session.Visiting && !_session.ViewOnly;
    }

    private void OpenOutsideHelp()
    {
        if (SendToServer == null) return;
        if (_outsideForm is { IsDisposed: false }) { _outsideForm.Activate(); return; }
        _outsideForm = new OutsideHelpForm(_t, _session.MachineName, ServerBase, Technician, CompanyName);
        _outsideForm.MakeCode += who => SendToServer?.Invoke(new OutsiderInviteRequestMsg { SessionId = _session.SessionId, For = who });
        _outsideForm.StopCode += () => SendToServer?.Invoke(new OutsiderInviteCancelMsg { SessionId = _session.SessionId });
        _outsideForm.RemoveThem += () => SendToServer?.Invoke(new OutsiderRemoveMsg { SessionId = _session.SessionId });
        _outsideForm.FormClosed += (_, _) => _outsideForm = null;
        _outsideForm.Show(this);
    }

    /// <summary>The server's word on this session's code. With no window open — closed after sending the code —
    /// only what matters is said: somebody connected, or left.</summary>
    public void ShowOutsideHelp(OutsiderInviteMsg m)
    {
        if (_outsideForm is { IsDisposed: false }) { _outsideForm.Apply(m); return; }
        if (m.Error.Length > 0 || m.Code.Length > 0) return;
        if (m.Connected.Length > 0 || m.State.Contains("has left")) ShowBanner(m.State, reconnect: false, close: true);
    }

    /// <summary>Somebody typed this technician's code: let them in? Answered on the dashboard's socket.</summary>
    public void AskToLetIn(OutsiderJoinAskMsg ask)
    {
        using var dlg = new OutsiderAllowDialog(_t, ask);
        var answer = dlg.ShowDialog(this);
        if (dlg.TimedOut) return;
        SendToServer?.Invoke(new OutsiderJoinAnswerMsg { SessionId = ask.SessionId, Allow = answer == DialogResult.OK, Files = answer == DialogResult.OK && dlg.Files });
    }
}
