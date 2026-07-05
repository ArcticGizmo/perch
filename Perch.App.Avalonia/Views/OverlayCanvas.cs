using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Perch.Avalonia.Rendering;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn overlay body — the Avalonia port of <c>OverlayForm</c>'s painting, replacing the
/// thin-vertical XAML <c>OverlayView</c>. A single <see cref="Draw"/> routine both measures (returns the
/// content height when given a null context) and paints (when given a real one), so the measured height
/// and painted layout can never drift — the same measure-or-paint discipline the WinForms dashboards use.
///
/// Built up over Phase 4. Done so far: rounded panel (4.1); header/collapsed bar (4.2 — brand, status
/// count pills, dense toggle, expand chevron). Rows, sub-agents, glyphs, bars and interaction follow.
/// </summary>
public sealed class OverlayCanvas : Control
{
    // ── Layout (mirrors OverlayForm's constants) ──────────────────────────────
    private const double FormWidth    = 280;
    private const double HeaderHeight = 44;
    private const double Corner       = 10;
    private const double HorizPad     = 12;
    private const double IconBoxW     = 16;
    private const double IconBoxH     = 16;
    private const double IconGap      = 6;
    private const double RowHeight    = 46;
    private const double RowsTop      = HeaderHeight; // strips (usage/metrics/quick-links) inserted here later

    // Font sizes (px ~= the WinForms point sizes: name 8.5pt, status/activity 7.5pt).
    private const double NameSize     = 11.5;
    private const double StatusSize   = 10;
    private const double ActivitySize = 10;

    // ── Palette (the overlay's own; matches OverlayForm) ──────────────────────
    private static readonly IBrush BgBrush        = new SolidColorBrush(Color.FromArgb(245, 15, 15, 20));
    private static readonly IPen   BorderPen      = new Pen(new SolidColorBrush(Color.FromRgb(45, 45, 60)), 1);
    private static readonly IBrush MutedBrush     = new SolidColorBrush(Color.FromRgb(110, 110, 130));
    private static readonly IBrush FgBrush        = new SolidColorBrush(Color.FromRgb(225, 225, 235));
    private static readonly Color  RunningColor   = Color.FromRgb(34, 197, 94);
    private static readonly Color  AttentionColor = Color.FromRgb(251, 146, 60);
    private static readonly Color  AwaitingColor  = Color.FromRgb(250, 204, 21);
    private static readonly Color  IdleColor      = Color.FromRgb(100, 116, 139);
    private static readonly IPen   SepPen         = new Pen(new SolidColorBrush(Color.FromRgb(35, 35, 50)), 1);
    private static readonly IBrush AttentionBrush = new SolidColorBrush(AttentionColor);
    private static readonly IBrush AwaitingBrush  = new SolidColorBrush(AwaitingColor);

    // "Waiting on you" ramp length (minutes to fully red); user-tunable later (SetWaitingTimerRedMinutes).
    private int _waitingTimerRedMinutes = 3;

    // Brand mark (the app icon), loaded once.
    private static readonly Bitmap? Brand = LoadBrand();

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private bool _expanded = true;

    /// <summary>Feeds the latest session list (called on the UI thread by the monitor host) and
    /// repaints.</summary>
    public void Update(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(FormWidth, Draw(null, FormWidth));

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Width);

    // Measure-or-paint: returns the content height; paints only when ctx is non-null.
    private double Draw(DrawingContext? ctx, double width)
    {
        bool showRows = _expanded && _sessions.Count > 0;
        double height = HeaderHeight + (showRows ? _sessions.Count * RowHeight + 2 : 0);

        if (ctx != null)
        {
            var panel = new Rect(0.5, 0.5, width - 1, height - 1);
            OverlayDraw.Panel(ctx, panel, BgBrush, BorderPen, Corner);
            DrawHeader(ctx, width);

            if (showRows)
            {
                double top = RowsTop;
                foreach (var s in _sessions)
                {
                    DrawSessionRow(ctx, s, top, width);
                    top += RowHeight;
                }
            }
        }

        return height;
    }

    // Core session row: separator, status dot, name (truncated), right-aligned status, and a second
    // line with the activity phrase + elapsed time. The per-row glyphs (warn/artifact/mail/remote/bot/
    // mode/thermo/tasks/burn/git) and hover highlight land in steps 4.5–4.7 / 4.11.
    private void DrawSessionRow(DrawingContext ctx, ClaudeSession session, double top, double width)
    {
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));

        double midY = top + RowHeight / 2;
        bool running  = session.Status == SessionStatus.Running;
        bool awaiting = session.Status == SessionStatus.AwaitingInput;

        string? activity = running ? session.Activity : awaiting ? "waiting on you" : null;
        string? elapsed  = running ? session.RunningElapsedLabel()
                         : awaiting ? session.AwaitingElapsedLabel() : null;
        bool twoLine = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        double nameMidY = twoLine ? top + RowHeight / 2 - 8 : midY;

        IBrush secondLine = awaiting
            ? new SolidColorBrush(WarmWaitingColor(session.AwaitingElapsed() ?? TimeSpan.Zero))
            : MutedBrush;

        var dotColor = session.Status switch
        {
            SessionStatus.Running        => RunningColor,
            SessionStatus.NeedsAttention => AttentionColor,
            SessionStatus.AwaitingInput  => AwaitingColor,
            _                            => IdleColor,
        };
        ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(HorizPad + 4, nameMidY), 4, 4);

        string statusText = session.Status switch
        {
            SessionStatus.Running        => "running",
            SessionStatus.NeedsAttention => "done ↩",
            SessionStatus.AwaitingInput  => "input ↩",
            _                            => "idle",
        };
        IBrush statusBrush = session.Status switch
        {
            SessionStatus.NeedsAttention => AttentionBrush,
            SessionStatus.AwaitingInput  => AwaitingBrush,
            _                            => MutedBrush,
        };

        double statusW = OverlayDraw.MeasureWidth(statusText, StatusSize);
        double nameMax = width - HorizPad * 3 - 8 - statusW; // later steps subtract glyph widths too
        string nameTrunc = OverlayDraw.Truncate(session.DisplayName, NameSize, nameMax);
        double nameX = HorizPad + 14;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(nameTrunc, NameSize, FgBrush), nameX, nameMidY);

        double statusX = width - HorizPad - statusW;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(statusText, StatusSize, statusBrush), statusX, nameMidY);

        if (twoLine)
        {
            double activityMidY = top + RowHeight / 2 + 9;
            double lineLeft = HorizPad + 14;

            double elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                elapsedW = OverlayDraw.MeasureWidth(elapsed, ActivitySize);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(elapsed, ActivitySize, secondLine),
                    width - HorizPad - elapsedW, activityMidY);
            }
            if (!string.IsNullOrEmpty(activity))
            {
                double activityMax = width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                string actTrunc = OverlayDraw.Truncate(activity, ActivitySize, activityMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(actTrunc, ActivitySize, secondLine),
                    lineLeft, activityMidY);
            }
        }
    }

    // "Waiting on you" ramp: awaiting-yellow at 0 warming to alarm-red once blocked _waitingTimerRedMinutes.
    private Color WarmWaitingColor(TimeSpan waited)
    {
        double fullMinutes = Math.Max(1, _waitingTimerRedMinutes);
        double t = Math.Clamp(waited.TotalMinutes / fullMinutes, 0.0, 1.0);
        var to = Color.FromRgb(239, 68, 68);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Lerp(AwaitingColor.R, to.R), Lerp(AwaitingColor.G, to.G), Lerp(AwaitingColor.B, to.B));
    }

    private void DrawHeader(DrawingContext ctx, double width)
    {
        double midY = HeaderHeight / 2;

        // Brand icon + "Perch" label.
        double brandRight = HorizPad;
        if (Brand is { })
        {
            const double iconSize = 18;
            ctx.DrawImage(Brand, new Rect(HorizPad, midY - iconSize / 2, iconSize, iconSize));
            brandRight = HorizPad + iconSize + 5;
        }

        var label = OverlayDraw.Text("Perch", 11, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, label, brandRight, midY);
        brandRight += label.Width;

        // Separator dot.
        double sepX = brandRight + 4;
        ctx.DrawEllipse(MutedBrush, null, new Point(sepX + 2, midY), 2, 2);
        double x = sepX + 10;

        if (_sessions.Count == 0)
        {
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("no sessions", 11, MutedBrush), x, midY);
        }
        else
        {
            int running   = _sessions.Count(s => s.Status == SessionStatus.Running);
            int attention = _sessions.Count(s => s.Status == SessionStatus.NeedsAttention);
            int awaiting  = _sessions.Count(s => s.Status == SessionStatus.AwaitingInput);
            int idle      = _sessions.Count(s => s.Status == SessionStatus.Idle);

            x = DrawStatusPill(ctx, x, midY, awaiting,  AwaitingColor,  AwaitingColor);
            x = DrawStatusPill(ctx, x, midY, running,   RunningColor,   Color.FromRgb(225, 225, 235));
            x = DrawStatusPill(ctx, x, midY, attention, AttentionColor, AttentionColor);
            if (running == 0 && attention == 0 && awaiting == 0)
                DrawStatusPill(ctx, x, midY, idle, IdleColor, IdleColor);
        }

        // Right-side glyph cluster, laid out right-to-left: [dense toggle] [expand chevron].
        // (Update badge is step 4.14; dense-toggle click wiring is step 4.12.)
        var sideRect = SideIconRect(width);
        DrawSideCollapseIcon(ctx, sideRect, reversed: false);
        double clusterLeft = width - HorizPad - IconBoxW;

        if (_sessions.Count > 0)
        {
            var chevron = OverlayDraw.Text(_expanded ? "▲" : "▼", 9, MutedBrush);
            double chevX = clusterLeft - IconGap - chevron.Width;
            OverlayDraw.TextLeftMid(ctx, chevron, chevX, midY);
        }
    }

    private Rect SideIconRect(double width)
    {
        double top = (HeaderHeight - IconBoxH) / 2;
        double right = width - HorizPad;
        return new Rect(right - IconBoxW, top, IconBoxW, IconBoxH);
    }

    private static double DrawStatusPill(DrawingContext ctx, double x, double midY, int count,
                                         Color dotColor, Color textColor)
    {
        if (count == 0) return x;

        ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(x + 4, midY), 4, 4);
        x += 12;

        var label = OverlayDraw.Text(count.ToString(), 12, new SolidColorBrush(textColor), FontWeight.Bold);
        OverlayDraw.TextLeftMid(ctx, label, x, midY);
        return x + label.Width + 8;
    }

    // The dense-toggle glyph: an arrow into a pipe ("->|" collapses to the right edge). Non-reversed
    // form only for now; dense mode (and the reversed "|<-") lands in step 4.12.
    private static void DrawSideCollapseIcon(DrawingContext ctx, Rect r, bool reversed)
    {
        var pen = new Pen(MutedBrush, 1.6, lineCap: PenLineCap.Round);
        double midY = r.Top + r.Height / 2;
        double pad = 3;
        double left = r.Left + pad;
        double right = r.Right - pad;
        double headLen = 4;

        if (!reversed)
        {
            double pipeX = right;
            double shaftEnd = pipeX - 2;
            ctx.DrawLine(pen, new Point(left, midY), new Point(shaftEnd, midY));                          // shaft
            ctx.DrawLine(pen, new Point(shaftEnd - headLen, midY - headLen), new Point(shaftEnd, midY));  // arrowhead
            ctx.DrawLine(pen, new Point(shaftEnd - headLen, midY + headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(pipeX, r.Top + pad), new Point(pipeX, r.Bottom - pad));           // pipe
        }
        else
        {
            double pipeX = left;
            double shaftEnd = pipeX + 2;
            ctx.DrawLine(pen, new Point(right, midY), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd + headLen, midY - headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd + headLen, midY + headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(pipeX, r.Top + pad), new Point(pipeX, r.Bottom - pad));
        }
    }

    private static Bitmap? LoadBrand()
    {
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://perch-avalonia/Assets/icon.png"))); }
        catch { return null; }
    }
}
