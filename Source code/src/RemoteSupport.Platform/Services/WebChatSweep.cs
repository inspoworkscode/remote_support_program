namespace RemoteSupport.Platform.Services;

/// <summary>
/// Ends website chats whose visitor has gone and that have been quiet for
/// <see cref="WebChatService.IdleEnd"/>, once a minute — so the office's list does not fill with
/// people who closed the tab an hour ago. The TrialNoticeService pattern: it must never take the
/// server down, so every pass is wrapped.
/// </summary>
public sealed class WebChatSweep : BackgroundService
{
    private readonly WebChatService _chat;
    private readonly ILogger<WebChatSweep> _log;
    public WebChatSweep(WebChatService chat, ILogger<WebChatSweep> log) { _chat = chat; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await _chat.SweepAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "website chat sweep"); }
        }
    }
}
