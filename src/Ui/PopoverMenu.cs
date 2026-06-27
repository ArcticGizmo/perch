using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Perch.Ui;

namespace Perch.Ui;

/// <summary>
/// A small, self-contained context popover: a borderless, top-most, dark-themed list of clickable
/// items. Used instead of a WinForms ContextMenuStrip, which wouldn't reliably display from the
/// transparent, top-most tool window that hosts the overlay. Dismissed by clicking an item, clicking
/// away (deactivation), or Esc.
/// </summary>
internal sealed class PopoverMenu : Form
{
    private static readonly Color BgColor     = Color.FromArgb(22,  22,  30);
    private static readonly Color BorderColor = Color.FromArgb(55,  55,  72);
    private static readonly Color FgColor     = Color.FromArgb(225, 225, 235);
    private static readonly Color HoverColor  = Color.FromArgb(40,  40,  58);

    private const int Corner = 8;
    private const int ItemH  = 30;
    private const int PadX   = 14;
    private const int PadY   = 6;
    private const int MinW   = 150;

    private readonly (string Label, Action OnClick)[] _items;
    private int _hover = -1;

    public PopoverMenu(IReadOnlyList<(string Label, Action OnClick)> items)
    {
        _items = items.ToArray();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        DoubleBuffered  = true;
        KeyPreview      = true;
        BackColor       = BgColor;
        StartPosition   = FormStartPosition.Manual;

        int w = MinW;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        using (var font = MenuFont())
            foreach (var (label, _) in _items)
                w = Math.Max(w, PadX * 2 + (int)Math.Ceiling(g.MeasureString(label, font).Width));

        ClientSize = new Size(w, PadY * 2 + _items.Length * ItemH);
    }

    private static Font MenuFont() => new("Segoe UI", 9f, GraphicsUnit.Point);

    /// <summary>Shows the popover at a screen point, nudged to stay fully on its monitor.</summary>
    public void ShowAt(Point screenPt)
    {
        var wa = Screen.FromPoint(screenPt).WorkingArea;
        int x = Math.Clamp(screenPt.X, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = Math.Clamp(screenPt.Y, wa.Top,  Math.Max(wa.Top,  wa.Bottom - Height));
        Location = new Point(x, y);
        Show();
        Activate();
    }

    private Rectangle ItemRect(int i) => new(0, PadY + i * ItemH, ClientSize.Width, ItemH);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using (var path = PaintKit.RoundedRect(bounds, Corner))
        {
            using var bg = new SolidBrush(BgColor);
            g.FillPath(bg, path);
            using var pen = new Pen(BorderColor, 1.5f);
            g.DrawPath(pen, path);
        }

        using var font = MenuFont();
        using var fg   = new SolidBrush(FgColor);
        var fmt = new StringFormat { LineAlignment = StringAlignment.Center };

        for (int i = 0; i < _items.Length; i++)
        {
            var r = ItemRect(i);
            if (i == _hover)
            {
                using var hover = new SolidBrush(HoverColor);
                using var hp = PaintKit.RoundedRect(new Rectangle(r.X + 4, r.Y, r.Width - 8, r.Height), 5);
                g.FillPath(hover, hp);
            }
            g.DrawString(_items[i].Label, font, fg,
                new RectangleF(r.X + PadX, r.Y, r.Width - PadX * 2, r.Height), fmt);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = -1;
        for (int i = 0; i < _items.Length; i++)
            if (ItemRect(i).Contains(e.Location)) { idx = i; break; }

        if (idx != _hover)
        {
            _hover = idx;
            Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            for (int i = 0; i < _items.Length; i++)
                if (ItemRect(i).Contains(e.Location))
                {
                    var action = _items[i].OnClick;
                    Close();
                    action();   // run after closing so the popover is gone before any follow-on UI
                    return;
                }
        base.OnMouseClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // Clicking anywhere outside the popover dismisses it.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }
}
