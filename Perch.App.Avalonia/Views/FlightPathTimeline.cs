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
/// waiting-on-you / stuck) over a faint idle track. A single <see cref="Draw"/> routine measures
/// (null context → returns height) and paints, so the two never drift. Hosted, scrolled, by
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
    private static readonly IBrush TrackBrush  = new SolidColorBrush(Palette.Track);
    private static readonly IPen   BorderPen   = new Pen(new SolidColorBrush(Palette.Border), 1);
    private static readonly IPen   NowPen       = new Pen(new SolidColorBrush(Palette.Brand), 1) { DashStyle = DashStyle.Dash };

    private const double H1Size = 20, BodySize = 13, LaneSize = 13, LabelSize = 11;
    private const double Pad = 22, GutterW = 168, RowH = 42, TrackH = 18, AxisH = 20;

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
            bool isToday = report.Day == DateOnly.FromDateTime(DateTime.Now);
            Text(ctx, isToday ? "No sessions recorded yet today." : "No sessions recorded on this day.",
                BodySize, MutedBrush, x, y);
            return y + 40;
        }

        y = Legend(ctx, x, y) + 6;

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
        cx = LegendChip(ctx, cx, y, OrangeBrush, "Waiting on you");
        LegendChip(ctx, cx, y, RedBrush, "Stuck");
        return y + 22;
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

        var proj = OverlayDraw.Text(OverlayDraw.Truncate(lane.Project, LaneSize, GutterW - 10), LaneSize, TitleBrush, FontWeight.SemiBold);
        ctx.DrawText(proj, new Point(x, y + 4));
        string sub = lane.Branch.Length > 0 ? $"{lane.Branch} · {StatsFormat.Duration(lane.ActiveTime)}" : StatsFormat.Duration(lane.ActiveTime);
        var subFt = OverlayDraw.Text(OverlayDraw.Truncate(sub, LabelSize, GutterW - 10), LabelSize, MutedBrush);
        ctx.DrawText(subFt, new Point(x, y + 22));

        OverlayDraw.Pill(ctx, TrackBrush, new Rect(trackX, trackY, trackW, TrackH));
        foreach (var seg in lane.Segments)
        {
            double x0 = XOf(seg.Start), x1 = XOf(seg.End);
            double w = Math.Max(3, x1 - x0);
            OverlayDraw.Panel(ctx, new Rect(x0, trackY, w, TrackH), SegmentColor(seg.State), null, 3);
        }
    }

    private static IBrush SegmentColor(FlightState state) => state switch
    {
        FlightState.Waiting => OrangeBrush,
        FlightState.Stuck   => RedBrush,
        _                   => AccentBrush,
    };

    private static void Text(DrawingContext? ctx, string s, double size, IBrush brush, double x, double y,
        FontWeight weight = FontWeight.Normal)
    {
        if (ctx != null) ctx.DrawText(OverlayDraw.Text(s, size, brush, weight), new Point(x, y));
    }
}
