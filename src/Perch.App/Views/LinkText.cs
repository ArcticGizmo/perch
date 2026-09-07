using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// Makes the http(s) links inside a <see cref="SelectableTextBlock"/> followable without disturbing its text
/// selection. A link only "arms" while <kbd>Ctrl</kbd> is held: hovering a link with Ctrl down shows a hand
/// cursor (and the URL as a tip) and a <em>Ctrl+left-click</em> opens it in the default browser; a
/// <em>middle-click</em> opens it in a <em>new</em> browser window (no modifier needed). Without Ctrl the
/// pointer stays an I-beam so the link text selects and copies like any other text. A drag (which selects
/// text), or a click that moved appreciably, never opens a link.
///
/// <para>Link char-ranges come from the caller: <see cref="MarkdownView"/> records them as it lays out
/// inlines (so markdown link text, autolinks and image links all count), and plain surfaces (tool output,
/// the user's bubble) detect them with <see cref="UrlDetect"/>. Hit-testing reuses the block's own
/// <c>TextLayout.HitTestPoint</c> — the same machinery the markdown editor uses for cursor sync. Handlers are
/// wired once; a re-<see cref="Attach"/> (e.g. tool output changing) just swaps the current spans.</para>
///
/// <para>The hand cursor tracks the live Ctrl state even without pointer movement: we listen for Ctrl
/// key-down/up on the hosting <see cref="TopLevel"/> while the pointer sits over the block, so pressing or
/// releasing Ctrl flips the cursor immediately.</para>
/// </summary>
internal static class LinkText
{
    // How far the pointer may travel between press and release and still count as a click, not a selection drag.
    private const double ClickSlop = 4;

    // Per-block mutable state: the current link spans, live hover position (null when the pointer is off the
    // block), and the TopLevel key hook so the cursor can react to Ctrl without pointer movement.
    private sealed class State
    {
        public IReadOnlyList<UrlSpan> Spans = System.Array.Empty<UrlSpan>();
        public Point PressPoint;
        public Point? HoverPos;
        public TopLevel? Top;
        public System.EventHandler<KeyEventArgs>? KeyHandler;
    }

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, State?>("PerchLinkState");

    /// <summary>Wires <paramref name="tb"/> so the given link <paramref name="spans"/> are followable. A no-op
    /// when there are no spans (and none were set before), so callers can attach unconditionally.</summary>
    public static void Attach(SelectableTextBlock tb, IReadOnlyList<UrlSpan> spans)
    {
        if (tb.GetValue(StateProperty) is { } existing)
        {
            existing.Spans = spans;
            return;
        }
        if (spans.Count == 0) return;

        var state = new State { Spans = spans };
        tb.SetValue(StateProperty, state);

        tb.PointerMoved += (_, e) =>
        {
            state.HoverPos = e.GetPosition(tb);
            UpdateHover(tb, state, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        };
        tb.PointerExited += (_, _) =>
        {
            state.HoverPos = null;
            ClearTip(tb);
            tb.Cursor = null;
        };
        tb.PointerPressed += (_, e) => state.PressPoint = e.GetPosition(tb);
        tb.PointerReleased += (_, e) =>
        {
            var p = e.GetPosition(tb);
            var btn = e.InitialPressMouseButton;
            if (btn != MouseButton.Left && btn != MouseButton.Middle) return;
            // Ctrl+left-click follows the link; a plain left-click is left to select text. Either way a drag
            // that moved appreciably (or selected text) is a selection gesture, not a link click.
            if (btn == MouseButton.Left)
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
                if (!string.IsNullOrEmpty(tb.SelectedText) || Dist(p, state.PressPoint) > ClickSlop) return;
            }
            if (SpanAt(tb, p) is not { } span) return;
            if (btn == MouseButton.Middle) PlatformServices.UrlOpener.OpenInNewWindow(span.Url);
            else PlatformServices.UrlOpener.Open(span.Url);
            e.Handled = true;
        };

        tb.AttachedToVisualTree += (_, _) => HookKeys(tb, state);
        tb.DetachedFromVisualTree += (_, _) => UnhookKeys(state);
        HookKeys(tb, state); // no-op if not yet rooted; the AttachedToVisualTree handler covers that case
    }

    /// <summary>Convenience for plain (non-markdown) text: detects the URLs in <paramref name="text"/> and
    /// makes them followable. Safe to call again when the text changes.</summary>
    public static void AttachDetected(SelectableTextBlock tb, string? text) => Attach(tb, UrlDetect.Find(text));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // Recompute the tip + cursor for the current hover position. The hand cursor only shows when Ctrl is held
    // over a link, so link text stays selectable/copyable when it isn't.
    private static void UpdateHover(SelectableTextBlock tb, State state, bool ctrl)
    {
        if (state.HoverPos is not { } pos || SpanAt(tb, pos) is not { } span)
        {
            ClearTip(tb);
            tb.Cursor = null;
            return;
        }
        ShowTip(tb, span.Url);
        tb.Cursor = ctrl ? HandCursor : null;
    }

    private static UrlSpan? SpanAt(SelectableTextBlock tb, Point p)
    {
        if (tb.GetValue(StateProperty) is not { Spans.Count: > 0 } state || tb.TextLayout is not { } layout)
            return null;
        var hit = layout.HitTestPoint(new Point(p.X - tb.Padding.Left, p.Y - tb.Padding.Top));
        int idx = hit.TextPosition;
        foreach (var s in state.Spans)
            if (idx >= s.Start && idx < s.Start + s.Length)
                return s;
        return null;
    }

    // Listen for Ctrl on the hosting window so the cursor flips the instant Ctrl is pressed/released while the
    // pointer sits over a link, not only on the next pointer move.
    private static void HookKeys(SelectableTextBlock tb, State state)
    {
        var top = TopLevel.GetTopLevel(tb);
        if (top is null || ReferenceEquals(state.Top, top)) return;
        UnhookKeys(state);
        state.Top = top;
        state.KeyHandler = (_, e) =>
        {
            if (e.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
            UpdateHover(tb, state, e.RoutedEvent == InputElement.KeyDownEvent);
        };
        top.AddHandler(InputElement.KeyDownEvent, state.KeyHandler,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        top.AddHandler(InputElement.KeyUpEvent, state.KeyHandler,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void UnhookKeys(State state)
    {
        if (state.Top is { } top && state.KeyHandler is { } handler)
        {
            top.RemoveHandler(InputElement.KeyDownEvent, handler);
            top.RemoveHandler(InputElement.KeyUpEvent, handler);
        }
        state.Top = null;
        state.KeyHandler = null;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    // A lightweight, in-place tip that just shows the destination URL.
    private static void ShowTip(Control c, string url)
    {
        if (!Equals(ToolTip.GetTip(c), url)) ToolTip.SetTip(c, url);
    }

    private static void ClearTip(Control c)
    {
        if (ToolTip.GetTip(c) is not null) ToolTip.SetTip(c, null);
    }
}
