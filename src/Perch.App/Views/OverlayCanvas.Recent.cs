using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's "Recent" section (docs/session-recovery-plan.md, R7): sessions that ended lately — the ones Perch had
/// open when it closed first, then those a restart interrupted or that ended just before a shutdown, then the rest,
/// newest first. It follows the standard collapsible-section pattern (chevron header, persisted expand state; see
/// <c>OverlayCanvas.Todos.cs</c>) and the section-order rules.
///
/// <para>A click on a line resumes it in Perch (dormant: the conversation opens, Claude starts on the first send); a
/// right-click offers Resume in Perch / Resume in terminal / Dismiss; hovering a line swaps its note for a "×" that
/// dismisses it. The lines arrive precomputed (<see cref="SetRecent"/>, notes and ages included) from the app's
/// off-thread Recent build, so paint reads no files and formats nothing.</para>
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

    /// <summary>One Recent line, reduced to what the section paints. <paramref name="Folder"/> is the project folder's
    /// name when it differs from the title (null otherwise); <paramref name="Note"/> the trailing label ("interrupted ·
    /// 2h", "3h ago", "was open").</summary>
    internal readonly record struct RecentLine(string SessionId, string Cwd, string Title, string? Folder, string Note, RecentTone Tone);

    private IReadOnlyList<RecentLine> _recentLines = [];
    private int _recentMore;   // rows the cap left out: the "show +N more" line opens the launcher's full list
    private int _hoveredRecentRow = -1;
    private bool _hoveredRecentHeader, _hoveredRecentDismiss;
    private bool _recentEnabled = true;
    private bool _recentExpanded = true;
    private Rect _recentHeaderRect;
    private double _recentWidth;   // the width the strip was last painted at (where a line's "×" sits)

    /// <summary>A line was clicked (or "Resume in Perch" picked): open it in a Perch window, dormant.</summary>
    public event Action<string, string>? RecentResumeRequested;

    /// <summary>"Resume in terminal" on a line: (session id, cwd).</summary>
    public event Action<string, string>? RecentTerminalRequested;

    /// <summary>A line's "×" or "Dismiss": (session id).</summary>
    public event Action<string>? RecentDismissRequested;

    /// <summary>The "show +N more" line: the launcher's full list of past sessions.</summary>
    public event Action? RecentMoreRequested;

    /// <summary>The header was clicked to expand or collapse — the app persists it.</summary>
    public event Action<bool>? RecentExpandChanged;

    // Shown while there's something recent (in Rearrange mode always, so it can be placed).
    private bool RecentStripVisible => _recentEnabled && (_recentLines.Count > 0 || RearrangeMode);
    private bool RecentOverflow => _recentMore > 0;
    private int RecentLineCount => _recentLines.Count == 0 ? 1 : _recentLines.Count + (RecentOverflow ? 1 : 0);
    private double RecentHeaderHeight => FeedCaptionHeight + 12;   // matches the Todo / Friends headers
    private double RecentBodyHeight => !_recentExpanded ? 0 : RecentLineCount * HypertreeLineHeight + 6;
    private double RecentStripHeight => !RecentStripVisible ? 0 : RecentHeaderHeight + RecentBodyHeight;
    private double RecentTop => _sectionTop.GetValueOrDefault(Perch.Data.OverlaySection.Recent);

    /// <summary>Show/hide the whole section (R8's setting). Changes the panel height, so relayout.</summary>
    public void SetShowRecent(bool enabled)
    {
        if (_recentEnabled == enabled) return;
        _recentEnabled = enabled;
        RemeasurePanel();
    }

    /// <summary>The section's initial expand state (from AppSettings), without raising the change event.</summary>
    public void SetRecentExpanded(bool expanded)
    {
        if (_recentExpanded == expanded) return;
        _recentExpanded = expanded;
        if (RecentStripVisible) RemeasurePanel();
    }

    /// <summary>Replaces the lines (UI thread). Unchanged lines are a no-op; a different line count relayouts.</summary>
    internal void SetRecent(IReadOnlyList<RecentLine> lines, int more)
    {
        if (more == _recentMore && lines.SequenceEqual(_recentLines)) return;
        bool wasVisible = RecentStripVisible;
        int before = RecentLineCount;
        _recentLines = lines;
        _recentMore = more;
        _hoveredRecentRow = -1;
        _hoveredRecentDismiss = false;
        if (RecentStripVisible != wasVisible || (_recentExpanded && RecentLineCount != before)) RemeasurePanel();
        else if (RecentStripVisible) InvalidateVisual();
    }

    private void OnRecentHeaderClicked()
    {
        _recentExpanded = !_recentExpanded;
        RecentExpandChanged?.Invoke(_recentExpanded);
        RemeasurePanel();
    }

    private void DrawRecentStrip(DrawingContext ctx, double width, double top)
    {
        DrawRecentHeader(ctx, width, top);
        _recentWidth = width;
        if (!_recentExpanded) return;

        double y = top + RecentHeaderHeight;
        double lineH = HypertreeLineHeight;
        const double DotR = 3, NoteMaxW = 96, Box = 16;
        double nameX = HorizPad + DotR * 2 + 6;
        var hover = OverlayDraw.Brush(Color.FromArgb(28, 255, 255, 255));

        // Only in Rearrange mode with nothing to show (the section is hidden otherwise).
        if (_recentLines.Count == 0)
        {
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("nothing recent", HyperRowSize, MutedBrush), nameX, y + lineH / 2);
            return;
        }

        for (int i = 0; i < _recentLines.Count; i++)
        {
            var l = _recentLines[i];
            double midY = y + lineH / 2;
            bool hot = _hoveredRecentRow == i;
            if (hot) ctx.FillRectangle(hover, new Rect(4, y, Math.Max(0, width - 8), lineH));

            var (dot, note) = l.Tone switch
            {
                RecentTone.Flagged => (WarnBrush, WarnBrush),
                RecentTone.Perch => (RemoteBrush, RemoteBrush),
                RecentTone.Faded => (MutedBrush, MutedBrush),
                _ => (BotBrush, MutedBrush),
            };
            ctx.DrawEllipse(dot, null, new Point(HorizPad + DotR, midY), DotR, DotR);

            // Trailing: the note, or — hovered — a "×" that dismisses the line.
            double reserve;
            if (hot)
            {
                double cx = width - HorizPad - Box / 2 + 2;
                var box = new Rect(cx - Box / 2, midY - Box / 2, Box, Box);
                if (_hoveredRecentDismiss) OverlayDraw.Panel(ctx, box, FeedHoverBrush, null, 5);
                var x = OverlayDraw.Text("×", HyperRowSize + 1, _hoveredRecentDismiss ? FgBrush : MutedBrush);
                OverlayDraw.TextLeftMid(ctx, x, cx - x.Width / 2, midY);
                reserve = Box + 6;
            }
            else
            {
                var noteText = OverlayDraw.Truncate(l.Note, HyperMetaSize, NoteMaxW);
                var noteFt = OverlayDraw.Text(noteText, HyperMetaSize, note);
                OverlayDraw.TextLeftMid(ctx, noteFt, width - HorizPad - noteFt.Width, midY);
                reserve = noteText.Length > 0 ? noteFt.Width + 8 : 0;
            }

            // The title gives way first: the folder's name (the tail of the path) stays legible.
            double avail = Math.Max(20, width - HorizPad - nameX - reserve);
            double folderW = 0;
            FormattedText? folderFt = null;
            if (l.Folder is { Length: > 0 } folder)
            {
                folderFt = OverlayDraw.Text(OverlayDraw.Truncate(folder, HyperMetaSize, avail * 0.45), HyperMetaSize, MutedBrush);
                folderW = folderFt.Width + 6;
            }
            var nameBrush = hot ? FgBrush : l.Tone == RecentTone.Faded ? MutedBrush : BotBrush;
            var nameFt = OverlayDraw.Text(OverlayDraw.Truncate(l.Title, HyperRowSize, Math.Max(20, avail - folderW)), HyperRowSize, nameBrush);
            OverlayDraw.TextLeftMid(ctx, nameFt, nameX, midY);
            if (folderFt is not null) OverlayDraw.TextLeftMid(ctx, folderFt, nameX + nameFt.Width + 6, midY);

            y += lineH;
        }

        if (RecentOverflow)
        {
            bool hot = _hoveredRecentRow == _recentLines.Count;
            if (hot) ctx.FillRectangle(hover, new Rect(4, y, Math.Max(0, width - 8), lineH));
            var moreFt = OverlayDraw.Text($"show +{_recentMore} more", HyperRowSize, hot ? FgBrush : MutedBrush);
            OverlayDraw.TextLeftMid(ctx, moreFt, nameX, y + lineH / 2);
        }
    }

    // Chevron + "Recent" caption; collapsed, a count on the right (the flagged ones, if any, in the warning hue).
    private void DrawRecentHeader(DrawingContext ctx, double width, double top)
    {
        double midY = top + 6 + FeedCaptionHeight / 2;
        if (_hoveredRecentHeader)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), RecentHeaderHeight - 6),
                FeedHoverBrush, null, 6);

        DrawChevron(ctx, HorizPad + 4, midY, _recentExpanded);
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("Recent", FeedCaptionSize, MutedBrush, FontWeight.SemiBold), HorizPad + 14, midY);

        if (!_recentExpanded && _recentLines.Count > 0)
        {
            int flagged = 0;
            foreach (var l in _recentLines) if (l.Tone == RecentTone.Flagged) flagged++;
            var countFt = flagged > 0
                ? OverlayDraw.Text(flagged == 1 ? "1 interrupted" : $"{flagged} interrupted", FeedCaptionSize, WarnBrush)
                : OverlayDraw.Text($"{_recentLines.Count + _recentMore}", FeedCaptionSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, countFt, width - HorizPad - countFt.Width, midY);
        }

        _recentHeaderRect = new Rect(0, top, width, RecentHeaderHeight);
    }

    // The body line under p — an index into the lines, or the line count for "show +N more" — or -1.
    private int HitTestRecentRow(Point p)
    {
        if (!(ShowFullPanel && RecentStripVisible && _recentExpanded) || _recentLines.Count == 0) return -1;
        double top = RecentTop + RecentHeaderHeight;
        double lineH = HypertreeLineHeight;
        int count = _recentLines.Count + (RecentOverflow ? 1 : 0);
        if (p.Y < top || p.Y >= top + count * lineH) return -1;
        int index = (int)((p.Y - top) / lineH);
        return index >= 0 && index < count ? index : -1;
    }

    // Hover: the line, its "×", and the header. Returns whether anything changed.
    private bool UpdateRecentHover(Point p)
    {
        int row = HitTestRecentRow(p);
        bool header = _recentHeaderRect.Width > 0 && _recentHeaderRect.Contains(p) && RecentStripVisible && ShowFullPanel;
        // The "×" is laid out by the paint for the hovered line; a line just entered has none yet, so test the box
        // where it will be.
        bool dismiss = row >= 0 && row < _recentLines.Count && RecentDismissBox(row).Contains(p);
        if (row == _hoveredRecentRow && header == _hoveredRecentHeader && dismiss == _hoveredRecentDismiss) return false;
        _hoveredRecentRow = row;
        _hoveredRecentHeader = header;
        _hoveredRecentDismiss = dismiss;
        return true;
    }

    private Rect RecentDismissBox(int row)
    {
        const double Box = 16;
        double midY = RecentTop + RecentHeaderHeight + row * HypertreeLineHeight + HypertreeLineHeight / 2;
        double cx = _recentWidth - HorizPad - Box / 2 + 2;
        return new Rect(cx - Box / 2, midY - Box / 2, Box, Box);
    }

    private bool ClearRecentHover()
    {
        bool changed = _hoveredRecentRow != -1 || _hoveredRecentHeader || _hoveredRecentDismiss;
        _hoveredRecentRow = -1;
        _hoveredRecentHeader = _hoveredRecentDismiss = false;
        return changed;
    }

    // Left click: the header toggles, "×" dismisses, "show +N more" opens the launcher, a line resumes in Perch.
    private bool RouteRecentClick(Point p)
    {
        if (!RecentStripVisible || !ShowFullPanel) return false;
        if (_recentHeaderRect.Width > 0 && _recentHeaderRect.Contains(p)) { OnRecentHeaderClicked(); return true; }
        int row = HitTestRecentRow(p);
        if (row < 0) return false;
        if (row == _recentLines.Count) { RecentMoreRequested?.Invoke(); return true; }
        var l = _recentLines[row];
        if (RecentDismissBox(row).Contains(p)) RecentDismissRequested?.Invoke(l.SessionId);
        else RecentResumeRequested?.Invoke(l.SessionId, l.Cwd);
        return true;
    }

    // Right click on a line: its actions.
    private bool ShowRecentMenuAt(Point p)
    {
        int row = HitTestRecentRow(p);
        if (row < 0) return false;
        if (row == _recentLines.Count)
        {
            ShowFlyout(new List<Control> { MenuItem("Show all past sessions…", () => RecentMoreRequested?.Invoke()) });
            return true;
        }
        var l = _recentLines[row];
        ShowFlyout(new List<Control>
        {
            MenuItem("Resume in Perch", () => RecentResumeRequested?.Invoke(l.SessionId, l.Cwd)),
            MenuItem("Resume in terminal", () => RecentTerminalRequested?.Invoke(l.SessionId, l.Cwd)),
            new Separator(),
            MenuItem("Dismiss", () => RecentDismissRequested?.Invoke(l.SessionId)),
        });
        return true;
    }
}
