using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Builds the in-place editor control for a non-toggle setting (dropdown / text field / threshold slider /
/// stepper / hotkey), wired straight to the real <see cref="AppSettings"/> (persisted and applied live).
/// Used by <see cref="SettingEditorWindow"/> so the search page can offer a uniform "Edit" button for any
/// kind while keeping its rows the same height.
/// </summary>
internal static class SettingEditors
{
    public static Control Build(SettingDescriptor d, AppSettings s, SettingsHooks h) => d.Kind switch
    {
        SettingKind.Dropdown => Dropdown(d, s),
        SettingKind.Field    => Field(d, s),
        SettingKind.Slider   => Slider(s, h),
        SettingKind.Stepper  => Stepper(d, s, h),
        SettingKind.Hotkey   => Hotkey(d, s, h),
        _                    => new TextBlock { Text = "Edited elsewhere.", Foreground = Palette.MutedBrush, FontSize = 13 },
    };

    private static Control Dropdown(SettingDescriptor d, AppSettings s)
    {
        if (d.Id == "start-mode")
        {
            var combo = SettingsUi.Dropdown(["Never", "On session start", "At login"], (int)s.StartMode);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                s.StartMode = (StartMode)combo.SelectedIndex;
                s.Save();
                App.SyncLoginItem(s.StartMode);
            };
            return combo;
        }

        var term = SettingsUi.Dropdown(
            ["Auto", "Windows Terminal", "PowerShell", "Command Prompt"], (int)s.ReopenTerminal);
        term.SelectionChanged += (_, _) =>
        {
            if (term.SelectedIndex < 0) return;
            s.ReopenTerminal = (TerminalApp)term.SelectedIndex;
            s.Save();
        };
        return term;
    }

    private static Control Field(SettingDescriptor d, AppSettings s)
    {
        bool host = d.Id == "ntfy-host";
        var box = SettingsUi.ThemedTextBox((host ? s.NtfyHost : s.NtfyTopic) ?? "");
        box.PlaceholderText = host ? "https://ntfy.sh" : "your-topic-name";
        box.MinWidth = 300;
        box.TextChanged += (_, _) =>
        {
            if (host) s.NtfyHost = box.Text; else s.NtfyTopic = box.Text;
            s.Save();
        };
        return box;
    }

    private static Control Slider(AppSettings s, SettingsHooks h)
    {
        var slider = new ContextThresholdSliderView { Width = 380 };
        slider.SetValues(s.ContextPressureYellowPercent, s.ContextPressureOrangePercent, s.ContextPressureRedPercent);
        slider.ShowGreenSegment = s.ShowContextGreenSegment;
        slider.RangeChanged += (yellow, orange, red) =>
        {
            s.ContextPressureYellowPercent = yellow;
            s.ContextPressureOrangePercent = orange;
            s.ContextPressureRedPercent = red;
            s.Save();
            h.DisplayChanged?.Invoke();
        };
        return slider;
    }

    private static Control Stepper(SettingDescriptor d, AppSettings s, SettingsHooks h)
    {
        var value = new TextBlock
        {
            Text = d.GetInt!(s).ToString(), FontSize = 14, Foreground = Palette.FgBrush,
            MinWidth = 34, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, monospace"),
        };

        void Bump(int delta)
        {
            var next = Math.Clamp(d.GetInt!(s) + delta, 1, 999);
            d.SetInt!(s, next);
            value.Text = next.ToString();
            s.Save();
            SettingsLiveApply.Value(d.Id, h);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(StepButton("−", () => Bump(-1)));
        row.Children.Add(value);
        row.Children.Add(StepButton("+", () => Bump(+1)));
        return row;
    }

    private static Control Hotkey(SettingDescriptor d, AppSettings s, SettingsHooks h)
    {
        var binding = d.Id switch
        {
            "hotkey-cycle"    => s.HotkeyCycleSessions,
            "hotkey-switcher" => s.HotkeyOpenSwitcher,
            _                 => s.HotkeyToggleDense,
        };
        var btn = new HotkeyCaptureButton(binding);
        btn.Changed += () => { s.Save(); h.HotkeysChanged?.Invoke(); };
        return btn;
    }

    private static Button StepButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, Width = 30, Height = 30, Padding = new Thickness(0),
            FontSize = 16, Background = Palette.ButtonBgBrush, Foreground = Palette.FgBrush,
            BorderThickness = new Thickness(1), BorderBrush = Palette.BorderBrush, CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}

/// <summary>
/// A small modal that hosts one setting's editor, opened from the search page's "Edit" button. Keeps the
/// search results a uniform height (name + a single Edit button / toggle) by moving the varied editors
/// (slider, stepper, text field, dropdown) off the row and into this dialog. Edits persist and apply live
/// as they're made; the dialog is just a frame with a Done button.
/// </summary>
internal sealed class SettingEditorWindow : Window
{
    public SettingEditorWindow(SettingDescriptor d, AppSettings settings, SettingsHooks hooks)
    {
        Title = d.Name;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = Palette.FormBgBrush;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch/Assets/icon.ico"))); } catch { }

        var stack = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        stack.Children.Add(SettingsUi.SectionTitle(d.Name));
        stack.Children.Add(SettingsUi.BodyText(d.Description));
        stack.Children.Add(SettingEditors.Build(d, settings, hooks));

        var done = SettingsUi.FlatButton("Done");
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Padding = new Thickness(20, 6);
        done.Click += (_, _) => Close();
        stack.Children.Add(done);

        Content = stack;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
