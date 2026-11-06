namespace RemoteSupport.Shared;

/// <summary>
/// The product's two sounds, made here as WAV files rather than borrowed from Windows (2026-10-07).
///
/// Clint: "When someone receives a chat message, I would like it to play a sound. A simple ding ding
/// type sound. Also when we get incoming support call requests ... more like a constant ringing which
/// is not overly intrusive." Windows' own Asterisk and Exclamation were what played before, once, and
/// a machine with its system sounds turned down or off played nothing at all.
///
/// Made in code, not shipped as files: nothing is bought in, and the console, the customer's tray app
/// and the portal (which serves these same bytes at <c>/sounds/{name}.wav</c>) cannot drift apart.
///
/// <see cref="Ding"/> is two soft bell notes, rising. <see cref="Ring"/> is ONE ring — two short warm
/// bursts — that the caller repeats every <see cref="RingEverySeconds"/> while somebody is waiting;
/// the quiet between rings is what keeps it from being intrusive.
/// </summary>
public static class Chimes
{
    private const int Rate = 22050;

    /// <summary>How often a waiting call rings again. The ring itself is about a second long.</summary>
    public const int RingEverySeconds = 4;

    private static byte[]? _ding, _ring;

    /// <summary>A chat message has arrived: C6 then E6, each a struck bell that dies away.</summary>
    public static byte[] Ding() => _ding ??= Wav(MakeDing());

    /// <summary>One ring of a waiting call. Play it again every <see cref="RingEverySeconds"/>.</summary>
    public static byte[] Ring() => _ring ??= Wav(MakeRing());

    private static float[] MakeDing()
    {
        var s = new float[(int)(Rate * 1.15)];
        Bell(s, 0.00, 1046.50);   // C6
        Bell(s, 0.17, 1318.51);   // E6
        return Finish(s, 0.40);
    }

    /// <summary>A struck bell: the note and two quieter overtones, a 4 ms strike, then a decay.</summary>
    private static void Bell(float[] s, double at, double f)
    {
        int start = (int)(at * Rate);
        for (int i = start; i < s.Length; i++)
        {
            double t = (double)(i - start) / Rate;
            double strike = Math.Min(1.0, t / 0.004);
            double v = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t / 0.30)
                     + 0.30 * Math.Sin(2 * Math.PI * f * 2 * t) * Math.Exp(-t / 0.12)
                     + 0.10 * Math.Sin(2 * Math.PI * f * 3 * t) * Math.Exp(-t / 0.06);
            s[i] += (float)(strike * v);
        }
    }

    private static float[] MakeRing()
    {
        var s = new float[(int)(Rate * 1.15)];
        Burst(s, 0.00, 0.42);
        Burst(s, 0.62, 0.42);
        return Finish(s, 0.26);
    }

    /// <summary>One burst of the ring: G5 and B5 together, a gentle 12 Hz warble, soft edges.</summary>
    private static void Burst(float[] s, double at, double length)
    {
        int start = (int)(at * Rate), end = Math.Min(s.Length, (int)((at + length) * Rate));
        for (int i = start; i < end; i++)
        {
            double t = (double)(i - start) / Rate;
            double edge = Math.Min(1.0, Math.Min(t / 0.025, (length - t) / 0.06));
            double warble = 0.78 + 0.22 * Math.Sin(2 * Math.PI * 12 * t);
            double v = 0.5 * Math.Sin(2 * Math.PI * 783.99 * t) + 0.5 * Math.Sin(2 * Math.PI * 987.77 * t);
            s[i] += (float)(Math.Max(0, edge) * warble * v);
        }
    }

    /// <summary>Scales to <paramref name="peak"/> of full scale and fades the last 30 ms, so it never clicks.</summary>
    private static float[] Finish(float[] s, double peak)
    {
        float max = 0;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        float k = max > 0 ? (float)(peak / max) : 0;
        int fade = Rate * 30 / 1000;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] *= k;
            int left = s.Length - 1 - i;
            if (left < fade) s[i] *= (float)left / fade;
        }
        return s;
    }

    /// <summary>16-bit mono PCM in a RIFF/WAVE wrapper: what SoundPlayer and every browser play.</summary>
    private static byte[] Wav(float[] s)
    {
        using var ms = new MemoryStream(44 + s.Length * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + s.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(s.Length * 2);
        foreach (var v in s) w.Write((short)Math.Clamp(Math.Round(v * 32767), -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
