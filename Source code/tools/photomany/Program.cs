using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// photomany - a workshop job takes as many photos as the damage needs, at both ends.
//
// Clint, 2026-09-29: "We just had a broken laptop that came in and we took 14 photos of all of the
// damage, so one must have the option at both ends to add as many photos as one likes."
//
// Three is a MINIMUM and always was - nothing counted a maximum. What stopped 14 was the size of
// the upload: every photo picked went up as ONE request, and 14 phone photos are 40-70 MB against
// the server's 30 MB default. And once a job was booked in there was nowhere to add another photo
// of it as it arrived. So this uses REAL-sized photos (about 3 MB, as a phone takes them).
//
// Its own platform on port 8095; the live server is never touched.
//
//   dotnet run -c Debug --project tools/photomany/photomany.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8095;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";
int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
Console.WriteLine("photomany - as many workshop photos as the damage needs");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-photomany-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
if (b.code != 0) { Console.WriteLine(b.output); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var env = new Dictionary<string, string> { ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http, ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development" };
HarnessDb.Use(env, dataDir);
foreach (var cmd in new[] { "create-tenant \"Photo Many\" pm 5", "approve pm", "add-tech pm sam@local.test localtest \"Sam Super\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); return 2; }
}
var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var server = Process.Start(psi)!;
server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
using var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
bool up = false;
for (int i = 0; i < 60 && !up; i++) { try { up = (await hc.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { await Task.Delay(500); } }
if (!up) { Console.WriteLine("the platform did not come up"); server.Kill(true); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex.Message); }
finally { try { server.Kill(true); } catch { } }
Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    async Task<(int before, int after)> Count(int ticket)
    {
        await using var db = new PlatformDb(options);
        var kinds = await db.TicketPhotos.Where(p => p.TicketId == ticket).Select(p => p.Kind).ToListAsync();
        return (kinds.Count(k => k == PhotoKind.Before), kinds.Count(k => k == PhotoKind.After));
    }

    // 14 photos the size a phone takes them: noise does not compress, so each is about 3 MB.
    var photos = new List<string>();
    var rng = new Random(14);
    for (int i = 0; i < 14; i++)
    {
        var p = Path.Combine(work, $"damage {i + 1:00}.jpg");
        using var bmp = new Bitmap(2600, 1800, PixelFormat.Format24bppRgb);   // a 4.7-megapixel frame of noise: about 4 MB
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[data.Stride * data.Height]; rng.NextBytes(bytes);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bmp.UnlockBits(data);
        var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var ep = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L) } };
        bmp.Save(p, enc, ep);
        photos.Add(p);
    }
    long total = photos.Sum(p => new FileInfo(p).Length);
    Console.WriteLine($"  (14 photos, {total / 1048576.0:0} MB together, {photos.Min(p => new FileInfo(p).Length) / 1048576.0:0.0}-{photos.Max(p => new FileInfo(p).Length) / 1048576.0:0.0} MB each)");

    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "sam@local.test", password = "localtest" });
    var token = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

    async Task<(HttpStatusCode st, int ok)> Post(IEnumerable<string> files, string kind, string stage, int ticketId = 0)
    {
        using var form = new MultipartFormDataContent { { new StringContent(kind), "kind" }, { new StringContent(stage), "stage" } };
        if (ticketId > 0) form.Add(new StringContent(ticketId.ToString()), "ticketId");
        foreach (var f in files) { var c = new ByteArrayContent(File.ReadAllBytes(f)); c.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"); form.Add(c, "files", Path.GetFileName(f)); }
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{http}/api/portal/photos") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var resp = await hc.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (resp.StatusCode, 0);
            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("photos");
            return (resp.StatusCode, body.EnumerateArray().Count(p => p.TryGetProperty("id", out _)));
        }
        catch (Exception ex) { Console.WriteLine("    (" + ex.GetType().Name + ")"); return (0, 0); }
    }

    // ---- booking in ----
    Console.WriteLine();
    Console.WriteLine("== booking in, as it arrived ==");
    var (st1, n1) = await Post(photos, "before", "stage-all-at-once");
    Check("14 photos in ONE upload, as the portal and older consoles send them, all arrive", n1 == 14, $"{(int)st1}, {n1} of 14");
    var (_, n2) = await Post(photos.Take(1), "before", "stage-one");
    int oneByOne = n2; foreach (var p in photos.Skip(1)) oneByOne += (await Post(new[] { p }, "before", "stage-one")).ok;
    Check("14 photos one at a time all arrive", oneByOne == 14, $"{oneByOne} of 14");

    // The console's own upload, the real code.
    var link = new TechLink(http);
    var login = await link.LoginAsync("sam@local.test", "localtest");
    // That console sign-in closed the first token (one console at a time, HANDOVER §82): use its own from here on.
    token = link.Token;
    var (consoleUp, consoleErr) = await link.UploadPhotosAsync(photos, "before", "stage-console");
    Check("the console's upload takes all 14", login.Ok && consoleUp == 14, $"{consoleUp} of 14 {consoleErr}");

    // ---- made smaller before they are sent (Clint: "so they don't use up so much space") ----
    Console.WriteLine();
    Console.WriteLine("== smaller before they are sent ==");
    await using (var db = new PlatformDb(options))
    {
        var stored = await db.TicketPhotos.Where(p => p.StageId == "stage-console").ToListAsync();
        var dims = stored.Select(p =>
        {
            var f = Path.Combine(dataDir, "ticketphotos", p.TenantId.ToString(), p.FileName);
            using var im = Image.FromFile(f);
            return (w: im.Width, h: im.Height, bytes: new FileInfo(f).Length);
        }).ToList();
        Check("what the console stored is at most 2048 pixels on its longest side", dims.Count == 14 && dims.All(d => Math.Max(d.w, d.h) <= 2048),
            dims.Count > 0 ? $"{dims[0].w}x{dims[0].h}" : "none");
        Check("...and smaller than what was picked", dims.Count == 14 && dims.All(d => d.bytes < new FileInfo(photos[0]).Length),
            dims.Count > 0 ? $"{dims.Average(d => d.bytes) / 1048576.0:0.0} MB against {new FileInfo(photos[0]).Length / 1048576.0:0.0} MB" : "");
    }
    // A camera-like photo — smooth, like a real one, not noise — taken with the phone on its side.
    var camera = Path.Combine(work, "camera.jpg");
    using (var bmp = new Bitmap(4000, 3000))
    {
        using (var g = Graphics.FromImage(bmp))
        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 4000, 3000), Color.SteelBlue, Color.Wheat, 35f))
        {
            g.FillRectangle(br, 0, 0, 4000, 3000);
            g.FillRectangle(Brushes.Red, 0, 0, 400, 400);   // top-left corner of the SENSOR
            for (int i = 0; i < 60; i++) g.DrawLine(Pens.DimGray, 200 + i * 55, 900, 500 + i * 50, 2600);   // "scratches"
        }
        var orient = (System.Drawing.Imaging.PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(System.Drawing.Imaging.PropertyItem));
        orient.Id = 0x0112; orient.Type = 3; orient.Len = 2; orient.Value = BitConverter.GetBytes((ushort)6);   // 6 = turn 90 degrees clockwise to view
        bmp.SetPropertyItem(orient);
        var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var ep = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L) } };
        bmp.Save(camera, enc, ep);
    }
    var (shrunk, shrunkName) = PhotoShrink.Prepare(camera);
    using (var ms = new MemoryStream(shrunk))
    using (var im = new Bitmap(ms))
    {
        Check("a 4000x3000 photo comes out 2048 on its longest side", Math.Max(im.Width, im.Height) == 2048, $"{im.Width}x{im.Height}");
        Check("...turned upright: a phone held sideways gives a portrait photo", im.Height > im.Width, $"{im.Width}x{im.Height}");
        var corner = im.GetPixel(im.Width - 20, 20);
        Check("...the right way round (the sensor's top-left is now top-right)", corner.R > 180 && corner.G < 80, $"#{corner.R:X2}{corner.G:X2}{corner.B:X2}");
        Check("...and much smaller", shrunk.Length < new FileInfo(camera).Length / 3, $"{new FileInfo(camera).Length / 1024} KB -> {shrunk.Length / 1024} KB, as {shrunkName}");
    }
    var tiny = Path.Combine(work, "tiny.png");
    using (var bmp = new Bitmap(640, 480)) { using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Olive); bmp.Save(tiny, ImageFormat.Png); }
    var (tinyBytes, tinyName) = PhotoShrink.Prepare(tiny);
    Check("a photo already small enough is sent exactly as it is", tinyBytes.SequenceEqual(File.ReadAllBytes(tiny)) && tinyName == "tiny.png");

    // Book it in with the 14 staged one at a time.
    var techRaw = new ClientWebSocket();
    // The console sign-in above (link) closed the first token: one console at a time, HANDOVER §82.
    await techRaw.ConnectAsync(new Uri($"{ws}/ws/tech?token={link.Token}"), default);
    var tc = new WsConnection(techRaw); var q = new ConcurrentQueue<Msg>(); tc.OnMessage += m => q.Enqueue(m); _ = tc.RunAsync();
    await Task.Delay(500);
    tc.Send(new TicketCreateMsg
    {
        Subject = "Broken laptop, dropped", Description = "Screen cracked, hinge broken, lid dented", Category = (int)TicketCategory.Workshop,
        AssignToMe = true, NewClientName = "Photo Client", PhotoStageId = "stage-one", AssetMake = "Lenovo", AssetModel = "T14", Accessories = "charger"
    });
    int ticket = 0;
    for (int i = 0; i < 60 && ticket == 0; i++) { await using var db = new PlatformDb(options); ticket = await db.Tickets.Where(t => t.Subject == "Broken laptop, dropped").Select(t => t.Id).FirstOrDefaultAsync(); if (ticket == 0) await Task.Delay(200); }
    var err = q.OfType<ErrorMsg>().FirstOrDefault()?.Message ?? "";
    Check("a workshop job books in with all 14", ticket > 0 && (await Count(ticket)).before == 14, ticket > 0 ? $"{(await Count(ticket)).before} in" : err);

    // ---- more, after booking in ----
    Console.WriteLine();
    Console.WriteLine("== more photos of it as it arrived, after booking in ==");
    var (st3, n3) = await Post(photos.Take(5), "before", "", ticket);
    Check("five more 'as it arrived' photos go onto the booked-in job", n3 == 5 && (await Count(ticket)).before == 19, $"{(int)st3}, now {(await Count(ticket)).before} in");
    var (consoleMore, e2) = await link.UploadPhotosAsync(photos.Take(3), "before", "", ticket);
    Check("...from the console too", consoleMore == 3 && (await Count(ticket)).before == 22, $"{consoleMore} {e2}");

    // ---- as it leaves ----
    Console.WriteLine();
    Console.WriteLine("== as it leaves ==");
    var (st4, n4) = await Post(photos, "after", "", ticket);
    Check("14 'as it leaves' photos in one upload all arrive", n4 == 14 && (await Count(ticket)).after == 14, $"{(int)st4}, {(await Count(ticket)).after} out");
    var (consoleOut, e3) = await link.UploadPhotosAsync(photos.Take(4), "after", "", ticket);
    Check("...and more from the console", consoleOut == 4 && (await Count(ticket)).after == 18, $"{consoleOut} {e3}");

    // ---- the pages ----
    Console.WriteLine();
    Console.WriteLine("== the portal ==");
    tc.Send(new PortalLinkRequestMsg { Path = $"/portal/tickets/{ticket}?tab=photos" });
    PortalLinkMsg? pl = null;
    for (int i = 0; i < 40 && pl == null; i++) { pl = q.OfType<PortalLinkMsg>().FirstOrDefault(); if (pl == null) await Task.Delay(200); }
    var web = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
    var page = await (await web.GetAsync($"{http}{pl!.Url}")).Content.ReadAsStringAsync();
    var photosPane = page.Contains("id=\"pane-photos\"") ? page[page.IndexOf("id=\"pane-photos\"")..] : "";
    if (photosPane.Contains("id=\"pane-quote\"")) photosPane = photosPane[..photosPane.IndexOf("id=\"pane-quote\"")];
    Check("the Photos tab offers more photos AS IT ARRIVED, not only as it leaves", photosPane.Contains("uploadMore(this, 'before'"));
    Check("...and as it leaves", photosPane.Contains("uploadMore(this, 'after'"));
    // "At least 3" shows only while a job has none yet; what must hold on every page is that both
    // ends invite as many as you like, and nothing names three as THE number.
    Check("it says as many as you like, at both ends, and never that three is the number",
        !photosPane.Contains("3 are needed") && photosPane.Split("as many as you like").Length - 1 == 2);
    var newPage = await (await web.GetAsync($"{http}/portal/tickets/new")).Content.ReadAsStringAsync();
    Check("the booking form says 'at least' too", newPage.Contains("At least 3 photos are needed") && newPage.Contains("as many as the damage needs"));
    Check("both pages make photos smaller before sending them", newPage.Contains("/photos.js") && page.Contains("/photos.js")
        && newPage.Contains("await shrinkPhoto(") && page.Contains("await shrinkPhoto("));
    var js = await web.GetAsync($"{http}/photos.js");
    Check("...with the script the portal serves", js.IsSuccessStatusCode && (await js.Content.ReadAsStringAsync()).Contains("MAX = 2048"));
}

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var p0 = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) p0.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(p0)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}
