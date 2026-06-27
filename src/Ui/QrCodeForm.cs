using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Perch.Ui;
using QRCoder;

namespace Perch.Ui;

/// <summary>
/// A small always-on-top card, centered on screen, showing the remote-control deep-link QR code for
/// one session. Encodes https://claude.ai/code/{bridgeSessionId} — scanning it from the Claude mobile
/// app joins the session, mirroring what /remote-control surfaces in the terminal. The link is also
/// clickable (opens in the default browser) and copyable. Dismissed via the ✕ glyph, the Close
/// button, Esc, or by clicking away (deactivation).
/// </summary>
internal sealed class QrCodeForm : Form
{
    // ── Palette (mirrors OverlayForm so the popup feels part of the same app) ──
    private static readonly Color BgColor     = Color.FromArgb(15,  15,  20);
    private static readonly Color BorderColor = Color.FromArgb(45,  45,  60);
    private static readonly Color FgColor     = Color.FromArgb(225, 225, 235);
    private static readonly Color MutedColor  = Color.FromArgb(110, 110, 130);
    private static readonly Color RemoteColor = Color.FromArgb(96,  165, 250);
    private static readonly Color BtnColor    = Color.FromArgb(30,  30,  44);
    private static readonly Color BtnHoverCol = Color.FromArgb(40,  40,  58);

    // ── Layout ─────────────────────────────────────────────────────────────────
    private const int Corner  = 12;
    private const int Pad     = 22;
    private const int QrSize  = 240;  // on-screen size of the QR square, in px
    private const int QrQuiet = 14;   // white margin around the code inside its card
    private const int Gap     = 14;
    private const int TitleH  = 24;
    private const int UrlH    = 18;
    private const int BtnH    = 32;
    private const int BtnW    = 96;
    private const int BtnGap  = 10;

    private readonly Bitmap _qr;
    private readonly string _title;
    private readonly string _url;

    private Rectangle _cardRect;
    private Rectangle _closeIconRect;
    private Rectangle _urlRect;
    private Rectangle _copyBtnRect;
    private Rectangle _closeBtnRect;

    private bool _closeIconHover;
    private bool _urlHover;
    private bool _copyBtnHover;
    private bool _closeBtnHover;

    // Briefly flips the Copy button's label to "Copied!" after a successful copy.
    private bool _copied;
    private readonly System.Windows.Forms.Timer _copiedTimer;

    public QrCodeForm(string title, string url)
    {
        _title = title;
        _url   = url;
        _qr    = RenderQr(url);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        DoubleBuffered  = true;
        KeyPreview      = true;
        BackColor       = BgColor;
        StartPosition   = FormStartPosition.Manual;

        _copiedTimer = new System.Windows.Forms.Timer { Interval = 1300 };
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); _copied = false; Invalidate(); };

        int cardSide = QrSize + QrQuiet * 2;

        // Width must also accommodate the (potentially long) URL line — measure it against an
        // off-screen surface since the handle doesn't exist yet.
        float urlWidth;
        using (var probe = Graphics.FromImage(_qr))
        using (var urlFont = new Font("Segoe UI", 8f, GraphicsUnit.Point))
            urlWidth = probe.MeasureString(_url, urlFont).Width;

        int urlW     = (int)Math.Ceiling(urlWidth);
        int contentW = Math.Max(cardSide, urlW);
        int w = Pad * 2 + contentW;
        int h = Pad + TitleH + Gap + cardSide + Gap + UrlH + Gap + BtnH + Pad;
        ClientSize = new Size(w, h);

        int cardX = (w - cardSide) / 2;
        int cardY = Pad + TitleH + Gap;
        _cardRect = new Rectangle(cardX, cardY, cardSide, cardSide);

        _closeIconRect = new Rectangle(w - Pad - 16, Pad - 2, 16, 16);

        int urlTop = _cardRect.Bottom + Gap;
        _urlRect = new Rectangle((w - urlW) / 2, urlTop, urlW, UrlH);

        // Two buttons centered as a pair: Copy link | Close.
        int pairW  = BtnW * 2 + BtnGap;
        int startX = (w - pairW) / 2;
        int btnY   = h - Pad - BtnH;
        _copyBtnRect  = new Rectangle(startX,                btnY, BtnW, BtnH);
        _closeBtnRect = new Rectangle(startX + BtnW + BtnGap, btnY, BtnW, BtnH);
    }

    /// <summary>Centers the card on the given screen's working area.</summary>
    public void CenterOn(Screen screen)
    {
        var wa = screen.WorkingArea;
        Location = new Point(
            wa.X + (wa.Width  - Width)  / 2,
            wa.Y + (wa.Height - Height) / 2);
    }

    // Renders the QR as a crisp black-on-white bitmap. We draw the module matrix ourselves (rather
    // than QRCoder's System.Drawing renderer) to avoid the extra drawing dependency and to control
    // the exact pixel size. The matrix already carries QRCoder's 4-module quiet zone.
    private static Bitmap RenderQr(string url)
    {
        using var generator = new QRCodeGenerator();
        var data    = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var matrix  = data.ModuleMatrix;
        int modules = matrix.Count;
        int scale   = Math.Max(2, QrSize / modules);
        int size    = scale * modules;

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var black = new SolidBrush(Color.Black);
        for (int y = 0; y < modules; y++)
            for (int x = 0; x < modules; x++)
                if (matrix[y][x])
                    g.FillRectangle(black, x * scale, y * scale, scale, scale);
        return bmp;
    }

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

        using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold,      GraphicsUnit.Point);
        using var urlFont   = new Font("Segoe UI", 8f,  FontStyle.Underline, GraphicsUnit.Point);
        using var iconFont  = new Font("Segoe UI", 9f,                       GraphicsUnit.Point);
        using var btnFont   = new Font("Segoe UI", 8.5f,                     GraphicsUnit.Point);

        var center = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        // Title (project name).
        using (var fg = new SolidBrush(FgColor))
            g.DrawString(_title, titleFont, fg,
                new RectangleF(Pad, Pad, ClientSize.Width - Pad * 2, TitleH), center);

        // White card holding the QR, with the code centered inside its quiet-zone margin.
        using (var cardPath = PaintKit.RoundedRect(_cardRect, 8))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, cardPath);

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode   = PixelOffsetMode.Half;
        g.DrawImage(_qr,
            new Rectangle(_cardRect.X + QrQuiet, _cardRect.Y + QrQuiet, QrSize, QrSize));
        g.PixelOffsetMode = PixelOffsetMode.Default;

        // URL line — an underlined link that opens in the browser, brightening on hover.
        using (var urlBrush = new SolidBrush(_urlHover ? FgColor : RemoteColor))
            g.DrawString(_url, urlFont, urlBrush, _urlRect, center);

        // ✕ close glyph, top-right.
        using (var iconBrush = new SolidBrush(_closeIconHover ? FgColor : MutedColor))
            g.DrawString("✕", iconFont, iconBrush, _closeIconRect, center);

        // Buttons.
        DrawButton(g, _copyBtnRect, _copied ? "Copied!" : "Copy link", _copyBtnHover, btnFont, center);
        DrawButton(g, _closeBtnRect, "Close", _closeBtnHover, btnFont, center);
    }

    private static void DrawButton(Graphics g, Rectangle r, string text, bool hover, Font font, StringFormat fmt)
    {
        using (var path = PaintKit.RoundedRect(r, 6))
        {
            using var bg = new SolidBrush(hover ? BtnHoverCol : BtnColor);
            g.FillPath(bg, path);
            using var border = new Pen(BorderColor, 1f);
            g.DrawPath(border, path);
        }
        using var textBrush = new SolidBrush(hover ? FgColor : RemoteColor);
        g.DrawString(text, font, textBrush, r, fmt);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool icon = _closeIconRect.Contains(e.Location);
        bool url  = _urlRect.Contains(e.Location);
        bool copy = _copyBtnRect.Contains(e.Location);
        bool btn  = _closeBtnRect.Contains(e.Location);

        if (icon != _closeIconHover || url != _urlHover || copy != _copyBtnHover || btn != _closeBtnHover)
        {
            _closeIconHover = icon;
            _urlHover       = url;
            _copyBtnHover   = copy;
            _closeBtnHover  = btn;
            Cursor = (icon || url || copy || btn) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_closeIconRect.Contains(e.Location) || _closeBtnRect.Contains(e.Location))
                Close();
            else if (_urlRect.Contains(e.Location))
                OpenLink();
            else if (_copyBtnRect.Contains(e.Location))
                CopyLink();
        }
        base.OnMouseClick(e);
    }

    private void OpenLink()
    {
        try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — nothing useful to do but stay open */ }
    }

    private void CopyLink()
    {
        try
        {
            Clipboard.SetText(_url);
            _copied = true;
            _copiedTimer.Stop();
            _copiedTimer.Start();
            Invalidate();
        }
        catch { /* clipboard contention — leave the label unchanged */ }
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

    // Clicking elsewhere (the overlay, another window) dismisses the popup.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copiedTimer.Dispose();
            _qr.Dispose();
        }
        base.Dispose(disposing);
    }
}
