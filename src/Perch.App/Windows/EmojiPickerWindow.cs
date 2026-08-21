using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;

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
/// A reusable, activating emoji chooser shown near the cursor: a grid (not a long vertical list) of preset
/// emoji chips plus an entry box that accepts <em>any</em> system emoji you type or paste — so a reaction (or a
/// status mood) is never limited to the curated set. Typing text filters the presets by keyword; typing/pasting
/// an emoji offers it as a highlighted "use this" chip, and Enter picks it. Closes on pick, Esc, or deactivation.
///
/// <para>It's a real (activating) window rather than a flyout on purpose: the overlay is a no-activate tool
/// window, so a flyout hung off it can't reliably take keyboard focus for the entry box / the OS emoji picker.</para>
/// </summary>
internal sealed class EmojiPickerWindow : Window
{
    private readonly Action<string?> _onPick;
    private readonly (string Emoji, string Keywords)[] _presets;
    private readonly bool _showClear;
    private readonly TextBox _entry;
    private readonly WrapPanel _grid;
    private bool _picked;

    /// <param name="onPick">Invoked with the chosen emoji, or <c>null</c> when the "clear" chip is used
    /// (only offered when <paramref name="showClear"/> is set).</param>
    public EmojiPickerWindow(
        string title,
        (string Emoji, string Keywords)[] presets,
        Action<string?> onPick,
        PixelPoint anchor,
        bool showClear = false)
    {
        _onPick = onPick;
        _presets = presets;
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
        _entry.PlaceholderText = "search or type an emoji…";
        _entry.TextChanged += (_, _) => Rebuild();

        _grid = new WrapPanel { MaxWidth = 276 };

        var tip = new TextBlock
        {
            Text = "Tip: press Win + . for the system emoji picker.",
            Foreground = Palette.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
        });
        panel.Children.Add(_entry);
        panel.Children.Add(new ScrollViewer { MaxHeight = 184, Content = _grid });
        panel.Children.Add(tip);

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
            // Otherwise, if the keyword search narrowed to a single preset, pick it.
            var matches = FilteredPresets(q);
            if (matches.Count == 1) { Pick(matches[0].Emoji); e.Handled = true; return; }
        }
        base.OnKeyDown(e);
    }

    // Rebuilds the chip grid from the current entry text: a highlighted custom chip when an emoji was typed,
    // an optional "clear" chip, then the presets that match the keyword search (all of them when it's empty).
    private void Rebuild()
    {
        _grid.Children.Clear();
        var q = _entry.Text?.Trim() ?? "";

        if (EmojiText.ContainsEmoji(q))
        {
            var custom = EmojiText.FirstGrapheme(q);
            _grid.Children.Add(Chip(custom, () => Pick(custom), highlight: true));
        }
        else if (_showClear && q.Length == 0)
        {
            _grid.Children.Add(Chip("🚫", () => Pick(null), dim: true));
        }

        foreach (var (emoji, _) in FilteredPresets(q))
            _grid.Children.Add(Chip(emoji, () => Pick(emoji)));
    }

    // The presets to show for a query: all of them when it's empty or an emoji was typed (so a preset stays
    // one click away), otherwise those whose keywords contain the query.
    private List<(string Emoji, string Keywords)> FilteredPresets(string q)
    {
        if (q.Length == 0 || EmojiText.ContainsEmoji(q)) return _presets.ToList();
        return _presets.Where(p =>
            p.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Emoji == q).ToList();
    }

    private void Pick(string? emoji)
    {
        if (_picked) return;
        _picked = true;
        _onPick(emoji);
        Close();
    }

    private static Button Chip(string emoji, Action onClick, bool highlight = false, bool dim = false)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = emoji, FontFamily = EmojiText.Font, FontSize = 20 },
            Background = highlight ? Palette.OverlayRowHoverBrush : Brushes.Transparent,
            BorderBrush = highlight ? Palette.AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(highlight ? 1.5 : 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 3), Margin = new Thickness(0, 0, 3, 3),
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
}
