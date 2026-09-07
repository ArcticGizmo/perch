using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// Makes the http(s) links inside a <see cref="SelectableTextBlock"/> clickable without disturbing its text
/// selection: hover shows a hand cursor (and the URL as a tip), a plain left-click opens the link in the
/// default browser, and a middle-click opens it in a <em>new</em> browser window. A drag (which selects text)
/// never opens a link — we only act on a click that left the selection empty and the pointer roughly still.
///
/// <para>Link char-ranges come from the caller: <see cref="MarkdownView"/> records them as it lays out
/// inlines (so markdown link text, autolinks and image links all count), and plain surfaces (tool output,
/// the user's bubble) detect them with <see cref="UrlDetect"/>. Hit-testing reuses the block's own
/// <c>TextLayout.HitTestPoint</c> — the same machinery the markdown editor uses for cursor sync. Handlers are
/// wired once; a re-<see cref="Attach"/> (e.g. tool output changing) just swaps the current spans.</para>
/// </summary>
internal static class LinkText
{
    // How far the pointer may travel between press and release and still count as a click, not a selection drag.
    private const double ClickSlop = 4;

    // The block's current link spans, and whether we've already wired its pointer handlers (so re-attaching
    // dynamic text only updates the spans).
    private static readonly AttachedProperty<IReadOnlyList<UrlSpan>?> SpansProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, IReadOnlyList<UrlSpan>?>("PerchLinkSpans");
    private static readonly AttachedProperty<bool> WiredProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("PerchLinkWired");

    /// <summary>Wires <paramref name="tb"/> so the given link <paramref name="spans"/> are clickable. A no-op
    /// when there are no spans (and none were set before), so callers can attach unconditionally.</summary>
    public static void Attach(SelectableTextBlock tb, IReadOnlyList<UrlSpan> spans)
    {
        tb.SetValue(SpansProperty, spans);
        if (spans.Count == 0 || tb.GetValue(WiredProperty)) return;
        tb.SetValue(WiredProperty, true);

        Point pressPoint = default;

        UrlSpan? SpanAt(Point p)
        {
            if (tb.GetValue(SpansProperty) is not { Count: > 0 } cur || tb.TextLayout is not { } layout) return null;
            var hit = layout.HitTestPoint(new Point(p.X - tb.Padding.Left, p.Y - tb.Padding.Top));
            int idx = hit.TextPosition;
            foreach (var s in cur)
                if (idx >= s.Start && idx < s.Start + s.Length)
                    return s;
            return null;
        }

        tb.PointerMoved += (_, e) =>
        {
            bool overLink = SpanAt(e.GetPosition(tb)) is { } s && ShowTip(tb, s.Url);
            if (!overLink) ClearTip(tb);
            tb.Cursor = overLink ? HandCursor : null;
        };
        tb.PointerExited += (_, _) => { ClearTip(tb); tb.Cursor = null; };

        tb.PointerPressed += (_, e) => pressPoint = e.GetPosition(tb);

        tb.PointerReleased += (_, e) =>
        {
            var p = e.GetPosition(tb);
            var btn = e.InitialPressMouseButton;
            if (btn != MouseButton.Left && btn != MouseButton.Middle) return;
            // A drag that selected text, or that moved appreciably, is a selection gesture — not a link click.
            if (btn == MouseButton.Left &&
                (!string.IsNullOrEmpty(tb.SelectedText) || Dist(p, pressPoint) > ClickSlop))
                return;
            if (SpanAt(p) is not { } span) return;
            if (btn == MouseButton.Middle) PlatformServices.UrlOpener.OpenInNewWindow(span.Url);
            else PlatformServices.UrlOpener.Open(span.Url);
            e.Handled = true;
        };
    }

    /// <summary>Convenience for plain (non-markdown) text: detects the URLs in <paramref name="text"/> and
    /// makes them clickable. Safe to call again when the text changes.</summary>
    public static void AttachDetected(SelectableTextBlock tb, string? text) => Attach(tb, UrlDetect.Find(text));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    // A lightweight, in-place tip that just shows the destination URL; returns true so the caller can chain it.
    private static bool ShowTip(Control c, string url)
    {
        if (!Equals(ToolTip.GetTip(c), url)) ToolTip.SetTip(c, url);
        return true;
    }

    private static void ClearTip(Control c)
    {
        if (ToolTip.GetTip(c) is not null) ToolTip.SetTip(c, null);
    }
}
