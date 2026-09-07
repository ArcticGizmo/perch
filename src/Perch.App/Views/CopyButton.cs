using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// A small "copy this message" affordance that floats at a bubble's corner and is revealed on hover (the
/// host toggles <see cref="Reveal"/>). It reads its text lazily through a callback at click time — so an
/// assistant bubble that is still streaming copies whatever it holds when clicked — writes it to the
/// clipboard, and briefly flips its glyph to a tick as confirmation. Colours come from
/// <see cref="SessionPalette"/>.
/// </summary>
internal sealed class CopyButton : Border
{
    private readonly SessionPalette _p;
    private readonly Func<string?> _text;
    private readonly TextBlock _glyph;
    private DispatcherTimer? _revert;

    public CopyButton(SessionPalette p, Func<string?> text)
    {
        _p = p;
        _text = text;
        Width = 24;
        Height = 24;
        CornerRadius = new CornerRadius(7);
        Background = p.Raised2;
        BorderBrush = p.Border;
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);
        IsVisible = false;   // shown only while the host is hovered
        VerticalAlignment = VerticalAlignment.Top;
        _glyph = new TextBlock
        {
            Text = "⎘", FontSize = 12.5, Foreground = p.Muted,   // ⎘ copy glyph
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _glyph;
        ToolTip.SetTip(this, "Copy message");

        PointerEntered += (_, _) => { if (_glyph.Text != "✓") Background = p.Border; };
        PointerExited += (_, _) => { if (_glyph.Text != "✓") Background = p.Raised2; };
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;   // don't also toggle the tool card / open a link under the button
            _ = CopyAsync();
        };
    }

    /// <summary>Show or hide the button (the host wires this to its own hover state).</summary>
    public void Reveal(bool shown) => IsVisible = shown;

    private async System.Threading.Tasks.Task CopyAsync()
    {
        var text = _text();
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is null) return;
        try { await clip.SetTextAsync(text); }
        catch { return; }

        _glyph.Text = "✓";           // ✓
        _glyph.Foreground = _p.Brand;
        Background = _p.BrandWash;
        _revert?.Stop();
        _revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _revert.Tick += (_, _) =>
        {
            _revert?.Stop();
            _glyph.Text = "⎘";
            _glyph.Foreground = _p.Muted;
            Background = _p.Raised2;
        };
        _revert.Start();
    }
}
