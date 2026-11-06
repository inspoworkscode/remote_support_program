using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

// =====================================================================================
// The Remote Support Program — multi-tenant platform server + public site + portal.
// =====================================================================================

var baseDir = AppContext.BaseDirectory;

// The live server's move to PostgreSQL (2026-10-09, step 4 of HANDOVER §118). FIRST, before the platform is
// built: building it migrates the SQLite database, which is a write to the file the switch promises never to
// touch, and would hide a file that is behind on migrations.
if (args.Length > 0 && args[0] == "copy-to-postgres")
    return await PostgresCopy.RunAsync(args);

var bootstrap = new ConfigurationBuilder().SetBasePath(baseDir).AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables("RSP_").Build();
var dataDirCfg = bootstrap["Platform:DataDirectory"] ?? "data";
var dataDir = Path.IsPathRooted(dataDirCfg) ? dataDirCfg : Path.Combine(baseDir, dataDirCfg);
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir, WebRootPath = Path.Combine(baseDir, "wwwroot") });
// The live server's own settings (2026-10-01), written by go-live.sh — the public address above
// all. The service gets them from systemd's environment too, but the COMMAND LINE does not: an
// `invite` typed on the server would have mailed a link to the office LAN address that
// appsettings.json ships with. A file beside the program is read by every run of it. The
// environment still wins over it.
builder.Configuration.AddJsonFile(Path.Combine(baseDir, "appsettings.live.json"), optional: true);
builder.Configuration.AddEnvironmentVariables("RSP_");
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// The database (2026-10-08, HANDOVER §118): PostgreSQL when ConnectionStrings:Postgres is set, SQLite otherwise.
// The rest of the program asks for a PlatformDb either way; PgPlatformDb exists only to carry its own migrations.
var pgConnection = builder.Configuration.GetConnectionString("Postgres");
string dbDescription;
if (!string.IsNullOrWhiteSpace(pgConnection))
{
    pgConnection = PgPlatformDb.Prepare(pgConnection);
    builder.Services.AddDbContextFactory<PgPlatformDb>(o => o.UseNpgsql(pgConnection));
    builder.Services.AddSingleton<IDbContextFactory<PlatformDb>>(sp => new PlatformDbFactory(sp.GetRequiredService<IDbContextFactory<PgPlatformDb>>()));
    dbDescription = PgPlatformDb.Describe(pgConnection);
}
else
{
    builder.Services.AddDbContextFactory<PlatformDb>(o => o.UseSqlite($"Data Source={dbPath}"));
    dbDescription = dbPath;
}
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton(sp => new TenantService(sp.GetRequiredService<IDbContextFactory<PlatformDb>>(), sp.GetRequiredService<IConfiguration>(), dataDir, sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(sp => new PayloadService(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TenantService>(), baseDir));
builder.Services.AddSingleton(sp => new HealthCheckService(sp.GetRequiredService<IDbContextFactory<PlatformDb>>(),
    sp.GetRequiredService<TenantService>(), sp.GetRequiredService<ILogger<HealthCheckService>>(), dataDir));
builder.Services.AddSingleton<ReportPdfService>();
// The mailbox the product itself writes from, and the only thing on this server that runs on a
// timer. See TrialNoticeService: a lapsed trial refuses every one of a tenant's machines at once,
// and warning them beforehand is the only thing that prevents that rather than explaining it.
builder.Services.AddSingleton<PlatformMailService>();
builder.Services.AddSingleton<TrialNoticeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrialNoticeService>());
builder.Services.AddSingleton<HubRegistry>();
// SetApplicationName matters more than it looks. Data Protection mixes an "application
// discriminator" into the key derivation, and without this it defaults to the content root PATH.
// So the same key ring in data/keys produces different keys once the application moves - and
// every stored password stops decrypting with "the payload was invalid", while the keys look
// perfectly present. Measured: a password saved by the server running from
// bin/Release/net8.0 could not be read by the same build running from another folder.
//
// That matters because the platform is meant to move: it publishes linux-x64 and will live at
// some /opt path on a real host. Without a fixed name, that deployment silently loses every
// workshop device password and the tenant's mail password.
//
// Changing this value invalidates everything already encrypted, so it is set once and left alone.
builder.Services.AddDataProtection()
    .SetApplicationName("RemoteSupportPlatform")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddSingleton<ClientService>();
builder.Services.AddSingleton<ContactService>();
builder.Services.AddSingleton<ClientImportService>();
builder.Services.AddSingleton<PortalHandoffService>();
builder.Services.AddSingleton<RegionServers>();
builder.Services.AddSingleton(sp => new TicketPhotoService(
    sp.GetRequiredService<IDbContextFactory<PlatformDb>>(),
    sp.GetRequiredService<ILogger<TicketPhotoService>>(), dataDir));
builder.Services.AddSingleton(sp => new RecordingService(
    sp.GetRequiredService<IDbContextFactory<PlatformDb>>(),
    sp.GetRequiredService<ILogger<RecordingService>>(), dataDir));
// Files and photos on comments (2026-10-13), beside the workshop photos in the data folder.
builder.Services.AddSingleton(sp => new TicketAttachmentService(
    sp.GetRequiredService<IDbContextFactory<PlatformDb>>(),
    sp.GetRequiredService<ILogger<TicketAttachmentService>>(), dataDir));
// With the photo service, so the workshop close gate applies on every path that can move a
// status - the browser, the console, and anything added later.
builder.Services.AddSingleton(sp => new TicketService(
    sp.GetRequiredService<IDbContextFactory<PlatformDb>>(), sp.GetRequiredService<TicketPhotoService>()));
// Latest activity (2026-10-26, HANDOVER §164): every ticket's story, newest first, and what is new to each person.
builder.Services.AddSingleton<TicketActivityService>();
builder.Services.AddSingleton<QuoteService>();
builder.Services.AddSingleton<QuoteSignatureService>();
builder.Services.AddSingleton<StaffChatService>();
builder.Services.AddSingleton<StaffPresence>();
builder.Services.AddSingleton<Visitors>();
builder.Services.AddSingleton<WebChatService>();
builder.Services.AddHostedService<WebChatSweep>();
// Tickets by email and the contact form (3.8.4): the rules, and the minute that looks at the mailboxes.
builder.Services.AddSingleton<MicrosoftSignIn>();
builder.Services.AddSingleton<TicketIntake>();
builder.Services.AddHostedService<TicketIntakeSweep>();
// Updating the client from a ticket (2026-10-09): a comment emailed through the same support address.
builder.Services.AddSingleton<ClientUpdateService>();
// Recurring tasks (2026-10-07): the rules, and the minute that opens each one on its morning.
builder.Services.AddSingleton<RecurringTaskService>();
builder.Services.AddHostedService<RecurringTaskSweep>();
builder.Services.AddSingleton<DashboardService>();
// The knowledge base (3.8.5): its rules, and the folder its pictures live in beside the photos.
builder.Services.AddSingleton<OutsiderCodeService>();   // outside help's codes (3.9); one instance, so the guess limit is one
builder.Services.AddSingleton(sp => new KnowledgeBaseService(
    sp.GetRequiredService<IDbContextFactory<PlatformDb>>(),
    sp.GetRequiredService<ILogger<KnowledgeBaseService>>(), dataDir));

builder.Services.AddSingleton<OwnerTwoFactor>();
// Platform admins and what they do to companies (2026-10-13, docs/NEXT-SESSION-CONTROL.md).
builder.Services.AddSingleton<PlatformAdmins>();
builder.Services.AddSingleton<AdminCompanies>();
// A company paying by card (2026-10-13, stage B): switched on by itself when PayFast confirms the card.
builder.Services.AddSingleton<CompanyBilling>();
builder.Services.AddSingleton<SitePreview>();
builder.Services.AddSingleton<ReviewService>();
// Pilot companies (2026-10-15): one link each, five seats free while they are pilots — every rule is PilotService; the
// sweep ends a pilot on its notice day (2026-10-22).
builder.Services.AddSingleton<PilotService>();
builder.Services.AddHostedService<PilotSweep>();
// Five companies at a time (2026-10-22): past them a full signup waits its turn — every rule is OpenPlaces.
builder.Services.AddSingleton<OpenPlaces>();
// A company takes its data with it (2026-10-22, roadmap 4.9): made in the background into data/exports — every rule is
// CompanyExportService.
builder.Services.AddSingleton(sp => new CompanyExportService(sp.GetRequiredService<IDbContextFactory<PlatformDb>>(),
    sp.GetRequiredService<ILogger<CompanyExportService>>(), sp.GetRequiredService<PlatformMailService>(), sp.GetRequiredService<PlatformAdmins>(),
    sp.GetRequiredService<AdminCompanies>(), sp.GetRequiredService<TicketPhotoService>(), sp.GetRequiredService<TicketAttachmentService>(),
    sp.GetRequiredService<KnowledgeBaseService>(), sp.GetRequiredService<RecordingService>(), sp.GetRequiredService<IConfiguration>(), dataDir));
builder.Services.AddHostedService<CompanyExportSweep>();
builder.Services.AddHostedService<PayfastSweep>();
// The control dashboard's servers (stage B): this machine's readings, this server's report, the others, and the alert sweep.
builder.Services.AddSingleton(sp => new ServerReadings(sp.GetRequiredService<IConfiguration>(), dataDir));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerReadings>());
builder.Services.AddSingleton(sp => new ServerReporter(sp.GetRequiredService<IDbContextFactory<PlatformDb>>(), sp.GetRequiredService<HubRegistry>(),
    sp.GetRequiredService<ServerReadings>(), sp.GetRequiredService<PlatformAdmins>(), sp.GetRequiredService<IConfiguration>(),
    pgConnection != null ? "PostgreSQL" : "SQLite", sp.GetRequiredService<UpdateService>()));
// Auto-update (2026-10-16, docs/NEXT-SESSION-AUTOUPDATE.md): the release this server hands out, its rollout, and the sweep
// that offers it to machines a few a minute. Every rule is UpdateService.
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<UpdateSweep>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateSweep>());
builder.Services.AddSingleton<ControlServers>();
builder.Services.AddSingleton<ControlSweep>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlSweep>());
builder.Services.AddSingleton<AccountLinks>();
builder.Services.AddSingleton<FreeCompanies>();
// Taking the money (2026-10-13, roadmap 4.8): the PayFast account, checkouts, notices and the subscription API.
builder.Services.AddSingleton<PayfastService>();
// The form-protection cookie, Secure over https like the sign-in cookie. Its default is "never
// Secure" — seen on the first live server, 2026-10-01; HSTS covered it, this makes it exact.
builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);

// Behind Caddy every request arrives from 127.0.0.1. Without this the server could not see who
// was calling at all: the sign-in guard's per-address count, website chat's per-address limit and
// every refusal in the log all saw one address — Caddy's. Trusted from loopback only (the
// default), which is the only place Caddy talks to it from; anything else claiming to forward is
// ignored. X-Forwarded-Proto makes a request that came in over https KNOW it did, so the cookie is
// marked Secure and HSTS is sent. Since 2026-10-13 Caddy comes in through a Unix socket (BUGS #12),
// which has no address at all — UnixSocket.TrustCaddy says why that is still right, and unixsockcheck proves it.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(UnixSocket.TrustCaddy);

// Portal sign-in: one cookie, two roles (tenant admin / platform owner)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/login"; o.AccessDeniedPath = "/login";
    o.ExpireTimeSpan = TimeSpan.FromDays(7); o.SlidingExpiration = true;
    o.Cookie.Name = "rsp.portal"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax;
    // Secure whenever the request was https — which, behind Caddy, needs the forwarded headers above.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    // The security stamp (2026-10-01). A cookie used to be good for as long as it kept being used,
    // whatever happened to the person: removed, password changed, still in. Now every request
    // compares the stamp the cookie was issued with against the person's stamp now (cached 15 s),
    // and the owner's against the admin key and authenticator — see AuthService, OwnerTwoFactor.
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var user = ctx.Principal;
        if (user?.Identity?.IsAuthenticated != true) return;
        CookieVerdict verdict;
        if (user.IsInRole("owner") && AdminIdentity.IdOf(user) is int adminId)
            // A named admin (2026-10-13): their own stamp, which moves when anything about them changes or they are removed.
            verdict = PortalIdentity.StampOf(user) is { Length: > 0 } st && st == await ctx.HttpContext.RequestServices.GetRequiredService<PlatformAdmins>().StampAsync(adminId)
                ? CookieVerdict.Good : CookieVerdict.Changed;
        else if (user.IsInRole("owner"))
            verdict = PortalIdentity.StampOf(user) == await ctx.HttpContext.RequestServices.GetRequiredService<OwnerTwoFactor>().StampAsync()
                ? CookieVerdict.Good : CookieVerdict.Changed;
        else
            verdict = await ctx.HttpContext.RequestServices.GetRequiredService<AuthService>()
                .CookieCheckAsync(PortalIdentity.TechnicianIdOf(user), PortalIdentity.StampOf(user), PortalIdentity.SignInOf(user));
        if (verdict != CookieVerdict.Good)
        {
            // Signed in on another browser (one at a time, 2026-10-05): the sign-in page says so, once.
            if (verdict == CookieVerdict.Elsewhere)
                ctx.HttpContext.Response.Cookies.Append(RemoteSupport.Platform.Pages.LoginModel.SignedOutCookie, "elsewhere", new CookieOptions
                { HttpOnly = true, Secure = ctx.HttpContext.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true });
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
    // The owner's pages send a signed-out visitor to the OWNER's sign-in (admin key), not the
    // companies' — found by securitycheck, when a signed-out owner landed on the wrong form.
    o.Events.OnRedirectToLogin = ctx =>
    {
        var to = ctx.Request.Path.StartsWithSegments("/owner") ? "/owner/login" : ctx.RedirectUri;
        ctx.Response.Redirect(to);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Tenant", p => p.RequireRole("tenant"));
    // reception / accounts sign in for billing only
    o.AddPolicy("TenantOrBilling", p => p.RequireRole("tenant", "billing"));
    // Everyone who may reach the portal at all, technicians included since 3.6.3. It is the
    // FOLDER policy now, and the narrower ones above sit on top of it page by page — folder and
    // page policies combine with AND, so widening the folder cannot widen a page.
    o.AddPolicy("PortalUser", p => p.RequireRole("tenant", "billing", "technician"));
    o.AddPolicy("Owner", p => p.RequireRole("owner"));
});
builder.Services.AddRazorPages(o =>
{
    // Folder and page policies combine with AND, so the folder must allow both roles and each
    // admin-only page then narrows it. Billing users are left with the billing page alone.
    o.Conventions.AuthorizeFolder("/Portal", "PortalUser");
    // billing / reception may read Billing and the ticket history (they invoice from it); nothing else
    // reception/accounts may also raise jobs and manage clients — "anyone in the office can open a ticket"
    // HealthCheck carries the tenant's outgoing mail password, so it is admin-only like the rest.
    // Hiding the link from the billing nav is not protection; the page policy is.
    // Devices is NOT in this list, deliberately. It was, until 2026-09-20, and that meant only the
    // owner could say which customer a machine belongs to — while an unlinked machine is exactly
    // what stops a ticket reaching a client page. See Permissions.CanLinkDevices.
    foreach (var adminPage in new[] { "/Portal/Index", "/Portal/RemoveDevice", "/Portal/Branding", "/Portal/Technicians", "/Portal/Sessions", "/Portal/Downloads", "/Portal/HealthCheck", "/Portal/HealthChecks", "/Portal/WebSupport", "/Portal/MailIntake", "/Portal/MailIntakeMicrosoft", "/Portal/Outsiders", "/Portal/Export" })
        o.Conventions.AuthorizePage(adminPage, "Tenant");
    // What the money costs is not a technician's business. Billing stays where it was, which now
    // means narrowing it back down from the wider folder policy — the ONLY page that needs this,
    // because every other page a technician may not see was already admin-only above.
    o.Conventions.AuthorizePage("/Portal/Billing", "TenantOrBilling");
    // Who is carrying what: the owner supervising, and the office administrator deciding who gets
    // the next job. Not a technician — that board is everybody else's work.
    o.Conventions.AuthorizePage("/Portal/Workload", "TenantOrBilling");
    o.Conventions.AuthorizeFolder("/Owner", "Owner");
    o.Conventions.AllowAnonymousToPage("/Owner/Login");
    o.Conventions.AllowAnonymousToPage("/Owner/Setup");

    // Addresses by number (2026-10-23, HANDOVER §159). Each page's own route is the new shape (/portal/t/{number} …);
    // these are the OLD ones, which a bookmark, a page left open or a not-yet-updated console still asks for. They are
    // routed to the same page, which sends a company's own id on to its number (PortalPage.AddressedIdAsync) and
    // answers anybody else's as "not found" — and they never make a link, so no page can draw an id by accident.
    // (AddPageRoute would do the opposite: it makes the added route the one every link uses.)
    void OldAddress(string page, string template) => o.Conventions.AddPageRouteModelConvention(page, m =>
        m.Selectors.Add(new Microsoft.AspNetCore.Mvc.ApplicationModels.SelectorModel
        {
            AttributeRouteModel = new Microsoft.AspNetCore.Mvc.ApplicationModels.AttributeRouteModel { Template = template, SuppressLinkGeneration = true }
        }));
    OldAddress("/Portal/Ticket", "portal/tickets/{id:int}");
    OldAddress("/Portal/Client", "portal/clients/{id:int}");
    OldAddress("/Portal/KnowledgeArticle", "portal/kb/{id:int}");
    OldAddress("/Portal/KnowledgeEdit", "portal/kb/edit/{id:int}");
    OldAddress("/Portal/RecurringEdit", "portal/recurring/{which}");
    // A new article has no number yet: /portal/kb/edit, as always (its form writes its address out).
    o.Conventions.AddPageRouteModelConvention("/Portal/KnowledgeEdit", m =>
        m.Selectors.Add(new Microsoft.AspNetCore.Mvc.ApplicationModels.SelectorModel
        {
            AttributeRouteModel = new Microsoft.AspNetCore.Mvc.ApplicationModels.AttributeRouteModel { Template = "portal/kb/edit", SuppressLinkGeneration = true }
        }));
});

var certPath = builder.Configuration["Platform:CertificatePath"];
var httpsPort = int.TryParse(builder.Configuration["Platform:HttpsPort"], out var hp) ? hp : 0;
if (!string.IsNullOrEmpty(certPath) && httpsPort > 0)
    builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(httpsPort, o => o.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(Path.IsPathRooted(certPath) ? certPath : Path.Combine(baseDir, certPath), builder.Configuration["Platform:CertificatePassword"]))));

var app = builder.Build();

// Caddy's way in, a Unix socket beside 127.0.0.1:8090 (BUGS #12): once listening, its group may connect too.
app.Lifetime.ApplicationStarted.Register(() => UnixSocket.OpenToGroup(app.Urls, app.Logger));

using (var scope = app.Services.CreateScope())
{
    // Migrations (not EnsureCreated) so the schema can evolve without losing tenant data.
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlatformDb>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

var tenants = app.Services.GetRequiredService<TenantService>();
var auth = app.Services.GetRequiredService<AuthService>();
var hubs = app.Services.GetRequiredService<HubRegistry>();
var payloads = app.Services.GetRequiredService<PayloadService>();
var productName = app.Configuration["Platform:ProductName"] ?? "The Remote Support Program";

// Pretend companies for tools/loadtest, on a throwaway server only — it refuses any database with a real one.
if (args.Length > 0 && args[0] == "seed-loadtest")
    return await LoadTestSeed.RunAsync(args, app.Services.GetRequiredService<IDbContextFactory<PlatformDb>>());
if (args.Length > 0 && args[0] != "run")
    return await Cli.RunAsync(args, tenants,
        app.Services.GetRequiredService<PlatformMailService>(),
        app.Services.GetRequiredService<TrialNoticeService>(),
        app.Services.GetRequiredService<AccountLinks>(),
        app.Services.GetRequiredService<TicketIntake>(),
        app.Services.GetRequiredService<PlatformAdmins>(),
        app.Services.GetRequiredService<PayfastService>(), app.Services.GetRequiredService<PilotService>(),
        app.Services.GetRequiredService<MicrosoftSignIn>());

// Signing somebody out reaches their console at once (2026-10-01): the hub closes it and every
// session window they had open. A change made through this process is checked at once; one made
// from the command line, a separate process, is found by AuthService's sweep within 15 s.
hubs.Auth = auth;
hubs.Knowledge = app.Services.GetRequiredService<KnowledgeBaseService>();
hubs.Outsiders = app.Services.GetRequiredService<OutsiderCodeService>();
hubs.Recurring = app.Services.GetRequiredService<RecurringTaskService>();
hubs.Activity = app.Services.GetRequiredService<TicketActivityService>();
hubs.ClientUpdates = app.Services.GetRequiredService<ClientUpdateService>();
hubs.Updates = app.Services.GetRequiredService<UpdateService>();
// Branding saved in the portal reaches every connected machine at once (2026-10-07, roadmap 4.6). A change made
// from the command line, a separate process, reaches each machine the next time it connects.
hubs.Tenants = tenants;
// Made now so it is listening before the first PayFast notice can arrive.
app.Services.GetRequiredService<CompanyBilling>();
auth.SignedOut += (tenantId, techId, name, why) => hubs.Loaded(tenantId)?.SignOutTechnician(techId, name, why);
auth.ConsoleReplaced += (tenantId, techId, keep, name, why) => hubs.Loaded(tenantId)?.SignOutTechnician(techId, name, why, keepToken: keep);
tenants.PersonChanged += id => _ = auth.RecheckAsync(id);

// First, so everything after it — the sign-in guard, the logs, the cookie's Secure flag — sees
// the caller's real address and scheme rather than Caddy's. See the ForwardedHeadersOptions above.
app.UseForwardedHeaders();

// The standard headers every page should carry (2026-10-01). No full Content-Security-Policy yet:
// the pages carry inline script, and a policy that breaks a page is worse than none. What IS here:
//   - nothing of ours may be framed by another site (clickjacking), EXCEPT website chat, whose
//     whole job is to sit in a frame on the company's own website;
//   - browsers never guess a file's type, and never send a portal address on to another site;
//   - once a browser has seen us over https it never uses plain http again (HSTS).
// Microphone and screen sharing are left alone: browser support (/help) and voice need both.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), geolocation=(), payment=(), usb=()";
    // The contact form (3.8.4) likewise: it sits in a frame on the company's site.
    var framed = ctx.Request.Path.StartsWithSegments("/webchat") || ctx.Request.Path.StartsWithSegments("/api/webchat")
                 || ctx.Request.Path.StartsWithSegments("/contact");
    if (!framed)
    {
        h["X-Frame-Options"] = "DENY";
        h["Content-Security-Policy"] = "frame-ancestors 'none'";
    }
    // The full policy, reported and not enforced (the pentest's F4) — CspReports says why.
    h["Content-Security-Policy-Report-Only"] = CspReports.Policy(framed);
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseStaticFiles();
app.UseAuthentication();
// The company's own clock for everything this request shows or reads (2026-10-05, CompanyClock) —
// never the server's, which is UTC on the live server. No company (the platform owner, a visitor):
// this server's region.
CompanyClock.ServerDefault = CompanyClock.Find(CompanyClock.DefaultFor(app.Configuration["Platform:Region"]));
app.Use(async (ctx, next) =>
{
    if (int.TryParse(ctx.User.FindFirst(PortalIdentity.TenantIdClaim)?.Value, out var tid) && tid > 0)
        CompanyClock.Set(await CompanyClock.ForTenantAsync(ctx.RequestServices.GetRequiredService<IDbContextFactory<PlatformDb>>(), tid));
    await next();
});
// THE FREE VERSION'S PORTAL (2026-10-06, Stage F): My account, Downloads, Machines, Upgrade — nothing else.
// Here, in front of every portal page and portal API, so no page can forget it (FreePortal). A refused page
// goes to Machines saying why; anything else gets a 403.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (FreePortal.Governs(path) && int.TryParse(ctx.User.FindFirst(PortalIdentity.TenantIdClaim)?.Value, out var tid) && tid > 0
        && !FreePortal.Allowed(path)
        && await ctx.RequestServices.GetRequiredService<TenantService>().ByIdAsync(tid) is { } t && RemoteSupport.Shared.FreePlan.Is(t.Plan))
    {
        if (HttpMethods.IsGet(ctx.Request.Method) && path.StartsWithSegments("/portal"))
        {
            // The dashboard is simply where a free company starts; anything else is said.
            var bare = path.Value!.TrimEnd('/');
            ctx.Response.Redirect(bare.Equals("/portal", StringComparison.OrdinalIgnoreCase) ? FreePortal.Home : FreePortal.Home + "?full=1");
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("That is part of the full version, not the Basic version.");
        return;
    }
    await next();
});
app.UseAuthorization();
app.MapRazorPages();

// Up or not, and nothing more (the pentest's F9): every check that asks reads only the status.
app.MapGet("/health", () => Results.Ok(new { ok = true }));

// What the report-only policy would have blocked (CspReports). Logged with "CSP report" in front, so
// journalctl -u remotesupport-platform | grep "CSP report" is the list of inline scripts still to move into files.
app.MapPost(CspReports.ReportPath, async (HttpContext ctx, ILoggerFactory logs) =>
{
    var log = logs.CreateLogger("CspReports");
    if (!CspReports.Allow(From(ctx))) return Results.NoContent();
    var buf = new char[CspReports.MaxBytes];
    using var reader = new StreamReader(ctx.Request.Body);
    var n = await reader.ReadBlockAsync(buf, 0, buf.Length);
    log.LogInformation("CSP report from {From}: {Report}", From(ctx), CspReports.OneLine(new string(buf, 0, n)));
    return Results.NoContent();
});

// Trades a one-use hand-off token for a portal sign-in cookie, so the console's Tickets button
// opens a browser as the person who is already signed in to the console. See PortalHandoffService.
//
// Anonymous by necessity — the browser arriving here has no cookie yet, which is the whole point.
// The token is what authenticates, and it is spent by the time this method returns.
app.MapGet("/portal/enter", async (HttpContext ctx, PortalHandoffService handoff, TenantService tenants, IDbContextFactory<PlatformDb> dbf, string? t) =>
{
    var (session, path) = handoff.Redeem(t);
    // Deliberately the same answer for expired, already-used and never-existed: there is nothing
    // useful to tell somebody holding a bad token, and telling them which kind it is is free
    // information for anybody guessing.
    if (session == null) return Results.Redirect("/login");

    var tenant = await tenants.ByIdAsync(session.TenantId);
    if (tenant == null || !tenant.IsServiceEnabled) return Results.Redirect("/login");

    // Their light or dark as it is NOW. The session is the one the console signed in with, and the
    // switch may have been flipped since — in the portal or in the console — so the value it carries
    // would put the portal back in the palette they had just left.
    var appearance = await auth.AppearanceOfAsync(session.TenantId, session.TechnicianId);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        PortalIdentity.Build(session, tenant.Slug, appearance));
    // A console not yet updated asks by the OLD id addresses (/portal/tickets/{id} …): translated here, within the
    // company, so the browser lands straight on the number and never shows the id (HANDOVER §159). Nothing of this
    // company's, and it is the queue.
    await using (var db = await dbf.CreateDbContextAsync())
        path = await PortalAddresses.FromOldAsync(db, session.TenantId, path) ?? PortalHandoffService.DefaultPath;
    // Redirect rather than render, so the spent token leaves the address bar immediately.
    return Results.Redirect(path);
}).AllowAnonymous();

// ---- workshop photos ----
// One endpoint, two callers: the portal's booking form and the console's New Ticket dialog. The
// gates live in TicketPhotoService, not here, so both meet the same rule.

// Two ways in, because the same upload serves the browser and the console. The portal cookie is
// how a booking form posts; the console holds a bearer token from /api/login and has no cookie at
// all. One endpoint rather than two, so the gates in TicketPhotoService cannot drift apart.
static (int tenantId, string who, TenantRole role, bool ok) PhotoUploader(HttpContext c, AuthService auth)
{
    var header = c.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        var s = auth.FromToken(header["Bearer ".Length..].Trim());
        if (s != null) return (s.TenantId, s.DisplayName, s.Role, true);
        return (0, "", TenantRole.Technician, false);
    }
    bool isStaff = c.User.IsInRole("tenant") || c.User.IsInRole("billing") || c.User.IsInRole("technician");
    if (!isStaff || !int.TryParse(c.User.FindFirst(PortalIdentity.TenantIdClaim)?.Value, out var id))
        return (0, "", TenantRole.Technician, false);
    return (id, c.User.Identity?.Name ?? "", PortalIdentity.RoleOf(c.User), true);
}

// The dropdown under a quote's Item box (stage 3.7; a dropdown since 2026-09-29). Nothing typed is
// the whole list; every typed word must start a word of the item, in any order, so "mem ddr" finds
// "DDR4 memory 8GB" — QuoteService.BrowseAsync.
app.MapGet("/api/portal/items/suggest", async (HttpContext ctx, QuoteService quotes, string? q) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    var items = await quotes.BrowseAsync(tenantId, q);
    return Results.Ok(items.Select(i => new { id = i.Id, name = i.Name, part = i.LastPartNumber }));
});

static (int tenantId, string who, bool ok) PortalUser(HttpContext c)
{
    bool isStaff = c.User.IsInRole("tenant") || c.User.IsInRole("billing") || c.User.IsInRole("technician");
    if (!isStaff || !int.TryParse(c.User.FindFirst(PortalIdentity.TenantIdClaim)?.Value, out var id)) return (0, "", false);
    return (id, c.User.Identity?.Name ?? "", true);
}

// ---- session recordings (1.6) ----
// The console uploads the .mp4 when the technician stops recording, as the raw request body, with a
// bearer token. Streamed to disk: a long session is hundreds of megabytes, far past Kestrel's 30 MB
// default, which is lifted for this one endpoint only.
app.MapPost("/api/recordings", async (HttpContext ctx, RecordingService recs, AuthService auth, HubRegistry hubs, string session, int seconds) =>
{
    var (tenantId, who, role, ok) = PhotoUploader(ctx, auth);
    if (!ok || !ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
    var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = RecordingService.MaxBytes;
    var (rec, err) = await recs.AddAsync(tenantId, session ?? "", who, role, ctx.Request.Body, seconds, ctx.RequestAborted);
    if (rec == null) return Results.Json(new { error = err }, statusCode: 400);
    hubs.Loaded(tenantId)?.RecordingSaved(rec);
    return Results.Ok(new { id = rec.Id, ticketId = rec.TicketId, bytes = rec.Bytes });
});

// ---- stored files by their portal address (2026-10-23, HANDOVER §159) ----
// A photo, a comment's file, an article's picture and a recording are each addressed by a random token on its row
// (/portal/p|f|i|v/{token}), looked up WITH the signed-in company — never by the id one sequence gives every company.
// The old id addresses below still answer, for the company's own people only, by sending them on to the token address;
// another company's id is the same 404 as a token that does not exist.
static async Task<IResult> OldFileAddress(HttpContext ctx, IDbContextFactory<PlatformDb> dbf)
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    await using var db = await dbf.CreateDbContextAsync();
    var to = await PortalAddresses.FromOldAsync(db, tenantId, ctx.Request.Path + ctx.Request.QueryString);
    return to == null ? Results.NotFound() : Results.Redirect(to);
}

// Watching one: anybody who may see its ticket, and range requests so the player can seek.
app.MapGet("/portal/v/{token}", async (HttpContext ctx, RecordingService recs, IDbContextFactory<PlatformDb> dbf, string token) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    await using var db = await dbf.CreateDbContextAsync();
    var id = await PortalAddresses.RecordingIdAsync(db, tenantId, token);
    var r = id == 0 ? null : await recs.GetForViewerAsync(tenantId, id, PortalIdentity.RoleOf(ctx.User), me);
    if (r == null) return Results.NotFound();
    var path = recs.FilePath(tenantId, r.FileName);
    return File.Exists(path) ? Results.File(path, "video/mp4", enableRangeProcessing: true) : Results.NotFound();
});
app.MapGet("/portal/recordings/{id:long}", (HttpContext ctx, IDbContextFactory<PlatformDb> dbf) => OldFileAddress(ctx, dbf));
app.MapGet("/portal/photo/{id:long}", (HttpContext ctx, IDbContextFactory<PlatformDb> dbf) => OldFileAddress(ctx, dbf));
app.MapGet("/portal/attachment/{id:long}", (HttpContext ctx, IDbContextFactory<PlatformDb> dbf) => OldFileAddress(ctx, dbf));
app.MapGet("/portal/kb/image/{id:long}", (HttpContext ctx, IDbContextFactory<PlatformDb> dbf) => OldFileAddress(ctx, dbf));

// ?ticket= is the ticket's NUMBER in the company, where it goes back to.
app.MapPost("/portal/v/{token}/delete", async (HttpContext ctx, RecordingService recs, IDbContextFactory<PlatformDb> dbf, string token, int ticket) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    await using var db = await dbf.CreateDbContextAsync();
    var id = await PortalAddresses.RecordingIdAsync(db, tenantId, token);
    if (id == 0) return Results.NotFound();
    var err = await recs.DeleteAsync(tenantId, id, PortalIdentity.RoleOf(ctx.User));
    return Results.Redirect(PortalAddresses.Ticket(ticket) + (err.Length > 0 ? "?msg=" + Uri.EscapeDataString(err) : ""));
}).RequireAuthorization();
// A ticket page left open since before 2026-10-23: the recording by id, the ticket by id (its old address sends it on).
app.MapPost("/portal/recordings/{id:long}/delete", async (HttpContext ctx, RecordingService recs, long id, int ticket) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    var err = await recs.DeleteAsync(tenantId, id, PortalIdentity.RoleOf(ctx.User));
    return Results.Redirect($"/portal/tickets/{ticket}" + (err.Length > 0 ? "?msg=" + Uri.EscapeDataString(err) : ""));
}).RequireAuthorization();

app.MapPost("/api/portal/photos", async (HttpContext ctx, TicketPhotoService photos, TicketService tickets, AuthService auth, IDbContextFactory<PlatformDb> dbf) =>
{
    var (tenantId, who, role, ok) = PhotoUploader(ctx, auth);
    if (!ok) return Results.Unauthorized();
    // Photos of a job are ticket work; an owner-only login oversees and comments (2026-10-15). A comment's files go
    // through /api/portal/attachments, which it may use.
    if (!Permissions.CanWorkTickets(role)) return Results.Json(new { error = Permissions.OwnerOnlyRefusal }, statusCode: 403);
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected a file upload." });

    // Several phone photos in one upload are past both of ASP.NET's defaults (30 MB for the body,
    // 128 MB for a form); see TicketPhotoService.MaxRequestBytes. Raised for this endpoint only.
    var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = TicketPhotoService.MaxRequestBytes;
    ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(ctx.Request,
        new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = TicketPhotoService.MaxRequestBytes }));
    var form = await ctx.Request.ReadFormAsync();
    // "part" is a quote line's photo (stage 3.7): the console uploads it, then names its id on the line.
    var kind = form["kind"] == "after" ? PhotoKind.After : form["kind"] == "part" ? PhotoKind.Part : PhotoKind.Before;
    var stageId = form["stage"].ToString();
    int? ticketId = int.TryParse(form["ticketId"], out var tid) && tid > 0 ? tid : null;
    // The portal names the ticket by its number in the company (HANDOVER §159); the console by its id, as always.
    if (int.TryParse(form["ticketNumber"], out var tnum) && tnum > 0)
    {
        await using var ndb = await dbf.CreateDbContextAsync();
        ticketId = await PortalAddresses.TicketIdAsync(ndb, tenantId, tnum);
        if (ticketId == 0) return Results.NotFound();
    }

    // A photo may only be added to a ticket this person is allowed to touch, and only to a
    // workshop one — every other category has no machine on a bench to photograph.
    if (ticketId.HasValue)
    {
        var t = await tickets.GetAsync(tenantId, ticketId.Value);
        if (t == null) return Results.NotFound();
        // A call out may take its "as it arrived" photos too: they are how it books in to the workshop.
        // So may a walk in, at the counter, before it is booked in.
        // And since 2026-10-13 any OPEN ticket: changing a ticket's category to Workshop needs them first (TicketService.EditAsync).
        bool toWorkshop = kind == PhotoKind.Before
            && ((t.Category == TicketCategory.CallOut && t.Stage == Workflow.CallOutWorkDone)
                || (t.Category == TicketCategory.WalkIn && t.Stage == Workflow.WalkInFirst)
                || (t.Stage.Length > 0 && Workflow.Stage(t.Stage)?.Lane is { } lane && lane != WorkLane.Done));
        // A part's photo goes with a quote, and a General job asks for quotes too (2026-10-06: the
        // portal now sends them this way, one at a time, so several can be added to one item).
        if (t.Category != TicketCategory.Workshop && !toWorkshop && kind != PhotoKind.Part) return Results.BadRequest(new { error = "Photos belong to workshop jobs." });
        // The technician who did the work may still change the quote's items once it has moved on
        // (QuoteService.MayEditLines), so may add a part's photos too.
        if (!Permissions.CanSeeAllTickets(role) && !Assignees.Has(t.AssignedTo, who) && t.AssignedTo.Length > 0
            && !(kind == PhotoKind.Part && Assignees.Has(t.WorkedBy, who)))
            return Results.NotFound();
    }

    var results = new List<object>();
    foreach (var file in form.Files)
    {
        if (file.Length <= 0 || file.Length > TicketPhotoService.MaxBytes)
        { results.Add(new { error = $"{file.FileName}: too large or empty." }); continue; }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var (photo, error) = await photos.AddAsync(tenantId, ms.ToArray(), file.FileName, kind, who, ticketId, stageId);
        results.Add(error.Length > 0 ? new { error = $"{file.FileName}: {error}" } : (object)new { id = photo!.Id, token = photo.Token, name = photo.OriginalName });
    }
    return Results.Ok(new { photos = results });
}).DisableAntiforgery();

// Serves one photo back. Scoped to the signed-in company, so a photo id from another tenant is a
// 404 rather than somebody else's customer's laptop.
app.MapGet("/portal/p/{token}", async (HttpContext ctx, TicketPhotoService photos, IDbContextFactory<PlatformDb> dbf, string token) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    await using var db = await dbf.CreateDbContextAsync();
    var id = await PortalAddresses.PhotoIdAsync(db, tenantId, token);
    var p = id == 0 ? null : await photos.GetAsync(tenantId, id);
    if (p == null) return Results.NotFound();
    var path = photos.FilePath(tenantId, p.FileName);
    if (!File.Exists(path)) return Results.NotFound();
    var type = TicketPhotoService.SniffType(await File.ReadAllBytesAsync(path)) is { Length: > 0 } t ? t : "application/octet-stream";
    return Results.File(path, type);
});

// ---- files and photos on comments (2026-10-13) ----
// One file per request, as it is picked — the portal and the console both — held loose on the ticket by whoever
// picked it until the comment that carries it is posted. Every rule is TicketAttachmentService's.
// A browser's request must say X-Attach as well as carry the cookie, as office chat's say X-Chat: another site can make
// the browser send the cookie, but not a header of its own. The console signs with its bearer token instead.
static bool AttachWrite(HttpContext c) =>
    c.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || c.Request.Headers["X-Attach"] == "1";
app.MapPost("/api/portal/attachments", async (HttpContext ctx, TicketAttachmentService files, AuthService auth, IDbContextFactory<PlatformDb> dbf) =>
{
    var (tenantId, who, role, ok) = PhotoUploader(ctx, auth);
    if (!ok) return Results.Unauthorized();
    if (!AttachWrite(ctx)) return Results.BadRequest(new { error = "Expected a file from the ticket page." });
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected a file upload." });
    var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = TicketAttachmentService.MaxBytes + 1024 * 1024;
    var form = await ctx.Request.ReadFormAsync();
    // The portal names the ticket by its number in the company (HANDOVER §159); the console by its id.
    int ticketId = 0;
    if (int.TryParse(form["ticketNumber"], out var tnum) && tnum > 0)
    {
        await using var ndb = await dbf.CreateDbContextAsync();
        ticketId = await PortalAddresses.TicketIdAsync(ndb, tenantId, tnum);
        if (ticketId == 0) return Results.NotFound();
    }
    else if (!int.TryParse(form["ticketId"], out ticketId) || ticketId <= 0) return Results.BadRequest(new { error = "Which ticket is it for?" });
    // Only on a ticket this person may see — the ticket page's own question.
    await using (var db = await dbf.CreateDbContextAsync())
        if (!await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == tenantId), role, who).AnyAsync(t => t.Id == ticketId))
            return Results.NotFound();
    var results = new List<object>();
    foreach (var file in form.Files)
    {
        if (TicketAttachmentService.Refusal(file.FileName, file.Length) is { Length: > 0 } no) { results.Add(new { error = no }); continue; }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var (a, error) = await files.AddAsync(tenantId, ticketId, ms.ToArray(), file.FileName, who);
        results.Add(a == null ? new { error } : (object)new { id = a.Id, name = a.OriginalName, size = TicketAttachmentService.Size(a.Bytes), image = a.IsImage });
    }
    return Results.Ok(new { files = results });
}).DisableAntiforgery();

// The × beside a picked file, before the comment is posted. Only the person who picked it.
app.MapPost("/api/portal/attachments/{id:long}/remove", async (HttpContext ctx, TicketAttachmentService files, AuthService auth, long id) =>
{
    var (tenantId, who, _, ok) = PhotoUploader(ctx, auth);
    if (!ok) return Results.Unauthorized();
    if (!AttachWrite(ctx)) return Results.BadRequest(new { error = "Expected the ticket page." });
    var why = await files.RemoveLooseAsync(tenantId, id, who);
    return why.Length == 0 ? Results.Ok(new { }) : Results.BadRequest(new { error = why });
}).DisableAntiforgery();

// Opening one: anybody who may see its ticket. A picture or a PDF — told by its bytes — opens in the browser;
// anything else downloads under the name it was picked with, never shown as a page of ours.
app.MapGet("/portal/f/{token}", async (HttpContext ctx, TicketAttachmentService files, IDbContextFactory<PlatformDb> dbf, string token) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    await using var db = await dbf.CreateDbContextAsync();
    var id = await PortalAddresses.AttachmentIdAsync(db, tenantId, token);
    var a = id == 0 ? null : await files.GetForViewerAsync(tenantId, id, PortalIdentity.RoleOf(ctx.User), me);
    if (a == null) return Results.NotFound();
    var path = files.FilePath(tenantId, a.FileName);
    if (!File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    bool shown = a.IsImage || a.ContentType == "application/pdf";
    if (!shown) return Results.File(path, "application/octet-stream", a.OriginalName);
    var inline = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
    inline.SetHttpFileName(a.OriginalName);
    ctx.Response.Headers.ContentDisposition = inline.ToString();
    return Results.File(path, a.ContentType);
});

// A quote request signature's image (2026-10-06). PUBLIC, by a random token that changes with every
// new image: web mail fetches it once the signature is pasted in, and the supplier's mail program
// fetches it after that. It goes to suppliers anyway; the token is only so the addresses cannot be
// walked. Never changes once written, so it may be kept. Only PNG, JPEG or GIF is ever stored.
app.MapGet("/quote-signature/{token}", async (HttpContext ctx, QuoteSignatureService sigs, string token) =>
{
    if (await sigs.ImageAsync(token) is not { } img) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(img.bytes, img.type);
});

// ---- the knowledge base (3.8.5) ----
// A picture pasted or dropped into the editor goes up the moment it arrives, and the editor writes
// [image:N] where it was put. Writes need X-Kb as well as the cookie, as office chat's need X-Chat: a
// page on another site can make the browser send the cookie, but not a header of its own.
static bool KbWrite(HttpContext c) => c.Request.Headers["X-Kb"] == "1";

app.MapPost("/api/portal/kb/images", async (HttpContext ctx, KnowledgeBaseService kb) =>
{
    var (tenantId, who, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    if (!KbWrite(ctx) || !ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected a picture." });
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null || file.Length <= 0) return Results.BadRequest(new { error = "That picture was empty." });
    if (file.Length > KnowledgeBaseService.MaxImageBytes) return Results.BadRequest(new { error = $"That picture is larger than {KnowledgeBaseService.MaxImageBytes / (1024 * 1024)} MB." });
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var (img, err) = await kb.AddImageAsync(tenantId, ms.ToArray(), who);
    return img == null ? Results.BadRequest(new { error = err }) : Results.Ok(new { id = img.Id, tag = KbText.ImageTag(img.Id) });
}).DisableAntiforgery();

// A picture, for this company's people only. Never changes once written, so it may be kept.
app.MapGet("/portal/i/{token}", async (HttpContext ctx, KnowledgeBaseService kb, string token) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    if (await kb.ImageByTokenAsync(tenantId, token) is not { } f) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=604800, immutable";
    return Results.File(f.path, f.type);
});

// The editor's Folder box (2026-10-06): a section's folders, nested, for the client just chosen —
// client 0 or none is General. Reading only, so no X-Kb; this company's folders only.
app.MapGet("/api/portal/kb/folders", async (HttpContext ctx, KnowledgeBaseService kb, int? client) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    var section = await kb.FoldersAsync(tenantId, client);
    return Results.Ok(KnowledgeBaseService.Tree(section).Select(t => new { id = t.folder.Id, label = KnowledgeBaseService.TreeLabel(t.folder.Name, t.depth) }));
});

// The editor's Preview: the SAME renderer the article page uses, so what is previewed is what is saved.
app.MapPost("/api/portal/kb/preview", async (HttpContext ctx, KnowledgeBaseService kb) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    if (!KbWrite(ctx)) return Results.BadRequest();
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    if (body.Length > KnowledgeBaseService.MaxBody) body = body[..KnowledgeBaseService.MaxBody];
    return Results.Ok(new { html = KbText.Render(body, await kb.ImageAddressesAsync(tenantId, body)), password = KbText.LooksLikePassword(body) });
}).DisableAntiforgery();

// client type-ahead for the portal's new-ticket form (signed-in staff only)
// Technicians too (2026-09-29): Add a client and New ticket are everybody's since 3.6.3, and this
// check still said owner and office only — so a technician's type-ahead was silently empty.
app.MapGet("/api/portal/clients", async (HttpContext ctx, ClientService clients, string? term, int? take, bool? people) =>
{
    var (tenantId, _, ok) = PortalUser(ctx);
    if (!ok) return Results.Unauthorized();
    // The client picker (2026-10-02) asks for more rows, and for "whose machine" their people too.
    if (take.HasValue || people == true)
    {
        var picked = await clients.PickAsync(tenantId, term ?? "", people == true, Math.Clamp(take ?? 12, 1, 100));
        return Results.Ok(picked.Select(x => new
        {
            id = x.client.Id, number = x.client.Number, name = x.client.Name, display = x.client.Display,
            people = x.people.Select(p => new { id = p.Id, name = p.Name, role = p.Role })
        }));
    }
    var list = await clients.SearchAsync(tenantId, term ?? "");
    return Results.Ok(list.Select(c => new { id = c.Id, number = c.Number, name = c.Name, display = c.Display }));
});

// ---- office chat (3.8.1, 2026-09-30): the portal's half. The console's is the hub's. ----
// Writes need the X-Chat header as well as the cookie: a page on another site can make the browser
// send the cookie, but it cannot add a header without the browser asking this server first, which
// it does not answer. Reads are the signed-in person's own conversations and nothing else.
static bool ChatWrite(HttpContext c) => c.Request.Headers["X-Chat"] == "1";
static object ChatJson(RemoteSupport.Shared.StaffChatInfo m) => new { id = m.Id, from = m.From, to = m.To, text = m.Text, sentUtc = m.SentUtc };

app.MapGet("/api/portal/chat", async (HttpContext ctx, StaffChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0) return Results.Unauthorized();
    var o = await chat.OverviewAsync(tenantId, me);
    return Results.Ok(new
    {
        me, people = o.People, totalUnread = o.TotalUnread,
        threads = o.Threads.Select(t => new { with = t.With, unread = t.Unread, last = t.Last == null ? null : ChatJson(t.Last) })
    });
});
app.MapGet("/api/portal/chat/history", async (HttpContext ctx, StaffChatService chat, string? with, long? before) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0) return Results.Unauthorized();
    var rows = await chat.HistoryAsync(tenantId, me, with, before);
    return Results.Ok(rows.Select(r => ChatJson(StaffChatService.Info(r))));
});
app.MapPost("/api/portal/chat/send", async (HttpContext ctx, StaffChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0 || !ChatWrite(ctx)) return Results.Unauthorized();
    var body = await ctx.Request.ReadFromJsonAsync<ChatSendRequest>();
    var (m, err) = await chat.SendAsync(tenantId, me, body?.To, body?.Text);
    return m == null ? Results.Json(new { error = err }, statusCode: 400) : Results.Ok(ChatJson(StaffChatService.Info(m)));
});
app.MapPost("/api/portal/chat/read", async (HttpContext ctx, StaffChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0 || !ChatWrite(ctx)) return Results.Unauthorized();
    var body = await ctx.Request.ReadFromJsonAsync<ChatReadRequest>();
    await chat.MarkReadAsync(tenantId, me, body?.With, body?.UpTo ?? 0);
    return Results.Ok();
});
// Live: every message this person is part of, and a nudge when what they have read changes (in
// another tab, or the console), as server-sent events. A comment every 20 s keeps proxies from
// closing a quiet line; the browser reconnects by itself if it drops.
app.MapGet("/api/portal/chat/stream", async (HttpContext ctx, StaffChatService chat, WebChatService webChat, StaffPresence presence, TicketService tickets, TicketActivityService activity) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0) { ctx.Response.StatusCode = 401; return; }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
    Action<int, StaffMessage> onSent = (t, m) =>
    {
        if (t != tenantId || !StaffChatService.Involves(m, me)) return;
        var mention = m.To.Length == 0 && m.From != me && StaffChatService.Mentions(m.Text, me);
        queue.Writer.TryWrite("event: message\ndata: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            message = ChatJson(StaffChatService.Info(m)), mentionsMe = mention
        }) + "\n\n");
    };
    Action<int, string> onRead = (t, p) => { if (t == tenantId && p == me) queue.Writer.TryWrite("event: read\ndata: {}\n\n"); };
    // Website chat (3.8.3): every chat and line of the company, to everybody signed in — and an open
    // portal page is somebody here to answer, which is what decides whether the bubble takes chats.
    Action<int, RemoteSupport.Shared.WebChatInfo> onWebChat = (t, c) => { if (t == tenantId) queue.Writer.TryWrite("event: webchat\ndata: " + System.Text.Json.JsonSerializer.Serialize(WebChatJson(c)) + "\n\n"); };
    Action<int, RemoteSupport.Shared.WebChatLineInfo> onWebLine = (t, l) => { if (t == tenantId) queue.Writer.TryWrite("event: webline\ndata: " + System.Text.Json.JsonSerializer.Serialize(WebLineJson(l)) + "\n\n"); };
    // The billing queue (2026-10-05): to the people told, the new count, and — when a job has just
    // arrived and somebody else put it there — the job, which pops up.
    Action<int, BillingNews> onBilling = (t, n) =>
    {
        if (t != tenantId || !n.Notify.Contains(me)) return;
        var a = n.Arrived != null && n.Arrived.By != me ? n.Arrived : null;
        queue.Writer.TryWrite("event: billing\ndata: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            count = n.Count,
            // By its number in the company only (HANDOVER §159): chat.js links to /portal/t/{number}.
            arrived = a == null ? null : new { number = a.Number, subject = a.Subject, stage = a.Stage, by = a.By }
        }) + "\n\n");
    };
    chat.Sent += onSent; chat.ReadChanged += onRead;
    webChat.Changed += onWebChat; webChat.Line += onWebLine;
    tickets.BillingChanged += onBilling;
    // Latest activity (2026-10-26, HANDOVER §164): something new on the company's tickets, or this person looked somewhere
    // else — their count, which chat.js puts on the tab and the Latest activity page takes as "fetch again".
    var techId = PortalIdentity.TechnicianIdOf(ctx.User);
    async Task TellActivityAsync()
    {
        try
        {
            var (n, mine) = await activity.NewCountAsync(tenantId, techId);
            queue.Writer.TryWrite("event: activity\ndata: " + System.Text.Json.JsonSerializer.Serialize(new { count = n, label = ActivityWords.Count(n), mine }) + "\n\n");
        }
        catch { /* the next one will say it */ }
    }
    Action<int> onActivity = t => { if (t == tenantId) _ = TellActivityAsync(); };
    Action<int, string> onSeen = (t, p) => { if (t == tenantId && p == me) _ = TellActivityAsync(); };
    activity.Changed += onActivity; activity.SeenChanged += onSeen;
    // Only somebody who may answer counts as here, or a visitor would wait for an owner-only login that cannot reply.
    using var here = Permissions.CanAnswerWebChat(PortalIdentity.RoleOf(ctx.User)) ? presence.Arrive(tenantId, me) : null;
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            string line;
            try { line = await queue.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { line = ": ping\n\n"; }
            await ctx.Response.WriteAsync(line, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally { chat.Sent -= onSent; chat.ReadChanged -= onRead; webChat.Changed -= onWebChat; webChat.Line -= onWebLine; tickets.BillingChanged -= onBilling; activity.Changed -= onActivity; activity.SeenChanged -= onSeen; }
});

// ---- the owner key opens nothing by itself (2026-10-15) ----
// /api/admin/status and /api/admin/tenants used to take the key alone in an X-Admin-Key header - no authenticator
// code - and list every company. Nothing used them, and /owner shows the same behind the key AND a code
// (OwnerTwoFactor), so they are gone rather than guarded. Do not bring back a way in on the key alone:
// securitycheck fails if either address answers it.
string? adminKey = app.Configuration["Platform:AdminKey"];

// ---- the control dashboard's way in to this server (2026-10-13, stage B) ----
// Platform:ServerKey, presented by the control server as a bearer token — never an admin's password. No key set: these
// do not exist (404), which is what a server too old for the dashboard says as well. Wrong keys count against the
// caller's address like wrong passwords (SignInGuard), so the key cannot be guessed at speed.
string? serverKey = app.Configuration["Platform:ServerKey"];
async Task<IResult?> ControlRefusal(HttpContext c)
{
    if (string.IsNullOrEmpty(serverKey) || serverKey.Length < 32) return Results.NotFound();
    var from = c.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (auth.Guard.RetryAfter("server-key", from) > 0) return Results.StatusCode(429);
    var given = c.Request.Headers.Authorization.ToString();
    given = given.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? given[7..].Trim() : "";
    if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(serverKey))) return null;
    await Task.Delay(Random.Shared.Next(200, 500));
    auth.Guard.Fail("server-key", from);
    app.Logger.LogWarning("Control API: a wrong server key from {From}", from);
    return Results.Unauthorized();
}
app.MapGet("/api/control/status", async (HttpContext c) =>
    await ControlRefusal(c) ?? Results.Json(await app.Services.GetRequiredService<ServerReporter>().BuildAsync(), ControlServers.Json));
app.MapPost("/api/control/act", async (HttpContext c, ControlActRequest req) =>
{
    if (await ControlRefusal(c) is { } no) return no;
    var level = Enum.IsDefined((AdminLevel)req.Level) ? (AdminLevel)req.Level : AdminLevel.Viewer;
    var by = new PlatformAdmins.Actor(null, $"{req.By} (from {req.From})", level);
    var company = (await tenants.ByIdAsync(req.Id))?.CompanyName ?? (await tenants.WaitingListAsync()).FirstOrDefault(w => w.Id == req.Id)?.CompanyName ?? "";
    var done = await ControlServers.Dispatch(app.Services.GetRequiredService<AdminCompanies>(), by, req.Action, req.Id, req.Args ?? new(), app.Services.GetRequiredService<UpdateService>(), app.Services.GetRequiredService<OpenPlaces>(), app.Services.GetRequiredService<CompanyExportService>());
    return Results.Json(new ControlActAnswer(done.Error, done.Said, company), ControlServers.Json);
});

// ---- technician app login ----
// Three steps since 2026-10-01 (AuthService): the password, then either the six-digit code or —
// for somebody with no authenticator yet — setting one up. Every answer that is not a token is a
// 401 carrying an `error` an OLD console can show as it stands ("update your console"), and a
// `step` a new one acts on instead.
const string OldConsole = "This account now uses two-factor sign-in, which this console is too old for. " +
                          "Please download the new console from your company's technician link.";
string From(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";

async Task<IResult> LoginAnswer(SignInResult r)
{
    switch (r.Step)
    {
        case SignInStep.Code:
            return Results.Json(new { error = OldConsole, step = "code", pending = r.Pending, wrong = r.Error, notice = r.Notice }, statusCode: 401);
        case SignInStep.Enrol:
            return Results.Json(new
            {
                error = OldConsole, step = "enrol", pending = r.Pending, secret = r.Secret, secretGrouped = Totp.Grouped(r.Secret),
                qrPng = Convert.ToBase64String(QrImage.Png(r.OtpUri)), wrong = r.Error
            }, statusCode: 401);
        case SignInStep.Refused:
            return Results.Json(new { error = r.Error }, statusCode: 401);
    }
    var session = r.Session!;
    if (!session.Role.Has(TenantRoles.Seated))
    {
        auth.Revoke(r.Token);
        return Results.Json(new { error = session.Role.Has(TenantRole.Billing)
            ? "This is an office administrator login. Sign in on the website; the console is for technicians."
            // An owner-only login is seat-free BECAUSE it stays out of the console. Ticking
            // Supervisor or Technician beside Owner on the People page is what buys a seat and a way in.
            : "This is an owner login without a seat. Sign in on the website, or tick Supervisor or Technician beside Owner on the People page to use the console." }, statusCode: 403);
    }
    var t = await tenants.ByIdAsync(session.TenantId);
    return Results.Ok(new
    {
        token = r.Token, name = session.DisplayName, username = session.Email, tenant = new { t!.Slug, t.CompanyName, t.ProductName },
        isAdmin = session.IsAdmin, appearance = session.Appearance, recoveryCodes = r.RecoveryCodes ?? Array.Empty<string>(),
        // Kept by the console and sent back with the next sign-in: the code is not asked again until it runs out.
        trust = r.Trust
    });
}

app.MapPost("/api/login", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(Json.Options);
    if (body == null) return Results.BadRequest();
    await Task.Delay(Random.Shared.Next(100, 300));
    return await LoginAnswer(await auth.PasswordAsync(body.Email ?? body.Username ?? "", body.Password, body.Code, From(ctx), "console",
        string.IsNullOrWhiteSpace(body.Trust) ? null : new[] { body.Trust }, body.Device));
});
app.MapPost("/api/login/code", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<LoginStepRequest>(Json.Options);
    if (body == null) return Results.BadRequest();
    await Task.Delay(Random.Shared.Next(100, 300));
    return await LoginAnswer(await auth.CodeAsync(body.Pending ?? "", body.Code ?? "", From(ctx)));
});
app.MapPost("/api/login/enrol", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<LoginStepRequest>(Json.Options);
    if (body == null) return Results.BadRequest();
    await Task.Delay(Random.Shared.Next(100, 300));
    return await LoginAnswer(await auth.EnrolAsync(body.Pending ?? "", body.Code ?? "", From(ctx)));
});

// The console closing — Exit or Sign out (2026-10-05, one sign-in at a time). Its token goes, so
// signing in on another computer afterwards pushes nobody off and asks no extra code. Bearer only;
// an unknown token is answered the same, there being nothing to tell.
app.MapPost("/api/logout", (HttpContext ctx) =>
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) auth.ConsoleSignedOut(header["Bearer ".Length..].Trim());
    return Results.Ok();
}).DisableAntiforgery();

// The console's light-or-dark switch (3.6.9). The same column the portal's switch writes — one
// setting on the person, whichever place they flip it from. Bearer only: the console has no cookie,
// and the portal has its own form at /portal/appearance that also re-issues the cookie.
app.MapPost("/api/appearance", async (HttpContext ctx) =>
{
    var header = ctx.Request.Headers.Authorization.ToString();
    var s = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.FromToken(header["Bearer ".Length..].Trim()) : null;
    if (s == null) return Results.Unauthorized();
    var body = await ctx.Request.ReadFromJsonAsync<AppearanceRequest>(Json.Options);
    var saved = await auth.SaveAppearanceAsync(s.TenantId, s.TechnicianId, body?.Theme);
    return saved == null ? Results.Unauthorized() : Results.Ok(new { appearance = saved });
}).DisableAntiforgery();

// ---- monthly health check: the machine does the work, the server sends the mail ----
//
// Plain HTTP POST rather than new websocket messages. BinKind.FileChunk is session-scoped and a
// monthly run has no session, so chunking a multi-megabyte PDF down the device socket would be new
// protocol surface for no gain. The machine already holds a DeviceId and DeviceKey for the
// websocket, so it authenticates with those. See docs/PLAN-HEALTHCHECK.md.

// What a machine needs before it builds its report: which customer it belongs to. The installer
// cannot know - a machine is linked to a client in the portal afterwards - so it asks each month.
app.MapGet("/api/healthcheck/context", async (HttpContext ctx, HealthCheckService hc) =>
{
    var deviceId = ctx.Request.Query["deviceId"].ToString();
    var deviceKey = ctx.Request.Query["deviceKey"].ToString();
    var auth = await hc.AuthenticateAsync(deviceId, deviceKey);
    if (auth == null) return Results.Unauthorized();
    var (tenant, device) = auth.Value;
    return Results.Json(new
    {
        clientName = await hc.ClientNameAsync(device),
        machineName = device.MachineName,
        companyName = tenant.CompanyName,
        enabled = tenant.HealthCheckReady,
        // THIS machine, not the company: the customer's tick and the machine's own override. The
        // monthly run removes its own task when this is false (2026-09-28). `enabled` is kept for
        // any older script still asking, which never read it anyway.
        wanted = await hc.WantedAsync(tenant, device),
        runMaintenance = tenant.HealthCheckRunMaintenance
    });
});

app.MapPost("/api/healthcheck/report", async (HttpContext ctx, HealthCheckService hc, ReportPdfService pdfs) =>
{
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "expected a multipart upload" });
    var form = await ctx.Request.ReadFormAsync();

    var auth = await hc.AuthenticateAsync(form["deviceId"].ToString(), form["deviceKey"].ToString());
    if (auth == null) return Results.Unauthorized();
    var (tenant, device) = auth.Value;

    // Accepted even when the tenant has since switched the check off. The machine did the work a
    // month ago under the settings it had; throwing the result away would lose it for nothing.
    var dataFile = form.Files["data"];
    if (dataFile == null || dataFile.Length == 0) return Results.BadRequest(new { error = "no data file" });

    string json;
    using (var r = new StreamReader(dataFile.OpenReadStream())) json = await r.ReadToEndAsync();

    byte[]? pdf = null;
    var pdfFile = form.Files["report"];
    if (pdfFile is { Length: > 0 })
    {
        using var ms = new MemoryStream();
        await pdfFile.CopyToAsync(ms);
        pdf = ms.ToArray();
    }

    // Optional: absent when maintenance was skipped, and absent from any endpoint older than this.
    string? maintenanceJson = null;
    var maintFile = form.Files["maintenance"];
    if (maintFile is { Length: > 0 })
        using (var mr = new StreamReader(maintFile.OpenReadStream())) maintenanceJson = await mr.ReadToEndAsync();

    var run = await hc.RecordAsync(tenant, device, json, pdf, pdfFile?.FileName ?? "report.pdf", maintenanceJson);

    // A machine that ran its check as SYSTEM could not make a PDF - Edge refuses to start under
    // that account - so it sends the HTML. Upgrade it here, before the mail goes out, so the
    // attachment is the proper artefact. Failure is not fatal: the HTML is a complete report.
    var pdfNote = await hc.TryUpgradeToPdfAsync(run, pdfs);

    // Sending is attempted now but never fails the upload. The machine has done its part and must
    // be told so, or it will upload the same run again next month on top of a mail problem.
    var mail = tenant.HealthCheckReady
        ? await hc.SendAsync(tenant, run, await hc.ClientNameAsync(device))
        : "the health check is switched off for this company";

    return Results.Json(new { stored = true, runId = run.Id, mail, report = run.PdfFile, pdf = pdfNote.Length == 0 ? "ok" : pdfNote });
});

// ---- branding for installers / apps ----
app.MapGet("/api/branding", async (string key) => { var t = await tenants.ByKeyAsync(key); return t == null ? Results.NotFound() : Results.Json(tenants.ToBranding(t), Json.Options); });
app.MapGet("/api/branding/logo", async (string key) =>
{
    var t = await tenants.ByKeyAsync(key);
    if (t == null || string.IsNullOrEmpty(t.LogoFile)) return Results.NotFound();
    var path = Path.Combine(tenants.LogoDir, t.LogoFile);
    return File.Exists(path) ? Results.File(path, t.LogoFile.EndsWith(".png") ? "image/png" : "image/jpeg") : Results.NotFound();
});
// logo by slug for the public download page and portal preview
app.MapGet("/logo/{slug}", async (string slug) =>
{
    var t = await tenants.BySlugAsync(slug);
    if (t == null || string.IsNullOrEmpty(t.LogoFile)) return Results.NotFound();
    var path = Path.Combine(tenants.LogoDir, t.LogoFile);
    return File.Exists(path) ? Results.File(path, t.LogoFile.EndsWith(".png") ? "image/png" : "image/jpeg") : Results.NotFound();
});

// ---- branded downloads ----
// Microsoft's proof that theremotesupportprogram.com is ours (2026-10-19, §151): Entra fetches this file when the domain
// is set as our app's PUBLISHER DOMAIN, the step before publisher verification. It names our app and nothing else;
// 404 until `microsoft-app set` has run. Caddy's holding page never covers /.well-known.
app.MapGet("/.well-known/microsoft-identity-association.json", async (PlatformMailService mail) =>
{
    var s = await mail.GetAsync();
    return s.MsClientId.Length == 0 ? Results.NotFound()
        : Results.Json(new { associatedApplications = new[] { new { applicationId = s.MsClientId } } });
});

app.MapGet("/downloads/{slug}/client.zip", async (string slug) =>
{
    var t = await tenants.BySlugAsync(slug);
    if (t == null || !t.IsServiceEnabled || !payloads.ClientAvailable) return Results.NotFound();
    return Results.File(await payloads.BuildClientZipAsync(t), "application/zip", $"{t.Slug}-support-client.zip");
});
// Anybody signed in to THIS company who may have the console — technicians included since
// 2026-09-30, when /tech/{slug} became their download link. It used to be "tenant" only, so a
// technician could not fetch their own tool and it went round on a USB stick. The owner of the
// platform as before. One rule for the zip and the .exe.
bool MayDownloadConsole(HttpContext ctx, string slug) =>
    (ctx.User.FindFirstValue(PortalIdentity.SlugClaim) == slug && Permissions.CanDownloadConsole(PortalIdentity.RoleOf(ctx.User)))
    || ctx.User.IsInRole("owner");
app.MapGet("/downloads/{slug}/technician.zip", async (string slug, HttpContext ctx) =>
{
    if (!MayDownloadConsole(ctx, slug)) return Results.Unauthorized();
    var t = await tenants.BySlugAsync(slug);
    if (t == null || !payloads.TechnicianAvailable) return Results.NotFound();
    return Results.File(await payloads.BuildTechnicianZipAsync(t), "application/zip", $"{t.Slug}-technician-console.zip");
});
// The console as one .exe (2026-10-01): the SAME file for every company, as the customers' is, so it
// can be signed. Its name and this address are how it learns whose it is (DownloadIdentity).
app.MapGet("/downloads/{slug}/technician.exe", async (string slug, HttpContext ctx) =>
{
    if (!MayDownloadConsole(ctx, slug)) return Results.Unauthorized();
    var t = await tenants.BySlugAsync(slug);
    if (t == null || !payloads.TechnicianExeAvailable) return Results.NotFound();
    return Results.File(payloads.TechnicianExePath, "application/vnd.microsoft.portable-executable", DownloadIdentity.ConsoleFileNameFor(t.Slug));
});

// ---- auto-update (2026-10-16) ----
// The release's two files, the same bytes the downloads serve, for agents and consoles to fetch an update by. Public, as the
// downloads and /guest/console.exe already are: nothing in them is secret, and it is their SIGNATURE every machine trusts
// (UpdateTrust), never this address. Range requests, so a broken 130 MB download carries on where it stopped.
var updates = app.Services.GetRequiredService<UpdateService>();
app.MapGet(UpdateService.ClientPath, () => updates.Client != null
    ? Results.File(payloads.ClientExePath, "application/vnd.microsoft.portable-executable", "RemoteSupport.Client.exe", enableRangeProcessing: true)
    : Results.NotFound());
app.MapGet(UpdateService.ConsolePath, () => updates.Console != null
    ? Results.File(payloads.TechnicianExePath, "application/vnd.microsoft.portable-executable", "RemoteSupport.Technician.exe", enableRangeProcessing: true)
    : Results.NotFound());
// What an installed console asks at start, before anybody signs in: the console release its company may take now, or
// nothing (204). The company's ring decides, exactly as for its machines.
app.MapGet("/update/console.json", async (string? slug) =>
{
    var t = string.IsNullOrWhiteSpace(slug) ? null : await tenants.BySlugAsync(slug.Trim());
    if (t == null) return Results.NoContent();
    return await updates.ConsoleOfferAsync(UpdateService.RingOf(t)) is { } r ? Results.Json(r, Json.Options) : Results.NoContent();
});

// A console, tray app or agent that has gone quiet this long is gone (2026-10-09): switched off, asleep or
// off its network, none of which closes a socket. Each pings every 15 s, so 45 s is three missed in a row.
// See WsConnection.DropWhenSilent.
var silenceLimit = TimeSpan.FromSeconds(int.TryParse(app.Configuration["Platform:SilenceSeconds"], out var silS) && silS > 0 ? silS : 45);

// ---- technician control socket ----
app.Map("/ws/tech", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var who = auth.FromToken(ctx.Request.Query["token"]);
    if (who == null) { ctx.Response.StatusCode = 401; return; }
    var hub = await hubs.GetAsync(who.TenantId);
    if (hub == null) { ctx.Response.StatusCode = 403; return; }
    await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
    ws.DropWhenSilent(silenceLimit);
    await hub.RunTechnicianAsync(who, ws, ctx.RequestAborted);
});

// ---- device socket ----
app.Map("/ws/device", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
    ws.DropWhenSilent(silenceLimit);
    var helloTcs = new TaskCompletionSource<HelloMsg?>();
    Action<Msg> first = m => { if (m is HelloMsg h) helloTcs.TrySetResult(h); };
    ws.OnMessage += first;
    ws.OnClosed += _ => helloTcs.TrySetResult(null);
    var loop = ws.RunAsync(ctx.RequestAborted);
    var hello = await Task.WhenAny(helloTcs.Task, Task.Delay(10000, ctx.RequestAborted)) == helloTcs.Task ? helloTcs.Task.Result : null;
    ws.OnMessage -= first;
    // Every refusal below is LOGGED, and none of them used to be. A device turned away here is
    // otherwise invisible from both ends: the server says nothing, and the customer's tray app
    // just shows "not connected". When a tenant's trial lapses that happens to EVERY machine at
    // once, with no entry anywhere saying why — which is how an afternoon gets spent on the
    // firewall. The remote address is included because that is the only way to tell which machine
    // a refused connection came from when it never got as far as identifying itself.
    var from = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (hello == null || string.IsNullOrWhiteSpace(hello.DeviceId))
    {
        app.Logger.LogWarning("REFUSED a device from {From}: no usable hello within 10 s", from);
        await ws.CloseAsync("no hello"); return;
    }
    if (string.IsNullOrWhiteSpace(hello.TenantKey))
    {
        app.Logger.LogWarning("REFUSED {Machine} ({From}): its installer carries no tenant key", hello.MachineName, from);
        ws.Send(new ErrorMsg { Message = "This installer is not linked to a company. Please reinstall from your provider's download page." });
        await ws.CloseAsync("no tenant"); return;
    }
    var t = await tenants.ByKeyAsync(hello.TenantKey);
    if (t == null)
    {
        // Deliberately does not echo the key back. An unknown key is either a stale installer or
        // somebody guessing, and neither should be told which.
        app.Logger.LogWarning("REFUSED {Machine} ({From}): its tenant key matches no company on this server", hello.MachineName, from);
        ws.Send(new ErrorMsg { Message = "This installer is not linked to a company on this server. Please reinstall from your provider's download page." });
        await ws.CloseAsync("no tenant"); return;
    }
    if (!t.IsServiceEnabled)
    {
        // "Trial expired" and "suspended" are completely different problems with completely
        // different fixes, and lumping them together as "not active" sends the reader to the
        // wrong one. The date matters too: a trial that lapsed this morning explains a fleet of
        // machines that all went quiet this morning.
        var why = t.TrialExpired
            ? $"its 14-day trial ended on {t.TrialEndsUtc:d MMM yyyy}"
            : $"its account status is {t.Status}";
        app.Logger.LogWarning("REFUSED {Machine} ({From}) for tenant {Tenant}: {Why}. EVERY machine on this tenant is being turned away.",
            hello.MachineName, from, t.CompanyName, why);
        ws.Send(new ErrorMsg
        {
            Message = t.TrialExpired
                ? $"{t.CompanyName}'s support service trial has ended. Please contact them."
                : $"{t.CompanyName}'s support service is not active. Please contact them."
        });
        await ws.CloseAsync("bad tenant"); return;
    }
    // The address it came from (the real one behind Caddy: UseForwardedHeaders) is its site, for Wake-on-LAN.
    await (await hubs.GetAsync(t.Id))!.RunDeviceAsync(hello, ws, ctx.RequestAborted, loop, TenantHub.PlainAddress(ctx.Connection.RemoteIpAddress));
});

// ---- session relay ----
app.Map("/ws/session", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var id = ctx.Request.Query["id"].ToString(); var role = ctx.Request.Query["role"].ToString(); var token = ctx.Request.Query["token"].ToString();
    var hub = hubs.HubForSession(id);
    if (role == "host")
    {
        if (hub == null) { ctx.Response.StatusCode = 404; return; }
        bool asking = ctx.Request.Query["consent"].ToString() == "asking";
        int.TryParse(ctx.Request.Query["secs"].ToString(), out var askSecs);
        await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
        await hub.RunSessionHostAsync(id, token, ws, ctx.RequestAborted, asking, askSecs);
    }
    else
    {
        // A guest (3.9, step 3) has no account: their token is the one the guest socket gave them, and it
        // passes only the AdmitsVisitor check below, for the session their code started.
        var who = auth.FromToken(token) ?? hubs.GuestFromToken(token);
        // A session that has ENDED is no longer registered, and this used to answer 404 — which a
        // console's reconnect loop cannot tell from "not yet", so it went on dialling for two
        // minutes and then blamed the network (internal testing, 2026-10-02). The technician's own
        // company hub answers instead: "that session has ended", and why, if it still knows.
        hub ??= who != null ? await hubs.GetAsync(who.TenantId) : null;
        if (hub == null) { ctx.Response.StatusCode = 404; return; }
        // Another company's technician is let in to ONE session: the one a customer's code opened for
        // that very person (3.9, AdmitsVisitor). Everybody else from outside is refused, as ever.
        if (who == null || (who.TenantId != hub.TenantId && !hub.AdmitsVisitor(id, who))) { ctx.Response.StatusCode = 401; return; }
        await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
        await hub.RunSessionViewerAsync(id, who, ws, ctx.RequestAborted);
    }
});

// ---- support in the browser (3.8.2): the caller's page. The id and token came back only to the
// browser that posted the form on /help/{slug}; the session's host socket is /ws/session as for
// any host, with the token the hub hands this page when a technician answers.
app.Map("/ws/web", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (!int.TryParse(ctx.Request.Query["tenant"], out var tenantId)) { ctx.Response.StatusCode = 404; return; }
    var hub = await hubs.GetAsync(tenantId);
    if (hub == null) { ctx.Response.StatusCode = 404; return; }
    await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
    await hub.RunWebCallerAsync(ctx.Request.Query["id"].ToString(), ctx.Request.Query["token"].ToString(), ws, ctx.RequestAborted);
});

// The ding and the ring (2026-10-07) for sounds.js: the console's own, made by Chimes, so a browser
// and the console never sound different. Public: the website chat and browser support pages use them.
app.MapGet("/sounds/{name}.wav", (string name, HttpContext ctx) =>
{
    var wav = name switch { "ding" => Chimes.Ding(), "ring" => Chimes.Ring(), _ => null };
    if (wav == null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.Bytes(wav, "audio/wav");
});

// ---- website chat (3.8.3, 2026-09-30): the bubble on the company's own website ----
// The company pastes one line onto its site: <script src=".../webchat/{slug}.js" async></script>.
// That script draws the bubble and opens /webchat/{slug}/frame in an iframe, which is OURS — so
// everything the visitor does from then on is same-origin with this server, and only the state
// below ever has to answer another site (and it is public anyway: on, off, or leave a message).
async Task<Tenant?> ChatTenant(string slug)
{
    var t = await tenants.BySlugAsync(slug);
    return t != null && t.Status is not (TenantStatus.Pending or TenantStatus.Rejected or TenantStatus.Closed) ? t : null;
}
app.MapGet("/webchat/{slug}.js", async (string slug, HttpContext ctx) =>
{
    // Through the same file provider the static files use — not WebRootPath, which on a build is a
    // copy taken at build time and went on serving the old script after webchat.js had changed.
    var file = app.Environment.WebRootFileProvider.GetFileInfo("webchat-embed.js");
    if (!file.Exists) return Results.NotFound();
    string js;
    using (var r = new StreamReader(file.CreateReadStream())) js = await r.ReadToEndAsync();
    js = js.Replace("__SLUG__", System.Text.Json.JsonEncodedText.Encode(slug).ToString());
    ctx.Response.Headers.CacheControl = "public, max-age=300";
    return Results.Text(js, "text/javascript; charset=utf-8");
});
// The contact form's one line (3.8.4): the same file-provider read as the chat bubble's, for the same reason.
app.MapGet("/contactform/{slug}.js", async (string slug, HttpContext ctx) =>
{
    var file = app.Environment.WebRootFileProvider.GetFileInfo("contactform-embed.js");
    if (!file.Exists) return Results.NotFound();
    string js;
    using (var r = new StreamReader(file.CreateReadStream())) js = await r.ReadToEndAsync();
    js = js.Replace("__SLUG__", System.Text.Json.JsonEncodedText.Encode(slug).ToString());
    ctx.Response.Headers.CacheControl = "public, max-age=300";
    return Results.Text(js, "text/javascript; charset=utf-8");
});
app.MapGet("/api/webchat/{slug}", async (string slug, string? site, HttpContext ctx, WebChatService chat, IConfiguration cfg) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    var t = await ChatTenant(slug);
    var mode = t == null ? WebChatService.Mode.Off : chat.ModeFor(t, DateTime.UtcNow);
    // On our own site the bubble wears our name and colour; the chat is still that company's (SiteChat).
    var ours = t != null && SiteChat.Ours(cfg, slug, site);
    return Results.Ok(new
    {
        mode = mode.ToString().ToLowerInvariant(),
        company = ours ? SiteChat.Name : t?.CompanyName ?? "",
        accent = ours ? SiteChat.Fill : t?.AccentColor ?? "",
        hours = t == null ? new List<string>() : WebSupportHours.Describe(t.WebChatHours)
    });
});
// PayFast's notices (2026-10-13, roadmap 4.8): every payment, renewal and cancellation is posted here. Anybody can post
// at it; PayfastService believes a notice only after its checks (address, signature, merchant, PayFast's own "VALID").
// Read raw, because the signature is over the fields in the ORDER they were posted. 503 means "send it again".
app.MapPost("/api/payfast/notify", async (HttpContext ctx, PayfastService payfast) =>
{
    if (ctx.Request.ContentLength is > 65536) return Results.Text("too large", statusCode: 413);
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    if (body.Length > 65536) return Results.Text("too large", statusCode: 413);
    var r = await payfast.HandleNoticeAsync(body, ctx.Connection.RemoteIpAddress);
    return Results.Text(r.Outcome, statusCode: r.StatusCode);
});
// For search engines (2026-10-14, stage E): which pages to read, and — until go-live — to read none. SitePages is the list.
app.MapGet("/robots.txt", (IConfiguration cfg) => Results.Text(SitePages.Robots(cfg), "text/plain"));
app.MapGet("/sitemap.xml", (IConfiguration cfg) => Results.Text(SitePages.Sitemap(cfg), "application/xml"));
// Where to report a security fault (RFC 9116; the pentest's F10). Open before go-live too — it is not a sales page.
app.MapGet("/.well-known/security.txt", (IConfiguration cfg) => Results.Text(SitePages.SecurityTxt(cfg), "text/plain"));
// The return page's question (stage B): has PayFast's notice switched this company on yet? Only the state, by a reference
// of 80 random bits — nothing about the company.
app.MapGet("/api/payfast/checkout/{reference}", async (string reference, HttpContext ctx, CompanyBilling billing) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    var (state, _, _) = await billing.StateAsync(reference);
    return state == "unknown" ? Results.NotFound() : Results.Ok(new { state });
});
app.MapPost("/api/webchat/{slug}/start", async (string slug, HttpContext ctx, WebChatService chat) =>
{
    var t = await ChatTenant(slug);
    if (t == null) return Results.NotFound();
    var b = await ctx.Request.ReadFromJsonAsync<WebChatStartRequest>();
    if (b == null) return Results.BadRequest();
    // The field a person never sees: a robot is thanked and nothing happens.
    if (!string.IsNullOrEmpty(b.Website)) return Results.Ok(new { id = 0, token = "" });
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (!WebChatService.Allow(t.Id, ip))
        return Results.Json(new { error = "There have been several chats from here in the last hour. Please phone or email us instead." }, statusCode: 429);
    var (c, token, error) = await chat.StartAsync(t, b.Name, b.Email, b.Phone, b.Text, b.Page, ip);
    return c == null ? Results.Json(new { error, mode = chat.ModeFor(t, DateTime.UtcNow).ToString().ToLowerInvariant() }, statusCode: 400)
                     : Results.Ok(new { id = c.Id, token });
});
app.MapPost("/api/webchat/{slug}/leave", async (string slug, HttpContext ctx, WebChatService chat) =>
{
    var t = await ChatTenant(slug);
    if (t == null) return Results.NotFound();
    var b = await ctx.Request.ReadFromJsonAsync<WebChatStartRequest>();
    if (b == null) return Results.BadRequest();
    if (!string.IsNullOrEmpty(b.Website)) return Results.Ok(new { ok = true });
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (!WebChatService.Allow(t.Id, ip))
        return Results.Json(new { error = "There have been several messages from here in the last hour. Please phone or email us instead." }, statusCode: 429);
    var (_, error) = await chat.LeaveMessageAsync(t, b.Name, b.Email, b.Phone, b.Text, b.Page, ip);
    return error.Length > 0 ? Results.Json(new { error }, statusCode: 400) : Results.Ok(new { ok = true });
});
app.Map("/ws/webchat", (HttpContext ctx, WebChatService chat) => WebChatVisitor.RunAsync(ctx, chat));

// ---- outside help, step 3 (3.9): a GUEST with a code — nobody's account, the guest console ----
app.Map("/ws/guest", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await GuestVisitor.RunAsync(ctx, hubs, app.Logger);
});
// The guest console: the SAME .exe as every company's console, under a name and an address that say
// "guest" (DownloadIdentity.GuestFileName, /guest/console.exe) — so it opens on "Enter the code".
app.MapGet("/guest/console.exe", () => payloads.TechnicianExeAvailable
    ? Results.File(payloads.TechnicianExePath, "application/vnd.microsoft.portable-executable", DownloadIdentity.GuestFileName)
    : Results.NotFound("The guest console is not available on this server yet."));

// The office's half in the portal: the same service, as the signed-in person, writes with X-Chat.
static object WebChatJson(RemoteSupport.Shared.WebChatInfo c) => new
{
    id = c.Id, name = c.Name, email = c.Email, phone = c.Phone, page = c.Page, answeredBy = c.AnsweredBy,
    startedUtc = c.StartedUtc, ended = c.Ended, endedBy = c.EndedBy, visitorHere = c.VisitorHere, lastText = c.LastText,
    handedOverBy = c.HandedOverBy
};
static object WebLineJson(RemoteSupport.Shared.WebChatLineInfo l) => new { id = l.Id, chatId = l.ChatId, fromVisitor = l.FromVisitor, name = l.Name, text = l.Text, sentUtc = l.SentUtc, note = l.Note };
app.MapGet("/api/portal/webchats", async (HttpContext ctx, WebChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0) return Results.Unauthorized();
    var t = await tenants.ByIdAsync(tenantId);
    if (t == null) return Results.Unauthorized();
    return Results.Ok(new
    {
        me, enabled = t.WebChatEnabled, appLink = chat.AppLink(t), helpLink = chat.HelpLink(t),
        mayHandOverAny = Permissions.CanHandOverAnyWebChat(PortalIdentity.RoleOf(ctx.User)),
        // An owner-only login sees the chats and is never rung for them (2026-10-15): chat.js draws no reply box and no ring.
        mayAnswer = Permissions.CanAnswerWebChat(PortalIdentity.RoleOf(ctx.User)),
        chats =(await chat.OpenAsync(tenantId)).Select(WebChatJson)
    });
});
// A hand-over (2026-10-15): who could take a chat now, and giving it to one of them — every rule is WebChatService's.
app.MapGet("/api/portal/webchats/people", (HttpContext ctx, WebChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0) return Results.Unauthorized();
    return Results.Ok(new { me, names = chat.HereNow(tenantId) });
});
app.MapPost("/api/portal/webchats/{id:int}/handover", async (int id, HttpContext ctx, WebChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0 || !ChatWrite(ctx)) return Results.Unauthorized();
    if (!Permissions.CanAnswerWebChat(PortalIdentity.RoleOf(ctx.User))) return Results.Json(new { error = Permissions.OwnerOnlyRefusal }, statusCode: 403);
    var b = await ctx.Request.ReadFromJsonAsync<WebChatHandOverRequest>();
    var error = await chat.HandOverAsync(tenantId, id, me, PortalIdentity.RoleOf(ctx.User), b?.To);
    return error.Length > 0 ? Results.Json(new { error }, statusCode: 400) : Results.Ok();
});
app.MapGet("/api/portal/webchats/{id:int}", async (int id, HttpContext ctx, WebChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0) return Results.Unauthorized();
    return Results.Ok((await chat.LinesAsync(tenantId, id)).Select(WebLineJson));
});
app.MapPost("/api/portal/webchats/{id:int}/say", async (int id, HttpContext ctx, WebChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0 || !ChatWrite(ctx)) return Results.Unauthorized();
    if (!Permissions.CanAnswerWebChat(PortalIdentity.RoleOf(ctx.User))) return Results.Json(new { error = Permissions.OwnerOnlyRefusal }, statusCode: 403);
    var b = await ctx.Request.ReadFromJsonAsync<WebChatSayRequest>();
    var (line, error) = await chat.StaffSaysAsync(tenantId, id, me, b?.Text);
    return line == null ? Results.Json(new { error }, statusCode: 400) : Results.Ok(WebLineJson(line));
});
app.MapPost("/api/portal/webchats/{id:int}/end", async (int id, HttpContext ctx, WebChatService chat) =>
{
    var (tenantId, me, ok) = PortalUser(ctx);
    if (!ok || me.Length == 0 || !ChatWrite(ctx)) return Results.Unauthorized();
    if (!Permissions.CanAnswerWebChat(PortalIdentity.RoleOf(ctx.User))) return Results.Json(new { error = Permissions.OwnerOnlyRefusal }, statusCode: 403);
    var error = await chat.EndAsync(tenantId, id, me);
    return error.Length > 0 ? Results.Json(new { error }, statusCode: 400) : Results.Ok();
});

app.Logger.LogInformation("{Product} platform starting. DB: {Db}. Client payload: {C}, technician payload: {T}", productName, dbDescription, payloads.ClientAvailable, payloads.TechnicianAvailable);
if (string.IsNullOrEmpty(adminKey)) app.Logger.LogWarning("Platform:AdminKey is not set — the owner portal at /owner is disabled until you set it.");
else if (adminKey.Length < 32 || adminKey == "CHANGE-ME") app.Logger.LogWarning("Platform:AdminKey is short or a placeholder. Use a long random value — `openssl rand -hex 32` makes one.");
// Loud on purpose: both of these are fine for a harness on its own throwaway platform and wrong for
// anything a customer can reach.
if (!auth.RequireTwoFactor)
    app.Logger.LogWarning("Security:RequireTwoFactor is OFF — people without an authenticator sign in with a password alone. Never on a live server.");
if (RemoteSupport.Shared.SecureTransport.Refusal(app.Configuration["Platform:PublicUrl"]) != null)
    app.Logger.LogWarning("Platform:PublicUrl ({Url}) is plain http on a public address. Every app will REFUSE to connect to it; it must be https://.", app.Configuration["Platform:PublicUrl"]);
app.Run();
return 0;

// Trust (2026-10-02): what the console kept after its last code, so the code is asked only every TwoFactorTrustDays.
sealed record LoginRequest(string? Email, string? Username, string Password, string? Code = null, string? Trust = null, string? Device = null);
sealed record LoginStepRequest(string? Pending, string? Code);
sealed record AppearanceRequest(string? Theme);
sealed record ChatSendRequest(string? To, string? Text);
sealed record ChatReadRequest(string? With, long UpTo);
sealed record WebChatStartRequest(string? Name, string? Email, string? Phone, string? Text, string? Page, string? Website);
sealed record WebChatSayRequest(string? Text);
sealed record WebChatHandOverRequest(string? To);

// -------------------------------------------------------------------------------------
static class Cli
{
    public static async Task<int> RunAsync(string[] a, TenantService tenants, PlatformMailService mail, TrialNoticeService trials, AccountLinks links, TicketIntake intake, PlatformAdmins admins, PayfastService payfast, PilotService pilots, MicrosoftSignIn microsoft)
    {
        try
        {
            switch (a[0])
            {
                case "create-tenant" when a.Length >= 2:
                {
                    var (t, err) = await tenants.CreateAsync(a[1], a.Length > 2 ? a[2] : null, a.Length > 3 && int.TryParse(a[3], out var s) ? s : null);
                    if (t == null) { Console.WriteLine(err); return 1; }
                    Console.WriteLine($"Created tenant '{t.CompanyName}' (slug {t.Slug}, {t.Seats} seats)\nTenant key: {t.TenantKey}"); return 0;
                }
                // Platform mail and the trial notices, from the command line as well as the owner
                // portal. The Linux host will not have a browser on it, and a mailbox that can
                // only be configured through a web page is a mailbox that does not get configured.
                case "platform-mail" when a.Length >= 2 && a[1] == "show":
                {
                    var s = await mail.GetAsync();
                    Console.WriteLine($"host      : {(s.SmtpHost.Length == 0 ? "(not set)" : s.SmtpHost)}:{s.SmtpPort} ssl={s.SmtpUseSsl}");
                    Console.WriteLine($"user      : {s.SmtpUser}");
                    Console.WriteLine($"password  : {(string.IsNullOrEmpty(s.SmtpPasswordEnc) ? "(not set)" : "stored")}");
                    Console.WriteLine($"from      : {s.SmtpFrom} {(s.SmtpFromName.Length > 0 ? $"({s.SmtpFromName})" : "")}");
                    Console.WriteLine($"copy to   : {s.NoticeCopyTo}");
                    return 0;
                }
                case "platform-mail" when a.Length >= 3 && a[1] == "set":
                {
                    // The password comes from RSP_SMTP_PASSWORD, never from the command line: an
                    // argument ends up in shell history and in the process list.
                    string? Arg(string name)
                    {
                        var i = Array.IndexOf(a, "--" + name);
                        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
                    }
                    var pw = Environment.GetEnvironmentVariable("RSP_SMTP_PASSWORD");
                    await mail.SaveAsync(s =>
                    {
                        if (Arg("host") is { } h) s.SmtpHost = h;
                        if (Arg("port") is { } p && int.TryParse(p, out var pn)) s.SmtpPort = pn;
                        if (Arg("user") is { } u) s.SmtpUser = u;
                        if (Arg("from") is { } f) s.SmtpFrom = f;
                        if (Arg("from-name") is { } fn) s.SmtpFromName = fn;
                        if (Arg("copy-to") is { } c) s.NoticeCopyTo = c;
                        if (a.Contains("--nossl")) s.SmtpUseSsl = false;
                        if (a.Contains("--ssl")) s.SmtpUseSsl = true;
                        if (!string.IsNullOrEmpty(pw)) s.SmtpPasswordEnc = mail.Protect(pw);
                    });
                    Console.WriteLine("Saved." + (string.IsNullOrEmpty(pw) ? " (no RSP_SMTP_PASSWORD set, so the stored password was left alone)" : " Password updated."));
                    return 0;
                }
                case "platform-mail" when a.Length >= 3 && a[1] == "test":
                {
                    var r = await mail.SendAsync(new[] { a[2] }, "Test from The Remote Support Program",
                        "This is a test of the platform mailbox.\n\nIf you are reading it, trial notices will reach your customers.\n");
                    Console.WriteLine(r == "sent" ? $"Sent to {a[2]}." : "Could not send: " + r);
                    return r == "sent" ? 0 : 1;
                }
                // The forwarding mailbox for tickets by email (3.8.4): one mailbox on our domain that
                // companies forward their support address to. Its password, like the SMTP one, comes
                // from an environment variable — RSP_INTAKE_PASSWORD — never the command line.
                case "forwarding-mailbox" when a.Length >= 2 && a[1] == "show":
                {
                    var s = await mail.GetAsync();
                    Console.WriteLine($"address   : {(s.IntakeAddress.Length == 0 ? "(not set — forwarding is not offered)" : s.IntakeAddress)}");
                    Console.WriteLine($"server    : {(s.IntakeImapHost.Length == 0 ? "(not set)" : $"{s.IntakeImapHost}:{s.IntakeImapPort} {s.IntakeImapSecurity}")}");
                    Console.WriteLine($"user      : {s.IntakeImapUser}");
                    Console.WriteLine($"password  : {(string.IsNullOrEmpty(s.IntakeImapPasswordEnc) ? "(not set)" : "stored")}");
                    Console.WriteLine($"last look : {s.IntakeLastCheckUtc?.Local().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}{(s.IntakePaused ? " — PAUSED" : "")}");
                    if (s.IntakeLastProblem.Length > 0) Console.WriteLine($"problem   : {s.IntakeLastProblem}");
                    return 0;
                }
                case "forwarding-mailbox" when a.Length >= 3 && a[1] == "set":
                {
                    string? Arg(string name)
                    {
                        var i = Array.IndexOf(a, "--" + name);
                        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
                    }
                    var pw = Environment.GetEnvironmentVariable("RSP_INTAKE_PASSWORD");
                    await mail.SaveAsync(s =>
                    {
                        if (Arg("address") is { } ad) s.IntakeAddress = ad.Trim().ToLowerInvariant();
                        if (Arg("host") is { } h) s.IntakeImapHost = h.Trim();
                        if (Arg("port") is { } p && int.TryParse(p, out var pn)) s.IntakeImapPort = pn;
                        if (Arg("security") is { } sec && sec is "ssl" or "starttls" or "none") s.IntakeImapSecurity = sec;
                        if (Arg("user") is { } u) s.IntakeImapUser = u.Trim();
                        if (!string.IsNullOrEmpty(pw)) s.IntakeImapPasswordEnc = mail.Protect(pw);
                        s.IntakePaused = false; s.IntakeFailures = 0; s.IntakeNextTryUtc = null;
                    });
                    Console.WriteLine("Saved." + (string.IsNullOrEmpty(pw) ? " (no RSP_INTAKE_PASSWORD set, so the stored password was left alone)" : " Password updated."));
                    // Tried at once, and — the first time — noted where it is up to, so nothing
                    // forwarded between now and the platform's first look is passed over.
                    var (ok, words) = await intake.TestForwardingAsync();
                    Console.WriteLine((ok ? "Tested: " : "The test found a problem: ") + words);
                    return ok ? 0 : 1;
                }
                // The PayFast account (2026-10-13, roadmap 4.8). The key and passphrase come from RSP_PAYFAST_KEY and
                // RSP_PAYFAST_PASSPHRASE — never arguments, which end up in shell history and the process list.
                // deploy/Set-PayfastKeys.ps1 is how they get here from this PC.
                case "payfast" when a.Length >= 2 && a[1] == "show":
                {
                    var s = await payfast.SettingsAsync();
                    Console.WriteLine($"mode        : {(s.PayfastSandbox ? "SANDBOX (no money moves)" : "LIVE")}");
                    Console.WriteLine($"merchant id : {(s.PayfastMerchantId.Length == 0 ? "(not set)" : s.PayfastMerchantId)}");
                    Console.WriteLine($"key         : {(s.PayfastMerchantKeyEnc.Length == 0 ? "(not set)" : "stored")}");
                    Console.WriteLine($"passphrase  : {(s.PayfastPassphraseEnc.Length == 0 ? "(not set)" : "stored")}");
                    Console.WriteLine($"notices to  : {payfast.NotifyUrl}");
                    return 0;
                }
                case "payfast" when a.Length >= 2 && a[1] == "set":
                {
                    string? Arg(string name)
                    {
                        var i = Array.IndexOf(a, "--" + name);
                        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
                    }
                    bool sandbox = a.Contains("--sandbox"), live = a.Contains("--live");
                    if (sandbox == live) { Console.WriteLine("Say which account these are: --sandbox or --live."); return 1; }
                    var err = await payfast.SetAccountAsync(Arg("merchant-id") ?? "", Environment.GetEnvironmentVariable("RSP_PAYFAST_KEY"),
                        Environment.GetEnvironmentVariable("RSP_PAYFAST_PASSPHRASE"), sandbox);
                    if (err.Length > 0) { Console.WriteLine(err); return 1; }
                    Console.WriteLine($"Saved: the {(sandbox ? "SANDBOX" : "LIVE")} account.");
                    var ping = await payfast.PingAsync();
                    Console.WriteLine(ping.Ok ? $"PayFast's API accepts it ({ping.Body.Trim().Trim('"')})." : "PayFast's API did not accept it: " + ping.Error);
                    return ping.Ok ? 0 : 1;
                }
                case "payfast" when a.Length >= 2 && a[1] == "test":
                {
                    var ping = await payfast.PingAsync();
                    Console.WriteLine(ping.Ok ? $"PayFast's API accepts this server ({ping.Body.Trim().Trim('"')})." : "Refused: " + ping.Error);
                    return ping.Ok ? 0 : 1;
                }
                // Our Microsoft Entra app (2026-10-19, HANDOVER §151): Tickets by email's "Sign in with Microsoft". The secret
                // comes from RSP_MS_CLIENT_SECRET — never an argument. deploy/Set-MicrosoftApp.ps1 is how it gets here.
                case "microsoft-app" when a.Length >= 2 && a[1] == "show":
                {
                    var s = await mail.GetAsync();
                    Console.WriteLine($"client id    : {(s.MsClientId.Length == 0 ? "(not set — Sign in with Microsoft is not offered)" : s.MsClientId)}");
                    Console.WriteLine($"secret       : {(s.MsClientSecretEnc.Length == 0 ? "(not set)" : "stored")}");
                    if (s.MsSecretExpiresUtc is { } exp)
                    {
                        var days = (int)Math.Floor((exp - DateTime.UtcNow).TotalDays);
                        Console.WriteLine($"secret runs out: {exp:yyyy-MM-dd} ({(days < 0 ? "RUN OUT — every company's Microsoft sign-in has stopped" : $"in {days} days")})");
                    }
                    Console.WriteLine($"redirect URI : {microsoft.RedirectUri}   (must be on the app in Entra, exactly)");
                    Console.WriteLine($"scopes       : {MicrosoftSignIn.SignInScopes}");
                    return 0;
                }
                case "microsoft-app" when a.Length >= 2 && a[1] == "set":
                {
                    string? Arg(string name)
                    {
                        var i = Array.IndexOf(a, "--" + name);
                        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
                    }
                    DateTime? expires = null;
                    if (Arg("expires") is { Length: > 0 } e)
                    {
                        if (!DateTime.TryParseExact(e, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
                        { Console.WriteLine("--expires is the date the secret runs out, as yyyy-mm-dd."); return 1; }
                        expires = d;
                    }
                    var err = await microsoft.SetAppAsync(Arg("client-id") ?? "", Environment.GetEnvironmentVariable("RSP_MS_CLIENT_SECRET"), expires);
                    if (err.Length > 0) { Console.WriteLine(err); return 1; }
                    Console.WriteLine($"Saved: the Microsoft app. Sign in with Microsoft is offered on Tickets by email; Microsoft must send people back to {microsoft.RedirectUri}");
                    return 0;
                }
                case "trial-notices":
                    await trials.RunPassAsync();
                    Console.WriteLine("Trial notice pass finished. Anything it sent is in the log above.");
                    return 0;

                // The public page's terms, refunds and privacy (2026-10-06, for PayFast), written from the
                // SAME files signup shows (Terms/) between the LEGAL markers in landing/index.html — so
                // the two can never say different things. Run it after any change to Terms/.
                case "landing-legal" when a.Length >= 2:
                {
                    var path = a[1];
                    if (!File.Exists(path)) { Console.WriteLine($"Not found: {path}"); return 1; }
                    var page = File.ReadAllText(path);
                    const string begin = "<!-- LEGAL:BEGIN", end = "<!-- LEGAL:END -->";
                    int b = page.IndexOf(begin, StringComparison.Ordinal), e = page.IndexOf(end, StringComparison.Ordinal);
                    if (b < 0 || e < b) { Console.WriteLine($"{path} has no {begin} … {end} markers."); return 1; }
                    var written = page[..b] + LandingLegal.Block() + page[(e + end.Length)..];
                    File.WriteAllText(path, written, new System.Text.UTF8Encoding(false));
                    Console.WriteLine($"Wrote the terms (version {Terms.Version}) and the privacy policy into {path}.");
                    return 0;
                }
                case "list-tenants":
                    foreach (var t in await tenants.AllAsync()) Console.WriteLine($"{t.Id}\t{t.Slug}\t{t.CompanyName}\t{t.Country}\t{t.Seats} seats\t{t.Status}\tkey={t.TenantKey}");
                    return 0;
                case "approve" when a.Length >= 2:
                { var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; } var e = await tenants.ApproveAsync(t.Id); Console.WriteLine(e.Length == 0 ? "Approved — 14-day trial started." : e); return e.Length == 0 ? 0 : 1; }
                case "add-tech" when a.Length >= 4:
                {
                    var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; }
                    // Boxes combine: --technician --billing is somebody who does both.
                    var role = (a.Contains("--admin") ? TenantRole.Admin : 0) | (a.Contains("--billing") ? TenantRole.Billing : 0)
                             | (a.Contains("--owner") ? TenantRole.Owner : 0) | (a.Contains("--technician") ? TenantRole.Technician : 0);
                    if (role == TenantRole.None) role = TenantRole.Technician;
                    var (tech, err) = await tenants.AddTechnicianAsync(t.Id, a[2], a[3], a.Length > 4 && !a[4].StartsWith("--") ? a[4] : null, role);
                    Console.WriteLine(tech == null ? err : $"Added {tech.DisplayName} <{tech.Email}> to {t.CompanyName}."); return tech == null ? 1 : 0;
                }
                // A pilot link (2026-10-15): pilot-link "<company>" [email]. The same as Owner -> Pilots; printed once.
                case "pilot-link" when a.Length >= 2:
                {
                    var (token, invite, err) = await pilots.MakeAsync(new PlatformAdmins.Actor(null, "the platform (command line)", AdminLevel.Owner),
                        a[1], a.Length > 2 ? a[2] : null, "made on the command line");
                    if (invite == null) { Console.WriteLine(err); return 1; }
                    Console.WriteLine($"Pilot link for {invite.CompanyName} — it works once, until {invite.ExpiresUtc:yyyy-MM-dd} (UTC). It is not shown again:");
                    Console.WriteLine(pilots.LinkFor(token));
                    return 0;
                }
                // A person by invitation (2026-10-01): mailed a link to choose their own password and
                // set up two-factor. How each company's FIRST owner is added once signups are closed.
                case "invite" when a.Length >= 3:
                {
                    var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; }
                    var role = (a.Contains("--admin") ? TenantRole.Admin : 0) | (a.Contains("--billing") ? TenantRole.Billing : 0)
                             | (a.Contains("--owner") ? TenantRole.Owner : 0) | (a.Contains("--technician") ? TenantRole.Technician : 0);
                    if (role == TenantRole.None) role = TenantRole.Technician;
                    var (tech, err) = await tenants.AddTechnicianAsync(t.Id, a[2], null, a.Length > 3 && !a[3].StartsWith("--") ? a[3] : null, role);
                    if (tech == null) { Console.WriteLine(err); return 1; }
                    var sent = await links.IssueAsync(tech.Id, AccountTokenPurpose.Invite, "command line");
                    Console.WriteLine(sent.Mailed
                        ? $"Added {tech.DisplayName} <{tech.Email}> to {t.CompanyName} as {tech.RoleLabel}; the invitation is on its way."
                        : $"Added {tech.DisplayName} <{tech.Email}> to {t.CompanyName}, but the invitation could not be mailed ({sent.Why}).\nSend them this link yourself — it works once, for 72 hours:\n{sent.Link}");
                    if (!sent.Link.StartsWith("https://")) Console.WriteLine($"WARNING: the link is not https ({links.PublicUrl}). Set Platform:PublicUrl.");
                    return 0;
                }
                case "list-techs" when a.Length >= 2:
                {
                    var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; }
                    var list = await tenants.TechniciansAsync(t.Id);
                    foreach (var x in list) Console.WriteLine($"{x.Email}\t{x.DisplayName}\t{x.RoleLabel}{(x.UsesSeat ? "" : "\t(no seat)")}{(x.Disabled ? "\t(disabled)" : "")}");
                    var bill = Pricing.BillFor(t, Pricing.PeopleInSeats(list));
                    Console.WriteLine($"{bill.People} people in seats, {bill.Included} in the package{(list.Any(x => !x.UsesSeat) ? " (the owner alone is free)" : "")} — {(t.NotBilled ? "not billed" : bill.Sum)}");
                    return 0;
                }
                case "remove-tech" when a.Length >= 3:
                { var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; } var e = await tenants.RemoveTechnicianAsync(t.Id, a[2]); Console.WriteLine(e.Length == 0 ? "Removed." : e); return e.Length == 0 ? 0 : 1; }
                case "set-owner" when a.Length >= 3:
                { var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; } var e = await tenants.SetOwnerAsync(t.Id, a[2]); Console.WriteLine(e.Length == 0 ? "Owner set. The previous owner is now a supervisor." : e); return e.Length == 0 ? 0 : 1; }
                case "set-password" when a.Length >= 4:
                { var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; } var e = await tenants.SetPasswordAsync(t.Id, a[2], a[3]); Console.WriteLine(e.Length == 0 ? "Password updated. They are signed out everywhere." : e); return e.Length == 0 ? 0 : 1; }
                // Signing in safely (2026-10-01). The running server finds these within 15 s.
                case "reset-2fa" when a.Length >= 3:
                { var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; } var e = await tenants.ResetTwoFactorAsync(t.Id, a[2]); Console.WriteLine(e.Length == 0 ? "Two-factor reset. They are signed out everywhere, and set up an authenticator at their next sign-in." : e); return e.Length == 0 ? 0 : 1; }
                case "signout" when a.Length >= 3:
                { var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; } var e = await tenants.SignOutEverywhereAsync(t.Id, a[2]); Console.WriteLine(e.Length == 0 ? "Signed out everywhere." : e); return e.Length == 0 ? 0 : 1; }
                // Platform admins (2026-10-13): the first Owner is made here or by the admin key on Owner → Admins.
                case "add-admin" when a.Length >= 4:
                {
                    if (!Enum.TryParse<AdminLevel>(a[3], true, out var level) || !Enum.IsDefined(level)) { Console.WriteLine("Level is viewer, manager, full or owner."); return 1; }
                    var r = await admins.AddAsync(new PlatformAdmins.Actor(null, "command line", AdminLevel.Owner), a[2], a[1], level);
                    if (r.Error.Length > 0) { Console.WriteLine(r.Error); return 1; }
                    Console.WriteLine(r.Mailed ? $"Added. A set-up link has been emailed to {a[1]}." : $"Added, but the email did not go ({r.Why}). Give them this link, once:\n  {r.Link}");
                    return 0;
                }
                case "list-admins":
                    foreach (var x in await admins.AllAsync())
                        Console.WriteLine($"{x.Email,-34} {PlatformAdmins.Label(x.Level),-8} {x.Name}{(x.IsSetUp ? "" : "  (not set up yet)")}");
                    return 0;
                case "remove-admin" when a.Length >= 2:
                { var e = await admins.RemoveByEmailAsync(a[1]); Console.WriteLine(e.Length == 0 ? "Removed. They are signed out within 15 seconds." : e); return e.Length == 0 ? 0 : 1; }
                case "reset-owner-2fa":
                    await mail.SaveAsync(s => { s.OwnerTotpSecretEnc = ""; s.OwnerTotpLastStep = 0; });
                    Console.WriteLine("The owner's authenticator is cleared. The next sign-in with the admin key sets a new one up, and every owner browser is signed out.");
                    return 0;
                case "set-brand" when a.Length >= 4:
                {
                    var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; }
                    var field = a[2].ToLowerInvariant(); var value = a[3];
                    var e = await tenants.UpdateBrandingAsync(t.Id, x =>
                    {
                        switch (field)
                        {
                            case "company": x.CompanyName = value; break;
                            case "product": x.ProductName = value; break;
                            case "primary": x.PrimaryColor = value; break;
                            case "accent": x.AccentColor = value; break;
                            // only ever "light" or "dark" reaches the database, whatever was typed
                            case "tech-theme": x.TechnicianTheme = value.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light"; break;
                            case "client-theme": x.ClientTheme = value.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light"; break;
                            case "welcome": x.WelcomeText = value; break;
                            case "email": x.SupportEmail = value; break;
                            case "phone": x.SupportPhone = value; break;
                            case "country": x.Country = value; x.Region = Pricing.RegionOfCountry(value); break;
                            case "region": x.Region = Pricing.For(value).Code; break;
                            case "not-billed": x.NotBilled = bool.Parse(value); break;
                            // A company that signed up the ordinary way and was then agreed a pilot (2026-10-15).
                            case "pilot": x.Pilot = bool.Parse(value); break;
                            case "time-zone": x.TimeZone = CompanyClock.Offered(value) ? value : throw new ArgumentException("Not a time zone we offer, e.g. Africa/Johannesburg or America/New_York."); break;
                            case "seats": x.Seats = int.Parse(value); break;
                            // More seats, on request (2026-10-25): the most this company may have; 0 = its package.
                            case "most-seats": x.MostSeats = Math.Clamp(int.Parse(value), 0, Pricing.MostAllowed); break;
                            // The Basic version's computers paid for (2026-10-25); 0 = the three in its price. Not priced or agreed here.
                            case "basic-machines": x.BasicMachines = Math.Clamp(int.Parse(value), 0, RemoteSupport.Shared.FreePlan.MostMachines); break;
                            case "status": x.Status = Enum.Parse<TenantStatus>(value, true); break;
                            // The Free version (2026-10-06), the Basic version (§163): "free", "basic", or "full" (stored as the old "five-user").
                            case "plan": x.Plan = FreePlan.IsFree(value.Trim()) ? FreePlan.Name : FreePlan.IsBasic(value.Trim()) ? FreePlan.BasicName : "five-user"; break;
                            default: throw new ArgumentException("Unknown field. Use: company|product|primary|accent|tech-theme|client-theme|welcome|email|phone|country|region|not-billed|time-zone|seats|most-seats|basic-machines|status|plan");
                        }
                    });
                    Console.WriteLine(e.Length == 0 ? "Updated." : e); return e.Length == 0 ? 0 : 1;
                }
                case "set-logo" when a.Length >= 3:
                {
                    var t = await tenants.BySlugAsync(a[1]); if (t == null) { Console.WriteLine("Tenant not found."); return 1; }
                    await using var fs = File.OpenRead(a[2]);
                    var e = await tenants.SaveLogoAsync(t.Id, fs, a[2]);
                    Console.WriteLine(e.Length == 0 ? "Logo saved." : e); return e.Length == 0 ? 0 : 1;
                }
                default:
                    Console.WriteLine("""
                        The Remote Support Program — platform CLI
                          run
                          create-tenant "<Company Name>" [slug] [seats]
                          approve <slug>
                          list-tenants
                          invite <slug> <email> ["Display Name"] [--technician] [--admin] [--billing] [--owner]  mailed a link to set up (roles combine)
                          add-tech <slug> <email> <password> ["Display Name"] [--technician] [--admin] [--billing] [--owner]  (roles combine)
                                   --admin is a supervisor, --billing an office administrator, --owner an owner with no seat
                          set-owner <slug> <email>   hand the company to this person; the old owner becomes a supervisor
                          list-techs <slug> | remove-tech <slug> <email> | set-password <slug> <email> <newpassword>
                          reset-2fa <slug> <email>   lost phone: they set up an authenticator again at their next sign-in
                          signout <slug> <email>     sign them out of every console, session and browser
                          reset-owner-2fa            clear the platform owner's authenticator (the next owner sign-in sets one up)
                          add-admin <email> "<Name>" <viewer|manager|full|owner>   a platform admin, mailed a set-up link
                          list-admins | remove-admin <email>   (the command line may remove an Owner; the pages never can)
                          set-brand <slug> <company|product|primary|accent|tech-theme|client-theme|welcome|email|phone|country|seats|most-seats|basic-machines|status|plan> <value>
                                   tech-theme and client-theme are light or dark
                          platform-mail show
                          platform-mail set [--host h] [--port n] [--user u] [--from a] [--from-name n] [--copy-to a] [--ssl|--nossl]
                                           password comes from the RSP_SMTP_PASSWORD environment variable, never an argument
                          platform-mail test <address>
                          forwarding-mailbox show
                          forwarding-mailbox set [--address a] [--host h] [--port n] [--security ssl|starttls] [--user u]   (password from RSP_INTAKE_PASSWORD)
                          trial-notices      run the trial warning pass now
                          payfast show | test
                          payfast set --merchant-id N --sandbox|--live   (key from RSP_PAYFAST_KEY, passphrase from RSP_PAYFAST_PASSPHRASE)
                          microsoft-app show
                          microsoft-app set --client-id GUID [--expires yyyy-mm-dd]   (secret from RSP_MS_CLIENT_SECRET; deploy\Set-MicrosoftApp.ps1)
                          set-logo <slug> <path-to-png-or-jpg>
                        """);
                    return 1;
            }
        }
        catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); return 1; }
    }
}
