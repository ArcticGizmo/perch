using System.Drawing.Drawing2D;
using Perch.Data;

namespace Perch.Ui;

/// <summary>
/// The daily "flight path": a horizontal Gantt of one day, one lane per session, with each lane's time
/// coloured by what the session was doing — engaged (accent), waiting on the human (amber), or spinning
/// on erroring tools (red); blank lane is idle / walked-away time. A visual sibling to the Stats window,
/// built from the same transcript gaps active-time uses (see <see cref="FlightPathService"/>).
///
/// Owner-drawn onto a scrollable panel through a single <see cref="DrawTimeline"/> routine that both
/// measures (Graphics null) and paints, so the measured height and painted layout can never drift. The
/// report is computed off the UI thread, mirroring <see cref="StatsForm"/>. The toolbar steps between
/// days (‹ / ›, and a Today button); the ← / → arrow keys do the same.
/// </summary>
internal sealed class FlightPathForm : Form
{
    private static readonly Color BodyBg = Color.FromArgb(18, 18, 24);

    private readonly Panel _toolbar;
    private readonly Panel _scroll;
    private readonly ContentPanel _content;
    private readonly Button _prevButton;
    private readonly Button _nextButton;
    private readonly Button _todayButton;
    private readonly Label _dateLabel;

    private readonly Font _h1Font    = new("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _bodyFont   = new("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _laneFont   = new("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _labelFont  = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Bitmap? _icon = EmbeddedResources.LoadBitmap("Perch.icon.png");

    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private FlightPathReport? _report;
    private bool _loading = true;

    public FlightPathForm()
    {
        Text          = "Flight path";
        BackColor     = BodyBg;
        ForeColor     = Theme.Fg;
        StartPosition = FormStartPosition.Manual;
        MinimumSize   = new Size(620, 460);
        DoubleBuffered = true;
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Max(MinimumSize.Width, (int)(wa.Width * 0.5));
        int h = Math.Max(MinimumSize.Height, (int)(wa.Height * 0.7));
        Size = new Size(w, h);
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

        _content = new ContentPanel(this) { Location = Point.Empty };
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BodyBg };
        _scroll.Controls.Add(_content);
        _scroll.Resize += (_, _) => Relayout();
        Controls.Add(_scroll);                       // fill added first, toolbar docks above it

        _toolbar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.FormBg };
        _prevButton = NavButton("‹", () => StepDay(-1));
        _nextButton = NavButton("›", () => StepDay(+1));
        _todayButton = ThemedControls.FlatButton("Today");
        _todayButton.Height = 28;
        _todayButton.Width  = 68;
        _todayButton.TabStop = false;
        _todayButton.Font   = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _todayButton.Click += (_, _) => GoToDay(DateOnly.FromDateTime(DateTime.Now));
        _dateLabel = new Label
        {
            AutoSize  = false,
            Height    = 28,
            Width     = 220,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
        };
        _toolbar.Controls.Add(_prevButton);
        _toolbar.Controls.Add(_nextButton);
        _toolbar.Controls.Add(_dateLabel);
        _toolbar.Controls.Add(_todayButton);
        _toolbar.Resize += (_, _) => LayoutToolbar();
        Controls.Add(_toolbar);

        KeyPreview = true;
    }

    private Button NavButton(string glyph, Action onClick)
    {
        var b = ThemedControls.FlatButton(glyph);
        b.Height  = 28;
        b.Width   = 36;
        b.TabStop = false;
        b.Font    = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point);
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.UseDarkScrollBars(_scroll.Handle);
        LayoutToolbar();
        // The owner (WindowHost refresh) kicks the first load on open.
    }

    /// <summary>Recomputes the current day's flight path off the UI thread, then repaints. Safe to call
    /// repeatedly (each open, or when the day changes).</summary>
    public void RefreshPath()
    {
        _loading = true;
        UpdateNavState();
        Relayout();

        var day = _day;
        UiDispatch.RunThenPost(this,
            () => FlightPathService.ForDay(day),
            report =>
            {
                _report = report;
                _loading = false;
                Relayout();
            },
            FlightPathReport.Empty(day));
    }

    private void StepDay(int delta) => GoToDay(_day.AddDays(delta));

    private void GoToDay(DateOnly day)
    {
        // Never step past today — there is no future to show.
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (day > today) day = today;
        if (day == _day) return;
        _day = day;
        RefreshPath();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); e.Handled = true; break;
            case Keys.Left:   StepDay(-1); e.Handled = true; break;
            case Keys.Right:  StepDay(+1); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    // ── Toolbar ────────────────────────────────────────────────────────────────────
    private void LayoutToolbar()
    {
        const int pad = 12, gap = 6;
        int y = (_toolbar.Height - 28) / 2;
        int x = pad;
        _prevButton.SetBounds(x, y, _prevButton.Width, 28);
        x += _prevButton.Width + gap;
        _nextButton.SetBounds(x, y, _nextButton.Width, 28);
        x += _nextButton.Width + gap + 4;
        _dateLabel.SetBounds(x, y, _dateLabel.Width, 28);
        _todayButton.SetBounds(_toolbar.Width - pad - _todayButton.Width, y, _todayButton.Width, 28);
    }

    private void UpdateNavState()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _dateLabel.Text = _day == today
            ? "Today"
            : _day.Year == today.Year ? _day.ToString("dddd, MMM d") : _day.ToString("MMM d, yyyy");
        _nextButton.Enabled = _day < today;
        _todayButton.Enabled = _day != today;
    }

    // ── Layout ───────────────────────────────────────────────────────────────────
    private void Relayout()
    {
        if (!IsHandleCreated) return;
        int vw = Math.Max(MinimumSize.Width, _scroll.ClientSize.Width);
        int h = DrawTimeline(null, vw);
        if (h > _scroll.ClientSize.Height)
        {
            vw -= SystemInformation.VerticalScrollBarWidth;
            h = DrawTimeline(null, vw);
        }
        _content.Size = new Size(vw, h);
        _content.Invalidate();
    }

    // ── Timeline rendering ─────────────────────────────────────────────────────────
    private const int Pad = 22;
    private const int GutterW = 168;   // left column: project name + branch/duration
    private const int RowH = 42;       // per-lane row height
    private const int TrackH = 18;     // the coloured bar's height within a row
    private const int AxisH = 20;      // hour-label strip above the lanes

    // Single source of layout: g == null advances/measures only; otherwise it paints. Returns the total
    // content height. (See CLAUDE.md: measure-or-paint in one routine so the two never drift.)
    private int DrawTimeline(Graphics? g, int width)
    {
        if (g != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        int y = Pad;
        int x = Pad;
        int innerW = width - Pad * 2;

        Draw(g, "Flight path", _h1Font, Theme.Title, x, y);
        y += 30;

        if (_loading)
        {
            Draw(g, "Loading…", _bodyFont, Theme.Muted, x, y);
            return y + 40;
        }

        var report = _report ?? FlightPathReport.Empty(_day);
        if (report.IsEmpty)
        {
            bool isToday = _day == DateOnly.FromDateTime(DateTime.Now);
            Draw(g, isToday ? "No sessions recorded yet today." : "No sessions recorded on this day.",
                _bodyFont, Theme.Muted, x, y);
            return y + 40;
        }

        // Legend.
        y = Legend(g, x, y);
        y += 6;

        // Time axis geometry.
        int trackX = x + GutterW;
        int trackW = Math.Max(1, innerW - GutterW);
        double totalSeconds = Math.Max(1, (report.WindowEnd - report.WindowStart).TotalSeconds);
        double XOf(DateTime t) => trackX + trackW * Math.Clamp((t - report.WindowStart).TotalSeconds / totalSeconds, 0, 1);

        // Hour labels + gridlines. Step widens as the day gets longer so labels never collide.
        int hours = (int)Math.Ceiling((report.WindowEnd - report.WindowStart).TotalHours);
        int step = hours <= 12 ? 1 : hours <= 24 ? 2 : 3;
        int axisY = y;
        int lanesTop = y + AxisH;
        int lanesBottom = lanesTop + report.Lanes.Count * RowH;
        if (g != null)
        {
            using var grid = new Pen(Theme.Border);
            for (var t = report.WindowStart; t <= report.WindowEnd; t = t.AddHours(step))
            {
                int gx = (int)XOf(t);
                g.DrawLine(grid, gx, lanesTop, gx, lanesBottom);
                TextRenderer.DrawText(g, t.ToString("HH:mm"), _labelFont, new Point(gx + 3, axisY),
                    Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            // "Now" marker when looking at today and the moment falls inside the window.
            var now = DateTime.Now;
            if (_day == DateOnly.FromDateTime(now) && now >= report.WindowStart && now <= report.WindowEnd)
            {
                int nx = (int)XOf(now);
                using var pen = new Pen(Theme.Brand) { DashStyle = DashStyle.Dash };
                g.DrawLine(pen, nx, lanesTop, nx, lanesBottom);
            }
        }

        // Lanes.
        int laneY = lanesTop;
        foreach (var lane in report.Lanes)
        {
            DrawLane(g, lane, x, laneY, trackX, trackW, XOf);
            laneY += RowH;
        }

        return lanesBottom + Pad;
    }

    private int Legend(Graphics? g, int x, int y)
    {
        if (g == null)
            return y + 22;
        int cx = x;
        cx = LegendChip(g, cx, y, Theme.Accent, "Active");
        cx = LegendChip(g, cx, y, Theme.Orange, "Waiting on you");
        LegendChip(g, cx, y, Theme.Red, "Stuck");
        return y + 22;
    }

    private int LegendChip(Graphics g, int x, int y, Color color, string label)
    {
        const int dot = 11;
        PaintKit.FillRoundedRect(g, color, new Rectangle(x, y + 2, dot, dot), 3);
        int textX = x + dot + 6;
        TextRenderer.DrawText(g, label, _labelFont, new Point(textX, y),
            Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return textX + TextRenderer.MeasureText(label, _labelFont).Width + 18;
    }

    private void DrawLane(Graphics? g, FlightLane lane, int x, int y, int trackX, int trackW, Func<DateTime, double> XOf)
    {
        if (g == null)
            return;

        int rowMid = y + RowH / 2;
        int trackY = rowMid - TrackH / 2;

        // Left gutter: project (bold) over a muted "branch · active time" line.
        TextRenderer.DrawText(g, lane.Project, _laneFont, new Rectangle(x, y + 4, GutterW - 10, 20), Theme.Title,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        string sub = lane.Branch.Length > 0
            ? $"{lane.Branch} · {StatsFormat.Duration(lane.ActiveTime)}"
            : StatsFormat.Duration(lane.ActiveTime);
        TextRenderer.DrawText(g, sub, _labelFont, new Rectangle(x, y + 22, GutterW - 10, 16), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        // Track background (idle time reads as this faint pill), then the coloured segments on top.
        using (var track = new SolidBrush(Theme.Track))
            PaintKit.FillRoundedBar(g, track, trackX, trackY, trackW, TrackH);
        foreach (var seg in lane.Segments)
        {
            int x0 = (int)XOf(seg.Start);
            int x1 = (int)XOf(seg.End);
            int w = Math.Max(3, x1 - x0);           // keep slivers visible
            PaintKit.FillRoundedRect(g, SegmentColor(seg.State), new Rectangle(x0, trackY, w, TrackH), 3);
        }
    }

    private static Color SegmentColor(FlightState state) => state switch
    {
        FlightState.Waiting => Theme.Orange,
        FlightState.Stuck   => Theme.Red,
        _                   => Theme.Accent,
    };

    private void Draw(Graphics? g, string text, Font font, Color color, int x, int y)
    {
        if (g != null)
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _prevButton.Font.Dispose();
            _nextButton.Font.Dispose();
            _todayButton.Font.Dispose();
            _dateLabel.Font.Dispose();
            _h1Font.Dispose();
            _bodyFont.Dispose();
            _laneFont.Dispose();
            _labelFont.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The scrollable surface the timeline is painted onto. Double-buffered so the custom paint
    /// doesn't flicker; it forwards painting back to the owning form.</summary>
    private sealed class ContentPanel : Panel
    {
        private readonly FlightPathForm _owner;
        public ContentPanel(FlightPathForm owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            BackColor = BodyBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _owner.DrawTimeline(e.Graphics, Width);
        }
    }
}
