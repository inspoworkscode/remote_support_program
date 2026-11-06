using System.Text;

namespace TestChecklist;

/// The fonts every read-only text pane uses. In points, so they follow the monitor's scaling.
sealed class Styles
{
    public Font Body { get; }
    public Font Bold { get; }
    public Font Italic { get; }
    public Font BoldItalic { get; }
    public Font Mono { get; }
    public Font Heading { get; }
    public Font Small { get; }

    public Styles(Font b)
    {
        Body = b;
        Bold = new Font(b, FontStyle.Bold);
        Italic = new Font(b, FontStyle.Italic);
        BoldItalic = new Font(b, FontStyle.Bold | FontStyle.Italic);
        Mono = new Font("Consolas", b.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
        Heading = new Font(b.FontFamily, b.SizeInPoints * 1.3f, FontStyle.Bold, GraphicsUnit.Point);
        Small = new Font(b.FontFamily, b.SizeInPoints * 0.92f, FontStyle.Regular, GraphicsUnit.Point);
    }
}

/// Writes the checklist's markdown into a RichTextBox: paragraphs, bullets, code blocks,
/// **bold**, *italic* and `code`. Nothing more is used in TEST-3.6.md.
sealed class RichWriter
{
    readonly RichTextBox _box;
    readonly Styles _st;

    public RichWriter(RichTextBox box, Styles st)
    {
        _box = box;
        _st = st;
        _box.Clear();
    }

    public void Write(string text, Font? font = null, Color? color = null)
    {
        if (text.Length == 0) return;
        _box.SelectionStart = _box.TextLength;
        _box.SelectionLength = 0;
        _box.SelectionFont = font ?? _st.Body;
        _box.SelectionColor = color ?? _box.ForeColor;
        _box.SelectedText = text;
    }

    public void Line(string text = "", Font? font = null, Color? color = null) => Write(text + "\n", font, color);

    public void Heading(string text, Color? color = null) => Line(text, _st.Heading, color);

    /// One empty line, never two.
    public void Gap()
    {
        var t = _box.Text;
        if (t.Length > 0 && !t.EndsWith("\n\n")) Write(t.EndsWith("\n") ? "\n" : "\n\n");
    }

    public void Inline(string s, Color? color = null, bool italic = false)
    {
        var sb = new StringBuilder();
        bool bold = false, ital = false;
        void Flush()
        {
            if (sb.Length == 0) return;
            bool i = ital || italic;
            Write(sb.ToString(), bold && i ? _st.BoldItalic : bold ? _st.Bold : i ? _st.Italic : _st.Body, color);
            sb.Clear();
        }

        for (int i = 0; i < s.Length;)
        {
            if (s[i] == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    Write(s[(i + 1)..end], _st.Mono, color);
                    i = end + 1;
                    continue;
                }
            }
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                Flush(); bold = !bold; i += 2; continue;
            }
            if (s[i] == '*' && (ital || (i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && s.IndexOf('*', i + 1) > i)))
            {
                Flush(); ital = !ital; i++; continue;
            }
            sb.Append(s[i++]);
        }
        Flush();
    }

    public void Markdown(IEnumerable<string> lines, Color? color = null, bool italic = false)
    {
        var para = new StringBuilder();
        bool fence = false;
        void FlushPara()
        {
            if (para.Length == 0) return;
            Inline(para.ToString(), color, italic);
            Write("\n");
            para.Clear();
        }

        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.StartsWith("```")) { FlushPara(); fence = !fence; continue; }
            if (fence) { Line("    " + t, _st.Mono, color); continue; }
            if (t.Length == 0) { FlushPara(); Gap(); continue; }
            if (t == "---") { FlushPara(); continue; }
            if (t.StartsWith("- ") || t.StartsWith("* "))
            {
                FlushPara();
                para.Append("   •  ").Append(t[2..]);
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(t);
        }
        FlushPara();
    }
}

static class Ui
{
    /// An AutoSize button left on GrowOnly keeps the size it measured BEFORE the window was scaled,
    /// then gets scaled again on top — so every button ends up half as wide again as its text.
    public static void Tidy(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button { AutoSize: true } b) b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Tidy(c);
        }
    }
}
