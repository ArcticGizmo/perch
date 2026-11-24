using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Sprig;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's Sprig section: a collapsible header ("SPRIG" plus a running/starting summary), and under it one line
/// per workspace sprig has checked out, with a state dot, its branch and how many of its apps are ready. A line opens
/// sprig on that workspace; its right-click menu starts, stops, pauses or resumes it, or opens a running app. The
/// standard collapsible-section shape (see <c>OverlayCanvas.Feed.cs</c>), fed a <see cref="SprigStatus"/> by
/// <c>SprigMonitorHost</c> so the canvas never touches the sprig process. See docs/sprig-integration-plan.md.
/// </summary>
public sealed partial class OverlayCanvas
{
    private bool _sprigEnabled;
    private bool _sprigExpanded = true;
    private SprigStatus _sprig = SprigStatus.Empty;
    private SprigFeed _sprigFeed = SprigFeed.Off;

    // The workspaces drawn as lines, in drawing order: free slots are left out (they're sprig's spare capacity,
    // not work in progress). Rebuilt when the status changes, so measure, paint and hit-test agree.
    private IReadOnlyList<SprigWorkspace> _sprigLines = [];

    private Rect _sprigHeaderRect;
    private readonly List<Rect> _sprigLineRects = new();
    private bool _hoveredSprigHeader;
    private int _hoveredSprigLine = -1;

    /// <summary>Raised when the header's chevron toggles the section; the App persists it.</summary>
    public event Action<bool>? SprigExpandChanged;

    /// <summary>Raised to bring sprig's window up on a workspace, or on sprig as it is (null).</summary>
    internal event Action<SprigWorkspace?>? SprigOpenRequested;

    /// <summary>Raised for a menu action on a workspace, or (with an app name) on one of its apps.</summary>
    internal event Action<SprigWorkspace, SprigAction, string?>? SprigActionRequested;

    private bool SprigSectionVisible => _sprigEnabled;

    private double SprigHeaderHeight => FeedCaptionHeight + 12;
    private static double SprigLineHeight => HypertreeLineHeight + 4;

    // The body is a line per workspace, or one muted line saying there are none, once sprig has answered.
    private int SprigBodyLineCount => !_sprigExpanded || !_sprig.Seen ? 0 : Math.Max(1, _sprigLines.Count);

    private double SprigSectionHeight
        => !SprigSectionVisible ? 0
         : SprigHeaderHeight + (SprigBodyLineCount > 0 ? SprigBodyLineCount * SprigLineHeight + 4 : 0);

    /// <summary>Show/hide the section (the "Sprig" setting). Changes the panel height, so relayout. The data is
    /// kept: the live overlay's host empties it when it stops, and the Settings preview seeds it only once.</summary>
    public void SetShowSprig(bool enabled)
    {
        if (_sprigEnabled == enabled) return;
        _sprigEnabled = enabled;
        RelinkSprigSessions();
        RemeasurePanel();
    }

    /// <summary>Seeds the persisted expand state (no event).</summary>
    public void SetSprigExpanded(bool expanded)
    {
        if (_sprigExpanded == expanded) return;
        _sprigExpanded = expanded;
        if (SprigSectionVisible) RemeasurePanel();
    }

    /// <summary>Replaces what the section shows (UI thread). Relayouts only when its height changes.</summary>
    internal void SetSprig(SprigStatus status, SprigFeed feed)
    {
        double before = SprigSectionHeight;
        _sprig = status;
        _sprigFeed = feed;
        _sprigLines = status.Workspaces
            .Where(w => !w.IsFree)
            .OrderBy(SprigOrder)
            .ThenBy(w => w.Orchestration, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RelinkSprigSessions();
        if (!SprigSectionVisible) return;
        if (SprigSectionHeight != before) RemeasurePanel();
        else InvalidateVisual();
    }

    // What needs you first: a failure (a whole workspace, or one app of a running one), then what's on its way up,
    // then what's running, then what's parked.
    private static int SprigOrder(SprigWorkspace w) => w.InTrouble ? 0 : w.Shown switch
    {
        SprigWorkspaceState.Failed => 0,
        SprigWorkspaceState.Starting => 1,
        SprigWorkspaceState.Running => 2,
        SprigWorkspaceState.Paused => 3,
        SprigWorkspaceState.Stopped => 4,
        _ => 5,
    };

    // Grey is the text-muted grey, so a parked sprig mark matches the worktree glyph beside it on a row.
    private static Color SprigStateColor(SprigWorkspaceState s) => s switch
    {
        SprigWorkspaceState.Running => Palette.Green,
        SprigWorkspaceState.Starting => Palette.Accent,
        SprigWorkspaceState.Paused => Palette.Yellow,
        SprigWorkspaceState.Failed => Palette.Red,
        _ => Palette.Muted,
    };

    private static Color SprigAppColor(SprigApp a)
        => a.IsReady ? Palette.Green : a.IsFailed ? Palette.Red : a.IsBusy ? Palette.Accent
         : a.State == "restarting" ? Palette.Yellow : Palette.Muted;

    // ── activity spinner ────────────────────────────────────────────────────────────────────────────────────

    // Actions asked for from Perch that sprig hasn't finished, by workspace key, so the spinner shows from the click
    // rather than from sprig's first report. The token stops an earlier action finishing from clearing a later one.
    private readonly Dictionary<string, (int Token, SprigActivity Activity)> _sprigPending = new(StringComparer.Ordinal);
    private int _sprigPendingToken;
    private bool _anySpinnerThisFrame;
    private static Geometry? _spinnerArc;
    private const double SpinnerPeriodMs = 900;

    /// <summary>Marks <paramref name="key"/> as busy with an action Perch just asked for. Returns the token to end it
    /// with.</summary>
    internal int BeginSprigPending(string key, SprigActivity activity)
    {
        int token = ++_sprigPendingToken;
        _sprigPending[key] = (token, activity);
        InvalidateVisual();
        return token;
    }

    /// <summary>Ends the wait <see cref="BeginSprigPending"/> started, unless a later action has replaced it.</summary>
    internal void EndSprigPending(string key, int token)
    {
        if (!_sprigPending.TryGetValue(key, out var p) || p.Token != token) return;
        _sprigPending.Remove(key);
        InvalidateVisual();
    }

    private SprigActivity SprigActivityOf(SprigWorkspace w)
        => _sprigPending.TryGetValue(w.Key, out var p) ? p.Activity : w.Activity;

    // The header mark's spinner: starting while the feed connects, else the busiest workspace (a stop outranks a
    // start, being the shorter and rarer of the two).
    private SprigActivity SprigHeaderActivity()
    {
        if (_sprigFeed is SprigFeed.Connecting or SprigFeed.Reconnecting) return SprigActivity.Starting;
        if (_sprigFeed != SprigFeed.Live) return SprigActivity.None;
        var any = SprigActivity.None;
        foreach (var w in _sprigLines)
            switch (SprigActivityOf(w))
            {
                case SprigActivity.Stopping: return SprigActivity.Stopping;
                case SprigActivity.Starting: any = SprigActivity.Starting; break;
            }
        return any;
    }

    // A small spinner badged on the bottom right of a sprig mark drawn by DrawSprigMark at (cx, cy, size): the
    // starting colour while starting, yellow while stopping. A disc of the panel surface goes under it so it reads
    // against the mark. Under reduced motion it's a still dot and no repaint timer runs for it.
    private void DrawSprigSpinner(DrawingContext ctx, double cx, double cy, double size, SprigActivity activity)
    {
        if (activity == SprigActivity.None) return;
        var brush = OverlayDraw.Brush(activity == SprigActivity.Stopping ? Palette.Yellow : Palette.Accent);
        double r = size * 0.18;
        var c = new Point(cx + size * 0.3, cy + size * 0.34);
        ctx.DrawEllipse(Palette.OverlaySurfaceBrush, null, c, r + 1.2, r + 1.2);
        if (Pulse.ReduceMotion)
        {
            ctx.DrawEllipse(brush, null, c, r, r);
            return;
        }
        _anySpinnerThisFrame = true;
        double turn = DateTime.Now.TimeOfDay.TotalMilliseconds % SpinnerPeriodMs / SpinnerPeriodMs * 2 * Math.PI;
        var arc = _spinnerArc ??= UnitArc(270);
        var m = Matrix.CreateScale(r, r) * Matrix.CreateRotation(turn) * Matrix.CreateTranslation(c.X, c.Y);
        using (ctx.PushTransform(m))
            ctx.DrawGeometry(null, OverlayDraw.Pen(brush, 1.6 / r, lineCap: PenLineCap.Round), arc);
    }

    // An open arc on the unit circle, built once and scaled into place, so a spinning frame allocates no geometry.
    private static StreamGeometry UnitArc(double sweepDeg)
    {
        double end = sweepDeg * Math.PI / 180;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(new Point(1, 0), isFilled: false);
            gc.ArcTo(new Point(Math.Cos(end), Math.Sin(end)), new Size(1, 1), 0, sweepDeg > 180, SweepDirection.Clockwise);
            gc.EndFigure(false);
        }
        return geo;
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────────────────

    private void DrawSprigSection(DrawingContext ctx, double width, double top)
    {
        DrawSprigHeader(ctx, width, top);
        _sprigLineRects.Clear();
        if (SprigBodyLineCount == 0) return;

        double y = top + SprigHeaderHeight;
        if (_sprigLines.Count == 0)
        {
            var rect = new Rect(0, y, width, SprigLineHeight);
            _sprigLineRects.Add(rect);
            bool hot = _hoveredSprigLine == 0;
            if (hot) OverlayDraw.Panel(ctx, rect.Deflate(new Thickness(HorizPad - 4, 1)), FeedHoverBrush, null, 6);
            var ft = OverlayDraw.Text("No workspaces checked out · open sprig", HyperMetaSize, hot ? FgBrush : MutedBrush);
            OverlayDraw.TextLeftMid(ctx, ft, HorizPad + 16, y + SprigLineHeight / 2);
            return;
        }

        // While watch is coming back, the lines are the last state read, so they're drawn faded as stale.
        using (ctx.PushOpacity(_sprigFeed == SprigFeed.Reconnecting ? 0.5 : 1))
        {
            for (int i = 0; i < _sprigLines.Count; i++)
            {
                DrawSprigLine(ctx, width, y, _sprigLines[i], i);
                y += SprigLineHeight;
            }
        }
    }

    private void DrawSprigHeader(DrawingContext ctx, double width, double top)
    {
        double h = SprigHeaderHeight;
        double midY = top + h / 2;
        _sprigHeaderRect = new Rect(0, top, width, h);
        if (_hoveredSprigHeader)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), h - 6), FeedHoverBrush, null, 6);

        DrawChevron(ctx, HorizPad + 4, midY, _sprigExpanded);
        // Sprig's brand green while the feed is live; greyed while connecting, reconnecting or not installed.
        DrawSprigMark(ctx, HorizPad + 17, midY, 13, _sprigFeed == SprigFeed.Live ? Palette.SprigBrush : MutedBrush);
        DrawSprigSpinner(ctx, HorizPad + 17, midY, 13, SprigHeaderActivity());
        var capFt = OverlayDraw.Text("SPRIG", FeedCaptionSize, MutedBrush);
        double x = HorizPad + 26;
        OverlayDraw.TextLeftMid(ctx, capFt, x, midY);
        x += capFt.Width + 10;

        // Right-aligned: where the feed stands, or what's running.
        var (summary, color, dot) = SprigSummary();
        if (summary.Length == 0) return;
        double right = width - HorizPad;
        double avail = right - x - (dot is null ? 0 : 10);
        if (avail < 24) return;
        var ft = OverlayDraw.Text(OverlayDraw.Truncate(summary, FeedCaptionSize, avail), FeedCaptionSize, OverlayDraw.Brush(color));
        double tx = right - ft.Width;
        OverlayDraw.TextLeftMid(ctx, ft, tx, midY);
        if (dot is { } d) ctx.DrawEllipse(OverlayDraw.Brush(d), null, new Point(tx - 7, midY), 3, 3);
    }

    // The header's right-hand text, its colour, and the dot before it (null for none).
    private (string Text, Color Color, Color? Dot) SprigSummary()
    {
        var muted = Palette.Active.TextMuted.ToColor();
        switch (_sprigFeed)
        {
            case SprigFeed.NotInstalled: return ("not installed", muted, null);
            case SprigFeed.Connecting:   return ("connecting…", muted, null);
            case SprigFeed.Reconnecting: return ("reconnecting…", Palette.Yellow, null);
            case SprigFeed.Off:          return ("", muted, null);
        }

        int failed = _sprigLines.Count(w => w.Shown == SprigWorkspaceState.Failed);
        int starting = _sprigLines.Count(w => w.Shown == SprigWorkspaceState.Starting);
        int running = _sprigLines.Count(w => w.Shown == SprigWorkspaceState.Running);
        var parts = new List<string>(3);
        if (running > 0) parts.Add($"{running} running");
        if (starting > 0) parts.Add($"{starting} starting");
        if (failed > 0) parts.Add($"{failed} failed");
        if (parts.Count == 0)
        {
            int paused = _sprigLines.Count(w => w.State == SprigWorkspaceState.Paused);
            return paused > 0 ? ($"{paused} paused", muted, null) : ("nothing running", muted, null);
        }
        var lead = failed > 0 ? Palette.Red : starting > 0 ? Palette.Accent : Palette.Green;
        return (string.Join(" · ", parts), muted, lead);
    }

    private void DrawSprigLine(DrawingContext ctx, double width, double top, SprigWorkspace w, int index)
    {
        var rect = new Rect(0, top, width, SprigLineHeight);
        _sprigLineRects.Add(rect);
        double midY = top + SprigLineHeight / 2;
        bool hot = _hoveredSprigLine == index;
        if (hot) OverlayDraw.Panel(ctx, rect.Deflate(new Thickness(HorizPad - 4, 1)), FeedHoverBrush, null, 6);

        ctx.DrawEllipse(OverlayDraw.Brush(SprigStateColor(w.Shown)), null, new Point(HorizPad + 17, midY), 3.2, 3.2);

        bool parked = w.State is SprigWorkspaceState.Stopped or SprigWorkspaceState.Paused or SprigWorkspaceState.Unknown;
        var (detail, detailColor) = SprigLineDetail(w);
        var detailFt = OverlayDraw.Text(detail, HyperMetaSize, OverlayDraw.Brush(detailColor));
        double right = width - HorizPad;
        OverlayDraw.TextLeftMid(ctx, detailFt, right - detailFt.Width, midY);

        double textX = HorizPad + 26;
        double avail = right - detailFt.Width - 10 - textX;
        if (avail < 20) return;
        var labelBrush = hot ? FgBrush : parked ? MutedBrush : FgBrush;
        var labelFt = OverlayDraw.Text(OverlayDraw.Truncate(w.Label, HyperRowSize, avail), HyperRowSize, labelBrush);
        OverlayDraw.TextLeftMid(ctx, labelFt, textX, midY);
    }

    // The right-hand detail: how many apps are ready while it's up, else its state in a word.
    private static (string Text, Color Color) SprigLineDetail(SprigWorkspace w)
    {
        var muted = Palette.Active.TextMuted.ToColor();
        // Red wherever it's shown: up, but an app has exited, failed or is crash-looping.
        // Worded as sprig words it (AppsReadiness.Detail), so the overlay and sprig's own app say the same thing.
        if (w.InTrouble)
            return (w.Down.Count switch
            {
                0 => "app down",
                1 => $"{ShortAppName(w.Down[0])} down",
                var n => $"{n} apps down",
            }, Palette.Red);
        var (ready, of) = w.ReadyOf;
        switch (w.Shown)
        {
            case SprigWorkspaceState.Running:
                return (of > 0 ? $"{ready}/{of} ready" : "running", ready == of ? Palette.Green : muted);
            case SprigWorkspaceState.Starting:
                return (of > 0 ? $"starting {ready}/{of}" : "running its jobs", Palette.Accent);
            case SprigWorkspaceState.Failed:
                return ("failed", Palette.Red);
            case SprigWorkspaceState.Paused:
                return ("paused", Palette.Yellow);
            case SprigWorkspaceState.Stopped:
                return ("stopped", muted);
            default:
                return (w.StateText.Length > 0 ? w.StateText : "unknown", muted);
        }
    }

    // Sprig's own mark (its sprig.svg path, verbatim): a leaf on a stem that forks to three ringed nodes. The
    // source is a 256-unit square; parsed once on first use.
    private const string SprigMarkPath =
        "M 183.27874 6.9923381 C 158.83902 7.5605893 145.45555 9.0962712 129.58175 21.490678 C 126.50668 23.891713 122.01354 27.461442 114.4571 36.067049 C 91.328822 66.402568 112.17866 95.804677 114.4571 101.20829 C 120.88779 77.104358 130.90769 55.064197 156.78061 42.139547 C 127.72662 71.885245 125.64448 90.392403 119.05785 110.7767 L 119.05785 165.42918 L 92.574711 138.94656 A 24.72245 24.72245 0 0 0 94.98645 128.29656 A 24.72245 24.72245 0 0 0 70.263928 103.57404 A 24.72245 24.72245 0 0 0 45.541406 128.29656 A 24.72245 24.72245 0 0 0 70.263928 153.01908 A 24.72245 24.72245 0 0 0 82.337093 149.87044 L 118.13749 185.67084 L 118.13749 200.69783 A 24.72245 24.72245 0 0 0 101.492 224.06333 A 24.72245 24.72245 0 0 0 126.21452 248.78585 A 24.72245 24.72245 0 0 0 150.93704 224.06333 A 24.72245 24.72245 0 0 0 133.59495 200.46838 L 133.59495 185.85481 L 169.67026 149.77949 A 24.72245 24.72245 0 0 0 181.90518 153.01908 A 24.72245 24.72245 0 0 0 206.6277 128.29656 A 24.72245 24.72245 0 0 0 181.90518 103.57404 A 24.72245 24.72245 0 0 0 157.18265 128.29656 A 24.72245 24.72245 0 0 0 159.8135 139.39459 L 133.68693 165.52116 L 133.68693 114.27313 L 138.56312 104.1523 C 166.5092 89.190839 191.20939 68.524071 183.27874 6.9923381 z " +
        "M 70.263928 118.14731 A 10.149215 10.149215 0 0 1 80.41318 128.29656 A 10.149215 10.149215 0 0 1 70.263928 138.44581 A 10.149215 10.149215 0 0 1 60.114677 128.29656 A 10.149215 10.149215 0 0 1 70.263928 118.14731 z " +
        "M 181.90518 118.14731 A 10.149215 10.149215 0 0 1 192.05443 128.29656 A 10.149215 10.149215 0 0 1 181.90518 138.44581 A 10.149215 10.149215 0 0 1 171.75592 128.29656 A 10.149215 10.149215 0 0 1 181.90518 118.14731 z " +
        "M 126.21452 213.91407 A 10.149215 10.149215 0 0 1 136.36377 224.06333 A 10.149215 10.149215 0 0 1 126.21452 234.21258 A 10.149215 10.149215 0 0 1 116.06527 224.06333 A 10.149215 10.149215 0 0 1 126.21452 213.91407 z";

    private static Geometry? _sprigMark;

    // The sprig mark centred on (cx, cy), fitted into a size×size box. The mark is taller than it is wide, so it
    // takes the box's height.
    private static void DrawSprigMark(DrawingContext ctx, double cx, double cy, double size, IBrush brush)
    {
        var mark = _sprigMark ??= Geometry.Parse(SprigMarkPath);
        var b = mark.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        double scale = size / Math.Max(b.Width, b.Height);
        var m = Matrix.CreateTranslation(-(b.X + b.Width / 2), -(b.Y + b.Height / 2))
              * Matrix.CreateScale(scale, scale)
              * Matrix.CreateTranslation(cx, cy);
        using (ctx.PushTransform(m))
            ctx.DrawGeometry(brush, null, mark);
    }

    // ── hit-testing ─────────────────────────────────────────────────────────────────────────────────────────

    private bool InSprigSection(Point p)
        => ShowFullPanel && SprigSectionVisible && _sectionTop.TryGetValue(OverlaySection.Sprig, out var top)
           && p.Y >= top && p.Y < top + SprigSectionHeight;

    private bool HitTestSprigHeader(Point p) => InSprigSection(p) && _sprigHeaderRect.Contains(p);

    private int HitTestSprigLine(Point p)
    {
        if (!InSprigSection(p)) return -1;
        for (int i = 0; i < _sprigLineRects.Count; i++)
            if (_sprigLineRects[i].Contains(p)) return i;
        return -1;
    }

    private void ClearSprigHitRects()
    {
        _sprigHeaderRect = default;
        _sprigLineRects.Clear();
    }

    /// <summary>Hover bookkeeping from OnPointerMoved. Returns whether anything clickable is under the cursor.</summary>
    private bool UpdateSprigHover(Point p)
    {
        bool header = HitTestSprigHeader(p);
        int line = HitTestSprigLine(p);
        if (header != _hoveredSprigHeader || line != _hoveredSprigLine)
        {
            _hoveredSprigHeader = header;
            _hoveredSprigLine = line;
            InvalidateVisual();
        }
        return header || line >= 0;
    }

    private bool ClearSprigHover()
    {
        bool changed = _hoveredSprigHeader || _hoveredSprigLine >= 0;
        _hoveredSprigHeader = false;
        _hoveredSprigLine = -1;
        return changed;
    }

    /// <summary>Left click routing. True when the click landed in the section.</summary>
    private bool RouteSprigClick(Point p)
    {
        if (!InSprigSection(p)) return false;
        if (HitTestSprigHeader(p))
        {
            _sprigExpanded = !_sprigExpanded;
            _hoveredSprigLine = -1;
            RemeasurePanel();
            SprigExpandChanged?.Invoke(_sprigExpanded);
            return true;
        }
        int line = HitTestSprigLine(p);
        if (line >= 0) SprigOpenRequested?.Invoke(line < _sprigLines.Count ? _sprigLines[line] : null);
        return true;
    }

    /// <summary>Right-click menu: a workspace's actions on its line, "Open sprig" anywhere else in the section.</summary>
    private bool ShowSprigMenu(Point p)
    {
        if (!InSprigSection(p)) return false;
        int line = HitTestSprigLine(p);
        var items = line >= 0 && line < _sprigLines.Count
            ? SprigWorkspaceMenu(_sprigLines[line])
            : [MenuItem("Open sprig", () => SprigOpenRequested?.Invoke(null))];
        ShowFlyout(items);
        return true;
    }

    private List<Control> SprigWorkspaceMenu(SprigWorkspace w)
    {
        void Act(SprigAction a, string? app = null) => SprigActionRequested?.Invoke(w, a, app);

        // The workspace as a whole: what fits the state it's in.
        var whole = new List<Control>();
        switch (w.State)
        {
            case SprigWorkspaceState.Running or SprigWorkspaceState.Starting:
                whole.Add(MenuItem("Restart", () => Act(SprigAction.Restart)));
                whole.Add(MenuItem("Pause", () => Act(SprigAction.Pause)));
                whole.Add(MenuItem("Stop", () => Act(SprigAction.Stop)));
                break;
            case SprigWorkspaceState.Paused:
                whole.Add(MenuItem("Resume", () => Act(SprigAction.Resume)));
                whole.Add(MenuItem("Stop", () => Act(SprigAction.Stop)));
                break;
            case SprigWorkspaceState.Failed:
                whole.Add(MenuItem("Start again", () => Act(SprigAction.Start)));
                whole.Add(MenuItem("Stop", () => Act(SprigAction.Stop)));
                break;
            default:
                whole.Add(MenuItem("Start", () => Act(SprigAction.Start)));
                break;
        }

        // Each ready app that listens on a port, straight to its page.
        var open = new List<Control>();
        foreach (var app in w.Apps.Where(a => a is { IsReady: true, Port: not null }))
        {
            var url = $"http://localhost:{app.Port}";
            open.Add(UrlMenuItem($"Open {app.ShortName}  ({url[7..]})", url));
        }

        // One submenu per app, for starting or stopping just that one.
        var apps = new List<Control>();
        if (w.Apps.Count > 0)
        {
            var perApp = w.Apps.Select(app =>
            {
                var sub = new List<Control>();
                if (app.IsOff || app.IsFailed || app.State == "stopped")
                    sub.Add(MenuItem("Start", () => Act(SprigAction.Start, app.Name)));
                else
                {
                    sub.Add(MenuItem("Restart", () => Act(SprigAction.Restart, app.Name)));
                    sub.Add(MenuItem("Stop", () => Act(SprigAction.Stop, app.Name)));
                }
                if (app.LogFile is { Length: > 0 } log)
                    sub.Add(MenuItem("Open log", () => PlatformServices.FileRevealer.OpenInEditor(log)));
                return (Control)new MenuItem { Header = $"{app.ShortName}  · {SprigAppStateText(app)}", ItemsSource = sub };
            }).ToList();
            apps.Add(new MenuItem { Header = "Apps", ItemsSource = perApp });
        }

        var where = new List<Control> { MenuItem("Open in sprig", () => SprigOpenRequested?.Invoke(w)) };
        var folder = w.Folder ?? w.Dirs.FirstOrDefault();
        if (folder is { Length: > 0 })
        {
            where.Add(MenuItem("Open folder", () => PlatformServices.FileRevealer.RevealInFileManager(folder)));
            where.Add(MenuItem("Copy path", () => CopyToClipboard(folder)));
        }

        var items = new List<Control>();
        AddMenuGroups(items, whole, open, apps, where);
        return items;
    }

    private static string ShortAppName(string name) => name.LastIndexOf('/') is var i and >= 0 ? name[(i + 1)..] : name;

    private static string SprigAppStateText(SprigApp a) => a.State switch
    {
        "ready" when a.Port is { } port => $"ready on :{port}",
        "building" when a.Doing is { Length: > 0 } doing => doing,
        "completed" => "finished",
        "exited" => a.Exit is { } code ? $"exited ({code})" : "exited",
        _ => a.State,
    };

    // ── session rows ────────────────────────────────────────────────────────────────────────────────────────

    // A session row working in a sprig workspace gets a small sprout before its PR glyph, coloured by the
    // workspace's state. Which workspace each session is in is worked out when the sessions or sprig's status
    // change (path matching), never while painting.
    private const double SprigRowIconWidth = 14;
    private Dictionary<string, SprigWorkspace> _sprigBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Rect> _sprigRowRects = new();
    private int _hoveredSprigRow = -1;

    private void RelinkSprigSessions()
    {
        var map = new Dictionary<string, SprigWorkspace>(StringComparer.Ordinal);
        if (_sprigEnabled && _sprig.Seen)
            foreach (var s in _sessions)
                if (SprigSessionLink.Find(_sprig, s.SessionId, s.Cwd, s.LinkedWorktree?.Dir) is { } w)
                    map[s.SessionId] = w;
        _sprigBySession = map;
    }

    private SprigWorkspace? SprigFor(ClaudeSession s) => _sprigBySession.GetValueOrDefault(s.SessionId);

    private void DrawSprigRowIcon(DrawingContext ctx, double x, double midY, SprigWorkspace w, int rowIndex)
    {
        if (_hoveredSprigRow == rowIndex)
            OverlayDraw.Panel(ctx, new Rect(x - 2, midY - 8, SprigRowIconWidth, 16), FeedHoverBrush, null, 4);
        // Sprig's own mark, but in the workspace's state colour: on a row it's a status glyph first.
        var color = w.InTrouble ? Palette.Red : SprigStateColor(w.Shown);
        DrawSprigMark(ctx, x + 5, midY, 14, OverlayDraw.Brush(color));
        DrawSprigSpinner(ctx, x + 5, midY, 14, SprigActivityOf(w));
        _sprigRowRects[rowIndex] = new Rect(x - 2, midY - 9, SprigRowIconWidth + 2, 18);
    }

    private SprigWorkspace? SprigAtRow(int row)
        => row >= 0 && row < _rows.Count && _rows[row].Session is { } s ? SprigFor(s) : null;

    private void ShowSprigRowTooltip(int row)
    {
        if (SprigAtRow(row) is not { } w || !_sprigRowRects.TryGetValue(row, out var r)) return;
        var (detail, color) = SprigLineDetail(w);
        var lines = new List<OverlayTooltip.Line>
        {
            new($"Sprig · {OverlayDraw.Truncate(w.Label, 11, 260)}", OverlayTooltip.FgColor, true),
            new(w.Name == "main" ? $"{w.Orchestration} · main checkout" : $"{w.Orchestration} · slot {w.Name}", OverlayTooltip.MutedColor, false),
            new(detail, color, false),
        };
        foreach (var app in w.Apps.Where(a => !a.IsOff).Take(8))
            lines.Add(new($"{app.ShortName}  {SprigAppStateText(app)}", SprigAppColor(app), false));
        lines.Add(new("Click to open in sprig · right-click for actions", OverlayTooltip.MutedColor, false));
        Tooltip().ShowLines(lines, ToScreen(r.Left, r.Bottom + 4));
    }

    // ── tooltip ─────────────────────────────────────────────────────────────────────────────────────────────

    // A line's dwell tooltip: which workspace it is, then every app with its state, then any error.
    private void ShowSprigLineTooltip(int index)
    {
        if (index < 0 || index >= _sprigLineRects.Count) return;
        var lines = new List<OverlayTooltip.Line>();
        if (index >= _sprigLines.Count)
        {
            lines.Add(new("Nothing checked out", OverlayTooltip.FgColor, true));
            lines.Add(new("Click to open sprig and check a branch out", OverlayTooltip.MutedColor, false));
        }
        else
        {
            var w = _sprigLines[index];
            lines.Add(new(OverlayDraw.Truncate(w.Label, 11, 300), OverlayTooltip.FgColor, true));
            string where = w.Name == "main" ? $"{w.Orchestration} · main checkout" : $"{w.Orchestration} · slot {w.Name}";
            lines.Add(new(where + (w.Paused && w.State != SprigWorkspaceState.Paused ? " · paused" : ""), OverlayTooltip.MutedColor, false));
            foreach (var app in w.Apps.Take(12))
                lines.Add(new($"{app.ShortName}  {SprigAppStateText(app)}" + (app.Error is { Length: > 0 } e ? $" · {OverlayDraw.Truncate(e, 10, 200)}" : ""),
                    app.IsOff ? OverlayTooltip.MutedColor : SprigAppColor(app), false));
            if (w.Apps.Count > 12) lines.Add(new($"…and {w.Apps.Count - 12} more", OverlayTooltip.MutedColor, false));
            if (w.Error is { Length: > 0 } error) lines.Add(new("⚠ " + OverlayDraw.Truncate(error, 10, 300), Palette.Yellow, false));
            if (!_sprig.HostRunning && w.State is not (SprigWorkspaceState.Stopped or SprigWorkspaceState.Paused))
                lines.Add(new("Sprig's host isn't running", OverlayTooltip.MutedColor, false));
            lines.Add(new("Click to open in sprig · right-click for actions", OverlayTooltip.MutedColor, false));
        }
        var r = _sprigLineRects[index];
        Tooltip().ShowLines(lines, ToScreen(r.Left + HorizPad, r.Bottom + 4));
    }
}
