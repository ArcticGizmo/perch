using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// Makes the http(s) links inside a <see cref="SelectableTextBlock"/> followable without disturbing its text
/// selection. A link only "arms" while <kbd>Ctrl</kbd> is held: hovering a link with Ctrl down shows a hand
/// cursor (and the URL as a tip) and a <em>Ctrl+left-click</em> opens it in the default browser; a
/// <em>middle-click</em> opens it in a <em>new</em> browser window (no modifier needed). Without Ctrl the
/// pointer stays an I-beam so the link text selects and copies like any other text. A drag (which selects
/// text), or a click that moved appreciably, never opens a link. A reading surface can opt into a plain
/// left-click (<c>plainClick</c>, the feed story player).
///
/// <para>Only a link whose text is its own address opens straight away. Any other one (<see cref="LinkLabel"/>)
/// first shows a popup with the text and the full address, warning when the text names a different site.</para>
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
        public bool PlainClick;
        public Point PressPoint;
        public Point? HoverPos;
        public TopLevel? Top;
        public System.EventHandler<KeyEventArgs>? KeyHandler;
    }

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, State?>("PerchLinkState");

    /// <summary>Wires <paramref name="tb"/> so the given link <paramref name="spans"/> are followable. A no-op
    /// when there are no spans (and none were set before), so callers can attach unconditionally.</summary>
    /// <param name="plainClick">A plain left-click follows a link too (no Ctrl) — for reading surfaces like the
    /// feed story player, where selecting text is the rarer gesture. A drag or a selection still never opens.</param>
    public static void Attach(SelectableTextBlock tb, IReadOnlyList<UrlSpan> spans, bool plainClick = false)
    {
        if (tb.GetValue(StateProperty) is { } existing)
        {
            existing.Spans = spans;
            existing.PlainClick = plainClick;
            return;
        }
        if (spans.Count == 0) return;

        var state = new State { Spans = spans, PlainClick = plainClick };
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
        // handledEventsToo: SelectableTextBlock marks every press handled (it starts a selection), so a plain += never
        // ran — the press point stayed stale and every Ctrl+click failed the click-slop check below.
        tb.AddHandler(InputElement.PointerPressedEvent, (_, e) => state.PressPoint = e.GetPosition(tb),
            RoutingStrategies.Bubble, handledEventsToo: true);
        tb.PointerReleased += (_, e) =>
        {
            var p = e.GetPosition(tb);
            var btn = e.InitialPressMouseButton;
            if (btn != MouseButton.Left && btn != MouseButton.Middle) return;
            // Ctrl+left-click follows the link; a plain left-click is left to select text (unless PlainClick). Either
            // way a drag that moved appreciably (or selected text) is a selection gesture, not a link click.
            if (btn == MouseButton.Left)
            {
                if (!state.PlainClick && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
                if (!string.IsNullOrEmpty(tb.SelectedText) || Dist(p, state.PressPoint) > ClickSlop) return;
            }
            if (SpanAt(tb, p) is not { } span) return;
            Follow(tb, span, newWindow: btn == MouseButton.Middle);
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
        tb.Cursor = ctrl || state.PlainClick ? HandCursor : null;
    }

    // Opens a link whose text is its address; any other link (every click: left, Ctrl or middle) first shows a
    // popup with the text and the real address, so a link can't pass itself off as somewhere else.
    private static void Follow(SelectableTextBlock tb, UrlSpan span, bool newWindow)
    {
        var text = tb.Inlines?.Text ?? tb.Text ?? "";
        string label = span.Start >= 0 && span.Start + span.Length <= text.Length ? text.Substring(span.Start, span.Length) : "";
        var check = LinkLabel.Check(label, span.Url);
        if (check.Match == LinkLabelMatch.Same) Open(span.Url, newWindow);
        else ShowConfirm(tb, label, span.Url, check, newWindow);
    }

    private static void Open(string url, bool newWindow)
    {
        if (newWindow) PlatformServices.UrlOpener.OpenInNewWindow(url);
        else PlatformServices.UrlOpener.Open(url);
    }

    // The "where does this really go?" popup at the pointer: the link's text, its full address (the site in bold,
    // never trimmed) which opens on click, a warning when the text names another site, and Open / Copy link.
    private static void ShowConfirm(SelectableTextBlock tb, string label, string url, LinkLabelCheck check, bool newWindow)
    {
        _openPopup?.Hide();
        var flyout = new Flyout { Placement = PlacementMode.Pointer };
        _openPopup = flyout;
        flyout.Closed += (_, _) => { if (ReferenceEquals(_openPopup, flyout)) _openPopup = null; };

        var panel = new StackPanel { Spacing = 4, MaxWidth = 420 };
        panel.Children.Add(Caption("Link text"));
        panel.Children.Add(new SelectableTextBlock
        {
            Text = label.Length > 0 ? label : "(no text)", Foreground = Palette.FgBrush, FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Caption("Goes to"));

        var address = new TextBlock
        {
            Foreground = Palette.AccentBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            TextDecorations = TextDecorations.Underline, Cursor = HandCursor,
        };
        // The site in bold, so it's what the eye lands on; the rest of the address as-is.
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0
            && url.IndexOf(u.Host, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
        {
            address.Inlines =
            [
                new Run(url[..at]),
                new Run(url.Substring(at, u.Host.Length)) { FontWeight = FontWeight.Bold },
                new Run(url[(at + u.Host.Length)..]),
            ];
        }
        else address.Text = url;
        ToolTip.SetTip(address, newWindow ? "Open in a new browser window" : "Open in your browser");
        address.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(address).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            flyout.Hide();
            Open(url, newWindow);
        };
        panel.Children.Add(address);

        if (check is { Match: LinkLabelMatch.OtherSite, ShownHost: { } shown, Host: { } host })
            panel.Children.Add(new TextBlock
            {
                Text = $"⚠ The text shows {shown}, but this link goes to {host}.",
                Foreground = new SolidColorBrush(Palette.Yellow), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

        var open = new Button
        {
            Content = "Open link  ↗", Foreground = Palette.OnAccentBrush, Background = Palette.AccentBrush,
            Padding = new Thickness(12, 4), FontSize = 12.5, Cursor = HandCursor,
        };
        open.Click += (_, _) => { flyout.Hide(); Open(url, newWindow); };
        var copy = new Button
        {
            Content = "Copy link", Foreground = Palette.FgBrush, Background = Palette.ButtonBgBrush,
            Padding = new Thickness(12, 4), FontSize = 12.5, Cursor = HandCursor,
        };
        copy.Click += (_, _) =>
        {
            TopLevel.GetTopLevel(tb)?.Clipboard?.SetTextAsync(url);
            flyout.Hide();
        };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children = { open, copy },
        });

        flyout.Content = panel;
        flyout.ShowAt(tb, showAtPointer: true);
    }

    private static Flyout? _openPopup;

    /// <summary>Headless-render seam: the popup for a link, as a click would show it.</summary>
    internal static void ShowConfirmForRender(SelectableTextBlock tb, string label, string url) =>
        ShowConfirm(tb, label, url, LinkLabel.Check(label, url), newWindow: false);

    /// <summary>Closes the link popup if one is open (true when it was). For windows whose own keys would otherwise
    /// act behind it — Escape in the story player closes the window, not the popup.</summary>
    public static bool DismissPopup()
    {
        if (_openPopup is not { IsOpen: true } open) return false;
        open.Hide();
        return true;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text, Foreground = Palette.MutedBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
    };

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
