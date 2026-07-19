using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The settings window's shared custom controls and control factories — the Avalonia counterparts of
/// the WinForms <c>SettingsControls.cs</c> (the pill <c>ToggleSwitch</c>, the busy <c>Spinner</c>, the
/// owner-drawn usage bars, the permission-mode legend, and the context-pressure slider) plus small
/// factory helpers for the themed text/buttons/rows every page builds from. Everything reads the shared
/// <see cref="Palette"/> so the settings surface renders as one app with the overlay.
/// </summary>
internal static class SettingsUi
{
    // ── Text ────────────────────────────────────────────────────────────────────────
    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold,
        Foreground = Palette.TitleBrush, Margin = new Thickness(0, 4, 0, 8),
    };

    public static TextBlock BodyText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13,
        Foreground = Palette.MutedBrush, Margin = new Thickness(0, 0, 0, 6),
    };

    public static TextBlock FieldCaption(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Palette.MutedBrush,
        Margin = new Thickness(0, 2, 0, 2),
    };

    // A muted, vertically-centred caption sitting beside an inline toggle.
    public static TextBlock ToggleCaption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Palette.MutedBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Separator() => new()
    {
        Height = 1, Background = Palette.BorderBrush, Margin = new Thickness(0, 12, 0, 12),
    };

    // A monospace, boxed block for copy-pasteable commands.
    public static TextBlock CodeBlock(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, monospace"),
        FontSize = 13, Foreground = Palette.FgBrush,
        Background = new SolidColorBrush(Color.FromRgb(34, 34, 44)),
        Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 8),
    };

    // ── Buttons / inputs ──────────────────────────────────────────────────────────────
    // A flat dark button matching the overlay/settings surface. Fluent supplies the pointer-over/pressed
    // shading; we set the resting palette so it reads as part of Perch (twin of the WinForms FlatButton).
    public static Button FlatButton(string text) => new()
    {
        Content = text, Background = Palette.ButtonBgBrush, Foreground = Palette.FgBrush,
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(12, 6), FontSize = 13,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    public static TextBox ThemedTextBox(string value) => new()
    {
        Text = value, Background = Palette.ButtonBgBrush, Foreground = Palette.FgBrush,
        BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), FontSize = 13, Padding = new Thickness(6, 4),
    };

    // A themed single-select dropdown for the (rare) enum-style setting; Fluent supplies the popup, we set
    // the resting palette so the closed control reads as part of Perch.
    public static ComboBox Dropdown(IEnumerable<string> items, int selectedIndex) => new()
    {
        ItemsSource = items.ToList(), SelectedIndex = selectedIndex,
        Background = Palette.ButtonBgBrush, Foreground = Palette.FgBrush,
        BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), FontSize = 13, Padding = new Thickness(8, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    // A horizontal row of buttons (LeftToRight, small gap), for the action rows on each page.
    public static StackPanel ButtonRow() => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4),
    };

    // ── Rows ────────────────────────────────────────────────────────────────────────
    // A section header with a right-justified control (usually a toggle) on the same row.
    public static Grid TitleRow(string title, Control right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 8),
        };
        var label = new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TitleBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(label);
        grid.Children.Add(right);
        return grid;
    }

    // An indented sub-row: a left label (returned so callers can dim it) and a right-aligned control.
    public static Grid SubRow(string text, Control right, out TextBlock label)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 2, 0, 4),
        };
        label = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = Palette.FgBrush,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(label, 0);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(label);
        grid.Children.Add(right);
        return grid;
    }
}

/// <summary>A Material-style on/off switch: a rounded pill track with a sliding knob. The Avalonia port
/// of the WinForms <c>ToggleSwitch</c> — a custom-drawn control rather than the Fluent
/// <see cref="ToggleSwitch"/>, so it matches the overlay palette exactly and stays compact.</summary>
internal sealed class PerchToggle : Control
{
    private bool _on;

    /// <summary>Raised when the state changes through a click (not through <see cref="SetCheckedSilent"/>).</summary>
    public event EventHandler? CheckedChanged;

    public PerchToggle()
    {
        Width = 46;
        Height = 26;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public bool IsChecked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            InvalidateVisual();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set the state without raising <see cref="CheckedChanged"/> (for syncing to external state).</summary>
    public void SetCheckedSilent(bool value)
    {
        if (_on == value) return;
        _on = value;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
        }
    }

    // Repaint when enabled-state flips so the dimmed look tracks it.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(1, 1, Bounds.Width - 2, Bounds.Height - 2);
        Color track = _on ? Palette.Green : Color.FromRgb(70, 70, 88);
        if (!IsEnabled) track = Palette.Blend(track, Palette.FormBg, 0.5f);
        OverlayDraw.Pill(ctx, new SolidColorBrush(track), rect);

        double knobD = rect.Height - 6;
        double knobX = _on ? rect.Right - knobD - 3 : rect.Left + 3;
        Color knob = Color.FromRgb(235, 235, 245);
        if (!IsEnabled) knob = Palette.Blend(knob, Palette.FormBg, 0.4f);
        double r = knobD / 2;
        ctx.DrawEllipse(new SolidColorBrush(knob), null,
            new Point(knobX + r, rect.Top + 3 + r), r, r);
    }
}

/// <summary>
/// A button that records a global-shortcut combo: click it, then press your modifiers + a main key
/// (letter / digit / Space) and it captures the chord into the bound <see cref="HotkeyBinding"/>. Shows the
/// current combo ("Alt + Shift + W") at rest and "Press keys…" while listening. A modifier-less chord is
/// rejected (so a bare key can't be claimed system-wide); Escape or losing focus cancels the capture.
/// </summary>
internal sealed class HotkeyCaptureButton : Button
{
    private readonly HotkeyBinding _binding;
    private bool _listening;

    /// <summary>Raised once a new combo has been captured into the binding.</summary>
    public event Action? Changed;

    public HotkeyCaptureButton(HotkeyBinding binding)
    {
        _binding = binding;
        Width = 210;
        Background = Palette.ButtonBgBrush;
        Foreground = Palette.FgBrush;
        BorderThickness = new Thickness(1);
        BorderBrush = Palette.BorderBrush;
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(12, 6);
        FontSize = 13;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        UpdateLabel();

        // Clicking away mid-capture cancels it.
        LostFocus += (_, _) => { if (_listening) StopListening(); };
    }

    // Clicking (or Space/Enter while focused) begins capture; don't chain to base (there's no command).
    protected override void OnClick() => StartListening();

    private void StartListening()
    {
        _listening = true;
        Content = "Press keys…";
        BorderBrush = Palette.AccentBrush;
        Focus();
    }

    private void StopListening()
    {
        _listening = false;
        BorderBrush = Palette.BorderBrush;
        UpdateLabel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_listening) { base.OnKeyDown(e); return; }

        // Swallow every key while listening so Space/Enter don't re-fire the button and Escape doesn't
        // bubble up to close the settings window.
        e.Handled = true;

        if (e.Key == Key.Escape) { StopListening(); return; }
        if (IsModifierKey(e.Key)) return;           // lone modifier — wait for the main key
        if (!TryMapKey(e.Key, out char key)) return; // unsupported main key — keep listening

        var mods = MapModifiers(e.KeyModifiers);
        if (mods == HotkeyModifiers.None) return;    // require ≥1 modifier — keep listening

        _binding.Modifiers = mods;
        _binding.KeyChar = key;
        StopListening();
        Changed?.Invoke();
    }

    private void UpdateLabel() => Content = _binding.Describe();

    private static bool IsModifierKey(Key k) => k is
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    private static HotkeyModifiers MapModifiers(KeyModifiers m)
    {
        var r = HotkeyModifiers.None;
        if (m.HasFlag(KeyModifiers.Control)) r |= HotkeyModifiers.Control;
        if (m.HasFlag(KeyModifiers.Alt)) r |= HotkeyModifiers.Alt;
        if (m.HasFlag(KeyModifiers.Shift)) r |= HotkeyModifiers.Shift;
        return r;
    }

    // Maps the main key to the char IGlobalHotkey expects; only letters, digits and Space are supported
    // (the cross-platform hotkey layer maps exactly these).
    private static bool TryMapKey(Key k, out char c)
    {
        if (k is >= Key.A and <= Key.Z) { c = (char)('A' + (k - Key.A)); return true; }
        if (k is >= Key.D0 and <= Key.D9) { c = (char)('0' + (k - Key.D0)); return true; }
        if (k is >= Key.NumPad0 and <= Key.NumPad9) { c = (char)('0' + (k - Key.NumPad0)); return true; }
        if (k == Key.Space) { c = ' '; return true; }
        c = '\0';
        return false;
    }
}

/// <summary>A small indeterminate spinner: a rotating accent arc on a faint track. Only animates (and
/// only holds a timer) while <see cref="Spinning"/> is true; hidden otherwise.</summary>
internal sealed class SpinnerView : Control
{
    private readonly DispatcherTimer _timer;
    private double _angle;
    private bool _spinning;

    public SpinnerView()
    {
        Width = 18;
        Height = 18;
        IsVisible = false;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => { _angle = (_angle + 30) % 360; InvalidateVisual(); };
    }

    public bool Spinning
    {
        get => _spinning;
        set
        {
            if (_spinning == value) return;
            _spinning = value;
            IsVisible = value;
            if (value) _timer.Start(); else _timer.Stop();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext ctx)
    {
        if (!_spinning) return;
        double pad = 2.5;
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2;
        double r = (Bounds.Width - pad * 2) / 2;
        double thick = Math.Max(2, Bounds.Width / 9);

        var trackPen = new Pen(new SolidColorBrush(Palette.Border), thick);
        OverlayDraw.Arc(ctx, trackPen, cx, cy, r, 0, 359.9);

        var arcPen = new Pen(new SolidColorBrush(Palette.Accent), thick) { LineCap = PenLineCap.Round };
        OverlayDraw.Arc(ctx, arcPen, cx, cy, r, _angle, 100);
    }
}

/// <summary>Renders the 5-hour ("Session") and 7-day ("Weekly") usage windows as labelled bars, matching
/// the overlay (via <see cref="UsageBarRenderer"/>). Shows a placeholder line when usage tracking is off
/// or no reading is available yet. The Avalonia port of the WinForms <c>UsageBarsControl</c>.</summary>
internal sealed class UsageBarsView : Control
{
    private const double BarRowHeight = 24;
    private const double CaptionW = 64;
    private const double PctW = 44;
    private const double TrackH = 8;

    private UsageInfo _usage = UsageInfo.Empty;
    private bool _on = true;
    private bool _showExpectedRate = true;

    public UsageBarsView() => Height = 74;

    public void SetUsage(UsageInfo usage) { _usage = usage; InvalidateVisual(); }
    public void SetOn(bool on) { _on = on; InvalidateVisual(); }
    public void SetShowExpectedRate(bool show) { _showExpectedRate = show; InvalidateVisual(); }

    public override void Render(DrawingContext ctx)
    {
        var muted = new SolidColorBrush(Palette.Muted);

        if (!_on)
        {
            ctx.DrawText(OverlayDraw.Text("Usage tracking is off — enable it above to see your limits.", 11, muted), new Point(0, 4));
            return;
        }

        if (_usage.LastUpdated == DateTime.MinValue && _usage.FiveHourPercent == null)
        {
            ctx.DrawText(OverlayDraw.Text(_usage.Error ?? "No usage data yet.", 11, muted), new Point(0, 4));
            return;
        }

        bool stale = _usage.IsStale(DateTime.Now);
        double? sessionExpected = _showExpectedRate ? UsageBarRenderer.ElapsedPercent(_usage.FiveHourResetsAt, TimeSpan.FromHours(5)) : null;
        double? weeklyExpected = _showExpectedRate ? UsageBarRenderer.ElapsedPercent(_usage.SevenDayResetsAt, TimeSpan.FromDays(7)) : null;
        DrawBar(ctx, 0, "Session", _usage.FiveHourPercent, sessionExpected, stale);
        DrawBar(ctx, BarRowHeight, "Weekly", _usage.SevenDayPercent, weeklyExpected, stale);

        var parts = new List<string>
        {
            _usage.Ok ? $"Updated {_usage.LastUpdated:h:mm tt}" : $"Stale — {_usage.Error}",
        };
        if (_usage.FiveHourResetsAt is { } fr) parts.Add($"5h resets {fr:ddd h:mm tt}");
        if (_usage.SevenDayResetsAt is { } wr) parts.Add($"weekly resets {wr:ddd h:mm tt}");
        ctx.DrawText(OverlayDraw.Text(string.Join("   ·   ", parts), 11, muted), new Point(0, BarRowHeight * 2 + 2));
    }

    private void DrawBar(DrawingContext ctx, double rowTop, string caption, double? percent, double? expected, bool stale) =>
        UsageBarRenderer.Draw(ctx, 0, Bounds.Width, rowTop + BarRowHeight / 2,
            caption, percent, expected, stale, 11.5, 11.5,
            Palette.Muted, Palette.Track, Palette.ExpectedMark, Palette.FormBg,
            CaptionW, PctW, TrackH);
}

/// <summary>A legend listing each permission mode beside the coloured fast-forward badge the overlay
/// draws for it. The Avalonia port of the WinForms <c>ModeLegend</c>.</summary>
internal sealed class ModeLegendView : Control
{
    private const double RowH = 24;

    private static readonly (PermissionMode mode, string label)[] Modes =
    [
        (PermissionMode.Normal,      "Normal — no badge shown"),
        (PermissionMode.Plan,        "Plan mode"),
        (PermissionMode.AcceptEdits, "Accept edits"),
        (PermissionMode.Auto,        "Auto-accept"),
        (PermissionMode.Bypass,      "Bypass permissions"),
    ];

    public ModeLegendView() => Height = Modes.Length * RowH;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double y = 0;
        foreach (var (mode, label) in Modes)
        {
            double midY = y + RowH / 2;

            if (mode == PermissionMode.Normal)
            {
                var pen = new Pen(new SolidColorBrush(Dim(Palette.Muted)), 1.6);
                ctx.DrawLine(pen, new Point(6, midY), new Point(16, midY));
            }
            else
            {
                DrawModeBadge(ctx, mode, 4, midY);
            }

            Color textColor = mode == PermissionMode.Normal ? Palette.Muted : Palette.Fg;
            var ft = OverlayDraw.Text(label, 13, new SolidColorBrush(Dim(textColor)));
            OverlayDraw.TextLeftMid(ctx, ft, 28, midY);

            y += RowH;
        }
    }

    private Color Dim(Color c) => IsEnabled ? c : Palette.Blend(c, Palette.FormBg, 0.5f);

    // A pair of chevrons in the mode colour — the same badge the overlay draws (see OverlayCanvas).
    private void DrawModeBadge(DrawingContext ctx, PermissionMode mode, double x, double midY)
    {
        var brush = new SolidColorBrush(Dim(Palette.ModeColor(mode)));
        const double hh = 4, w = 5;
        Chevron(ctx, brush, x, midY, hh, w);
        Chevron(ctx, brush, x + w + 1, midY, hh, w);

        static void Chevron(DrawingContext ctx, IBrush brush, double x, double midY, double hh, double w)
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(x, midY - hh), isFilled: true);
                gc.LineTo(new Point(x + w, midY));
                gc.LineTo(new Point(x, midY + hh));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, g);
        }
    }
}

/// <summary>
/// A horizontal track with three draggable handles setting the context-pressure thresholds (Yellow,
/// Orange, Red). The track is painted in the four resulting bands so it doubles as a live preview.
/// Values are whole percentages kept ordered (Yellow &lt; Orange &lt; Red). <see cref="RangeChanged"/>
/// fires once per committed adjustment (drag release). The Avalonia port of the WinForms
/// <c>ContextThresholdSlider</c>.
/// </summary>
internal sealed class ContextThresholdSliderView : Control
{
    private const double HandleR = 7;
    private const double TrackH = 8;
    private const double Pad = HandleR + 3;
    private const double TrackY = 14;

    private int _yellow = 50, _orange = 65, _red = 80;
    private int _drag = -1;              // 0=Yellow, 1=Orange, 2=Red, -1=none
    private bool _showGreenSegment;

    public bool ShowGreenSegment
    {
        get => _showGreenSegment;
        set { if (_showGreenSegment == value) return; _showGreenSegment = value; InvalidateVisual(); }
    }

    /// <summary>Fired on drag release with the ordered (Yellow, Orange, Red) thresholds.</summary>
    public event Action<int, int, int>? RangeChanged;

    public ContextThresholdSliderView()
    {
        Height = 54;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Seeds the handle positions without raising <see cref="RangeChanged"/>. Sanitises order.</summary>
    public void SetValues(int yellow, int orange, int red)
    {
        _red = Math.Clamp(red, 2, 100);
        _orange = Math.Clamp(orange, 1, _red - 1);
        _yellow = Math.Clamp(yellow, 0, _orange - 1);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty) InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        _drag = NearestHandle(p.X, p.Y);
        if (_drag >= 0) { ApplyDrag(p.X); e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true; }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag < 0) return;
        ApplyDrag(e.GetPosition(this).X);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag >= 0)
        {
            _drag = -1;
            e.Pointer.Capture(null);
            RangeChanged?.Invoke(_yellow, _orange, _red);
        }
    }

    private double TrackLeft => Pad;
    private double TrackRight => Bounds.Width - Pad;
    private double TrackW => Math.Max(1, TrackRight - TrackLeft);
    private double HandleMidY => TrackY + TrackH / 2;
    private double XFor(int v) => TrackLeft + Math.Round(TrackW * v / 100.0);
    private int ValFor(double x) => Math.Clamp((int)Math.Round((x - TrackLeft) * 100.0 / TrackW), 0, 100);

    private int NearestHandle(double x, double y)
    {
        if (Math.Abs(y - HandleMidY) > HandleR + 8) return -1;
        double[] xs = { XFor(_yellow), XFor(_orange), XFor(_red) };
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < xs.Length; i++)
        {
            double d = Math.Abs(x - xs[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void ApplyDrag(double x)
    {
        int v = ValFor(x);
        switch (_drag)
        {
            case 0: _yellow = Math.Clamp(v, 0, _orange - 1); break;
            case 1: _orange = Math.Clamp(v, _yellow + 1, _red - 1); break;
            case 2: _red = Math.Clamp(v, _orange + 1, 100); break;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        double left = TrackLeft, right = TrackRight;

        Color hidden = _showGreenSegment ? Palette.Green : Palette.Blend(Palette.Green, Palette.FormBg, 0.55f);
        Color yellow = Palette.Yellow, orange = Palette.Orange, red = Palette.Red;
        if (!IsEnabled)
        {
            hidden = Palette.Blend(hidden, Palette.FormBg, 0.5f);
            yellow = Palette.Blend(yellow, Palette.FormBg, 0.5f);
            orange = Palette.Blend(orange, Palette.FormBg, 0.5f);
            red = Palette.Blend(red, Palette.FormBg, 0.5f);
        }

        double xy = XFor(_yellow), xo = XFor(_orange), xr = XFor(_red);

        // Clip to a rounded track so the outer ends are capped but internal band joins stay crisp.
        using (ctx.PushClip(new RoundedRect(new Rect(left, TrackY, TrackW, TrackH), TrackH / 2)))
        {
            FillSpan(ctx, left, xy, hidden);
            FillSpan(ctx, xy, xo, yellow);
            FillSpan(ctx, xo, xr, orange);
            FillSpan(ctx, xr, right, red);
        }

        DrawHandle(ctx, xy);
        DrawHandle(ctx, xo);
        DrawHandle(ctx, xr);

        Color capColor = IsEnabled ? Palette.Muted : Palette.Border;
        string caption = $"Shows at {_yellow}%      orange at {_orange}%      red at {_red}%";
        ctx.DrawText(OverlayDraw.Text(caption, 11, new SolidColorBrush(capColor)), new Point(left - 1, TrackY + TrackH + 8));
    }

    private static void FillSpan(DrawingContext ctx, double x0, double x1, Color color)
    {
        if (x1 <= x0) return;
        ctx.DrawRectangle(new SolidColorBrush(color), null, new Rect(x0, TrackY, x1 - x0, TrackH));
    }

    private void DrawHandle(DrawingContext ctx, double cx)
    {
        double cy = HandleMidY;
        Color fill = IsEnabled ? Color.FromRgb(235, 235, 245) : Palette.Blend(Color.FromRgb(235, 235, 245), Palette.FormBg, 0.5f);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1);
        ctx.DrawEllipse(new SolidColorBrush(fill), pen, new Point(cx, cy), HandleR, HandleR);
    }
}
