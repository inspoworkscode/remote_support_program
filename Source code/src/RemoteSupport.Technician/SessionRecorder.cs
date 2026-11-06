using System.Diagnostics;
using System.Net.Http.Headers;
using RemoteSupport.Media;

namespace RemoteSupport.Technician;

/// <summary>
/// Records a session as the technician sees it (roadmap 1.6) — their screen and their pointer, off
/// the picture already on this machine, so it costs the customer's machine nothing. Clint,
/// 2026-09-29: a Record button, kept on the server with the session's ticket, and the customer told.
///
/// <para>Eight frames a second into an .mp4 through Windows' own encoder (<see cref="ScreenRecorder"/>),
/// at the size of their screen up to 1920 wide: tools/recordcheck measured 10 ms a frame and 7.5 MB
/// a minute for full HD. A still screen repeats its frame, which H.264 all but gives away.</para>
///
/// <para>The file is made in %LOCALAPPDATA%\RemoteSupport\recordings and uploaded when recording
/// stops; once the server has it, the local copy is deleted — a recording of a customer's screen
/// has no business staying on a technician's laptop. If the upload fails it stays, and the
/// technician is told where.</para>
/// </summary>
public sealed class SessionRecorder
{
    public const int Fps = 8;
    public const int MaxWidth = 1920;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "recordings");

    public string LocalPath { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public TimeSpan Elapsed => _clock.Elapsed;
    public int Seconds => (int)_clock.Elapsed.TotalSeconds;
    public long Frames => _rec.Frames;

    private readonly ScreenRecorder _rec;
    private readonly Func<Bitmap, bool> _render;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private volatile bool _stop;
    private string _error = "";

    private SessionRecorder(string path, ScreenRecorder rec, Func<Bitmap, bool> render)
    {
        LocalPath = path; _rec = rec; _render = render;
        _thread = new Thread(Loop) { IsBackground = true, Name = "rs-recording" };
        _thread.Start();
    }

    /// <summary>
    /// Starts recording what <paramref name="render"/> draws, at the shape of a screen of
    /// <paramref name="remote"/>. Null, with the reason, when it cannot.
    /// </summary>
    public static SessionRecorder? Start(Size remote, Func<Bitmap, bool> render, string machineName, out string why)
    {
        why = "";
        if (remote.Width <= 0 || remote.Height <= 0) { why = "there is no picture to record yet"; return null; }
        double scale = Math.Min(1.0, (double)MaxWidth / remote.Width);
        int w = (int)(remote.Width * scale) & ~1, h = (int)(remote.Height * scale) & ~1;
        try
        {
            Directory.CreateDirectory(Folder);
            var safe = string.Concat(machineName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(Folder, $"{safe} {DateTime.Now:yyyy-MM-dd HHmmss}.mp4");
            var rec = ScreenRecorder.Create(path, w, h, Fps);
            return new SessionRecorder(path, rec, render);
        }
        catch (Exception ex) { why = ex.Message; return null; }
    }

    private void Loop()
    {
        var frame = TimeSpan.FromSeconds(1.0 / Fps);
        using var bmp = new Bitmap(_rec.Width, _rec.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        long n = 0;
        while (!_stop)
        {
            var due = frame * n;
            var wait = due - _clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            try
            {
                // No picture for a moment (a reconnect, a monitor switch): the last frame is simply
                // held, which is what the technician was looking at too.
                _render(bmp);
                _rec.AddFrame(bmp, _clock.Elapsed, frame);
            }
            catch (Exception ex) { _error = ex.Message; Trace.Write("recording: a frame failed — " + ex.Message); break; }
            n++;
            // Behind by more than a second (the laptop was busy): skip ahead rather than race.
            if (_clock.Elapsed - frame * n > TimeSpan.FromSeconds(1)) n = (long)(_clock.Elapsed / frame);
        }
    }

    /// <summary>Stops and finishes the file. Returns "" or what went wrong.</summary>
    public string Stop()
    {
        _stop = true;
        _thread.Join(3000);
        _clock.Stop();
        try { _rec.Finish(); }
        catch (Exception ex) { return ex.Message; }
        return _error;
    }

    /// <summary>
    /// Uploads a finished recording to the server for <paramref name="sessionId"/>'s ticket and
    /// deletes the local copy once the server has it. Returns "" or why not — and when not, the
    /// file is still at <paramref name="path"/>.
    /// </summary>
    public static async Task<string> UploadAsync(string wsBase, string token, string sessionId, string path, int seconds)
    {
        var http = wsBase.TrimEnd('/');
        if (http.StartsWith("wss://")) http = "https://" + http["wss://".Length..];
        else if (http.StartsWith("ws://")) http = "http://" + http["ws://".Length..];
        try
        {
            // Long enough for an hour's recording over an ordinary office upload.
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await using var file = File.OpenRead(path);
            using var body = new StreamContent(file, 1 << 16);
            body.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            var url = $"{http}/api/recordings?session={Uri.EscapeDataString(sessionId)}&seconds={seconds}";
            using var resp = await client.PostAsync(url, body);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync();
                return $"the server refused it ({(int)resp.StatusCode}{(text.Length > 0 ? ": " + text : "")})";
            }
        }
        catch (Exception ex) { return ex.Message; }
        try { File.Delete(path); } catch { }
        return "";
    }
}
