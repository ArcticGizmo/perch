using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Services.Replay;
using Perch.Avalonia.Theming;
using Perch.Data.Replay;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The replay "transport" — a small always-on-top window that drives the <see cref="ReplayController"/>:
/// play/pause, a speed selector, a scrub bar over the whole scene, event stepping, and jump-to-marker.
/// Debug-only by construction: it exists only under <c>perch replay</c>. Standard themed controls (not
/// owner-drawn) — it's a developer tool, so plumbing beats pixels.
/// </summary>
internal sealed class ReplayControllerWindow : Window
{
    private static readonly double[] Speeds = [0.5, 1, 2, 4, 8, 1000];
    private static readonly string[] SpeedLabels = ["0.5×", "1×", "2×", "4×", "8×", "Max"];
    private const int DefaultSpeedIndex = 3; // 4×

    private readonly ReplayController _controller;
    private readonly IReadOnlyList<ReplayMarker> _markers;
    private readonly Slider _scrub;
    private readonly TextBlock _position;
    private readonly Button _playPause;
    private bool _syncing; // suppresses the scrub→seek feedback loop during programmatic updates

    public ReplayControllerWindow(ReplayController controller, IReadOnlyList<ReplayMarker> markers)
    {
        _controller = controller;
        _markers = markers;

        Title = "Perch Replay";
        Width = 480;
        Height = 190;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        _position = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, monospace"), FontSize = 13,
            Foreground = Palette.TitleBrush, VerticalAlignment = VerticalAlignment.Center,
        };

        _scrub = new Slider
        {
            Minimum = 0, Maximum = Math.Max(1, _controller.DurationMs), SmallChange = 1000, LargeChange = 5000,
            Foreground = Palette.AccentBrush,
        };
        _scrub.ValueChanged += OnScrubChanged;

        _playPause = SettingsUi.FlatButton("Pause");
        _playPause.Width = 82;
        _playPause.Click += (_, _) => { _controller.TogglePlay(); RefreshPlayLabel(); };

        var stepBack = SettingsUi.FlatButton("◀ prev");
        stepBack.Click += (_, _) => JumpToMarker(forward: false);
        var stepFwd = SettingsUi.FlatButton("next ▶");
        stepFwd.Click += (_, _) => JumpToMarker(forward: true);

        var speed = SettingsUi.Dropdown(SpeedLabels, DefaultSpeedIndex);
        speed.Width = 84;
        speed.SelectionChanged += (_, _) =>
        {
            if (speed.SelectedIndex >= 0)
                _controller.Speed = Speeds[speed.SelectedIndex];
        };
        _controller.Speed = Speeds[DefaultSpeedIndex];

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
        };
        transport.Children.Add(stepBack);
        transport.Children.Add(_playPause);
        transport.Children.Add(stepFwd);
        transport.Children.Add(new TextBlock
        {
            Text = "speed", Foreground = Palette.MutedBrush, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        });
        transport.Children.Add(speed);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var hint = new TextBlock
        {
            Text = $"{_markers.Count} markers", Foreground = Palette.MutedBrush, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_position);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(header);
        layout.Children.Add(_scrub);
        layout.Children.Add(transport);
        Content = layout;

        _controller.PositionChanged += OnPositionChanged;
        UpdateReadout(_controller.PositionMs);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _controller.TogglePlay(); RefreshPlayLabel(); e.Handled = true; break;
            case Key.Left: JumpToMarker(forward: false); e.Handled = true; break;
            case Key.Right: JumpToMarker(forward: true); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    // Fired on the UI thread after each projection lands — move the scrub + readout to match, then
    // reflect play/pause state (the controller auto-pauses at the end of the scene).
    private void OnPositionChanged(long pos)
    {
        UpdateReadout(pos);
        RefreshPlayLabel();
    }

    private void OnScrubChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
            return; // a programmatic move, not a user drag — don't seek (which would pause playback)
        _controller.Seek((long)e.NewValue);
        RefreshPlayLabel();
    }

    private void UpdateReadout(long pos)
    {
        _syncing = true;
        _scrub.Value = Math.Clamp(pos, 0, _scrub.Maximum);
        _syncing = false;
        _position.Text = $"{Format(pos)} / {Format(_controller.DurationMs)}";
    }

    private void RefreshPlayLabel() => _playPause.Content = _controller.IsPlaying ? "Pause" : "Play";

    // Seeks to the nearest marker strictly before / after the current position (clamped to the ends).
    private void JumpToMarker(bool forward)
    {
        var pos = _controller.PositionMs;
        long? target = forward
            ? _markers.Where(m => m.ScenePos > pos).Select(m => (long?)m.ScenePos).FirstOrDefault()
            : _markers.Where(m => m.ScenePos < pos).Select(m => (long?)m.ScenePos).LastOrDefault();
        _controller.Seek(target ?? (forward ? _controller.DurationMs : 0));
        RefreshPlayLabel();
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }
}
