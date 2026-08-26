using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Small shared helpers for treating user-typed text as an emoji, so the reaction picker and the status-mood
/// picker can both accept <em>any</em> system emoji the user types or pastes (Win + . inserts one) rather than
/// only the curated presets.
/// </summary>
internal static class EmojiText
{
    /// <summary>The platform emoji typeface, matching <c>OverlayDraw.Emoji</c> so a glyph renders in colour
    /// (where the toolkit supports it) rather than falling through to tofu.</summary>
    public static readonly FontFamily Font = new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji");

    /// <summary>True when <paramref name="s"/> contains at least one emoji-ish rune — a supplementary-plane
    /// codepoint (nearly all pictographic emoji, skin-tone modifiers) or a BMP "Other Symbol" (dingbats,
    /// arrows, regional indicators). Good enough to decide "the user typed an emoji, not a search keyword".</summary>
    public static bool ContainsEmoji(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value > 0xFFFF) return true;                                  // supplementary plane
            if (Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol) return true;
        }
        return false;
    }

    /// <summary>The first grapheme cluster of <paramref name="s"/> — one whole emoji including any variation
    /// selectors, ZWJ sequence (family/flag) or skin-tone modifier — so a custom reaction is a single glyph.</summary>
    public static string FirstGrapheme(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var e = StringInfo.GetTextElementEnumerator(s);
        return e.MoveNext() ? (string)e.Current! : s;
    }
}

/// <summary>
/// Perch's own emoji chooser, shown near the cursor — a replacement for leaning on the OS emoji dialog. With the
/// box empty it shows your <em>recently used</em> emoji (falling back to a popular set on first run); typing filters
/// the whole cross-platform emoji set (<see cref="EmojiCatalog"/>) by name, shortcode or keyword; and typing or
/// pasting an actual emoji offers it directly as a highlighted "use this" chip. Enter picks the best match; picking
/// records it as recently used. Closes on pick, Esc, or deactivation.
///
/// <para>It's a real (activating) window rather than a flyout on purpose: the overlay is a no-activate tool
/// window, so a flyout hung off it can't reliably take keyboard focus for the entry box.</para>
/// </summary>
internal sealed class EmojiPickerWindow : Window
{
    // Shown in the empty state when the user has no history yet, so the grid is never blank on first run.
    private static readonly string[] PopularFallback =
    [
        "👍", "🔥", "🎉", "😂", "😮", "❤️", "🙌", "👀",
        "😢", "🚀", "💯", "🤯", "🫡", "😅", "💀", "✅",
    ];

    private const int MaxSearchResults = 60;

    private readonly Action<string?> _onPick;
    private readonly Action<string>? _onEmojiUsed;
    private readonly IReadOnlyList<string> _recents;
    private readonly bool _showClear;
    private readonly TextBox _entry;
    private readonly TextBlock _sectionLabel;
    private readonly WrapPanel _grid;
    private readonly TextBlock _empty;
    private bool _picked;

    /// <param name="onPick">Invoked with the chosen emoji, or <c>null</c> when the "clear" chip is used
    /// (only offered when <paramref name="showClear"/> is set).</param>
    /// <param name="recents">Most-recently-used emoji, most-recent first, shown by default. May be empty/null.</param>
    /// <param name="onEmojiUsed">Invoked with the chosen emoji (never null) so the caller can record it as
    /// recently used. Not called for the "clear" chip.</param>
    public EmojiPickerWindow(
        string title,
        Action<string?> onPick,
        PixelPoint anchor,
        IReadOnlyList<string>? recents = null,
        Action<string>? onEmojiUsed = null,
        bool showClear = false)
    {
        _onPick = onPick;
        _onEmojiUsed = onEmojiUsed;
        _recents = recents ?? [];
        _showClear = showClear;

        Title = title;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = anchor;
        Background = Palette.OverlaySurfaceBrush;

        _entry = SettingsUi.ThemedTextBox("");
        _entry.PlaceholderText = "search emoji…";
        _entry.TextChanged += (_, _) => Rebuild();

        _sectionLabel = new TextBlock
        {
            FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.MutedBrush,
            Margin = new Thickness(2, 0, 0, 0),
        };

        // A little top inset so the first row's emoji glyphs (which sit high in their line box) aren't clipped
        // by the ScrollViewer's top edge.
        _grid = new WrapPanel { MaxWidth = 276, Margin = new Thickness(0, 4, 0, 0) };

        _empty = new TextBlock
        {
            Text = "No emoji found.", Foreground = Palette.MutedBrush, FontSize = 12,
            Margin = new Thickness(2, 6, 0, 2), IsVisible = false,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
        });
        panel.Children.Add(_entry);
        panel.Children.Add(_sectionLabel);
        panel.Children.Add(new ScrollViewer { MaxHeight = 220, Content = _grid });
        panel.Children.Add(_empty);

        Content = new Border
        {
            Background = Palette.OverlaySurfaceBrush,
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
            Child = panel,
        };

        Rebuild();
        Deactivated += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _entry.Focus();
        // SizeToContent settles Bounds on the next layout pass; clamp on-screen once the real height is known.
        Dispatcher.UIThread.Post(ClampToScreen, DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Enter)
        {
            var q = _entry.Text?.Trim() ?? "";
            if (EmojiText.ContainsEmoji(q)) { Pick(EmojiText.FirstGrapheme(q)); e.Handled = true; return; }
            // Otherwise pick the top search result, so "type a couple letters, hit Enter" works.
            if (q.Length > 0)
            {
                var matches = EmojiCatalog.Search(q, 1);
                if (matches.Count > 0) { Pick(matches[0].Emoji); e.Handled = true; return; }
            }
        }
        base.OnKeyDown(e);
    }

    // Rebuilds the chip grid from the current entry text. Empty box → recently-used (or popular) grid; a typed
    // emoji → a highlighted custom chip above the recents; a keyword → the full-catalogue search results.
    private void Rebuild()
    {
        _grid.Children.Clear();
        _empty.IsVisible = false;
        var q = _entry.Text?.Trim() ?? "";

        if (EmojiText.ContainsEmoji(q))
        {
            var custom = EmojiText.FirstGrapheme(q);
            _sectionLabel.Text = "Use this";
            _grid.Children.Add(Chip(custom, () => Pick(custom), highlight: true));
            return;
        }

        if (q.Length == 0)
        {
            if (_showClear) _grid.Children.Add(Chip("🚫", () => Pick(null), dim: true));

            var recents = DistinctRecents();
            if (recents.Count > 0)
            {
                _sectionLabel.Text = "Recently used";
                foreach (var emoji in recents) _grid.Children.Add(Chip(emoji, () => Pick(emoji)));
            }
            else
            {
                _sectionLabel.Text = "Popular";
                foreach (var emoji in PopularFallback) _grid.Children.Add(Chip(emoji, () => Pick(emoji)));
            }
            return;
        }

        var results = EmojiCatalog.Search(q, MaxSearchResults);
        _sectionLabel.Text = results.Count > 0 ? "Results" : "";
        _empty.IsVisible = results.Count == 0;
        foreach (var r in results) _grid.Children.Add(Chip(r.Emoji, () => Pick(r.Emoji)));
    }

    // Recents as unique glyphs, most-recent first (the caller's list should already be deduped, but be defensive).
    private List<string> DistinctRecents()
    {
        var seen = new HashSet<string>();
        var list = new List<string>();
        foreach (var e in _recents)
            if (!string.IsNullOrWhiteSpace(e) && seen.Add(e)) list.Add(e);
        return list;
    }

    private void Pick(string? emoji)
    {
        if (_picked) return;
        _picked = true;
        if (!string.IsNullOrWhiteSpace(emoji)) _onEmojiUsed?.Invoke(emoji);
        _onPick(emoji);
        Close();
    }

    private static Button Chip(string emoji, Action onClick, bool highlight = false, bool dim = false)
    {
        var b = new Button
        {
            // Owner-drawn glyph rather than a TextBlock: Avalonia clips a colour-emoji glyph to the text line's
            // own bounds (the tall ascent overflows and gets cut at the top), so we draw it ourselves through
            // the same baseline-centred path the overlay uses — no clipping, and consistent with the rows.
            Content = new EmojiGlyph(emoji, 20),
            Background = highlight ? Palette.OverlayRowHoverBrush : Brushes.Transparent,
            BorderBrush = highlight ? Palette.AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(highlight ? 1.5 : 0),
            CornerRadius = new CornerRadius(6),
            // A fixed square chip: the emoji is centred in it with room to spare, so the tall glyph never clips.
            Width = 40, Height = 40, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = dim ? 0.6 : 1.0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // Nudges the window fully onto the screen under its anchor, so a chip near the screen edge doesn't spill off.
    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary
            ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        var wa = screen.WorkingArea;                                  // physical pixels
        double scale = screen.Scaling;
        int w = (int)((Bounds.Width > 0 ? Bounds.Width : Width) * scale);
        int h = (int)((Bounds.Height > 0 ? Bounds.Height : 220) * scale);

        int x = Math.Clamp(Position.X, wa.X, Math.Max(wa.X, wa.X + wa.Width - w));
        int y = Math.Clamp(Position.Y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - h));
        Position = new PixelPoint(x, y);
    }

    // A single emoji drawn owner-style (via OverlayDraw) rather than through a TextBlock, so the tall colour-
    // glyph ascent isn't clipped at the top the way a TextBlock clips it to its text line. Sized to a small
    // square with the glyph centred through the same baseline-aware path the overlay rows use.
    private sealed class EmojiGlyph : Control
    {
        private readonly string _emoji;
        private readonly double _size;

        public EmojiGlyph(string emoji, double size)
        {
            _emoji = emoji;
            _size = size;
            Width = size + 6;
            Height = size + 6;
        }

        public override void Render(DrawingContext ctx)
        {
            var ft = OverlayDraw.Emoji(_emoji, _size, Brushes.White);
            OverlayDraw.EmojiCentered(ctx, ft, Bounds.Width / 2, Bounds.Height / 2, _size);
        }
    }
}
