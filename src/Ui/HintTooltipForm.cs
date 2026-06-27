using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Perch.Ui;

/// <summary>
/// A minimal one-line dark tooltip — the same borderless, rounded, theme-matched styling as
/// <see cref="UsageTooltipForm"/>, but for a single caller-supplied string anchored at a screen
/// point. Used for the per-row context-pressure thermometer ("Context at NN%").
/// </summary>
internal sealed class HintTooltipForm : Form
{
    private const int HorizPad = 8;
    private const int VertPad  = 5;
    private const int Corner   = 6;

    private static readonly Color BgColor     = Color.FromArgb(20,  20,  28);
    private static readonly Color BorderColor = Color.FromArgb(60,  60,  80);
    private static readonly Color FgColor     = Color.FromArgb(225, 225, 235);

    private string _text = "";

    public HintTooltipForm()
    {
        FormBorderStyle   = FormBorderStyle.None;
        ShowInTaskbar     = false;
        TopMost           = true;
        AllowTransparency = true;
        BackColor         = Color.Black;
        TransparencyKey   = Color.Black;
        DoubleBuffered    = true;
        StartPosition     = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>Shows <paramref name="text"/> with its top-left at <paramref name="anchorScreen"/>
    /// (screen coordinates), nudged so it stays within that point's screen working area.</summary>
    public void ShowText(string text, Point anchorScreen)
    {
        _text = text;

        using var font = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        using (var g = CreateGraphics())
        {
            var sz = g.MeasureString(text, font);
            ClientSize = new Size((int)sz.Width + HorizPad * 2, (int)sz.Height + VertPad * 2);
        }

        var wa = Screen.FromPoint(anchorScreen).WorkingArea;
        int x = Math.Clamp(anchorScreen.X, wa.Left + 2, wa.Right  - ClientSize.Width  - 2);
        int y = Math.Clamp(anchorScreen.Y, wa.Top  + 2, wa.Bottom - ClientSize.Height - 2);

        Location = new Point(x, y);
        Invalidate();
        Show();
        BringToFront();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var path = PaintKit.RoundedRect(bounds, Corner);

        using (var bg = new SolidBrush(BgColor))
            g.FillPath(bg, path);
        using (var pen = new Pen(BorderColor, 1.5f))
            g.DrawPath(pen, path);

        using var font  = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(FgColor);
        g.DrawString(_text, font, brush, HorizPad, VertPad);
    }
}
