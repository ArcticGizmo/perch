using System.Drawing.Drawing2D;

namespace Perch.Ui;

/// <summary>Which screen edge the dense strip docks to.</summary>
internal enum DenseSide { Left, Right }

/// <summary>
/// A translucent column shown on the left or right edge of a monitor while the dense strip is
/// being dragged, marking where it can be pinned. Purely visual: it never takes focus or input,
/// so the overlay detects "cursor is over this zone" via screen coordinates rather than mouse
/// events.
/// </summary>
internal sealed class DenseDropZoneForm : Form
{
    private const int ColumnWidth = 56;

    private static readonly Color IdleFill   = Color.FromArgb(56,  189, 248);  // calm accent blue
    private static readonly Color ActiveFill = Color.FromArgb(34,  197, 94);   // running green

    private bool _active;

    public Screen TargetScreen { get; }
    public DenseSide Side { get; }

    public DenseDropZoneForm(Screen screen, DenseSide side)
    {
        TargetScreen      = screen;
        Side              = side;
        FormBorderStyle   = FormBorderStyle.None;
        ShowInTaskbar     = false;
        TopMost           = true;
        AllowTransparency = true;
        BackColor         = IdleFill;
        Opacity           = 0.40;
        StartPosition     = FormStartPosition.Manual;
        Enabled           = false;   // never interactive
        DoubleBuffered    = true;

        var wa = screen.WorkingArea;
        int x  = side == DenseSide.Left ? wa.Left : wa.Right - ColumnWidth;
        Bounds = new Rectangle(x, wa.Top, ColumnWidth, wa.Height);
    }

    public bool ContainsScreenPoint(Point screenPt) => Bounds.Contains(screenPt);

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active   = active;
        BackColor = active ? ActiveFill : IdleFill;
        Opacity   = active ? 0.65 : 0.40;
        Invalidate();
    }

    // Don't steal focus or activation when shown during a drag.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW — no taskbar entry, no Alt+Tab
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE — never take foreground
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var accent = _active ? ActiveFill : IdleFill;

        // Dashed inner border to read as a "drop lane".
        using (var pen = new Pen(Color.FromArgb(220, accent), 2f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(pen, 2, 2, ClientSize.Width - 5, ClientSize.Height - 5);

        // A centered pin glyph (arrow into a pipe) hinting that the strip docks to this edge:
        // "->|" for the right edge, mirrored to "|<-" for the left.
        int cx = ClientSize.Width / 2;
        int cy = ClientSize.Height / 2;
        using var glyphPen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (Side == DenseSide.Right)
        {
            int pipeX    = cx + 9;
            int shaftEnd = pipeX - 3;
            g.DrawLine(glyphPen, cx - 11, cy, shaftEnd, cy);            // shaft
            g.DrawLine(glyphPen, shaftEnd - 6, cy - 6, shaftEnd, cy);   // arrowhead
            g.DrawLine(glyphPen, shaftEnd - 6, cy + 6, shaftEnd, cy);
            g.DrawLine(glyphPen, pipeX, cy - 10, pipeX, cy + 10);       // pipe
        }
        else
        {
            int pipeX    = cx - 9;
            int shaftEnd = pipeX + 3;
            g.DrawLine(glyphPen, cx + 11, cy, shaftEnd, cy);            // shaft
            g.DrawLine(glyphPen, shaftEnd + 6, cy - 6, shaftEnd, cy);   // arrowhead
            g.DrawLine(glyphPen, shaftEnd + 6, cy + 6, shaftEnd, cy);
            g.DrawLine(glyphPen, pipeX, cy - 10, pipeX, cy + 10);       // pipe
        }
    }
}
