using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Perch.Ui;

using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// Small borderless popup shown when the cursor dwells over the usage bars. Lists the 5-hour
/// ("session") and 7-day ("weekly") percentages with their reset times, plus — when the data is
/// dimmed — the reason it couldn't be refreshed.
/// </summary>
internal sealed class UsageTooltipForm : Form
{
    private const int HorizPad = 10;
    private const int VertPad  = 7;
    private const int Corner   = 6;
    private const int LineGap  = 3;

    private static readonly Color BgColor     = Color.FromArgb(20,  20,  28);
    private static readonly Color BorderColor = Color.FromArgb(60,  60,  80);
    private static readonly Color FgColor     = Color.FromArgb(225, 225, 235);
    private static readonly Color MutedColor  = Color.FromArgb(150, 150, 170);

    private readonly List<(string text, Color color, bool bold)> _lines = new();

    public UsageTooltipForm()
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

    /// <summary>
    /// Builds the tooltip content from a usage snapshot and shows it anchored just to the left of
    /// the given strip rectangle (in screen coordinates), so it never covers the overlay.
    /// </summary>
    public void ShowFor(UsageInfo usage, Rectangle stripScreenRect)
    {
        var now = DateTime.Now;
        _lines.Clear();
        _lines.Add(("Plan usage", FgColor, true));
        _lines.Add((Line("Session", usage.FiveHourPercent, usage.FiveHourResetsAt, now), FgColor, false));
        _lines.Add((Line("Weekly",  usage.SevenDayPercent, usage.SevenDayResetsAt, now), FgColor, false));

        if (usage.IsStale(now))
        {
            var reason = usage.Error;
            if (string.IsNullOrEmpty(reason))
                reason = usage.LastUpdated == DateTime.MinValue
                    ? "No usage data yet"
                    : $"Updated {Ago(now - usage.LastUpdated)} ago — couldn't refresh";
            _lines.Add((reason, MutedColor, false));
        }

        using var bold = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        using var reg  = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var g    = CreateGraphics();

        float w = 0;
        float h = VertPad * 2;
        foreach (var (text, _, isBold) in _lines)
        {
            var sz = g.MeasureString(text, isBold ? bold : reg);
            w = Math.Max(w, sz.Width);
            h += sz.Height + LineGap;
        }
        h -= LineGap;

        ClientSize = new Size((int)w + HorizPad * 2, (int)h);

        int x = stripScreenRect.Left - ClientSize.Width - 6;
        if (x < 0) x = stripScreenRect.Right + 6;  // fall back to the right if there's no room left
        int y = stripScreenRect.Top;

        Location = new Point(x, y);
        Invalidate();
        Show();
        BringToFront();
    }

    private static string Line(string label, double? percent, DateTime? resetsAt, DateTime now)
    {
        string pct = percent is { } p ? $"{(int)Math.Round(p)}%" : "—";
        string s = $"{label}  {pct}";
        if (resetsAt is { } r && r > now)
            s += $"  · resets in {Until(r - now)}";
        return s;
    }

    private static string Until(TimeSpan t)
    {
        if (t.TotalDays >= 1)    return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1)   return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }

    private static string Ago(TimeSpan t)
    {
        if (t.TotalHours >= 1)   return $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalSeconds}s";
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

        using var bold = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        using var reg  = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

        float y = VertPad;
        foreach (var (text, color, isBold) in _lines)
        {
            var font = isBold ? bold : reg;
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, HorizPad, y);
            y += g.MeasureString(text, font).Height + LineGap;
        }
    }
}
