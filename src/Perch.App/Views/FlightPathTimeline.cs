using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn daily "flight path" — the Avalonia port of <c>FlightPathForm.DrawTimeline</c>. A
/// horizontal Gantt of one day, one lane per session, each lane's time coloured by state (active /
/// waiting-for-input / idle-done / stuck) over a faint track. A single <see cref="Draw"/> routine
/// measures (null context → returns height) and paints, so the two never drift. Hosted, scrolled, by
/// <c>FlightPathWindow</c>, which owns the day navigation + off-thread loading.
/// </summary>
internal sealed class FlightPathTimeline : Control
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush TitleBrush  = new SolidColorBrush(Palette.Title);
    private static readonly IBrush MutedBrush  = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush AccentBrush = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Palette.Orange);
    private static readonly IBrush RedBrush    = new SolidColorBrush(Palette.Red);
    // A cool, dim slate for done-and-idle time: present enough to read as a state, quiet enough not to
    // compete with the warm "waiting for input" amber or the active-blue.
    private static readonly IBrush IdleBrush   = new SolidColorBrush(Color.FromRgb(96, 108, 132));
    // A point-event marker for API failures (529/429/…) — gold, distinct from the warm "waiting" amber
    // and the "stuck" red, and read as a marker (tick + status number) rather than a track-fill state.
    private static readonly IBrush ApiErrorBrush = new SolidColorBrush(Palette.Yellow);
    private static readonly IBrush TrackBrush  = new SolidColorBrush(Palette.Track);
    private static readonly IPen   BorderPen   = new Pen(new SolidColorBrush(Palette.Border), 1);
    private static readonly IPen   NowPen       = new Pen(new SolidColorBrush(Palette.Brand), 1) { DashStyle = DashStyle.Dash };

    private const double H1Size = 20, BodySize = 13, LaneSize = 13, LabelSize = 11;
    private const double Pad = 22, GutterW = 214, RowH = 50, TrackH = 18, AxisH = 20;

    private FlightPathReport? _report;
    private bool _loading = true;

    public void SetLoading()
    {
        _loading = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetReport(FlightPathReport report)
    {
        _report = report;
        _loading = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 620;
        return new Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(BodyBg), new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    private double Draw(DrawingContext? ctx, double width)
    {
        double y = Pad, x = Pad;
        double innerW = width - Pad * 2;

        Text(ctx, "Flight path", H1Size, TitleBrush, x, y, FontWeight.Bold);
        y += 30;

        if (_loading)
        {
            Text(ctx, "Loading…", BodySize, MutedBrush, x, y);
            return y + 40;
        }

        var report = _report ?? FlightPathReport.Empty(DateOnly.FromDateTime(DateTime.Now));
        if (report.IsEmpty)
        {
            bool isTodayEmpty = report.Day == DateOnly.FromDateTime(DateTime.Now);
            Text(ctx, isTodayEmpty ? "No sessions recorded yet today." : "No sessions recorded on this day.",
                BodySize, MutedBrush, x, y);
            return y + 40;
        }

        y = Legend(ctx, x, y);
        y = DaySummary(ctx, report, x, y) + 6;

        double trackX = x + GutterW;
        double trackW = Math.Max(1, innerW - GutterW);
        double totalSeconds = Math.Max(1, (report.WindowEnd - report.WindowStart).TotalSeconds);
        double XOf(DateTime t) => trackX + trackW * Math.Clamp((t - report.WindowStart).TotalSeconds / totalSeconds, 0, 1);

        int hours = (int)Math.Ceiling((report.WindowEnd - report.WindowStart).TotalHours);
        int step = hours <= 12 ? 1 : hours <= 24 ? 2 : 3;
        double axisY = y;
        double lanesTop = y + AxisH;
        double lanesBottom = lanesTop + report.Lanes.Count * RowH;

        if (ctx != null)
        {
            for (var t = report.WindowStart; t <= report.WindowEnd; t = t.AddHours(step))
            {
                double gx = XOf(t);
                ctx.DrawLine(BorderPen, new Point(gx, lanesTop), new Point(gx, lanesBottom));
                Text(ctx, t.ToString("HH:mm"), LabelSize, MutedBrush, gx + 3, axisY);
            }

            var now = DateTime.Now;
            if (report.Day == DateOnly.FromDateTime(now) && now >= report.WindowStart && now <= report.WindowEnd)
            {
                double nx = XOf(now);
                ctx.DrawLine(NowPen, new Point(nx, lanesTop), new Point(nx, lanesBottom));
            }

            double laneY = lanesTop;
            foreach (var lane in report.Lanes)
            {
                DrawLane(ctx, lane, x, laneY, trackX, trackW, XOf);
                laneY += RowH;
            }
        }

        return lanesBottom + Pad;
    }

    private double Legend(DrawingContext? ctx, double x, double y)
    {
        if (ctx == null) return y + 22;
        double cx = x;
        cx = LegendChip(ctx, cx, y, AccentBrush, "Active");
        cx = LegendChip(ctx, cx, y, OrangeBrush, "Waiting for input");
        cx = LegendChip(ctx, cx, y, IdleBrush, "Idle (done)");
        cx = LegendChip(ctx, cx, y, RedBrush, "Stuck");
        LegendChip(ctx, cx, y, ApiErrorBrush, "API error");
        return y + 22;
    }

    // A muted day-level roll-up under the legend, summing the lanes so the split reads as numbers, not
    // only as colour on the track.
    private double DaySummary(DrawingContext? ctx, FlightPathReport report, double x, double y)
    {
        if (ctx == null) return y + 18;
        TimeSpan active = TimeSpan.Zero, waiting = TimeSpan.Zero, idle = TimeSpan.Zero;
        int apiErrors = 0;
        foreach (var lane in report.Lanes)
        {
            active  += lane.ActiveTime;
            waiting += lane.AwaitingInputTime;
            idle    += lane.IdleTime;
            apiErrors += lane.ApiErrors.Count;
        }

        string label = report.Day == DateOnly.FromDateTime(DateTime.Now) ? "Today" : report.Day.ToString("ddd d MMM");
        string summary = $"{label} · {report.Lanes.Count} session{(report.Lanes.Count == 1 ? "" : "s")} · " +
                         $"active {StatsFormat.Duration(active)} · waiting {StatsFormat.Duration(waiting)} · idle {StatsFormat.Duration(idle)}";
        if (apiErrors > 0)
            summary += $" · {apiErrors} API error{(apiErrors == 1 ? "" : "s")}";
        Text(ctx, summary, LabelSize, MutedBrush, x, y);
        return y + 18;
    }

    private double LegendChip(DrawingContext ctx, double x, double y, IBrush color, string label)
    {
        const double dot = 11;
        OverlayDraw.Panel(ctx, new Rect(x, y + 2, dot, dot), color, null, 3);
        double textX = x + dot + 6;
        var ft = OverlayDraw.Text(label, LabelSize, MutedBrush);
        ctx.DrawText(ft, new Point(textX, y));
        return textX + ft.Width + 18;
    }

    private void DrawLane(DrawingContext ctx, FlightLane lane, double x, double y,
        double trackX, double trackW, Func<DateTime, double> XOf)
    {
        double rowMid = y + RowH / 2;
        double trackY = rowMid - TrackH / 2;

        // Gutter: project, branch, then the active / waiting / idle breakdown.
        var proj = OverlayDraw.Text(OverlayDraw.Truncate(lane.Project, LaneSize, GutterW - 10), LaneSize, TitleBrush, FontWeight.SemiBold);
        ctx.DrawText(proj, new Point(x, y + 3));
        if (lane.Branch.Length > 0)
        {
            var branch = OverlayDraw.Text(OverlayDraw.Truncate(lane.Branch, LabelSize, GutterW - 10), LabelSize, MutedBrush);
            ctx.DrawText(branch, new Point(x, y + 19));
        }
        var durFt = OverlayDraw.Text(OverlayDraw.Truncate(DurationBreakdown(lane), LabelSize, GutterW - 10), LabelSize, MutedBrush);
        ctx.DrawText(durFt, new Point(x, y + 32));

        OverlayDraw.Pill(ctx, TrackBrush, new Rect(trackX, trackY, trackW, TrackH));
        foreach (var seg in lane.Segments)
        {
            double x0 = XOf(seg.Start), x1 = XOf(seg.End);
            double w = Math.Max(3, x1 - x0);
            OverlayDraw.Panel(ctx, new Rect(x0, trackY, w, TrackH), SegmentColor(seg.State), null, 3);
        }

        // API failures overlay the segments: a gold tick poking through the track with the status code
        // (529, 429, …) sitting just above it, so the exact spot — and which error — is readable. Every
        // failure gets a tick (a retry storm should read as a dense cluster), but the status label is
        // drawn only when it clears the last one, so overlapping labels never smear into nonsense. Marks
        // arrive time-ordered from the service.
        double lastLabelRight = double.NegativeInfinity;
        foreach (var mark in lane.ApiErrors)
        {
            double mx = XOf(mark.Time);
            ctx.FillRectangle(ApiErrorBrush, new Rect(mx - 1, trackY - 3, 2, TrackH + 6));
            string label = mark.Status > 0 ? mark.Status.ToString() : "API";
            var ft = OverlayDraw.Text(label, 9, ApiErrorBrush, FontWeight.SemiBold);
            double left = mx - ft.Width / 2;
            if (left > lastLabelRight + 3)
            {
                ctx.DrawText(ft, new Point(left, y));
                lastLabelRight = left + ft.Width;
            }
        }
    }

    // The lane's engaged / blocked / idle times, joined compactly. Active always shows; waiting and idle
    // only when the session actually spent time there, so a clean lane stays uncluttered.
    private static string DurationBreakdown(FlightLane lane)
    {
        var parts = new List<string> { $"active {StatsFormat.Duration(lane.ActiveTime)}" };
        if (lane.AwaitingInputTime > TimeSpan.Zero)
            parts.Add($"waiting {StatsFormat.Duration(lane.AwaitingInputTime)}");
        if (lane.IdleTime > TimeSpan.Zero)
            parts.Add($"idle {StatsFormat.Duration(lane.IdleTime)}");
        return string.Join(" · ", parts);
    }

    private static IBrush SegmentColor(FlightState state) => state switch
    {
        FlightState.AwaitingInput => OrangeBrush,
        FlightState.Idle          => IdleBrush,
        FlightState.Stuck         => RedBrush,
        _                         => AccentBrush,
    };

    private static void Text(DrawingContext? ctx, string s, double size, IBrush brush, double x, double y,
        FontWeight weight = FontWeight.Normal)
    {
        if (ctx != null) ctx.DrawText(OverlayDraw.Text(s, size, brush, weight), new Point(x, y));
    }
}
