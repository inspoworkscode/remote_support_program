using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace RemoteSupport.Shared;

/// <summary>
/// Thin wrapper over a WebSocket: serialised sends, typed receive loop.
///
/// ---- What the send queue does when the link cannot keep up (stage 2.3) --------------------
///
/// It used to be one bounded channel of 1024 with <c>DropOldest</c>, for everything. That is the
/// wrong rule twice over, and both were silent.
///
/// It evicted the OLDEST item, which on a screen stream is the worst possible choice: the frames
/// are DELTAS, and both capture paths record what the far end is believed to hold at the moment
/// of capture, not at the moment of delivery. So an evicted tile was marked as delivered, the
/// next pass compared against the new state, found no difference, and sent nothing. The stale
/// rectangle then persisted until that region of the screen changed again on its own. Rejecting
/// the NEWEST instead keeps every delta already queued, and tells the caller, which is what lets
/// the host demand a complete frame and converge.
///
/// And it applied that rule to text messages and file chunks, which must not be dropped at all —
/// a lost <c>SessionEndedMsg</c> is a customer told they are still being watched, and a lost file
/// chunk is a silently corrupt file, since nothing acknowledges or hashes them (see BUGS #8).
/// They get the headroom between <see cref="DropFramesAbove"/> and the capacity, which the screen
/// stream is not allowed to touch, so in practice they are never refused at all.
///
/// Nothing here is flow control. It is the floor under it: the queue stops destroying data
/// quietly and starts reporting, so that the thing above it can react. <see cref="PendingSends"/>
/// is that report and it is the signal stage 2.3's controller is built on.
/// </summary>
public sealed class WsConnection : IAsyncDisposable
{
    /// <summary>How many outbound items may be queued before anything at all is refused. A
    /// backstop against unbounded growth, not the working limit — see <see cref="DropFramesAboveBytes"/>.</summary>
    public const int Capacity = 4096;

    /// <summary>
    /// How many BYTES may be queued before screen and audio frames are refused.
    ///
    /// Bytes rather than items, and the harness is what settled it. Counting items, the threshold
    /// could not fire for the screen stream at all: at 12 fps a stalled socket needs a minute to
    /// queue a few hundred frames, by which time the session is over. But a screen frame is a few
    /// hundred KB and a ping is thirty bytes, so an item count says a queue holding one frame and
    /// one holding a hundred pings are the same depth, and neither the drop rule nor the
    /// controller above it can work on a number like that.
    ///
    /// 2 MB is about three quarters of a second of the 2817 KB/s a scrolling document costs today.
    /// Past that the technician is watching the past, and the newest frame is worth more than the
    /// backlog in front of it.
    /// </summary>
    public const long DropFramesAboveBytes = 2 * 1024 * 1024;

    /// <summary>The ceiling for text and file chunks, which may not be dropped. The gap above
    /// <see cref="DropFramesAboveBytes"/> is their reserve, and the screen stream may not touch
    /// it; it only has to survive the moment it takes the sender to notice.</summary>
    public const long HardLimitBytes = 8 * 1024 * 1024;

    private readonly WebSocket _ws;
    // Wait, not DropOldest: on a full channel TryWrite then REFUSES the new item rather than
    // silently destroying an older one, and returns false so the caller knows. Nothing here ever
    // blocks -- TryWrite does not wait, whatever the mode is called.
    private readonly Channel<(byte[] data, bool text)> _out = Channel.CreateBounded<(byte[], bool)>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendLoop;
    private long _queuedBytes;
    private long _peakBytes;

    public event Action<Msg>? OnMessage;
    public event Action<ReadOnlyMemory<byte>>? OnBinary;
    public event Action<Exception?>? OnClosed;
    public bool IsOpen => _ws.State == WebSocketState.Open;
    public object? Tag { get; set; }

    /// <summary>
    /// How many bytes are queued for the socket right now. **This is the backpressure signal** —
    /// a figure that climbs means the socket is draining slower than it is being filled, which is
    /// the condition stage 2.3 adapts to. Reading it costs nothing.
    /// </summary>
    public long PendingBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>How many items are queued. Kept for the trace; the byte figure is the one to
    /// reason with, for the reason <see cref="DropFramesAboveBytes"/> gives.</summary>
    public int PendingSends => _out.Reader.CanCount ? _out.Reader.Count : 0;

    /// <summary>The deepest <see cref="PendingBytes"/> has been, for the trace after the fact.</summary>
    public long PeakBytes => Interlocked.Read(ref _peakBytes);

    /// <summary>Screen and audio frames refused because the queue was too deep. Expected to be
    /// non-zero on a bad link; it is the count of frames the sender must make good.</summary>
    public long DroppedFrames;

    /// <summary>Text messages and file chunks refused. **This should always be zero.** Anything
    /// else means the reserve was exhausted, which is data loss of a kind nothing recovers from.</summary>
    public long DroppedCritical;

    public WsConnection(WebSocket ws)
    {
        _ws = ws;
        _sendLoop = Task.Run(SendLoopAsync);
    }

    // ---- a far end that has gone without saying so (2026-10-09) ----------------------------------
    //
    // Clint: "Machines still show online in the console even if they are off." A machine switched off
    // at the wall, asleep, or cut from its network never closes its socket, and nothing here noticed:
    // .NET 8's KeepAliveInterval only SENDS, it never asks for an answer, so the server waited on the
    // operating system to give up on the TCP connection — a quarter of an hour on Linux.

    private long _lastHeardMs = Environment.TickCount64;
    private volatile bool _pinged;

    /// <summary>True once <see cref="DropWhenSilent"/> has closed this connection for going quiet.</summary>
    public bool DroppedForSilence { get; private set; }

    /// <summary>
    /// Aborts the socket once the far end has sent nothing for <paramref name="limit"/> — but only a far
    /// end that has sent a <see cref="PingMsg"/>. Every console, tray app and agent pings every 15 s from
    /// the moment it connects, and has since the first commit; a connection that has never pinged is
    /// something that does not promise to (the harnesses' pretend machines, a one-off set-up), and is left
    /// to close on its own. Anything arriving counts, not only pings.
    /// </summary>
    public void DropWhenSilent(TimeSpan limit)
    {
        long limitMs = (long)limit.TotalMilliseconds;
        var check = TimeSpan.FromMilliseconds(Math.Max(250, limitMs / 5));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    await Task.Delay(check, _cts.Token);
                    if (!_pinged) continue;
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastHeardMs) <= limitMs) continue;
                    DroppedForSilence = true;
                    // Abort, not Close: a close handshake waits for an answer from a machine that is off.
                    // The pending receive fails, RunAsync returns, and OnClosed tells whoever holds it.
                    try { _ws.Abort(); } catch { }
                    return;
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// End-to-end encryption (2026-10-07, <see cref="SessionSeal"/>): set on a session socket at the host and at each
    /// console, never on the server. While it is <see cref="SessionSeal.Sealing"/>, content is sealed as it is
    /// queued; sealed frames that arrive are opened before <see cref="OnMessage"/>/<see cref="OnBinary"/> see them.
    /// </summary>
    public SessionSeal? Seal { get; set; }

    /// <summary>Held around seal-and-queue, so frames reach the queue in the order of their counters (a receiver
    /// refuses a counter lower than one it has seen).</summary>
    private readonly object _sealOrder = new();

    /// <summary>Queues a message. False means it was refused, which for text should never happen
    /// -- see <see cref="DroppedCritical"/>.</summary>
    public bool Send(Msg m)
    {
        var seal = Seal;
        if (seal is { Sealing: true, Keyed: true } && !SessionSeal.StaysClear(m))
            lock (_sealOrder) return Write(seal.SealMessage(m), false);
        return Write(Encoding.UTF8.GetBytes(Json.Serialize(m)), true);
    }

    /// <summary>
    /// Queues a binary frame. **False means it was not queued and will never be sent**, and the
    /// caller owns the consequence: a screen frame refused here has to be made good by demanding
    /// a complete frame, or the far end keeps a stale rectangle for ever.
    ///
    /// The policy comes from byte 0, the <see cref="BinKind"/>, because that is already on the
    /// wire and no call site has to remember to say which it is.
    /// </summary>
    public bool SendBinary(byte[] data)
    {
        // Voice too: a late syllable is worse than a missing one, and queuing it behind a screen
        // burst would put the whole conversation a second behind and keep it there.
        // By BinKind.Of: a sealed frame (2026-10-07) keeps its kind with the top bit set, and the server's own
        // connections — which relay sealed frames they cannot open — must drop and keep the same ones.
        byte kind = data.Length > 0 ? BinKind.Of(data[0]) : (byte)0;
        bool droppable = kind is BinKind.Tiles or BinKind.Video or BinKind.Audio or BinKind.Voice;
        if (droppable && PendingBytes >= DropFramesAboveBytes) { Interlocked.Increment(ref DroppedFrames); return false; }
        var seal = Seal;
        if (seal is { Sealing: true, Keyed: true } && data.Length > 0 && SessionSeal.IsContentBinary(data[0]))
            lock (_sealOrder) return Write(seal.SealBinary(data), false);
        return Write(data, false);
    }

    private bool Write(byte[] data, bool text)
    {
        // A sealed message (BinKind.Sealed) is text in disguise and may no more be dropped than text.
        // A bridge frame (2026-10-15) carries file chunks between two machines: no more droppable than a chunk.
        bool critical = text || (data.Length > 0 && (BinKind.Of(data[0]) is BinKind.FileChunk or BinKind.Bridge || data[0] == BinKind.Sealed));
        // Past the hard ceiling nothing is queued at all, whatever it is. For a frame that is the
        // drop above arriving a moment later; for anything else it is unrecoverable and has to be
        // loud, which is what DroppedCritical is for.
        if (PendingBytes + data.Length > HardLimitBytes || !_out.Writer.TryWrite((data, text)))
        {
            if (critical) Interlocked.Increment(ref DroppedCritical);
            else Interlocked.Increment(ref DroppedFrames);
            return false;
        }
        long now = Interlocked.Add(ref _queuedBytes, data.Length);
        // Not a compare-exchange loop: this is a high-water mark for a trace line, and a race
        // costs it one sample. Paying for exactness here would be paying on every frame.
        if (now > Interlocked.Read(ref _peakBytes)) Interlocked.Exchange(ref _peakBytes, now);
        return true;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var (data, text) in _out.Reader.ReadAllAsync(_cts.Token))
            {
                // Taken off the queue before the await, not after: the byte count has to mean
                // "waiting for the socket", and an item being written to the socket right now is
                // no longer waiting. Counting it until SendAsync returns would hold the figure one
                // frame high for exactly as long as the socket is slow, which is when it is read.
                Interlocked.Add(ref _queuedBytes, -data.Length);
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(data, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, _cts.Token);
            }
        }
        catch { }
    }

    /// <summary>Runs until the socket closes. Dispatches messages to OnMessage / OnBinary.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        Exception? err = null;
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                bool closed = false;
                do
                {
                    r = await _ws.ReceiveAsync(buf, ct);
                    Interlocked.Exchange(ref _lastHeardMs, Environment.TickCount64);
                    if (r.MessageType == WebSocketMessageType.Close) { closed = true; break; }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (closed) break;

                var seal = Seal;
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    Msg? m = null;
                    try { m = Json.Deserialize<Msg>(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); } catch { }
                    if (m == null) continue;
                    if (m is PingMsg) _pinged = true;
                    // Content that came clear on a sealed end: counted for the badge — and refused by a host that is
                    // sealing, since only the server could have sent it.
                    if (seal != null && !SessionSeal.StaysClear(m))
                    {
                        if (seal.RefuseClearContent) continue;
                        seal.NoteClearContent();
                    }
                    OnMessage?.Invoke(m);
                }
                else
                {
                    var frame = ms.ToArray();
                    // No seal: this is the SERVER, relaying what it cannot open (or an old end, which ignores a kind it
                    // does not know). Passed on untouched — the server must never drop what it merely cannot read.
                    if (frame.Length > 0 && seal != null && SessionSeal.IsSealedFrame(frame))
                    {
                        if (frame[0] == BinKind.Sealed) { var opened = seal.OpenMessage(frame); if (opened != null) OnMessage?.Invoke(opened); }
                        else { var opened = seal.OpenBinary(frame); if (opened != null) OnBinary?.Invoke(opened); }
                        continue;
                    }
                    if (seal != null && frame.Length > 0 && SessionSeal.IsContentBinary(frame[0]))
                    {
                        if (seal.RefuseClearContent) continue;
                        seal.NoteClearContent();
                    }
                    OnBinary?.Invoke(frame);
                }
            }
        }
        catch (Exception ex) { err = ex; }
        _cts.Cancel();
        OnClosed?.Invoke(err);
    }

    public async Task CloseAsync(string reason = "bye")
    {
        // Stop accepting new frames, let the send loop flush what is already queued
        // (e.g. a final "session ended" message), then close the socket cleanly.
        try { _out.Writer.TryComplete(); } catch { }
        try { await _sendLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _sendLoop; } catch { }
        _ws.Dispose();
    }
}
