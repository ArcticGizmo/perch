using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Services.Replay;
using Perch.Avalonia.Theming;
using Perch.Data.Replay;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The replay "transport" — a small always-on-top window that drives the <see cref="ReplayController"/>:
/// play/pause, a speed selector, an owner-drawn <see cref="ReplayTimelineBar"/> that plots the markers,
/// event stepping, and jump-to-marker. Debug-only by construction: it exists only under
/// <c>perch replay</c>.
/// </summary>
internal sealed class ReplayControllerWindow : Window
{
    private static readonly double[] Speeds = [0.5, 1, 2, 4, 8, 1000];
    private static readonly string[] SpeedLabels = ["0.5×", "1×", "2×", "4×", "8×", "Max"];
    private const int DefaultSpeedIndex = 3; // 4×

    private readonly ReplayController _controller;
    private readonly IReadOnlyList<ReplayMarker> _markers;
    private readonly ReplayTimelineBar _timeline;
    private readonly TextBlock _position;
    private readonly TextBlock _hint;
    private readonly Button _playPause;

    public ReplayControllerWindow(ReplayController controller, IReadOnlyList<ReplayMarker> markers)
    {
        _controller = controller;
        _markers = markers;

        Title = "Perch Replay";
        Width = 480;
        Height = 196;
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

        _timeline = new ReplayTimelineBar();
        _timeline.SetDuration(_controller.DurationMs);
        _timeline.SetMarkers(_markers);
        _timeline.Seeked += t => { _controller.Seek(t); RefreshPlayLabel(); };
        _timeline.Hovered += OnMarkerHover;

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
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
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

        // Header: position/duration on the left, and a hint that shows the marker count at rest and the
        // hovered marker's label + time while the cursor is over a tick.
        _hint = new TextBlock
        {
            Text = MarkerCountText(), Foreground = Palette.MutedBrush, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_hint, Dock.Right);
        header.Children.Add(_hint);
        header.Children.Add(_position);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(header);
        layout.Children.Add(_timeline);
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

    // Reflect the hovered marker in the header hint (label + time), or the marker count when none.
    private void OnMarkerHover(ReplayMarker? marker)
    {
        if (marker is { } m)
        {
            _hint.Text = $"{m.Label} · {Format(m.ScenePos)}";
            _hint.Foreground = new SolidColorBrush(ReplayTimelineBar.MarkerColor(m.Kind));
        }
        else
        {
            _hint.Text = MarkerCountText();
            _hint.Foreground = Palette.MutedBrush;
        }
    }

    private string MarkerCountText() => _markers.Count == 1 ? "1 marker" : $"{_markers.Count} markers";

    private void UpdateReadout(long pos)
    {
        _timeline.SetPosition(pos);
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
