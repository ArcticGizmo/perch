using System.Drawing.Drawing2D;
using Perch.Ui;

using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// A dedicated window showing Claude Code session statistics. A scope switch across the top selects
/// Today / Last 7 days / Last 30 days / All time; each view shows the headline figures (sessions,
/// active time, prompts, tool calls), token totals with an equivalent API cost, an hourly activity
/// histogram, and per-project / tool-mix / model / branch breakdowns. The range views add a daily
/// activity trend and streak/record extras. All figures come from <see cref="SessionStatsService"/>,
/// which derives them from the transcripts on disk — so the view is retroactive.
///
/// The dashboard is owner-drawn onto a scrollable panel: a single <see cref="DrawDashboard"/> routine
/// both measures (Graphics null) and paints, so layout never drifts between the two passes. Reports are
/// computed off the UI thread, mirroring <see cref="HistoryViewerForm"/>'s loading pattern.
/// </summary>
internal sealed class StatsForm : Form
{
    private static readonly Color BodyBg = Color.FromArgb(18, 18, 24);
    private static readonly Color CardBg = Color.FromArgb(30, 30, 42);

    private enum Scope { Today, Week, Month, AllTime }

    private readonly Panel _toolbar;
    private readonly Panel _scroll;
    private readonly ContentPanel _content;
    private readonly Dictionary<Scope, Button> _scopeButtons = new();

    private readonly Font _bigFont    = new("Segoe UI Semibold", 21f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _h1Font     = new("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _h2Font     = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _bodyFont   = new("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _labelFont  = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Bitmap? _icon = EmbeddedResources.LoadBitmap("Perch.icon.png");

    private readonly AppSettings _settings;
    private Scope _scope = Scope.Today;
    private StatsReport? _report;       // the totals to render
    private RangeReport? _range;        // non-null for the range scopes (adds trend + records)
    private bool _loading = true;

    public StatsForm(AppSettings settings)
    {
        _settings = settings;
        Text          = "Session stats";
        BackColor     = BodyBg;
        ForeColor     = Theme.Fg;
        StartPosition = FormStartPosition.Manual;
        MinimumSize   = new Size(560, 520);
        DoubleBuffered = true;
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Max(MinimumSize.Width, (int)(wa.Width * 0.36));
        int h = Math.Max(MinimumSize.Height, (int)(wa.Height * 0.8));
        Size = new Size(w, h);
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

        _content = new ContentPanel(this) { Location = Point.Empty };
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BodyBg };
        _scroll.Controls.Add(_content);
        _scroll.Resize += (_, _) => Relayout();
        Controls.Add(_scroll);                       // fill added first, toolbar docks above it

        _toolbar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.FormBg };
        AddScopeButton("Today", Scope.Today);
        AddScopeButton("7 days", Scope.Week);
        AddScopeButton("30 days", Scope.Month);
        AddScopeButton("All time", Scope.AllTime);
        _toolbar.Resize += (_, _) => LayoutToolbar();
        Controls.Add(_toolbar);

        KeyPreview = true;
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
        // The owner (WindowHost refresh) kicks the first stats load on open.
    }

    /// <summary>Recomputes the current scope's report off the UI thread, then repaints. Safe to call
    /// repeatedly (e.g. each time the window is re-opened, or when the scope changes).</summary>
    public void RefreshStats()
    {
        _loading = true;
        UpdateScopeButtons();
        Relayout();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var scope = _scope;
        UiDispatch.RunThenPost<(StatsReport report, RangeReport? range)>(this,
            () => LoadScope(scope, today),
            result =>
            {
                _report = result.report;
                _range = result.range;
                _loading = false;
                Relayout();
            },
            (StatsReport.Empty(today), null));
    }

    private static (StatsReport report, RangeReport? range) LoadScope(Scope scope, DateOnly today)
    {
        switch (scope)
        {
            case Scope.Week:
            {
                var r = SessionStatsService.ReportForRange(today.AddDays(-6), today, "Last 7 days");
                return (r.Totals, r);
            }
            case Scope.Month:
            {
                var r = SessionStatsService.ReportForRange(today.AddDays(-29), today, "Last 30 days");
                return (r.Totals, r);
            }
            case Scope.AllTime:
            {
                var r = SessionStatsService.ReportAllTime(today);
                return (r.Totals, r);
            }
            default:
                return (SessionStatsService.ReportForDay(today), null);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // ── Scope toolbar ──────────────────────────────────────────────────────────────
    private void AddScopeButton(string text, Scope scope)
    {
        var b = ThemedControls.FlatButton(text);
        b.Height   = 28;
        b.Font     = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        b.TabStop  = false;
        b.AutoSize = false;
        b.Width    = 78;
        b.Click += (_, _) => { if (_scope != scope) { _scope = scope; RefreshStats(); } };
        _scopeButtons[scope] = b;
        _toolbar.Controls.Add(b);
    }

    private void LayoutToolbar()
    {
        const int pad = 12, gap = 6;
        int x = pad, y = (_toolbar.Height - 28) / 2;
        foreach (Scope s in new[] { Scope.Today, Scope.Week, Scope.Month, Scope.AllTime })
        {
            var b = _scopeButtons[s];
            b.SetBounds(x, y, b.Width, 28);
            x += b.Width + gap;
        }
    }

    private void UpdateScopeButtons()
    {
        foreach (var (scope, b) in _scopeButtons)
            ThemedControls.StyleToggle(b, scope == _scope);
    }

    // ── Layout ───────────────────────────────────────────────────────────────────
    private void Relayout()
    {
        if (!IsHandleCreated) return;
        int vw = Math.Max(MinimumSize.Width, _scroll.ClientSize.Width);
        int h = DrawDashboard(null, vw);
        if (h > _scroll.ClientSize.Height)
        {
            vw -= SystemInformation.VerticalScrollBarWidth;
            h = DrawDashboard(null, vw);
        }
        _content.Size = new Size(vw, h);
        _content.Invalidate();
    }

    // ── Dashboard rendering ────────────────────────────────────────────────────────
    private const int Pad = 22;
    private const int Gap = 12;

    // Single source of layout: when g is null this only advances the y cursor (a measure pass);
    // otherwise it paints. Returns the total content height.
    private int DrawDashboard(Graphics? g, int width)
    {
        if (g != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        int y = Pad;
        int x = Pad;
        int innerW = width - Pad * 2;

        // Title + subtitle
        string title = _range?.ScopeLabel ?? "Today";
        Draw(g, title, _h1Font, Theme.Title, x, y);
        var subtitle = Subtitle();
        if (subtitle.Length > 0)
            Draw(g, subtitle, _bodyFont, Theme.Muted, x + 4, y + 28);
        y += 58;

        if (_loading)
        {
            Draw(g, "Loading…", _bodyFont, Theme.Muted, x, y);
            return y + 40;
        }

        var report = _report ?? StatsReport.Empty(DateOnly.FromDateTime(DateTime.Now));

        if (report.SessionCount == 0)
        {
            Draw(g, "No sessions recorded in this range yet.", _bodyFont, Theme.Muted, x, y);
            return y + 40;
        }

        // Streak / records line (range scopes only)
        if (_range is { } rng)
        {
            if (rng.StreakDays is { } streak && streak > 0)
            {
                Draw(g, $"{streak}-day streak", _h2Font, Theme.Accent, x, y);
                y += 24;
            }
            var bits = new List<string> { $"{rng.ActiveDays} active {(rng.ActiveDays == 1 ? "day" : "days")}" };
            if (rng.BusiestDay is { } bd)
                bits.Add($"busiest {bd:MMM d} ({StatsFormat.Duration(rng.BusiestDayActive)})");
            if (rng.LongestSession > TimeSpan.Zero)
                bits.Add($"longest session {StatsFormat.Duration(rng.LongestSession)}");
            Draw(g, string.Join("  ·  ", bits), _bodyFont, Theme.Muted, x, y);
            y += 28;
        }

        // ── Headline stat cards ──
        var cards = new (string value, string label)[]
        {
            (report.SessionCount.ToString(),            report.SessionCount == 1 ? "session" : "sessions"),
            (StatsFormat.Duration(report.ActiveTime),   "active"),
            (report.Prompts.ToString(),                 report.Prompts == 1 ? "prompt" : "prompts"),
            (report.ToolCalls.ToString(),               "tool calls"),
        };
        // Derive the card height from the actual font line heights (not a hard-coded pixel value), so
        // the big numbers are never clipped at the bottom — see CLAUDE.md (text-clipping rule).
        int valueH = _bigFont.Height;
        int labelH = _labelFont.Height;
        const int cardPadV = 12, cardGapV = 2;
        int cardH = cardPadV + valueH + cardGapV + labelH + cardPadV;
        int cardW = (innerW - Gap * (cards.Length - 1)) / cards.Length;
        for (int i = 0; i < cards.Length; i++)
        {
            int cx = x + i * (cardW + Gap);
            if (g != null)
            {
                PaintKit.FillRoundedRect(g, CardBg, new Rectangle(cx, y, cardW, cardH), 8);
                DrawCentered(g, cards[i].value, _bigFont, Theme.Title, new Rectangle(cx, y + cardPadV, cardW, valueH));
                DrawCentered(g, cards[i].label, _labelFont, Theme.Muted,
                    new Rectangle(cx, y + cardPadV + valueH + cardGapV, cardW, labelH));
            }
        }
        y += cardH + 18;
        if (report.SubAgents > 0)
        {
            Draw(g, $"includes {report.SubAgents} sub-agent {(report.SubAgents == 1 ? "run" : "runs")}",
                _labelFont, Theme.Muted, x, y);
            y += 22;
        }

        // ── Daily trend (range scopes only) ──
        if (_range is { } rng2 && rng2.Trend.Count > 1)
            y = TrendHistogram(g, rng2.Trend, rng2.TrendLabel, x, y, innerW);

        // ── Tokens & cost ──
        y = SectionHeader(g, "Tokens & cost", x, y, innerW);
        var tk = report.Tokens;
        y = KeyValueRow(g, "Output",      StatsFormat.Tokens(tk.Output),     x, y, innerW);
        y = KeyValueRow(g, "Input",       StatsFormat.Tokens(tk.Input),      x, y, innerW);
        y = KeyValueRow(g, "Cache write", StatsFormat.Tokens(tk.CacheWrite), x, y, innerW);
        y = KeyValueRow(g, "Cache read",  StatsFormat.Tokens(tk.CacheRead),  x, y, innerW);
        y = KeyValueRow(g, "Total",       StatsFormat.Tokens(tk.Total),      x, y, innerW, bold: true);
        if (_settings.ShowEstimatedCost)
        {
            y += 6;
            string cost = report.EstimatedCost > 0
                ? $"≈ {StatsFormat.Cost(report.EstimatedCost)} equivalent API cost{(report.CostComplete ? "" : " (partial)")}"
                : "cost unavailable for these models";
            Draw(g, cost, _labelFont, Theme.Muted, x, y);
        }
        y += 26;

        // ── Hourly activity ──
        if (report.HourlyActiveSeconds.Any(s => s > 0))
            y = HourlyHistogram(g, report.HourlyActiveSeconds, x, y, innerW);

        // ── Per-project ──
        if (report.Projects.Count > 0)
            y = GroupSection(g, "By project", report.Projects, 8, x, y, innerW, Theme.Accent);

        // ── Per-branch (only worth showing when more than one branch appears) ──
        if (report.Branches.Count > 1)
            y = GroupSection(g, "By branch", report.Branches, 8, x, y, innerW, Color.FromArgb(167, 139, 250));

        // ── Tool mix ──
        if (report.Tools.Count > 0)
        {
            y = SectionHeader(g, "Tool mix", x, y, innerW);
            long maxTool = Math.Max(1, report.Tools.Max(t => (long)t.Count));
            foreach (var t in report.Tools.Take(12))
                y = BarRow(g, t.Tool, t.Count.ToString(), t.Count, maxTool, x, y, innerW, Theme.Green);
            y += 8;
        }

        // ── Model split ──
        if (report.Models.Count > 0)
        {
            y = SectionHeader(g, "By model", x, y, innerW);
            foreach (var m in report.Models)
            {
                string name = m.Model.StartsWith("claude-", StringComparison.Ordinal) ? m.Model["claude-".Length..] : m.Model;
                string right = m.Cost is { } c
                    ? $"{StatsFormat.Tokens(m.Tokens.Total)} · {StatsFormat.Cost(c)}"
                    : $"{StatsFormat.Tokens(m.Tokens.Total)} · —";
                y = KeyValueRow(g, name, right, x, y, innerW);
            }
            y += 4;
        }

        return y + Pad;
    }

    private string Subtitle()
    {
        if (_range is not { } r)
            return DateTime.Now.ToString("dddd, MMM d");
        if (_scope == Scope.AllTime)
            return r.FirstActiveDay is { } first ? $"since {first:MMM yyyy}" : "";
        if (r.Trend.Count > 0)
            return $"{r.Trend[0].Day:MMM d} – {r.Trend[^1].Day:MMM d}";
        return "";
    }

    // A labelled group section (project / branch): one bar per entry with active time + token count.
    private int GroupSection(Graphics? g, string title, IReadOnlyList<ProjectStat> items, int take,
        int x, int y, int innerW, Color color)
    {
        y = SectionHeader(g, title, x, y, innerW);
        long maxActive = Math.Max(1, items.Max(p => (long)p.ActiveTime.TotalSeconds));
        foreach (var p in items.Take(take))
        {
            string right = $"{StatsFormat.Duration(p.ActiveTime)} · {StatsFormat.Tokens(p.Tokens)}";
            y = BarRow(g, p.Project, right, (long)p.ActiveTime.TotalSeconds, maxActive, x, y, innerW, color);
        }
        return y + 8;
    }

    // ── Drawing helpers (all no-op when g is null, but advance the caller's y identically) ──
    private void Draw(Graphics? g, string text, Font font, Color color, int x, int y)
    {
        if (g != null)
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private static void DrawCentered(Graphics g, string text, Font font, Color color, Rectangle r) =>
        TextRenderer.DrawText(g, text, font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

    private int SectionHeader(Graphics? g, string title, int x, int y, int innerW)
    {
        Draw(g, title, _h2Font, Theme.Fg, x, y);
        if (g != null)
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, x, y + 24, x + innerW, y + 24);
        return y + 34;
    }

    private int KeyValueRow(Graphics? g, string key, string value, int x, int y, int innerW, bool bold = false)
    {
        var f = bold ? _h2Font : _bodyFont;
        Draw(g, key, _bodyFont, bold ? Theme.Fg : Theme.Muted, x, y);
        if (g != null)
            TextRenderer.DrawText(g, value, f, new Rectangle(x, y, innerW, 20), Theme.Fg,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return y + 24;
    }

    // A labelled horizontal bar: name on the left, a proportional bar filling the row, value at the right.
    private int BarRow(Graphics? g, string label, string right, long value, long max, int x, int y, int innerW, Color color)
    {
        const int rowH = 26;
        if (g != null)
        {
            var track = new Rectangle(x, y + 3, innerW, rowH - 8);
            PaintKit.FillRoundedRect(g, Theme.Track, track, 5);
            int barW = (int)(innerW * Math.Clamp(value / (double)max, 0, 1));
            if (barW > 4)
                PaintKit.FillRoundedRect(g, color, new Rectangle(x, y + 3, barW, rowH - 8), 5);

            TextRenderer.DrawText(g, label, _bodyFont, new Rectangle(x + 8, y, innerW - 100, rowH), Theme.Title,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, right, _bodyFont, new Rectangle(x, y, innerW - 8, rowH), Theme.Title,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        return y + rowH;
    }

    private int HourlyHistogram(Graphics? g, int[] hourly, int x, int y, int innerW)
    {
        y = SectionHeader(g, "Active by hour", x, y, innerW);
        const int areaH = 64;
        int max = Math.Max(1, hourly.Max());
        double cellW = innerW / 24.0;
        if (g != null)
        {
            for (int hr = 0; hr < 24; hr++)
            {
                int bx = x + (int)(hr * cellW);
                int bw = Math.Max(2, (int)cellW - 3);
                int bh = (int)(areaH * (hourly[hr] / (double)max));
                if (bh < 2 && hourly[hr] > 0) bh = 2;
                if (bh > 0)
                    PaintKit.FillRoundedRect(g, Theme.Accent, new Rectangle(bx, y + (areaH - bh), bw, bh), 2);
            }
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, x, y + areaH + 1, x + innerW, y + areaH + 1);
            foreach (int hr in new[] { 0, 6, 12, 18 })
                TextRenderer.DrawText(g, $"{hr:00}", _labelFont, new Point(x + (int)(hr * cellW), y + areaH + 4),
                    Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        return y + areaH + 24;
    }

    // Daily activity bars across the range (one bar per day, oldest → newest), with the first/last
    // date labelled under the baseline.
    private int TrendHistogram(Graphics? g, IReadOnlyList<DayPoint> trend, string label, int x, int y, int innerW)
    {
        y = SectionHeader(g, label, x, y, innerW);
        const int areaH = 56;
        long max = Math.Max(1, trend.Max(p => (long)p.Active.TotalSeconds));
        double cellW = innerW / (double)trend.Count;
        if (g != null)
        {
            for (int i = 0; i < trend.Count; i++)
            {
                int sec = (int)trend[i].Active.TotalSeconds;
                int bx = x + (int)(i * cellW);
                int bw = Math.Max(2, (int)cellW - 2);
                int bh = (int)(areaH * (sec / (double)max));
                if (bh < 2 && sec > 0) bh = 2;
                if (bh > 0)
                    PaintKit.FillRoundedRect(g, Theme.Accent, new Rectangle(bx, y + (areaH - bh), bw, bh), 2);
            }
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, x, y + areaH + 1, x + innerW, y + areaH + 1);
            TextRenderer.DrawText(g, $"{trend[0].Day:MMM d}", _labelFont, new Point(x, y + areaH + 4),
                Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, $"{trend[^1].Day:MMM d}", _labelFont,
                new Rectangle(x, y + areaH + 4, innerW, 16), Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        return y + areaH + 24;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in _scopeButtons.Values) b.Font.Dispose();
            _bigFont.Dispose();
            _h1Font.Dispose();
            _h2Font.Dispose();
            _bodyFont.Dispose();
            _labelFont.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The scrollable surface the dashboard is painted onto. Double-buffered so the custom
    /// paint doesn't flicker; it simply forwards painting back to the owning form.</summary>
    private sealed class ContentPanel : Panel
    {
        private readonly StatsForm _owner;
        public ContentPanel(StatsForm owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            BackColor = BodyBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _owner.DrawDashboard(e.Graphics, Width);
        }
    }
}
