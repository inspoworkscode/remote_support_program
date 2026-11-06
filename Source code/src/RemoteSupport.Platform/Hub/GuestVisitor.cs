using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// A GUEST on /ws/guest (roadmap 3.9, step 3, 2026-10-06): somebody with no account here — Clint's
/// example, a customer's accounting firm — using the guest console, which belongs to no company. They
/// type the customer's code and their name, and the visit is the same as another company's (§89):
/// the machine's hub starts it, the person is always asked, the allow-lists apply, the company is told
/// and can watch and end it, and when the guest goes, it goes.
///
/// <para>What is different is that nothing about a guest can be checked. So: who they are is what they
/// TYPED and is described everywhere as "a guest — not checked"; guessing is limited by their internet
/// address; and their only credential is a token for that one session's viewer socket, which dies with
/// this connection. A guest belongs to no company (tenant 0), so no company keeps their record but the
/// machine's.</para>
/// </summary>
public static class GuestVisitor
{
    private static int _nextGuest;

    public static async Task RunAsync(HttpContext ctx, HubRegistry hubs, ILogger log)
    {
        await using var ws = new WsConnection(await ctx.WebSockets.AcceptWebSocketAsync());
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string? token = null;
        var busy = new SemaphoreSlim(1, 1);

        async Task ConnectAsync(GuestConnectMsg g)
        {
            // One visit per guest connection at a time; a second Connect waits for the first answer.
            await busy.WaitAsync();
            try
            {
                var problem = GuestRules.Problem(g.Name, g.Company);
                if (problem.Length > 0) { ws.Send(new ErrorMsg { Message = problem }); return; }
                if (hubs.Outsiders is not { } codes) { ws.Send(new ErrorMsg { Message = "Codes cannot be used on this server." }); return; }
                var name = g.Name.Trim();
                var company = GuestRules.CompanyLabel(g.Company);
                int guestId = -Interlocked.Increment(ref _nextGuest);   // never a real technician's id
                var visitor = new TenantHub.Visitor(0, company, guestId, name, m => ws.Send(m));

                TenantHub? home = null;
                var (hit, error) = await codes.RedeemAsync(g.Code, "guest:" + ip, visitor.Label, found =>
                {
                    home = hubs.Get(found.TenantId);
                    // The customer's code (§89) or a technician's for their session (§155): the machine's hub knows which.
                    return Task.FromResult(home == null ? "That code does not work. Ask for a new one." : home.CheckCode(found));
                });
                if (hit == null || home == null) { ws.Send(new ErrorMsg { Message = error }); return; }

                // A token for THIS guest, before the visit is announced: the session window opens with it
                // the moment the person allows it. A previous visit's token goes first.
                if (token != null) hubs.ForgetGuest(token);
                token = Passwords.RandomToken();
                hubs.AddGuest(token, new TechSession(0, guestId, name, "", false, TenantRole.None));
                ws.Send(new GuestTicketMsg { Token = token });

                var why = await home.ArriveWithCodeAsync(visitor, hit);
                if (why.Length > 0) { ws.Send(new ErrorMsg { Message = why }); hubs.ForgetGuest(token); token = null; return; }
                log.LogInformation("guest {Name} ({Ip}) connected with a code to a machine of {Home}", name, ip, home.TenantName);
            }
            catch (Exception ex) { log.LogWarning(ex, "guest connect"); ws.Send(new ErrorMsg { Message = "Something went wrong. Try again." }); }
            finally { busy.Release(); }
        }

        // Nothing else a guest says is listened to here: their session runs on its own socket.
        ws.OnMessage += m => { if (m is GuestConnectMsg g) _ = ConnectAsync(g); };
        try { await ws.RunAsync(ctx.RequestAborted); }
        finally { if (token != null) hubs.ForgetGuest(token); }
    }
}
