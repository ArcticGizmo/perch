using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn session-stats dashboard — the Avalonia port of <c>StatsForm.DrawDashboard</c>. A
/// single <see cref="Draw"/> routine both measures (returns the content height when given a null
/// context) and paints, so the measured height and painted layout never drift. Hosted in a
/// <see cref="ScrollViewer"/> by <c>StatsWindow</c>, which owns the scope toolbar + off-thread loading.
/// </summary>
internal sealed class StatsDashboard : Control
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush CardBg = new SolidColorBrush(Color.FromRgb(30, 30, 42));
    private static readonly IBrush TitleBrush  = new SolidColorBrush(Palette.Title);
    private static readonly IBrush MutedBrush  = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush FgBrush     = new SolidColorBrush(Palette.Fg);
    private static readonly IBrush AccentBrush = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush GreenBrush  = new SolidColorBrush(Palette.Green);
    private static readonly IBrush TrackBrush  = new SolidColorBrush(Palette.Track);
    private static readonly IBrush BranchBrush = new SolidColorBrush(Color.FromRgb(167, 139, 250));
    private static readonly IPen   BorderPen   = new Pen(new SolidColorBrush(Palette.Border), 1);

    // px sizes ≈ the WinForms point sizes (pt × 96/72).
    private const double BigSize = 28, H1Size = 20, H2Size = 15, BodySize = 13, LabelSize = 11;
    private const double Pad = 22, Gap = 12;

    private readonly bool _showCost;
    private StatsReport? _report;
    private RangeReport? _range;
    private bool _loading = true;

    public StatsDashboard(bool showCost) => _showCost = showCost;

    public void SetLoading()
    {
        _loading = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetReport(StatsReport report, RangeReport? range)
    {
        _report = report;
        _range = range;
        _loading = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 560;
        return new Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(BodyBg), new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    // Single source of layout: advances the y cursor on both passes; paints only when ctx != null.
    private double Draw(DrawingContext? ctx, double width)
    {
        double y = Pad, x = Pad;
        double innerW = width - Pad * 2;

        string title = _range?.ScopeLabel ?? "Today";
        Text(ctx, title, H1Size, TitleBrush, x, y, FontWeight.Bold);
        var subtitle = Subtitle();
        if (subtitle.Length > 0) Text(ctx, subtitle, BodySize, MutedBrush, x + 4, y + 28);
        y += 58;

        if (_loading)
        {
            Text(ctx, "Loading…", BodySize, MutedBrush, x, y);
            return y + 40;
        }

        var report = _report ?? StatsReport.Empty(DateOnly.FromDateTime(DateTime.Now));
        if (report.SessionCount == 0)
        {
            Text(ctx, "No sessions recorded in this range yet.", BodySize, MutedBrush, x, y);
            return y + 40;
        }

        // Streak / records line (range scopes only).
        if (_range is { } rng)
        {
            if (rng.StreakDays is { } streak && streak > 0)
            {
                Text(ctx, $"{streak}-day streak", H2Size, AccentBrush, x, y, FontWeight.Bold);
                y += 24;
            }
            var bits = new List<string> { $"{rng.ActiveDays} active {(rng.ActiveDays == 1 ? "day" : "days")}" };
            if (rng.BusiestDay is { } bd) bits.Add($"busiest {bd:MMM d} ({StatsFormat.Duration(rng.BusiestDayActive)})");
            if (rng.LongestSession > TimeSpan.Zero) bits.Add($"longest session {StatsFormat.Duration(rng.LongestSession)}");
            Text(ctx, string.Join("  ·  ", bits), BodySize, MutedBrush, x, y);
            y += 28;
        }

        // Headline stat cards.
        var cards = new (string value, string label)[]
        {
            (report.SessionCount.ToString(), report.SessionCount == 1 ? "session" : "sessions"),
            (StatsFormat.Duration(report.ActiveTime), "active"),
            (report.Prompts.ToString(), report.Prompts == 1 ? "prompt" : "prompts"),
            (report.ToolCalls.ToString(), "tool calls"),
        };
        double valueH = OverlayDraw.Text("0", BigSize, TitleBrush, FontWeight.SemiBold).Height;
        double labelH = OverlayDraw.Text("x", LabelSize, MutedBrush).Height;
        const double cardPadV = 12, cardGapV = 2;
        double cardH = cardPadV + valueH + cardGapV + labelH + cardPadV;
        double cardW = (innerW - Gap * (cards.Length - 1)) / cards.Length;
        if (ctx != null)
        {
            for (int i = 0; i < cards.Length; i++)
            {
                double cx = x + i * (cardW + Gap);
                OverlayDraw.Panel(ctx, new Rect(cx, y, cardW, cardH), CardBg, null, 8);
                TextCentered(ctx, cards[i].value, BigSize, TitleBrush, new Rect(cx, y + cardPadV, cardW, valueH), FontWeight.SemiBold);
                TextCentered(ctx, cards[i].label, LabelSize, MutedBrush, new Rect(cx, y + cardPadV + valueH + cardGapV, cardW, labelH));
            }
        }
        y += cardH + 18;

        if (report.SubAgents > 0 || report.Teammates > 0)
        {
            var parts = new List<string>();
            if (report.SubAgents > 0) parts.Add($"{report.SubAgents} sub-agent {(report.SubAgents == 1 ? "run" : "runs")}");
            if (report.Teammates > 0) parts.Add($"{report.Teammates} teammate{(report.Teammates == 1 ? "" : "s")}");
            Text(ctx, "includes " + string.Join(" · ", parts), LabelSize, MutedBrush, x, y);
            y += 22;
        }

        // Daily trend (range scopes only).
        if (_range is { } rng2 && rng2.Trend.Count > 1)
            y = TrendHistogram(ctx, rng2.Trend, rng2.TrendLabel, x, y, innerW);

        // Tokens & cost.
        y = SectionHeader(ctx, "Tokens & cost", x, y, innerW);
        var tk = report.Tokens;
        y = KeyValueRow(ctx, "Output", StatsFormat.Tokens(tk.Output), x, y, innerW);
        y = KeyValueRow(ctx, "Input", StatsFormat.Tokens(tk.Input), x, y, innerW);
        y = KeyValueRow(ctx, "Cache write", StatsFormat.Tokens(tk.CacheWrite), x, y, innerW);
        y = KeyValueRow(ctx, "Cache read", StatsFormat.Tokens(tk.CacheRead), x, y, innerW);
        y = KeyValueRow(ctx, "Total", StatsFormat.Tokens(tk.Total), x, y, innerW, bold: true);
        if (report.TeammateTokens.Total > 0)
            y = KeyValueRow(ctx, "Teammate tokens", StatsFormat.Tokens(report.TeammateTokens.Total), x, y, innerW);
        if (_showCost)
        {
            y += 6;
            string cost = report.EstimatedCost > 0
                ? $"≈ {StatsFormat.Cost(report.EstimatedCost)} equivalent API cost{(report.CostComplete ? "" : " (partial)")}"
                : "cost unavailable for these models";
            Text(ctx, cost, LabelSize, MutedBrush, x, y);
        }
        y += 26;

        // Hourly activity.
        if (report.HourlyActiveSeconds.Any(s => s > 0))
            y = HourlyHistogram(ctx, report.HourlyActiveSeconds, x, y, innerW);

        // Per-project.
        if (report.Projects.Count > 0)
            y = GroupSection(ctx, "By project", report.Projects, 8, x, y, innerW, AccentBrush);

        // Per-branch (only when more than one branch appears).
        if (report.Branches.Count > 1)
            y = GroupSection(ctx, "By branch", report.Branches, 8, x, y, innerW, BranchBrush);

        // Tool mix.
        if (report.Tools.Count > 0)
        {
            y = SectionHeader(ctx, "Tool mix", x, y, innerW);
            long maxTool = Math.Max(1, report.Tools.Max(t => (long)t.Count));
            foreach (var t in report.Tools.Take(12))
                y = BarRow(ctx, t.Tool, t.Count.ToString(), t.Count, maxTool, x, y, innerW, GreenBrush);
            y += 8;
        }

        // Model split.
        if (report.Models.Count > 0)
        {
            y = SectionHeader(ctx, "By model", x, y, innerW);
            foreach (var m in report.Models)
            {
                string name = m.Model.StartsWith("claude-", StringComparison.Ordinal) ? m.Model["claude-".Length..] : m.Model;
                string right = m.Cost is { } c
                    ? $"{StatsFormat.Tokens(m.Tokens.Total)} · {StatsFormat.Cost(c)}"
                    : $"{StatsFormat.Tokens(m.Tokens.Total)} · —";
                y = KeyValueRow(ctx, name, right, x, y, innerW);
            }
            y += 4;
        }

        return y + Pad;
    }

    private string Subtitle()
    {
        if (_range is not { } r) return DateTime.Now.ToString("dddd, MMM d");
        if (_range.ScopeLabel.StartsWith("All", StringComparison.Ordinal))
            return r.FirstActiveDay is { } first ? $"since {first:MMM yyyy}" : "";
        if (r.Trend.Count > 0) return $"{r.Trend[0].Day:MMM d} – {r.Trend[^1].Day:MMM d}";
        return "";
    }

    private double GroupSection(DrawingContext? ctx, string title, IReadOnlyList<ProjectStat> items, int take,
        double x, double y, double innerW, IBrush color)
    {
        y = SectionHeader(ctx, title, x, y, innerW);
        long maxActive = Math.Max(1, items.Max(p => (long)p.ActiveTime.TotalSeconds));
        foreach (var p in items.Take(take))
        {
            string right = $"{StatsFormat.Duration(p.ActiveTime)} · {StatsFormat.Tokens(p.Tokens)}";
            y = BarRow(ctx, p.Project, right, (long)p.ActiveTime.TotalSeconds, maxActive, x, y, innerW, color);
        }
        return y + 8;
    }

    // ── Drawing helpers (no-op when ctx is null, but advance y identically) ──
    private static void Text(DrawingContext? ctx, string s, double size, IBrush brush, double x, double y,
        FontWeight weight = FontWeight.Normal)
    {
        if (ctx != null) ctx.DrawText(OverlayDraw.Text(s, size, brush, weight), new Point(x, y));
    }

    private static void TextCentered(DrawingContext ctx, string s, double size, IBrush brush, Rect r,
        FontWeight weight = FontWeight.Normal)
    {
        var ft = OverlayDraw.Text(s, size, brush, weight);
        ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    private double SectionHeader(DrawingContext? ctx, string title, double x, double y, double innerW)
    {
        Text(ctx, title, H2Size, FgBrush, x, y, FontWeight.Bold);
        ctx?.DrawLine(BorderPen, new Point(x, y + 24), new Point(x + innerW, y + 24));
        return y + 34;
    }

    private double KeyValueRow(DrawingContext? ctx, string key, string value, double x, double y, double innerW, bool bold = false)
    {
        Text(ctx, key, BodySize, bold ? FgBrush : MutedBrush, x, y);
        if (ctx != null)
        {
            var ft = OverlayDraw.Text(value, bold ? H2Size : BodySize, FgBrush, bold ? FontWeight.Bold : FontWeight.Normal);
            ctx.DrawText(ft, new Point(x + innerW - ft.Width, y));
        }
        return y + 24;
    }

    private double BarRow(DrawingContext? ctx, string label, string right, long value, long max,
        double x, double y, double innerW, IBrush color)
    {
        const double rowH = 26;
        if (ctx != null)
        {
            OverlayDraw.Panel(ctx, new Rect(x, y + 3, innerW, rowH - 8), TrackBrush, null, 5);
            double barW = innerW * Math.Clamp(value / (double)max, 0, 1);
            if (barW > 4) OverlayDraw.Panel(ctx, new Rect(x, y + 3, barW, rowH - 8), color, null, 5);

            var lft = OverlayDraw.Text(OverlayDraw.Truncate(label, BodySize, innerW - 100), BodySize, TitleBrush);
            ctx.DrawText(lft, new Point(x + 8, y + (rowH - lft.Height) / 2));
            var rft = OverlayDraw.Text(right, BodySize, TitleBrush);
            ctx.DrawText(rft, new Point(x + innerW - 8 - rft.Width, y + (rowH - rft.Height) / 2));
        }
        return y + rowH;
    }

    private double HourlyHistogram(DrawingContext? ctx, int[] hourly, double x, double y, double innerW)
    {
        y = SectionHeader(ctx, "Active by hour", x, y, innerW);
        const double areaH = 64;
        int max = Math.Max(1, hourly.Max());
        double cellW = innerW / 24.0;
        if (ctx != null)
        {
            for (int hr = 0; hr < 24; hr++)
            {
                double bx = x + hr * cellW;
                double bw = Math.Max(2, cellW - 3);
                double bh = areaH * (hourly[hr] / (double)max);
                if (bh < 2 && hourly[hr] > 0) bh = 2;
                if (bh > 0) OverlayDraw.Panel(ctx, new Rect(bx, y + (areaH - bh), bw, bh), AccentBrush, null, 2);
            }
            ctx.DrawLine(BorderPen, new Point(x, y + areaH + 1), new Point(x + innerW, y + areaH + 1));
            foreach (int hr in new[] { 0, 6, 12, 18 })
                Text(ctx, $"{hr:00}", LabelSize, MutedBrush, x + hr * cellW, y + areaH + 4);
        }
        return y + areaH + 24;
    }

    private double TrendHistogram(DrawingContext? ctx, IReadOnlyList<DayPoint> trend, string label,
        double x, double y, double innerW)
    {
        y = SectionHeader(ctx, label, x, y, innerW);
        const double areaH = 56;
        long max = Math.Max(1, trend.Max(p => (long)p.Active.TotalSeconds));
        double cellW = innerW / trend.Count;
        if (ctx != null)
        {
            for (int i = 0; i < trend.Count; i++)
            {
                int sec = (int)trend[i].Active.TotalSeconds;
                double bx = x + i * cellW;
                double bw = Math.Max(2, cellW - 2);
                double bh = areaH * (sec / (double)max);
                if (bh < 2 && sec > 0) bh = 2;
                if (bh > 0) OverlayDraw.Panel(ctx, new Rect(bx, y + (areaH - bh), bw, bh), AccentBrush, null, 2);
            }
            ctx.DrawLine(BorderPen, new Point(x, y + areaH + 1), new Point(x + innerW, y + areaH + 1));
            Text(ctx, $"{trend[0].Day:MMM d}", LabelSize, MutedBrush, x, y + areaH + 4);
            var last = OverlayDraw.Text($"{trend[^1].Day:MMM d}", LabelSize, MutedBrush);
            ctx.DrawText(last, new Point(x + innerW - last.Width, y + areaH + 4));
        }
        return y + areaH + 24;
    }
}
