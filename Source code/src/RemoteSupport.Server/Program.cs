using System.Security.Cryptography.X509Certificates;
using RemoteSupport.Server;
using RemoteSupport.Shared;

// ---------------------------------------------------------------- CLI: technician management
var cfgDir = AppContext.BaseDirectory;
var bootstrap = new ConfigurationBuilder().SetBasePath(cfgDir).AddJsonFile("appsettings.json", optional: true).Build();
var dataDir = Path.IsPathRooted(bootstrap["RemoteSupport:DataDirectory"] ?? "data") ? bootstrap["RemoteSupport:DataDirectory"]! : Path.Combine(cfgDir, bootstrap["RemoteSupport:DataDirectory"] ?? "data");
var maxTechs = int.TryParse(bootstrap["RemoteSupport:MaxTechnicians"], out var mt) ? mt : 5;

if (args.Length > 0)
{
    var store = new TechnicianStore(dataDir, maxTechs);
    switch (args[0])
    {
        case "add-tech" when args.Length >= 3:
        { var (ok, msg) = store.Add(args[1], args[2], args.Length > 3 ? args[3] : null); Console.WriteLine(msg); return ok ? 0 : 1; }
        case "remove-tech" when args.Length >= 2:
            Console.WriteLine(store.Remove(args[1]) ? "Removed." : "Not found."); return 0;
        case "set-password" when args.Length >= 3:
        { var (ok, msg) = store.SetPassword(args[1], args[2]); Console.WriteLine(msg); return ok ? 0 : 1; }
        case "list-techs":
            foreach (var t in store.All) Console.WriteLine($"{t.Username}\t{t.DisplayName}{(t.Disabled ? "\t(disabled)" : "")}");
            Console.WriteLine($"{store.All.Count}/{maxTechs} technicians"); return 0;
        case "run": break;
        default:
            Console.WriteLine("Usage: RemoteSupport.Server [run | add-tech <user> <password> [display name] | remove-tech <user> | set-password <user> <password> | list-techs]");
            return 1;
    }
}

// ---------------------------------------------------------------- web host
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = cfgDir, WebRootPath = Path.Combine(cfgDir, "wwwroot") });
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

var certPath = builder.Configuration["RemoteSupport:CertificatePath"];
var httpsPort = int.TryParse(builder.Configuration["RemoteSupport:HttpsPort"], out var hp) ? hp : 0;
if (!string.IsNullOrEmpty(certPath) && httpsPort > 0)
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.ListenAnyIP(httpsPort, o => o.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(Path.IsPathRooted(certPath) ? certPath : Path.Combine(cfgDir, certPath), builder.Configuration["RemoteSupport:CertificatePassword"])));
    });
}

builder.Services.AddSingleton(new TechnicianStore(dataDir, maxTechs));
builder.Services.AddSingleton<Hub>();

var app = builder.Build();
var hub = app.Services.GetRequiredService<Hub>();
var techStore = app.Services.GetRequiredService<TechnicianStore>();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));
app.MapGet("/api/status", () => Results.Ok(hub.Status()));

app.MapPost("/api/login", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(Json.Options);
    if (body == null) return Results.BadRequest();
    await Task.Delay(Random.Shared.Next(100, 300)); // blunt brute-force damping
    var tech = techStore.Verify(body.Username, body.Password);
    if (tech == null) return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
    var token = hub.IssueTechToken(tech.DisplayName);
    return Results.Ok(new { token, name = tech.DisplayName, username = tech.Username });
});

// technician control socket
app.Map("/ws/tech", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var name = hub.TechFromToken(ctx.Request.Query["token"]);
    if (name == null) { ctx.Response.StatusCode = 401; return; }
    await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
    await hub.RunTechnicianAsync(name, ws, ctx.RequestAborted);
});

// client app / unattended agent socket
app.Map("/ws/device", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
    await hub.RunDeviceAsync(ws, ctx.RequestAborted);
});

// session socket: host (device side) or viewer (technician side)
app.Map("/ws/session", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var id = ctx.Request.Query["id"].ToString();
    var role = ctx.Request.Query["role"].ToString();
    var token = ctx.Request.Query["token"].ToString();
    if (role == "host")
    {
        await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
        await hub.RunSessionHostAsync(id, token, ws, ctx.RequestAborted);
    }
    else
    {
        var name = hub.TechFromToken(token);
        if (name == null) { ctx.Response.StatusCode = 401; return; }
        await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
        await hub.RunSessionViewerAsync(id, name, ws, ctx.RequestAborted);
    }
});

app.Logger.LogInformation("Remote Support server starting. Data dir: {Dir}. Technicians: {N}/{Max}", dataDir, techStore.All.Count, maxTechs);
if (techStore.All.Count == 0) app.Logger.LogWarning("No technicians yet. Add one with: RemoteSupport.Server add-tech <user> <password> [\"Display Name\"]");
app.Run();
return 0;

sealed record LoginRequest(string Username, string Password);
