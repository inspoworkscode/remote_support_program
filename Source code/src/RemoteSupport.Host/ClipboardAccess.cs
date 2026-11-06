using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// Reads and writes the interactive user's clipboard from the host, which runs as SYSTEM.
///
/// Three things about the clipboard cost more than they look like they should, and all three
/// fail silently, which is why this is a class of its own rather than two P/Invokes inline.
///
/// 1. The clipboard belongs to a WINDOW STATION, not to a machine, a user or a desktop. A
///    process on the wrong one reads and writes a different, empty clipboard and is told
///    nothing. Measured on 2026-09-13 with tools/hostinfo/clipboardprobe.ps1: the host is on
///    WinSta0, the interactive window station, so this is genuinely the user's clipboard.
///    Unlike capture and injection it is NOT per-desktop, so DesktopFollower is irrelevant here
///    and a UAC prompt does not move it out from under us.
///
/// 2. Writing needs a real window handle. OpenClipboard(NULL) works for reading, but then
///    EmptyClipboard sets the clipboard owner to NULL and SetClipboardData FAILS — documented
///    behaviour, no error worth reading, and it looks exactly like "the paste did not arrive".
///    So this keeps a message-only window alive for the life of the host and opens the
///    clipboard against that.
///
/// 3. A window handle belongs to the thread that created it, and the clipboard is opened per
///    thread. So everything happens on one dedicated thread — the same reason capture, cursor
///    and input each have their own (HANDOVER 4). A pool thread would not do: async
///    continuations hop threads and the window would be left behind.
/// </summary>
/// <remarks>Public so tools/clipmirror can drive it directly, the same as DragOut.</remarks>
public sealed class ClipboardAccess : IDisposable
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string cls, string? name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>
    /// Bumped by Windows on every change to this window station's clipboard, by anyone. Cheap: it
    /// reads a counter and does not open the clipboard, so polling it costs nothing measurable and
    /// cannot contend with the program that is trying to write.
    /// </summary>
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    /// <summary>How many formats are on the clipboard. Like the sequence number, it needs no open.</summary>
    [DllImport("user32.dll")] private static extern int CountClipboardFormats();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    /// <summary>DROPEFFECT_COPY. What a pasted file list should do: leave the original alone.</summary>
    private const uint DropEffectCopy = 1;

    /// <summary>Above this, the clipboard is almost certainly not something a technician meant to
    /// move by hand, and shipping it would stall the session for no good reason.</summary>
    public const int MaxChars = 1024 * 1024;

    /// <summary>
    /// How often the sequence number is looked at. A human presses Ctrl+C and then reaches for
    /// the other machine, so a quarter of a second is invisible to them and is four calls a second
    /// to a counter.
    /// </summary>
    private const int PollMs = 250;

    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>(), 64);
    private readonly Thread _thread;
    private readonly Thread _watch;
    private IntPtr _owner = IntPtr.Zero;
    private volatile bool _stop;

    // ---- the watcher ------------------------------------------------------------------------
    // Sequence-number polling rather than AddClipboardFormatListener, and the reason is worth
    // keeping: WM_CLIPBOARDUPDATE is POSTED to a window, so it needs a message loop on the thread
    // that owns that window. This class's thread owns the message-only window and spends its life
    // blocked in a work queue, not in GetMessage — and giving it a message loop would mean the one
    // thread that must be free to open the clipboard could instead be stuck dispatching. A counter
    // that any thread may read has none of that, and the cost is a quarter-second of latency.

    private volatile uint _lastSeq;

    /// <summary>
    /// How long the counter must have read 0, with nothing on the clipboard, before the zero is
    /// believed (BUGS #10). The start-up zero clipmirror found is one read at process start; a
    /// clipboard nobody has used since logon reads 0 for as long as nobody uses it.
    /// </summary>
    private const int RealZeroMs = 2000;

    // Where the counter and the format count are read from. Windows' own, except in tools/clipfirst,
    // which hands in a counter it drives: an unused clipboard is a fresh logon, and making one for
    // real needs a new window station, which needs elevation.
    private readonly Func<uint> _sequence;
    private readonly Func<int> _formats;

    /// <summary>
    /// Set while THIS host is writing the clipboard, so the write does not come back as a change.
    ///
    /// Without it the mirror is a loop: the technician's text is put on the customer's clipboard,
    /// the watcher sees a change, sends it back as "the customer copied this", and the two ends
    /// trade the same string until one of them gives up. The flag plus re-reading the sequence
    /// number after the write closes the window completely — the watcher never fires for anything
    /// between the two.
    /// </summary>
    private volatile bool _writing;

    /// <summary>
    /// Raised on the watcher thread when someone ELSE changed the clipboard. Never raised for a
    /// change this class made itself.
    /// </summary>
    public event Action? Changed;

    /// <param name="sequence">For harnesses only: the clipboard sequence number to watch instead of Windows'.</param>
    /// <param name="formats">For harnesses only: how many formats are on the clipboard.</param>
    public ClipboardAccess(Func<uint>? sequence = null, Func<int>? formats = null)
    {
        _sequence = sequence ?? GetClipboardSequenceNumber;
        _formats = formats ?? CountClipboardFormats;
        _thread = new Thread(Pump) { IsBackground = true, Name = "rs-clipboard" };
        _thread.Start();
        _watch = new Thread(WatchPump) { IsBackground = true, Name = "rs-clipwatch" };
        _watch.Start();
    }

    private void WatchPump()
    {
        // Zero means the counter cannot be read YET, and the first read at process start is very
        // often zero: measured on 2026-09-18 by tools/clipmirror, which found the watcher dead on
        // arrival because the first call came back zero and the loop gave up on it.
        //
        // Treating that as permanent was the bug, and it would have been far worse in the field
        // than it was in the harness. The host frequently starts as SYSTEM **before anyone has
        // logged in**, and at that moment there is genuinely no interactive clipboard to watch.
        // Giving up on the first read would mean copy and paste never worked at all on any machine
        // where the service started at boot — and it would have failed silently, on the customer's
        // machine only, in the way this whole class exists to avoid.
        //
        // So it waits instead. Said once when it starts waiting and once when it succeeds, never
        // per tick.
        //
        // BUT there are two zeros (BUGS #10, 2026-10-06). The start-up one clears in a moment and
        // the clipboard already holds something — what is there then is NOT a copy, and sending it
        // would hand the technician the password copied at nine this morning (§26). The other is a
        // clipboard nobody has used since logon: it reads 0 until the person's FIRST copy, and the
        // old loop took that copy as its starting point, so the copy a technician asks for first
        // never arrived. A zero that lasted RealZeroMs with nothing on the clipboard is the second
        // kind, and the change that ends it is a copy.
        bool complained = false;
        long zeroSince = -1;
        bool zeroWasEmpty = true;
        bool firstCopy = false;
        while (!_stop)
        {
            // The counter is read BEFORE the flags: a write of our own that lands in between is
            // then either still flagged or has already re-based _lastSeq, never mistaken for theirs.
            uint seq;
            try { seq = _sequence(); } catch { seq = 0; }
            if (_lastSeq != 0) break;                    // Writing() re-based it: our own write ended the zero
            if (_writing) { Thread.Sleep(PollMs); continue; }
            if (seq != 0)
            {
                firstCopy = zeroSince >= 0 && zeroWasEmpty && Environment.TickCount64 - zeroSince >= RealZeroMs;
                _lastSeq = seq;
                break;
            }
            if (zeroSince < 0) zeroSince = Environment.TickCount64;
            try { if (_formats() > 0) zeroWasEmpty = false; } catch { }
            if (!complained)
            {
                HostTrace.Write("clipboard: no sequence number yet — waiting for a window station with a clipboard on it");
                complained = true;
            }
            Thread.Sleep(PollMs);
        }
        if (_stop) return;
        HostTrace.Write($"clipboard: watching for changes (sequence {_lastSeq})");
        if (firstCopy)
        {
            // Nothing had been copied since logon, so this IS the person's first copy.
            HostTrace.Write("clipboard: the first copy since logon — reported");
            try { Changed?.Invoke(); }
            catch (Exception ex) { HostTrace.Write($"clipboard: watcher handler failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        while (!_stop)
        {
            Thread.Sleep(PollMs);
            if (_stop) break;
            if (_writing) continue;

            uint seq;
            try { seq = _sequence(); } catch { continue; }
            // Zero again means the window station went away under us — a logoff, or a session
            // change. Hold rather than treating it as a change, and pick up when it comes back.
            if (seq == 0 || seq == _lastSeq) continue;
            _lastSeq = seq;
            // The handler reads the clipboard through this same class, which queues onto the other
            // thread, so nothing here holds the clipboard while it works.
            try { Changed?.Invoke(); }
            catch (Exception ex) { HostTrace.Write($"clipboard: watcher handler failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Runs a clipboard write with the watcher held off, and re-bases the watcher afterwards so
    /// the write itself is never reported as a change.
    /// </summary>
    private bool Writing(Func<bool> write)
    {
        _writing = true;
        try { return write(); }
        finally
        {
            // Read the counter AFTER the write and before the flag drops: anything the watcher
            // would have seen in between is exactly what we just did.
            try { _lastSeq = _sequence(); } catch { }
            _writing = false;
        }
    }

    private void Pump()
    {
        // "STATIC" is a predefined class, so this needs no window class registration. A
        // message-only window is never shown and never appears in the taskbar; it exists purely
        // to be a legal clipboard owner.
        _owner = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_owner == IntPtr.Zero)
            HostTrace.Write($"clipboard: could not create the owner window, win32 {Marshal.GetLastWin32Error()} — writes will fail");
        else
            HostTrace.Write("clipboard: owner window created; ready");

        while (!_stop)
        {
            Action? job = null;
            try { if (!_work.TryTake(out job, 250)) continue; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
            try { job?.Invoke(); }
            catch (Exception ex) { HostTrace.Write($"clipboard: job failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        if (_owner != IntPtr.Zero) { DestroyWindow(_owner); _owner = IntPtr.Zero; }
    }

    private T Run<T>(Func<T> f, T fallback, int timeoutMs = 5000)
    {
        if (_stop) return fallback;
        var done = new ManualResetEventSlim(false);
        T result = fallback;
        try { _work.Add(() => { try { result = f(); } finally { done.Set(); } }); }
        catch { return fallback; }
        // Never block the socket thread indefinitely on a clipboard another process is holding.
        if (!done.Wait(timeoutMs)) { HostTrace.Write("clipboard: timed out waiting for the clipboard thread"); return fallback; }
        return result;
    }

    /// <summary>
    /// The clipboard is a single shared resource and any process may hold it open for a moment.
    /// Failing on the first attempt would make this feel unreliable for no reason, so retry
    /// briefly before giving up.
    /// </summary>
    private static bool OpenWithRetry(IntPtr hwnd)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(hwnd)) return true;
            Thread.Sleep(20);
        }
        HostTrace.Write($"clipboard: OpenClipboard failed after retries, win32 {Marshal.GetLastWin32Error()}");
        return false;
    }

    /// <summary>Reads the clipboard as text. Returns null when there is no text on it.</summary>
    public string? GetText() => Run<string?>(() =>
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!OpenWithRetry(_owner)) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }, null);

    /// <summary>Replaces the clipboard with the given text. Returns false with a reason on failure.</summary>
    public bool SetText(string text, out string error)
    {
        string err = "";
        bool ok = Writing(() => Run(() =>
        {
            // Without a real owner window EmptyClipboard nulls the owner and SetClipboardData
            // then fails. Say so plainly rather than reporting a mysterious false.
            if (_owner == IntPtr.Zero) { err = "the host has no clipboard owner window"; return false; }
            if (!OpenWithRetry(_owner)) { err = "another program is holding the clipboard open"; return false; }
            try
            {
                if (!EmptyClipboard()) { err = $"EmptyClipboard failed (win32 {Marshal.GetLastWin32Error()})"; return false; }

                var bytes = (UIntPtr)((text.Length + 1) * 2);
                var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
                if (hMem == IntPtr.Zero) { err = "out of memory"; return false; }
                var p = GlobalLock(hMem);
                if (p == IntPtr.Zero) { GlobalFree(hMem); err = "GlobalLock failed"; return false; }
                try { Marshal.Copy((text + "\0").ToCharArray(), 0, p, text.Length + 1); }
                finally { GlobalUnlock(hMem); }

                // On success the SYSTEM owns the block: freeing it here would corrupt the
                // clipboard. Only free it if the hand-over failed.
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                {
                    err = $"SetClipboardData failed (win32 {Marshal.GetLastWin32Error()})";
                    GlobalFree(hMem);
                    return false;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }, false));
        error = err;
        return ok;
    }

    // ---- file lists (CF_HDROP) ---------------------------------------------------------------
    // What Explorer puts on the clipboard when you copy files or folders, and what a drag carries.
    // It is not text: it is a DROPFILES header followed by a run of null-separated paths ending in
    // a second null. Reading and writing it is the difference between "copy and paste works" and
    // "copy and paste works for text only".

    private const uint CF_HDROP = 15;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint index, System.Text.StringBuilder? file, uint cch);

    /// <summary>The DROPFILES header that precedes the path list. 20 bytes on x86 and x64 alike.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DropFiles
    {
        public uint pFiles;              // byte offset from the start of this struct to the paths
        public int ptX, ptY;
        public int fNC;
        public int fWide;                // non-zero: the paths are UTF-16
    }

    /// <summary>
    /// Reads a file list off the clipboard. Null when there is not one.
    ///
    /// The raw route reads our OWN writes and anything another SYSTEM process copied, and for
    /// those it is right and cheap. It cannot read what the PERSON at the machine copied, and
    /// that is the case this whole feature is about — see <see cref="GetFilesAsUser"/>.
    /// </summary>
    public List<string>? GetFiles() => GetFilesRaw();

    /// <summary>
    /// What is on the raw clipboard, cheaply, for deciding whether it is worth asking the user's
    /// own session about it. Nothing here opens a door to another process.
    /// </summary>
    private (bool HasHdrop, bool HasText, bool HasAnything) Survey() => Run(() =>
    {
        if (!OpenWithRetry(_owner)) return (false, false, false);
        try
        {
            bool hdrop = IsClipboardFormatAvailable(CF_HDROP);
            bool text = IsClipboardFormatAvailable(CF_UNICODETEXT);
            bool any = EnumClipboardFormats(0) != 0;
            return (hdrop, text, any);
        }
        finally { CloseClipboard(); }
    }, (false, false, false));

    /// <summary>
    /// Reads a file list the way the PERSON at the machine sees it — which for a real Ctrl+C is
    /// the only way there is.
    ///
    /// Measured on DESKTOP-OPHQIDH, 2026-09-21, tools/hostinfo/oleprobe.ps1, while the logged-on
    /// user held a copy of two files. From the SYSTEM host, every route came back empty:
    ///
    ///   raw Win32   the only format on the clipboard is `DataObject`. No CF_HDROP at all,
    ///               GetClipboardData(CF_HDROP) returns NULL.
    ///   OLE         Clipboard.GetFileDropList on an STA thread returns 0 paths, and the data
    ///               object reports an EMPTY format list. OLE will not marshal it to us.
    ///   Get-Clipboard  reports "1 item" whose path is a blank string — a phantom, and the thing
    ///               that made an earlier reading of this look like it worked.
    ///
    /// Explorer does not put bytes on the clipboard when you copy files. It puts a data object
    /// that renders the list on demand, in the copying process, and that rendering does not
    /// cross to another account. So the host asks a copy of itself running AS that account,
    /// exactly as the paste does in the other direction (§39). Same mechanism, mirrored.
    ///
    /// The helper is only started when it can possibly help: the raw clipboard has no file list
    /// and no text of its own, and yet something IS on it. A plain text copy never pays for a
    /// process, and neither does an empty clipboard.
    /// </summary>
    public List<string>? GetFilesAsUser()
    {
        var raw = GetFilesRaw();
        if (raw is { Count: > 0 }) return raw;
        if (!UserContext.IsSystem) return null;   // already the user; raw is all there is

        var (hdrop, text, any) = Survey();
        if (hdrop || text || !any) return null;   // nothing delay-rendered to go looking for

        string listDir = Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp", "RemoteSupport", "paste");
        string list = Path.Combine(listDir, $"read-{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(listDir);
            var exe = Environment.ProcessPath ?? "";
            if (!UserProcess.RunAsInteractiveUser(exe, $"--get-files \"{list}\"", 15000, out int code, out var why))
            {
                HostTrace.Write($"clipboard: could not ask the user's session what was copied ({why})");
                return null;
            }
            if (code != HelperOk || !File.Exists(list)) return null;
            var paths = File.ReadAllLines(list).Where(l => l.Trim().Length > 0).ToList();
            if (paths.Count == 0) return null;
            HostTrace.Write($"clipboard: the customer's copy was delay-rendered; {paths.Count} path(s) read by a process running as {UserContext.InteractiveUser}");
            return paths;
        }
        catch (Exception ex) { HostTrace.Write($"clipboard: reading the customer's copy failed: {ex.GetType().Name}: {ex.Message}"); return null; }
        finally { try { File.Delete(list); } catch { } }
    }

    /// <summary>
    /// The <c>--get-files</c> helper. Runs as the logged-on user, where the copied list can
    /// actually be rendered, and writes the paths one per line. OLE needs an STA thread with an
    /// apartment of its own, which is why this does not just call the raw reader.
    /// </summary>
    public static int RunGetFilesHelper(string outPath)
    {
        List<string>? found = null;
        var t = new Thread(() =>
        {
            try
            {
                var col = System.Windows.Forms.Clipboard.GetFileDropList();
                found = col.Cast<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch { }
        }) { IsBackground = true, Name = "rs-clipread" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(8000)) return HelperCrashed;
        if (found is not { Count: > 0 }) { try { File.WriteAllText(outPath, ""); } catch { } return HelperOk; }
        try { File.WriteAllLines(outPath, found); } catch { return HelperCrashed; }
        return HelperOk;
    }

    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);

    private List<string>? GetFilesRaw() => Run<List<string>?>(() =>
    {
        if (!IsClipboardFormatAvailable(CF_HDROP)) return null;
        if (!OpenWithRetry(_owner)) return null;
        try
        {
            var h = GetClipboardData(CF_HDROP);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                uint count = DragQueryFileW(p, 0xFFFFFFFF, null, 0);
                var list = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    uint len = DragQueryFileW(p, i, null, 0);
                    if (len == 0) continue;
                    var sb = new System.Text.StringBuilder((int)len + 1);
                    if (DragQueryFileW(p, i, sb, (uint)sb.Capacity) > 0) list.Add(sb.ToString());
                }
                return list.Count > 0 ? list : null;
            }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }, null);

    /// <summary>
    /// Puts a file list on the clipboard, so the user can paste the files straight into Explorer.
    /// The paths must already exist on THIS machine — a clipboard file list is a set of pointers,
    /// so the bytes have to have been transferred first.
    /// </summary>
    public bool SetFiles(IReadOnlyList<string> paths, out string error)
    {
        string err = "";
        bool ok = Writing(() => Run(() =>
        {
            if (_owner == IntPtr.Zero) { err = "the host has no clipboard owner window"; return false; }
            if (paths.Count == 0) { err = "there were no files to put on the clipboard"; return false; }
            if (!OpenWithRetry(_owner)) { err = "another program is holding the clipboard open"; return false; }
            try
            {
                if (!EmptyClipboard()) { err = $"EmptyClipboard failed (win32 {Marshal.GetLastWin32Error()})"; return false; }

                // Header, then every path null-separated, then one more null to end the run.
                int header = Marshal.SizeOf<DropFiles>();
                int chars = paths.Sum(p => p.Length + 1) + 1;
                int bytes = header + chars * 2;

                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) { err = "out of memory"; return false; }
                var p = GlobalLock(hMem);
                if (p == IntPtr.Zero) { GlobalFree(hMem); err = "GlobalLock failed"; return false; }
                try
                {
                    Marshal.StructureToPtr(new DropFiles { pFiles = (uint)header, fWide = 1 }, p, false);
                    var buf = new char[chars];
                    int at = 0;
                    foreach (var path in paths) { path.CopyTo(0, buf, at, path.Length); at += path.Length; buf[at++] = '\0'; }
                    buf[at] = '\0';
                    Marshal.Copy(buf, 0, p + header, chars);
                }
                finally { GlobalUnlock(hMem); }

                if (SetClipboardData(CF_HDROP, hMem) == IntPtr.Zero)
                {
                    err = $"SetClipboardData failed (win32 {Marshal.GetLastWin32Error()})";
                    GlobalFree(hMem);
                    return false;
                }

                // "Preferred DropEffect" = Copy, alongside the file list.
                //
                // The drag path has set this since 2026-09-15 and the reason is written there:
                // without it some targets offer a MOVE. On a paste that is worse than on a drag.
                // These files live in a staging folder under the customer's temp that they will
                // never open, so a MOVE empties it — the paste works once and a second paste of
                // the same clipboard finds nothing, with no error and nothing to see.
                //
                // Failing to add it does NOT fail the paste: a file list without a drop effect is
                // still pasteable, and the list is already on the clipboard by this point. So it
                // is traced and let go rather than throwing away a transfer that worked.
                var effect = RegisterClipboardFormatW("Preferred DropEffect");
                if (effect != 0)
                {
                    var hEff = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4);
                    if (hEff != IntPtr.Zero)
                    {
                        var pe = GlobalLock(hEff);
                        if (pe != IntPtr.Zero)
                        {
                            try { Marshal.WriteInt32(pe, (int)DropEffectCopy); }
                            finally { GlobalUnlock(hEff); }
                            if (SetClipboardData(effect, hEff) == IntPtr.Zero)
                            {
                                HostTrace.Write($"clipboard: the file list is set, but Preferred DropEffect was refused (win32 {Marshal.GetLastWin32Error()}) — a paste may offer to MOVE");
                                GlobalFree(hEff);
                            }
                        }
                        else GlobalFree(hEff);
                    }
                }
                return true;
            }
            finally { CloseClipboard(); }
        }, false));
        error = err;
        return ok;
    }

    // ---- the write Explorer will actually paste from --------------------------------------------
    // SetFiles above is correct and Explorer will not paste from it when this process is SYSTEM.
    // Measured on DESKTOP-OPHQIDH, 2026-09-21, tools/userpaste: the logged-on user's own process
    // read both paths off the clipboard and opened both files, and the shell offered no Paste
    // while the clipboard's owner was the SYSTEM host. The same paths written by a process
    // running as the user were offered and pasted. Reading was never the problem; the owner was.

    /// <summary>Exit codes of the <c>--put-files</c> helper, so the host can say which it got.</summary>
    public const int HelperOk = 0, HelperSetFailed = 3, HelperCrashed = 4;

    /// <summary>
    /// Puts a file list on the clipboard so that the person at the machine can paste it. When
    /// this host is SYSTEM the write is done by a short-lived copy of the host running as that
    /// person (<see cref="UserProcess"/>); otherwise this process is already them and writes
    /// directly. Either way the watcher is held off, so the write never comes back as "the
    /// customer copied this".
    /// </summary>
    public bool SetFilesAsUser(IReadOnlyList<string> paths, out string error)
    {
        if (!UserContext.IsSystem) return SetFiles(paths, out error);
        if (paths.Count == 0) { error = "there were no files to put on the clipboard"; return false; }

        string err = "";
        bool ok = Writing(() =>
        {
            // The list goes through a file rather than the command line: paths are long, there
            // may be thousands, and the staging root under the user's temp is somewhere they can
            // read (they own it) and nobody else needs to.
            string listDir = Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp", "RemoteSupport", "paste");
            string list = Path.Combine(listDir, $"clip-{Guid.NewGuid():N}.txt");
            string result = list + ".result";
            try
            {
                Directory.CreateDirectory(listDir);
                File.WriteAllLines(list, paths);
            }
            catch (Exception ex) { err = $"could not write the list for the user helper: {ex.Message}"; return false; }

            try
            {
                var exe = Environment.ProcessPath ?? "";
                if (!UserProcess.RunAsInteractiveUser(exe, $"--put-files \"{list}\"", 15000, out int code, out var why))
                {
                    // No user to be: the lock screen, or nobody logged on. There is nobody to
                    // paste either, so the direct write is as good as it gets and better than
                    // nothing — and it says so, so the trace does not read as a success.
                    HostTrace.Write($"clipboard: could not hand the file list to the user ({why}); writing it as SYSTEM, which Explorer will not paste from");
                    return SetFiles(paths, out err);
                }
                string helperSaid = "";
                try { if (File.Exists(result)) helperSaid = File.ReadAllText(result).Trim(); } catch { }
                if (code == HelperOk)
                {
                    HostTrace.Write($"clipboard: the file list was written by a process running as {UserContext.InteractiveUser}, as Explorer requires");
                    return true;
                }
                err = code switch
                {
                    HelperSetFailed => "the user helper could not set the clipboard: " + (helperSaid.Length > 0 ? helperSaid : "no reason given"),
                    HelperCrashed => "the user helper failed: " + (helperSaid.Length > 0 ? helperSaid : "no detail"),
                    _ => $"the user helper exited with code {code}" + (helperSaid.Length > 0 ? ": " + helperSaid : ""),
                };
                return false;
            }
            finally
            {
                try { File.Delete(list); } catch { }
                try { File.Delete(result); } catch { }
            }
        });
        error = err;
        return ok;
    }

    /// <summary>
    /// The <c>--put-files</c> helper's whole job: read the list, set the clipboard, say how it
    /// went in a file next to the list (this process cannot write the host trace), exit with a
    /// code the host understands. Runs as the logged-on user.
    /// </summary>
    public static int RunPutFilesHelper(string listPath)
    {
        string result = listPath + ".result";
        try
        {
            var paths = File.ReadAllLines(listPath).Where(l => l.Length > 0).ToList();
            using var clip = new ClipboardAccess();
            bool ok = clip.SetFiles(paths, out var why);
            try { File.WriteAllText(result, ok ? $"ok: {paths.Count} path(s)" : why); } catch { }
            return ok ? HelperOk : HelperSetFailed;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(result, ex.GetType().Name + ": " + ex.Message); } catch { }
            return HelperCrashed;
        }
    }

    public void Dispose()
    {
        _stop = true;
        Changed = null;
        try { _work.CompleteAdding(); } catch { }
        try { _thread.Join(1500); } catch { }
        try { _watch.Join(PollMs * 4); } catch { }
        _work.Dispose();
    }
}
