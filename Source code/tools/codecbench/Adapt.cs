using RemoteSupport.Host;

namespace CodecBench;

/// <summary>
/// Stage 2.3's controller, driven through link conditions it cannot tell from the real thing.
///
/// No machine, no socket, no screen: <see cref="LinkController"/> takes a queue depth, a refusal
/// count and how much of the screen moved, and answers with a quality and a frame rate. That is
/// the whole of its contract, so that is what gets tested — and it can be tested at a thousand
/// passes a second rather than at twelve.
///
///   codecbench --adapt
///
/// Every check here states the property rather than the number, because the ladders ARE guesses
/// and are meant to be replaced by a measurement. A check that asserted "level 3 gives quality 33"
/// would fail the first time someone improved the ladder, which is the opposite of useful. What
/// must not change is that it backs off under pressure, recovers slowly, never exceeds the
/// ceiling, and moves frame rate first on moving pictures and quality first on still ones.
/// </summary>
public static class Adapt
{
    public static int Run()
    {
        Console.WriteLine("Adaptive quality — the controller against scripted link conditions.");
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        }

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ---- it starts by doing exactly what it was told ------------------------------------
        {
            var c = new LinkController(80, 15);
            Check("with a healthy link it gives the technician what they asked for",
                  c.Level == 0 && c.Quality == 80 && c.Fps == 15, $"quality {c.Quality}, {c.Fps} fps");
        }

        // ---- a link that cannot carry it ------------------------------------------------------
        {
            var c = new LinkController(80, 15);
            var now = t0;
            long dropped = 0;
            for (int i = 0; i < 200; i++)
            {
                now = now.AddMilliseconds(80);
                c.Update(2 * 1024 * 1024, dropped, 0.1, now);   // 2 MB backed up, still screen
            }
            Check("a backed-up queue drives it down", c.Level > 0, c.Describe());
            Check("and it stops at the bottom of the ladder rather than running away",
                  c.Level == LinkController.MaxLevel, $"level {c.Level} of {LinkController.MaxLevel}");
            Check("it never sends more than the technician asked for",
                  c.Quality <= 80 && c.Fps <= 15, $"quality {c.Quality} <= 80, {c.Fps} fps <= 15");
            Check("and what it settles on is genuinely cheaper", c.Quality < 80 && c.Fps < 15, c.Describe());
        }

        // ---- a refusal is harder evidence than depth ------------------------------------------
        {
            var byDepth = new LinkController(80, 15);
            var byRefusal = new LinkController(80, 15);
            var now = t0.AddMilliseconds(50);
            // One pass only, and less time than the step-down rate limit allows.
            byDepth.Update(2 * 1024 * 1024, 0, 0.1, now);
            byRefusal.Update(2 * 1024 * 1024, 1, 0.1, now);
            byDepth.Update(2 * 1024 * 1024, 0, 0.1, now.AddMilliseconds(20));
            byRefusal.Update(2 * 1024 * 1024, 2, 0.1, now.AddMilliseconds(20));
            Check("a refused frame steps down immediately — picture is already lost by then",
                  byRefusal.Level > byDepth.Level, $"refusal level {byRefusal.Level}, depth-only level {byDepth.Level}");
        }

        // ---- fast down, slow up ----------------------------------------------------------------
        {
            var c = new LinkController(80, 15);
            var now = t0;
            // Down under load.
            int downPasses = 0;
            while (c.Level < LinkController.MaxLevel && downPasses < 500)
            { now = now.AddMilliseconds(80); c.Update(2 * 1024 * 1024, 0, 0.1, now); downPasses++; }
            var downTook = now;

            // Up once the link goes quiet.
            int upPasses = 0;
            while (c.Level > 0 && upPasses < 5000)
            { now = now.AddMilliseconds(80); c.Update(0, 0, 0.1, now); upPasses++; }
            Check("it climbs back when the link recovers", c.Level == 0, c.Describe());
            Check("and it climbs back MORE SLOWLY than it fell — the classic failure here is hunting",
                  (now - downTook) > (downTook - t0) * 2,
                  $"fell in {(downTook - t0).TotalSeconds:0.0}s, climbed in {(now - downTook).TotalSeconds:0.0}s");
        }

        // ---- the hold band: neither struggling nor clearly fine ---------------------------------
        {
            var c = new LinkController(80, 15);
            var now = t0;
            // Settle it somewhere in the middle first.
            for (int i = 0; i < 20; i++) { now = now.AddMilliseconds(80); c.Update(2 * 1024 * 1024, 0, 0.1, now); }
            int settled = c.Level;
            // Then sit squarely between the two marks for a long time.
            long between = (LinkController.RecoverBytes + LinkController.BackOffBytes) / 2;
            int moves = 0;
            for (int i = 0; i < 400; i++)
            {
                now = now.AddMilliseconds(80);
                if (c.Update(between, 0, 0.1, now)) moves++;
            }
            Check("between the two marks it holds still instead of hunting",
                  c.Level == settled && moves == 0, $"level {settled} held for 32 s, {moves} changes");
        }

        // ---- the two ladders --------------------------------------------------------------------
        {
            var still = new LinkController(80, 15);
            var moving = new LinkController(80, 15);
            var now = t0;
            for (int i = 0; i < 10; i++)
            {
                now = now.AddMilliseconds(500);
                still.Update(2 * 1024 * 1024, 0, 0.05, now);   // a few tiles changing: a document
                moving.Update(2 * 1024 * 1024, 0, 0.95, now);  // nearly every tile: video
            }
            Check("both kinds of screen back off", still.Level > 0 && moving.Level > 0,
                  $"still {still.Level}, moving {moving.Level}");
            Check("a MOVING picture gives up frame rate first", moving.Fps < still.Fps,
                  $"moving {moving.Fps} fps vs still {still.Fps} fps");
            Check("a STILL picture gives up quality first", still.Quality < moving.Quality,
                  $"still quality {still.Quality} vs moving {moving.Quality}");
        }

        // ---- the control: what this prints if the controller does nothing ------------------------
        // The question every check in this repository has to survive is "what would this print if
        // the thing it tests were absent?" Here that is answerable exactly: a controller that never
        // leaves level 0 is the product as it was before 2.3, so run one and show it is different.
        {
            var fixedOne = new LinkController(80, 15);
            var adaptive = new LinkController(80, 15);
            var now = t0;
            for (int i = 0; i < 100; i++)
            {
                now = now.AddMilliseconds(80);
                adaptive.Update(2 * 1024 * 1024, 0, 0.3, now);
                // fixedOne is never told anything, which is exactly what the old host did with the
                // link: nothing at all.
            }
            Console.WriteLine();
            Console.WriteLine($"  before 2.3 (never adapts): quality {fixedOne.Quality}, {fixedOne.Fps} fps — on a link 2 MB behind");
            Console.WriteLine($"  with the controller:       quality {adaptive.Quality}, {adaptive.Fps} fps");
            Console.WriteLine();
            Check("the two are distinguishable, so this harness can tell them apart",
                  adaptive.Quality != fixedOne.Quality || adaptive.Fps != fixedOne.Fps);
        }

        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }
}
