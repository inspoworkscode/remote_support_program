using System.Diagnostics;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Turns a stored HTML report into a PDF, server-side.
///
/// This exists because the endpoint cannot do it. The original plan had the machine build its own
/// PDF, on the reasoning that every Windows machine has Edge and a Linux server does not. That was
/// half right: the machine has Edge, but the monthly run is a scheduled task running as SYSTEM, and
/// Edge refuses to start under that account - measured on DESKTOP-OPHQIDH, exit code 1002, silently
/// and identically for --headless, --headless=new and --headless=old. So the advantage the endpoint
/// was supposed to have does not exist in the one context that matters.
///
/// Solving it here means solving it once rather than depending on every customer machine's browser.
/// Conversion is best-effort: when it cannot be done the HTML is kept and sent, because a report
/// that arrives in a slightly plainer form beats no report at all.
/// </summary>
public sealed class ReportPdfService
{
    private readonly ILogger<ReportPdfService> _log;
    private readonly string? _configured;

    public ReportPdfService(IConfiguration cfg, ILogger<ReportPdfService> log)
    {
        _log = log;
        _configured = cfg["Platform:PdfEngine"];
    }

    /// <summary>
    /// The browser used for printing. Configure Platform:PdfEngine to override - which is how the
    /// Linux host will be pointed at its own Chromium, since none of the Windows paths will exist.
    /// </summary>
    public string? FindEngine()
    {
        if (!string.IsNullOrWhiteSpace(_configured))
            return File.Exists(_configured) ? _configured : null;

        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            }
            : new[]
            {
                "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome",
                "/snap/bin/chromium", "/usr/bin/microsoft-edge",
            };

        return candidates.FirstOrDefault(File.Exists);
    }

    public bool Available => FindEngine() != null;

    /// <summary>
    /// Converts <paramref name="htmlPath"/> to a PDF beside it. Returns the PDF path, or null with
    /// the reason in <paramref name="reason"/>.
    /// </summary>
    public async Task<(string? pdfPath, string reason)> ConvertAsync(string htmlPath, CancellationToken ct = default)
    {
        var engine = FindEngine();
        if (engine == null) return (null, "no PDF engine is installed on the server");
        if (!File.Exists(htmlPath)) return (null, "the HTML report is missing");

        // Same lesson the toolkit learned the hard way: the browser drops privileges and may not be
        // able to write where it is told, and it exits 0 having produced nothing. So it always works
        // in a temp folder of its own and the finished file is moved into place afterwards.
        var stage = Path.Combine(Path.GetTempPath(), "rsp-pdf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        var stageHtml = Path.Combine(stage, "report.html");
        var stagePdf = Path.Combine(stage, "report.pdf");
        var profile = Path.Combine(stage, "profile");

        try
        {
            File.Copy(htmlPath, stageHtml, true);

            var psi = new ProcessStartInfo(engine)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "--headless", "--disable-gpu", "--no-sandbox", "--disable-extensions",
                "--no-first-run", "--no-default-browser-check", "--print-to-pdf-no-header",
                $"--user-data-dir={profile}",
                $"--print-to-pdf={stagePdf}",
                // Absolute file:// URL. A relative path produces something the browser cannot
                // resolve, and it then fails silently with no error and no PDF.
                new Uri(stageHtml).AbsoluteUri
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return (null, "the PDF engine would not start");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var stderr = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (null, "the PDF engine did not finish within two minutes");
            }

            // It can report finishing just before the file lands.
            for (var i = 0; i < 10 && !File.Exists(stagePdf); i++) await Task.Delay(200, ct);

            if (!File.Exists(stagePdf))
            {
                var err = (await stderr).Trim();
                if (err.Length > 200) err = err[..200];
                return (null, $"the PDF engine exited {p.ExitCode} without producing a file{(err.Length > 0 ? ": " + err : "")}");
            }

            var target = Path.ChangeExtension(htmlPath, ".pdf");
            File.Move(stagePdf, target, true);
            return (target, "");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Server-side PDF conversion failed for {Html}", htmlPath);
            return (null, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
    }
}
