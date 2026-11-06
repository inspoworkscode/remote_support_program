using System.Drawing;
using System.Windows.Forms;

namespace RemoteSupport.UI;

/// <summary>Asked only when neither the download's address nor its name says whose it is.</summary>
public sealed class AskCompanyDialog : Form
{
    private readonly TextBox _box;

    public const string CustomerPrompt = "Which IT company is helping you? Type the code they gave you — it is the last part of the link you downloaded this from, such as anywhere-it.";

    public AskCompanyDialog(string question = CustomerPrompt)
    {
        Text = "Remote support";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Segoe UI", 10f);
        int line = TextRenderer.MeasureText("Ag", Font).Height;
        var prompt = new Label
        {
            Text = question,
            AutoSize = true, MaximumSize = new Size(420, 0), Location = new Point(16, 16)
        };
        _box = new TextBox { Location = new Point(16, 16 + prompt.PreferredSize.Height + 10), Width = 420 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        int y = _box.Bottom + 14;
        cancel.Location = new Point(436 - cancel.PreferredSize.Width, y);
        ok.Location = new Point(cancel.Left - ok.PreferredSize.Width - 8, y);
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange(new Control[] { prompt, _box, ok, cancel });
        ClientSize = new Size(452, y + Math.Max(ok.PreferredSize.Height, line + 12) + 16);
    }

    public static string? Ask(string prompt = CustomerPrompt)
    {
        using var d = new AskCompanyDialog(prompt);
        return d.ShowDialog() == DialogResult.OK ? d._box.Text : null;
    }
}
