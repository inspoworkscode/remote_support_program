using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// The website visitor's live connection (/ws/webchat?id=…&amp;token=…), from the chat bubble's frame
/// (wwwroot/webchat.js). Plain JSON, not the console protocol: a visitor's page must never be able to
/// speak a message the hub understands. It hears only its OWN chat, and whatever it sends is said as
/// the name it gave when it started — see <see cref="WebChatService.VisitorSaysAsync"/>.
///
/// Server → page: {type:"hello", chat, lines}, {type:"line", line}, {type:"state", chat}, {type:"error", error}.
/// Page → server: {type:"say", text}, {type:"end"}.
/// </summary>
public static class WebChatVisitor
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(HttpContext ctx, WebChatService chats)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        int.TryParse(ctx.Request.Query["id"], out var id);
        var chat = await chats.AuthAsync(id, ctx.Request.Query["token"]);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var gate = new SemaphoreSlim(1, 1);
        async Task Send(object o)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(o, J);
            await gate.WaitAsync();
            try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { }
            finally { gate.Release(); }
        }

        if (chat == null)
        {
            // A wrong or forgotten secret: the page starts afresh rather than showing somebody else's chat.
            await Send(new { type = "gone" });
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "unknown", CancellationToken.None); } catch { }
            return;
        }
        var tenantId = chat.TenantId;
        object Visible(WebChatInfo c) => new { answeredBy = c.AnsweredBy, ended = c.Ended, endedBy = c.Ended ? (c.EndedBy == "visitor" ? "you" : c.EndedBy == "idle" ? "" : "us") : "" };
        object LineJson(WebChatLineInfo l) => new { id = l.Id, fromVisitor = l.FromVisitor, name = l.Name, text = l.Text, sentUtc = l.SentUtc, note = l.Note };

        Action<int, WebChatLineInfo> onLine = (t, l) => { if (t == tenantId && l.ChatId == id) _ = Send(new { type = "line", line = LineJson(l) }); };
        Action<int, WebChatInfo> onChanged = (t, c) => { if (t == tenantId && c.Id == id) _ = Send(new { type = "state", chat = Visible(c) }); };
        chats.Line += onLine; chats.Changed += onChanged;
        bool counted = false;
        try
        {
            await Send(new { type = "hello", chat = Visible(chats.Info(chat)), lines = (await chats.LinesAsync(tenantId, id)).Select(LineJson) });
            // Present only after hello: its own "state" must never arrive before the chat it belongs to.
            await chats.VisitorHereAsync(tenantId, id, +1); counted = true;
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > 32 * 1024) return;   // nothing a visitor says is this long
                } while (!r.EndOfMessage);

                string type = "", text = "";
                try
                {
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    type = doc.RootElement.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                    text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                }
                catch { continue; }

                if (type == "say")
                {
                    var (_, error) = await chats.VisitorSaysAsync(chat, text);
                    if (error.Length > 0) await Send(new { type = "error", error });
                }
                else if (type == "end") await chats.EndAsync(tenantId, id, "visitor");
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            chats.Line -= onLine; chats.Changed -= onChanged;
            if (counted) await chats.VisitorHereAsync(tenantId, id, -1);
        }
    }
}
