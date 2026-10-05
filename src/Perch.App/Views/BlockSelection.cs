using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// A character selection that spans every <see cref="SelectableTextBlock"/> under a scroll viewer — something a
/// per-block native selection can't do (a drag can never leave the block it started in, so a heading and the
/// paragraph under it, or two list items, can't be copied together). Modelled on <see cref="DiffView"/>'s char
/// selection: a global (block, char) anchor→focus, painted by an owner-drawn <see cref="Layer"/> through each
/// block's own <c>TextLayout</c> (the same <c>HitTestTextRange</c> the find highlights use).
///
/// <para>The gesture is deferred: a press only records the anchor, and the selection takes over (capturing the
/// pointer to the scroll) once the drag moves past a small slop. Until then the press belongs to the block, so a
/// bare click, a Ctrl+click on a link (<see cref="LinkText"/>) and a double-click word select all behave as
/// before. Ctrl+C (with focus in the scroll) and a right-click inside the selection copy it as Markdown: each
/// rendered Markdown block carries a <see cref="SourceMapProperty"/>, so the selection maps back to the source that
/// produced it (emphasis, links, list markers, fences, table pipes) and a whole message copies exactly as written.
/// "Copy as plain text" on the menu copies the rendered characters instead.</para>
///
/// <para>The host places <see cref="Layer"/> above its content, inside the scrolled content (so it rides along
/// as the view scrolls), and sets <see cref="Root"/> to the subtree whose blocks take part. The blocks are
/// collected at the start of each gesture, so content that grows or re-renders (a streaming thread) needs no
/// bookkeeping; a block that has since left the tree just stops painting.</para>
/// </summary>
internal sealed class BlockSelection
{
    private const double DragSlop = 4;

    /// <summary>Where a rendered Markdown block's text came from in its source (set by <c>MarkdownView</c>), so a
    /// selection over it copies as the Markdown that produced it. A block without one copies its rendered text.</summary>
    public static readonly AttachedProperty<MarkdownSourceMap?> SourceMapProperty =
        AvaloniaProperty.RegisterAttached<BlockSelection, Control, MarkdownSourceMap?>("SourceMap");

    public static void SetSourceMap(Control c, MarkdownSourceMap map) => c.SetValue(SourceMapProperty, map);

    private readonly ScrollViewer _scroll;
    private readonly bool _startInGaps;
    private readonly SelectionLayer _layer;
    private readonly DispatcherTimer _autoScroll;
    private readonly List<SelectableTextBlock> _blocks = new();   // collected at gesture start, in document order

    private bool _pending;        // pressed, not yet dragged past the slop (still the block's own click)
    private bool _dragging;       // the selection owns the gesture (pointer captured to the scroll)
    private Point _pressViewport;
    private Point _dragViewport;  // last drag point in viewport space, for the edge auto-scroll
    private SelectableTextBlock? _pressBlock;
    private IPointer? _captured;  // the pointer captured to the scroll for the drag
    private int _anchorPos = -1, _anchorCh = -1;   // (block index, char in block)
    private int _focusPos = -1, _focusCh = -1;

    /// <summary>The subtree whose selectable text blocks take part. Setting it clears any selection.</summary>
    public Control? Root
    {
        get => _root;
        set { _root = value; Reset(); }
    }
    private Control? _root;

    /// <summary>The owner-drawn overlay painting the selection. The host adds it above its content, in the same
    /// scrolled coordinate space. It's click-through.</summary>
    public Control Layer => _layer;

    /// <summary>The translucent wash painted over selected text.</summary>
    public IBrush Fill
    {
        get => _layer.Fill;
        set { _layer.Fill = value; _layer.InvalidateVisual(); }
    }

    /// <summary>A left press that was released without dragging — the host's click gesture (e.g. the doc
    /// viewer's jump-to-source). Raised whether or not a block handled the release.</summary>
    public event Action<PointerReleasedEventArgs>? Clicked;

    /// <param name="scroll">The scroll viewer hosting the content; it receives the gesture and keyboard copy.</param>
    /// <param name="fill">The selection wash.</param>
    /// <param name="startInGaps">Whether a press between blocks (in padding or a gap) starts a selection too. The
    /// doc preview wants that; a session thread full of buttons and cards only starts one on text.</param>
    public BlockSelection(ScrollViewer scroll, IBrush fill, bool startInGaps)
    {
        _scroll = scroll;
        _startInGaps = startInGaps;
        _layer = new SelectionLayer(this, fill);

        // handledEventsToo: the block under the pointer handles its own press/move (native selection), and a
        // link or file reference may handle the release — the selection still has to see all of them.
        scroll.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Only the scroll's own loss ends the drag — the block it took the capture from loses it too.
        scroll.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) =>
        {
            if (ReferenceEquals(e.Source, scroll)) StopDrag();
        });
        // Tunnel (where the event tunnels), so the selection's menu wins over a block's own native "Copy" flyout
        // (which would copy only its own, empty, native selection) — but only for a right-click inside the
        // selection. Handling it in the tunnel keeps the bubble registration from running twice.
        scroll.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        // Tunnel, so Ctrl+C copies the selection before a focused block's native copy runs.
        scroll.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && TryCopy())
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Focusable so a finished drag can take focus and Ctrl+C reaches the scroll; not a Tab stop.
        scroll.Focusable = true;
        KeyboardNavigation.SetIsTabStop(scroll, false);

        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScroll.Tick += (_, _) => AutoScrollTick();
    }

    /// <summary>True when a non-empty selection exists.</summary>
    public bool HasSelection
    {
        get
        {
            var (sp, sc, ep, ec) = Range();
            return sp >= 0 && !(sp == ep && sc == ec);
        }
    }

    /// <summary>Clears the selection (the next gesture starts fresh).</summary>
    public void Clear()
    {
        _anchorPos = _anchorCh = _focusPos = _focusCh = -1;
        _layer.InvalidateVisual();
    }

    /// <summary>Drops any gesture in flight and the selection — the content was rebuilt or the host is closing.</summary>
    public void Reset()
    {
        _pending = false;
        StopDrag();
        _blocks.Clear();
        Clear();
    }

    /// <summary>Copies the selection to the clipboard — as Markdown (the default), or as its rendered characters.
    /// False when nothing is selected, so a caller's Ctrl+C can fall through to whatever else holds a selection.</summary>
    public bool TryCopy(bool markdown = true)
    {
        if (SelectedText(markdown) is not { } text)
            return false;
        TopLevel.GetTopLevel(_scroll)?.Clipboard?.SetTextAsync(text);
        return true;
    }

    /// <summary>The selection's text. As Markdown, rendered Markdown blocks copy the source that produced the
    /// selected characters (see <see cref="MarkdownCopy.Compose"/>) — a whole message copies exactly as written;
    /// other text copies as rendered. Null when nothing is selected.</summary>
    public string? SelectedText(bool markdown = true)
    {
        var (sp, sc, ep, ec) = Range();
        if (sp < 0 || (sp == ep && sc == ec))
            return null;
        var pieces = new List<MarkdownCopy.Piece>();
        for (int i = sp; i <= ep && i < _blocks.Count; i++)
        {
            var tb = _blocks[i];
            if (!tb.IsEffectivelyVisible)
                continue;   // a collapsed section's text isn't part of what was selected on screen
            string c = TextOf(tb);
            int start = i == sp ? Math.Clamp(sc, 0, c.Length) : 0;
            int end = i == ep ? Math.Clamp(ec, 0, c.Length) : c.Length;
            if (end > start)
                pieces.Add(new MarkdownCopy.Piece(tb.GetValue(SourceMapProperty), c, start, end));
        }
        string text = MarkdownCopy.Compose(pieces, markdown);
        return text.Length > 0 ? text : null;
    }

    // ── Gesture ──────────────────────────────────────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_scroll);
        if (!point.Properties.IsLeftButtonPressed)
            return;   // a right-click keeps the selection for its menu
        _pending = false;

        var source = e.Source as Visual;
        // Don't hijack a scrollbar-thumb drag (the outer scroll's or a code panel's).
        if (source?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            Clear();
            return;
        }

        var pView = point.Position;
        // Shift+click extends the current selection to the pointer, and the drag carries on from there.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && HasSelection && Nearest(pView) is { } ext)
        {
            if (source?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is { } stb)
                stb.SelectionEnd = stb.SelectionStart;   // the block's own shift-extend; ours replaces it
            (_focusPos, _focusCh) = ext;
            BeginDrag(e.Pointer, pView);
            return;
        }

        Clear();
        // A double/triple-click is the block's own word/line select; a press off text in a thread is a click on
        // a card, a button or empty space — neither starts a selection.
        _pressBlock = source?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        if (e.ClickCount >= 2 || (_pressBlock is null && !_startInGaps))
            return;

        Collect();
        if (Nearest(pView) is not { } hit)
            return;
        (_anchorPos, _anchorCh) = hit;
        (_focusPos, _focusCh) = hit;
        _pressViewport = pView;
        _pending = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pending && !_dragging)
            return;
        var point = e.GetCurrentPoint(_scroll);
        if (!point.Properties.IsLeftButtonPressed)
        {
            // The release went missing (capture lost mid-gesture): end it.
            _pending = false;
            StopDrag();
            return;
        }
        var pView = point.Position;
        if (_pending)
        {
            if (Dist(pView, _pressViewport) < DragSlop)
                return;
            _pending = false;
            // The block's native selection grew a little before the slop was crossed; ours replaces it.
            if (_pressBlock is { } tb) tb.SelectionEnd = tb.SelectionStart;
            BeginDrag(e.Pointer, pView);
        }
        _dragViewport = pView;
        ExtendTo(pView);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pending)
        {
            _pending = false;
            if (e.InitialPressMouseButton == MouseButton.Left)
                Clicked?.Invoke(e);
            return;
        }
        if (!_dragging)
            return;
        StopDrag();
        // A real selection takes focus so Ctrl+C reaches it; a drag that came back to its start is nothing.
        if (HasSelection) _scroll.Focus();
        else Clear();
    }

    private void BeginDrag(IPointer pointer, Point pView)
    {
        _dragging = true;
        _dragViewport = pView;
        _captured = pointer;
        pointer.Capture(_scroll);   // the rest of the gesture routes here, whichever block it crosses
        _autoScroll.Start();
        _layer.InvalidateVisual();
    }

    private void StopDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        _autoScroll.Stop();
        // Release only our own capture (a capture-lost callback has already lost it).
        if (_captured is { } p && ReferenceEquals(p.Captured, _scroll))
            p.Capture(null);
        _captured = null;
    }

    private void ExtendTo(Point pView)
    {
        if (Nearest(pView) is not { } hit || (hit.Pos == _focusPos && hit.Ch == _focusCh))
            return;
        (_focusPos, _focusCh) = hit;
        _layer.InvalidateVisual();
    }

    // While dragging, scroll when the pointer nears the viewport's top/bottom edge, then re-resolve the focus
    // against the (fixed-on-screen) pointer so the selection keeps growing as the content scrolls under it.
    private void AutoScrollTick()
    {
        if (!_dragging) { _autoScroll.Stop(); return; }
        const double edge = 24, step = 14;
        double max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double y = _dragViewport.Y;
        if (y < edge && _scroll.Offset.Y > 0)
            _scroll.Offset = _scroll.Offset.WithY(Math.Max(0, _scroll.Offset.Y - step));
        else if (y > _scroll.Viewport.Height - edge && _scroll.Offset.Y < max)
            _scroll.Offset = _scroll.Offset.WithY(Math.Min(max, _scroll.Offset.Y + step));
        else
            return;
        ExtendTo(_dragViewport);
    }

    // A right-click inside the selection offers "Copy" (plus the host's entries). Anywhere else the click is
    // left to the block under it (a file reference's menu, a block's own flyout).
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!HasSelection)
            return;
        if (e.TryGetPosition(_scroll, out var p) && !Contains(p))
            return;
        var items = new List<Control>
        {
            MenuItem("Copy", () => TryCopy()),
            MenuItem("Copy as plain text", () => TryCopy(markdown: false)),
        };
        new MenuFlyout { ItemsSource = items }.ShowAt(_scroll, showAtPointer: true);
        e.Handled = true;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Whether a viewport point falls on selected text.
    private bool Contains(Point pView)
    {
        var (sp, sc, ep, ec) = Range();
        if (sp < 0 || Nearest(pView) is not { } hit)
            return false;
        return Compare(hit.Pos, hit.Ch, sp, sc) >= 0 && Compare(hit.Pos, hit.Ch, ep, ec) <= 0;
    }

    private static int Compare(int posA, int chA, int posB, int chB) =>
        posA != posB ? posA.CompareTo(posB) : chA.CompareTo(chB);

    // ── Geometry ─────────────────────────────────────────────────────────────────────────────────────

    private void Collect()
    {
        _blocks.Clear();
        if (_root is not null)
            Collect(_root);
    }

    // Depth-first in logical order (= reading order for these stacked layouts). Recursion stops at a block,
    // whose logical children are its own inline widgets.
    private void Collect(ILogical node)
    {
        foreach (var child in node.LogicalChildren)
        {
            if (child is SelectableTextBlock tb) _blocks.Add(tb);
            else Collect(child);
        }
    }

    // The nearest block to a viewport point, plus the caret char within it: a block whose (visible) vertical
    // band holds the point — the horizontally nearest of them, so a table row resolves to the right cell — else
    // the last block that starts above it (a point in the gap after a block lands at that block's end), else the
    // first visible block. Null when there are no blocks on show.
    private (int Pos, int Ch)? Nearest(Point pView)
    {
        int inBand = -1, above = -1, first = -1;
        double bestDx = double.MaxValue;
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (VisibleRect(_blocks[i], _scroll) is not { } r)
                continue;
            if (first < 0) first = i;
            if (pView.Y >= r.Top && pView.Y <= r.Bottom)
            {
                double dx = pView.X < r.Left ? r.Left - pView.X : pView.X > r.Right ? pView.X - r.Right : 0;
                if (dx < bestDx) { bestDx = dx; inBand = i; }
            }
            else if (r.Top <= pView.Y)
                above = i;
        }
        int chosen = inBand >= 0 ? inBand : above >= 0 ? above : first;
        if (chosen < 0)
            return null;
        var tb = _blocks[chosen];
        int ch = _scroll.TranslatePoint(pView, tb) is { } pInText ? CharIndexAt(tb, pInText, TextOf(tb).Length) : 0;
        return (chosen, ch);
    }

    // The part of a block actually on show, in <paramref name="target"/>'s space: its bounds cut by every clipping
    // ancestor inside the content (a code panel's sideways scroller, a tool output's capped viewport), so text
    // scrolled away inside its own panel neither paints its selection outside the panel nor claims points over
    // neighbouring content. Null when it's hidden or entirely clipped away.
    private Rect? VisibleRect(SelectableTextBlock tb, Visual target)
    {
        if (!tb.IsEffectivelyVisible || tb.TranslatePoint(default, target) is not { } tl)
            return null;
        var r = new Rect(tl, tb.Bounds.Size);
        var stop = _scroll.Content as Visual;
        for (var v = tb.GetVisualParent(); v is not null && v != stop && v != _scroll; v = v.GetVisualParent())
        {
            if (!v.ClipToBounds || v.TranslatePoint(default, target) is not { } o)
                continue;
            r = r.Intersect(new Rect(o, v.Bounds.Size));
            if (r.Height <= 0)
                return null;
        }
        return r;
    }

    // The caret char index for a point in a block's own space. The TextLayout is laid out inside the padding.
    private static int CharIndexAt(SelectableTextBlock tb, Point p, int maxLen)
    {
        if (tb.TextLayout is not { } layout)
            return 0;
        var hit = layout.HitTestPoint(new Point(p.X - tb.Padding.Left, p.Y - tb.Padding.Top));
        return Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, maxLen);
    }

    // The selection in document order: (startBlock, startChar, endBlock, endChar); startBlock < 0 when empty.
    private (int SP, int SC, int EP, int EC) Range()
    {
        if (_anchorPos < 0 || _focusPos < 0)
            return (-1, -1, -1, -1);
        return Compare(_anchorPos, _anchorCh, _focusPos, _focusCh) <= 0
            ? (_anchorPos, _anchorCh, _focusPos, _focusCh)
            : (_focusPos, _focusCh, _anchorPos, _anchorCh);
    }

    // A block's text as its TextLayout counts it — Text, or the flattened inlines (each InlineUIContainer one
    // position) — so hit-test char offsets index straight into it.
    private static string TextOf(SelectableTextBlock tb)
    {
        if (tb.Inlines is not { Count: > 0 } inlines)
            return tb.Text ?? "";
        var sb = new StringBuilder();
        Flatten(inlines, sb);
        return sb.ToString();
    }

    private static void Flatten(InlineCollection inlines, StringBuilder sb)
    {
        foreach (var inline in inlines)
            switch (inline)
            {
                case Run r: sb.Append(r.Text); break;
                case LineBreak: sb.Append('\n'); break;
                case InlineUIContainer: sb.Append('￼'); break;   // one layout position (object replacement)
                case Span s: Flatten(s.Inlines, sb); break;
            }
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // Paint the selection across every block in its range — the first from the start char, the last up to the
    // end char, the ones between in full — one rect per visual row, clipped to each block's visible part.
    private void Paint(DrawingContext ctx, Visual layer, IBrush fill)
    {
        var (sp, sc, ep, ec) = Range();
        if (sp < 0)
            return;
        for (int i = sp; i <= ep && i < _blocks.Count; i++)
        {
            var tb = _blocks[i];
            if (tb.TextLayout is not { } layout || VisibleRect(tb, layer) is not { } clip)
                continue;
            int len = TextOf(tb).Length;
            int start = i == sp ? Math.Clamp(sc, 0, len) : 0;
            int end = i == ep ? Math.Clamp(ec, 0, len) : len;
            if (end <= start || tb.TranslatePoint(new Point(tb.Padding.Left, tb.Padding.Top), layer) is not { } o)
                continue;
            using (ctx.PushClip(clip))
                foreach (var r in layout.HitTestTextRange(start, end - start))
                    ctx.FillRectangle(fill, new Rect(o.X + r.X, o.Y + r.Y, r.Width, r.Height));
        }
    }

    // A translucent, click-through overlay above the content (so the wash also shows over blocks with opaque
    // backgrounds — code, tables). Repaints after layout passes (reflow, streaming growth) while a selection is up.
    private sealed class SelectionLayer : Control
    {
        private readonly BlockSelection _owner;
        public IBrush Fill;

        public SelectionLayer(BlockSelection owner, IBrush fill)
        {
            _owner = owner;
            Fill = fill;
            IsHitTestVisible = false;
            LayoutUpdated += (_, _) => { if (_owner._anchorPos >= 0) InvalidateVisual(); };
        }

        public override void Render(DrawingContext context) => _owner.Paint(context, this, Fill);
    }
}
