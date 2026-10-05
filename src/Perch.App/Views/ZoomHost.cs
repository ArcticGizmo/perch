using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// Whole-window zoom for the session window and the Roost. The window's content sits in a layout transform, so it
/// re-lays out at the scaled size (a zoomed Roost fits fewer columns rather than spilling off the edge), and this
/// owns the chords: Ctrl+= / Ctrl+− (main row or keypad, with or without Shift) and Ctrl+wheel step through
/// <see cref="ViewZoom.Steps"/>; Ctrl+0 resets where the window doesn't already bind it. A brief "110%" badge
/// confirms each press. Popups (menus, the command palette) are their own top levels and keep their size.
/// </summary>
internal sealed class ZoomHost : Panel
{
    private readonly LayoutTransformControl _scaled;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly DispatcherTimer _badgeTimer;
    private double _wheel;   // touchpads report fractions of a notch; a step fires per whole notch

    public ZoomHost(SessionPalette p, Control content)
    {
        _scaled = new LayoutTransformControl { Child = content };
        _badgeText = new TextBlock { FontFamily = p.Mono, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = p.Text };
        _badge = new Border
        {
            Background = p.Raised, BorderBrush = p.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6), Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = BoxShadows.Parse("0 6 20 0 #44000000"),
            IsHitTestVisible = false, IsVisible = false, Child = _badgeText,
        };
        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); _badge.IsVisible = false; };
        Children.Add(_scaled);
        Children.Add(_badge);
    }

    public double Zoom { get; private set; } = ViewZoom.Default;

    /// <summary>Whether Ctrl+0 resets to 100%. Off where the window binds Ctrl+0 itself (the Roost's Focus tab).</summary>
    public bool ResetOnCtrl0 { get; init; }

    /// <summary>The user changed the zoom (a chord, the wheel, a <see cref="ZoomButton"/> pick). Not raised by
    /// <see cref="SetZoom"/>.</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>The level changed by any route, <see cref="SetZoom"/> included — for a readout to follow.</summary>
    public event Action? ZoomApplied;

    /// <summary>Applies a level quietly: the saved one at open, or a step made in another window.</summary>
    public void SetZoom(double zoom)
    {
        zoom = ViewZoom.Normalize(zoom);
        if (Math.Abs(zoom - Zoom) < 0.0001) return;
        Zoom = zoom;
        _scaled.LayoutTransform = Math.Abs(zoom - ViewZoom.Default) < 0.0001 ? null : new ScaleTransform(zoom, zoom);
        ZoomApplied?.Invoke();
    }


    /// <summary>Hooks the chords on <paramref name="window"/>, tunnelling, so a focused composer or thread can't
    /// swallow them (and they work with nothing focused).</summary>
    public void Attach(Window window)
    {
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers & ~KeyModifiers.Shift;   // Ctrl+Shift+= is Ctrl++ on most layouts
        if (mods != KeyModifiers.Control) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomTo(ViewZoom.Step(Zoom, +1));
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomTo(ViewZoom.Step(Zoom, -1));
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 when ResetOnCtrl0 && e.KeyModifiers == KeyModifiers.Control:
                ZoomTo(ViewZoom.Default);
                e.Handled = true;
                break;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) { _wheel = 0; return; }
        e.Handled = true;   // Ctrl+wheel zooms; it never scrolls
        _wheel += e.Delta.Y;
        if (Math.Abs(_wheel) < 1) return;
        ZoomTo(ViewZoom.Step(Zoom, Math.Sign(_wheel)));
        _wheel = 0;
    }

    /// <summary>Applies a level the user picked (a chord, the wheel, the zoom button): with the badge and
    /// <see cref="ZoomChanged"/>.</summary>
    public void ZoomTo(double zoom)
    {
        bool changed = Math.Abs(ViewZoom.Normalize(zoom) - Zoom) >= 0.0001;
        SetZoom(zoom);
        // The badge shows even when pinned at an end, so the press visibly registered.
        _badgeText.Text = ViewZoom.Label(Zoom);
        _badge.IsVisible = true;
        _badgeTimer.Stop();
        _badgeTimer.Start();
        if (changed) ZoomChanged?.Invoke(Zoom);
    }
}
