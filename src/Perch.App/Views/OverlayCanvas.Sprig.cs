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
    private static int SprigOrder(SprigWorkspace w) => w.State switch
    {
        SprigWorkspaceState.Failed => 0,
        SprigWorkspaceState.Running when w.AnyFailed => 0,
        SprigWorkspaceState.Starting => 1,
        SprigWorkspaceState.Running => 2,
        SprigWorkspaceState.Paused => 3,
        SprigWorkspaceState.Stopped => 4,
        _ => 5,
    };

    private static Color SprigStateColor(SprigWorkspaceState s) => s switch
    {
        SprigWorkspaceState.Running => Palette.Green,
        SprigWorkspaceState.Starting => Palette.Accent,
        SprigWorkspaceState.Paused => Palette.Yellow,
        SprigWorkspaceState.Failed => Palette.Red,
        _ => IdleColor,
    };

    private static Color SprigAppColor(SprigApp a)
        => a.IsReady ? Palette.Green : a.IsFailed ? Palette.Red : a.IsBusy ? Palette.Accent
         : a.State == "restarting" ? Palette.Yellow : IdleColor;

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
        DrawSprigMark(ctx, HorizPad + 17, midY, _sprigFeed == SprigFeed.Live ? Palette.Green : IdleColor);
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

        int failed = _sprigLines.Count(w => w.State == SprigWorkspaceState.Failed);
        int starting = _sprigLines.Count(w => w.State == SprigWorkspaceState.Starting);
        int running = _sprigLines.Count(w => w.State == SprigWorkspaceState.Running);
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

        ctx.DrawEllipse(OverlayDraw.Brush(SprigStateColor(w.State)), null, new Point(HorizPad + 17, midY), 3.2, 3.2);

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
        int active = w.ActiveCount;
        int failedApps = w.Apps.Count(a => a.IsFailed);
        switch (w.State)
        {
            case SprigWorkspaceState.Running when failedApps > 0:
                return (failedApps == 1 ? "1 app failed" : $"{failedApps} apps failed", Palette.Red);
            case SprigWorkspaceState.Running:
                return (active > 0 ? $"{w.ReadyCount}/{active} ready" : "running", w.ReadyCount == active ? Palette.Green : muted);
            case SprigWorkspaceState.Starting:
                return (active > 0 ? $"starting {w.ReadyCount}/{active}" : "starting", Palette.Accent);
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

    // A small sprout: a stem with a leaf either side, ~12px around (cx, cy).
    private static void DrawSprigMark(DrawingContext ctx, double cx, double cy, Color color)
    {
        var brush = OverlayDraw.Brush(color);
        ctx.DrawLine(OverlayDraw.Pen(brush, 1.3, null, PenLineCap.Round), new Point(cx, cy + 5.5), new Point(cx, cy - 1));
        using (At(ctx, cx, cy))
        {
            ctx.DrawGeometry(brush, null, SprigLeafLeft);
            ctx.DrawGeometry(brush, null, SprigLeafRight);
        }
    }

    private static readonly Geometry SprigLeafLeft = GlyphShape(g =>
    {
        g.BeginFigure(new Point(0, 2), isFilled: true);
        g.QuadraticBezierTo(new Point(-6, 1.5), new Point(-5.5, -3.5));
        g.QuadraticBezierTo(new Point(-1, -3), new Point(0, 2));
        g.EndFigure(true);
    });

    private static readonly Geometry SprigLeafRight = GlyphShape(g =>
    {
        g.BeginFigure(new Point(0, -0.5), isFilled: true);
        g.QuadraticBezierTo(new Point(5.5, -1), new Point(5, -6));
        g.QuadraticBezierTo(new Point(0.5, -5.5), new Point(0, -0.5));
        g.EndFigure(true);
    });

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
            open.Add(MenuItem($"Open {app.ShortName}  ({url[7..]})", () => PlatformServices.UrlOpener.Open(url)));
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
        var color = w.State == SprigWorkspaceState.Running && w.AnyFailed ? Palette.Red : SprigStateColor(w.State);
        DrawSprigMark(ctx, x + 5, midY, color);
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
