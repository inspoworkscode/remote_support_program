using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// A small bar showing how loud the customer's machine currently is.
///
/// This is not decoration. It is the answer to "is the audio working", and it is the only answer
/// available to a technician who is not wearing headphones, who has their own speakers turned
/// down, or who is watching an unattended machine at night. The drag-and-drop lesson applies
/// exactly: a feature whose only feedback is a sound nobody is listening to is indistinguishable
/// from a broken one, and will be reported as broken.
///
/// It holds a decaying peak alongside the live level, because sound is intermittent — a meter
/// that only showed the instantaneous value would sit at zero between words and look dead.
/// </summary>
internal sealed class LevelMeter : Control
{
    private readonly Theme _t;
    private double _level, _peak;
    private DateTime _peakAt = DateTime.MinValue;

    public LevelMeter(Theme t)
    {
        _t = t;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    /// <summary>Off means "not listening": the bar greys out rather than reading zero, so
    /// "silent" and "not switched on" never look the same.</summary>
    public bool Active { get; set; }

    public void SetLevel(double level)
    {
        _level = Math.Clamp(level, 0, 1);
        if (_level >= _peak) { _peak = _level; _peakAt = DateTime.UtcNow; }
        else
        {
            // Fall away over about a second, so a peak is visible without freezing the meter.
            var age = (DateTime.UtcNow - _peakAt).TotalSeconds;
            _peak = Math.Max(_level, _peak - age * 0.02);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, (Height - 10) / 2, Math.Max(1, Width - 1), 10);

        using (var track = new SolidBrush(Color.FromArgb(Active ? 40 : 20, _t.Ink)))
            g.FillRectangle(track, r);

        if (!Active) return;

        // Sound is logarithmic and a linear bar spends its life near zero, so show roughly
        // decibels: quiet speech lands in the middle instead of being invisible.
        double shown = _level <= 0 ? 0 : Math.Clamp(1 + Math.Log10(_level) / 3.0, 0, 1);
        int w = (int)(r.Width * shown);
        if (w > 0)
        {
            var colour = shown > 0.9 ? _t.Orange : _t.Green;
            using var b = new SolidBrush(colour);
            g.FillRectangle(b, new Rectangle(r.X, r.Y, w, r.Height));
        }

        double peakShown = _peak <= 0 ? 0 : Math.Clamp(1 + Math.Log10(_peak) / 3.0, 0, 1);
        int px = r.X + (int)(r.Width * peakShown);
        if (peakShown > 0.01)
        {
            using var p = new Pen(_t.Ink, 1.5f);
            g.DrawLine(p, px, r.Y, px, r.Bottom);
        }
    }
}
