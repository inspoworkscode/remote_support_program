using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

namespace RemoteSupport.Shared;

/// <summary>
/// Setting up a FREE computer (2026-10-06, docs/NEXT-SESSION-FREE.md Stage D). Clint's answers: the
/// unattended password is chosen when the computer is installed, unattended access is switched on then,
/// and nothing of ours sits on its screen afterwards — no tray app. Changing the password is a reinstall.
///
/// <para>Three steps, all from the downloaded copy running AS THE PERSON at the machine (never elevated):
/// <see cref="MayAddAsync"/> before installing anything, so a third computer is told why and nothing is
/// left behind; then the installer puts the agent service on; then <see cref="SetPasswordAsync"/> connects
/// once as this machine's CLIENT and sets the permission and the password. It has to be the client: only a
/// client connection may change either (§31, §77) — the agent is what they guard, and must never be able
/// to unlock itself. Without a tray app, this one connection is the client.</para>
/// </summary>
public static class FreeSetup
{
    /// <summary>Would this computer be let on? Null for yes, else the server's words.</summary>
    public static async Task<string?> MayAddAsync(string origin, string slug, HttpClient? http = null)
    {
        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var o = DownloadIdentity.HttpOrigin(origin);
            if (SecureTransport.Refusal(o) is { } insecure) return insecure;
            var hw = Uri.EscapeDataString(MachineFingerprint.Get());
            var answer = await http.GetFromJsonAsync<JsonElement>($"{o}/download/{slug}/may-add?hw={hw}");
            return answer.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? null
                : answer.TryGetProperty("message", out var m) ? m.GetString() : "The server would not add this computer.";
        }
        catch (Exception ex) { return $"Could not ask the server whether this computer can be added. Check the internet connection and try again. ({ex.Message})"; }
        finally { if (own) http.Dispose(); }
    }

    /// <summary>
    /// The machine's identity, once the agent service has written it (it runs as SYSTEM and writes
    /// ProgramData\RemoteSupport\device.json when it starts). Read, never created: a second identity made
    /// here would be a second machine to the server.
    /// </summary>
    public static async Task<DeviceIdentity?> WaitForIdentityAsync(TimeSpan within, string? dir = null)
    {
        var path = Path.Combine(dir ?? DeviceIdentity.MachineDir, "device.json");
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<DeviceIdentity>(await File.ReadAllTextAsync(path), Json.Options) is { DeviceId.Length: > 0 } id)
                    return id;
            }
            catch { }
            await Task.Delay(500);
        }
        return null;
    }

    /// <summary>
    /// The Basic version with the password left blank (2026-10-25, HANDOVER §163): connects once as this machine's client and
    /// switches unattended access OFF, so every connection asks the person here — a reinstall of a computer that was unattended
    /// stops being so. Empty for done; otherwise words to show. The hub answers every switch with the machine's state once its
    /// stored record is read; an earlier "on" is the old one, so only an "off" after ours counts.
    /// </summary>
    public static async Task<string> SetAttendedOnlyAsync(string serverWs, string tenantKey, DeviceIdentity id, TimeSpan within, string? hardwareId = null)
    {
        var url = serverWs.TrimEnd('/');
        if (SecureTransport.Refusal(url) is { } insecure) return insecure;
        var got = new List<Msg>();
        var arrived = new SemaphoreSlim(0);
        using var cts = new CancellationTokenSource(within);
        var cws = new ClientWebSocket();
        try { await cws.ConnectAsync(new Uri($"{url}/ws/device"), cts.Token); }
        catch (Exception ex) { return $"Could not reach the server to finish setting up. ({ex.Message})"; }
        var ws = new WsConnection(cws);
        ws.OnMessage += m => { lock (got) got.Add(m); arrived.Release(); };
        var run = ws.RunAsync(cts.Token);
        try
        {
            ws.Send(new HelloMsg
            {
                Role = DeviceRole.Client, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey,
                MachineName = Environment.MachineName, UserName = Environment.UserName,
                Os = Environment.OSVersion.VersionString, Version = "",   // the installer, not a tray app (SetPasswordAsync says why)
                TenantKey = tenantKey, HardwareId = hardwareId ?? MachineFingerprint.Get()
            });
            bool sent = false;
            while (!cts.IsCancellationRequested)
            {
                try { await arrived.WaitAsync(cts.Token); } catch (OperationCanceledException) { break; }
                Msg? m; lock (got) { m = got.Count > 0 ? got[0] : null; if (m != null) got.RemoveAt(0); }
                switch (m)
                {
                    case ErrorMsg e:
                        return e.Message;
                    case UnattendedStateMsg when !sent:
                        ws.Send(new SetUnattendedMsg { On = false });
                        sent = true;
                        break;
                    case UnattendedStateMsg s when !s.On:
                        return "";
                }
            }
            return "The server did not answer in time. Check the internet connection and run the installer again.";
        }
        finally
        {
            try { await ws.CloseAsync("set up"); } catch { }
            try { await run; } catch { }
        }
    }

    /// <summary>
    /// Connects once as this machine's client, switches unattended access on and sets the password.
    /// Empty for done; otherwise words to show. Waits for the SERVER to say so — a password it refused
    /// (<see cref="UnattendedPasswords.Problem"/>) or a computer it will not let on is reported, never
    /// assumed away.
    /// </summary>
    public static async Task<string> SetPasswordAsync(string serverWs, string tenantKey, DeviceIdentity id, string password, TimeSpan within,
        string? hardwareId = null, Action<bool>? awaitingConfirm = null)
    {
        if (UnattendedPasswords.Problem(password) is { Length: > 0 } weak) return weak;
        var url = serverWs.TrimEnd('/');
        if (SecureTransport.Refusal(url) is { } insecure) return insecure;

        var got = new List<Msg>();
        var arrived = new SemaphoreSlim(0);
        using var cts = new CancellationTokenSource(within);
        var cws = new ClientWebSocket();
        try { await cws.ConnectAsync(new Uri($"{url}/ws/device"), cts.Token); }
        catch (Exception ex) { return $"Could not reach the server to set the password. ({ex.Message})"; }
        var ws = new WsConnection(cws);
        ws.OnMessage += m => { lock (got) got.Add(m); arrived.Release(); };
        var run = ws.RunAsync(cts.Token);
        try
        {
            ws.Send(new HelloMsg
            {
                Role = DeviceRole.Client, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey,
                MachineName = Environment.MachineName, UserName = Environment.UserName,
                Os = Environment.OSVersion.VersionString,
                // Blank, on purpose (2026-10-16): this one-off hello is the free INSTALLER, not a tray app — a free machine
                // has none. The hub keeps a Client hello's version as the tray app's, and a machine's version is the
                // older of its two halves, so the installer's would sit there for ever once the agent had been updated.
                Version = "",
                TenantKey = tenantKey, HardwareId = hardwareId ?? MachineFingerprint.Get()   // given only by tools/freeinstall
            });

            // The server says where the machine stands as soon as it knows it (twice as it connects: once
            // from memory, once from the stored record); a refusal comes instead. Only the state carrying
            // OUR request id answers the password — an earlier "has a password" may be the old one.
            var requestId = Guid.NewGuid().ToString("N");
            bool sentOn = false, sentPassword = false;
            while (!cts.IsCancellationRequested)
            {
                try { await arrived.WaitAsync(cts.Token); } catch (OperationCanceledException) { break; }
                Msg? m; lock (got) { m = got.Count > 0 ? got[0] : null; if (m != null) got.RemoveAt(0); }
                switch (m)
                {
                    case ErrorMsg e:
                        return e.Message;
                    case UnattendedStateMsg s when s.RequestId == requestId:
                        if (s.PasswordProblem.Length > 0) return s.PasswordProblem;
                        // A reinstall the IT company has not confirmed yet (2026-10-07): done, but not reachable until they do.
                        awaitingConfirm?.Invoke(s.AwaitingConfirm);
                        return s.HasPassword ? "" : "The server did not keep the password. Run the installer again.";
                    case UnattendedStateMsg s when !sentPassword:
                        if (!s.On && !sentOn) { ws.Send(new SetUnattendedMsg { On = true }); sentOn = true; break; }
                        if (s.On) { ws.Send(new SetUnattendedPasswordMsg { Password = password, RequestId = requestId }); sentPassword = true; }
                        break;
                }
            }
            return "The server did not answer in time. Check the internet connection and run the installer again.";
        }
        finally
        {
            try { await ws.CloseAsync("set up"); } catch { }
            try { await run; } catch { }
        }
    }
}
