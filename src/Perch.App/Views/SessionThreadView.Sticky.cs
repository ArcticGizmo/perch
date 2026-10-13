using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

// Sticky card headers (docs/sticky-card-headers-plan.md). While the viewport's top line sits inside a tall, open
// collapsible card (an Edit diff, a thought, an expanded tool fold), a copy of that card's header is pinned to the top
// of the view, with collapse, jump-to-start and jump-past-end, so a long body never loses its context. A card inside an
// open fold pins as a second row under the fold's. Each row matches its card's left edge and width, and is pushed up by
// the card's bottom edge as it scrolls away.
internal sealed partial class SessionThreadView
{
    // A card shorter than this never pins: its header is never far off, and a strip flashing over every ordinary card
    // as it scrolls past would be noise.
    private const double MinStickyHeight = 220;

    // Collapsing a pinned card, or jumping to its start, parks its header this far below the viewport top.
    private const double StickyLandGap = 8;

    // Topmost layer of the scrolled content: rows are positioned in content coordinates, at the viewport's top.
    private readonly Canvas _stickyLayer = new();
    private StickyRow? _outerRow, _innerRow;

    // A collapsible card the strip can pin. Registered on its item's ItemView when built, so virtualization drops and
    // rebuilds the registry along with the controls (a card in view is never dehydrated).
    private sealed class Sticky
    {
        public required Control Card;               // the card's outer control: its bounds are the span it pins over
        public required Func<bool> IsOpen;
        public required Action Collapse;
        public required string Glyph;               // the tool icon, or "" for none
        public required Func<StickyLabel> Label;    // re-read on each rebind, so a status flip shows
        public Sticky? Parent;                      // the fold a card sits in
    }

    // What a pinned row says: a verb + object (the object elides its directory when it's a path), and a status.
    private readonly record struct StickyLabel(
        string Verb, string Object, bool ObjectIsPath = false, bool ObjectMono = true,
        string? Status = null, IBrush? StatusBrush = null);

    /// <summary>Render mode only: open every fold and every expandable tool card, so a capture can show nested rows.</summary>
    internal void ExpandAllForRender()
    {
        foreach (var view in _views.Values)
        {
            foreach (var group in view.Groups.Values.Distinct()) group.SetExpanded(true);
            foreach (var card in view.Tools.Values) card.Expand();
        }
    }

    /// <summary>Render mode only: open every agent report notice (the state a click on its header sets) and rebuild it.</summary>
    internal void OpenReportsForRender()
    {
        foreach (var item in _views.Keys.OfType<TaskNoticeItem>().Where(n => n.Task.Report is not null).ToList())
        {
            _expandState[item] = true;
            UpdateItem(item);
        }
    }

    /// <summary>Render mode only: scroll so the viewport top sits <paramref name="into"/> DIPs inside the first open agent
    /// report card. False when none is built.</summary>
    internal bool ScrollIntoReportForRender(double into)
    {
        foreach (var (item, view) in _views)
            if (item is TaskNoticeItem && view.Stickies.FirstOrDefault() is { } s && TryCardRect(s, out var r))
            {
                _stickToBottom = false;
                Offset = new Vector(Offset.X, r.Top + into);
                UpdateSticky();
                return true;
            }
        return false;
    }

    /// <summary>Render mode only: scroll so the viewport top sits <paramref name="into"/> DIPs inside the first tool
    /// card for <paramref name="toolName"/>. False when no such card is built.</summary>
    internal bool ScrollIntoToolForRender(string toolName, double into)
    {
        foreach (var view in _views.Values)
            foreach (var (part, card) in view.Tools)
                if (part.ToolName == toolName && TryCardRect(card.Sticky, out var r))
                {
                    _stickToBottom = false;
                    Offset = new Vector(Offset.X, r.Top + into);
                    return true;
                }
        return false;
    }

    // Re-derives which card(s) the viewport top is inside and places their rows. Called on every scroll/growth (so it
    // stays cheap: a walk of the column's wrappers, then only the open cards of the one item under the top line) and,
    // with relabel, after each batch of model updates so a pinned card's status follows the card.
    private void UpdateSticky(bool relabel = false)
    {
        double y = Offset.Y;
        if (Viewport.Height <= 0 || ViewAt(y) is not { Stickies.Count: > 0 } view)
        {
            HideRow(_outerRow);
            HideRow(_innerRow);
            return;
        }

        Sticky? outer = null, inner = null;
        Rect outerRect = default, innerRect = default;
        foreach (var s in view.Stickies)
        {
            if (!s.IsOpen() || !TryCardRect(s, out var r)) continue;
            if (r.Height < MinStickyHeight || r.Top >= y || r.Bottom <= y) continue;
            if (s.Parent is null) (outer, outerRect) = (s, r);
            else (inner, innerRect) = (s, r);
        }
        if (outer is null && inner is not null)   // can't happen (a fold encloses its card), but stay sane if it does
            (outer, outerRect, inner) = (inner, innerRect, null);

        if (outer is null) { HideRow(_outerRow); HideRow(_innerRow); return; }
        double below = PlaceRow(ref _outerRow, outer, outerRect, y, relabel);
        if (inner is null) HideRow(_innerRow);
        else PlaceRow(ref _innerRow, inner, innerRect, below, relabel);
    }

    // The built item whose wrapper spans content line y, if any. Reads wrapper bounds, like RowTop.
    private ItemView? ViewAt(double y)
    {
        double origin = _stack.Bounds.Y;
        foreach (var child in _stack.Children)
        {
            if (child is not Border { Tag: ConversationItem item } wrap) continue;
            var b = wrap.Bounds;
            if (b.Height <= 0 || origin + b.Bottom <= y) continue;
            return origin + b.Y < y && _views.TryGetValue(item, out var view) ? view : null;
        }
        return null;
    }

    // A card's rectangle in content coordinates (what Offset measures), or false when it isn't shown.
    private bool TryCardRect(Sticky s, out Rect rect)
    {
        rect = default;
        if (!s.Card.IsEffectivelyVisible || s.Card.TranslatePoint(default, _content) is not { } at) return false;
        rect = new Rect(at, s.Card.Bounds.Size);
        return rect.Height > 0;
    }

    // Shows row for s at content line `at` (or higher, once the card's bottom pushes it up), over the card's column.
    // Returns the row's bottom: where a nested row stacks.
    private double PlaceRow(ref StickyRow? row, Sticky s, Rect card, double at, bool relabel)
    {
        if (row is null)
        {
            row = new StickyRow(_p, StickyCollapse, StickyJumpStart, StickyJumpEnd);
            _stickyLayer.Children.Add(row.Root);
        }
        if (relabel || !ReferenceEquals(row.Bound, s)) row.Bind(s);
        var root = row.Root;
        root.Width = card.Width;
        root.Measure(new Size(card.Width, double.PositiveInfinity));   // a no-op unless the width or text changed
        double h = root.DesiredSize.Height;
        double top = Math.Min(at, card.Bottom - h);
        Canvas.SetLeft(root, card.X);
        Canvas.SetTop(root, top);
        root.IsVisible = true;
        return top + h;
    }

    private static void HideRow(StickyRow? row) => row?.Unbind();

    private void ClearSticky()
    {
        HideRow(_outerRow);
        HideRow(_innerRow);
    }

    // Row click: fold the card, then bring its header to the top — without the re-scroll, folding a long diff from
    // below would leave the view somewhere unrelated.
    private void StickyCollapse(Sticky s)
    {
        if (!TryCardRect(s, out var r)) return;
        s.Collapse();
        LandAt(r.Top - StickyLandGap);
    }

    private void StickyJumpStart(Sticky s)
    {
        if (TryCardRect(s, out var r)) LandAt(r.Top - StickyLandGap);
    }

    // Lands the card's bottom edge on the viewport top, so it's no longer pinned and what follows it is in view.
    private void StickyJumpEnd(Sticky s)
    {
        if (TryCardRect(s, out var r)) LandAt(r.Bottom);
    }

    private void LandAt(double y)
    {
        _stickToBottom = false;
        double max = Math.Max(0, Extent.Height - Viewport.Height);
        Offset = new Vector(Offset.X, Math.Clamp(y, 0, max));
        RecomputeScrollState();
        UpdateSticky();
    }

    // One pinned header: ▾ + the tool icon + "Verb object" + status, then ↑ / ↓. A click on the row folds the card
    // (the same gesture as its own header); the arrows jump.
    private sealed class StickyRow
    {
        private readonly SessionPalette _p;
        private readonly Border _glyphBox;
        private readonly TextBlock _glyph, _verb, _obj, _status;
        private readonly Ellipse _dot;
        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

        public Border Root { get; }
        public Sticky? Bound { get; private set; }

        public StickyRow(SessionPalette p, Action<Sticky> collapse, Action<Sticky> jumpStart, Action<Sticky> jumpEnd)
        {
            _p = p;
            var chevron = new TextBlock
            {
                Text = "▾", FontSize = 11, Foreground = p.Faint, VerticalAlignment = VerticalAlignment.Center,
            };
            _glyph = new TextBlock
            {
                Foreground = p.Muted, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _glyphBox = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Background = p.Raised2,
                Margin = new Thickness(0, 0, 9, 0), Child = _glyph,
            };
            _verb = new TextBlock
            {
                Foreground = p.Title, FontWeight = FontWeight.SemiBold, FontSize = 13.5, FontFamily = p.Body,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
            };
            _obj = new TextBlock { Foreground = p.Muted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            // The verb never trims; the object fills and elides (its directory, when it's a path).
            DockPanel.SetDock(_verb, Dock.Left);
            var title = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _verb, _obj } };

            _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            _status = new TextBlock
            {
                FontSize = 12, FontWeight = FontWeight.SemiBold, FontFamily = p.Mono, VerticalAlignment = VerticalAlignment.Center,
            };
            var status = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0), Children = { _dot, _status },
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto") };
            void Add(Control c, int col) { Grid.SetColumn(c, col); grid.Children.Add(c); }
            chevron.Margin = new Thickness(0, 0, 9, 0);
            Add(chevron, 0);
            Add(_glyphBox, 1);
            Add(title, 2);
            Add(status, 3);
            Add(ArrowButton("↑", "Jump to the start of this card", () => { if (Bound is { } s) jumpStart(s); }), 4);
            Add(ArrowButton("↓", "Jump past the end of this card", () => { if (Bound is { } s) jumpEnd(s); }), 5);

            Root = new Border
            {
                Background = p.Surface, BorderBrush = p.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 10, 10), Padding = new Thickness(13, 6, 6, 6),
                BoxShadow = BoxShadows.Parse("0 4 12 0 #38000000"), Cursor = HandCursor, IsVisible = false,
                Child = grid, [ToolTip.TipProperty] = "Collapse",
            };
            Root.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left && Bound is { } s) collapse(s);
            };
        }

        public void Bind(Sticky s)
        {
            Bound = s;
            var l = s.Label();
            _glyph.Text = s.Glyph;
            _glyphBox.IsVisible = s.Glyph.Length > 0;
            _verb.Text = l.Verb;
            _verb.IsVisible = l.Verb.Length > 0;
            _obj.Text = l.Object;
            _obj.FontFamily = l.ObjectMono ? _p.Mono : _p.Body;
            _obj.FontWeight = l.ObjectMono ? FontWeight.Normal : FontWeight.SemiBold;
            _obj.TextTrimming = l.ObjectIsPath ? TextTrimming.PrefixCharacterEllipsis : TextTrimming.CharacterEllipsis;
            bool hasStatus = l.Status is { Length: > 0 };
            _dot.IsVisible = _status.IsVisible = hasStatus;
            _status.Text = l.Status;
            _dot.Fill = l.StatusBrush;
            _status.Foreground = l.StatusBrush;
        }

        public void Unbind()
        {
            Bound = null;
            Root.IsVisible = false;
        }

        // A small hover-washed arrow. Its release is handled, so it never also reaches the row's collapse.
        private Control ArrowButton(string glyph, string tip, Action onClick)
        {
            var b = new Border
            {
                Width = 26, Height = 22, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
                Cursor = HandCursor, [ToolTip.TipProperty] = tip,
                Child = new TextBlock
                {
                    Text = glyph, FontSize = 13, Foreground = _p.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            b.PointerEntered += (_, _) => b.Background = _p.Raised2;
            b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
            b.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                e.Handled = true;
                onClick();
            };
            return b;
        }
    }
}
