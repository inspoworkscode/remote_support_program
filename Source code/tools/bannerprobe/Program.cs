// Renders the customer-side overlays and saves a picture of each, so the look can be checked
// without staging a build to a customer machine and running a live session.
//
// Draws each window to a bitmap rather than grabbing the screen: a screen grab captures whatever
// is behind the window (i.e. somebody's desktop) and needs a running message loop to be accurate.
using System.Drawing.Imaging;
using RemoteSupport.Client;
using RemoteSupport.Shared;
using RemoteSupport.UI;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var outDir = args.Length > 0 ? args[0] : Path.GetTempPath();
Directory.CreateDirectory(outDir);

var theme = Theme.FromBranding(new Branding
{
    CompanyName = "Anywhere IT",
    ProductName = "Anywhere IT Support",
    PrimaryColor = "#8B4499",
    AccentColor = "#00B0F0"
}, AppSurface.Customer);

void Snap(Form f, string name)
{
    Application.DoEvents();
    using var bmp = new Bitmap(f.Width, f.Height);
    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
    bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
    Console.WriteLine($"{name}  {f.Width}x{f.Height}");
}

var banner = new SessionBanner(theme);
banner.ShowInProgress("Anywhere IT Support", "Clint");
Snap(banner, "banner-in-progress.png");
// Talking (3.3): the only state in which the banner ASKS something, so it has buttons.
banner.SetVoice("Clint is talking to you. Would you like them to hear you too?", "Use my microphone", "Not now");
Snap(banner, "banner-voice-asking.png");
banner.SetVoice("Your microphone is on — Clint can hear you.", null, "Turn my microphone off");
Snap(banner, "banner-voice-on.png");
banner.ShowHeld("Anywhere IT Support", "Clint");
banner.SetVoice("Clint is talking to you. Would you like them to hear you too?", "Use my microphone", "Not now");
Snap(banner, "banner-voice-held.png");
banner.ShowEnded("Anywhere IT Support");
Snap(banner, "banner-ended.png");
banner.HideNow();
banner.Dispose();

var chat = new ChatPopup(theme, "Anywhere IT");
chat.Incoming(new ChatMsg { From = "Clint", Text = "Morning — I can see the printer queue is stuck. Mind if I clear it?", FromTech = true, Ts = DateTimeOffset.Now.AddMinutes(-2) });
chat.Outgoing("Yes please, go ahead.");
chat.Incoming(new ChatMsg { From = "Clint", Text = "Done. Try printing again and tell me if it comes out.", FromTech = true, Ts = DateTimeOffset.Now });
chat.System_("Remote support session started.");
Snap(chat, "chat-popup.png");
chat.HideNow();
chat.Dispose();
