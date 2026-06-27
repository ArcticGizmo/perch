using System.Drawing.Drawing2D;

namespace Perch.Ui;

/// <summary>
/// An owner-drawn pill button that wears the Perch Wrapped poster's gradient (indigo → violet →
/// magenta), so it advertises the feature it launches. Brightens on hover and dims when disabled. The
/// label is drawn as two runs — a colour-emoji sparkle and the body text — because GDI+ renders a mixed
/// string's emoji as a tofu box (the same reason the poster splits them).
/// </summary>
internal sealed class GradientButton : Button
{
    private readonly string _emoji;
    private readonly string _label;
    private readonly Font _emojiFont = new("Segoe UI Emoji", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _labelFont = new("Segoe UI Semibold", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private bool _hover;

    public GradientButton(string emoji, string label)
    {
        _emoji = emoji;
        _label = label;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Corners show the toolbar behind the pill.
        g.Clear(Parent?.BackColor ?? BackColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Height / 2;
        using var path = PaintKit.RoundedRect(rect, radius);

        var stops = WrappedRenderer.BackdropStops;
        using (var brush = new LinearGradientBrush(rect, stops[0], stops[^1], LinearGradientMode.Horizontal))
        {
            brush.InterpolationColors = new ColorBlend(stops.Count)
            {
                Colors    = stops.ToArray(),
                Positions = Positions(stops.Count),
            };
            g.FillPath(brush, path);
        }

        if (_hover && Enabled)                                  // a brightening sheen on hover
            using (var sheen = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
                g.FillPath(sheen, path);
        if (!Enabled)                                          // dim toward the toolbar when disabled
            using (var dim = new SolidBrush(Color.FromArgb(150, 24, 24, 32)))
                g.FillPath(dim, path);

        using (var border = new Pen(Color.FromArgb(Enabled ? 70 : 30, 255, 255, 255), 1f))
            g.DrawPath(border, path);

        DrawLabel(g);
    }

    // Centres [sparkle] [label] as a pair: the sparkle in the emoji font, the label in the body font.
    private void DrawLabel(Graphics g)
    {
        Color textColor = Enabled ? Color.White : Color.FromArgb(150, 150, 165);
        var eSize = g.MeasureString(_emoji, _emojiFont);
        var tSize = g.MeasureString(_label, _labelFont);
        const float gap = 1f;
        float x = (Width - (eSize.Width + gap + tSize.Width)) / 2f;
        float midY = Height / 2f;
        if (Enabled)
            using (var eBr = new SolidBrush(Color.White))
                g.DrawString(_emoji, _emojiFont, eBr, x, midY - eSize.Height / 2f);
        using (var tBr = new SolidBrush(textColor))
            g.DrawString(_label, _labelFont, tBr, x + eSize.Width + gap, midY - tSize.Height / 2f);
    }

    // Evenly spaced blend positions (0, 0.5, 1 for three stops) — endpoints must be exactly 0 and 1.
    private static float[] Positions(int n)
    {
        var p = new float[n];
        for (int i = 0; i < n; i++) p[i] = i / (float)(n - 1);
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _emojiFont.Dispose();
            _labelFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
