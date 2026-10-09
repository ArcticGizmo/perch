using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The joined one-of-N toggle of <see cref="SettingsUi.Segmented"/>, with a handle: the owner can set the selection
/// from code (no callback fires), and disable or hide single segments. For a form whose default moves under the user
/// until they pick (the PR session dialog's worktree choice) and whose options come and go with what's on disk.
/// <see cref="Changed"/> fires only on a user click that changes the selection.
/// </summary>
internal sealed class SegmentedPicker
{
    private readonly List<Button> _buttons = new();
    private readonly List<Border?> _dividers = new();   // the divider before each segment; null before the first
    private int _selected;

    public SegmentedPicker(IReadOnlyList<string> labels, int selected, double height = 28, double minWidth = 84)
    {
        _selected = selected;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        for (int i = 0; i < labels.Count; i++)
        {
            Border? divider = null;
            if (i > 0)
            {
                divider = new Border { Width = 1, Background = Palette.BorderBrush };
                panel.Children.Add(divider);
            }
            _dividers.Add(divider);

            int idx = i;
            var btn = new Button
            {
                Content = labels[i], Height = height, MinWidth = minWidth, Padding = new Thickness(12, 0),
                Background = Brushes.Transparent, Foreground = Palette.FgBrush,
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetShowOnDisabled(btn, true);   // a greyed segment says why
            btn.Click += (_, _) =>
            {
                if (_selected == idx) return;
                _selected = idx;
                Restyle();
                Changed?.Invoke(idx);
            };
            _buttons.Add(btn);
            panel.Children.Add(btn);
        }
        Restyle();
        View = new Border
        {
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Background = Palette.ButtonBgBrush, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Child = panel,
        };
    }

    /// <summary>The control to place.</summary>
    public Control View { get; }

    /// <summary>A user click changed the selection to this segment.</summary>
    public event Action<int>? Changed;

    /// <summary>The selected segment. Setting it restyles without raising <see cref="Changed"/>.</summary>
    public int Selected
    {
        get => _selected;
        set { _selected = value; Restyle(); }
    }

    /// <summary>Greys a segment out (it can't be clicked) with an optional tooltip saying why.</summary>
    public void SetEnabled(int index, bool enabled, string? whyNot = null)
    {
        var b = _buttons[index];
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1 : 0.45;
        ToolTip.SetTip(b, enabled ? null : whyNot);
    }

    /// <summary>Shows or hides a segment, with the divider that would sit beside it.</summary>
    public void SetVisible(int index, bool visible)
    {
        _buttons[index].IsVisible = visible;
        // A divider shows only between two visible segments.
        bool seen = false;
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_dividers[i] is { } d) d.IsVisible = seen && _buttons[i].IsVisible;
            seen |= _buttons[i].IsVisible;
        }
    }

    private void Restyle()
    {
        for (int i = 0; i < _buttons.Count; i++) SettingsUi.StyleSegment(_buttons[i], i == _selected);
    }
}
