using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Perch.Ui;

/// <summary>
/// A borderless reveal card that shows a rendered "Perch Wrapped" poster scaled to fit, with buttons to
/// copy it to the clipboard or save it as a PNG. Modal (shown via <c>ShowDialog</c> from the stats
/// window); dismissed with the ✕ glyph, the Close button, or Esc. Owns the poster bitmap it is handed
/// and disposes it with the form.
/// </summary>
internal sealed class WrappedForm : Form
{
    private static readonly Color BgColor     = Color.FromArgb(15, 15, 22);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 90);

    private const int Pad     = 22;
    private const int Corner  = 16;
    private const int BtnH    = 38;
    private const int BtnGap  = 10;
    private const int GapAbove= 18;

    private readonly Bitmap _poster;
    private readonly string _suggestedName;
    private Rectangle _posterRect;
    private Rectangle _closeIconRect;
    private bool _closeIconHover;

    private readonly Button _copyBtn;
    private readonly Button _saveBtn;
    private readonly Button _closeBtn;
    private readonly System.Windows.Forms.Timer _copiedTimer;

    public WrappedForm(Bitmap poster, string suggestedName)
    {
        _poster = poster;
        _suggestedName = suggestedName;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        DoubleBuffered  = true;
        KeyPreview      = true;
        BackColor       = BgColor;
        StartPosition   = FormStartPosition.Manual;

        // Scale the poster to fit comfortably within the working area, preserving its 2:3 aspect.
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int previewH = Math.Min(WrappedRenderer.PosterHeight, wa.Height - 220);
        previewH = Math.Max(600, previewH);
        int previewW = previewH * WrappedRenderer.PosterWidth / WrappedRenderer.PosterHeight;

        int w = previewW + Pad * 2;
        int h = Pad + previewH + GapAbove + BtnH + Pad;
        ClientSize = new Size(w, h);
        Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);

        _posterRect    = new Rectangle(Pad, Pad, previewW, previewH);
        _closeIconRect = new Rectangle(w - 30, 10, 18, 18);

        _copyBtn  = MakeButton("Copy image", primary: true);
        _saveBtn  = MakeButton("Save PNG…", primary: false);
        _closeBtn = MakeButton("Close", primary: false);

        _copiedTimer = new System.Windows.Forms.Timer { Interval = 1300 };
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); _copyBtn.Text = "Copy image"; };

        _copyBtn.Click  += (_, _) => CopyImage();
        _saveBtn.Click  += (_, _) => SaveImage();
        _closeBtn.Click += (_, _) => Close();
        Controls.Add(_copyBtn);
        Controls.Add(_saveBtn);
        Controls.Add(_closeBtn);
        LayoutButtons();

        Region = new Region(PaintKit.RoundedRect(new Rectangle(0, 0, w, h), Corner));
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = ThemedControls.FlatButton(text);
        b.AutoSize = false;
        b.Height = BtnH;
        b.Width = 132;
        b.TabStop = false;
        b.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        if (primary)
            ThemedControls.StyleToggle(b, on: true);
        return b;
    }

    private void LayoutButtons()
    {
        int totalW = _copyBtn.Width + _saveBtn.Width + _closeBtn.Width + BtnGap * 2;
        int x = (ClientSize.Width - totalW) / 2;
        int y = _posterRect.Bottom + GapAbove;
        _copyBtn.SetBounds(x, y, _copyBtn.Width, BtnH); x += _copyBtn.Width + BtnGap;
        _saveBtn.SetBounds(x, y, _saveBtn.Width, BtnH); x += _saveBtn.Width + BtnGap;
        _closeBtn.SetBounds(x, y, _closeBtn.Width, BtnH);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var outline = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using (var path = PaintKit.RoundedRect(outline, Corner))
        using (var pen = new Pen(BorderColor, 1.5f))
            g.DrawPath(pen, path);

        // The poster, with its own rounded corners so it sits neatly on the card.
        using (var clip = PaintKit.RoundedRect(_posterRect, 12))
        {
            var saved = g.Clip;
            g.SetClip(clip, CombineMode.Replace);
            g.DrawImage(_poster, _posterRect);
            g.Clip = saved;
        }

        using var iconFont = new Font("Segoe UI", 10f, GraphicsUnit.Point);
        using var iconBrush = new SolidBrush(_closeIconHover ? Color.White : Color.FromArgb(150, 150, 170));
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("✕", iconFont, iconBrush, _closeIconRect, fmt);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool icon = _closeIconRect.Contains(e.Location);
        if (icon != _closeIconHover)
        {
            _closeIconHover = icon;
            Cursor = icon ? Cursors.Hand : Cursors.Default;
            Invalidate(_closeIconRect);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _closeIconRect.Contains(e.Location))
            Close();
        base.OnMouseClick(e);
    }

    private void CopyImage()
    {
        try
        {
            Clipboard.SetImage(_poster);
            _copyBtn.Text = "Copied!";
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }
        catch { /* clipboard contention — leave the label unchanged */ }
    }

    private void SaveImage()
    {
        using var dlg = new SaveFileDialog
        {
            Title    = "Save your Perch Wrapped",
            Filter   = "PNG image (*.png)|*.png",
            FileName = _suggestedName,
            DefaultExt = "png",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try { _poster.Save(dlg.FileName, ImageFormat.Png); }
            catch { /* disk full / permission — nothing useful to do but stay open */ }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copiedTimer.Dispose();
            _copyBtn.Font.Dispose();
            _saveBtn.Font.Dispose();
            _closeBtn.Font.Dispose();
            _poster.Dispose();
        }
        base.Dispose(disposing);
    }
}
