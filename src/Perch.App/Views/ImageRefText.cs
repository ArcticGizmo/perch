using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Windows;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// Makes the <c>[Image #N]</c> placeholder tokens inside a resumed user message interactive: the token is
/// tinted (the UI's "special/link" violet) so it reads as more than text, hovering it for ~750ms floats a
/// full-resolution preview of the image, and a plain left-click opens it in the in-app
/// <see cref="ImageViewerWindow"/>. The k-th token pairs with the k-th image attachment on the message. Text
/// stays selectable — a click that moved (a drag) or that left a selection never opens.
///
/// The hover preview is deliberately delayed and hit-test-transparent so it never sits under the cursor
/// stealing a click. Hit-testing reuses the block's own <c>TextLayout.HitTestPoint</c> (as
/// <see cref="LinkText"/> does); the popup is parented to a caller-supplied panel (a Popup needs a rooted
/// parent), and each image is decoded at native resolution the first time it is shown.
/// </summary>
internal static partial class ImageRefText
{
    private const double ClickSlop = 4;
    private const int PreviewDelayMs = 750;

    [GeneratedRegex(@"\[Image #\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    private readonly record struct Span(int Start, int Length, MessageAttachment Image);

    /// <summary>Wire <paramref name="tb"/> so its <c>[Image #N]</c> tokens tint, preview on a delayed hover,
    /// and open on click. <paramref name="host"/> is a panel containing <paramref name="tb"/> that the preview
    /// popup is parented to. A no-op when the text has no tokens or the message has no image attachments.</summary>
    public static void Attach(SelectableTextBlock tb, Panel host, string text, IReadOnlyList<MessageAttachment> images, SessionPalette p)
    {
        var imageList = images.Where(a => a.Kind == AttachmentKind.Image).ToList();
        if (imageList.Count == 0) return;

        var spans = new List<Span>();
        int k = 0;
        foreach (Match m in TokenRegex().Matches(text))
        {
            if (k >= imageList.Count) break;
            spans.Add(new Span(m.Index, m.Length, imageList[k]));
            k++;
        }
        if (spans.Count == 0) return;

        Tint(tb, text, spans, p);

        var previewImage = new Image { MaxWidth = 560, MaxHeight = 560, Stretch = Stretch.Uniform };
        var popup = new Popup
        {
            // Anchored ABOVE the text block, not at the pointer: a popup is a separate OS surface, so one placed
            // under the cursor makes the block lose the pointer (PointerExited) and the preview closes itself.
            PlacementTarget = tb, Placement = PlacementMode.Top, VerticalOffset = -6,
            IsLightDismissEnabled = false, IsHitTestVisible = false,
            Child = new Border
            {
                Background = p.Surface, BorderBrush = p.Border, BorderThickness = new Thickness(1),
                CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(6),
                BoxShadow = BoxShadows.Parse("0 10 30 0 #66000000"),
                IsHitTestVisible = false,   // never sit under the cursor and eat a click on the token
                Child = previewImage,
            },
        };
        host.Children.Add(popup);

        var cache = new Dictionary<MessageAttachment, Bitmap?>();
        MessageAttachment? pending = null;                 // the span the delay timer is counting down for
        var press = new Point();
        var hand = new Cursor(StandardCursorType.Hand);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (pending is not { } a) return;
            var bmp = Load(cache, a);
            previewImage.Source = bmp;
            popup.IsOpen = bmp is not null;
        };

        Span? SpanAt(Point pt)
        {
            if (tb.TextLayout is not { } layout) return null;
            var hit = layout.HitTestPoint(new Point(pt.X - tb.Padding.Left, pt.Y - tb.Padding.Top));
            int idx = hit.TextPosition;
            foreach (var s in spans)
                if (idx >= s.Start && idx < s.Start + s.Length) return s;
            return null;
        }

        void Hide()
        {
            timer.Stop();
            popup.IsOpen = false;
            pending = null;
        }

        tb.PointerMoved += (_, e) =>
        {
            if (SpanAt(e.GetPosition(tb)) is { } s)
            {
                tb.Cursor = hand;
                var img = s.Image;
                if (popup.IsOpen)
                {
                    if (!ReferenceEquals(pending, img)) { Hide(); pending = img; timer.Start(); }  // moved to another token
                }
                else if (!ReferenceEquals(pending, img))
                {
                    // A new token under the cursor: (re)start the hover delay before the preview shows.
                    pending = img;
                    timer.Stop();
                    timer.Start();
                }
            }
            else
            {
                tb.Cursor = null;
                Hide();
            }
        };
        tb.PointerExited += (_, _) => { tb.Cursor = null; Hide(); };
        tb.PointerPressed += (_, e) => press = e.GetPosition(tb);
        tb.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            var pt = e.GetPosition(tb);
            if (!string.IsNullOrEmpty(tb.SelectedText) || Dist(pt, press) > ClickSlop) return;   // a selection drag
            if (SpanAt(pt) is not { } s) return;
            Hide();
            ImageViewerWindow.ShowFor(s.Image.Path);
            e.Handled = true;
        };
    }

    // Rebuild the block's runs so the token spans are tinted violet + semibold, the rest the normal title colour.
    private static void Tint(SelectableTextBlock tb, string text, List<Span> spans, SessionPalette p)
    {
        var inlines = new InlineCollection();
        int pos = 0;
        foreach (var s in spans)
        {
            if (s.Start > pos)
                inlines.Add(new Run(text[pos..s.Start]) { Foreground = p.Title });
            inlines.Add(new Run(text.Substring(s.Start, s.Length)) { Foreground = p.Violet, FontWeight = FontWeight.SemiBold });
            pos = s.Start + s.Length;
        }
        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]) { Foreground = p.Title });
        tb.Inlines = inlines;
    }

    private static Bitmap? Load(Dictionary<MessageAttachment, Bitmap?> cache, MessageAttachment a)
    {
        if (cache.TryGetValue(a, out var b)) return b;
        try { b = new Bitmap(a.Path); } catch { b = null; }   // native resolution for a crisp preview
        cache[a] = b;
        return b;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }
}
