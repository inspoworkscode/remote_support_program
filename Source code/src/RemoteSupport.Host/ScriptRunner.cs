using System.Text;
using System.Diagnostics;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>Runs a script (PowerShell / batch / cmd) and streams stdout+stderr back live.</summary>
internal sealed class ScriptRunner
{
    private readonly Dictionary<string, Process> _running = new();
    private readonly object _lock = new();

    /// <summary>
    /// Coming out: we read the child's stdout as UTF-8, but PowerShell writes it in the console's
    /// OEM code page, so anything above ASCII is decoded as the wrong characters. The child has to
    /// be told to write UTF-8, and the only place to say so is inside the script itself.
    ///
    /// One line, guarded, and deliberately on its own line so the offset it adds to any line
    /// number in an error message is exactly one.
    /// </summary>
    private static string WithOutputEncodingPrologue(string script) =>
        "try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }\r\n" + script;

    public async Task RunAsync(ScriptRunMsg msg, Action<ScriptOutputMsg> send)
    {
        string tempFile = "", exe, args;
        try
        {
            switch (msg.Language.ToLowerInvariant())
            {
                case "powershell":
                    tempFile = Path.Combine(Path.GetTempPath(), $"rs_{msg.RunId}.ps1");
                    // Two encoding traps, both of which turn "café" into "cafÃ©" and neither of
                    // which reports anything. Found on 2026-09-13 when a transferred file called
                    // "café ümläut.txt" arrived on the customer's machine perfectly and then came
                    // back from a diagnostic script as "caf? ?ml?ut.txt".
                    //
                    // Going in: Windows PowerShell 5.1 reads a file with no byte-order mark as
                    // ANSI, not UTF-8, so a script containing any non-ASCII character arrives
                    // corrupted. WriteAllText's default is UTF-8 WITHOUT a BOM, so write one.
                    await File.WriteAllTextAsync(tempFile, WithOutputEncodingPrologue(msg.Script), new UTF8Encoding(true));
                    exe = "powershell.exe"; args = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{tempFile}\"";
                    break;
                case "batch": case "cmd":
                    tempFile = Path.Combine(Path.GetTempPath(), $"rs_{msg.RunId}.bat");
                    await File.WriteAllTextAsync(tempFile, msg.Script);
                    exe = "cmd.exe"; args = $"/c \"{tempFile}\"";
                    break;
                default:
                    send(new ScriptOutputMsg { RunId = msg.RunId, Text = $"Unsupported language: {msg.Language}", IsError = true, Done = true, ExitCode = -1 });
                    return;
            }

            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) send(new ScriptOutputMsg { RunId = msg.RunId, Text = e.Data + "\n" }); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) send(new ScriptOutputMsg { RunId = msg.RunId, Text = e.Data + "\n", IsError = true }); };
            p.Start();
            lock (_lock) _running[msg.RunId] = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            // 740 is Windows' "the requested operation requires elevation" — DISM, sfc and friends
            // run from a session the support app is hosting, as the person rather than as SYSTEM.
            // Said here, under the error, because that is where the technician is looking.
            var why = p.ExitCode == 740 && !UserContext.IsSystem
                ? "\n[This session runs as the person at the machine, not through the background service, so nothing here runs as administrator.]"
                : "";
            send(new ScriptOutputMsg { RunId = msg.RunId, Done = true, ExitCode = p.ExitCode, Text = $"\n[Exited with code {p.ExitCode}]{why}\n" });
        }
        catch (Exception ex)
        {
            send(new ScriptOutputMsg { RunId = msg.RunId, Text = ex.Message, IsError = true, Done = true, ExitCode = -1 });
        }
        finally
        {
            lock (_lock) _running.Remove(msg.RunId);
            if (tempFile.Length > 0) try { File.Delete(tempFile); } catch { }
        }
    }

    public void Kill(string runId)
    {
        Process? p; lock (_lock) _running.TryGetValue(runId, out p);
        try { p?.Kill(true); } catch { }
    }
}
