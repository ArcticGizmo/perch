using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// Drag-to-reorder for the overlay's movable sections — used <em>only</em> by the Settings live-preview pane
/// (<c>PreviewPane</c>), never the live overlay, so the overlay's normal drag/click routing is untouched.
///
/// <para>Turning <see cref="RearrangeMode"/> on forces every section gate on (so all nine sections lay out
/// against the preview's sample data) and remembers which were <em>off</em> in the user's settings, so those
/// paint dimmed but can still be positioned. A press picks up the section under the pointer; moving shows an
/// accent insertion line; releasing splices the section into its new slot and raises
/// <see cref="SectionOrderChanged"/> for the Settings window to persist. The caller restores the real gates by
/// re-applying settings when it turns the mode back off.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double RearrangeDimOpacity = 0.4;

    private bool _rearrangeMode;
    // Sections OFF in the user's settings when rearrange began — painted dimmed. Hypertree/QuickLinks/Sessions
    // are always treated as on (they have no simple on/off gate on the canvas / are always shown).
    private readonly HashSet<OverlaySection> _rearrangeDimmed = new();

    // The section being dragged (null when idle), the live pointer Y, and the index in _sectionOrder the drop
    // would land at (a value in [0, _sectionOrder.Length]).
    private OverlaySection? _dragSection;
    private double _dragPointerY;
    private int _dropIndex = -1;

    /// <summary>Raised when a drag reorders the sections; carries the new full order for the app to persist.</summary>
    public event Action<IReadOnlyList<OverlaySection>>? SectionOrderChanged;

    /// <summary>Whether the panel is in drag-to-reorder mode (Settings preview only).</summary>
    public bool RearrangeMode
    {
        get => _rearrangeMode;
        set
        {
            if (_rearrangeMode == value) return;
            _rearrangeMode = value;
            _dragSection = null;
            _dropIndex = -1;
            if (value) EnterRearrange();
            else Cursor = Cursor.Default;
            RemeasurePanel();
        }
    }

    // Snapshot which sections are currently off (for dimming), then force every gate on so all nine sections
    // show with the preview's sample data. The order matters: read the real gates before overwriting them.
    private void EnterRearrange()
    {
        _rearrangeDimmed.Clear();
        if (!_showSystemMetrics) _rearrangeDimmed.Add(OverlaySection.SystemInfo);
        if (!UsageStripVisible)  _rearrangeDimmed.Add(OverlaySection.ClaudeMetrics);
        if (!TodosStripVisible)  _rearrangeDimmed.Add(OverlaySection.Todo);
        if (!MediaStripVisible)  _rearrangeDimmed.Add(OverlaySection.Media);
        if (!MicStripVisible)    _rearrangeDimmed.Add(OverlaySection.Call);
        if (!FeedStripVisible)   _rearrangeDimmed.Add(OverlaySection.Friends);

        _showSystemMetrics = true;
        _usageEnabled = true;
        _todosEnabled = true;
        _mediaEnabled = true;
        _micEnabled = true;
        _socialEnabled = true;
        _daemonEnabled = true;
    }

    // ── Pointer routing (called from the main handlers when RearrangeMode is on) ─────────────────────────────
    private void HandleRearrangePressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        double y = e.GetPosition(this).Y;
        if (SectionAt(y) is not { } sec) return;
        _dragSection = sec;
        _dragPointerY = y;
        _dropIndex = Array.IndexOf(_sectionOrder, sec);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private void HandleRearrangeMoved(PointerEventArgs e)
    {
        double y = e.GetPosition(this).Y;
        if (_dragSection is null)
        {
            Cursor = SectionAt(y) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            return;
        }

        _dragPointerY = y;
        _dropIndex = DropIndexAt(y);
        InvalidateVisual();
    }

    private void HandleRearrangeReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_dragSection is { } sec) ApplyDrop(sec, _dropIndex);
        _dragSection = null;
        _dropIndex = -1;
        InvalidateVisual();
    }

    // The visible section whose band contains y, or null.
    private OverlaySection? SectionAt(double y)
    {
        foreach (var s in _sectionOrder)
        {
            if (!_sectionTop.TryGetValue(s, out var top)) continue;
            if (y >= top && y < top + SectionHeight(s)) return s;
        }
        return null;
    }

    // Insertion index: the number of laid-out sections whose vertical midpoint the pointer has passed.
    private int DropIndexAt(double y)
    {
        int idx = 0;
        foreach (var s in _sectionOrder)
        {
            if (!_sectionTop.TryGetValue(s, out var top)) continue;
            if (y >= top + SectionHeight(s) / 2) idx++;
        }
        return idx;
    }

    private void ApplyDrop(OverlaySection sec, int dropIndex)
    {
        var list = _sectionOrder.ToList();
        int from = list.IndexOf(sec);
        if (from < 0) return;

        list.RemoveAt(from);
        int to = dropIndex > from ? dropIndex - 1 : dropIndex;   // removing sec shifts later indices down one
        to = Math.Clamp(to, 0, list.Count);
        list.Insert(to, sec);

        var arr = list.ToArray();
        if (_sectionOrder.AsSpan().SequenceEqual(arr)) return;   // no net move
        _sectionOrder = arr;
        SectionOrderChanged?.Invoke(list);
        RemeasurePanel();
    }

    // ── Painting (called from Draw's body, inside the panel clip) ────────────────────────────────────────────
    private void DrawRearrangeAffordances(DrawingContext ctx, double width)
    {
        var accent = new SolidColorBrush(Palette.Active.Accent.ToColor());

        foreach (var s in _sectionOrder)
        {
            if (!_sectionTop.TryGetValue(s, out var top)) continue;
            double h = SectionHeight(s);
            bool dragging = _dragSection == s;

            // A faint band outline per section, brighter for the one being dragged, so the movable units read.
            var band = new Rect(1.5, top + 0.5, Math.Max(0, width - 3), Math.Max(0, h - 1));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(dragging ? 220 : 46), 255, 255, 255)),
                dragging ? 1.5 : 1);
            ctx.DrawRectangle(dragging ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : null, pen, band);

            // A drag-handle grip (⋮⋮) at the right edge.
            DrawGrip(ctx, width - 13, top + h / 2, MutedBrush);
        }

        // The accent drop-insertion line at the current target slot.
        if (_dragSection is not null && _dropIndex >= 0)
        {
            double y = InsertionY(_dropIndex);
            ctx.DrawLine(new Pen(accent, 2), new Point(6, y), new Point(width - 6, y));
        }
    }

    // The Y at the top of the section currently occupying slot dropIndex, or the bottom of the last section.
    private double InsertionY(int dropIndex)
    {
        int i = 0;
        foreach (var s in _sectionOrder)
        {
            if (!_sectionTop.TryGetValue(s, out var top)) continue;
            if (i == dropIndex) return top;
            i++;
        }
        double bottom = HeaderHeight;
        foreach (var kv in _sectionTop) bottom = Math.Max(bottom, kv.Value + SectionHeight(kv.Key));
        return bottom;
    }

    private static void DrawGrip(DrawingContext ctx, double cx, double cy, IBrush brush)
    {
        const double r = 1.1, gap = 3.4;
        for (int col = 0; col < 2; col++)
            for (int row = -1; row <= 1; row++)
                ctx.DrawEllipse(brush, null, new Point(cx + col * gap, cy + row * gap), r, r);
    }
}
