using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data.Resources;

namespace Perch.Avalonia.Views;

/// <summary>
/// The resource monitor's charts (docs/resource-diagnostics-plan.md, "Viewer"): a legend, a lane of finding
/// markers, stacked CPU, the Claude activity lane, and stacked memory on one time axis. Click or drag to scrub the
/// cursor; click a marker to pick its finding. Hovering any chart (or a legend entry) shows that moment's values and
/// highlights the series under the pointer across all three. One <see cref="Draw"/> routine measures and paints.
/// </summary>
internal sealed class ResourceTimelineView : Control
{
    private const double Pad = 16, LegendSize = 12, LabelSize = 11, AxisW = 52, ChartH = 150, ChartGap = 26,
        MarkerLaneH = 18, AxisH = 20, LaneH = 64, TipSize = 11.5;

    private ResourceAnalysis? _analysis;
    private int _cursor = -1;
    private ResourceFinding? _selected;

    // Layout from the last paint, for hit-testing.
    private Rect _plot, _cpuRect, _laneRect, _memRect;
    private double _plotLeft, _plotWidth;
    private readonly List<(Rect hit, ResourceFinding finding)> _markers = new();
    private readonly List<(Rect hit, string key)> _legendChips = new();
    private readonly List<(Rect hit, string key)> _laneChips = new();

    // Band outlines and the lane's columns depend only on the analysis and width; a cursor move or a hover repaints
    // without rebuilding them.
    private (ResourceAnalysis analysis, double width, List<StreamGeometry> cpu, List<StreamGeometry> mem)? _geometry;
    private LaneData? _lane;

    // What the pointer is over.
    private Point? _hover;
    private enum HoverChart { None, Cpu, Lane, Memory }

    public event Action<int>? CursorMoved;
    public event Action<ResourceFinding>? FindingPicked;

    public int CursorTick => _cursor;

    private static readonly DashStyle RamLineDash = new([4, 3], 0);
    private static readonly DashStyle GuideDash = new([2, 3], 0);

    public ResourceTimelineView()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void SetAnalysis(ResourceAnalysis? analysis)
    {
        _analysis = analysis;
        if (_selected is not null && analysis is not null && !analysis.Findings.Contains(_selected)) _selected = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetCursor(int tick)
    {
        _cursor = tick;
        InvalidateVisual();
    }

    public void SetSelected(ResourceFinding? finding)
    {
        _selected = finding;
        InvalidateVisual();
    }

    /// <summary>Points the hover at <paramref name="point"/> (null clears it), for the headless render.</summary>
    internal void HoverForRender(Point? point)
    {
        _hover = point;
        InvalidateVisual();
    }

    /// <summary>The colour of a chart band, shared with the breakdown so a group reads the same in both.</summary>
    // The OS's own bands share the idle slate in steps, so they read as one family apart from the programs.
    public static Color BandColor(ResourceGroup group, int band) => group.Kind switch
    {
        ResourceGroupKind.Rest => Palette.Active.TeamGray.ToColor(),
        ResourceGroupKind.Unattributed => Palette.Muted,
        ResourceGroupKind.System => group.Key switch
        {
            "sys:nonpaged" => Palette.Blend(Palette.Idle, Palette.Red, 0.35f),
            "sys:irq" => Palette.Blend(Palette.Idle, Palette.Orange, 0.35f),
            _ => Palette.Idle,
        },
        _ => Palette.SeriesColor(band),
    };

    public static double BandOpacity(ResourceGroup group) => group.Kind switch
    {
        ResourceGroupKind.Unattributed => 0.28,
        ResourceGroupKind.System => group.Key switch
        {
            "sys:paged" => 0.8,
            "sys:cache" => 0.55,
            "sys:modified" => 0.4,
            _ => 0.8,
        },
        _ => 0.85,
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 900;
        return new Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.SurfaceSunkenBrush, new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    private double Draw(DrawingContext? ctx, double width)
    {
        double y = Pad;
        var a = _analysis;
        if (a is null || a.TickCount == 0)
        {
            var msg = OverlayDraw.Text(a is null ? "No recording yet." : "Waiting for the first sample…", 13, Palette.MutedBrush);
            ctx?.DrawText(msg, new Point(Pad, y));
            return y + msg.Height + Pad;
        }

        // The legend's height is the same whether a chip is highlighted, so measure and paint agree.
        double legendBottom = Legend(null, a, width, y, null);

        _plotLeft = Pad + AxisW;
        _plotWidth = Math.Max(10, width - _plotLeft - Pad);
        double markerTop = legendBottom + 8;
        double cpuTop = markerTop + MarkerLaneH + 4;
        bool lane = a.ActivitySessions.Count > 0 || a.Launches.Any(v => v > 0);
        double captionH = OverlayDraw.Text("Ag", LabelSize - 0.5, Palette.MutedBrush).Height + 4;
        double laneTop = cpuTop + ChartH + 6 + captionH;
        double memTop = (lane ? laneTop + LaneH : cpuTop + ChartH) + ChartGap;
        double bottom = memTop + ChartH;
        _plot = new Rect(_plotLeft, markerTop, _plotWidth, bottom - markerTop);
        _cpuRect = new Rect(_plotLeft, cpuTop, _plotWidth, ChartH);
        _laneRect = lane ? new Rect(_plotLeft, laneTop, _plotWidth, LaneH) : default;
        _memRect = new Rect(_plotLeft, memTop, _plotWidth, ChartH);

        if (ctx is null) return bottom + AxisH + Pad;

        var geometry = Geometry(a, width, cpuTop, memTop);
        var laneData = lane ? Lane(a, width) : null;
        var border = OverlayDraw.Pen(Palette.BorderBrush, 1);
        double memMax = MemoryCeiling(a);

        // Resolve the hover first: which chart, which moment, which series. Everything below paints from it.
        var (chart, tick, key) = ResolveHover(a, laneData, memMax);

        _legendChips.Clear();
        Legend(ctx, a, width, y, key);

        ChartFrame(ctx, "CPU", "100%", _cpuRect, border);
        ChartFrame(ctx, "Memory", ResourceFormat.Bytes((long)memMax), _memRect, border);

        // The selected finding's span, under the bands.
        if (_selected is { } sel)
        {
            double x0 = XOf(a, sel.StartTick) - HalfStep(a), x1 = XOf(a, sel.EndTick) + HalfStep(a);
            var shade = OverlayDraw.Brush(Palette.Accent, 0.14);
            ctx.FillRectangle(shade, new Rect(x0, cpuTop, Math.Max(2, x1 - x0), ChartH));
            ctx.FillRectangle(shade, new Rect(x0, memTop, Math.Max(2, x1 - x0), ChartH));
        }

        for (int b = 0; b < a.Charted.Count; b++)
        {
            var group = a.Charted[b];
            bool lit = key == group.Key;
            double opacity = BandOpacity(group) * (key is null || lit ? 1 : 0.28);
            var fill = OverlayDraw.Brush(BandColor(group, b), Math.Max(opacity, lit ? 0.9 : 0));
            var outline = lit ? OverlayDraw.Pen(Palette.TitleBrush, 1.2) : null;
            ctx.DrawGeometry(fill, outline, geometry.cpu[b]);
            ctx.DrawGeometry(fill, outline, geometry.mem[b]);
        }

        // Total-RAM line: where the memory chart's ceiling is physical RAM, say so.
        if (a.Recording.TotalRam > 0)
        {
            double ry = memTop + ChartH - ChartH * Math.Min(1, a.Recording.TotalRam / memMax);
            ctx.DrawLine(OverlayDraw.Pen(Palette.ErrorBrush, 1, RamLineDash),
                new Point(_plotLeft, ry), new Point(_plotLeft + _plotWidth, ry));
        }

        if (laneData is not null) ActivityLane(ctx, a, laneData, laneTop, captionH, border, key);
        Markers(ctx, a, markerTop);
        TimeAxis(ctx, a, bottom + 4);

        if (_cursor >= 0 && _cursor < a.TickCount)
        {
            double cx = XOf(a, _cursor);
            ctx.DrawLine(OverlayDraw.Pen(Palette.TitleBrush, 1.5), new Point(cx, cpuTop - 2), new Point(cx, bottom));
            var at = OverlayDraw.Text(a.Recording.Ticks[_cursor].AtUtc.ToLocalTime().ToString("HH:mm:ss"), LabelSize,
                Palette.TitleBrush, FontWeight.SemiBold);
            double tx = Math.Clamp(cx - at.Width / 2, _plotLeft, _plotLeft + _plotWidth - at.Width);
            var bg = new Rect(tx - 4, memTop - ChartGap + 3, at.Width + 8, at.Height + 2);
            OverlayDraw.Panel(ctx, bg, Palette.FormBgBrush, border, 4);
            ctx.DrawText(at, new Point(tx, bg.Y + 1));
        }

        if (chart != HoverChart.None && tick >= 0)
        {
            double hx = XOf(a, tick);
            ctx.DrawLine(OverlayDraw.Pen(Palette.MutedBrush, 1, GuideDash), new Point(hx, cpuTop), new Point(hx, bottom));
            Tooltip(ctx, a, laneData, chart, tick, key, memMax, width);
        }

        return bottom + AxisH + Pad;
    }

    // ── Hover ────────────────────────────────────────────────────────────────

    // The hovered chart, moment and series key (a group key; a session's group key in the lane). A legend chip
    // highlights its series without a moment.
    private (HoverChart chart, int tick, string? key) ResolveHover(ResourceAnalysis a, LaneData? lane, double memMax)
    {
        if (_hover is not { } p) return (HoverChart.None, -1, null);
        foreach (var (hit, key) in _legendChips.Concat(_laneChips))
            if (hit.Contains(p)) return (HoverChart.None, -1, key);

        int tick = TickAt(a, p.X);
        if (_cpuRect.Contains(p)) return (HoverChart.Cpu, tick, BandAt(a.Cpu[tick], 100, _cpuRect, p.Y, a));
        if (_memRect.Contains(p)) return (HoverChart.Memory, tick, BandAt(a.Memory[tick].Select(v => (double)v).ToArray(), memMax, _memRect, p.Y, a));
        if (lane is not null && _laneRect.Contains(p))
        {
            int c = Math.Clamp((int)((p.X - _plotLeft) / lane.ColumnWidth), 0, lane.Columns - 1);
            double yb = _laneRect.Bottom - 2;
            for (int s = 0; s < lane.Sessions; s++)
            {
                double h = lane.Scale > 0 ? (LaneH - 4) * lane.Calls[c, s] / lane.Scale : 0;
                if (p.Y <= yb && p.Y >= yb - h) return (HoverChart.Lane, tick, a.ActivitySessions[s].Key);
                yb -= h;
            }
            return (HoverChart.Lane, tick, null);
        }
        return (HoverChart.None, -1, null);
    }

    // The band whose slice of the stack the pointer is in, at one moment.
    private static string? BandAt(double[] values, double max, Rect chart, double y, ResourceAnalysis a)
    {
        double cum = 0;
        for (int b = 0; b < values.Length; b++)
        {
            double lo = cum;
            cum += values[b];
            double top = chart.Bottom - chart.Height * Math.Min(cum, max) / max;
            double bot = chart.Bottom - chart.Height * Math.Min(lo, max) / max;
            if (y >= top && y <= bot && bot - top > 0) return a.Charted[b].Key;
        }
        return null;
    }

    private void Tooltip(DrawingContext ctx, ResourceAnalysis a, LaneData? lane, HoverChart chart, int tick, string? key,
        double memMax, double width)
    {
        var t = a.Recording.Ticks[tick];
        string time = t.AtUtc.ToLocalTime().ToString("HH:mm:ss");
        string header;
        var rows = new List<(Color color, double opacity, string label, string value, bool lit)>();
        switch (chart)
        {
            case HoverChart.Cpu:
                header = $"{time}  ·  CPU {ResourceFormat.Percent(t.Cpu)}";
                foreach (int b in Enumerable.Range(0, a.Charted.Count)
                             .Where(b => a.Cpu[tick][b] >= 0.5 || a.Charted[b].Key == key)
                             .OrderByDescending(b => a.Cpu[tick][b]))
                    rows.Add((BandColor(a.Charted[b], b), BandOpacity(a.Charted[b]), a.Charted[b].Label,
                        ResourceFormat.Percent(a.Cpu[tick][b]), a.Charted[b].Key == key));
                break;
            case HoverChart.Memory:
                header = $"{time}  ·  RAM {ResourceFormat.Bytes(t.RamUsed)}" +
                         (a.Recording.TotalRam > 0 ? $" of {ResourceFormat.Bytes(a.Recording.TotalRam)}" : "");
                var mem = new List<(long v, int b)>();
                for (int b = 0; b < a.Charted.Count; b++)
                    if (a.Memory[tick][b] >= 16L << 20 || a.Charted[b].Key == key) mem.Add((a.Memory[tick][b], b));
                foreach (var (v, b) in mem.OrderByDescending(m => m.v))
                    rows.Add((BandColor(a.Charted[b], b), BandOpacity(a.Charted[b]), a.Charted[b].Label, ResourceFormat.Bytes(v),
                        a.Charted[b].Key == key));
                break;
            default:
                header = $"{time}  ·  Claude, last minute";
                for (int s = 0; s < a.ActivitySessions.Count; s++)
                {
                    var g = a.ActivitySessions[s];
                    double calls = a.ToolCallsPerMinute.Count > tick ? a.ToolCallsPerMinute[tick][s] : 0;
                    int agents = a.AgentsPerMinute.Count > tick ? a.AgentsPerMinute[tick][s] : 0;
                    rows.Add((LaneColor(a, s), 0.9, g.Label.Replace("Session · ", "", StringComparison.Ordinal),
                        $"{calls:0} tool calls · {agents} agent{(agents == 1 ? "" : "s")}", g.Key == key));
                }
                rows.Sort((x, y) => y.lit.CompareTo(x.lit));
                int window = Math.Max(1, (int)Math.Round(60 / a.TickInterval.TotalSeconds));
                int launched = 0;
                for (int i = Math.Max(0, tick - window + 1); i <= tick; i++) launched += a.Launches[i];
                rows.Add((Palette.Title, 1, "New processes seen", $"{launched}", false));
                break;
        }
        if (rows.Count > 14) rows = rows.Where(r => r.lit).Concat(rows.Where(r => !r.lit).Take(13)).ToList();

        var head = OverlayDraw.Text(header, TipSize, Palette.TitleBrush, FontWeight.SemiBold);
        double rowH = OverlayDraw.Text("Ag", TipSize, Palette.FgBrush).Height + 3;
        double labelW = 0, valueW = 0;
        foreach (var r in rows)
        {
            labelW = Math.Max(labelW, OverlayDraw.MeasureWidth(r.label, TipSize, r.lit ? FontWeight.SemiBold : FontWeight.Normal));
            valueW = Math.Max(valueW, OverlayDraw.MeasureWidth(r.value, TipSize, r.lit ? FontWeight.SemiBold : FontWeight.Normal));
        }
        labelW = Math.Min(labelW, 260);
        double boxW = Math.Max(head.Width, 16 + labelW + 14 + valueW) + 20;
        double boxH = 8 + head.Height + 4 + rows.Count * rowH + 6;

        var p = _hover!.Value;
        double bx = p.X + 16 + boxW <= width - 4 ? p.X + 16 : p.X - 16 - boxW;
        double by = Math.Clamp(p.Y - boxH / 2, 4, Math.Max(4, Bounds.Height - boxH - 4));
        var box = new Rect(Math.Max(4, bx), by, boxW, boxH);
        OverlayDraw.Panel(ctx, box, Palette.OverlaySurfaceBrush, OverlayDraw.Pen(Palette.BorderBrush, 1), 6);
        ctx.DrawText(head, new Point(box.X + 10, box.Y + 6));

        double ry = box.Y + 8 + head.Height + 4;
        foreach (var r in rows)
        {
            double mid = ry + rowH / 2;
            if (r.lit) ctx.FillRectangle(OverlayDraw.Brush(Palette.Accent, 0.16), new Rect(box.X + 4, ry, box.Width - 8, rowH));
            OverlayDraw.Panel(ctx, new Rect(box.X + 10, mid - 4.5, 10, 9), OverlayDraw.Brush(r.color, Math.Min(1, r.opacity + 0.1)), null, 2);
            var weight = r.lit ? FontWeight.SemiBold : FontWeight.Normal;
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(OverlayDraw.Truncate(r.label, TipSize, labelW, weight), TipSize,
                r.lit ? Palette.TitleBrush : Palette.FgBrush, weight), box.X + 26, mid);
            var value = OverlayDraw.Text(r.value, TipSize, r.lit ? Palette.TitleBrush : Palette.FgBrush, weight);
            OverlayDraw.TextLeftMid(ctx, value, box.Right - 10 - value.Width, mid);
            ry += rowH;
        }
    }

    /// <summary>The chart rectangles from the last layout, for the headless render to aim a hover.</summary>
    internal (Rect cpu, Rect lane, Rect memory) ChartsForRender => (_cpuRect, _laneRect, _memRect);

    // ── Legend and frames ──────────────────────────────────────────────────────

    private double Legend(DrawingContext? ctx, ResourceAnalysis a, double width, double y, string? lit)
    {
        double x = Pad, lineH = OverlayDraw.Text("Ag", LegendSize, Palette.FgBrush).Height + 4;
        for (int b = 0; b < a.Charted.Count; b++)
        {
            var group = a.Charted[b];
            bool on = group.Key == lit;
            var weight = on ? FontWeight.SemiBold : FontWeight.Normal;
            double chipW = 12 + 6 + OverlayDraw.MeasureWidth(group.Label, LegendSize, FontWeight.SemiBold) + 16;
            if (x + chipW > width - Pad && x > Pad)
            {
                x = Pad;
                y += lineH;
            }
            if (ctx is not null)
            {
                double mid = y + lineH / 2;
                var chip = new Rect(x - 4, y, chipW - 8, lineH);
                if (on) OverlayDraw.Panel(ctx, chip, OverlayDraw.Brush(Palette.Accent, 0.16), null, 4);
                double fade = lit is null || on ? 1 : 0.45;
                OverlayDraw.Panel(ctx, new Rect(x, mid - 5, 12, 10), OverlayDraw.Brush(BandColor(group, b), BandOpacity(group) * fade), null, 2);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(group.Label, LegendSize,
                    fade < 1 ? Palette.MutedBrush : Palette.FgBrush, weight), x + 18, mid);
                _legendChips.Add((chip, group.Key));
            }
            x += chipW;
        }
        return y + lineH;
    }

    private void ChartFrame(DrawingContext ctx, string title, string max, Rect r, IPen border)
    {
        ctx.DrawRectangle(Palette.FormBgBrush, border, r);
        var grid = OverlayDraw.Pen(Palette.SeparatorBrush, 1);
        for (int i = 1; i < 4; i++)
        {
            double gy = r.Y + r.Height * i / 4;
            ctx.DrawLine(grid, new Point(r.X, gy), new Point(r.Right, gy));
        }
        var t = OverlayDraw.Text(title, LabelSize, Palette.TitleBrush, FontWeight.SemiBold);
        ctx.DrawText(t, new Point(Pad, r.Y + r.Height / 2 - t.Height / 2));
        var m = OverlayDraw.Text(max, LabelSize, Palette.MutedBrush);
        ctx.DrawText(m, new Point(r.X - m.Width - 6, r.Y));
        var z = OverlayDraw.Text("0", LabelSize, Palette.MutedBrush);
        ctx.DrawText(z, new Point(r.X - z.Width - 6, r.Bottom - z.Height));
    }

    // ── Claude lane ────────────────────────────────────────────────────────────

    private sealed record LaneData(
        ResourceAnalysis Analysis, double Width, int Columns, double ColumnWidth, int Sessions,
        double[,] Calls, double[] Launches, double Scale, double LaunchScale);

    // Tool calls a minute per session, and new processes a minute, averaged into about one column per 3 px; the
    // scales are rounded up to a readable figure (1, 2 or 5 × 10ⁿ) so the axis says something.
    private LaneData Lane(ResourceAnalysis a, double width)
    {
        if (_lane is { } cached && ReferenceEquals(cached.Analysis, a) && cached.Width == width) return cached;
        int n = a.TickCount, sessions = a.ActivitySessions.Count;
        int columns = Math.Clamp((int)(_plotWidth / 3), 1, Math.Max(1, n));
        double perMinute = 60 / a.TickInterval.TotalSeconds;
        var calls = new double[columns, Math.Max(1, sessions)];
        var launches = new double[columns];
        double maxCalls = 0, maxLaunches = 0;
        for (int c = 0; c < columns; c++)
        {
            int from = (int)((long)c * n / columns), to = Math.Max(from + 1, (int)((long)(c + 1) * n / columns));
            double total = 0;
            for (int s = 0; s < sessions; s++)
            {
                double v = 0;
                for (int i = from; i < to; i++) v += a.ToolCallsPerMinute[i][s];
                calls[c, s] = v / (to - from);
                total += calls[c, s];
            }
            double l = 0;
            for (int i = from; i < to; i++) l += a.Launches[i];
            launches[c] = l / (to - from) * perMinute;
            maxCalls = Math.Max(maxCalls, total);
            maxLaunches = Math.Max(maxLaunches, launches[c]);
        }
        _lane = new LaneData(a, width, columns, _plotWidth / columns, sessions, calls, launches, NiceCeiling(maxCalls), NiceCeiling(maxLaunches));
        return _lane;
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var step in new[] { 1.0, 2, 5, 10 })
            if (v <= step * magnitude) return step * magnitude;
        return 10 * magnitude;
    }

    private static Color LaneColor(ResourceAnalysis a, int session)
    {
        var group = a.ActivitySessions[session];
        int band = a.ChartIndexOf(group.Key);
        return a.Charted[band].Key == group.Key ? BandColor(group, band) : Palette.SeriesColor(band + session + 3);
    }

    // What Claude was doing under the CPU chart: tool calls a minute per session (bars, from the transcripts) and new
    // processes a minute (line), so "CPU jumped here" sits over "the agents started here". A caption row above names
    // each session's colour; the left axis is the bars' scale, the line's is in the caption.
    private void ActivityLane(DrawingContext ctx, ResourceAnalysis a, LaneData lane, double top, double captionH, IPen border, string? lit)
    {
        var rect = new Rect(_plotLeft, top, _plotWidth, LaneH);
        ctx.DrawRectangle(Palette.FormBgBrush, border, rect);
        ctx.DrawLine(OverlayDraw.Pen(Palette.SeparatorBrush, 1), new Point(rect.X, rect.Y + rect.Height / 2), new Point(rect.Right, rect.Y + rect.Height / 2));
        var title = OverlayDraw.Text("Claude", LabelSize, Palette.TitleBrush, FontWeight.SemiBold);
        ctx.DrawText(title, new Point(Pad, top + LaneH / 2 - title.Height / 2));
        if (lane.Scale > 0)
        {
            var max = OverlayDraw.Text($"{lane.Scale:0}/min", LabelSize - 0.5, Palette.MutedBrush);
            ctx.DrawText(max, new Point(_plotLeft - max.Width - 6, top));
            var zero = OverlayDraw.Text("0", LabelSize, Palette.MutedBrush);
            ctx.DrawText(zero, new Point(_plotLeft - zero.Width - 6, rect.Bottom - zero.Height));
        }

        // Caption row: a chip per session (hover one to pick it out), then what the line is.
        _laneChips.Clear();
        double cx = _plotLeft, cmid = top - captionH / 2 - 1;
        var lead = OverlayDraw.Text("Tool calls/min:", LabelSize - 0.5, Palette.MutedBrush);
        OverlayDraw.TextLeftMid(ctx, lead, cx, cmid);
        cx += lead.Width + 8;
        for (int s = 0; s < lane.Sessions; s++)
        {
            var g = a.ActivitySessions[s];
            bool on = g.Key == lit;
            string name = g.Label.Replace("Session · ", "", StringComparison.Ordinal);
            var text = OverlayDraw.Text(name, LabelSize - 0.5, lit is null || on ? Palette.FgBrush : Palette.MutedBrush,
                on ? FontWeight.SemiBold : FontWeight.Normal);
            var chip = new Rect(cx - 3, cmid - captionH / 2 + 1, 10 + 5 + text.Width + 6, captionH - 2);
            if (on) OverlayDraw.Panel(ctx, chip, OverlayDraw.Brush(Palette.Accent, 0.16), null, 4);
            OverlayDraw.Panel(ctx, new Rect(cx, cmid - 4, 9, 8), OverlayDraw.Brush(LaneColor(a, s), lit is null || on ? 0.9 : 0.35), null, 2);
            OverlayDraw.TextLeftMid(ctx, text, cx + 14, cmid);
            _laneChips.Add((chip, g.Key));
            cx += chip.Width + 8;
        }
        if (lane.LaunchScale > 0)
        {
            var line = OverlayDraw.Text($"— line: new processes/min (0–{lane.LaunchScale:0})", LabelSize - 0.5, Palette.MutedBrush);
            OverlayDraw.TextLeftMid(ctx, line, Math.Max(cx + 8, _plotLeft + _plotWidth - line.Width), cmid);
        }

        double inner = LaneH - 4;
        if (lane.Scale > 0)
        {
            for (int c = 0; c < lane.Columns; c++)
            {
                double yb = rect.Bottom - 2;
                for (int s = 0; s < lane.Sessions; s++)
                {
                    double h = inner * lane.Calls[c, s] / lane.Scale;
                    if (h <= 0) continue;
                    bool on = lit is null || a.ActivitySessions[s].Key == lit;
                    ctx.FillRectangle(OverlayDraw.Brush(LaneColor(a, s), on ? 0.9 : 0.25),
                        new Rect(_plotLeft + c * lane.ColumnWidth, yb - h, Math.Max(1, lane.ColumnWidth - 0.5), h));
                    yb -= h;
                }
            }
        }
        if (lane.LaunchScale > 0)
        {
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                for (int c = 0; c < lane.Columns; c++)
                {
                    var p = new Point(_plotLeft + (c + 0.5) * lane.ColumnWidth, rect.Bottom - 2 - inner * lane.Launches[c] / lane.LaunchScale);
                    if (c == 0) g.BeginFigure(p, false);
                    else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, OverlayDraw.Pen(Palette.TitleBrush, lit is null ? 1.2 : 0.8), line);
        }
    }

    // ── Markers and axis ───────────────────────────────────────────────────────

    private void Markers(DrawingContext ctx, ResourceAnalysis a, double top)
    {
        _markers.Clear();
        double mid = top + MarkerLaneH / 2;
        foreach (var f in a.Findings.OrderBy(f => f.Severity))
        {
            double x = XOf(a, f.PeakTick);
            var color = SeverityColor(f.Severity);
            bool selected = f.Equals(_selected);
            double r = selected ? 7 : 5;
            var diamond = new StreamGeometry();
            using (var g = diamond.Open())
            {
                g.BeginFigure(new Point(x, mid - r), true);
                g.LineTo(new Point(x + r, mid));
                g.LineTo(new Point(x, mid + r));
                g.LineTo(new Point(x - r, mid));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(OverlayDraw.Brush(color), selected ? OverlayDraw.Pen(Palette.TitleBrush, 1.5) : null, diamond);
            _markers.Add((new Rect(x - 8, top, 16, MarkerLaneH), f));
        }
    }

    public static Color SeverityColor(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => Palette.Red,
        FindingSeverity.Warning => Palette.Orange,
        _ => Palette.Accent,
    };

    private void TimeAxis(DrawingContext ctx, ResourceAnalysis a, double y)
    {
        var ticks = a.Recording.Ticks;
        var span = ticks[^1].AtUtc - ticks[0].AtUtc;
        string format = span < TimeSpan.FromMinutes(10) ? "HH:mm:ss" : "HH:mm";
        int labels = Math.Clamp((int)(_plotWidth / 110), 2, 8);
        for (int i = 0; i < labels; i++)
        {
            int tick = (int)Math.Round((double)i * (ticks.Count - 1) / (labels - 1));
            var t = OverlayDraw.Text(ticks[tick].AtUtc.ToLocalTime().ToString(format), LabelSize, Palette.MutedBrush);
            double x = Math.Clamp(XOf(a, tick) - t.Width / 2, _plotLeft, _plotLeft + _plotWidth - t.Width);
            ctx.DrawText(t, new Point(x, y));
        }
    }

    private double MemoryCeiling(ResourceAnalysis a)
    {
        double max = a.Recording.TotalRam;
        foreach (var m in a.Memory) max = Math.Max(max, m.Sum());
        return Math.Max(1, max);
    }

    private double XOf(ResourceAnalysis a, int tick) =>
        a.TickCount <= 1 ? _plotLeft + _plotWidth / 2 : _plotLeft + _plotWidth * tick / (a.TickCount - 1);

    private double HalfStep(ResourceAnalysis a) => a.TickCount <= 1 ? 4 : Math.Max(1.5, _plotWidth / (a.TickCount - 1) / 2);

    private int TickAt(ResourceAnalysis a, double x)
    {
        if (a.TickCount <= 1) return 0;
        double t = (x - _plotLeft) / _plotWidth * (a.TickCount - 1);
        return Math.Clamp((int)Math.Round(t), 0, a.TickCount - 1);
    }

    // Stacked bands, one closed outline each. Long recordings are bucketed to about one column per pixel
    // (averaged) so a 4-hour recording draws as cheaply as a short one.
    private (ResourceAnalysis analysis, double width, List<StreamGeometry> cpu, List<StreamGeometry> mem) Geometry(
        ResourceAnalysis a, double width, double cpuTop, double memTop)
    {
        if (_geometry is { } cached && ReferenceEquals(cached.analysis, a) && cached.width == width) return cached;

        int n = a.TickCount, bands = a.Charted.Count;
        int columns = Math.Clamp((int)_plotWidth, 1, Math.Max(1, n));
        double memMax = MemoryCeiling(a);
        var cpuStack = new double[columns, bands + 1];
        var memStack = new double[columns, bands + 1];
        var xs = new double[columns];
        for (int c = 0; c < columns; c++)
        {
            int from = (int)((long)c * n / columns), to = Math.Max(from + 1, (int)((long)(c + 1) * n / columns));
            double cumCpu = 0, cumMem = 0;
            for (int b = 0; b < bands; b++)
            {
                double sc = 0, sm = 0;
                for (int i = from; i < to; i++) { sc += a.Cpu[i][b]; sm += a.Memory[i][b]; }
                cumCpu += sc / (to - from);
                cumMem += sm / (to - from);
                cpuStack[c, b + 1] = Math.Min(100, cumCpu);
                memStack[c, b + 1] = Math.Min(memMax, cumMem);
            }
            xs[c] = XOf(a, (from + to - 1) / 2);
        }
        if (columns == 1) xs[0] = _plotLeft;

        StreamGeometry Band(double[,] stack, int b, double top, double max)
        {
            double Y(double v) => top + ChartH - ChartH * (v / max);
            var geo = new StreamGeometry();
            using var g = geo.Open();
            double right = columns == 1 ? _plotLeft + _plotWidth : xs[columns - 1];
            g.BeginFigure(new Point(xs[0], Y(stack[0, b + 1])), true);
            for (int c = 1; c < columns; c++) g.LineTo(new Point(xs[c], Y(stack[c, b + 1])));
            if (columns == 1) g.LineTo(new Point(right, Y(stack[0, b + 1])));
            g.LineTo(new Point(right, Y(stack[columns - 1, b])));
            for (int c = columns - 1; c >= 0; c--) g.LineTo(new Point(xs[c], Y(stack[c, b])));
            g.EndFigure(true);
            return geo;
        }

        var cpu = new List<StreamGeometry>(bands);
        var mem = new List<StreamGeometry>(bands);
        for (int b = 0; b < bands; b++)
        {
            cpu.Add(Band(cpuStack, b, cpuTop, 100));
            mem.Add(Band(memStack, b, memTop, memMax));
        }
        _geometry = (a, width, cpu, mem);
        return _geometry.Value;
    }

    // ── Input ────────────────────────────────────────────────────────────────

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_analysis is not { TickCount: > 0 } a || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        Focus();
        foreach (var (hit, finding) in _markers)
        {
            if (!hit.Contains(p)) continue;
            FindingPicked?.Invoke(finding);
            e.Handled = true;
            return;
        }
        if (!_plot.Contains(p)) return;
        _dragging = true;
        e.Pointer.Capture(this);
        MoveTo(a, p.X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging && _analysis is { TickCount: > 0 } a) MoveTo(a, p.X);
        if (_hover != p)
        {
            _hover = p;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void MoveTo(ResourceAnalysis a, double x)
    {
        int tick = TickAt(a, x);
        if (tick == _cursor) return;
        SetCursor(tick);
        CursorMoved?.Invoke(tick);
    }
}
