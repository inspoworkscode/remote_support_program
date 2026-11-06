// keeperretry - does the agent keep trying when an antivirus holds the health check install, and
// does the console let a technician say "try now"? (BUGS.md, bug 9)
//
// Clint, on a customer machine running ESET: ESET holds the file, the install fails, ESET releases
// it about a minute later having decided it is safe - and by then the one attempt there was had
// failed, and nothing ever tried again.
//
// This drives the REAL HealthCheckKeeper.Want -> retry -> report path, with an attempt that is
// scripted rather than PowerShell (so no machine is touched and nothing needs elevation) and
// delays in milliseconds rather than minutes. Then the console's rule, HealthCheckModes.WorthSending.
//
//   dotnet run -c Release --project tools/keeperretry/keeperretry.csproj

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Agent;
using RemoteSupport.Shared;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " - " + detail : "")}"); }
    else { fail++; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " - " + detail : "")}"); Console.ResetColor(); }
}

var ms = (int n) => TimeSpan.FromMilliseconds(n);
var on = new HealthCheckWantedMsg { On = true };
var off = new HealthCheckWantedMsg { On = false };

// One scripted machine: every attempt is recorded, with when it happened, and answers from a script.
(HealthCheckKeeper keeper, ConcurrentQueue<(bool On, long At)> attempts, ConcurrentQueue<HealthCheckStatusMsg> reports, Stopwatch clock)
    Machine(TimeSpan[] delays, Func<HealthCheckWantedMsg, int, HealthCheckKeeper.Outcome> script)
{
    var attempts = new ConcurrentQueue<(bool, long)>();
    var reports = new ConcurrentQueue<HealthCheckStatusMsg>();
    var clock = Stopwatch.StartNew();
    int n = 0;
    var k = new HealthCheckKeeper(NullLogger.Instance, new Branding())
    {
        RetryDelays = delays,
        Attempt = m => { attempts.Enqueue((m.On, clock.ElapsedMilliseconds)); return script(m, Interlocked.Increment(ref n)); }
    };
    return (k, attempts, reports, clock);
}

var installed = new HealthCheckKeeper.Outcome(new HealthCheckStatusMsg { Installed = true, Note = "installed" }, false);
var removed = new HealthCheckKeeper.Outcome(new HealthCheckStatusMsg { Installed = false, Note = "" }, false);
var held = new HealthCheckKeeper.Outcome(new HealthCheckStatusMsg { Installed = false, Note = "could not install it (exit 1)" }, true);
var noScripts = new HealthCheckKeeper.Outcome(new HealthCheckStatusMsg { Installed = false, Note = "the health check scripts are not beside the support app" }, false);

async Task Settle(Func<bool> done, int timeoutMs = 5000)
{
    var sw = Stopwatch.StartNew();
    while (!done() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
}

var three = new[] { ms(150), ms(150), ms(150) };

Console.WriteLine();
Console.WriteLine("== nothing wrong: installs first time ==");
{
    var (k, a, r, _) = Machine(three, (_, _) => installed);
    k.Want(on, r.Enqueue);
    await Settle(() => r.Count >= 1); await Task.Delay(600);
    Check("one attempt", a.Count == 1, $"{a.Count}");
    Check("one report, installed", r.Count == 1 && r.Last().Installed, $"{r.Count} report(s)");
}

Console.WriteLine();
Console.WriteLine("== ESET's shape: held twice, released, then it installs ==");
{
    var (k, a, r, _) = Machine(three, (_, n) => n <= 2 ? held : installed);
    k.Want(on, r.Enqueue);
    await Settle(() => r.Any(x => x.Installed)); await Task.Delay(600);
    Check("three attempts", a.Count == 3, $"{a.Count}");
    Check("it ends installed", r.Last().Installed, r.Last().Note);
    var notes = r.Select(x => x.Note).ToList();
    Check("each failure said a retry was coming", notes.Take(2).All(x => x.Contains("trying again in")), string.Join(" | ", notes.Take(2)));
    Check("and said why it might", notes[0].Contains("antivirus"));
    var times = a.Select(x => x.At).ToList();
    Check("it waited between attempts", times[1] - times[0] >= 120 && times[2] - times[1] >= 120, $"{times[1] - times[0]} ms, {times[2] - times[1]} ms");
}

Console.WriteLine();
Console.WriteLine("== the same machine as it was before this change: one attempt ==");
{
    var (k, a, r, _) = Machine(Array.Empty<TimeSpan>(), (_, n) => n <= 2 ? held : installed);
    k.Want(on, r.Enqueue);
    await Settle(() => r.Count >= 1); await Task.Delay(600);
    Check("one attempt, and it stays failed - bug 9 exactly", a.Count == 1 && !r.Last().Installed, $"{a.Count} attempt(s)");
}

Console.WriteLine();
Console.WriteLine("== held for good: it gives up, and says how to try again ==");
{
    var (k, a, r, _) = Machine(three, (_, _) => held);
    k.Want(on, r.Enqueue);
    await Settle(() => r.Any(x => x.Note.Contains("tried"))); await Task.Delay(700);
    Check("four attempts and no more", a.Count == 4, $"{a.Count}");
    Check("four reports", r.Count == 4, $"{r.Count}");
    Check("the last says it tried four times", r.Last().Note.Contains("tried 4 times"), r.Last().Note);
    Check("and how to try again", r.Last().Note.Contains("choose it again in the console"));
    Check("and where the log is", r.Last().Note.Contains("healthcheck-agent.log"));
}

Console.WriteLine();
Console.WriteLine("== not worth retrying: the scripts are missing ==");
{
    var (k, a, r, _) = Machine(three, (_, _) => noScripts);
    k.Want(on, r.Enqueue);
    await Settle(() => r.Count >= 1); await Task.Delay(600);
    Check("one attempt - waiting does not put the scripts there", a.Count == 1, $"{a.Count}");
    Check("reported as it is", r.Count == 1 && r.Last().Note.StartsWith("the health check scripts"));
}

// The trap bug 9 named: a machine switched OFF during the wait must not be installed at the end of it.
Console.WriteLine();
Console.WriteLine("== switched OFF while waiting to retry ==");
{
    var (k, a, r, clock) = Machine(new[] { ms(2000), ms(2000), ms(2000) }, (m, _) => m.On ? held : removed);
    k.Want(on, r.Enqueue);
    await Settle(() => a.Count >= 1); await Task.Delay(200);
    var offAt = clock.ElapsedMilliseconds;
    k.Want(off, r.Enqueue);
    await Settle(() => a.Any(x => !x.On)); await Task.Delay(4500);
    var seq = a.ToList();
    Check("the OFF is acted on at once, not after the wait", seq.Count >= 2 && !seq[1].On && seq[1].At - offAt < 1000,
          seq.Count >= 2 ? $"{seq[1].At - offAt} ms after it arrived" : "never");
    Check("and the old ON is never tried again", seq.Count(x => x.On) == 1, string.Join(", ", seq.Select(x => x.On ? "on" : "off")));
    Check("the last word is the OFF's", !r.Last().Installed && r.Last().Note == "", $"'{r.Last().Note}'");
}

Console.WriteLine();
Console.WriteLine("== a technician chooses On again while it waits: try now ==");
{
    var (k, a, r, clock) = Machine(new[] { ms(3000), ms(3000), ms(3000) }, (_, n) => n == 1 ? held : installed);
    k.Want(on, r.Enqueue);
    await Settle(() => a.Count >= 1); await Task.Delay(200);
    var againAt = clock.ElapsedMilliseconds;
    k.Want(on, r.Enqueue);
    await Settle(() => a.Count >= 2); await Task.Delay(300);
    var seq = a.ToList();
    Check("the second attempt comes straight away", seq.Count >= 2 && seq[1].At - againAt < 1000,
          seq.Count >= 2 ? $"{seq[1].At - againAt} ms, not 3000" : "never");
    Check("and it ends installed", r.Last().Installed);
}

// A release left over from an answer that never had to wait must not cut a later wait short.
Console.WriteLine();
Console.WriteLine("== a stale wake-up does not shorten a wait ==");
{
    var (k, a, r, _) = Machine(new[] { ms(600), ms(600), ms(600) }, (_, n) => n == 1 ? installed : n == 2 ? held : installed);
    k.Want(on, r.Enqueue);
    await Settle(() => r.Count >= 1); await Task.Delay(100);
    k.Want(on, r.Enqueue);      // the same answer later - say the agent reconnected
    await Settle(() => a.Count >= 3, 4000);
    var seq = a.ToList();
    Check("the retry still waited its full time", seq.Count >= 3 && seq[2].At - seq[1].At >= 550,
          seq.Count >= 3 ? $"{seq[2].At - seq[1].At} ms of 600" : $"{seq.Count} attempts");
}

Console.WriteLine();
Console.WriteLine("== the console: when is a choice sent? ==");
{
    const int As = HealthCheckModes.AsClient, On = HealthCheckModes.On, Off = HealthCheckModes.Off;
    bool Old(int cur, int chosen) => cur != chosen;   // the rule before this change
    Check("a different choice", HealthCheckModes.WorthSending(Off, On, false, false));
    Check("On again, installed: nothing to do", !HealthCheckModes.WorthSending(On, On, true, true));
    Check("On again, the install FAILED: sent - this is the retry", HealthCheckModes.WorthSending(On, On, true, false));
    Check("...which the old rule refused", !Old(On, On));
    Check("On again, never reported: sent", HealthCheckModes.WorthSending(On, On, true, null));
    Check("As its customer again, customer ticked, install failed: sent", HealthCheckModes.WorthSending(As, As, true, false));
    Check("Off again, the removal FAILED: sent", HealthCheckModes.WorthSending(Off, Off, false, true));
    Check("Off again, removed: nothing to do", !HealthCheckModes.WorthSending(Off, Off, false, false));
    Check("Off, somebody else's left alone: nothing to do", !HealthCheckModes.WorthSending(As, As, false, false));
    Check("an unrecognised stored mode reads as its customer", !HealthCheckModes.WorthSending(7, As, false, false));
}

const int expected = 31;
Console.WriteLine();
if (pass + fail < expected) { Console.WriteLine($"INCONCLUSIVE - only {pass + fail} of {expected} checks ran."); return 1; }
Console.WriteLine(fail == 0 ? $"{pass} of {pass} checks passed." : $"FAILED {fail} of {pass + fail}");
return fail == 0 ? 0 : 1;
