using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's Recent button (docs/session-recovery-plan.md): a clock glyph on the "+ New session" row, beside the
/// Roost button, that opens a flyout of the sessions that ended lately (<see cref="RecentListView"/>) — the ones Perch had
/// open when it closed first, then the rest, newest first — filterable to the interrupted ones and those that ended
/// just before a shutdown. It replaced a Recent section, which was too noisy for anyone running many sessions at once.
///
/// <para>A small warning-hue dot on the glyph says there's an interrupted / before-shutdown / was-open line the user
/// hasn't seen yet (opening the flyout marks them seen, for this run). The lines arrive precomputed
/// (<see cref="SetRecent"/>, notes and ages included) from the app's off-thread Recent build, so paint reads no files
/// and formats nothing.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    /// <summary>How a Recent line is coloured.</summary>
    internal enum RecentTone
    {
        /// <summary>An ordinary ending.</summary>
        Normal,
        /// <summary>Interrupted, or ended just before a shutdown: badged in the warning hue.</summary>
        Flagged,
        /// <summary>Perch had it open when Perch closed.</summary>
        Perch,
        /// <summary>Ended with <c>/exit</c> — the user meant it, so it's de-emphasised.</summary>
        Faded,
    }

    /// <summary>One Recent line, reduced to what the flyout shows. <paramref name="Folder"/> is the project folder's
    /// name when it differs from the title (null otherwise); <paramref name="Note"/> the trailing label ("interrupted ·
    /// 2h", "3h ago", "was open"). <paramref name="Interrupted"/> and <paramref name="BeforeShutdown"/> are the
    /// flyout's filters (a line a restart cut off is both).</summary>
    internal readonly record struct RecentLine(
        string SessionId, string Cwd, string Title, string? Folder, string Note, RecentTone Tone,
        bool Interrupted, bool BeforeShutdown);

    private IReadOnlyList<RecentLine> _recentLines = [];
    private bool _recentEnabled = true;
    private bool _hoveredRecent;
    private Rect _recentRect;   // captured at paint; empty when the button is gated off
    private RecentFilter _recentFilter = RecentFilter.All;   // the flyout's last filter, for this run
    private RecentListView? _recentView;   // the open flyout's list, which follows SetRecent while it's up
    // Flagged lines the user has seen (the flyout was opened while they were there): they no longer light the badge.
    private readonly HashSet<string> _recentSeen = new(StringComparer.Ordinal);

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
        if (!enabled) { _recentRect = default; _hoveredRecent = false; }
        InvalidateVisual();
    }

    /// <summary>Replaces the lines (UI thread); an open flyout follows. Unchanged lines are a no-op.</summary>
    internal void SetRecent(IReadOnlyList<RecentLine> lines)
    {
        if (lines.SequenceEqual(_recentLines)) return;
        _recentLines = lines;
        _recentView?.SetLines(lines);
        if (_recentView is not null) MarkRecentSeen();
        InvalidateVisual();
    }

    private static bool IsFlaggedRecent(RecentLine l) => l.Interrupted || l.BeforeShutdown;

    // Lit while a flagged line hasn't been seen in the flyout yet.
    private bool RecentBadge()
    {
        foreach (var l in _recentLines)
            if (IsFlaggedRecent(l) && !_recentSeen.Contains(l.SessionId)) return true;
        return false;
    }

    private void MarkRecentSeen()
    {
        foreach (var l in _recentLines)
            if (IsFlaggedRecent(l)) _recentSeen.Add(l.SessionId);
    }

    // The button in its box on the "+ New session" row (laid out by DrawNewSessionRow).
    private void DrawRecentButton(DrawingContext ctx, Rect box)
    {
        _recentRect = box;
        var c = box.Center;
        if (_hoveredRecent) OverlayDraw.Panel(ctx, box, FeedHoverBrush, null, 5);
        DrawRecentGlyph(ctx, _hoveredRecent ? Palette.AccentBrush : MutedBrush, c.X, c.Y);
        if (RecentBadge())
        {
            var dot = new Point(c.X + 5, c.Y - 4.5);
            ctx.DrawEllipse(Palette.FormBgBrush, null, dot, 3.4, 3.4);   // the Roost badge's cut-out ring
            ctx.DrawEllipse(WarnBrush, null, dot, 2.2, 2.2);
        }
    }

    // A clock face: a circle with its hands at ten past twelve; ~11px around (cx, cy), stroked like the Roost glyph.
    private static void DrawRecentGlyph(DrawingContext ctx, IBrush brush, double cx, double cy)
    {
        var pen = OverlayDraw.Pen(brush, 1.2);
        ctx.DrawEllipse(null, pen, new Point(cx, cy), 5.5, 5.5);
        ctx.DrawLine(pen, new Point(cx, cy), new Point(cx, cy - 3.2));
        ctx.DrawLine(pen, new Point(cx, cy), new Point(cx + 2.4, cy + 0.8));
    }

    private bool OverRecentButton(Point p) => ShowFullPanel && RecentButtonVisible && _recentRect.Width > 0 && _recentRect.Contains(p);

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
        MarkRecentSeen();
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
