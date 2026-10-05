using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's Recent button (docs/session-recovery-plan.md): a clock glyph on the "+ New session" row, beside the
/// Roost button, that opens a flyout of the sessions that ended lately (<see cref="RecentListView"/>) — the ones Perch had
/// open when it closed first, then the rest, newest first — filterable to the interrupted ones and those that ended
/// just before a shutdown. It replaced a Recent section, which was too noisy for anyone running many sessions at once.
///
/// <para>A small warning-hue dot on the glyph says there's a badged line (<see cref="RecentLine.Badged"/>) the user
/// hasn't seen yet (opening the flyout marks them seen, for this run). The lines arrive precomputed
/// (<see cref="SetRecent"/>, notes and ages included) from the app's off-thread Recent build, so paint reads no files
/// and formats nothing.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    private IReadOnlyList<RecentLine> _recentLines = [];
    private bool _recentEnabled = true;
    private bool _hoveredRecent;
    private Rect _recentRect;   // laid out by DrawNewSessionRow each paint; empty when the button is gated off
    private RecentFilter _recentFilter = RecentFilter.All;   // the flyout's last filter, for this run
    private RecentListView? _recentView;   // the open flyout's list, which follows SetRecent while it's up
    private readonly RecentSeen _recentSeen = new();
    private bool _recentBadge;   // RecentSeen.HasUnseen over the lines, kept current so paint doesn't walk them

    /// <summary>A line was clicked: open it in a Perch window, dormant.</summary>
    public event Action<string, string>? RecentResumeRequested;

    /// <summary>A line's "resume in terminal": (session id, cwd).</summary>
    public event Action<string, string>? RecentTerminalRequested;

    /// <summary>A line's "×": (session id).</summary>
    public event Action<string>? RecentDismissRequested;

    /// <summary>"Show all…": the launcher's full list of past sessions.</summary>
    public event Action? RecentMoreRequested;

    // Never in Rearrange preview, where the "+ New session" row is inert chrome (as the Roost button is).
    private bool RecentButtonVisible => _recentEnabled && !RearrangeMode;

    /// <summary>Show/hide the Recent button (R8's setting).</summary>
    public void SetShowRecent(bool enabled)
    {
        if (_recentEnabled == enabled) return;
        _recentEnabled = enabled;
        if (!enabled) _hoveredRecent = false;
        InvalidateVisual();
    }

    /// <summary>Replaces the lines (UI thread); an open flyout follows. Unchanged lines are a no-op.</summary>
    internal void SetRecent(IReadOnlyList<RecentLine> lines)
    {
        if (lines.SequenceEqual(_recentLines)) return;
        _recentLines = lines;
        if (_recentView is not null)
        {
            _recentView.SetLines(lines);
            _recentSeen.MarkSeen(lines);
        }
        _recentBadge = _recentSeen.HasUnseen(lines);
        InvalidateVisual();
    }

    // The button in its box on the "+ New session" row (laid out by DrawNewSessionRow).
    private void DrawRecentButton(DrawingContext ctx, Rect box)
    {
        var c = box.Center;
        if (_hoveredRecent) OverlayDraw.Panel(ctx, box, FeedHoverBrush, null, 5);
        DrawRecentGlyph(ctx, _hoveredRecent ? Palette.AccentBrush : MutedBrush, c.X, c.Y);
        if (_recentBadge) DrawGlyphBadge(ctx, new Point(c.X + 5, c.Y - 4.5), WarnBrush);
    }

    // A clock face: a circle with its hands at ten past twelve; ~11px around (cx, cy), stroked like the Roost glyph.
    private static void DrawRecentGlyph(DrawingContext ctx, IBrush brush, double cx, double cy)
    {
        var pen = OverlayDraw.Pen(brush, 1.2);
        ctx.DrawEllipse(null, pen, new Point(cx, cy), 5.5, 5.5);
        ctx.DrawLine(pen, new Point(cx, cy), new Point(cx, cy - 3.2));
        ctx.DrawLine(pen, new Point(cx, cy), new Point(cx + 2.4, cy + 0.8));
    }

    private bool OverRecentButton(Point p) => ShowFullPanel && _recentRect.Width > 0 && _recentRect.Contains(p);

    // Opens the flyout at the pointer (through ShowFlyout, so a press elsewhere on the overlay closes it).
    private void ShowRecentFlyout()
    {
        var view = new RecentListView(_recentFilter);
        view.SetLines(_recentLines);
        view.FilterChanged += f => _recentFilter = f;
        view.ResumeRequested += (id, cwd) => { _openFlyout?.Hide(); RecentResumeRequested?.Invoke(id, cwd); };
        view.TerminalRequested += (id, cwd) => { _openFlyout?.Hide(); RecentTerminalRequested?.Invoke(id, cwd); };
        view.DismissRequested += id => RecentDismissRequested?.Invoke(id);   // stays open; the push updates the list
        view.ShowAllRequested += () => { _openFlyout?.Hide(); RecentMoreRequested?.Invoke(); };

        var flyout = new Flyout { Content = view };
        flyout.Closed += (_, _) => { if (ReferenceEquals(_recentView, view)) _recentView = null; };
        _recentView = view;
        _recentSeen.MarkSeen(_recentLines);
        _recentBadge = false;
        InvalidateVisual();
        ShowFlyout(flyout);
    }

    // The dwell tooltip: what the button opens, plus what's waiting in it.
    private void ShowRecentTooltip()
    {
        if (_recentRect.Width <= 0) return;
        int interrupted = _recentLines.Count(l => l.Interrupted);
        int shutdown = _recentLines.Count(l => l.BeforeShutdown);
        var lines = new List<OverlayTooltip.Line>
        {
            new("Recent", OverlayTooltip.FgColor, true),
            new(_recentLines.Count == 0 ? "Nothing ended lately" : $"{_recentLines.Count} ended lately", OverlayTooltip.MutedColor, false),
        };
        if (interrupted > 0) lines.Add(new($"{interrupted} interrupted", Palette.WarnBrush.Color, false));
        if (shutdown > 0) lines.Add(new($"{shutdown} before shutdown", Palette.WarnBrush.Color, false));
        Tooltip().ShowLines(lines, ToScreen(_recentRect.Left - 60, _recentRect.Bottom + 4));
    }
}
