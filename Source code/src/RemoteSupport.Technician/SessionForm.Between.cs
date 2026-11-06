namespace RemoteSupport.Technician;

/// <summary>
/// Copy on one customer's machine, paste on another (2026-10-15).
///
/// Clint: two sessions side by side, one on each monitor — copy on one office machine, paste on the other. Nothing
/// new carries it: a copy on machine A lands on THIS PC's clipboard (text at once, files once fetched), and the
/// clipboard follows the technician into machine B's window when they click into it (<see cref="OnActivated"/>).
/// The PC in the middle is the only link between the two, so neither machine ever learns of the other.
///
/// The one gap was timing. A's files are on this clipboard only once they have ARRIVED. Click into B before then and
/// there was nothing to send — A had emptied the clipboard so a paste could not give stale files — so B sent nothing
/// and said nothing, and Ctrl+V over there pasted whatever B's own clipboard held. Now B says the files are still on
/// their way, and sends them the moment they land if it is still the window in front. Nothing about WHEN a copy may
/// reach a customer changes: still only to the window in front.
/// </summary>
public sealed partial class SessionForm
{
    /// <summary>Every session window open in this console. UI thread only.</summary>
    private static readonly List<SessionForm> s_openWindows = new();

    /// <summary>The fetch bringing this customer's copied files onto this PC's clipboard, while it runs.</summary>
    private string? _clipFetchSet;
    /// <summary>When that fetch last showed signs of life — asked, offered, a chunk. A transfer that stalls must not
    /// leave the other window saying "still arriving" for ever and sending nothing.</summary>
    private long _clipFetchActivityMs;
    private const int ClipFetchStallMs = 30000;

    /// <summary>The window whose files this one is waiting for, so it is told when they land.</summary>
    private SessionForm? _waitingOn;

    /// <summary>This customer's copied files are on their way to this PC's clipboard.</summary>
    internal bool FetchingForClipboard =>
        _clipFetchSet != null
        && (_pullIntents.ContainsKey(_clipFetchSet) || _incomingSets.ContainsKey(_clipFetchSet))
        && Environment.TickCount64 - _clipFetchActivityMs < ClipFetchStallMs;

    /// <summary>This customer copied a set too big to fetch unasked, and its offer is on this window's status bar.</summary>
    internal bool ClipboardOfferWaiting => _pendingFetch != null && _copiedHere == null;   // held to go straight across instead (SessionForm.Bridge.cs)

    private void StartedClipboardFetch(string setId)
    {
        _clipFetchSet = setId;
        _clipFetchActivityMs = Environment.TickCount64;
    }

    private void ClipboardFetchAlive(string setId)
    {
        if (setId == _clipFetchSet) _clipFetchActivityMs = Environment.TickCount64;
    }

    /// <summary>The fetch is over, landed or not. Any window waiting for it is told.</summary>
    private void ClipboardFetchEnded(string setId, bool landed)
    {
        if (setId != _clipFetchSet) return;   // a newer copy has replaced it; that one will say when it ends
        _clipFetchSet = null;
        TellWaitingWindows(landed);
    }

    private void TellWaitingWindows(bool landed)
    {
        foreach (var w in s_openWindows.Where(w => w != this && w._waitingOn == this && !w.IsDisposed).ToList())
            w.OtherWindowsCopyEnded(this, landed);
    }

    /// <summary>
    /// Asked by <see cref="SyncMyClipboard"/> before it reads the clipboard: true, having said so on the status bar,
    /// when another session window's customer has copied files that have not reached this PC's clipboard yet.
    /// </summary>
    private bool WaitingForAnotherWindowsCopy(string why)
    {
        var other = s_openWindows.FirstOrDefault(w => w != this && !w.IsDisposed && (w.FetchingForClipboard || w.ClipboardOfferWaiting));
        if (other == null) { _waitingOn = null; return false; }
        _waitingOn = other;
        Trace.Write($"clip: files copied on {other.MachineName} have not landed yet; sending nothing to {MachineName} until they do ({why})");
        SetTransferStatus(other.FetchingForClipboard
            ? $"files copied on {other.MachineName} are still arriving — don't paste on {MachineName} yet; they come here the moment they land"
            : $"files copied on {other.MachineName} are waiting — click the offer in {other.MachineName}'s window, then paste here");
        // Said until it is answered, not for the usual twenty seconds: a large copy can take longer than that.
        _xferIdle.Stop();
        return true;
    }

    private void OtherWindowsCopyEnded(SessionForm from, bool landed)
    {
        _waitingOn = null;
        if (!landed)
        {
            SetTransferStatus($"the files copied on {from.MachineName} did not arrive, so nothing was sent to {MachineName}");
            return;
        }
        // On this PC's clipboard now. Still the window in front: this is the paste that was waiting, so send them.
        // Not in front: the next click back into it sends them, as with any copy.
        if (ActiveForm == this) SyncMyClipboard($"files from {from.MachineName} landed");
        else SetTransferStatus($"the files from {from.MachineName} have arrived — click back into this window and they go to {MachineName}");
    }

    private void JoinOpenWindows()
    {
        if (!s_openWindows.Contains(this)) s_openWindows.Add(this);
    }

    private void LeaveOpenWindows()
    {
        s_openWindows.Remove(this);
        // Closed mid-fetch: whoever was waiting is told plainly rather than left waiting.
        if (_clipFetchSet != null) { _clipFetchSet = null; TellWaitingWindows(false); }
        // Files held to go straight to THIS window's machine go to this PC instead, as with one window (SessionForm.Bridge.cs).
        _copiedHere = null;
        foreach (var w in s_openWindows.Where(w => !w.IsDisposed).ToList()) w.OtherWindowGone();
    }
}
