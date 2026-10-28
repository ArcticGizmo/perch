using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small modal for flipping a Claude Code setting <b>per config directory</b> (Agent Teams, idle compaction).
/// It's the multi-config-dir form of the setting's single toggle: because the flag lives in each directory's
/// own <c>settings.json</c>, there's no one switch when several directories are in play — this lists them,
/// one toggle each, bound through the descriptor's <see cref="SettingDescriptor.GetBoolForDir"/> /
/// <see cref="SettingDescriptor.SetBoolForDir"/>.
/// </summary>
/// <remarks>
/// Each toggle applies immediately to that directory's settings.json (like the status-line designer's apply,
/// not a transactional edit), so there's no Save/Cancel — just Close. Dark-themed to match the settings
/// window. Best-effort: a write that fails silently leaves the toggle reflecting the on-disk state on reopen.
/// </remarks>
internal sealed class ConfigDirToggleDialog : Window
{
    public ConfigDirToggleDialog(SettingDescriptor setting, IReadOnlyList<ClaudeConfigDir> dirs)
    {
        Title = $"{setting.Name} — per directory";
        Width = 520;
        Height = 360;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(SettingsUi.BodyText(
            setting.Description + " This is set per config directory, in each directory's own settings.json."));
        layout.Children.Add(SettingsUi.Separator());

        var list = new StackPanel { Spacing = 0 };
        foreach (var dir in dirs)
            list.Children.Add(BuildRow(setting, dir));
        layout.Children.Add(list);

        var close = SettingsUi.FlatButton("Close");
        close.Width = 92;
        close.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(close);
        layout.Children.Add(buttons);

        Content = new ScrollViewer { Content = layout };
    }

    private static Control BuildRow(SettingDescriptor setting, ClaudeConfigDir dir)
    {
        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(dir.DisplayLabel) ? dir.Label : dir.DisplayLabel,
            FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
        });
        left.Children.Add(new TextBlock
        {
            // Path keeps its tail (folder name) legible — leading ellipsis, never trailing.
            Text = dir.Root, FontSize = 12, Foreground = Palette.MutedBrush,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        });

        var toggle = new PerchToggle { VerticalAlignment = VerticalAlignment.Center };
        toggle.SetCheckedSilent(setting.GetBoolForDir!(dir.UserSettingsFile));
        toggle.CheckedChanged += (_, _) => setting.SetBoolForDir!(dir.UserSettingsFile, toggle.IsChecked);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(left);
        grid.Children.Add(toggle);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(2, 11, 2, 11),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
